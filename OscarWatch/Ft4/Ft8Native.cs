using System.Runtime.InteropServices;
using System.Text;
using OscarWatch.Core.Ft4;

namespace OscarWatch.Ft4;

/// <summary>P/Invoke surface for the native oscarwatch_ft8 library (MIT ft8_lib).</summary>
internal static class Ft8Native
{
    private const string LibraryName = "oscarwatch_ft8";

    static Ft8Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Ft8Native).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(LibraryName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var path in CandidatePaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }

        return NativeLibrary.TryLoad(GetFileName(), assembly, searchPath, out var fallback)
            ? fallback
            : IntPtr.Zero;
    }

    private static string[] CandidatePaths()
    {
        var baseDir = AppContext.BaseDirectory;
        return
        [
            Path.Combine(baseDir, GetFileName()),
            Path.Combine(baseDir, "runtimes", GetRid(), "native", GetFileName()),
            Path.Combine(baseDir, "native", GetFileName()),
        ];
    }

    /// <summary>
    /// Status text key when the library will not load. On Windows a library that is present
    /// but will not load almost always means the Visual C++ runtime is missing.
    /// </summary>
    public static string UnavailableMessageKey =>
        OperatingSystem.IsWindows() && CandidatePaths().Any(File.Exists)
            ? "Ft4.NativeUnavailableWindows"
            : "Ft4.NativeUnavailable";

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
        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
            return arm64 ? "win-arm64" : "win-x64";
        if (OperatingSystem.IsMacOS())
            return arm64 ? "osx-arm64" : "osx-x64";
        return arm64 ? "linux-arm64" : "linux-x64";
    }

    /// <summary>Why the last <see cref="IsAvailable"/> check failed, for the log.</summary>
    public static string? LoadError { get; private set; }

    public static bool IsAvailable
    {
        get
        {
            try
            {
                // Touch an export without clearing the callsign hashtable.
                ow_ft8_remember_callsign("");
                return true;
            }
            catch (DllNotFoundException ex)
            {
                LoadError = ex.Message;
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                LoadError = ex.Message;
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
        /// <summary>
        /// Non-zero when the text is a hinted reply: <see cref="ApCrcChecked"/> when the
        /// calls were supplied and the rest passed the CRC, <see cref="ApGuessed"/> when it
        /// is only the closest message in the hint list.
        /// </summary>
        public int ap;
        /// <summary>Linear slide (Hz/s) the drift search matched; 0 for a steady signal.</summary>
        public float drift_hz_s;
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
    public static extern int ow_ft8_encode_pcm_ex(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string messageText,
        float freqHz,
        int isFt4,
        float slopeHzPerSec,
        float gain,
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
        float fMinHz,
        float fMaxHz,
        [Out] Decode[] outDecodes,
        int outCapacity,
        int deep);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ow_ft8_decode_pcm_ap(
        float[] samples,
        int numSamples,
        int sampleRate,
        int isFt4,
        float fMinHz,
        float fMaxHz,
        [Out] Decode[] outDecodes,
        int outCapacity,
        int deep,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? hints,
        float hintHz,
        float hintHalfHz);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ow_ft8_decode_pcm_drift(
        float[] samples,
        int numSamples,
        int sampleRate,
        int isFt4,
        float fMinHz,
        float fMaxHz,
        [Out] Decode[] outDecodes,
        int outCapacity,
        int deep,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? hints,
        float hintHz,
        float hintHalfHz,
        float maxResidualHzPerSec,
        int driftSteps,
        float dtMinSec,
        float dtMaxSec);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ow_ft8_remember_callsign(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string callsign);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ow_ft8_clear_callsigns();

    public static void RememberCallsign(string callsign)
    {
        var call = Ft4MessageCodec.NormalizeCall(callsign);
        // A locator such as KP04 packs like a hashed call. It is not one.
        if (call.Length == 0 || (Ft4MessageCodec.IsGrid(call) && !Ft4MessageCodec.IsClosing(call)))
            return;

        ow_ft8_remember_callsign(call);
    }

    public static void ClearCallsigns() =>
        ow_ft8_clear_callsigns();

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
        out string error) =>
        TryEncodeFt4(message, freqHz, sampleRate, 0f, 1f, out pcm, out error);

    /// <summary>
    /// Encode FT4 PCM whose tones move by <paramref name="slopeHzPerSec"/> from the slot start
    /// (TX Doppler pre-compensation), scaled by <paramref name="gain"/>.
    /// </summary>
    public static bool TryEncodeFt4(
        string message,
        float freqHz,
        int sampleRate,
        float slopeHzPerSec,
        float gain,
        out float[]? pcm,
        out string error,
        Ft4ProtocolProfile? profile = null)
    {
        pcm = null;
        error = "";
        var p = profile ?? Ft4ProtocolProfile.Ft4;
        // Portable/hashed calls are valid (e.g. MM9SQL/M). Force ASCII '/' for pack77.
        var text = NormalizeMessageForPack(message);
        if (text.Length == 0)
        {
            error = "TX message is empty.";
            return false;
        }

        // Headroom beyond one slot: lead-in + burst + tail, sized from the protocol's slot length.
        var capacity = p.EncodeCapacity(sampleRate);
        var buffer = new float[capacity];
        int rc;
        int count;
        try
        {
            // Hashed / portable callsigns (e.g. MM9SQL/M) need to be in the table first.
            // Native hashtable locking allows encode/decode to run on parallel TX slots.
            RememberHashedTokens(text);
            rc = ow_ft8_encode_pcm_ex(
                text, freqHz, isFt4: p.IsFt4 ? 1 : 0, slopeHzPerSec, gain, buffer, capacity, sampleRate, out count);
        }
        catch (DllNotFoundException)
        {
            error = "Native digital mode library not found.";
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
                -2 => $"Could not pack \"{text}\" as {p.Mode} (check callsign/grid format).",
                -3 => "Encode ran out of memory.",
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

        // <CALL> is kept: it tells ft8_lib to send that call as a hash.
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
            var call = part.Trim('<', '>');
            if (Ft4MessageCodec.IsHashedCall(call))
                RememberCallsign(call);
        }
    }

    /// <summary>Half-width pad around the TX tone when searching for our own echo (Hz).</summary>
    public const double DefaultSearchHalfWidthHz = 700;

    /// <summary>Decode search covering the whole waterfall, independent of the RX and TX brackets.</summary>
    public static void ResolveWaterfallSearchBand(out float fMinHz, out float fMaxHz)
    {
        fMinHz = (float)Ft4SpectrumAnalyzer.DefaultMinHz;
        fMaxHz = (float)Ft4SpectrumAnalyzer.DefaultMaxHz;
    }

    /// <summary>
    /// Narrow search around the TX tone for own-echo recovery.
    /// </summary>
    public static void ResolveSearchBand(double rxHz, double txHz, out float fMinHz, out float fMaxHz)
    {
        var a = Math.Clamp(rxHz, 200, 3000);
        var b = Math.Clamp(txHz, 200, 3000);
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        fMinHz = (float)Math.Clamp(lo - DefaultSearchHalfWidthHz, 100, 2900);
        fMaxHz = (float)Math.Clamp(hi + DefaultSearchHalfWidthHz, fMinHz + 100, 3000);
    }

    public static Decode[] DecodeFt4(
        float[] samples,
        int sampleRate = 12000,
        double centreHz = 1500,
        double halfWidthHz = DefaultSearchHalfWidthHz)
    {
        var fMin = (float)Math.Clamp(centreHz - halfWidthHz, 100, 2900);
        var fMax = (float)Math.Clamp(centreHz + halfWidthHz, fMin + 100, 3000);
        return DecodeFt4(samples, sampleRate, fMin, fMax, deep: false);
    }

    /// <summary><see cref="Decode.ap"/> for a hint taken on agreement alone, without a CRC.</summary>
    public const int ApGuessed = 1;

    /// <summary><see cref="Decode.ap"/> for a hint whose report, CRC and parity came from the audio.</summary>
    public const int ApCrcChecked = 2;

    /// <summary>Half-width around the station we are working when trying hinted messages (Hz).</summary>
    public const float ApSearchHalfWidthHz = 200;

    public static Decode[] DecodeFt4(float[] samples, int sampleRate, float fMinHz, float fMaxHz) =>
        DecodeFt4(samples, sampleRate, fMinHz, fMaxHz, deep: false);

    public static Decode[] DecodeFt4(
        float[] samples,
        int sampleRate,
        float fMinHz,
        float fMaxHz,
        bool deep) =>
        DecodeFt4(samples, sampleRate, fMinHz, fMaxHz, deep, apHints: null, apCentreHz: 0);

    public static Decode[] DecodeFt4(
        float[] samples,
        int sampleRate,
        float fMinHz,
        float fMaxHz,
        bool deep,
        string? apHints,
        float apCentreHz,
        Ft4ProtocolProfile? profile = null)
    {
        var isFt4 = (profile ?? Ft4ProtocolProfile.Ft4).IsFt4 ? 1 : 0;
        var output = new Decode[50];
        var n = string.IsNullOrEmpty(apHints)
            ? ow_ft8_decode_pcm(
                samples,
                samples.Length,
                sampleRate,
                isFt4,
                fMinHz,
                fMaxHz,
                output,
                output.Length,
                deep ? 1 : 0)
            : ow_ft8_decode_pcm_ap(
                samples,
                samples.Length,
                sampleRate,
                isFt4,
                fMinHz,
                fMaxHz,
                output,
                output.Length,
                deep ? 1 : 0,
                apHints,
                apCentreHz,
                ApSearchHalfWidthHz);
        return Trim(output, n);
    }

    /// <summary>
    /// One decode that also searches signals still sliding by up to
    /// <paramref name="maxResidualHzPerSec"/> either way, in
    /// <paramref name="driftSteps"/> steps each side. A drifting station is reported
    /// at its mid-burst frequency.
    /// </summary>
    public static Decode[] DecodeFt4Drift(
        float[] samples,
        int sampleRate,
        float fMinHz,
        float fMaxHz,
        bool deep,
        string? apHints,
        float apCentreHz,
        float maxResidualHzPerSec,
        int driftSteps,
        Ft4ProtocolProfile? profile = null)
    {
        if (driftSteps <= 0 || !(maxResidualHzPerSec > 0))
            return DecodeFt4(samples, sampleRate, fMinHz, fMaxHz, deep, apHints, apCentreHz, profile);

        var output = new Decode[50];
        var n = ow_ft8_decode_pcm_drift(
            samples,
            samples.Length,
            sampleRate,
            isFt4: (profile ?? Ft4ProtocolProfile.Ft4).IsFt4 ? 1 : 0,
            fMinHz,
            fMaxHz,
            output,
            output.Length,
            deep ? 1 : 0,
            string.IsNullOrEmpty(apHints) ? null : apHints,
            apCentreHz,
            string.IsNullOrEmpty(apHints) ? 0 : ApSearchHalfWidthHz,
            maxResidualHzPerSec,
            driftSteps,
            0f,
            0f);
        return Trim(output, n);
    }

    private static Decode[] Trim(Decode[] output, int n)
    {
        if (n <= 0)
            return [];
        var result = new Decode[n];
        Array.Copy(output, result, n);
        return result;
    }
}
