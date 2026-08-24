using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoxCore.Client.Dsp;

/// <summary>
/// Silero VAD v5 (MIT): ONNX, окно 512 сэмплов @16kHz, threshold 0.5,
/// hangover 400ms. Вход — кадры 48кГц, ресемплим 3:1 внутри.
/// </summary>
public sealed class SileroVad : IDisposable
{
    private const int Win16k = 512;
    private const int StateSize = 2 * 128;

    private readonly InferenceSession _session;
    private readonly float[] _resampled = new float[960];   // 20ms @16k
    private readonly float[] _winBuf = new float[Win16k];
    private int _winFill;
    private float[] _state = new float[StateSize];
    private float _lastProb;

    public double Threshold { get; set; } = 0.5;
    public int HangoverFrames { get; set; } = 20;            // 400ms при 20ms кадрах
    public double LastProb => _lastProb;

    private int _hangoverLeft;

    public SileroVad(string modelPath)
    {
        var so = new SessionOptions();
        so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, so);
    }

    /// <summary>Кадр 20мс @48кГц. Возвращает true если речь (с hangover).</summary>
    public bool Process(ReadOnlySpan<float> frame48k)
    {
        int n = Decimator48to16.Process(frame48k, _resampled);
        for (int i = 0; i < n; i++)
        {
            _winBuf[_winFill++] = _resampled[i];
            if (_winFill == Win16k)
            {
                _winFill = 0;
                _lastProb = RunInference(_winBuf);
            }
        }

        if (_lastProb >= Threshold)
            _hangoverLeft = HangoverFrames;
        else if (_hangoverLeft > 0)
            _hangoverLeft--;

        return _hangoverLeft > 0 && _lastProb >= Threshold * 0.5;
    }

    /// <summary>Сброс после долгого молчания.</summary>
    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_winBuf);
        _winFill = 0;
        _hangoverLeft = 0;
        _lastProb = 0;
    }

    private float RunInference(float[] window)
    {
        var inputs = new List<NamedOnnxValue>(3);
        foreach (var meta in _session.InputMetadata)
        {
            switch (meta.Key)
            {
                case "input":
                    var t = new DenseTensor<float>(new[] { 1, window.Length });
                    for (int i = 0; i < window.Length; i++) t[0, i] = window[i];
                    inputs.Add(NamedOnnxValue.CreateFromTensor("input", t));
                    break;
                case "state":
                    var st = new DenseTensor<float>(new[] { 2, 1, 128 });
                    for (int i = 0; i < StateSize; i++) st[i / 128, 0, i % 128] = _state[i];
                    inputs.Add(NamedOnnxValue.CreateFromTensor("state", st));
                    break;
                case "sr":
                    inputs.Add(NamedOnnxValue.CreateFromTensor("sr",
                        new DenseTensor<long>(new long[] { 16000 }, new[] { 1 })));
                    break;
            }
        }

        using var results = _session.Run(inputs);
        float prob = 0f;
        foreach (var meta in _session.OutputMetadata)
        {
            if (meta.Key == "output")
            {
                var outVal = results.First(r => r.Name == "output");
                if (outVal.Value is DenseTensor<float> dt) prob = dt[0, 0];
            }
            else if (meta.Key == "stateN")
            {
                var outVal = results.First(r => r.Name == "stateN");
                if (outVal.Value is DenseTensor<float> dt)
                    for (int i = 0; i < StateSize; i++) _state[i] = dt[i / 128, 0, i % 128];
            }
        }
        return prob;
    }

    public void Dispose() => _session.Dispose();
}
