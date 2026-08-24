using Avalonia.Controls;
using Avalonia.Interactivity;
using OscarWatch.Core.Models;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Views;

public partial class HamsAtActivationWindow : Window
{
    private readonly HamsAtActivationViewModel _vm;

    public HamsAtActivationWindow()
        : this(new HamsAtActivationViewModel(
            new PassInfo
            {
                SatelliteName = "—",
                NoradId = "0",
                AosUtc = DateTime.UtcNow,
                LosUtc = DateTime.UtcNow.AddMinutes(10),
                MaxElevationDeg = 0,
                MaxElevationUtc = DateTime.UtcNow.AddMinutes(5),
                AosAzimuthDeg = 0,
                LosAzimuthDeg = 0
            },
            new GroundStation(),
            "",
            LocalizationService.Instance,
            "—",
            "—",
            HamsAtActivationHints.Empty))
    {
    }

    public HamsAtActivationWindow(HamsAtActivationViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;
    }

    public bool OpenOnHamsAtAfterPost => _vm.OpenOnHamsAtAfterPost;

    public bool TryBuildRequest(out HamsAtCreateAlertRequest? request) =>
        _vm.TryConfirm(out request);

    private void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (_vm.TryConfirm(out _))
            Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
