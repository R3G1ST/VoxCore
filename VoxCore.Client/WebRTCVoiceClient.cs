using System.Net;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace VoxCore.Client;

public sealed class WebRTCVoiceClient : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSize = 960;

    private readonly ApiClient _api;
    private readonly string _serverHost;
    private RTCPeerConnection? _pc;
    private WaveInEvent? _capture;
    private WaveOutEvent? _playback;
    private BufferedWaveProvider? _playbackBuffer;
    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;
    private DeepFilterNet? _deepFilter;
    private CancellationTokenSource _cts = new();
    private volatile bool _running;
    private string _roomId = "";
    private int _channelId;

    private float _agcGain = 1.0f;
    private const float AgcTarget = 0.10f;
    private const float AgcMaxGain = 8.0f;
    private const float AgcMinGain = 0.2f;
    private const float AgcAttack = 0.10f;
    private const float AgcRelease = 0.005f;
    private float _prevRms = 0f;

    private float _dcOffset = 0f;
    private const float DcAlpha = 0.999f;

    private const float SoftLimitThreshold = 0.92f;
    private const float SoftLimitK = 6.0f;

    private readonly float[] _rmsHist = new float[16];
    private int _rmsIdx;

    public bool IsConnected => _pc?.connectionState == RTCPeerConnectionState.connected;
    public string RoomId => _roomId;
    public bool MicMuted { get; set; }
    public bool PlaybackMuted { get; set; }
    public double MicGain { get; set; } = 1.0;
    public bool NoiseSuppression { get; set; } = true;
    public bool AgcEnabled { get; set; } = true;
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

    public WebRTCVoiceClient(ApiClient api, string serverHost)
    {
        _api = api;
        _serverHost = serverHost;

        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 256000;
        _encoder.Complexity = 10;
        _encoder.UseDTX = true;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 15;

        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        try
        {
            var dfPath = Path.Combine(AppContext.BaseDirectory, "deep_filter_ladspa.dll");
            _deepFilter = new DeepFilterNet(dfPath, SampleRate);
            Console.WriteLine("[Voice] DeepFilterNet3 loaded");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Voice] DF3 load failed, no denoise: {ex.Message}");
        }
    }

    public async Task ConnectAsync(int channelId)
    {
        _channelId = channelId;
        StatusChanged?.Invoke("РїРѕРґРєР»СЋС‡РµРЅРёРµ Рє WebRTC...");

        var (peers, names, roomId) = await _api.WebRTCJoinAsync(channelId);
        _roomId = roomId;
        StatusChanged?.Invoke($"РІ РєРѕРјРЅР°С‚Рµ {roomId}, peers: {peers.Count}");
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

        var fmtp = "minptime=10;useinbandfec=1;usedtx=1;maxaveragebitrate=256000";
        var audioTrack = new MediaStreamTrack(
            new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, fmtp));
        _pc.addTrack(audioTrack);

        _pc.OnRtpPacketReceived += (ep, media, rtpPkt) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            try
            {
                if (_decoder == null) return;
                var pcm = new short[FrameSize];
                int n = _decoder.Decode(rtpPkt.Payload.AsSpan(), pcm.AsSpan(), FrameSize, false);
                if (n > 0 && !PlaybackMuted)
                {
                    var outBytes = new byte[n * 2];
                    float vol = _volume / 100f;
                    for (int i = 0; i < n; i++)
                    {
                        float s = pcm[i] / 32768f;
                        s *= vol;
                        s = SoftClip(s);
                        short outSample = (short)Math.Clamp((int)(s * 32768f), short.MinValue, short.MaxValue);
                        outBytes[i * 2] = (byte)(outSample & 0xFF);
                        outBytes[i * 2 + 1] = (byte)((outSample >> 8) & 0xFF);
                    }
                    _playbackBuffer?.AddSamples(outBytes, 0, n * 2);
                }
            }
            catch { }
        };

        _pc.onconnectionstatechange += (state) =>
        {
            StatusChanged?.Invoke($"WebRTC: {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                connectedTcs.TrySetResult(true);
            }
            else if (state == RTCPeerConnectionState.failed)
            {
                connectedTcs.TrySetResult(false);
            }
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
            BufferDuration = TimeSpan.FromMilliseconds(80),
            DiscardOnBufferOverflow = true
        };
        _playback.Init(_playbackBuffer);

        _running = true;
        _cts = new CancellationTokenSource();

        _capture.StartRecording();
        _playback.Play();

        var offer = _pc.createOffer(null);
        _pc.setLocalDescription(offer);

        StatusChanged?.Invoke("РѕС‚РїСЂР°РІРєР° offer, РѕР¶РёРґР°РЅРёРµ ICE...");

        var (answerSdp, _) = await _api.WebRTCOfferAsync(_roomId, offer.sdp);
        StatusChanged?.Invoke($"answer SDP ({answerSdp.Length} chars), setting remote...");
        var answer = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp };
        var res = _pc.setRemoteDescription(answer);
        StatusChanged?.Invoke($"setRemoteDescription: {res}, ice={_pc.iceConnectionState}, gathering={_pc.iceGatheringState}");

        StatusChanged?.Invoke("offer СѓСЃС‚Р°РЅРѕРІР»РµРЅ, РѕР¶РёРґР°РЅРёРµ РїРѕРґРєР»СЋС‡РµРЅРёСЏ...");

        var completed = await Task.WhenAny(connectedTcs.Task, Task.Delay(15000));
        if (completed == connectedTcs.Task && await connectedTcs.Task)
        {
            StatusChanged?.Invoke("WebRTC РїРѕРґРєР»СЋС‡РµРЅ (Opus 256kbps + AGC + DeepFilterNet3)");
        }
        else
        {
            throw new Exception("WebRTC ICE failed");
        }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || MicMuted || _pc == null) return;

        try
        {
            if (_encoder == null) return;

            var frameShorts = new short[FrameSize];
            int sampleCount = Math.Min(e.BytesRecorded / 2, FrameSize);

            for (int i = 0; i < sampleCount; i++)
            {
                float s = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8)) / 32768f;

                _dcOffset = _dcOffset * DcAlpha + s * (1f - DcAlpha);
                s -= _dcOffset;

                if (MicGain != 1.0)
                    s *= (float)MicGain;

                frameShorts[i] = (short)Math.Clamp((int)(s * 32768f), short.MinValue, short.MaxValue);
            }

            if (NoiseSuppression && _deepFilter != null && _deepFilter.IsLoaded)
            {
                Span<float> fIn = new float[sampleCount];
                Span<float> fOut = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                    fIn[i] = frameShorts[i] / 32768f;
                _deepFilter.Process(fIn, fOut, sampleCount);
                for (int i = 0; i < sampleCount; i++)
                    frameShorts[i] = (short)Math.Clamp((int)(fOut[i] * 32768f), short.MinValue, short.MaxValue);
            }

            if (AgcEnabled)
            {
                float sumSq = 0f;
                for (int i = 0; i < sampleCount; i++)
                {
                    float f = frameShorts[i] / 32768f;
                    sumSq += f * f;
                }
                float rms = MathF.Sqrt(sumSq / Math.Max(1, sampleCount));

                _rmsHist[_rmsIdx % _rmsHist.Length] = rms;
                _rmsIdx++;
                float smoothRms = 0f;
                for (int i = 0; i < _rmsHist.Length; i++) smoothRms += _rmsHist[i];
                smoothRms /= _rmsHist.Length;
                _prevRms = smoothRms;

                if (smoothRms > 0.001f)
                {
                    float desiredGain = AgcTarget / smoothRms;
                    desiredGain = Math.Clamp(desiredGain, AgcMinGain, AgcMaxGain);
                    float rate = desiredGain > _agcGain ? AgcAttack : AgcRelease;
                    _agcGain += (desiredGain - _agcGain) * rate;
                    _agcGain = Math.Clamp(_agcGain, AgcMinGain, AgcMaxGain);
                }
                else
                {
                    _agcGain += (1.0f - _agcGain) * AgcRelease;
                }

                for (int i = 0; i < sampleCount; i++)
                {
                    float s = frameShorts[i] / 32768f;
                    s *= _agcGain;
                    s = SoftClip(s);
                    frameShorts[i] = (short)Math.Clamp((int)(s * 32768f), short.MinValue, short.MaxValue);
                }
            }

            var opusBuf = new byte[4000];
            int n = _encoder.Encode(frameShorts.AsSpan(), FrameSize, opusBuf.AsSpan(), opusBuf.Length);
            if (n > 0)
            {
                var opusBytes = opusBuf.AsSpan(0, n).ToArray();
                _pc.SendAudio(960, opusBytes);
            }
        }
        catch { }
    }

    private static float SoftClip(float x)
    {
        float abs = MathF.Abs(x);
        if (abs <= SoftLimitThreshold) return x;
        float over = abs - SoftLimitThreshold;
        float compressed = SoftLimitThreshold + over / (1f + over * SoftLimitK);
        return MathF.Sign(x) * compressed;
    }

    public void Disconnect()
    {
        _running = false;
        _cts.Cancel();
        _capture?.StopRecording();
        _playback?.Stop();
        _capture?.Dispose();
        _playback?.Dispose();
        _pc?.Close("disconnect");
        _pc?.Dispose();
        _pc = null;
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
        _cts.Dispose();
    }
}
