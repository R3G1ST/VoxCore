using System.Net;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;
using RNNoise.NET;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace VoxCore.Client;

public sealed class WebRTCVoiceClient : IDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSize = 960; // 20ms at 48kHz

    private readonly ApiClient _api;
    private readonly string _serverHost;
    private RTCPeerConnection? _pc;
    private WaveInEvent? _capture;
    private WaveOutEvent? _playback;
    private BufferedWaveProvider? _playbackBuffer;
    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;
    private Denoiser? _denoiser;
    private readonly float[] _denoiseBuf = new float[FrameSize];
    private CancellationTokenSource _cts = new();
    private volatile bool _running;
    private string _roomId = "";
    private int _channelId;

    public bool IsConnected => _pc?.connectionState == RTCPeerConnectionState.connected;
    public string RoomId => _roomId;
    public bool MicMuted { get; set; }
    public bool PlaybackMuted { get; set; }
    public double MicGain { get; set; } = 1.0;
    public bool NoiseSuppression { get; set; } = true;
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

    private readonly Dictionary<string, DateTime> _speakerLast = new();
    private readonly object _speakerLock = new();

    public WebRTCVoiceClient(ApiClient api, string serverHost)
    {
        _api = api;
        _serverHost = serverHost;

        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 128000;
        _encoder.Complexity = 10;
        _encoder.UseDTX = false;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 5;

        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        _denoiser = new Denoiser();
    }

    public async Task ConnectAsync(int channelId)
    {
        _channelId = channelId;
        StatusChanged?.Invoke("подключение к WebRTC...");

        var (peers, roomId) = await _api.WebRTCJoinAsync(channelId);
        _roomId = roomId;
        StatusChanged?.Invoke($"в комнате {roomId}, peers: {peers.Count}");

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

        var audioTrack = new MediaStreamTrack(SDPWellKnownMediaFormatsEnum.PCMU);
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
                    for (int i = 0; i < n; i++)
                    {
                        short sample = pcm[i];
                        if (_volume != 100)
                            sample = (short)Math.Clamp(sample * (_volume / 100f), short.MinValue, short.MaxValue);
                        outBytes[i * 2] = (byte)(sample & 0xFF);
                        outBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                    }
                    _playbackBuffer?.AddSamples(outBytes, 0, n * 2);
                }
            }
            catch { }
        };

        _pc.onconnectionstatechange += (state) =>
        {
            StatusChanged?.Invoke($"WebRTC: {state}");
            if (state == RTCPeerConnectionState.failed)
            {
                _pc?.Close("ice failure");
                Disconnect();
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
            BufferDuration = TimeSpan.FromMilliseconds(200),
            DiscardOnBufferOverflow = true
        };
        _playback.Init(_playbackBuffer);

        _running = true;
        _cts = new CancellationTokenSource();

        _capture.StartRecording();
        _playback.Play();

        var offer = _pc.createAnswer(null);
        await _pc.setLocalDescription(offer);

        var (answerSdp, _) = await _api.WebRTCOfferAsync(_roomId, offer.sdp);
        var answer = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp };
        _pc.setRemoteDescription(answer);

        StatusChanged?.Invoke("WebRTC подключен (Opus 128kbps + RNNoise)");
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || MicMuted || _pc == null) return;

        try
        {
            if (_encoder == null) return;

            var frameShorts = new short[FrameSize];
            for (int i = 0; i < Math.Min(e.BytesRecorded / 2, FrameSize); i++)
            {
                short sample = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
                if (MicGain != 1.0)
                    sample = (short)Math.Clamp(sample * MicGain, short.MinValue, short.MaxValue);
                frameShorts[i] = sample;
            }

            if (NoiseSuppression && _denoiser != null)
            {
                for (int i = 0; i < FrameSize; i++)
                    _denoiseBuf[i] = frameShorts[i] / 32768f;
                _denoiser.Denoise(_denoiseBuf.AsSpan(0, 480), false);
                _denoiser.Denoise(_denoiseBuf.AsSpan(480, 480), false);
                for (int i = 0; i < FrameSize; i++)
                    frameShorts[i] = (short)Math.Clamp((int)(_denoiseBuf[i] * 32768f), short.MinValue, short.MaxValue);
            }

            var opusBuf = new byte[1000];
            int n = _encoder.Encode(frameShorts.AsSpan(), FrameSize, opusBuf.AsSpan(), opusBuf.Length);
            if (n > 0)
            {
                var opusBytes = opusBuf.AsSpan(0, n).ToArray();
                _pc.SendAudio(SampleRate, opusBytes);
            }
        }
        catch { }
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
        _denoiser?.Dispose();
        _cts.Dispose();
    }
}
