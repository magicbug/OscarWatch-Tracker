using System.Diagnostics;

namespace OscarWatch.Troubleshoot;

/// <summary><see cref="Responding"/> is null where it cannot be told (other platforms, or no window yet).</summary>
internal sealed record RunningTracker(int Id, DateTime? StartedLocal, bool? Responding = null);

/// <summary>Finds, closes, and starts the OscarWatch executable that ships beside this one.</summary>
internal static class TrackerProcess
{
    public const string ProcessName = "OscarWatch";

    public static string ExecutablePath { get; } = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "OscarWatch.exe" : "OscarWatch");

    public static bool ExecutableExists => File.Exists(ExecutablePath);

    public static IReadOnlyList<RunningTracker> FindRunning()
    {
        var result = new List<RunningTracker>();
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                DateTime? started = null;
                try
                {
                    started = process.StartTime;
                }
                catch
                {
                    // another user's process, or it exited
                }

                result.Add(new RunningTracker(process.Id, started, ReadResponding(process)));
            }
        }

        return result;
    }

    private static bool? ReadResponding(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            return process.MainWindowHandle == IntPtr.Zero ? null : process.Responding;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Product version of the sibling OscarWatch, without the source revision suffix.</summary>
    public static string? ReadVersion()
    {
        // Windows ships a single-file exe with a version resource. Linux and macOS apphosts have none, so read the dll.
        var candidates = new[]
        {
            ExecutablePath,
            Path.Combine(AppContext.BaseDirectory, "OscarWatch.dll")
        };

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
                if (string.IsNullOrWhiteSpace(version))
                    continue;

                var plus = version.IndexOf('+');
                return plus > 0 ? version[..plus] : version;
            }
            catch
            {
                // try the next file
            }
        }

        return null;
    }

    /// <summary>Asks each OscarWatch window to close, then ends any process still running after a few seconds.</summary>
    public static async Task CloseAllAsync()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    process.CloseMainWindow();
                }
                catch
                {
                    // ended below
                }
            }

            foreach (var process in processes)
            {
                if (await WaitForExitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    continue;

                process.Kill(entireProcessTree: true);
                if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                    throw new TimeoutException($"Process {process.Id} did not exit.");
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    public static void Start()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false
        })?.Dispose();
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited)
                return true;

            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
