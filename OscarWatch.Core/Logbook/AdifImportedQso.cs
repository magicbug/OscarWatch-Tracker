namespace OscarWatch.Core.Logbook;

/// <summary>One QSO parsed from an ADI file, before duplicate checks or persistence.</summary>
public sealed class AdifImportedQso
{
    public required DateTime QsoUtc { get; init; }
    public required string Call { get; init; }
    public string RstSent { get; init; } = "";
    public string RstRcvd { get; init; } = "";
    public string GridSquare { get; init; } = "";
    public string Name { get; init; } = "";
    public string Comment { get; init; } = "";
    public string SatName { get; init; } = "";
    public string Mode { get; init; } = "";
    public string ModeRx { get; init; } = "";
    public long FreqHz { get; init; }
    public long FreqRxHz { get; init; }
    public string Band { get; init; } = "";
    public string BandRx { get; init; } = "";
    /// <summary>Propagation mode from the file. Empty when the record has no <c>PROP_MODE</c>.</summary>
    public string PropMode { get; init; } = "";
    public int? Dxcc { get; init; }
    public string Country { get; init; } = "";

    /// <summary>
    /// True when the record is a satellite contact: <c>PROP_MODE</c> is <c>SAT</c>,
    /// or <c>SAT_NAME</c> is present.
    /// </summary>
    public bool IsSatelliteContact =>
        string.Equals(PropMode, "SAT", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(SatName);

    /// <summary>UTC instant truncated to the minute, used as the duplicate key with <see cref="Call"/>.</summary>
    public DateTime DuplicateMinuteUtc => AdifImporter.ToDuplicateMinute(QsoUtc);
}
