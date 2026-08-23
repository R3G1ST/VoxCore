using System.Runtime.InteropServices;

namespace VoxCore.Client;

public sealed class DeepFilterNet : IDisposable
{
    private nint _libHandle;
    private nint _handle;
    private bool _activated;

    private unsafe delegate*<nint, nint, nint> _instantiateFn;
    private unsafe delegate*<nint, uint, float*, void> _connectPortFn;
    private unsafe delegate*<nint, void> _activateFn;
    private unsafe delegate*<nint, uint, void> _runFn;
    private unsafe delegate*<nint, void> _cleanupFn;

    private readonly float[] _ctrlPorts = new float[6];
    private readonly float[] _inChunk = new float[480];
    private readonly float[] _outChunk = new float[480];
    private float[] _carry = [];

    public int HopSize { get; } = 480;
    public bool IsLoaded => _handle != 0;

    public DeepFilterNet(string dllPath, int sampleRate = 48000)
    {
        _libHandle = NativeLibrary.Load(dllPath);
        if (_libHandle == 0)
            throw new Exception($"Failed to load {dllPath}");

        var entryAddr = NativeLibrary.GetExport(_libHandle, "ladspa_descriptor");
        if (entryAddr == 0)
            throw new Exception("ladspa_descriptor not found");

        nint descPtr;
        unsafe
        {
            descPtr = ((delegate*<nint, nint>)entryAddr)(0);
        }
        if (descPtr == 0) throw new Exception("Mono descriptor (index 0) not found");

        // Раскладка LADSPA_Descriptor у этой сборки содержит доп. поле (Name) после
        // Properties, поэтому функции сдвинуты на +1 слот относительно классики.
        // Авто-детект: ищем instantiate — указатель кода внутри образа модуля.
        unsafe
        {
            const nint ModuleSpan = (nint)96 * 1024 * 1024;
            nint inst = 0, conn = 0, act = 0, runF = 0, cleanup = 0;
            for (int i = 8; i <= 14; i++)
            {
                var cand = Marshal.ReadIntPtr(descPtr, IntPtr.Size * i);
                if (cand >= _libHandle && cand < _libHandle + ModuleSpan)
                {
                    inst = Marshal.ReadIntPtr(descPtr, IntPtr.Size * i);
                    conn = Marshal.ReadIntPtr(descPtr, IntPtr.Size * (i + 1));
                    act = Marshal.ReadIntPtr(descPtr, IntPtr.Size * (i + 2));
                    runF = Marshal.ReadIntPtr(descPtr, IntPtr.Size * (i + 3));
                    for (int k = i + 7; k >= i + 4; k--)
                    {
                        var p = Marshal.ReadIntPtr(descPtr, IntPtr.Size * k);
                        if (p != 0) { cleanup = p; break; }
                    }
                    break;
                }
            }
            if (inst == 0)
                throw new Exception("LADSPA descriptor: instantiate not found (unknown layout)");

            _instantiateFn = (delegate*<nint, nint, nint>)inst;
            _connectPortFn = (delegate*<nint, uint, float*, void>)conn;
            _activateFn = (delegate*<nint, void>)act;
            _runFn = (delegate*<nint, uint, void>)runF;
            _cleanupFn = (delegate*<nint, void>)cleanup;

            _handle = _instantiateFn(descPtr, (nint)sampleRate);
        }
        if (_handle == 0) throw new Exception("DF3 instantiate failed");

        _ctrlPorts[0] = 50f;
        _ctrlPorts[1] = -10f;
        _ctrlPorts[2] = 30f;
        _ctrlPorts[3] = 20f;
        _ctrlPorts[4] = 0f;
        _ctrlPorts[5] = 0f;

        Console.WriteLine("[DF3] Loaded OK");
    }

    public void Activate()
    {
        if (_handle != 0 && !_activated)
        {
            unsafe { _activateFn(_handle); }
            _activated = true;
        }
    }

    public unsafe void Process(ReadOnlySpan<float> input, Span<float> output, int sampleCount)
    {
        if (_handle == 0) { input[..sampleCount].CopyTo(output); return; }

        Activate();

        // Плагин принимает строго hop 480 (10мс @48кГц). Вызов run с другим
        // размером уводит его в медленный путь (RTF ~3) и выдаёт мусор.
        const int hop = 480;

        // Склейка: остаток прошлого вызова + новые сэмплы
        float[] work = new float[_carry.Length + sampleCount];
        _carry.CopyTo(work, 0);
        input[..sampleCount].CopyTo(work.AsSpan(_carry.Length));
        int workLen = work.Length;

        int off = 0;
        fixed (float* pCtrl = _ctrlPorts)
        {
            while (off + hop <= workLen)
            {
                work.AsSpan(off, hop).CopyTo(_inChunk);
                fixed (float* pIn = _inChunk, pOut = _outChunk)
                {
                    _connectPortFn(_handle, 0, pIn);
                    _connectPortFn(_handle, 1, pOut);
                    _connectPortFn(_handle, 2, pCtrl);
                    _connectPortFn(_handle, 3, pCtrl + 1);
                    _connectPortFn(_handle, 4, pCtrl + 2);
                    _connectPortFn(_handle, 5, pCtrl + 3);
                    _connectPortFn(_handle, 6, pCtrl + 4);
                    _connectPortFn(_handle, 7, pCtrl + 5);
                    _runFn(_handle, (uint)hop);
                }
                _outChunk.CopyTo(work.AsSpan(off, hop));
                off += hop;
            }
        }

        // Отдаём первые sampleCount обработанных сэмплов
        int outLen = Math.Min(sampleCount, off);
        work.AsSpan(0, outLen).CopyTo(output[..outLen]);
        if (outLen < sampleCount)
            output[outLen..sampleCount].Clear();

        // Хвост (неполный chunk) — на следующий вызов
        int tail = workLen - off;
        if (tail > 0)
        {
            _carry = new float[tail];
            work.AsSpan(off, tail).CopyTo(_carry);
        }
        else if (_carry.Length > 0)
        {
            _carry = [];
        }
    }

    public void Process(ReadOnlySpan<short> input, Span<short> output, int sampleCount)
    {
        if (_handle == 0) { input[..sampleCount].CopyTo(output); return; }

        Span<float> fIn = new float[sampleCount];
        Span<float> fOut = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
            fIn[i] = input[i] / 32768f;

        Process(fIn, fOut, sampleCount);

        for (int i = 0; i < sampleCount; i++)
            output[i] = (short)Math.Clamp((int)(fOut[i] * 32768f), short.MinValue, short.MaxValue);
    }

    public unsafe void Cleanup()
    {
        if (_handle != 0)
        {
            if (_activated && _cleanupFn != null)
                _cleanupFn(_handle);
            _handle = 0;
            _activated = false;
        }
    }

    public void Dispose()
    {
        Cleanup();
        if (_libHandle != 0)
        {
            NativeLibrary.Free(_libHandle);
            _libHandle = 0;
        }
    }
}
