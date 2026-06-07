namespace OscarWatch.Ft4.Core.Models;

public enum Ft4MessageType
{
    Unknown = 0,
    De = 1,
    dB = 2,
    R_dB = 3,
    RR73 = 4,
    _73 = 5,
    CQ = 6,
}

public sealed class Ft4DecodeLine
{
    public required DateTime Utc { get; init; }
    public long SlotNumber { get; init; }
    public bool IsTransmit { get; init; }
    public string RawText { get; init; } = "";
    public string Message { get; init; } = "";
    public int Snr { get; init; }
    public float OffsetTimeSeconds { get; init; }
    public int FrequencyHz { get; init; }

    public static Ft4DecodeLine FromNativeCallback(string raw, DateTime utc, long slotNumber, bool isTransmit = false)
    {
        if (raw.Length >= 24 && int.TryParse(raw.AsSpan(7, 3), out var snr))
        {
            _ = float.TryParse(raw.AsSpan(11, 4), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var dt);
            _ = int.TryParse(raw.AsSpan(16, 4), out var freq);
            var message = raw.Length > 24 ? raw[24..].Trim() : "";
            return new Ft4DecodeLine
            {
                Utc = utc,
                SlotNumber = slotNumber,
                IsTransmit = isTransmit,
                RawText = raw,
                Message = message,
                Snr = snr,
                OffsetTimeSeconds = dt,
                FrequencyHz = freq,
            };
        }

        return new Ft4DecodeLine
        {
            Utc = utc,
            SlotNumber = slotNumber,
            IsTransmit = isTransmit,
            RawText = raw,
            Message = raw.Trim(),
            FrequencyHz = 1500,
        };
    }
}
