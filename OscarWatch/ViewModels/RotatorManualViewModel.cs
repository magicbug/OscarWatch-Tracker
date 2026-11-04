using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Core.Services;
using OscarWatch.Localization;

namespace OscarWatch.ViewModels;

public partial class RotatorManualViewModel : ViewModelBase
{
    private readonly IRotatorController _rotator;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _l;
    private Action? _refreshMainDisplay;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RotateCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ParkCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string _currentPositionText = "—";

    [ObservableProperty]
    private decimal _azimuthDeg;

    [ObservableProperty]
    private decimal _elevationDeg;

    [ObservableProperty]
    private decimal _maxAzimuthDeg = 450;

    [ObservableProperty]
    private decimal _maxElevationDeg = 180;

    [ObservableProperty]
    private bool _showElevation = true;

    public RotatorManualViewModel(
        IRotatorController rotator,
        ISettingsService settings,
        ILocalizationService localization)
    {
        _rotator = rotator;
        _settings = settings;
        _l = localization;
    }

    public void Initialize(Action refreshMainDisplay)
    {
        _refreshMainDisplay = refreshMainDisplay;
        var rotatorSettings = _settings.Current.Rotator;
        MaxAzimuthDeg = (decimal)rotatorSettings.MaxAzimuthDeg;
        MaxElevationDeg = (decimal)rotatorSettings.MaxElevationDeg;
        ShowElevation = !rotatorSettings.IsAzimuthOnly;
        RefreshPosition();

        var status = _rotator.GetPositionStatus();
        if (status.AzimuthDeg is { } az)
            AzimuthDeg = az;
        else if (status.CommandedAzimuthDeg is { } commanded)
            AzimuthDeg = commanded;

        if (!ShowElevation)
            ElevationDeg = 0;
        else if (status.ElevationDeg is { } el)
            ElevationDeg = el;
        else if (status.CommandedElevationDeg is { } commandedEl)
            ElevationDeg = commandedEl;
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void Rotate()
    {
        var elevation = ShowElevation ? (double)ElevationDeg : 0;
        _rotator.MoveTo((double)AzimuthDeg, elevation, _settings.Current.Rotator);
        _refreshMainDisplay?.Invoke();
        RefreshPosition();
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void Stop()
    {
        _rotator.Stop(_settings.Current.Rotator);
        RefreshPosition();
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void Park()
    {
        _rotator.Park(_settings.Current.Rotator);
        _refreshMainDisplay?.Invoke();
        RefreshPosition();
    }

    private void RefreshPosition()
    {
        var status = _rotator.GetPositionStatus();
        IsConnected = status.IsConnected;
        if (!status.IsConnected)
        {
            CurrentPositionText = _l.Get("RotatorManual.NotConnected");
            return;
        }

        var azText = MainViewModel.FormatRotatorAzimuthText(status);
        if (!ShowElevation)
        {
            CurrentPositionText = azText == "—"
                ? _l.Get("RotatorManual.PositionUnknown")
                : _l.Get("RotatorManual.PositionAzimuth", azText);
            return;
        }

        var elText = MainViewModel.FormatRotatorElevationText(status);
        CurrentPositionText = azText == "—" && elText == "—"
            ? _l.Get("RotatorManual.PositionUnknown")
            : _l.Get("RotatorManual.PositionAzEl", azText, elText);
    }
}
