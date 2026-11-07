using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Troubleshoot.Localization;

namespace OscarWatch.Troubleshoot;

/// <summary>GS-232 tab: talks to the saved rotator directly while OscarWatch is closed and keeps a byte log.</summary>
internal sealed partial class Gs232ProbeViewModel : ObservableObject
{
    private static readonly int[] ReadDelaysAfterMoveMs = [1000, 3000];

    private readonly ITroubleshootHost _host;
    private readonly Func<Task<bool>> _ensureTrackerClosed;
    private readonly Action<string> _setResult;
    private readonly Gs232ProbeLog _log = new();
    private Gs232Target? _target;

    [ObservableProperty] private string _availabilityText = "";
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private string _baudText = "";
    [ObservableProperty] private string _azimuthOnlyText = "";
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private string _logText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadPositionCommand), nameof(StopCommand), nameof(MoveCommand))]
    private bool _isAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadPositionCommand), nameof(StopCommand), nameof(MoveCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private Gs232Position _lastPosition;

    public Gs232ProbeViewModel(ITroubleshootHost host, Func<Task<bool>> ensureTrackerClosed, Action<string> setResult)
    {
        _host = host;
        _ensureTrackerClosed = ensureTrackerClosed;
        _setResult = setResult;
        _log.LineAdded += line => Dispatcher.UIThread.Post(() =>
            LogText = LogText.Length == 0 ? line : LogText + Environment.NewLine + line);
        PositionText = Tr.Get("Gs232.PositionUnknown");
    }

    private bool CanProbe => IsAvailable && !IsBusy;

    private bool CanMove => CanProbe && _target is { } target && LastPosition.Azimuth is not null
                            && (target.AzimuthOnly || LastPosition.Elevation is not null);

    public void Update(SettingsSnapshot settings)
    {
        var target = settings.State == SettingsFileState.Readable ? settings.Gs232 : null;
        if (target != _target)
            LastPosition = default;
        _target = target;

        if (target is null)
        {
            AvailabilityText = Tr.Get("Gs232.SettingsUnavailable");
            PortText = BaudText = AzimuthOnlyText = Tr.Get("Device.Unknown");
            IsAvailable = false;
            return;
        }

        PortText = target.Port.Length > 0 ? target.Port : Tr.Get("Device.Unknown");
        BaudText = target.BaudRate.ToString(CultureInfo.CurrentCulture);
        AzimuthOnlyText = Tr.Get(target.AzimuthOnly ? "Gs232.Yes" : "Gs232.No");

        AvailabilityText = !target.IsSerialGs232
            ? Tr.Get("Gs232.NotGs232")
            : target.Port.Length == 0
                ? Tr.Get("Gs232.NoPort")
                : Tr.Get("Gs232.Ready");
        IsAvailable = target.IsSerialGs232 && target.Port.Length > 0;
    }

    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task ReadPositionAsync()
    {
        if (await RunAsync(session => session.ReadPosition(_target!.AzimuthOnly)) is { } position)
            ShowPosition(position);
    }

    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task StopAsync()
    {
        var sent = await RunAsync(session =>
        {
            session.Send("S");
            return true;
        });
        if (sent == true)
            _setResult(Tr.Get("Result.Gs232Stopped"));
    }

    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task MoveAsync()
    {
        var target = _target!;
        var from = LastPosition.Azimuth!.Value;
        var to = Gs232Commands.NudgeAzimuth(from, target.MaxAzimuthDeg);
        var command = Gs232Commands.Move(
            to, LastPosition.Elevation ?? 0, target.AzimuthOnly, target.MaxAzimuthDeg, target.MaxElevationDeg);

        if (!await _host.ConfirmAsync(Tr.Format("Confirm.Gs232Move", command, target.Port, from, to)))
            return;

        var position = await RunAsync(session =>
        {
            session.Send(command);
            Gs232Position latest = default;
            var waited = 0;
            foreach (var delay in ReadDelaysAfterMoveMs)
            {
                session.Wait(delay - waited);
                waited = delay;
                _log.Info(string.Create(CultureInfo.InvariantCulture, $"Reading position {delay / 1000.0:0.0} s after {command}"));
                latest = session.ReadPosition(target.AzimuthOnly);
            }

            return latest;
        });

        if (position is { } after)
        {
            ShowPosition(after);
            _setResult(Tr.Format("Result.Gs232Moved", command, PositionText));
        }
    }

    [RelayCommand]
    private async Task CopyLogAsync()
    {
        try
        {
            await _host.CopyTextAsync(_log.Text);
            _setResult(Tr.Get("Result.Gs232LogCopied"));
        }
        catch (Exception ex)
        {
            _setResult(Tr.Format("Result.Failed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        try
        {
            var path = await _host.PickSavePathAsync($"oscarwatch-gs232-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            if (path is null)
                return;

            await File.WriteAllTextAsync(path, _log.Text);
            _setResult(Tr.Format("Result.Gs232LogSaved", path));
        }
        catch (Exception ex)
        {
            _setResult(Tr.Format("Result.Failed", ex.Message));
        }
    }

    /// <summary>Null when the operator kept OscarWatch running or the port could not be used.</summary>
    private async Task<T?> RunAsync<T>(Func<Gs232Session, T> action) where T : struct
    {
        if (!await _ensureTrackerClosed() || _target is not { } target || !IsAvailable)
            return null;

        IsBusy = true;
        try
        {
            var (value, error) = await Task.Run(() =>
                Gs232Session.Run(() => new Gs232SerialPort(target.Port, target.BaudRate), target, _log, action));
            if (error is not null)
            {
                _setResult(Tr.Format("Result.Gs232PortFailed", target.Port, error));
                return null;
            }

            return value;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowPosition(Gs232Position position)
    {
        LastPosition = position;
        PositionText = position switch
        {
            { Azimuth: { } az, Elevation: { } el } => Tr.Format("Gs232.Position", az, el),
            { Azimuth: { } az } => Tr.Format("Gs232.PositionAzimuth", az),
            { Elevation: { } el } => Tr.Format("Gs232.PositionElevation", el),
            _ => Tr.Get("Gs232.PositionNone")
        };
        _setResult(position.HasAny ? Tr.Format("Result.Gs232Position", PositionText) : Tr.Get("Result.Gs232NoPosition"));
    }
}
