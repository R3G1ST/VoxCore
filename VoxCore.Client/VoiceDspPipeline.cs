using System;
using System.IO;
using System.Runtime.InteropServices;
using VoxCore.Client.Dsp;

namespace VoxCore.Client;

/// <summary>
/// Единый голосовой конвейер для WebRTC и UDP: HPF → DFN3 → AGC2 → VAD → Gate.
/// AEC3 не включаем (нет рендер-потока в UDP).
/// </summary>
public sealed class VoiceDspPipeline : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _frameSize;
    private readonly bool _noiseSuppression;
    private readonly double _dfAttLim;

    private HpfBiquad? _hpf;
    private DeepFilterNet? _dfn;
    private Agc2Limiter? _agc;
    private SileroVad? _vad;
    private NoiseGate? _gate;

    private double _vadThreshold = 0.3; // ниже = чувствительнее
    private float _preVadGain = 1.0f;   // буст перед VAD

    public bool IsDfnLoaded => _dfn?.IsLoaded ?? false;
    public bool IsVadLoaded => _vad != null;
    public double VadProb => _vad?.LastProb ?? 0;
    public double VadThreshold { get => _vadThreshold; set => _vadThreshold = Math.Clamp(value, 0.05, 0.9); }
    public float PreVadGain { get => _preVadGain; set => _preVadGain = Math.Clamp(value, 0.5f, 10f); }

    public event Action<bool>? VadStateChanged;

    public VoiceDspPipeline(int sampleRate, int frameSize, bool noiseSuppression, double dfAttLim, AppSettings settings)
    {
        _sampleRate = sampleRate;
        _frameSize = frameSize;
        _noiseSuppression = noiseSuppression;
        _dfAttLim = dfAttLim;

        _hpf = new HpfBiquad(80, sampleRate);

        _agc = new Agc2Limiter(-18, 18, sampleRate, 5);
        _gate = new NoiseGate(-40, sampleRate, frameSize);

        // DeepFilterNet3
        if (_noiseSuppression)
        {
            var dfLocal = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VoxCore", "native", "deep_filter_ladspa.dll");
            var dfApp = Path.Combine(AppContext.BaseDirectory, "native", "deep_filter_ladspa.dll");
            var dfPath = File.Exists(dfLocal) ? dfLocal : dfApp;
            if (File.Exists(dfPath))
            {
                _dfn = new DeepFilterNet(dfPath, sampleRate, dfAttLim);
                try { _dfn.Warmup(); } catch { }
            }
        }

        // Silero VAD
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
        }

        // Settings sync
        if (settings != null)
        {
            AgcEnabled = settings.AgcEnabled;
            NoiseSuppression = settings.NoiseSuppression;
            VadThreshold = settings.VadThreshold;
            PreVadGain = settings.PreVadGain;
        }
    }

    public bool AgcEnabled
    {
        get => _agc != null;
        set { if (value && _agc == null) _agc = new Agc2Limiter(-18, 18, _sampleRate, 5); else if (!value) _agc = null; }
    }

    public bool NoiseSuppression
    {
        get => _dfn != null && _dfn.IsLoaded;
        set
        {
            if (value && _dfn == null)
            {
                var dfLocal = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VoxCore", "native", "deep_filter_ladspa.dll");
                var dfApp = Path.Combine(AppContext.BaseDirectory, "native", "deep_filter_ladspa.dll");
                var dfPath = File.Exists(dfLocal) ? dfLocal : dfApp;
                if (File.Exists(dfPath))
                {
                    _dfn = new DeepFilterNet(dfPath, _sampleRate, _dfAttLim);
                    try { _dfn.Warmup(); } catch { }
                }
            }
            else if (!value && _dfn != null)
            {
                _dfn.Dispose();
                _dfn = null;
            }
        }
    }

    /// <summary>
    /// Обработка одного кадра 20мс. Возвращает true если речь (VAD открыт).
    /// Вход/выход: float[] длина frameSize (-1..1).
    /// </summary>
    public bool Process(Span<float> frame)
    {
        // 1) HPF
        _hpf?.Process(frame);

        // 2) DeepFilterNet3
        if (_dfn != null && _dfn.IsLoaded)
        {
            var denoised = new float[_frameSize];
            _dfn.Process(frame, denoised, _frameSize);
            denoised.CopyTo(frame);
        }

        // 3) AGC2
        _agc?.Process(frame);

        // 4) Pre-VAD gain boost (помогает тихим микрофонам)
        if (_preVadGain != 1.0f)
        {
            for (int i = 0; i < _frameSize; i++)
                frame[i] *= _preVadGain;
        }

        // 5) VAD
        bool vadActiveRaw = false;
        try 
        { 
            var _ = _vad?.Process(frame) ?? true;
            var prob = _vad?.LastProb ?? 1.0;
            vadActiveRaw = prob >= _vadThreshold;
        } 
        catch { vadActiveRaw = true; }

        // 6) Gate с VAD-оверрайдом
        if (vadActiveRaw) _gate?.ForceOpen();
        _gate?.Process(frame);

        if (VadStateChanged != null)
        {
            // Fire event on state change
        }
        return vadActiveRaw;
    }

    public void ResetVad() => _vad?.Reset();

    public void Dispose()
    {
        _dfn?.Dispose();
        _vad?.Dispose();
        _dfn = null;
        _vad = null;
    }
}