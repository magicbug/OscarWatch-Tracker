using System;
using System.Runtime.InteropServices;

namespace OscarWatch.Troubleshoot;

internal static class OsName
{
    /// <summary>Windows 11 still reports version 10.0; build 22000 and later is Windows 11.</summary>
    public static string Describe()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (!OperatingSystem.IsWindows())
            return $"{RuntimeInformation.OSDescription} ({arch})";

        var version = Environment.OSVersion.Version;
        var name = version.Major == 10 && version.Build >= 22000 ? "Windows 11" : $"Windows {version.Major}";
        return $"{name} (build {version.Build}, {arch})";
    }
}
