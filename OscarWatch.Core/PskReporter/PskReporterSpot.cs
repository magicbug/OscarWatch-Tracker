namespace OscarWatch.Core.PskReporter;

/// <summary>One station heard, as a PSK Reporter sender information record.</summary>
public sealed record PskReporterSpot(
    string SenderCallsign,
    string? SenderLocator,
    long FrequencyHz,
    int SnrDb,
    string Mode,
    DateTime TimeUtc);

/// <summary>The reporting station, sent once per datagram as the receiver information record.</summary>
public sealed record PskReporterReceiver(
    string Callsign,
    string Locator,
    string DecodingSoftware,
    string Antenna);
