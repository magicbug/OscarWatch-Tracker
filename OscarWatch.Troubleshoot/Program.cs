using Avalonia;
using Avalonia.Media;
using OscarWatch.Troubleshoot.Localization;

namespace OscarWatch.Troubleshoot;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var uiLanguage = SettingsFile.ReadUiLanguage(TroubleshootPaths.SettingsPath);
        Tr.ApplyCulture(uiLanguage);
        BuildAvaloniaApp(uiLanguage).StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(Tr.DefaultLanguage);

    private static AppBuilder BuildAvaloniaApp(string uiLanguage) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = Tr.FontFamilyFor(uiLanguage) })
            .LogToTrace();
}
