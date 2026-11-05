using System.Diagnostics;

namespace OscarWatch.Troubleshoot;

internal static class ShellFolder
{
    /// <summary>Explorer on Windows, Finder on macOS, the default file manager (xdg-open) on Linux.</summary>
    public static void Open(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        })?.Dispose();
    }
}
