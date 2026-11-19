namespace OscarWatch.Core.Models;

/// <summary>Optional WSJT-X UDP feed for newly logged contacts. Off unless the operator turns it on.</summary>
public sealed class WsjtxUdpSettings
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 2237;
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    public bool Enabled { get; set; }

    public string Host { get; set; } = DefaultHost;

    public int Port { get; set; } = DefaultPort;

    public static bool IsValidPort(int port) => port is >= MinPort and <= MaxPort;

    public static bool IsValidHost(string? host) => !string.IsNullOrWhiteSpace(host);
}
