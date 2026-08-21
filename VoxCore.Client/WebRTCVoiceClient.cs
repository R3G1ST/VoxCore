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
    private const int FrameSize = 960; // 20ms at 48kHz

    private readonly ApiClient _api;
    private readonly string _serverHost;
    private RTCPeerConnection? _pc;
    private WaveInEvent? _capture;
    private WaveOutEvent? _playback;
    private BufferedWaveProvider? _playbackBuffer;
    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;
    private CancellationTokenSource _cts = new();
    private Thread? _encodeThread;
    private Thread? _receiveThread;
    private volatile bool _running;
    private string _roomId = "";
    private int _channelId;

    public bool IsConnected => _pc?.connectionState == RTCPeerConnectionState.connected;
    public bool MicMuted { get; set; }
    public bool PlaybackMuted { get; set; }
    public double MicGain { get; set; } = 1.0;

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
        _encoder.Bitrate = 96000;
        _encoder.Complexity = 10;
        _encoder.UseDTX = true;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
    }

    public async Task ConnectAsync(int channelId)
    {
        _channelId = channelId;
        StatusChanged?.Invoke("подключение к WebRTC...");

        // Join signaling room
        var (peers, roomId) = await _api.WebRTCJoinAsync(channelId);
        _roomId = roomId;
        StatusChanged?.Invoke($"в комнате {roomId}, peers: {peers.Count}");

        // Create peer connection
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            }
        };
        _pc = new RTCPeerConnection(config);

        // Add audio track
        var audioTrack = new MediaStreamTrack(SDPWellKnownMediaFormatsEnum.PCMU);
        _pc.addTrack(audioTrack);

        // Handle incoming audio
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
                        outBytes[i * 2] = (byte)(pcm[i] & 0xFF);
                        outBytes[i * 2 + 1] = (byte)((pcm[i] >> 8) & 0xFF);
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

        // Setup audio capture
        _capture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = 20,
            DeviceNumber = 0
        };
        _capture.DataAvailable += OnCaptureDataAvailable;

        // Setup playback
        _playback = new WaveOutEvent();
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

        // Create offer
        var offer = _pc.createAnswer(null);
        await _pc.setLocalDescription(offer);

        // Send offer to server
        var (answerSdp, _) = await _api.WebRTCOfferAsync(_roomId, offer.sdp);
        var answer = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp };
        _pc.setRemoteDescription(answer);

        StatusChanged?.Invoke("WebRTC подключен");
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_running || MicMuted || _pc == null) return;

        try
        {
            if (_encoder == null) return;

            // Convert bytes to shorts
            var frameShorts = new short[FrameSize];
            for (int i = 0; i < Math.Min(e.BytesRecorded / 2, FrameSize); i++)
            {
                short sample = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
                if (MicGain != 1.0)
                    sample = (short)Math.Clamp(sample * MicGain, short.MinValue, short.MaxValue);
                frameShorts[i] = sample;
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
        _cts.Dispose();
    }
}
