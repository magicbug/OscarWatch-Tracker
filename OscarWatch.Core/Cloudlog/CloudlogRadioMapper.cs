using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Core.Cloudlog;

public static class CloudlogRadioMapper
{
    public static CloudlogRadioUpdate? TryCreate(
        string satelliteName,
        SatelliteTransponderMode? mode,
        CorrectedFrequencies? corrected,
        bool cwUplink = false,
        bool cwKeepSidebandDownlink = false)
    {
        if (string.IsNullOrWhiteSpace(satelliteName) || mode is null || corrected is null)
            return null;

        var uplinkHz = ToHz(corrected.RadioTransmitKHz);
        var downlinkHz = ToHz(corrected.RadioReceiveKHz);
        if (mode.IsBeaconOnly || uplinkHz <= 0)
        {
            uplinkHz = downlinkHz;
            if (uplinkHz <= 0)
                return null;
        }

        if (downlinkHz <= 0)
            downlinkHz = uplinkHz;

        var (uplinkMode, downlinkMode) =
            TransponderOperatingModes.GetEffectiveModes(mode, cwUplink, cwKeepSidebandDownlink);

        return new CloudlogRadioUpdate(
            satelliteName.Trim(),
            uplinkHz,
            downlinkHz,
            MapMode(uplinkMode),
            MapMode(downlinkMode));
    }

    public static CloudlogRadioApiRequest ToApiRequest(CloudlogRadioUpdate update, CloudlogSettings settings)
    {
        var radio = string.IsNullOrWhiteSpace(settings.RadioName) ? "OscarWatch" : settings.RadioName.Trim();
        return new CloudlogRadioApiRequest
        {
            Key = settings.ApiKey.Trim(),
            Radio = radio,
            Frequency = update.UplinkHz.ToString(),
            Mode = update.UplinkMode,
            FrequencyRx = update.DownlinkHz.ToString(),
            ModeRx = update.DownlinkMode,
            PropMode = "SAT",
            SatName = update.SatelliteName
        };
    }

    public static string MapMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return "SSB";

        var upper = mode.Trim().ToUpperInvariant();
        return upper switch
        {
            "FMN" => "FM",
            "DATA-FM" or "FM-DATA" => "FM",
            "DATA-USB" => "USB",
            "DATA" => "USB",
            "DV" or "DSTAR" or "D-STAR" => "DIGITALVOICE",
            _ => upper
        };
    }

    private static long ToHz(double kHz) => (long)Math.Round(kHz * 1000.0, MidpointRounding.AwayFromZero);
}
