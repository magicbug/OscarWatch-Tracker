using OscarWatch.Core.Models;

namespace OscarWatch.Localization;

public static class RigStatusLocalizer
{
    public static string Localize(ILocalizationService localization, RigConnectionStatus status)
    {
        if (status.StatusKind == RigStatusKind.None)
            return status.IsConnected
                ? localization.Get("Rig.Connected")
                : localization.Get("Rig.Disconnected");

        switch (status.StatusKind)
        {
            case RigStatusKind.Disconnected:
                return localization.Get("Rig.Disconnected");
            case RigStatusKind.Connected:
                return localization.Get("Rig.Connected");
            case RigStatusKind.CatPaused:
                return localization.Get("Rig.CatPaused");
            case RigStatusKind.Tracking:
                return localization.Get("Rig.Tracking");
            case RigStatusKind.Ts2000SatlUnconfirmed:
                return localization.Get("Rig.Ts2000SatlUnconfirmed");
            case RigStatusKind.NoComPort:
                return localization.Get("Rig.NoComPort");
            case RigStatusKind.SelectDualComPorts:
                return localization.Get("Rig.SelectDualComPorts");
            case RigStatusKind.DualNotConnected:
                return string.IsNullOrWhiteSpace(status.StatusDetail)
                    ? localization.Get("Rig.DualNotConnected")
                    : localization.Get("Rig.DualNotConnectedDetail", status.StatusDetail);
            case RigStatusKind.SerialPortNotFound:
                return LocalizeEndpointSerialPortFailure(
                    localization,
                    status,
                    "Rig.SerialPortNotFound",
                    "Rig.DualEndpointSerialPortNotFound");
            case RigStatusKind.SerialPortBusy:
                return LocalizeEndpointSerialPortFailure(
                    localization,
                    status,
                    "Rig.SerialPortBusy",
                    "Rig.DualEndpointSerialPortBusy");
            case RigStatusKind.DualRadioSamePort:
                return string.IsNullOrWhiteSpace(status.StatusPort)
                    ? localization.Get("Rig.DualRadioSamePort")
                    : localization.Get("ComPort.DualRadioSamePort", status.StatusPort);
            case RigStatusKind.FlexControlFailed:
                return string.IsNullOrWhiteSpace(status.StatusDetail)
                    ? localization.Get("Rig.FlexControlFailed")
                    : localization.Get("Rig.FlexControlFailedDetail", status.StatusDetail);
            case RigStatusKind.IdentityMismatch:
                return localization.Get(
                    "Hardware.IdentityMismatch",
                    status.StatusPort ?? "",
                    status.StatusDetail ?? "");
            case RigStatusKind.NotConnected:
                if (!string.IsNullOrWhiteSpace(status.StatusPort) && !string.IsNullOrWhiteSpace(status.StatusDetail))
                    return localization.Get("Rig.NotConnectedPortDetail", status.StatusPort, status.StatusDetail);
                if (!string.IsNullOrWhiteSpace(status.StatusPort))
                    return localization.Get("Rig.NotConnectedPort", status.StatusPort);
                if (!string.IsNullOrWhiteSpace(status.StatusDetail))
                    return localization.Get("Rig.NotConnectedDetail", status.StatusDetail);
                return localization.Get("Rig.NotConnected");
            default:
                return status.StatusDetail ?? RigStatusText.ToEnglish(status);
        }
    }

    private static string LocalizeEndpointSerialPortFailure(
        ILocalizationService localization,
        RigConnectionStatus status,
        string singlePortKey,
        string dualEndpointKey)
    {
        if (!string.IsNullOrWhiteSpace(status.StatusDetail) && !string.IsNullOrWhiteSpace(status.StatusPort))
        {
            var endpoint = status.StatusDetail == "Uplink"
                ? localization.Get("Settings.Radio.Uplink")
                : localization.Get("Settings.Radio.Downlink");
            return localization.Get(dualEndpointKey, endpoint, status.StatusPort);
        }

        if (!string.IsNullOrWhiteSpace(status.StatusPort))
            return localization.Get(singlePortKey, status.StatusPort);

        return RigStatusText.ToEnglish(status);
    }
}
