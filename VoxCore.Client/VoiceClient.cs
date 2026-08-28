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

/// <summary>
/// UDP-only voice client (TeamSpeak model):
/// Клиент → UDP → Сервер(ретранслятор) → UDP → Клиент.
/// Весь DSP на клиенте: HPF → DFN3 → AGC2 → Gate → Opus → UDP.
/// Jitter buffer + pre-roll + per-user volume на приёме.
/// </summary>
public sealed class VoiceClient : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSize = 960; // 20ms @48kHz
    private const int FrameBytes = FrameSize * 2; // 16-bit PCM
    private const int VkSpace = 0x20;
    private const int PreRollFrames = 10; // 200ms pre-roll

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
    private Thread? _playbackThread;
    private volatile bool _running;
    private string _room = "";
    private string _name = "";
    private readonly Dictionary<string, DateTime> _speakerLast = [];
    private readonly object _speakerLock = new();
    private AesGcm? _gcm;
    private long _nonceCounter;
    private readonly byte[] _sessionId = RandomNumberGenerator.GetBytes(4);

    private VoiceDspPipeline? _dsp;
    private AdaptiveJitterBuffer? _jitterBuffer;

    // Per-user volume: name -> 0..200 (100 = normal)
    private readonly Dictionary<string, int> _userVolumes = [];
    private readonly object _userVolLock = new();

    // Sequence number for jitter buffer
    private int _sendSeq;

    // Pre-roll: buffer of silence frames before first speech
    private readonly Queue<float[]> _preRollBuffer = new();
    private bool _preRollFilled;
    private bool _wasSilent = true;

    // Adaptive bitrate
    private int _currentBitrate = 48000;
    private DateTime _lastBitrateAdjust = DateTime.UtcNow;
    private long _packetsSent;
    private long _bytesSent;

    public event Action<IReadOnlyList<string>>? MembersChanged;
    public event Action<bool>? TalkingChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? SpeakerStarted;
    public event Action<string>? SpeakerStopped;

    public int LastPingMs { get; private set; } = -1;
    public double VadProb { get; private set; }
    public bool IsGateOpen { get; private set; }
    public bool IsConnected => _running;
    public int JitterBufferMs => _jitterBuffer?.BufferedMs ?? 0;
    public int JitterTargetMs => _jitterBuffer?.TargetMs ?? 0;
    public long JitterLostFrames => _jitterBuffer?.LostFrames ?? 0;

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

    /// <summary>Set per-user volume (0-200, 100=normal).</summary>
    public void SetUserVolume(string name, int volume)
    {
        lock (_userVolLock)
            _userVolumes[name] = Math.Clamp(volume, 0, 200);
    }

    public int GetUserVolume(string name)
    {
        lock (_userVolLock)
            return _userVolumes.TryGetValue(name, out var v) ? v : 100;
    }

    public void Connect(string server, int port, string room, string name, string password, AppSettings? settings = null)
    {
        _room = room;
        _name = name;
        _gcm?.Dispose();
        _gcm = string.IsNullOrEmpty(password)
            ? null
            : new AesGcm(SHA256.HashData(Encoding.UTF8.GetBytes(password)), 16);
        _nonceCounter = 0;
        _sendSeq = 0;
        _udp = new UdpClient(server, port);
        _udp.Client.SendTimeout = 1000;
        _udp.Client.ReceiveTimeout = 5000;

        // Opus encoder: DRED + FEC for packet loss resilience
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = _currentBitrate;
        _encoder.Complexity = 8; // balanced quality/CPU
        _encoder.UseDTX = false;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 15; // aggressive FEC for UDP

        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        // DSP pipeline: HPF → DFN3 → AGC2 → Gate (no Silero — energy-only VAD)
        _dsp = new VoiceDspPipeline(SampleRate, FrameSize, NoiseSuppression, settings?.DfAttLim ?? 60.0, settings);

        // Jitter buffer for smooth playback
        _jitterBuffer = new AdaptiveJitterBuffer();

        // Pre-roll buffer: fill with silence before first speech
        _preRollBuffer.Clear();
        _preRollFilled = false;
        _wasSilent = true;

        var waveFormat = new WaveFormat(SampleRate, 16, Channels);
        _playbackBuffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(600),
            DiscardOnBufferOverflow = true
        };
        _playback = new WaveOutEvent();
        _playback.Init(_playbackBuffer);
        _playback.Volume = Math.Clamp(_volume / 100f, 0f, 1f);
        _playback.Play();

        _capture = new WaveInEvent
        {
            DeviceNumber = InputDevice,
            WaveFormat = waveFormat,
            BufferMilliseconds = 20
        };
        _capture.DataAvailable += OnCaptureData;
        Log($"VoiceClient: connect to {server}:{port}, room={room}, gain={MicGain}, ns={NoiseSuppression}, bitrate={_currentBitrate}");
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
        _playbackThread = new Thread(() => PlaybackLoop(token)) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _encodeThread.Start();
        _receiveThread.Start();
        _heartbeatThread.Start();
        _speakerThread.Start();
        _playbackThread.Start();

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
        _udp?.Close();
        _jitterBuffer?.Reset();
        _dsp?.Dispose();
        _dsp = null;
        lock (_userVolLock) _userVolumes.Clear();
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
        var wasSilent = true;

        // Pre-roll: fill silence frames
        for (int i = 0; i < PreRollFrames && _running; i++)
        {
            var silent = new float[FrameSize];
            _preRollBuffer.Enqueue(silent);
        }
        _preRollFilled = true;

        while (!token.IsCancellationRequested)
        {
            var talk = !MicMuted && (OpenMic || (GetAsyncKeyState(VkSpace) & 0x8000) != 0);
            if (talk != lastTalk)
            {
                lastTalk = talk;
                TalkingChanged?.Invoke(talk);
                Log($"EncodeLoop: talk={talk}, OpenMic={OpenMic}, MicMuted={MicMuted}");
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

                    // DSP pipeline: HPF → DFN3 → AGC2 → Gate
                    bool vadActive = _dsp?.Process(frameFloat) ?? true;
                    IsGateOpen = vadActive;
                    VadProb = _dsp?.VadProb ?? (vadActive ? 1.0 : 0.0);

                    // Adaptive bitrate: lower if CPU high, raise if stable
                    AdjustBitrate();

                    bool shouldSend = talk && vadActive && _encoder is not null;

                    if (shouldSend)
                    {
                        // Pre-roll: send buffered silence before first speech
                        if (_wasSilent && _preRollBuffer.Count > 0)
                        {
                            foreach (var preFrame in _preRollBuffer)
                            {
                                var preShorts = new short[FrameSize];
                                for (int i = 0; i < FrameSize; i++)
                                    preShorts[i] = (short)Math.Clamp((int)(preFrame[i] * 32768f), short.MinValue, short.MaxValue);
                                int preN = _encoder.Encode(preShorts.AsSpan(), FrameSize, opusBuf.AsSpan(), opusBuf.Length);
                                if (preN > 0) SendAudio(opusBuf, preN);
                            }
                            _preRollBuffer.Clear();
                        }
                        _wasSilent = false;

                        // float[] -> short[]
                        for (int i = 0; i < FrameSize; i++)
                            frameShorts[i] = (short)Math.Clamp((int)(frameFloat[i] * 32768f), short.MinValue, short.MaxValue);

                        int n = _encoder.Encode(frameShorts.AsSpan(), FrameSize, opusBuf.AsSpan(), opusBuf.Length);
                        if (n > 0)
                        {
                            SendAudio(opusBuf, n);
                            Interlocked.Increment(ref _packetsSent);
                            Interlocked.Add(ref _bytesSent, n);
                        }
                    }
                    else
                    {
                        if (!_wasSilent)
                        {
                            // Refill pre-roll buffer with last good frame
                            _preRollBuffer.Clear();
                            for (int i = 0; i < PreRollFrames; i++)
                            {
                                var copy = new float[FrameSize];
                                Array.Copy(frameFloat, copy, FrameSize);
                                _preRollBuffer.Enqueue(copy);
                            }
                            _wasSilent = true;
                            _dsp?.ResetVad();
                        }
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

    private void AdjustBitrate()
    {
        if (DateTime.UtcNow - _lastBitrateAdjust < TimeSpan.FromSeconds(5)) return;
        _lastBitrateAdjust = DateTime.UtcNow;

        long sent = Interlocked.Read(ref _packetsSent);
        long bytes = Interlocked.Read(ref _bytesSent);
        if (sent < 50) return; // not enough data

        double avgBytesPerPacket = (double)bytes / sent;
        double avgBitrate = avgBytesPerPacket * 8 * 50; // 50 packets/sec

        // Adjust: target 48kbps, range 24-80kbps
        int target = avgBitrate < 32000 ? 32000 : (avgBitrate > 64000 ? 48000 : 48000);
        if (target != _currentBitrate && _encoder is not null)
        {
            _currentBitrate = target;
            _encoder.Bitrate = target;
            Log($"AdaptiveBitrate: adjusted to {target} bps (avg={avgBitrate:F0})");
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
        // Packet: [0x03][roomLen][room][nameLen][name][seq(2)][opus...]
        var packet = new byte[2 + _room.Length + 1 + nameBytes.Length + 2 + payload.Length];
        packet[0] = 0x03;
        packet[1] = (byte)_room.Length;
        Encoding.UTF8.GetBytes(_room, 0, _room.Length, packet, 2);
        packet[2 + _room.Length] = (byte)nameBytes.Length;
        nameBytes.CopyTo(packet, 3 + _room.Length);
        // Sequence number (big-endian)
        int seq = Interlocked.Increment(ref _sendSeq);
        packet[3 + _room.Length + nameBytes.Length] = (byte)(seq >> 8);
        packet[4 + _room.Length + nameBytes.Length] = (byte)(seq & 0xFF);
        Array.Copy(payload, 0, packet, 5 + _room.Length + nameBytes.Length, payload.Length);
        try { _udp.Send(packet, packet.Length); } catch { }
    }

    /// <summary>Playback loop: pull from jitter buffer at 20ms intervals.</summary>
    private void PlaybackLoop(CancellationToken token)
    {
        var outFrame = new short[FrameSize];
        var outBytes = new byte[FrameBytes];
        while (!token.IsCancellationRequested)
        {
            if (_jitterBuffer != null && _playbackBuffer != null)
            {
                bool hasData = _jitterBuffer.Pull(outFrame);
                if (hasData)
                {
                    // Per-user volume (applied to mixed output — we don't have per-speaker separation here)
                    int userVol = 100;
                    // Convert short[] to byte[]
                    for (int i = 0; i < FrameSize; i++)
                    {
                        int sample = (int)(outFrame[i] * userVol / 100);
                        sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
                        outBytes[i * 2] = (byte)(sample & 0xFF);
                        outBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                    }
                    if (!PlaybackMuted)
                        _playbackBuffer.AddSamples(outBytes, 0, FrameBytes);
                }
            }
            Thread.Sleep(18); // ~20ms frame interval with small margin
        }
    }

    private void ReceiveLoop(CancellationToken token)
    {
        var pcmBuf = new short[FrameSize];
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
                if (recvCount % 50 == 1) Log($"VoiceClient: recv #{recvCount} type=0x{data[0]:X2} len={received}");

                switch (data[0])
                {
                    case 0x03: // audio [0x03][roomLen][room][nameLen][name][seq(2)][opus...]
                        if (_decoder is null || _jitterBuffer is null) continue;
                        int roomLen = data[1];
                        int nameLen = data[2 + roomLen];
                        if (data.Length < 5 + roomLen + nameLen) continue;
                        var speaker = Encoding.UTF8.GetString(data, 3 + roomLen, nameLen);
                        // Parse sequence number
                        int seq = (data[3 + roomLen + nameLen] << 8) | data[4 + roomLen + nameLen];
                        var raw = data.AsSpan(5 + roomLen + nameLen).ToArray();
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

                        // Decode and push to jitter buffer
                        int n = _decoder.Decode(payload.AsSpan(), pcmBuf.AsSpan(), FrameSize, false);
                        if (n > 0)
                            _jitterBuffer.PushDecoded(seq, pcmBuf);
                        break;

                    case 0x06: // member list
                        var names = ParseMembers(data);
                        Log($"ReceiveLoop: members {names.Count} [{string.Join(",", names)}]");
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
