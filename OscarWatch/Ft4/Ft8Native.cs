using System.Runtime.InteropServices;
using System.Text;

namespace OscarWatch.Ft4;

/// <summary>P/Invoke surface for the native oscarwatch_ft8 library (MIT ft8_lib).</summary>
internal static class Ft8Native
{
    private const string LibraryName = "oscarwatch_ft8";

    /// <summary>
    /// Serialises native calls that share the callsign hashtable until a thread-safe
    /// oscarwatch_ft8 build is loaded. Parallel TX echo still overlaps alignment work.
    /// </summary>
    private static readonly object NativeGate = new();

    static Ft8Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Ft8Native).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(LibraryName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, GetFileName()),
            Path.Combine(baseDir, "runtimes", GetRid(), "native", GetFileName()),
            Path.Combine(baseDir, "native", GetFileName()),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }

        return NativeLibrary.TryLoad(GetFileName(), assembly, searchPath, out var fallback)
            ? fallback
            : IntPtr.Zero;
    }

    private static string GetFileName()
    {
        if (OperatingSystem.IsWindows())
            return "oscarwatch_ft8.dll";
        if (OperatingSystem.IsMacOS())
            return "oscarwatch_ft8.dylib";
        return "oscarwatch_ft8.so";
    }

    private static string GetRid()
    {
        if (OperatingSystem.IsWindows())
            return Environment.Is64BitProcess ? "win-x64" : "win-x86";
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
    }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                lock (NativeGate)
                    ow_ft8_clear_callsigns();
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct Decode
    {
        public float freq_hz;
        public float time_sec;
        public float snr;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 48)]
        public string text;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ow_ft8_encode_pcm(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string messageText,
        float freqHz,
        int isFt4,
        float[] outSamples,
        int outCapacity,
        int sampleRate,
        out int outCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ow_ft8_decode_pcm(
        float[] samples,
        int numSamples,
        int sampleRate,
        int isFt4,
        [Out] Decode[] outDecodes,
        int outCapacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ow_ft8_remember_callsign(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string callsign);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ow_ft8_clear_callsigns();

    public static void RememberCallsign(string callsign)
    {
        lock (NativeGate)
            ow_ft8_remember_callsign(callsign);
    }

    public static void ClearCallsigns()
    {
        lock (NativeGate)
            ow_ft8_clear_callsigns();
    }

    public static float[]? EncodeFt4(string message, float freqHz, int sampleRate = 12000)
    {
        if (!TryEncodeFt4(message, freqHz, sampleRate, out var pcm, out _))
            return null;
        return pcm;
    }

    /// <summary>Encode FT4 PCM. On failure, <paramref name="error"/> explains why.</summary>
    public static bool TryEncodeFt4(
        string message,
        float freqHz,
        int sampleRate,
        out float[]? pcm,
        out string error)
    {
        pcm = null;
        error = "";
        // Portable/hashed calls are valid FT4 (e.g. MM9SQL/M). Force ASCII '/' for pack77.
        var text = NormalizeMessageForPack(message);
        if (text.Length == 0)
        {
            error = "TX message is empty.";
            return false;
        }

        // Plenty of headroom beyond the 7.5 s slot (lead-in + burst + tail).
        var capacity = Math.Max(sampleRate * 8 + 256, 16);
        var buffer = new float[capacity];
        int rc;
        int count;
        try
        {
            lock (NativeGate)
            {
                // Hashed / portable callsigns (e.g. MM9SQL/M) need to be in the table first.
                RememberHashedTokens(text);
                rc = ow_ft8_encode_pcm(text, freqHz, isFt4: 1, buffer, capacity, sampleRate, out count);
            }
        }
        catch (DllNotFoundException)
        {
            error = "Native FT4 library not found.";
            return false;
        }
        catch (Exception ex)
        {
            error = "Encode exception: " + ex.Message;
            return false;
        }

        if (rc != 0 || count <= 0)
        {
            error = rc switch
            {
                -2 => $"Could not pack \"{text}\" as FT4 (check callsign/grid format).",
                -4 => "Encode buffer too small.",
                _ => $"Encode failed (code {rc}) for \"{text}\"."
            };
            return false;
        }

        if (count == capacity)
        {
            pcm = buffer;
        }
        else
        {
            pcm = new float[count];
            Array.Copy(buffer, pcm, count);
        }

        return true;
    }

    private static string NormalizeMessageForPack(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        return message.Trim()
            .Replace('\u2215', '/')
            .Replace('\u2044', '/')
            .Replace('\\', '/')
            .ToUpperInvariant();
    }

    private static void RememberHashedTokens(string message)
    {
        foreach (var part in message.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // pack77 hashes any token containing '/', including portable suffixes like /M /P /A.
            if (part.Contains('/', StringComparison.Ordinal) && !part.Equals("CQ", StringComparison.OrdinalIgnoreCase))
                ow_ft8_remember_callsign(part);
        }
    }

    public static Decode[] DecodeFt4(float[] samples, int sampleRate = 12000)
    {
        var output = new Decode[50];
        int n;
        lock (NativeGate)
            n = ow_ft8_decode_pcm(samples, samples.Length, sampleRate, isFt4: 1, output, output.Length);
        if (n <= 0)
            return [];
        var result = new Decode[n];
        Array.Copy(output, result, n);
        return result;
    }
}
