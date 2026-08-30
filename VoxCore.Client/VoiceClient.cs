using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.Wave;
using VoxCore.Client.Dsp;

namespace VoxCore.Client;

/// <summary>
/// Phase 1+2: UDP kak TeamSpeak + HPF.
/// Mic → HPF → Opus → UDP → Server → UDP → Opus → Speaker.
/// </summary>
public sealed class VoiceClient : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSize = 960; // 20ms
    private const int FrameBytes = FrameSize * 2;
    private const int VkSpace = 0x20;

    private CancellationTokenSource _cts = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _pcmQueue = new();
    private byte[] _accum = [];
    private UdpClient? _udp;
    private WaveInEvent? _capture;
    private WaveOutEvent? _playback;
    private AdaptiveJitterBuffer? _playbackBuffer;
    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;
    private Thread? _encodeThread;
    private Thread? _receiveThread;
    private Thread? _heartbeatThread;
    private Thread? _speakerThread;
    private volatile bool _running;
    private string _room = "";
    private string _name = "";
    private readonly Dictionary<string, DateTime> _speakerLast = [];
    private readonly object _speakerLock = new();
    private AesGcm? _gcm;
    private long _nonceCounter;
    private readonly byte[] _sessionId = RandomNumberGenerator.GetBytes(4);
    private VoiceDspPipeline? _dsp;

    public event Action<IReadOnlyList<string>>? MembersChanged;
    public event Action<bool>? TalkingChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? SpeakerStarted;
    public event Action<string>? SpeakerStopped;

    public int LastPingMs { get; private set; } = -1;
    public double VadProb { get; private set; }
    public bool IsGateOpen { get; private set; }
    public bool IsConnected => _running;
    public int JitterBufferMs => 0;
    public int JitterTargetMs => 0;
    public long JitterLostFrames => 0;

    public bool OpenMic { get; set; }
    public bool MicMuted { get; set; }
    public bool PlaybackMuted { get; set; }
    public int InputDevice { get; set; }
    public double MicGain { get; set; } = 1.0;
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (_playback != null) _playback.Volume = Math.Clamp(value / 100f, 0f, 1f);
        }
    }
    private int _volume = 80;
    private volatile bool _noiseSuppression = true;

    public bool NoiseSuppression
    {
        get => _noiseSuppression;
        set => _noiseSuppression = value;
    }

    public void Connect(string server, int port, string room, string name, string password, AppSettings? settings = null)
    {
        _room = room;
        _name = name;
        _dsp?.Dispose();
        _dsp = new VoiceDspPipeline(SampleRate, FrameSize, _noiseSuppression, 60.0, settings!);
        Log($"VoiceClient: DSP pipeline loaded (DFN3={_dsp.IsDfnLoaded})");
        _gcm?.Dispose();
        _gcm = string.IsNullOrEmpty(password)
            ? null
            : new AesGcm(SHA256.HashData(Encoding.UTF8.GetBytes(password)), 16);
        _nonceCounter = 0;
        _udp = new UdpClient(server, port);
        _udp.Client.SendTimeout = 1000;
        _udp.Client.ReceiveTimeout = 5000;
        _udp.Client.ReceiveBufferSize = 1024 * 1024;
        _udp.Client.SendBufferSize = 1024 * 1024;

        // Opus: VoIP, 48kbps, FEC
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 48000;
        _encoder.Complexity = 5; // nizkiy CPU
        _encoder.UseDTX = false;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;

        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        var waveFormat = new WaveFormat(SampleRate, 16, Channels);
        _playbackBuffer = new AdaptiveJitterBuffer(waveFormat, targetMs: 80, maxMs: 500);
        _playback = new WaveOutEvent();
        _playback.Init(_playbackBuffer);
        _playback.Volume = Math.Clamp(_volume / 100f, 0f, 1f);
        Log("VoiceClient: playback ready, waiting for prebuffer...");

        _capture = new WaveInEvent
        {
            DeviceNumber = InputDevice,
            WaveFormat = waveFormat,
            BufferMilliseconds = 20
        };
        _capture.DataAvailable += OnCaptureData;
        Log($"VoiceClient: connect to {server}:{port}, room={room}");
        try
        {
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Log($"VoiceClient: capture FAILED: {ex.Message}");
        }

        _running = true;
        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        oldCts.Cancel();
        oldCts.Dispose();
        var token = _cts.Token;
        _encodeThread = new Thread(() => EncodeLoop(token)) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _receiveThread = new Thread(() => ReceiveLoop(token)) { IsBackground = true };
        _heartbeatThread = new Thread(() => HeartbeatLoop(token)) { IsBackground = true };
        _speakerThread = new Thread(() => SpeakerLoop(token)) { IsBackground = true };
        _encodeThread.Start();
        _receiveThread.Start();
        _heartbeatThread.Start();
        _speakerThread.Start();

        SendJoin();
        MembersChanged?.Invoke([_name]);
        StatusChanged?.Invoke($"подключено к {server}:{port} (UDP)");
    }

    public void Disconnect()
    {
        if (!_running) return;
        SendLeave();
        _running = false;
        _cts.Cancel();
        _capture?.StopRecording();
        _capture?.Dispose();
        _playback?.Stop();
        _playback?.Dispose();
        _dsp?.Dispose();
        _udp?.Close();
        StatusChanged?.Invoke("отключено");
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var data = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, data, e.BytesRecorded);
        _pcmQueue.Enqueue(data);
    }

    private void EncodeLoop(CancellationToken token)
    {
        var frameBytes = new byte[FrameBytes];
        var frameShorts = new short[FrameSize];
        var frameFloat = new float[FrameSize];
        var opusBuf = new byte[4000];
        var lastTalk = false;

        while (!token.IsCancellationRequested)
        {
            var talk = !MicMuted && (OpenMic || (GetAsyncKeyState(VkSpace) & 0x8000) != 0);
            if (talk != lastTalk)
            {
                lastTalk = talk;
                TalkingChanged?.Invoke(talk);
            }

            while (_pcmQueue.TryDequeue(out var chunk))
            {
                _accum = [.. _accum, .. chunk];
                var off = 0;
                while (_accum.Length - off >= FrameBytes)
                {
                    Array.Copy(_accum, off, frameBytes, 0, FrameBytes);
                    Buffer.BlockCopy(frameBytes, 0, frameShorts, 0, FrameBytes);
                    off += FrameBytes;

                    if (talk && _encoder is not null)
                    {
                        if (MicGain != 1.0)
                        {
                            var g = (float)MicGain;
                            for (int i = 0; i < FrameSize; i++)
                                frameShorts[i] = (short)Math.Clamp((int)(frameShorts[i] * g), short.MinValue, short.MaxValue);
                        }

                        for (int i = 0; i < FrameSize; i++)
                            frameFloat[i] = frameShorts[i] / 32768f;

                        if (_dsp != null)
                            _dsp.Process(frameFloat);

                        for (int i = 0; i < FrameSize; i++)
                            frameShorts[i] = (short)Math.Clamp((int)(frameFloat[i] * 32768f), short.MinValue, short.MaxValue);

                        int n = _encoder.Encode(frameShorts.AsSpan(), FrameSize, opusBuf.AsSpan(), opusBuf.Length);
                        if (n > 0) SendAudio(opusBuf, n);
                    }
                }
                if (off > 0)
                {
                    var rest = new byte[_accum.Length - off];
                    Array.Copy(_accum, off, rest, 0, rest.Length);
                    _accum = rest;
                }
            }
            Thread.Sleep(1);
        }
    }

    private void SendAudio(byte[] opus, int len)
    {
        if (_udp is null || _room.Length > 255) return;
        var nameBytes = Encoding.UTF8.GetBytes(_name);
        if (nameBytes.Length > 255) return;
        byte[] payload;
        if (_gcm is not null)
        {
            var nonce = new byte[12];
            _sessionId.CopyTo(nonce, 0);
            var counter = Interlocked.Increment(ref _nonceCounter);
            for (int i = 0; i < 8; i++)
                nonce[4 + i] = (byte)(counter >> (56 - i * 8));
            var ct = new byte[len + 16];
            _gcm.Encrypt(nonce, opus.AsSpan(0, len), ct.AsSpan(0, len), ct.AsSpan(len), null);
            payload = new byte[12 + ct.Length];
            nonce.CopyTo(payload, 0);
            ct.CopyTo(payload, 12);
        }
        else
        {
            payload = new byte[len];
            Array.Copy(opus, 0, payload, 0, len);
        }
        var packet = new byte[2 + _room.Length + 1 + nameBytes.Length + payload.Length];
        packet[0] = 0x03;
        packet[1] = (byte)_room.Length;
        Encoding.UTF8.GetBytes(_room, 0, _room.Length, packet, 2);
        packet[2 + _room.Length] = (byte)nameBytes.Length;
        nameBytes.CopyTo(packet, 3 + _room.Length);
        Array.Copy(payload, 0, packet, 3 + _room.Length + nameBytes.Length, payload.Length);
        try { _udp.Send(packet, packet.Length); } catch { }
    }

    private void ReceiveLoop(CancellationToken token)
    {
        var pcmBuf = new short[FrameSize];
        var outBytes = new byte[FrameBytes];
        var recvBuf = new byte[8192];
        long audioCount = 0;
        long lastLogMs = 0;
        long lastArrivalMs = 0;
        bool playbackStarted = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_udp is null) break;
                int received = _udp.Client.Receive(recvBuf);
                if (received < 2) continue;
                var data = recvBuf.AsSpan(0, received).ToArray();
                long nowMs = Environment.TickCount64;

                switch (data[0])
                {
                    case 0x03: // audio
                        if (_decoder is null || _playbackBuffer is null) continue;
                        int roomLen = data[1];
                        int nameLen = data[2 + roomLen];
                        if (data.Length < 3 + roomLen + nameLen) continue;
                        var speaker = Encoding.UTF8.GetString(data, 3 + roomLen, nameLen);
                        var raw = data.AsSpan(3 + roomLen + nameLen).ToArray();
                        if (raw.Length == 0) continue;

                        audioCount++;
                        long gap = lastArrivalMs > 0 ? nowMs - lastArrivalMs : 0;
                        lastArrivalMs = nowMs;
                        if (nowMs - lastLogMs > 5000)
                        {
                            int buffered = _playbackBuffer.BufferedBytes;
                            Log($"[audio] recv={audioCount} gap={gap}ms buffered={buffered}B ({buffered * 1000 / (SampleRate * 2)}ms)");
                            lastLogMs = nowMs;
                        }

                        byte[] payload;
                        if (_gcm is not null)
                        {
                            if (raw.Length < 12 + 16) continue;
                            var nonce = raw.AsSpan(0, 12);
                            var ct = raw.AsSpan(12);
                            var pt = new byte[ct.Length - 16];
                            try { _gcm.Decrypt(nonce, ct[..^16], ct[^16..], pt, null); }
                            catch { continue; }
                            payload = pt;
                        }
                        else
                        {
                            payload = raw;
                        }
                        if (payload.Length == 0) continue;
                        SpeakerStarted?.Invoke(speaker);
                        lock (_speakerLock) _speakerLast[speaker] = DateTime.UtcNow;
                        int n = _decoder.Decode(payload.AsSpan(), pcmBuf.AsSpan(), FrameSize, false);
                        for (int i = 0; i < n; i++)
                        {
                            outBytes[i * 2] = (byte)(pcmBuf[i] & 0xFF);
                            outBytes[i * 2 + 1] = (byte)((pcmBuf[i] >> 8) & 0xFF);
                        }
                        if (!PlaybackMuted)
                            _playbackBuffer.AddSamples(outBytes, 0, n * 2);

                        if (!playbackStarted && _playbackBuffer.BufferedBytes >= _playbackBuffer.WaveFormat.SampleRate * _playbackBuffer.WaveFormat.BitsPerSample / 8 / 2)
                        {
                            _playback?.Play();
                            playbackStarted = true;
                            Log("[audio] playback started (prebuffered 200ms)");
                        }
                        break;

                    case 0x06: // members
                        var names = ParseMembers(data);
                        MembersChanged?.Invoke(names);
                        break;

                    case 0x07: // pong
                        if (_heartbeatSent != default)
                            LastPingMs = (int)(DateTime.UtcNow - _heartbeatSent).TotalMilliseconds;
                        break;
                }
            }
            catch
            {
                if (token.IsCancellationRequested) break;
            }
        }
    }

    private static IReadOnlyList<string> ParseMembers(byte[] data)
    {
        int roomLen = data[1];
        int count = data[2 + roomLen];
        var names = new List<string>(count);
        var off = 3 + roomLen;
        for (int i = 0; i < count && off < data.Length; i++)
        {
            int nl = data[off++];
            if (off + nl > data.Length) break;
            names.Add(Encoding.UTF8.GetString(data, off, nl));
            off += nl;
        }
        return names;
    }

    private void SpeakerLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            List<string>? stopped = null;
            lock (_speakerLock)
            {
                foreach (var (name, last) in _speakerLast)
                    if ((now - last).TotalMilliseconds > 300)
                        (stopped ??= []).Add(name);
                if (stopped is not null)
                    foreach (var name in stopped)
                        _speakerLast.Remove(name);
            }
            if (stopped is not null)
                foreach (var name in stopped)
                    SpeakerStopped?.Invoke(name);
            Thread.Sleep(100);
        }
    }

    private void HeartbeatLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_udp is not null && _room.Length <= 255)
            {
                _heartbeatSent = DateTime.UtcNow;
                var packet = new byte[2 + _room.Length];
                packet[0] = 0x04;
                packet[1] = (byte)_room.Length;
                Encoding.UTF8.GetBytes(_room, 0, _room.Length, packet, 2);
                try { _udp.Send(packet, packet.Length); } catch { }
            }
            Thread.Sleep(3000);
        }
    }
    private DateTime _heartbeatSent;

    private void SendJoin()
    {
        if (_udp is null || _room.Length > 255 || _name.Length > 255) return;
        var nameBytes = Encoding.UTF8.GetBytes(_name);
        var packet = new byte[3 + _room.Length + nameBytes.Length];
        packet[0] = 0x01;
        packet[1] = (byte)_room.Length;
        Encoding.UTF8.GetBytes(_room, 0, _room.Length, packet, 2);
        packet[2 + _room.Length] = (byte)nameBytes.Length;
        nameBytes.CopyTo(packet, 3 + _room.Length);
        try { _udp.Send(packet, packet.Length); } catch { }
    }

    private void SendLeave()
    {
        if (_udp is null || _room.Length > 255) return;
        var packet = new byte[2 + _room.Length];
        packet[0] = 0x02;
        packet[1] = (byte)_room.Length;
        Encoding.UTF8.GetBytes(_room, 0, _room.Length, packet, 2);
        try { _udp.Send(packet, packet.Length); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static void Log(string msg)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "voxcore-client.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
    }
}
