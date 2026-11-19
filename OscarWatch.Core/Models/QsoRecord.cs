namespace OscarWatch.Core.Models;

public sealed class QsoRecord
{
    public long Id { get; init; }
    public long LogbookId { get; init; }
    public DateTime QsoUtc { get; init; }
    public string Call { get; init; } = "";
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
    public string PropMode { get; init; } = "SAT";
    /// <summary>ADIF DXCC entity code, or null when unresolved.</summary>
    public int? Dxcc { get; init; }
    /// <summary>DXCC entity name for display and ADIF COUNTRY.</summary>
    public string Country { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public CloudlogUploadStatus CloudlogUploadStatus { get; init; }
    public int CloudlogUploadAttempts { get; init; }
    public string CloudlogUploadLastError { get; init; } = "";
    public DateTime? CloudlogUploadSentUtc { get; init; }
    /// <summary>Operator's POTA reference or list copied onto this QSO, empty when not set.</summary>
    public string PotaRef { get; init; } = "";
    /// <summary>Operator's SOTA reference copied onto this QSO, empty when not set.</summary>
    public string SotaRef { get; init; } = "";
    /// <summary>Operator's WWFF reference copied onto this QSO, empty when not set.</summary>
    public string WwffRef { get; init; } = "";
}

public sealed class QsoRecordCreateRequest
{
    public required long LogbookId { get; init; }
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
    public string PropMode { get; init; } = "SAT";
    public int? Dxcc { get; init; }
    public string Country { get; init; } = "";
    public CloudlogUploadStatus CloudlogUploadStatus { get; init; } = CloudlogUploadStatus.None;
    public string PotaRef { get; init; } = "";
    public string SotaRef { get; init; } = "";
    public string WwffRef { get; init; } = "";
}

public sealed class QsoRecordUpdateRequest
{
    public required long Id { get; init; }
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
    public string PropMode { get; init; } = "SAT";
    public int? Dxcc { get; init; }
    public string Country { get; init; } = "";
    public string PotaRef { get; init; } = "";
    public string SotaRef { get; init; } = "";
    public string WwffRef { get; init; } = "";
}
