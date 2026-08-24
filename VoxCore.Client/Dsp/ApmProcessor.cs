using System.Runtime.InteropServices;

namespace VoxCore.Client.Dsp;

/// <summary>
/// Минимальная P/Invoke обвязка над webrtc-apm.dll (SoundFlow 1.4.0, Win-x64 4.07 MB).
/// Источник: https://www.nuget.org/packages/SoundFlow.Extensions.WebRtc.Apm/1.4.0
/// Бинарник: runtimes/win-x64/native/webrtc-apm.dll (C-wrapper над webrtc-audio-processing)
/// Экспорты: webrtc_apm_create/destroy/config_*/process_stream/process_reverse_stream/...
/// License: MIT (SoundFlow) + BSD (WebRTC APM)
/// </summary>
internal static class ApmNative
{
    // DLL ищется в %LOCALAPPDATA%\VoxCore\native\webrtc-apm.dll или рядом с exe (см. ApmLoader).
    private const string Dll = "webrtc-apm.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr webrtc_apm_config_create();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void webrtc_apm_config_destroy(IntPtr cfg);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void webrtc_apm_config_set_echo_canceller(IntPtr cfg, [MarshalAs(UnmanagedType.I1)] bool enabled, [MarshalAs(UnmanagedType.I1)] bool mobileMode);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void webrtc_apm_config_set_noise_suppression(IntPtr cfg, [MarshalAs(UnmanagedType.I1)] bool enabled, int level); // 0=Low 1=Moderate 2=High 3=VeryHigh
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void webrtc_apm_config_set_gain_controller1(IntPtr cfg, [MarshalAs(UnmanagedType.I1)] bool enabled, int mode, int targetDbfs, int compressionGainDb, [MarshalAs(UnmanagedType.I1)] bool limiter);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void webrtc_apm_config_set_gain_controller2(IntPtr cfg, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void webrtc_apm_config_set_high_pass_filter(IntPtr cfg, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void webrtc_apm_config_set_pre_amplifier(IntPtr cfg, [MarshalAs(UnmanagedType.I1)] bool enabled, float factor);
    // Pipeline: только прямые вызовы не используются (дефолт AverageChannels)
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr webrtc_apm_create();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void webrtc_apm_destroy(IntPtr apm);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int webrtc_apm_apply_config(IntPtr apm, IntPtr cfg);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int webrtc_apm_initialize(IntPtr apm);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int webrtc_apm_get_frame_size(int sampleRateHz); // 480 @48k, 160 @16k -> 10ms фрейм!
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr webrtc_apm_stream_config_create(int sampleRateHz, UIntPtr numChannels);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void webrtc_apm_stream_config_destroy(IntPtr cfg);
    // src/dst = float** (массив указателей на каналы, float[frameSize] каждый). Требуется 10ms кадр!
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int webrtc_apm_process_stream(IntPtr apm, IntPtr src, IntPtr inCfg, IntPtr outCfg, IntPtr dst);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int webrtc_apm_process_reverse_stream(IntPtr apm, IntPtr src, IntPtr inCfg, IntPtr outCfg, IntPtr dst);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int webrtc_apm_set_stream_delay_ms(IntPtr apm, int delayMs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int webrtc_apm_stream_delay_ms(IntPtr apm);
}

/// <summary>
/// Загрузчик webrtc-apm.dll из %LOCALAPPDATA%\VoxCore\native\ или из native/ рядом с exe (dev).
/// Вызывать до первого P/Invoke: NativeLibrary.SetDllImportResolver
/// </summary>
internal static class ApmLoader
{
    private static bool _resolved;
    public static void EnsureLoaded()
    {
        if (_resolved) return;
        _resolved = true;
        NativeLibrary.SetDllImportResolver(typeof(ApmLoader).Assembly, (name, asm, path) =>
        {
            if (name != "webrtc-apm.dll") return IntPtr.Zero;
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxCore", "native", "webrtc-apm.dll"),
                Path.Combine(AppContext.BaseDirectory, "native", "webrtc-apm.dll"),
                Path.Combine(AppContext.BaseDirectory, "webrtc-apm.dll"),
                @"C:\Temp\apm\webrtc-apm.dll",
            };
            foreach (var c in candidates)
                if (File.Exists(c) && NativeLibrary.TryLoad(c, out var h))
                    return h;
            return IntPtr.Zero;
        });
    }
}

/// <summary>
/// Thin wrapper: AEC3 + NS(High) + AGC2 + HPF. 48kHz mono, 10ms кадр (480 семплов).
/// ВАЖНО: WebRTC APM требует 10ms кадр (480 @48k), а VoxCore использует 20ms (960). Нужно резать кадр пополам!
/// Reverse stream обязателен для AEC3: на каждый ProcessCapture вызывай ProcessRender с миксом воспроизведения.
/// </summary>
internal sealed class ApmProcessor : IDisposable
{
    private IntPtr _apm, _cfg, _inCfg, _outCfg;
    private IntPtr[] _srcPtrs = null!, _dstPtrs = null!;
    private GCHandle _hSrcArr, _hDstArr;
    private IntPtr _pSrcArr, _pDstArr;
    private GCHandle _hRevSrcArr, _hRevOutArr;
    private IntPtr _pRevSrcArr, _pRevOutArr;
    private IntPtr[] _revSrcPtrs = null!, _revOutPtrs = null!;
    private readonly int _frameSize; // 480 @48k

    public ApmProcessor(bool aec3 = true, bool ns = true, bool agc2 = true, bool hpf = true, int delayMs = 40)
    {
        ApmLoader.EnsureLoaded();
        _frameSize = ApmNative.webrtc_apm_get_frame_size(48000);
        _cfg = ApmNative.webrtc_apm_config_create();
        ApmNative.webrtc_apm_config_set_echo_canceller(_cfg, aec3, false);
        ApmNative.webrtc_apm_config_set_noise_suppression(_cfg, ns, 2); // High
        ApmNative.webrtc_apm_config_set_gain_controller2(_cfg, agc2);
        ApmNative.webrtc_apm_config_set_high_pass_filter(_cfg, hpf);
        _apm = ApmNative.webrtc_apm_create();
        if (_apm == IntPtr.Zero) throw new InvalidOperationException("webrtc_apm_create failed");
        int e1 = ApmNative.webrtc_apm_apply_config(_apm, _cfg);
        if (e1 != 0) throw new InvalidOperationException($"apply_config: {e1}");
        int e2 = ApmNative.webrtc_apm_initialize(_apm);
        if (e2 != 0) throw new InvalidOperationException($"initialize: {e2}");
        ApmNative.webrtc_apm_set_stream_delay_ms(_apm, delayMs);
        _inCfg = ApmNative.webrtc_apm_stream_config_create(48000, (UIntPtr)1);
        _outCfg = ApmNative.webrtc_apm_stream_config_create(48000, (UIntPtr)1);
        // Выделяем массивы указателей (1 канал)
        _srcPtrs = new IntPtr[1]; _dstPtrs = new IntPtr[1];
        _hSrcArr = GCHandle.Alloc(_srcPtrs, GCHandleType.Pinned); _pSrcArr = _hSrcArr.AddrOfPinnedObject();
        _hDstArr = GCHandle.Alloc(_dstPtrs, GCHandleType.Pinned); _pDstArr = _hDstArr.AddrOfPinnedObject();
        _revSrcPtrs = new IntPtr[1]; _revOutPtrs = new IntPtr[1];
        _revOutPtrs[0] = Marshal.AllocHGlobal(_frameSize * 4);
        _hRevSrcArr = GCHandle.Alloc(_revSrcPtrs, GCHandleType.Pinned); _pRevSrcArr = _hRevSrcArr.AddrOfPinnedObject();
        _hRevOutArr = GCHandle.Alloc(_revOutPtrs, GCHandleType.Pinned); _pRevOutArr = _hRevOutArr.AddrOfPinnedObject();
    }

    public void SetDelay(int ms) => ApmNative.webrtc_apm_set_stream_delay_ms(_apm, ms);

    // Far-end (то что играет из динамиков) — вызывать каждый кадр воспроизведения, до ProcessCapture
    public unsafe void ProcessRender(Span<float> farend10ms)
    {
        if (farend10ms.Length != _frameSize) throw new ArgumentException($"need {_frameSize}");
        fixed (float* p = farend10ms) { _revSrcPtrs[0] = (IntPtr)p; ApmNative.webrtc_apm_process_reverse_stream(_apm, _pRevSrcArr, _inCfg, _outCfg, _pRevOutArr); }
    }

    // Near-end (микрофон) — in-place, 10ms кадр
    public unsafe void ProcessCapture(Span<float> frame10ms)
    {
        if (frame10ms.Length != _frameSize) throw new ArgumentException($"need {_frameSize}");
        fixed (float* p = frame10ms)
        {
            _srcPtrs[0] = (IntPtr)p;
            // dst в тот же буфер — выделяем временный dst
            float* tmp = stackalloc float[_frameSize];
            _dstPtrs[0] = (IntPtr)tmp;
            int e = ApmNative.webrtc_apm_process_stream(_apm, _pSrcArr, _inCfg, _outCfg, _pDstArr);
            if (e == 0) new Span<float>(tmp, _frameSize).CopyTo(frame10ms);
        }
    }

    // Хелпер для 20ms кадра VoxCore (960): режет на 2x10ms
    public void ProcessCapture20ms(Span<float> frame20ms)
    {
        if (frame20ms.Length != 960) throw new ArgumentException("need 960");
        ProcessCapture(frame20ms.Slice(0, 480));
        ProcessCapture(frame20ms.Slice(480, 480));
    }
    public void ProcessRender20ms(Span<float> farend20ms)
    {
        if (farend20ms.Length != 960) throw new ArgumentException("need 960");
        ProcessRender(farend20ms.Slice(0, 480));
        ProcessRender(farend20ms.Slice(480, 480));
    }

    public void Dispose()
    {
        if (_hSrcArr.IsAllocated) _hSrcArr.Free();
        if (_hDstArr.IsAllocated) _hDstArr.Free();
        if (_hRevSrcArr.IsAllocated) _hRevSrcArr.Free();
        if (_hRevOutArr.IsAllocated) _hRevOutArr.Free();
        if (_revOutPtrs?[0] != IntPtr.Zero) Marshal.FreeHGlobal(_revOutPtrs[0]);
        if (_inCfg != IntPtr.Zero) ApmNative.webrtc_apm_stream_config_destroy(_inCfg);
        if (_outCfg != IntPtr.Zero) ApmNative.webrtc_apm_stream_config_destroy(_outCfg);
        if (_apm != IntPtr.Zero) ApmNative.webrtc_apm_destroy(_apm);
        if (_cfg != IntPtr.Zero) ApmNative.webrtc_apm_config_destroy(_cfg);
        _apm = _cfg = _inCfg = _outCfg = IntPtr.Zero;
    }
}
