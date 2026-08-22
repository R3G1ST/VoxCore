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

        unsafe
        {
            _instantiateFn = (delegate*<nint, nint, nint>)Marshal.ReadIntPtr(descPtr, IntPtr.Size * 10);
            _connectPortFn = (delegate*<nint, uint, float*, void>)Marshal.ReadIntPtr(descPtr, IntPtr.Size * 11);
            _activateFn = (delegate*<nint, void>)Marshal.ReadIntPtr(descPtr, IntPtr.Size * 12);
            _runFn = (delegate*<nint, uint, void>)Marshal.ReadIntPtr(descPtr, IntPtr.Size * 13);
            _cleanupFn = (delegate*<nint, void>)Marshal.ReadIntPtr(descPtr, IntPtr.Size * 15);

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

        Span<float> inBuf = new float[sampleCount];
        input[..sampleCount].CopyTo(inBuf);
        Span<float> outBuf = new float[sampleCount];

        fixed (float* pIn = inBuf, pOut = outBuf, pCtrl = _ctrlPorts)
        {
            _connectPortFn(_handle, 0, pIn);
            _connectPortFn(_handle, 1, pOut);
            _connectPortFn(_handle, 2, pCtrl);
            _connectPortFn(_handle, 3, pCtrl + 1);
            _connectPortFn(_handle, 4, pCtrl + 2);
            _connectPortFn(_handle, 5, pCtrl + 3);
            _connectPortFn(_handle, 6, pCtrl + 4);
            _connectPortFn(_handle, 7, pCtrl + 5);

            _runFn(_handle, (uint)sampleCount);
        }

        outBuf.CopyTo(output);
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
