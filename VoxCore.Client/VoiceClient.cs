using System.Collections.Concurrent;
using System.IO;
using System.Net;
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

/// <summary>LEGACY: UDP-fallback когда WebRTC недоступен. RNNoise-каскад удалён (DFN3 живёт в WebRTCVoiceClient).</summary>
public sealed class VoiceClient : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSize = 960; // 20 РјСЃ
    private const int FrameBytes = FrameSize * 2; // 16 Р±РёС‚ PCM
    private const int VkSpace = 0x20;

    private CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<byte[]> _pcmQueue = new();
    private byte[] _accum = [];
    private UdpClient? _udp;
    private WaveInEvent? _capture;
    private WaveOutEvent? _playback;
    private BufferedWaveProvider? _playbackBuffer;
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
    public double VadProb => _dsp?.VadProb ?? 0;
    public bool IsGateOpen => true; // UDP gate always open (energy-based VAD controls sending)

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

    public bool IsConnected => _running;

public void Connect(string server, int port, string room, string name, string password, AppSettings? settings = null)
    {
        _room = room;
        _name = name;
        _gcm?.Dispose();
        _gcm = string.IsNullOrEmpty(password)
            ? null
            : new AesGcm(SHA256.HashData(Encoding.UTF8.GetBytes(password)), 16);
        _nonceCounter = 0;
        _udp = new UdpClient(server, port);
        _udp.Client.SendTimeout = 1000;

        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 64000;
        _encoder.Complexity = 10;
        _encoder.UseDTX = false;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 5;

        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        // DSP pipeline (HPF → DFN3 → AGC2 → VAD → Gate)
        _dsp = new VoiceDspPipeline(SampleRate, FrameSize, NoiseSuppression, settings?.DfAttLim ?? 60.0, settings);

        var waveFormat = new WaveFormat(SampleRate, 16, Channels);
        _playbackBuffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(400),
            DiscardOnBufferOverflow = true
        };
        _playback = new WaveOutEvent();
        _playback.Init(_playbackBuffer);
        _playback.Play();

        _capture = new WaveInEvent
        {
            DeviceNumber = InputDevice,
            WaveFormat = waveFormat,
            BufferMilliseconds = 20
        };
        _capture.DataAvailable += OnCaptureData;
        Log($"VoiceClient: capture device={InputDevice}, gain={MicGain}, ns={NoiseSuppression}");
        try
        {
            _capture.StartRecording();
            Log("VoiceClient: capture started");
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
        StatusChanged?.Invoke($"подключено к {server}:{port}");
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
        _udp?.Close();
        _dsp?.Dispose();
        _dsp = null;
        StatusChanged?.Invoke("отключено");
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var data = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, data, e.BytesRecorded);
        _pcmQueue.Enqueue(data);
        if (_pcmQueue.Count > 10)
            Log($"VoiceClient: pcmQueue overflow {_pcmQueue.Count}");
    }

    private void EncodeLoop(CancellationToken token)
    {
        var frameBytes = new byte[FrameBytes];
        var frameShorts = new short[FrameSize];
        var frameFloat = new float[FrameSize];
        var opusBuf = new byte[4000];
        var lastTalk = false;
        var lastTalkPing = DateTime.MinValue;
        var wasSilent = true;

        while (!token.IsCancellationRequested)
        {
            var talk = !MicMuted && (OpenMic || (GetAsyncKeyState(VkSpace) & 0x8000) != 0);
            if (talk != lastTalk)
            {
                lastTalk = talk;
                TalkingChanged?.Invoke(talk);
                Log($"EncodeLoop: talk={talk}, OpenMic={OpenMic}, MicMuted={MicMuted}");
            }
            if (talk && (DateTime.UtcNow - lastTalkPing).TotalMilliseconds > 200)
            {
                lastTalkPing = DateTime.UtcNow;
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

                    // short[] -> float[] (-1..1)
                    for (int i = 0; i < FrameSize; i++)
                        frameFloat[i] = frameShorts[i] / 32768f;

                    if (MicGain != 1.0)
                    {
                        var g = (float)MicGain;
                        for (int i = 0; i < FrameSize; i++)
                            frameFloat[i] *= g;
                    }

                    // DSP pipeline
                    bool vadActive = _dsp?.Process(frameFloat) ?? true;
                    if (talk && vadActive != lastTalk)
                        Log($"EncodeLoop: vadActive={vadActive}, talk={talk}, VadProb={_dsp?.VadProb:F3}");

                    bool shouldSend = talk && vadActive && _encoder is not null;

                    if (shouldSend)
                    {
                        // float[] -> short[]
                        for (int i = 0; i < FrameSize; i++)
                            frameShorts[i] = (short)Math.Clamp((int)(frameFloat[i] * 32768f), short.MinValue, short.MaxValue);

                        int n = _encoder.Encode(frameShorts.AsSpan(), FrameSize, opusBuf.AsSpan(), opusBuf.Length);
                        if (n > 0) { SendAudio(opusBuf, n); Log($"EncodeLoop: sent {n} bytes opus"); }
                        wasSilent = false;
                    }
                    else
                    {
                        if (!wasSilent) { _dsp?.ResetVad(); Log("EncodeLoop: silence -> reset VAD"); }
                        wasSilent = true;
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
        var recvCount = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_udp is null) break;
                int received = _udp.Client.Receive(recvBuf);
                if (received < 2) continue;
                var data = recvBuf.AsSpan(0, received).ToArray();
                recvCount++;
                if (recvCount % 50 == 1) Log($"VoiceClient: recv packet #{recvCount} type=0x{data[0]:X2} len={received}");

                switch (data[0])
                {
                    case 0x03: // Р°СѓРґРёРѕ [0x03][roomLen][room][nameLen][name][opus...]
                        if (_decoder is null || _playbackBuffer is null) continue;
                        int roomLen = data[1];
                        int nameLen = data[2 + roomLen];
                        if (data.Length < 3 + roomLen + nameLen) continue;
                        var speaker = Encoding.UTF8.GetString(data, 3 + roomLen, nameLen);
                        var raw = data.AsSpan(3 + roomLen + nameLen).ToArray();
                        if (raw.Length == 0) continue;
                        byte[] payload;
                        if (_gcm is not null)
                        {
                            if (raw.Length < 12 + 16) continue;
                            var nonce = raw.AsSpan(0, 12);
                            var ct = raw.AsSpan(12);
                            var pt = new byte[ct.Length - 16];
                            try { _gcm.Decrypt(nonce, ct[..^16], ct[^16..], pt, null); }
                            catch { Log($"ReceiveLoop: decrypt failed from {speaker}"); continue; }
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
                        Log($"ReceiveLoop: decoded {n} samples from {speaker}, payload={payload.Length}");
                        for (int i = 0; i < n; i++)
                        {
                            outBytes[i * 2] = (byte)(pcmBuf[i] & 0xFF);
                            outBytes[i * 2 + 1] = (byte)((pcmBuf[i] >> 8) & 0xFF);
                        }
                        if (!PlaybackMuted)
                            _playbackBuffer.AddSamples(outBytes, 0, n * 2);
                        break;

                    case 0x06: // СЃРїРёСЃРѕРє СѓС‡Р°СЃС‚РЅРёРєРѕРІ
                        var names = ParseMembers(data);
                        Log($"ReceiveLoop: members {names.Count} [{string.Join(",", names)}]");
                        MembersChanged?.Invoke(names);
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
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var packet = new byte[2 + _room.Length];
                packet[0] = 0x04;
                packet[1] = (byte)_room.Length;
                Encoding.UTF8.GetBytes(_room, 0, _room.Length, packet, 2);
                try { _udp.Send(packet, packet.Length); } catch { }
                LastPingMs = (int)sw.ElapsedMilliseconds;
            }
            Thread.Sleep(3000);
        }
    }

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
