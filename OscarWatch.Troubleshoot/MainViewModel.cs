using System.Globalization;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Troubleshoot.Localization;

namespace OscarWatch.Troubleshoot;

/// <summary>Dialogues and clipboard, supplied by the window.</summary>
internal interface ITroubleshootHost
{
    Task<bool> ConfirmAsync(string message);

    Task CopyTextAsync(string text);

    Task<string?> PickSavePathAsync(string suggestedFileName);
}

internal sealed partial class MainViewModel(ITroubleshootHost host) : ObservableObject
{
    [ObservableProperty] private string _processText = "";
    [ObservableProperty] private string _applicationText = "";
    [ObservableProperty] private string _systemText = "";
    [ObservableProperty] private string _dataFolderText = TroubleshootPaths.DataFolder;
    [ObservableProperty] private string _logFolderText = TroubleshootPaths.LogFolder;
    [ObservableProperty] private string _latestLogText = "";
    [ObservableProperty] private string _settingsText = "";
    [ObservableProperty] private string _radioText = "";
    [ObservableProperty] private string _rotatorText = "";
    [ObservableProperty] private string _gpsText = "";
    [ObservableProperty] private string _echoCalibrationsText = "";
    [ObservableProperty] private string _recentErrorsText = "";
    [ObservableProperty] private string _resultText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseOscarWatchCommand))]
    private bool _isTrackerRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartOscarWatchCommand))]
    private bool _isTrackerFound;

    private SettingsSnapshot _settings = SettingsFile.Read(TroubleshootPaths.SettingsPath);

    [RelayCommand]
    public void Refresh()
    {
        var running = TrackerProcess.FindRunning();
        IsTrackerRunning = running.Count > 0;
        ProcessText = running.Count == 0
            ? Tr.Get("Process.NotRunning")
            : string.Join(Environment.NewLine, running.Select(p => Tr.Format(
                "Process.Running",
                p.Id,
                p.StartedLocal?.ToString("g", CultureInfo.CurrentCulture) ?? "?")));

        IsTrackerFound = TrackerProcess.ExecutableExists;
        ApplicationText = IsTrackerFound
            ? Tr.Format("Application.Found", TrackerProcess.ReadVersion() ?? "?") + Environment.NewLine + TrackerProcess.ExecutablePath
            : Tr.Get("Application.NotFound");

        SystemText = OsName.Describe();

        RefreshLog();
        RefreshSettings();
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(TroubleshootPaths.DataFolder);

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(TroubleshootPaths.LogFolder);

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            await host.CopyTextAsync(DiagnosticsReport.Build());
            ResultText = Tr.Get("Result.DiagnosticsCopied");
        }
        catch (Exception ex)
        {
            ResultText = Tr.Format("Result.Failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveDiagnosticsAsync()
    {
        try
        {
            var name = $"oscarwatch-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
            var path = await host.PickSavePathAsync(name);
            if (path is null)
                return;

            await File.WriteAllTextAsync(path, DiagnosticsReport.Build());
            ResultText = Tr.Format("Result.DiagnosticsSaved", path);
        }
        catch (Exception ex)
        {
            ResultText = Tr.Format("Result.Failed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(IsTrackerRunning))]
    private async Task CloseOscarWatchAsync()
    {
        if (!await host.ConfirmAsync(Tr.Get("Confirm.CloseOscarWatch")))
            return;

        await CloseTrackerAsync();
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(IsTrackerFound))]
    private void StartOscarWatch()
    {
        Refresh();
        if (IsTrackerRunning)
        {
            ResultText = Tr.Get("Result.AlreadyRunning");
            return;
        }

        try
        {
            TrackerProcess.Start();
            ResultText = Tr.Get("Result.Started");
        }
        catch (Exception ex)
        {
            ResultText = Tr.Format("Result.StartFailed", ex.Message);
        }

        Refresh();
    }

    [RelayCommand]
    private async Task DisableHardwareAsync()
    {
        Refresh();
        switch (_settings.State)
        {
            case SettingsFileState.Missing:
                ResultText = Tr.Get("Result.SettingsMissing");
                return;
            case SettingsFileState.Unreadable:
                ResultText = Tr.Get("Result.SettingsUnreadable");
                return;
        }

        if (_settings.Radio.Enabled != true && _settings.Rotator.Enabled != true && _settings.Gps.Enabled != true)
        {
            ResultText = Tr.Get("Result.HardwareAlreadyOff");
            return;
        }

        var message = Tr.Format(
            "Confirm.DisableHardware",
            DescribeDevice(_settings.Radio),
            DescribeDevice(_settings.Rotator),
            DescribeDevice(_settings.Gps));
        if (!await host.ConfirmAsync(message) || !await EnsureTrackerClosedAsync())
            return;

        try
        {
            ResultText = SettingsFile.DisableHardware(TroubleshootPaths.SettingsPath) switch
            {
                DisableHardwareResult.Disabled => Tr.Get("Result.HardwareDisabled"),
                DisableHardwareResult.AlreadyDisabled => Tr.Get("Result.HardwareAlreadyOff"),
                DisableHardwareResult.Missing => Tr.Get("Result.SettingsMissing"),
                _ => Tr.Get("Result.SettingsUnreadable")
            };
        }
        catch (Exception ex)
        {
            ResultText = Tr.Format("Result.Failed", ex.Message);
        }

        Refresh();
    }

    [RelayCommand]
    private async Task ClearEchoCalibrationsAsync()
    {
        Refresh();
        switch (_settings.State)
        {
            case SettingsFileState.Missing:
                ResultText = Tr.Get("Result.SettingsMissing");
                return;
            case SettingsFileState.Unreadable:
                ResultText = Tr.Get("Result.SettingsUnreadable");
                return;
        }

        if (_settings.EchoCalibrationCount == 0)
        {
            ResultText = Tr.Get("Result.EchoNoneSaved");
            return;
        }

        if (!await host.ConfirmAsync(Tr.Format("Confirm.ClearEchoCalibrations", _settings.EchoCalibrationCount))
            || !await EnsureTrackerClosedAsync())
            return;

        try
        {
            ResultText = SettingsFile.ClearEchoCalibrations(TroubleshootPaths.SettingsPath) switch
            {
                ClearEchoCalibrationsResult.Cleared => Tr.Get("Result.EchoCleared"),
                ClearEchoCalibrationsResult.NoneSaved => Tr.Get("Result.EchoNoneSaved"),
                ClearEchoCalibrationsResult.Missing => Tr.Get("Result.SettingsMissing"),
                _ => Tr.Get("Result.SettingsUnreadable")
            };
        }
        catch (Exception ex)
        {
            ResultText = Tr.Format("Result.Failed", ex.Message);
        }

        Refresh();
    }

    [RelayCommand]
    private async Task PutSettingsAsideAsync()
    {
        var folderName = "settings-aside-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        if (!await host.ConfirmAsync(Tr.Format("Confirm.PutSettingsAside", folderName)) || !await EnsureTrackerClosedAsync())
            return;

        try
        {
            var folder = SettingsFile.PutAside(TroubleshootPaths.SettingsPath, DateTime.UtcNow);
            ResultText = folder is null
                ? Tr.Get("Result.NothingToMove")
                : Tr.Format("Result.SettingsAside", folder);
        }
        catch (Exception ex)
        {
            ResultText = Tr.Format("Result.Failed", ex.Message);
        }

        Refresh();
    }

    /// <summary>A running tracker can save settings as it exits and undo an edit, so it must be gone first.</summary>
    private async Task<bool> EnsureTrackerClosedAsync()
    {
        if (TrackerProcess.FindRunning().Count == 0)
            return true;

        if (!await host.ConfirmAsync(Tr.Get("Confirm.CloseFirst")))
        {
            ResultText = Tr.Get("Result.StillRunning");
            return false;
        }

        return await CloseTrackerAsync();
    }

    private async Task<bool> CloseTrackerAsync()
    {
        try
        {
            await TrackerProcess.CloseAllAsync();
            ResultText = Tr.Get("Result.Closed");
            return true;
        }
        catch (Exception ex)
        {
            ResultText = Tr.Format("Result.CloseFailed", ex.Message);
            return false;
        }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            ShellFolder.Open(folder);
        }
        catch (Exception ex)
        {
            ResultText = Tr.Format("Result.FolderOpenFailed", ex.Message);
        }
    }

    private void RefreshLog()
    {
        var latest = LogFiles.FindLatest(TroubleshootPaths.LogFolder);
        if (latest is null)
        {
            LatestLogText = Tr.Get("Log.None");
            RecentErrorsText = Tr.Get("Errors.None");
            return;
        }

        LatestLogText = Tr.Format("Log.Latest", latest.Name, latest.LastWriteTime.ToString("g", CultureInfo.CurrentCulture));
        try
        {
            var problems = LogFiles.RecentProblems(LogFiles.ReadLines(latest.FullName));
            RecentErrorsText = problems.Count == 0
                ? Tr.Get("Errors.None")
                : string.Join(Environment.NewLine + Environment.NewLine, problems);
        }
        catch (Exception ex)
        {
            RecentErrorsText = Tr.Format("Result.Failed", ex.Message);
        }
    }

    private void RefreshSettings()
    {
        _settings = SettingsFile.Read(TroubleshootPaths.SettingsPath);
        SettingsText = _settings.State switch
        {
            SettingsFileState.Missing => Tr.Get("Settings.Missing"),
            SettingsFileState.Readable => Tr.Format(
                "Settings.Readable",
                _settings.LastWriteLocal?.ToString("g", CultureInfo.CurrentCulture) ?? "?"),
            _ => Tr.Format("Settings.Unreadable", _settings.Error ?? "?")
        };

        RadioText = DescribeDevice(_settings.Radio);
        RotatorText = DescribeDevice(_settings.Rotator);
        GpsText = DescribeDevice(_settings.Gps);
        EchoCalibrationsText = _settings.State != SettingsFileState.Readable
            ? Tr.Get("Device.Unknown")
            : _settings.EchoCalibrationCount == 0
                ? Tr.Get("Echo.None")
                : Tr.Format("Echo.Count", _settings.EchoCalibrationCount);
    }

    private static string DescribeDevice(DeviceState device) => device.Enabled switch
    {
        true when device.Target.Length > 0 => Tr.Format("Device.On", device.Target),
        true => Tr.Get("Device.OnNoTarget"),
        false => Tr.Get("Device.Off"),
        null => Tr.Get("Device.Unknown")
    };
}
