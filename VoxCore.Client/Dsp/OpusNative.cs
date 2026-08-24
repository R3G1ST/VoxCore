// OpusNative.cs — P/Invoke для libopus 1.5.2 + DRED (win-x64)
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VoxCore.Client.Dsp;

internal static class OpusNative
{
    private const string DllName = "opus";
    // Загрузка из %LOCALAPPDATA%\VoxCore\native как deep_filter (не рядом с exe — ломает WinUI)
    static OpusNative()
    {
        string nativeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoxCore", "native");
        string dllPath = Path.Combine(nativeDir, "opus.dll");
        if (File.Exists(dllPath))
        {
            NativeLibrary.Load(dllPath);
        }
        // fallback: C:\Temp\opus\opus.dll для теста
        else if (File.Exists(@"C:\Temp\opus\opus.dll"))
        {
            NativeLibrary.Load(@"C:\Temp\opus\opus.dll");
        }
    }

    public const int OPUS_APPLICATION_VOIP = 2048;
    public const int OPUS_APPLICATION_AUDIO = 2049;
    public const int OPUS_SET_BITRATE_REQUEST = 4002;
    public const int OPUS_SET_INBAND_FEC_REQUEST = 4012;
    public const int OPUS_SET_PACKET_LOSS_PERC_REQUEST = 4014;
    // DRED: реальный номер 4050 (в задании указано 4036 — это LSB_DEPTH, опечатка)
    public const int OPUS_SET_DRED_DURATION_REQUEST = 4050;
    public const int OPUS_GET_DRED_DURATION_REQUEST = 4051;
    public const int OPUS_SET_LSB_DEPTH_REQUEST = 4036;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_encoder_create(int Fs, int channels, int application, out int error);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encoder_ctl(IntPtr st, int request, int value);
    [DllImport(DllName, EntryPoint = "opus_encoder_ctl", CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encoder_ctl_get(IntPtr st, int request, out int value);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_encoder_destroy(IntPtr st);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_decoder_create(int Fs, int channels, out int error);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void opus_decoder_destroy(IntPtr st);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_encode(IntPtr st, short[] pcm, int frame_size, byte[] data, int max_data_bytes);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_decode(IntPtr st, byte[] data, int len, short[] pcm, int frame_size, int decode_fec);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr opus_get_version_string();
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int opus_packet_has_lbrr(byte[] packet, int len);

    public static string GetVersion() => Marshal.PtrToStringAnsi(opus_get_version_string()) ?? "unknown";

    // Пример: создать энкодер VoIP 48kHz mono с DRED 40ms (4 *10ms), FEC и PLC
    public static IntPtr CreateEncoderDred(int dredDuration = 4)
    {
        var enc = opus_encoder_create(48000, 1, OPUS_APPLICATION_VOIP, out int err);
        if (err != 0 || enc == IntPtr.Zero) throw new Exception($"opus_encoder_create failed {err}");
        opus_encoder_ctl(enc, OPUS_SET_BITRATE_REQUEST, 32000);
        opus_encoder_ctl(enc, OPUS_SET_INBAND_FEC_REQUEST, 1);
        opus_encoder_ctl(enc, OPUS_SET_PACKET_LOSS_PERC_REQUEST, 20);
        int ret = opus_encoder_ctl(enc, OPUS_SET_DRED_DURATION_REQUEST, dredDuration);
        if (ret != 0) throw new Exception($"DRED not supported, ret={ret} (OPUS_UNIMPLEMENTED=-5 если собрано без ENABLE_DRED)");
        return enc;
    }
}
