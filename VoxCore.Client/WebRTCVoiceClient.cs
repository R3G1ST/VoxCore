using System.Collections.Concurrent;
using System.Net;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VoxCore.Client.Dsp;

namespace VoxCore.Client;

/// <summary>
/// Голосовой клиент WebRTC. Цепочка захвата:
/// HPF 80Hz -> DeepFilterNet3 -> Gate -40dB -> AGC2 (-18dBFS, limiter -1dB)
/// -> Silero VAD (48k->16k) -> Opus 64k VBR + FEC.
/// Приём: per-speaker AdaptiveJitterBuffer -> per-user volume -> master EQ -> mix.
/// </summary>
public sealed class WebRTCVoiceClient : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSize = 960;          // 20ms
    private const int PrerollFrames = 10;       // 200ms

    private readonly ApiClient _api;
    private readonly string _serverHost;
    private RTCPeerConnection? _pc;
    private WaveInEvent? _capture;
    private WaveOutEvent? _playback;
    private BufferedWaveProvider? _playbackBuffer;
    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;
    private DeepFilterNet? _deepFilter;
    private SileroVad? _vad;
    private IntPtr _opusEncNative;
    private IntPtr _opusDecNative;
    private bool _useNativeOpus;
    private CancellationTokenSource _cts = new();
    private volatile bool _running;
    private string _roomId = "";
    private int _channelId;

    // --- DSP цепочка ---
    private readonly HpfBiquad _hpf = new(80.0, SampleRate);
    private readonly NoiseGate _gate = new(-40.0, SampleRate, FrameSize);
    private readonly Agc2Limiter _agc2 = new(-18.0, 18.0, SampleRate, 5.0);
    private readonly Equalizer3Band _masterEq = new();
    private readonly Queue<float[]> _preroll = new();
    private bool _wasSilent = true;
    private ApmProcessor? _apm;
    private readonly object _apmLock = new();

    // --- Приём: per-speaker jitter + громкости ---
    private sealed class SpeakerJb
    {
        public AdaptiveJitterBuffer Jb = new();
        public DateTime LastActivity = DateTime.UtcNow;
    }
    private readonly ConcurrentDictionary<uint, SpeakerJb> _speakers = new();   // ssrc -> jb
    private readonly ConcurrentDictionary<uint, string> _ssrcNames = new();     // ssrc -> ник
    private readonly ConcurrentDictionary<string, float> _userVolumes = new();  // ник -> 0..2

    private Thread? _mixerThread;

    public bool IsConnected => _pc?.connectionState == RTCPeerConnectionState.connected;
    public bool IsDeepFilterLoaded => _deepFilter?.IsLoaded == true;
    public bool IsVadLoaded => _vad != null;
    public bool IsAecActive => _apm != null;
    public int BitrateKbps => (_encoder?.Bitrate ?? 0) / 1000;
    public bool IsFec => _encoder?.UseInbandFEC == true;
    public bool IsDredActive => _useNativeOpus;
    public bool IsDtx => _encoder?.UseDTX == true;
    public string RoomId => _roomId;
    public bool MicMuted { get; set; }
    public bool PlaybackMuted { get; set; }
    public double MicGain { get; set; } = 1.0;
    public bool NoiseSuppression { get; set; } = true;
    public bool AgcEnabled { get; set; } = true;
    public double AgcGainDb => 20.0 * Math.Log10(Math.Max(1e-4, _agc2.CurrentGain));
    public double VadProb => _vad?.LastProb ?? 0;
    public bool IsGateOpen => _gate.IsOpen;

    // Статы приёма (агрегат по спикерам)
    public (int TargetMs, int BufferedMs, long Lost) JitterStats
    {
        get
        {
            int t = 0, b = 0; long l = 0;
            foreach (var s in _speakers.Values)
            {
                t = Math.Max(t, s.Jb.TargetMs);
                b += s.Jb.BufferedMs;
                l += s.Jb.LostFrames;
            }
            return (t, b, l);
        }
    }

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

    public event Action<string>? StatusChanged;
    public event Action<string>? SpeakerStarted;
    public event Action<string>? SpeakerStopped;
    public event Action<IReadOnlyList<string>>? MembersChanged;

    internal static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "voxcore-client.log"),
                $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    public WebRTCVoiceClient(ApiClient api, string serverHost, AppSettings settings)
    {
        _api = api;
        _serverHost = serverHost;

        // Opus 64k VBR (VBR в libopus/Concentus включён по умолчанию), FEC on, DTX off
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 64000;
        _encoder.Complexity = 10;
        _encoder.UseDTX = false;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 15;

        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);

        // Native Opus 1.5.2 + DRED (опционально, fallback на Concentus)
        try
        {
            _opusEncNative = Dsp.OpusNative.opus_encoder_create(SampleRate, Channels, Dsp.OpusNative.OPUS_APPLICATION_VOIP, out int encErr);
            if (encErr == 0 && _opusEncNative != IntPtr.Zero)
            {
                Dsp.OpusNative.opus_encoder_ctl(_opusEncNative, Dsp.OpusNative.OPUS_SET_BITRATE_REQUEST, 64000);
                Dsp.OpusNative.opus_encoder_ctl(_opusEncNative, Dsp.OpusNative.OPUS_SET_INBAND_FEC_REQUEST, 1);
                Dsp.OpusNative.opus_encoder_ctl(_opusEncNative, Dsp.OpusNative.OPUS_SET_PACKET_LOSS_PERC_REQUEST, 15);
                int dredRet = Dsp.OpusNative.opus_encoder_ctl(_opusEncNative, Dsp.OpusNative.OPUS_SET_DRED_DURATION_REQUEST, 4);
                _opusDecNative = Dsp.OpusNative.opus_decoder_create(SampleRate, Channels, out int decErr);
                if (decErr == 0 && dredRet == 0)
                {
                    _useNativeOpus = true;
                    Log($"Native Opus {Dsp.OpusNative.GetVersion()} DRED 40ms loaded");
                }
                else
                {
                    if (_opusDecNative != IntPtr.Zero) Dsp.OpusNative.opus_decoder_destroy(_opusDecNative);
                    Dsp.OpusNative.opus_encoder_destroy(_opusEncNative);
                    _opusEncNative = _opusDecNative = IntPtr.Zero;
                }
            }
        }
        catch (Exception ex) { Log($"Native Opus not loaded: {ex.Message}"); }

        // DeepFilterNet3 — DLL только из %LOCALAPPDATA%\VoxCore\native (или native/ у exe для dev).
        try
        {
            var dfLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoxCore", "native", "deep_filter_ladspa.dll");
            var dfApp = Path.Combine(AppContext.BaseDirectory, "native", "deep_filter_ladspa.dll");
            var dfPath = File.Exists(dfLocal) ? dfLocal : dfApp;
            _deepFilter = new DeepFilterNet(dfPath, SampleRate, settings.DfAttLim);
            Log("DeepFilterNet3 loaded");
        }
        catch (Exception ex)
        {
            _deepFilter = null;
            Log($"DF3 load failed, denoise off: {ex.Message}");
        }

        // Silero VAD v5
        try
        {
            var modelLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoxCore", "models", "silero_vad.onnx");
            var modelApp = Path.Combine(AppContext.BaseDirectory, "models", "silero_vad.onnx");
            var modelDir = Path.GetDirectoryName(modelLocal)!;
            if (!File.Exists(modelLocal) && File.Exists(modelApp))
            {
                Directory.CreateDirectory(modelDir);
                File.Copy(modelApp, modelLocal, true);
            }
            var modelPath = File.Exists(modelLocal) ? modelLocal : modelApp;
            if (File.Exists(modelPath))
            {
                _vad = new SileroVad(modelPath);
                Log("Silero VAD loaded");
            }
            else Log("Silero VAD model not found");
        }
        catch (Exception ex)
        {
            _vad = null;
            Log($"VAD load failed: {ex.Message}");
        }

        _masterEq.LowDb = settings.EqLow;
        _masterEq.MidDb = settings.EqMid;
        _masterEq.HighDb = settings.EqHigh;
        foreach (var kv in settings.UserVolumes)
            _userVolumes[kv.Key] = (float)Math.Clamp(kv.Value, 0.0, 2.0);

        try { ApmLoader.EnsureLoaded(); _apm = new ApmProcessor(aec3: true, ns: false, agc2: false, hpf: false); Log("APM AEC3 loaded"); }
        catch (Exception ex) { Log($"APM not loaded: {ex.Message}"); }
    }

    public void ApplyEq(double low, double mid, double high)
    {
        _masterEq.LowDb = low;
        _masterEq.MidDb = mid;
        _masterEq.HighDb = high;
    }

    public void SetUserVolume(string nick, double volume) =>
        _userVolumes[nick] = (float)Math.Clamp(volume, 0.0, 2.0);

    public double GetUserVolume(string nick) =>
        _userVolumes.TryGetValue(nick, out var v) ? v : 1.0;

    public async Task ConnectAsync(int channelId)
    {
        _channelId = channelId;
        Log($"ConnectAsync channel={channelId}");
        StatusChanged?.Invoke("подключение к WebRTC...");

        var (peers, names, roomId) = await _api.WebRTCJoinAsync(channelId);
        _roomId = roomId;
        StatusChanged?.Invoke($"в комнате {roomId}, peers: {peers.Count}");
        // peers[i] <-> names[i+1] (names[0] — это мы сами)
        _ssrcNames.Clear();
        for (int i = 0; i < peers.Count && i + 1 < names.Count; i++)
            _ssrcNames[(uint)peers[i]] = names[i + 1];
        if (names.Count > 0)
            MembersChanged?.Invoke(names);

        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
                new RTCIceServer
                {
                    urls = "turn:194.31.204.5:3478",
                    username = "voxcore",
                    credential = "voxcore123"
                },
                new RTCIceServer
                {
                    urls = "turn:194.31.204.5:3478?transport=tcp",
                    username = "voxcore",
                    credential = "voxcore123"
                }
            }
        };
        _pc = new RTCPeerConnection(config);

        var connectedTcs = new TaskCompletionSource<bool>();

        _pc.onicecandidate += (candidate) =>
        {
            if (candidate != null && !string.IsNullOrEmpty(candidate.candidate))
            {
                StatusChanged?.Invoke($"ICE: {candidate.candidate.Substring(0, Math.Min(60, candidate.candidate.Length))}...");
                _ = _api.WebRTCIceAsync(_roomId, candidate.candidate);
            }
        };

        var fmtp = "minptime=10;useinbandfec=1;usedtx=0;maxaveragebitrate=96000";
        var audioTrack = new MediaStreamTrack(
            new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, fmtp));
        _pc.addTrack(audioTrack);

        _pc.OnRtpPacketReceived += (ep, media, rtpPkt) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            try
            {
                var pcm = new short[FrameSize];
                int n;
                if (_useNativeOpus && _opusDecNative != IntPtr.Zero)
                    n = Dsp.OpusNative.opus_decode(_opusDecNative, rtpPkt.Payload, rtpPkt.Payload.Length, pcm, FrameSize, 0);
                else
                {
                    if (_decoder == null) return;
                    n = _decoder.Decode(rtpPkt.Payload.AsSpan(), pcm.AsSpan(), FrameSize, false);
                }
                if (n <= 0) return;

                var jb = _speakers.GetOrAdd(rtpPkt.Header.SyncSource, _ => new SpeakerJb());
                jb.Jb.PushDecoded(rtpPkt.Header.SequenceNumber, pcm);
                jb.LastActivity = DateTime.UtcNow;
                SpeakerStarted?.Invoke(SpeakerName(rtpPkt.Header.SyncSource));
            }
            catch { }
        };

        _pc.onconnectionstatechange += (state) =>
        {
            StatusChanged?.Invoke($"WebRTC: {state}");
            Log($"conn state: {state}, ice={_pc?.iceConnectionState}");
            if (state == RTCPeerConnectionState.connected)
                connectedTcs.TrySetResult(true);
            else if (state == RTCPeerConnectionState.failed)
                connectedTcs.TrySetResult(false);
        };

        _capture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = 20,
            DeviceNumber = 0
        };
        _capture.DataAvailable += OnCaptureDataAvailable;

        _playback = new WaveOutEvent { Volume = Math.Clamp(_volume / 100f, 0f, 1f) };
        _playbackBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels))
        {
            BufferDuration = TimeSpan.FromMilliseconds(400),
            DiscardOnBufferOverflow = true
        };
        _playback.Init(_playbackBuffer);

        _running = true;
        _cts = new CancellationTokenSource();
        _mixerThread = new Thread(() => MixerLoop(_cts.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _mixerThread.Start();

        _capture.StartRecording();
        _playback.Play();

        var offer = _pc.createOffer(null);
        _pc.setLocalDescription(offer);

        StatusChanged?.Invoke("отправка offer, ожидание ICE...");

        var (answerSdp, _) = await _api.WebRTCOfferAsync(_roomId, offer.sdp);
        Log($"answer received ({answerSdp.Length} chars)");
        var answer = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp };
        var res = _pc.setRemoteDescription(answer);
        Log($"setRemoteDescription: {res}");

        var completed = await Task.WhenAny(connectedTcs.Task, Task.Delay(15000));
        if (completed == connectedTcs.Task && await connectedTcs.Task)
        {
            Log($"WebRTC connected (opus={BitrateKbps}k vad={IsVadLoaded} df={IsDeepFilterLoaded})");
            StatusChanged?.Invoke("WebRTC подключен (Opus 64k + AGC2 + DFN3 + VAD)");
        }
        else
        {
            Log($"WebRTC ICE failed: ice={_pc?.iceConnectionState}, conn={_pc?.connectionState}");
            throw new Exception("WebRTC ICE failed");
        }
    }

    private string SpeakerName(uint ssrc) =>
        _ssrcNames.TryGetValue(ssrc, out var n) ? n : $"user{ssrc % 10000}";

    /// <summary>Миксер: раз в 20ms тянет кадры из всех jitter-буферов, громкость/EQ, микс.</summary>
    private void MixerLoop(CancellationToken token)
    {
        var mix = new float[FrameSize];
        var frame = new short[FrameSize];
        var outBytes = new byte[FrameSize * 2];
        var lastTalk = new Dictionary<uint, DateTime>();

        while (!token.IsCancellationRequested)
        {
            try
            {
                Array.Clear(mix);
                var now = DateTime.UtcNow;
                List<uint>? dead = null;

                foreach (var kv in _speakers)
                {
                    bool got = kv.Value.Jb.Pull(frame);
                    if (got)
                    {
                        string name = SpeakerName(kv.Key);
                        float vol = _userVolumes.TryGetValue(name, out var v) ? v : 1f;
                        Span<float> f = stackalloc float[FrameSize];
                        for (int i = 0; i < FrameSize; i++) f[i] = frame[i] / 32768f * vol;
                        _masterEq.Process(f);
                        for (int i = 0; i < FrameSize; i++) mix[i] += f[i];
                        lastTalk[kv.Key] = now;
                        SpeakerStarted?.Invoke(SpeakerName(kv.Key));
                    }
                    else if ((now - kv.Value.LastActivity).TotalSeconds > 15)
                        (dead ??= []).Add(kv.Key);
                }

                if (_apm != null)
                {
                    lock (_apmLock)
                    {
                        try { _apm.ProcessRender20ms(mix); } catch { }
                    }
                }

                if (!PlaybackMuted && _playbackBuffer != null)
                {
                    for (int i = 0; i < FrameSize; i++)
                    {
                        float s = Math.Clamp(mix[i], -1f, 1f);
                        short v = (short)(s * 32767f);
                        outBytes[i * 2] = (byte)(v & 0xFF);
                        outBytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
                    }
                    _playbackBuffer.AddSamples(outBytes, 0, FrameSize * 2);
                }

                if (dead != null)
                    foreach (var ssrc in dead)
                        if (_speakers.TryRemove(ssrc, out _))
                        {
                            lastTalk.Remove(ssrc);
                            SpeakerStopped?.Invoke(SpeakerName(ssrc));
                        }

                token.WaitHandle.WaitOne(20);
            }
            catch { }
        }
    }

    /// <summary>
    /// Захват 20ms: HPF -> MicGain -> DFN3 -> Gate -> AGC2 -> VAD(+pre-roll) -> Opus -> send.
    /// </summary>
    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || _pc == null || _encoder == null) return;

        try
        {
            int sampleCount = Math.Min(e.BytesRecorded / 2, FrameSize);
            if (sampleCount < FrameSize) return;

            var frame = new float[FrameSize];
            for (int i = 0; i < FrameSize; i++)
                frame[i] = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8)) / 32768f;

            // 1) HPF 80Hz (гул/DC)
            _hpf.Process(frame);

            // 2) MicGain
            if (MicGain != 1.0)
                for (int i = 0; i < FrameSize; i++) frame[i] *= (float)MicGain;

            // 2b) AEC3 (APM) — 20ms, после HPF, до DFN. Требует предварительного ProcessRender в миксере.
            if (_apm != null)
            {
                lock (_apmLock)
                {
                    try { _apm.ProcessCapture20ms(frame); } catch { }
                }
            }

            // 3) DeepFilterNet3
            if (NoiseSuppression && _deepFilter != null && _deepFilter.IsLoaded)
            {
                var denoised = new float[FrameSize];
                _deepFilter.Process(frame, denoised, FrameSize);
                frame = denoised;
            }

            // 4) Gate -40dB
            _gate.Process(frame);

            // 5) AGC2 + limiter
            if (AgcEnabled) _agc2.Process(frame);

            // 6) VAD: молчание = не отправляем
            bool vadActive = _vad?.Process(frame) ?? true;
            if (MicMuted) vadActive = false;

            if (!vadActive)
            {
                lock (_preroll)
                {
                    _preroll.Enqueue(frame);
                    while (_preroll.Count > PrerollFrames) _preroll.Dequeue();
                }
                if (!_wasSilent) _vad?.Reset();
                _wasSilent = true;
                return;
            }

            // 7) Начало речи: сбрасываем pre-roll (200ms обработанного аудио)
            float[]?[] flush = null;
            if (_wasSilent)
            {
                lock (_preroll)
                {
                    flush = _preroll.ToArray();
                    _preroll.Clear();
                }
            }
            _wasSilent = false;

            var opusBuf = new byte[4000];
            if (flush != null)
                foreach (var pf in flush)
                    EncodeSend(pf, opusBuf);
            EncodeSend(frame, opusBuf);
        }
        catch { }
    }

    private void EncodeSend(float[] frameFloat, byte[] opusBuf)
    {
        if (_pc == null) return;
        var frameShorts = new short[FrameSize];
        for (int i = 0; i < FrameSize; i++)
            frameShorts[i] = (short)Math.Clamp((int)(frameFloat[i] * 32768f), short.MinValue, short.MaxValue);

        int n;
        if (_useNativeOpus && _opusEncNative != IntPtr.Zero)
            n = Dsp.OpusNative.opus_encode(_opusEncNative, frameShorts, FrameSize, opusBuf, opusBuf.Length);
        else
        {
            if (_encoder == null) return;
            n = _encoder.Encode(frameShorts.AsSpan(), FrameSize, opusBuf.AsSpan(), opusBuf.Length);
        }
        if (n > 0)
            _pc.SendAudio(FrameSize, opusBuf.AsSpan(0, n).ToArray());
    }

    public void Disconnect()
    {
        _running = false;
        _cts.Cancel();
        try { _capture?.StopRecording(); } catch { }
        try { _playback?.Stop(); } catch { }
        _capture?.Dispose();
        _playback?.Dispose();
        try { _pc?.Close("disconnect"); } catch { }
        try { _pc?.Dispose(); } catch { }
        _pc = null;
        _speakers.Clear();
        if (_roomId.Length > 0)
        {
            _ = _api.WebRTCLeaveAsync(_roomId);
            _roomId = "";
        }
        StatusChanged?.Invoke("отключено");
    }

    public void Dispose()
    {
        Disconnect();
        _encoder?.Dispose();
        _decoder?.Dispose();
        _deepFilter?.Dispose();
        _vad?.Dispose();
        _apm?.Dispose();
        if (_opusEncNative != IntPtr.Zero) { try { Dsp.OpusNative.opus_encoder_destroy(_opusEncNative); } catch { } _opusEncNative = IntPtr.Zero; }
        if (_opusDecNative != IntPtr.Zero) { try { Dsp.OpusNative.opus_decoder_destroy(_opusDecNative); } catch { } _opusDecNative = IntPtr.Zero; }
        _cts.Dispose();
    }
}

