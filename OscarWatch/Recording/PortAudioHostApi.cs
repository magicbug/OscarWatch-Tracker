using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OscarWatch.Recording;

/// <summary>
/// Reads PortAudio host-API type ids. PortAudioSharp does not expose <c>Pa_GetHostApiInfo</c>.
/// </summary>
internal static class PortAudioHostApi
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetHostApiInfoDelegate(int hostApi);

    private static readonly Dictionary<int, int> TypeByIndex = new();
    private static GetHostApiInfoDelegate? _getHostApiInfo;

    public static int GetTypeId(int hostApiIndex)
    {
        lock (TypeByIndex)
        {
            if (TypeByIndex.TryGetValue(hostApiIndex, out var cached))
                return cached;

            var type = ReadTypeId(hostApiIndex);
            TypeByIndex[hostApiIndex] = type;
            return type;
        }
    }

    private static int ReadTypeId(int hostApiIndex)
    {
        try
        {
            _getHostApiInfo ??= ResolveGetHostApiInfo();
            if (_getHostApiInfo is null)
                return 0;

            var info = _getHostApiInfo(hostApiIndex);
            if (info == IntPtr.Zero)
                return 0;

            // PaHostApiInfo: structVersion at 0, type (PaHostApiTypeId) at 4.
            return Marshal.ReadInt32(info, 4);
        }
        catch
        {
            return 0;
        }
    }

    private static GetHostApiInfoDelegate? ResolveGetHostApiInfo()
    {
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            var name = module.ModuleName;
            if (name is null || name.IndexOf("portaudio", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var export = NativeLibrary.GetExport(module.BaseAddress, "Pa_GetHostApiInfo");
            return Marshal.GetDelegateForFunctionPointer<GetHostApiInfoDelegate>(export);
        }

        return null;
    }
}
