using System;
using System.IO;
using System.Runtime.InteropServices;
using VoxCore.Client.Dsp;

namespace VoxCore.Client;

/// <summary>
/// UDP-only DSP pipeline: HPF → DFN3 → AGC2 → Gate.
/// Energy-based VAD only (no Silero — broken ONNX).
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
    private NoiseGate? _gate;

    private double _vadThreshold = 0.1; // lower = more sensitive
    private float _preVadGain = 3.0f;   // boost before VAD

    public bool IsDfnLoaded => _dfn?.IsLoaded ?? false;
    public bool IsVadLoaded => false; // no Silero
    public double VadProb { get; private set; }
    public double VadThreshold { get => _vadThreshold; set => _vadThreshold = Math.Clamp(value, 0.05, 0.9); }
    public float PreVadGain { get => _preVadGain; set => _preVadGain = Math.Clamp(value, 0.5f, 10f); }

    public void SetAttenuationLimit(double attLimDb) => _dfn?.SetAttenuationLimit(attLimDb);

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

        // DeepFilterNet3 (neural denoiser)
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
    /// Process one 20ms frame. Returns true if speech (energy VAD active).
    /// In/Out: float[] length frameSize (-1..1).
    /// </summary>
    public bool Process(Span<float> frame)
    {
        // 1) HPF
        _hpf?.Process(frame);

        // 2) Pre-VAD gain boost
        if (_preVadGain != 1.0f)
        {
            for (int i = 0; i < _frameSize; i++)
                frame[i] *= _preVadGain;
        }

        // 3) Energy-based VAD (reliable, no ONNX dependency)
        double sum = 0;
        for (int i = 0; i < _frameSize; i++) sum += frame[i] * frame[i];
        double rms = Math.Sqrt(sum / _frameSize);
        bool vadActive = rms > _vadThreshold;
        VadProb = Math.Clamp(rms * 10, 0, 1); // normalized for display

        // 4) DeepFilterNet3 (after VAD)
        if (_dfn != null && _dfn.IsLoaded)
        {
            var denoised = new float[_frameSize];
            _dfn.Process(frame, denoised, _frameSize);
            denoised.CopyTo(frame);
        }

        // 5) AGC2
        _agc?.Process(frame);

        // 6) Gate with VAD override
        if (vadActive) _gate?.ForceOpen();
        _gate?.Process(frame);

        return vadActive;
    }

    public void ResetVad() { /* no-op: energy VAD needs no reset */ }

    public void Dispose()
    {
        _dfn?.Dispose();
        _dfn = null;
    }
}
