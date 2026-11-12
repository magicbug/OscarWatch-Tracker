using System.Text.Json;

namespace OscarWatch.Core.CwDecode;

/// <summary>
/// Input and output contract of a DeepCW model, read from the <c>model.onnx.json</c> shipped beside it.
/// </summary>
public sealed class CwModelMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public List<string> Chars { get; set; } = [];
    public int BlankIndex { get; set; }
    public int SampleRate { get; set; }
    public int FftLength { get; set; }
    public int HopLength { get; set; }
    public int OutputTimeStride { get; set; } = 1;
    public double SpectrogramMinFreqHz { get; set; }
    public double SpectrogramMaxFreqHz { get; set; }
    public int SpectrogramFrequencyBins { get; set; }
    public string Normalization { get; set; } = "";
    public double NoiseLevelQuantile { get; set; } = 0.9;
    public string OnnxInputName { get; set; } = "";
    public string OnnxOutputName { get; set; } = "";
    public int NumClasses { get; set; }

    /// <summary>The published wide-band DeepCW model (400 to 1200 Hz at 3200 Hz).</summary>
    public static CwModelMetadata DeepCw() => new()
    {
        Chars =
        [
            ",", ".", "/", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "?",
            "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
            "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", " ",
        ],
        BlankIndex = 41,
        SampleRate = 3200,
        FftLength = 256,
        HopLength = 48,
        OutputTimeStride = 2,
        SpectrogramMinFreqHz = 400.0,
        SpectrogramMaxFreqHz = 1200.0,
        SpectrogramFrequencyBins = 65,
        Normalization = "noise_q90_log1p",
        NoiseLevelQuantile = 0.9,
        OnnxInputName = "spectrogram",
        OnnxOutputName = "log_probs",
        NumClasses = 42,
    };

    public static CwModelMetadata Load(string path)
    {
        var metadata = JsonSerializer.Deserialize<CwModelMetadata>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Empty CW model metadata: {path}");
        metadata.Validate();
        return metadata;
    }

    public void Validate()
    {
        if (SampleRate <= 0 || FftLength <= 0 || HopLength <= 0 || OutputTimeStride <= 0)
            throw new InvalidDataException("CW model metadata has a non-positive sample rate, FFT length or hop.");
        if ((FftLength & (FftLength - 1)) != 0)
            throw new InvalidDataException($"CW model FFT length {FftLength} is not a power of two.");
        if (!(Normalization.StartsWith("noise_q", StringComparison.Ordinal)
              && Normalization.EndsWith("_log1p", StringComparison.Ordinal)))
            throw new InvalidDataException($"Unsupported CW model normalisation: {Normalization}");
        if (Chars.Count == 0 || NumClasses <= BlankIndex || Chars.Any(c => c.Length != 1))
            throw new InvalidDataException("CW model metadata has an invalid alphabet.");

        var (start, stop) = CwSpectrogram.FrequencyBinRange(this);
        if (stop - start != SpectrogramFrequencyBins)
            throw new InvalidDataException(
                $"CW model expects {SpectrogramFrequencyBins} bins, but its frequency range gives {stop - start}.");
    }

    /// <summary>Audio samples per model output frame.</summary>
    public int OutputHopSamples => HopLength * OutputTimeStride;
}
