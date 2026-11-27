using System.Text.Json.Serialization;
using OscarWatch.Core.Display;

namespace OscarWatch.Core.Models;
public sealed class SatelliteTransponderMode
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("downlink")]
    public double DownlinkKHz { get; set; }

    [JsonPropertyName("uplink")]
    public double UplinkKHz { get; set; }

    [JsonPropertyName("downlink_min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DownlinkMinKHz { get; set; }

    [JsonPropertyName("downlink_max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DownlinkMaxKHz { get; set; }

    [JsonPropertyName("uplink_min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? UplinkMinKHz { get; set; }

    [JsonPropertyName("uplink_max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? UplinkMaxKHz { get; set; }

    [JsonPropertyName("downlink_mode")]
    public string DownlinkMode { get; set; } = "";

    [JsonPropertyName("uplink_mode")]
    public string UplinkMode { get; set; } = "";

    [JsonPropertyName("doppler")]
    public string Doppler { get; set; } = "NOR";

    [JsonPropertyName("ctcss")]
    public double? CtcssHz { get; set; }

    [JsonPropertyName("ctcss_arm")]
    public double? CtcssArmHz { get; set; }

    /// <summary>SSTV modes the satellite sends on this downlink (<c>SstvModeId</c> names), if known.</summary>
    [JsonPropertyName("sstv_modes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SstvModes { get; set; }

    public bool IsFmMode =>
        UplinkMode.Contains("FM", StringComparison.OrdinalIgnoreCase)
        || DownlinkMode.Contains("FM", StringComparison.OrdinalIgnoreCase);

    public bool HasCtcss => CtcssHz is > 0;

    public bool HasCtcssArm => CtcssArmHz is > 0;

    public bool HasAnyCtcss => IsFmMode && (HasCtcss || HasCtcssArm);

    public DopplerCorrection DopplerCorrection =>
        Doppler.Equals("REV", StringComparison.OrdinalIgnoreCase)
            ? Models.DopplerCorrection.Reverse
            : Models.DopplerCorrection.Normal;

    public bool IsBeaconOnly => UplinkKHz <= 0;

    public bool HasDownlinkPassband =>
        DownlinkMinKHz is > 0 && DownlinkMaxKHz is > 0 && DownlinkMaxKHz > DownlinkMinKHz;

    public bool HasUplinkPassband =>
        UplinkMinKHz is > 0 && UplinkMaxKHz is > 0 && UplinkMaxKHz > UplinkMinKHz;

    public double? PassbandWidthKHz =>
        HasDownlinkPassband
            ? DownlinkMaxKHz!.Value - DownlinkMinKHz!.Value
            : HasUplinkPassband
                ? UplinkMaxKHz!.Value - UplinkMinKHz!.Value
                : null;

    /// <summary>Compact passband width and downlink range for mode dropdown secondary lines.</summary>
    public string PassbandSummarySuffix
    {
        get
        {
            if (!HasDownlinkPassband)
                return "";

            var width = DownlinkMaxKHz!.Value - DownlinkMinKHz!.Value;
            var widthStr = Math.Abs(width % 1) < 0.05
                ? $"{width.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} kHz"
                : $"{width.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} kHz";
            var minStr = FrequencyDisplayFormat.FormatMHzCompact(DownlinkMinKHz!.Value);
            var maxStr = FrequencyDisplayFormat.FormatMHzCompact(DownlinkMaxKHz!.Value);
            return $"{widthStr} ({minStr}–{maxStr})";
        }
    }

    /// <summary>Primary label for mode lists and combo boxes.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Type) ? "Mode" : Type.Trim();

    /// <summary>Secondary line: nominal frequencies and operating modes.</summary>
    public string SummaryLine
    {
        get
        {
            var rx = FrequencyDisplayFormat.FormatMHz(DownlinkKHz);
            if (IsBeaconOnly)
                return $"{rx} · {DownlinkMode} · beacon";

            var tx = FrequencyDisplayFormat.FormatMHz(UplinkKHz);
            var summary = $"{tx} ↑ · {rx} ↓ · {UplinkMode}/{DownlinkMode}";
            var passband = PassbandSummarySuffix;
            if (!string.IsNullOrEmpty(passband))
                summary = $"{summary} · {passband}";
            var tone = CtcssSummarySuffix;
            return string.IsNullOrEmpty(tone) ? summary : $"{summary} · {tone}";
        }
    }

    /// <summary>Compact tone info for mode list secondary lines.</summary>
    public string CtcssSummarySuffix
    {
        get
        {
            if (!HasAnyCtcss)
                return "";

            var parts = new List<string>();
            if (HasCtcss)
                parts.Add($"CTCSS {FrequencyDisplayFormat.FormatCtcssHz(CtcssHz!.Value)}");
            if (HasCtcssArm)
                parts.Add($"arm {FrequencyDisplayFormat.FormatCtcssHz(CtcssArmHz!.Value)}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Primary tone line for the frequency overlay.</summary>
    public string CtcssAccessDisplay =>
        HasCtcss ? FrequencyDisplayFormat.FormatCtcssHz(CtcssHz!.Value) : "";

    public string CtcssArmDisplay =>
        HasCtcssArm ? FrequencyDisplayFormat.FormatCtcssHz(CtcssArmHz!.Value) : "";

    public string CtcssHintLine =>
        HasCtcssArm ? "Send arm tone ~1 s before access" : "";
}