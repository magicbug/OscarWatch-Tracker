using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Localization;
using OscarWatch.Rig;

namespace OscarWatch.ViewModels;

public sealed record RigConnectionOption(RigConnectionKind Value, string Label);

/// <summary>COM / LAN settings for one dual-radio leg when it is a Kenwood TS-990S, TS-890S or TS-590.</summary>
public sealed partial class KenwoodHfLegSettingsViewModel : ObservableObject
{
    private readonly ILocalizationService _l;
    private readonly Func<bool> _dualRadioEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKenwoodHf), nameof(ShowConnectionChoice), nameof(ShowLanFields),
        nameof(ShowSerialOptions), nameof(ShowAccountType), nameof(UsesLan))]
    private RigType _rigType = RigType.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLanFields), nameof(ShowSerialOptions), nameof(ShowAccountType), nameof(UsesLan))]
    private RigConnectionOption? _selectedConnectionChoice;

    [ObservableProperty]
    private string _lanHost = "";

    [ObservableProperty]
    private int _lanPort = RigSettings.KenwoodLanDefaultPort;

    [ObservableProperty]
    private string _lanUser = "";

    [ObservableProperty]
    private string _lanPassword = "";

    [ObservableProperty]
    private bool _lanAdministrator = true;

    [ObservableProperty]
    private bool _hardwareRtsEnabled = true;

    [ObservableProperty]
    private string _testStatus = "";

    public KenwoodHfLegSettingsViewModel(ILocalizationService localization, Func<bool> dualRadioEnabled)
    {
        _l = localization;
        _dualRadioEnabled = dualRadioEnabled;
        ConnectionChoices =
        [
            new(RigConnectionKind.Serial, _l.Get("Settings.Radio.KenwoodConnection.Serial")),
            new(RigConnectionKind.Lan, _l.Get("Settings.Radio.KenwoodConnection.Lan"))
        ];
        _selectedConnectionChoice = ConnectionChoices[0];
    }

    public IReadOnlyList<RigConnectionOption> ConnectionChoices { get; }

    public bool IsKenwoodHf => _dualRadioEnabled() && RigSettings.IsKenwoodHfEndpoint(RigType);

    public bool ShowConnectionChoice => _dualRadioEnabled() && RigSettings.IsKenwoodLanCapable(RigType);

    public bool UsesLan =>
        RigSettings.IsKenwoodLanCapable(RigType) && SelectedConnectionChoice?.Value == RigConnectionKind.Lan;

    public bool ShowLanFields => _dualRadioEnabled() && UsesLan;

    public bool ShowSerialOptions => IsKenwoodHf && !UsesLan;

    public bool ShowAccountType => ShowLanFields && RigType == RigType.KenwoodTs890s;

    public RigConnectionKind Connection => SelectedConnectionChoice?.Value ?? RigConnectionKind.Serial;

    /// <summary>Re-evaluate visibility after Dual radio is turned on or off.</summary>
    public void NotifyDualRadioChanged()
    {
        OnPropertyChanged(nameof(IsKenwoodHf));
        OnPropertyChanged(nameof(ShowConnectionChoice));
        OnPropertyChanged(nameof(ShowLanFields));
        OnPropertyChanged(nameof(ShowSerialOptions));
        OnPropertyChanged(nameof(ShowAccountType));
    }

    public void Load(RigEndpointSettings endpoint)
    {
        SelectedConnectionChoice = ConnectionChoices.FirstOrDefault(o => o.Value == endpoint.Connection)
            ?? ConnectionChoices[0];
        var isKenwoodLanHost = RigSettings.IsKenwoodLanCapable(endpoint.Type);
        LanHost = isKenwoodLanHost ? endpoint.NetworkHost ?? "" : "";
        LanPort = isKenwoodLanHost && endpoint.NetworkPort is > 0 and <= 65535
            && endpoint.NetworkPort != RigEndpointSettings.SdrRigCtlDefaultPort
            ? endpoint.NetworkPort
            : RigSettings.KenwoodLanDefaultPort;
        LanUser = endpoint.NetworkUser ?? "";
        LanPassword = endpoint.NetworkPassword ?? "";
        LanAdministrator = endpoint.NetworkUserIsAdministrator;
        HardwareRtsEnabled = endpoint.KenwoodHardwareRtsEnabled;
        TestStatus = "";
    }

    /// <summary>Copy Kenwood fields into <paramref name="endpoint"/>. Network host/port only when this leg is LAN.</summary>
    public void ApplyTo(RigEndpointSettings endpoint)
    {
        endpoint.Connection = Connection;
        endpoint.NetworkUser = LanUser.Trim();
        endpoint.NetworkPassword = LanPassword;
        endpoint.NetworkUserIsAdministrator = LanAdministrator;
        endpoint.KenwoodHardwareRtsEnabled = HardwareRtsEnabled;
        if (RigSettings.IsKenwoodLanCapable(endpoint.Type))
        {
            endpoint.NetworkHost = LanHost.Trim();
            endpoint.NetworkPort = LanPort is > 0 and <= 65535 ? LanPort : RigSettings.KenwoodLanDefaultPort;
        }
    }

    [RelayCommand]
    public async Task TestLanConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(LanHost) || LanPort is <= 0 or > 65535
            || string.IsNullOrWhiteSpace(LanUser) || string.IsNullOrEmpty(LanPassword))
        {
            TestStatus = _l.Get("Settings.Radio.KenwoodLanEnterDetails");
            return;
        }

        var endpoint = new RigEndpointSettings { Type = RigType };
        ApplyTo(endpoint);
        endpoint.Connection = RigConnectionKind.Lan;
        var radioName = DeviceIdentityNames.Radio(RigType);
        TestStatus = _l.Get("Settings.Radio.KenwoodLanTesting");
        try
        {
            var (identityOk, hz) = await Task.Run(() =>
            {
                using var driver = RigDriverFactory.Create(endpoint);
                driver.Open();
                if (!driver.TryConfirmIdentity())
                    return (false, (long?)null);
                return (true, driver.ReadFrequencyHz(RigVfo.Main));
            }).ConfigureAwait(true);

            if (!identityOk)
            {
                TestStatus = _l.Get("Settings.Radio.KenwoodLanWrongRadio", radioName);
                return;
            }

            var mhz = hz is > 0
                ? (hz.Value / 1_000_000.0).ToString("0.000000", CultureInfo.CurrentCulture)
                : "?";
            TestStatus = _l.Get("Settings.Radio.KenwoodLanOk", radioName, mhz);
        }
        catch (KenwoodLanException ex) when (ex.Failure == KenwoodLanFailure.ConnectionDenied)
        {
            TestStatus = _l.Get("Settings.Radio.KenwoodLanDenied");
        }
        catch (KenwoodLanException ex) when (ex.Failure is KenwoodLanFailure.LoginRejected or KenwoodLanFailure.InvalidCredentials)
        {
            TestStatus = _l.Get("Settings.Radio.KenwoodLanLoginRejected");
        }
        catch (Exception ex)
        {
            TestStatus = _l.Get("Settings.Radio.KenwoodLanFailed", ex.Message);
        }
    }
}
