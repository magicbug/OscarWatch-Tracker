namespace OscarWatch.Ft4.Core.Native;

/// <summary>Sample counts and timing aligned with <c>ft4_coder_api.h</c> and SkyRoof <c>NativeFT4Coder</c>.</summary>
public static class Ft4Constants
{
    public const int SamplesPerSymbol = 576 * 4;
    private const int SymbolsPerMessage = 16 + 87;

    public const int SamplingRateHz = 48_000;
    public const int DefaultAudioFrequencyHz = 1500;

    public const int EncodeMessageLength = 37;
    public const int EncodeSampleCount = (SymbolsPerMessage + 2) * SamplesPerSymbol;
    public const double EncodeSeconds = EncodeSampleCount / (double)SamplingRateHz;

    public const int DecodeSampleCount = 21 * 3456 * 4;
    public const double DecodeSeconds = DecodeSampleCount / (double)SamplingRateHz;

    public const double TimeSlotSeconds = 7.5;
    public const int MaxCallLength = 12;
    public const int SignalBandwidthHz = 83;
}
