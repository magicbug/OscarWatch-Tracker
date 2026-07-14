using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OscarWatch.Core.Logbook;
using OscarWatch.Core.Net;
using OscarWatch.Core.Orbit;
using OscarWatch.Core.Services;
using OscarWatch.Orbit;
using OscarWatch.Core.Models;
using OscarWatch.ViewModels;
using OscarWatch.Cloudlog;
using OscarWatch.Recording;
using OscarWatch.Gps;
using OscarWatch.Rig;
using OscarWatch.Rotator;
using OscarWatch.SatelliteLink;
using OscarWatch.Services;
using OscarWatch.Speech;
using OscarWatch.Theme;
using OscarWatch.Diagnostics;
using OscarWatch.Localization;
using OscarWatch.Views;
using Serilog;

namespace OscarWatch;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ITleService>(sp =>
            new TleService(sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton<ISpeechService, PlatformSpeechService>();
        services.AddSingleton<PortAudioRecordingService>();
        services.AddSingleton<IAudioRecordingService>(sp => sp.GetRequiredService<PortAudioRecordingService>());
        services.AddSingleton<IRecordingTaskScheduler, LoggingRecordingTaskScheduler>();
        services.AddSingleton<RisingPassAnnouncer>();
        services.AddSingleton<PassRecordingCoordinator>();
        services.AddSingleton<IRotatorController, RotatorController>();
        services.AddSingleton<IDopplerPassLogger, DopplerPassLogger>();
        services.AddSingleton<IRigController>(sp => new RigController(
            propagator: sp.GetRequiredService<IOrbitPropagator>(),
            settingsService: sp.GetRequiredService<ISettingsService>(),
            dopplerPassLogger: sp.GetRequiredService<IDopplerPassLogger>()));
        services.AddSingleton<IGpsService, GpsService>();
        services.AddSingleton<ICloudlogRadioSyncService, CloudlogRadioSyncService>();
        services.AddSingleton<ISatelliteLinkBroadcastService, SatelliteLinkBroadcastService>();
        services.AddSingleton<ICloudlogLookupService, CloudlogLookupService>();
        services.AddSingleton<CloudlogQsoClient>();
        services.AddSingleton<ICloudlogQsoUploadService, CloudlogQsoUploadService>();
        var bundledDb = Path.Combine(AppContext.BaseDirectory, "Assets", "satellite_database.json");
        services.AddSingleton<ISatelliteDatabaseService>(_ =>
            new SatelliteDatabaseService(bundledDb));
        services.AddSingleton<ISatelliteDatabaseEditor>(sp =>
            new SatelliteDatabaseEditor(
                sp.GetRequiredService<ISatelliteDatabaseService>(),
                bundledDb));
        services.AddSingleton<ISatelliteDatabaseSyncService, SatelliteDatabaseSyncService>();
        services.AddSingleton<IGitHubReleaseService, GitHubReleaseService>();
        services.AddSingleton<IHamsAtRovesService, HamsAtRovesService>();
        services.AddSingleton<ILocalizationService>(LocalizationService.Instance);
        services.AddSingleton<LiveTrackerSnapshotProvider>();
        services.AddSingleton<ILiveTrackerSnapshotProvider>(sp => sp.GetRequiredService<LiveTrackerSnapshotProvider>());
        services.AddSingleton<IQsoLogbookRepository, QsoLogbookRepository>();
        services.AddSingleton<FrequencyOverlayViewModel>();
        services.AddSingleton<DxStationOverlayViewModel>();
        services.AddSingleton<ITrackingDiagnostics, SerilogTrackingDiagnostics>();
        services.AddSingleton<TrackingOrchestrator>();
        services.AddSingleton<LiveTrackingService>(sp => new LiveTrackingService(
            sp.GetRequiredService<TrackingOrchestrator>(),
            sp.GetRequiredService<IGpsService>()));
        services.AddSingleton<ILiveTrackingService>(sp => sp.GetRequiredService<LiveTrackingService>());
        services.AddOscarWatchOrbit();
        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SatellitePickerViewModel>();
        services.AddTransient<PassPlanningViewModel>();
        services.AddTransient<MutualPassViewModel>();
        services.AddTransient<MutualPassVisualizerViewModel>();
        services.AddTransient<PassVisualizerViewModel>();
        services.AddTransient<PassRadarGalleryViewModel>();
        services.AddTransient<DopplerPassInsightsViewModel>();
        services.AddTransient<SunlightPredictionViewModel>();
        services.AddTransient<SatelliteDatabaseEditorViewModel>();
        services.AddTransient<RotatorManualViewModel>();
        services.AddTransient<QsoLogbookViewModel>();
        services.AddTransient<CreateLogbookViewModel>();
        services.AddTransient<LogbookSettingsViewModel>();
        services.AddSingleton<OscarWatch.Core.SessionPlanner.SessionPlannerService>();
        services.AddSingleton<OscarWatch.Core.SessionPlanner.PlanExecutor>(sp =>
            new OscarWatch.Core.SessionPlanner.PlanExecutor(sp.GetRequiredService<ILiveTrackingService>()));
        services.AddTransient<SessionPlannerViewModel>();

        Services = services.BuildServiceProvider();

        var settingsService = Services.GetRequiredService<ISettingsService>();
        settingsService.Load();

        LocalizationCulture.ApplyFromSettings(settingsService);
        AppThemeManager.Apply(AppThemeManager.ReadPreferenceFromDisk());
        AccessibilityThemeResources.Install();

        AppLogging.RegisterAvaloniaHandlers();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev";
            Log.Information("OscarWatch {Version} starting", version);

            var startupStopwatch = Stopwatch.StartNew();
            var mainVm = Services.GetRequiredService<MainViewModel>();
            MainWindow = new MainWindow { DataContext = mainVm };
            MainWindowBounds.Apply(MainWindow, settingsService.Current);
            desktop.MainWindow = MainWindow;
            desktop.ShutdownRequested += OnDesktopShutdownRequested;
            Log.Information("Main window created in {ElapsedMs} ms", startupStopwatch.ElapsedMilliseconds);

            AppSingleInstance.StartActivationListener(ActivateMainWindow);

            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        OscarWatchHttpClients.DisposeSharedHandler();
    }

    private static void ActivateMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (MainWindow is null)
                return;

            if (MainWindow.WindowState == WindowState.Minimized)
                MainWindow.WindowState = WindowState.Normal;

            MainWindow.Show();
            MainWindow.Activate();
        });
    }

    private static void OnDesktopShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        Services?.GetService<MainViewModel>()?.DisconnectHardwareForShutdown();
        Services?.GetService<ISatelliteLinkBroadcastService>()?.StopAsync().GetAwaiter().GetResult();

        // Flush pending settings save on shutdown
        var settings = Services?.GetService<ISettingsService>();
        settings?.FlushAsync().GetAwaiter().GetResult();
    }
}
