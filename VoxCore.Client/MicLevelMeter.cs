using NAudio.Wave;

namespace VoxCore.Client;

public sealed class MicLevelMeter : IDisposable
{
    private WaveInEvent? _capture;
    private BufferedWaveProvider? _loopbackBuffer;
    private WaveOutEvent? _loopbackOut;

    public int DeviceIndex { get; set; }
    public bool Loopback { get; set; }
    public event Action<float>? LevelChanged;

    public static string[] GetDevices()
    {
        var list = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            list.Add(caps.ProductName);
        }
        return list.ToArray();
    }

    public void Start()
    {
        Stop();
        var fmt = new WaveFormat(48000, 16, 1);
        _capture = new WaveInEvent { DeviceNumber = DeviceIndex, WaveFormat = fmt, BufferMilliseconds = 20 };
        _capture.DataAvailable += OnData;
        if (Loopback)
        {
            _loopbackBuffer = new BufferedWaveProvider(fmt)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true
            };
            _loopbackOut = new WaveOutEvent();
            _loopbackOut.Init(_loopbackBuffer);
            _loopbackOut.Play();
        }
        _capture.StartRecording();
    }

    public void Stop()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
        _loopbackOut?.Stop();
        _loopbackOut?.Dispose();
        _loopbackOut = null;
        _loopbackBuffer = null;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        float sum = 0;
        int count = e.BytesRecorded / 2;
        for (int i = 0; i < count; i++)
        {
            short s = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
            float f = s / 32768f;
            sum += f * f;
        }
        float rms = count > 0 ? (float)Math.Sqrt(sum / count) : 0f;
        if (rms > 1f) rms = 1f;
        LevelChanged?.Invoke(rms);
        _loopbackBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    public void Dispose() => Stop();
}