using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OscarWatch.Troubleshoot.Localization;

namespace OscarWatch.Troubleshoot;

/// <summary>Starts and stops OscarWatch's own rotator recording, and shows the end of the latest one.</summary>
internal sealed partial class RotatorTraceViewModel : ObservableObject
{
    private const int TailLineCount = 200;

    private readonly ITroubleshootHost _host;
    private readonly Action<string> _setResult;
    private readonly DispatcherTimer _timer;
    private DateTime _shownWriteUtc;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _latestFileText = "";
    [ObservableProperty] private string _tailText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyRecordingCommand))]
    private bool _hasRecording;

    public RotatorTraceViewModel(ITroubleshootHost host, Action<string> setResult)
    {
        _host = host;
        _setResult = setResult;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Update());
        Update();
    }

    public void Update()
    {
        var until = RotatorTraceRequest.ReadUntilUtc(TroubleshootPaths.DataFolder, DateTime.UtcNow);
        IsRecording = until is not null;
        StatusText = until is { } end
            ? Tr.Format("Trace.Recording", end.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
            : Tr.Get("Trace.NotRecording");

        if (IsRecording)
            _timer.Start();
        else
            _timer.Stop();

        var latest = RotatorTraceRequest.FindLatest(TroubleshootPaths.LogFolder);
        HasRecording = latest is not null;
        if (latest is null)
        {
            LatestFileText = Tr.Get("Trace.NoFile");
            TailText = "";
            _shownWriteUtc = default;
            return;
        }

        LatestFileText = Tr.Format(
            "Trace.LatestFile",
            latest.Name,
            latest.LastWriteTime.ToString("g", CultureInfo.CurrentCulture),
            Math.Max(1, (latest.Length + 1023) / 1024));

        if (latest.LastWriteTimeUtc == _shownWriteUtc)
            return;

        try
        {
            TailText = string.Join(Environment.NewLine, LogFiles.Tail(LogFiles.ReadLines(latest.FullName), TailLineCount));
            _shownWriteUtc = latest.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TailText = Tr.Format("Result.Failed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        try
        {
            RotatorTraceRequest.Start(TroubleshootPaths.DataFolder, DateTime.UtcNow + RotatorTraceRequest.RecordingLength);
            _setResult(Tr.Format("Result.TraceStarted", (int)RotatorTraceRequest.RecordingLength.TotalMinutes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _setResult(Tr.Format("Result.Failed", ex.Message));
        }

        Update();
    }

    private bool CanStart() => !IsRecording;

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private void Stop()
    {
        try
        {
            RotatorTraceRequest.Stop(TroubleshootPaths.DataFolder);
            _setResult(Tr.Get("Result.TraceStopped"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _setResult(Tr.Format("Result.Failed", ex.Message));
        }

        Update();
    }

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private async Task CopyRecordingAsync()
    {
        var latest = RotatorTraceRequest.FindLatest(TroubleshootPaths.LogFolder);
        if (latest is null)
            return;

        try
        {
            await _host.CopyTextAsync(string.Join(Environment.NewLine, LogFiles.ReadLines(latest.FullName)));
            _setResult(Tr.Format("Result.TraceCopied", latest.Name));
        }
        catch (Exception ex)
        {
            _setResult(Tr.Format("Result.Failed", ex.Message));
        }
    }
}
