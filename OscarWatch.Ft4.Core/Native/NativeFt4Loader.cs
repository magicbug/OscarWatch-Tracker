using System.Runtime.InteropServices;

namespace OscarWatch.Ft4.Core.Native;

/// <summary>Loads the platform <c>ft4_coder</c> shared library before P/Invoke use.</summary>
public static class NativeFt4Loader
{
    private static int _loadState;

    public static string? LoadError { get; private set; }

    public static bool IsAvailable => EnsureLoaded();

    public static bool EnsureLoaded()
    {
        if (Volatile.Read(ref _loadState) == 1)
            return true;
        if (Volatile.Read(ref _loadState) == -1)
            return false;

        lock (typeof(NativeFt4Loader))
        {
            if (_loadState == 1)
                return true;
            if (_loadState == -1)
                return false;

            try
            {
                var path = ResolveLibraryPath();
                if (path is null)
                {
                    LoadError = $"ft4_coder native library not found for RID {GetRuntimeIdentifier()}.";
                    _loadState = -1;
                    return false;
                }

                NativeLibrary.Load(path);
                _loadState = 1;
                return true;
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
                _loadState = -1;
                return false;
            }
        }
    }

    public static string GetRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => $"win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}",
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _ => $"linux-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}",
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _ => $"osx-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}",
            };
        }

        return "unknown";
    }

    public static string GetLibraryFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "ft4_coder.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "libft4_coder.dylib";
        return "libft4_coder.so";
    }

    private static string? ResolveLibraryPath()
    {
        var fileName = GetLibraryFileName();
        var rid = GetRuntimeIdentifier();
        var baseDir = AppContext.BaseDirectory;

        foreach (var candidate in EnumerateCandidatePaths(baseDir, rid, fileName))
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidatePaths(string baseDir, string rid, string fileName)
    {
        yield return Path.Combine(baseDir, fileName);
        yield return Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        yield return Path.Combine(baseDir, "runtimes", $"ft4_coder-{rid}", fileName);

        var dir = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 6 && dir.Parent is not null; depth++)
        {
            dir = dir.Parent;
            yield return Path.Combine(dir.FullName, "runtimes", $"ft4_coder-{rid}", fileName);
            yield return Path.Combine(dir.FullName, "runtimes", rid, "native", fileName);
        }
    }
}
