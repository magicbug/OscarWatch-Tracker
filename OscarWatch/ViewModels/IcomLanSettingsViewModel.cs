using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Localization;
using OscarWatch.Rig;

namespace OscarWatch.ViewModels;

/// <summary>
/// Serial or network control for an Icom radio with a LAN or Wi-Fi interface:
/// the IC-9700 as a single radio, or an IC-705, IC-905 or IC-7300 dual-radio leg.
/// </summary>
public sealed partial class IcomLanSettingsViewModel : ObservableObject
{
    private readonly ILocalizationService _l;
    private readonly Func<bool> _inContext;
    private readonly Func<RigType, bool> _isCapable;
    private readonly Func<string> _civAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectionChoice), nameof(ShowLanFields), nameof(UsesLan))]
    private RigType _rigType = RigType.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLanFields), nameof(UsesLan))]
    private RigConnectionOption? _selectedConnectionChoice;

    [ObservableProperty]
    private string _lanHost = "";

    [ObservableProperty]
    private int _lanPort = RigSettings.IcomLanDefaultPort;

    [ObservableProperty]
    private string _lanUser = "";

    [ObservableProperty]
    private string _lanPassword = "";

    [ObservableProperty]
    private string _testStatus = "";

    /// <param name="inContext">True when this panel's radio is in use (single radio mode, or Dual radio for a leg).</param>
    /// <param name="isCapable">Which radio types offer network control in this context.</param>
    /// <param name="civAddress">Current CI-V address field (hex) for the connection test.</param>
    public IcomLanSettingsViewModel(
        ILocalizationService localization,
        Func<bool> inContext,
        Func<RigType, bool> isCapable,
        Func<string> civAddress)
    {
        _l = localization;
        _inContext = inContext;
        _isCapable = isCapable;
        _civAddress = civAddress;
        ConnectionChoices =
        [
            new(RigConnectionKind.Serial, _l.Get("Settings.Radio.IcomConnection.Serial")),
            new(RigConnectionKind.Lan, _l.Get("Settings.Radio.IcomConnection.Lan"))
        ];
        _selectedConnectionChoice = ConnectionChoices[0];
    }

    public IReadOnlyList<RigConnectionOption> ConnectionChoices { get; }

    public bool ShowConnectionChoice => _inContext() && _isCapable(RigType);

    public bool UsesLan => _isCapable(RigType) && SelectedConnectionChoice?.Value == RigConnectionKind.Lan;

    public bool ShowLanFields => _inContext() && UsesLan;

    public RigConnectionKind Connection => SelectedConnectionChoice?.Value ?? RigConnectionKind.Serial;

    /// <summary>Re-evaluate visibility after Dual radio is turned on or off.</summary>
    public void NotifyContextChanged()
    {
        OnPropertyChanged(nameof(ShowConnectionChoice));
        OnPropertyChanged(nameof(ShowLanFields));
    }

    public void Load(RigEndpointSettings endpoint) =>
        Load(endpoint.Type, endpoint.Connection, endpoint.NetworkHost, endpoint.NetworkPort,
            endpoint.NetworkUser, endpoint.NetworkPassword);

    public void Load(RigSettings rig) =>
        Load(rig.Type, rig.Connection, rig.NetworkHost, rig.NetworkPort, rig.NetworkUser, rig.NetworkPassword);

    /// <summary>Copy network fields into <paramref name="endpoint"/> when its type supports Icom network control.</summary>
    public void ApplyTo(RigEndpointSettings endpoint)
    {
        if (!_isCapable(endpoint.Type))
            return;

        endpoint.Connection = Connection;
        endpoint.NetworkHost = LanHost.Trim();
        endpoint.NetworkPort = ValidPort(LanPort);
        endpoint.NetworkUser = LanUser.Trim();
        endpoint.NetworkPassword = LanPassword;
    }

    /// <summary>Copy network fields into <paramref name="rig"/> when its type supports Icom network control.</summary>
    public void ApplyTo(RigSettings rig)
    {
        rig.NetworkUser = LanUser.Trim();
        rig.NetworkPassword = LanPassword;
        if (!_isCapable(rig.Type))
            return;

        rig.Connection = Connection;
        rig.NetworkHost = LanHost.Trim();
        rig.NetworkPort = ValidPort(LanPort);
    }

    [RelayCommand]
    public async Task TestLanConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(LanHost) || LanPort is <= 0 or > 65535
            || string.IsNullOrWhiteSpace(LanUser) || string.IsNullOrEmpty(LanPassword))
        {
            TestStatus = _l.Get("Settings.Radio.IcomLanEnterDetails");
            return;
        }

        var type = RigType;
        var host = LanHost.Trim();
        var port = LanPort;
        var user = LanUser.Trim();
        var password = LanPassword;
        var civ = _civAddress();
        var radioName = DeviceIdentityNames.Radio(type);
        TestStatus = _l.Get("Settings.Radio.IcomLanTesting");
        try
        {
            var (identityOk, hz) = await Task.Run(() =>
            {
                using var driver = RigDriverFactory.CreateIcomLan(type, host, port, user, password, civ, 50);
                driver.Open();
                if (!driver.TryConfirmIdentity())
                    return (false, (long?)null);
                return (true, driver.ReadFrequencyHz(RigVfo.Main));
            }).ConfigureAwait(true);

            if (!identityOk)
            {
                TestStatus = _l.Get("Settings.Radio.IcomLanWrongRadio", radioName);
                return;
            }

            var mhz = hz is > 0
                ? (hz.Value / 1_000_000.0).ToString("0.000000", CultureInfo.CurrentCulture)
                : "?";
            TestStatus = _l.Get("Settings.Radio.IcomLanOk", radioName, mhz);
        }
        catch (IcomLanException ex) when (ex.Failure == IcomLanFailure.LoginRejected)
        {
            TestStatus = _l.Get("Settings.Radio.IcomLanLoginRejected");
        }
        catch (IcomLanException ex) when (ex.Failure == IcomLanFailure.Busy)
        {
            TestStatus = _l.Get("Settings.Radio.IcomLanBusy", ex.OtherClient ?? "?");
        }
        catch (IcomLanException ex) when (ex.Failure == IcomLanFailure.NoReply)
        {
            TestStatus = _l.Get("Settings.Radio.IcomLanNoReply", $"{host}:{port}");
        }
        catch (Exception ex)
        {
            TestStatus = _l.Get("Settings.Radio.IcomLanFailed", ex.Message);
        }
    }

    private void Load(
        RigType type, RigConnectionKind connection, string? host, int port, string? user, string? password)
    {
        var capable = _isCapable(type);
        SelectedConnectionChoice = capable
            ? ConnectionChoices.FirstOrDefault(o => o.Value == connection) ?? ConnectionChoices[0]
            : ConnectionChoices[0];
        LanHost = capable ? host ?? "" : "";
        LanPort = capable && port is > 0 and <= 65535
            && port != RigSettings.FlexSmartSdrDefaultPort
            && port != RigEndpointSettings.SdrRigCtlDefaultPort
            ? port
            : RigSettings.IcomLanDefaultPort;
        LanUser = user ?? "";
        LanPassword = password ?? "";
        TestStatus = "";
    }

    private static int ValidPort(int port) => port is > 0 and <= 65535 ? port : RigSettings.IcomLanDefaultPort;
}
