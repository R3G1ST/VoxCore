using System;

namespace VoxCore.Client.Dsp;

/// <summary>RBJ highpass biquad, 80 Hz @ 48 kHz, Q=0.707. Резает гул/DC вместо DcAlpha-фильтра.</summary>
public sealed class HpfBiquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public HpfBiquad(double freq = 80.0, double sampleRate = 48000.0, double q = 0.7071)
    {
        double w0 = 2.0 * Math.PI * freq / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2.0 * q);
        double a0 = 1.0 + alpha;
        _b0 = ((1.0 + cos) / 2.0) / a0;
        _b1 = (-(1.0 + cos)) / a0;
        _b2 = ((1.0 + cos) / 2.0) / a0;
        _a1 = (-2.0 * cos) / a0;
        _a2 = (1.0 - alpha) / a0;
    }

    public void Process(Span<float> buf)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            double x = buf[i];
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            buf[i] = (float)y;
        }
    }
}

/// <summary>Noise Gate −40dBFS в стиле SpeexDSP: гистерезис open/close + hold 150ms + плавные края 5ms.</summary>
public sealed class NoiseGate
{
    private readonly float _openRms;
    private readonly float _closeRms;
    private readonly int _holdFrames;
    private readonly int _rampFrames;

    private bool _open;
    private int _holdLeft;
    private float _gain;
    private int _rampStep;

    public bool IsOpen => _open;

    /// <param name="thresholdDb">Порог открытия, dBFS (−40 по умолчанию)</param>
    public NoiseGate(double thresholdDb = -40.0, int sampleRate = 48000, int frameSamples = 960)
    {
        double frameMs = frameSamples * 1000.0 / sampleRate;
        _openRms = (float)Math.Pow(10, thresholdDb / 20.0);
        _closeRms = _openRms * 0.7f;                       // гистерезис ~3dB
        _holdFrames = Math.Max(1, (int)(150 / frameMs));    // hold 150ms
        _rampFrames = Math.Max(1, (int)(5 / frameMs));      // края 5ms
    }

    /// <summary>Возвращает true если кадр пропускается (не заглушен).</summary>
    public void Process(Span<float> frame)
    {
        double sum = 0;
        for (int i = 0; i < frame.Length; i++) sum += frame[i] * frame[i];
        float rms = (float)Math.Sqrt(sum / Math.Max(1, frame.Length));

        if (!_open && rms >= _openRms) { _open = true; _holdLeft = _holdFrames; }
        else if (_open)
        {
            if (rms < _closeRms)
            {
                if (--_holdLeft <= 0) { _open = false; _rampStep = 0; }
            }
            else _holdLeft = _holdFrames;
        }

        float target = _open ? 1f : 0f;
        if (_gain != target)
        {
            _rampStep++;
            float t = Math.Min(1f, (float)_rampStep / _rampFrames);
            _gain = _open ? t : 1f - t;
        }

        if (_gain < 0.999f)
            for (int i = 0; i < frame.Length; i++) frame[i] *= _gain;
    }
}

/// <summary>
/// AGC2 (WebRTC-style) на C#: речевой уровень к −18dBFS, maxGain +18dB,
/// лимитер −1dBFS с lookahead 5ms. Заменяет кастомный _agcGain/_rmsHist/SoftClip.
/// </summary>
public sealed class Agc2Limiter
{
    private readonly float _targetRms;    // −18dBFS
    private readonly float _maxGain;      // +18dB
    private readonly float _minGain;      // −12dB (приглушить крикунов)
    private readonly int _lookahead;      // 5ms
    private readonly float _limCeil;      // −1dBFS

    private readonly float[] _delay;
    private int _delayPos;
    private float _voiceGain = 1f;
    private float _limGain = 1f;
    private float _env;

    public float CurrentGain => _voiceGain;

    public Agc2Limiter(double targetDbfs = -18.0, double maxGainDb = 18.0,
                       double sampleRate = 48000, double lookaheadMs = 5.0)
    {
        _targetRms = (float)Math.Pow(10, targetDbfs / 20.0);
        _maxGain = (float)Math.Pow(10, maxGainDb / 20.0);
        _minGain = 0.25f;
        _lookahead = Math.Max(1, (int)(sampleRate * lookaheadMs / 1000.0));
        _limCeil = (float)Math.Pow(10, -1.0 / 20.0);
        _delay = new float[_lookahead];
    }

    public void Process(Span<float> frame)
    {
        // 1) Оценка уровня кадра
        double sum = 0;
        for (int i = 0; i < frame.Length; i++) sum += frame[i] * frame[i];
        float rms = (float)Math.Sqrt(sum / Math.Max(1, frame.Length));

        // 2) Целевой гейн AGC
        float desired = rms > 1e-4f ? _targetRms / rms : 1f;
        desired = Math.Clamp(desired, _minGain, _maxGain);

        // Анти-насос: быстрое снижение, медленный набор
        float coef = desired < _voiceGain ? 0.35f : 0.04f;
        _voiceGain += (desired - _voiceGain) * coef;

        // 3) Линейный прогон: delay → limiter → voiceGain
        float limCoefAttack = (float)Math.Pow(0.001, 1.0 / (frame.Length * 0.5));  // атака ~полкадра
        float limCoefRelease = (float)Math.Pow(0.001, 1.0 / (frame.Length * 25));  // релиз ~25 кадров
        for (int i = 0; i < frame.Length; i++)
        {
            float delayed = _delay[_delayPos];
            _delay[_delayPos] = frame[i];
            _delayPos = (_delayPos + 1) % _lookahead;

            // Пиковая огибающая задержанного сигнала
            float a = MathF.Abs(delayed);
            _env = a > _env ? a : _env * 0.9995f;

            float limTarget = _env > _limCeil ? _limCeil / _env : 1f;
            float c = limTarget < _limGain ? limCoefAttack : limCoefRelease;
            _limGain += (limTarget - _limGain) * c;

            frame[i] = delayed * Math.Min(_voiceGain, _limGain);
        }
    }
}

/// <summary>Мастер EQ 3 полосы: low shelf 200Hz, peak 1kHz, high shelf 4kHz. Гейны −12..+12 dB.</summary>
public sealed class Equalizer3Band
{
    private Biquad _low = new();
    private Biquad _mid = new();
    private Biquad _high = new();
    private double _lowDb, _midDb, _highDb;

    public double LowDb  { get => _lowDb;  set { _lowDb = value;  _low  = Biquad.LowShelf(200, 48000, value); } }
    public double MidDb  { get => _midDb;  set { _midDb = value;  _mid  = Biquad.Peaking(1000, 48000, value, 1.0); } }
    public double HighDb { get => _highDb; set { _highDb = value; _high = Biquad.HighShelf(4000, 48000, value); } }

    public void Process(Span<float> buf)
    {
        if (_lowDb == 0 && _midDb == 0 && _highDb == 0) return;
        _low.Process(buf);
        _mid.Process(buf);
        _high.Process(buf);
    }

    private sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public static Biquad LowShelf(double f, double fs, double gainDb, double s = 0.7071)
        {
            double A = Math.Pow(10, gainDb / 40);
            double w0 = 2 * Math.PI * f / fs, c = Math.Cos(w0), sn = Math.Sin(w0);
            double alpha = sn / 2 * Math.Sqrt((A + 1 / A) * (1 / s - 1) + 2);
            double two = 2 * Math.Sqrt(A) * alpha;
            return From((A * ((A + 1) - (A - 1) * c + two)), (2 * A * ((A - 1) - (A + 1) * c)),
                        (A * ((A + 1) - (A - 1) * c - two)), ((A + 1) + (A - 1) * c + two),
                        (-2 * ((A - 1) + (A + 1) * c)), ((A + 1) + (A - 1) * c - two));
        }

        public static Biquad HighShelf(double f, double fs, double gainDb, double s = 0.7071)
        {
            double A = Math.Pow(10, gainDb / 40);
            double w0 = 2 * Math.PI * f / fs, c = Math.Cos(w0), sn = Math.Sin(w0);
            double alpha = sn / 2 * Math.Sqrt((A + 1 / A) * (1 / s - 1) + 2);
            double two = 2 * Math.Sqrt(A) * alpha;
            return From((A * ((A + 1) + (A - 1) * c + two)), (-2 * A * ((A - 1) + (A + 1) * c)),
                        (A * ((A + 1) + (A - 1) * c - two)), ((A + 1) - (A - 1) * c + two),
                        (2 * ((A - 1) - (A + 1) * c)), ((A + 1) - (A - 1) * c - two));
        }

        public static Biquad Peaking(double f, double fs, double gainDb, double q)
        {
            double A = Math.Pow(10, gainDb / 40);
            double w0 = 2 * Math.PI * f / fs, c = Math.Cos(w0), sn = Math.Sin(w0);
            double alpha = sn / (2 * q);
            return From((1 + alpha * A), (-2 * c), (1 - alpha * A), (1 + alpha), (-2 * c), (1 - alpha));
        }

        private static Biquad From(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            return new Biquad { _b0 = b0 / a0, _b1 = b1 / a0, _b2 = b2 / a0, _a1 = a1 / a0, _a2 = a2 / a0 };
        }

        public void Process(Span<float> buf)
        {
            for (int i = 0; i < buf.Length; i++)
            {
                double x = buf[i];
                double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
                buf[i] = (float)y;
            }
        }
    }
}

/// <summary>Децимация 48кГц → 16кГц (усреднение по 3) для Silero VAD.</summary>
public static class Decimator48to16
{
    public static int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        int outN = input.Length / 3;
        for (int i = 0; i < outN; i++)
            output[i] = (input[i * 3] + input[i * 3 + 1] + input[i * 3 + 2]) / 3f;
        return outN;
    }
}
