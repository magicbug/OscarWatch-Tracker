namespace OscarWatch.Core.Sstv;

public enum SstvModeId
{
    Robot36,
    Robot72,
    ScottieS1,
    ScottieS2,
    ScottieDx,
    MartinM1,
    MartinM2,
    WraaseSc2180,
    Pd50,
    Pd90,
    Pd120,
    Pd160,
    Pd180,
    Pd240,
    Pd290,
}

public enum SstvColourSpace
{
    Rgb,
    YCbCr,
}

/// <summary>What a timed part of a transmitted line carries.</summary>
public enum SstvChannel
{
    Sync,
    /// <summary>Porch or separator tone: no picture content.</summary>
    Gap,
    Red,
    Green,
    Blue,
    /// <summary>Luminance of the first image row in the line (the only row outside PD).</summary>
    Y0,
    /// <summary>Luminance of the second image row (PD only).</summary>
    Y1,
    RMinusY,
    BMinusY,
    /// <summary>Robot 36 chroma: R-Y on even lines, B-Y on odd lines.</summary>
    AlternatingChroma,
}

public readonly record struct SstvSegment(SstvChannel Channel, double Ms);

/// <summary>Timing and layout of one SSTV mode. Times are milliseconds.</summary>
public sealed class SstvMode
{
    public SstvMode(
        SstvModeId id,
        string name,
        int visCode,
        int width,
        int height,
        SstvColourSpace colourSpace,
        IReadOnlyList<SstvSegment> segments,
        int rowsPerLine = 1,
        double leadInMs = 0)
    {
        Id = id;
        Name = name;
        VisCode = visCode;
        Width = width;
        Height = height;
        ColourSpace = colourSpace;
        Segments = segments;
        RowsPerLine = rowsPerLine;
        LeadInMs = leadInMs;

        var offset = 0.0;
        var syncFound = false;
        foreach (var segment in segments)
        {
            if (segment.Channel == SstvChannel.Sync && !syncFound)
            {
                SyncOffsetMs = offset;
                SyncMs = segment.Ms;
                syncFound = true;
            }

            offset += segment.Ms;
        }

        if (!syncFound)
            throw new ArgumentException($"{name} has no sync segment.", nameof(segments));
        LineMs = offset;
    }

    public SstvModeId Id { get; }
    public string Name { get; }
    public int VisCode { get; }
    public int Width { get; }
    public int Height { get; }
    public SstvColourSpace ColourSpace { get; }
    public IReadOnlyList<SstvSegment> Segments { get; }

    /// <summary>Image rows carried by one transmitted line (2 for PD, else 1).</summary>
    public int RowsPerLine { get; }

    /// <summary>Extra tone between the VIS stop bit and line 0 (Scottie sends a 9 ms sync).</summary>
    public double LeadInMs { get; }

    public double LineMs { get; }

    /// <summary>Where the line's sync pulse starts, measured from the start of the line.</summary>
    public double SyncOffsetMs { get; }

    public double SyncMs { get; }

    public int TransmittedLines => Height / RowsPerLine;

    public double ImageSeconds => (LeadInMs + LineMs * TransmittedLines) / 1000.0;

    public override string ToString() => Name;
}
