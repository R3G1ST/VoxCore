using NAudio.Wave;

namespace VoxCore.Client;

/// <summary>
/// Adaptive jitter buffer with PLC (Packet Loss Concealment).
/// Target: 80ms. Repeats last frame when buffer runs low.
/// Trims old frames when buffer grows too large.
/// </summary>
internal sealed class AdaptiveJitterBuffer : IWaveProvider
{
    private readonly WaveFormat _format;
    private readonly byte[] _ring;
    private int _writePos;
    private int _readPos;
    private int _count;
    private readonly object _lock = new();
    private readonly int _frameBytes;
    private readonly int _targetBytes;
    private readonly int _maxBytes;
    private byte[] _lastFrame = Array.Empty<byte>();
    private bool _started;

    public WaveFormat WaveFormat => _format;

    public int BufferedBytes { get { lock (_lock) return _count; } }

    public AdaptiveJitterBuffer() : this(new WaveFormat(48000, 16, 1), 80, 500) { }

    public AdaptiveJitterBuffer(WaveFormat format, int targetMs = 80, int maxMs = 500)
    {
        _format = format;
        _frameBytes = format.SampleRate * format.BitsPerSample / 8 / 50; // 20ms frame
        _targetBytes = format.SampleRate * format.BitsPerSample / 8 * targetMs / 1000;
        _maxBytes = format.SampleRate * format.BitsPerSample / 8 * maxMs / 1000;
        _ring = new byte[_maxBytes + _frameBytes * 4];
    }

    public void AddSamples(byte[] data, int offset, int count)
    {
        lock (_lock)
        {
            _lastFrame = new byte[count];
            Array.Copy(data, offset, _lastFrame, 0, count);

            if (!_started && count >= _frameBytes)
                _started = true;

            int freeSpace = _ring.Length - _count;
            if (count > freeSpace)
            {
                int skip = count - freeSpace;
                _readPos = (_readPos + skip) % _ring.Length;
                _count -= skip;
            }

            int space = _ring.Length - _writePos;
            if (count <= space)
            {
                Array.Copy(data, offset, _ring, _writePos, count);
            }
            else
            {
                Array.Copy(data, offset, _ring, _writePos, space);
                Array.Copy(data, offset + space, _ring, 0, count - space);
            }
            _writePos = (_writePos + count) % _ring.Length;
            _count += count;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (!_started)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            int available = _count;

            if (available == 0 && _lastFrame.Length > 0)
            {
                int toRepeat = Math.Min(count, _lastFrame.Length);
                Array.Copy(_lastFrame, 0, buffer, offset, toRepeat);
                if (toRepeat < count)
                    Array.Clear(buffer, offset + toRepeat, count - toRepeat);
                return count;
            }

            if (available < count && _lastFrame.Length > 0)
            {
                int space = _ring.Length - _readPos;
                if (available <= space)
                {
                    Array.Copy(_ring, _readPos, buffer, offset, available);
                }
                else
                {
                    Array.Copy(_ring, _readPos, buffer, offset, space);
                    Array.Copy(_ring, 0, buffer, offset + space, available - space);
                }
                int filled = available;
                _readPos = (_readPos + available) % _ring.Length;
                _count = 0;

                int remaining = count - filled;
                int repeatBytes = Math.Min(remaining, _lastFrame.Length);
                Array.Copy(_lastFrame, 0, buffer, offset + filled, repeatBytes);
                if (filled + repeatBytes < count)
                    Array.Clear(buffer, offset + filled + repeatBytes, count - filled - repeatBytes);
                return count;
            }

            int toRead = Math.Min(count, available);
            int sp = _ring.Length - _readPos;
            if (toRead <= sp)
            {
                Array.Copy(_ring, _readPos, buffer, offset, toRead);
            }
            else
            {
                Array.Copy(_ring, _readPos, buffer, offset, sp);
                Array.Copy(_ring, 0, buffer, offset + sp, toRead - sp);
            }
            _readPos = (_readPos + toRead) % _ring.Length;
            _count -= toRead;

            if (toRead < count)
                Array.Clear(buffer, offset + toRead, count - toRead);

            return count;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _writePos = 0;
            _readPos = 0;
            _count = 0;
            _started = false;
            _lastFrame = Array.Empty<byte>();
        }
    }

    // Stubs for WebRTCVoiceClient compilation (disabled code)
    public int NextExpectedSeq { get; set; }
    public int TargetMs { get; set; } = 80;
    public int BufferedMs => BufferedBytes * 1000 / (_format.SampleRate * _format.BitsPerSample / 8);
    public long LostFrames { get; set; }
    public int PulledFrames { get; set; }
    public bool Pull(float[] frame) => false;
    public bool Pull(short[] frame) => false;
    public void PushDecoded(ushort seq, short[] pcm) { }
}
