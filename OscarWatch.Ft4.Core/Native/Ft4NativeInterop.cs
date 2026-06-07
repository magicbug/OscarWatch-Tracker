using System.Runtime.InteropServices;

namespace OscarWatch.Ft4.Core.Native;

internal static class Ft4NativeInterop
{
    private const string LibraryName = "ft4_coder";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal delegate void DecodedMessageCallbackDelegate(IntPtr message);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "encode_ft4")]
    internal static extern void EncodeFt4(
        byte[] message,
        ref float txAudioFrequency,
        float[] audioSamples);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "decode_ft4", CharSet = CharSet.Ansi)]
    internal static extern void DecodeFt4(
        float[] audioSamples,
        ref Ft4QsoStage qsoProgress,
        ref int nfqso,
        ref int nfb,
        byte[] myCall,
        byte[] hisCall,
        DecodedMessageCallbackDelegate callback);
}
