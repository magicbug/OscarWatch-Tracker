namespace OscarWatch.Core.Sstv;

/// <summary>
/// Audio offset left over when rig control tunes in steps. On SSB the tones move by the
/// gap between where the dial should be and where it is; on FM the discriminator
/// cancels a small carrier offset, so nothing needs removing.
/// </summary>
public static class SstvDopplerFeedForward
{
    /// <summary>Gaps larger than this mean the dial is somewhere else, not lagging Doppler.</summary>
    public const double MaxPlausibleGapHz = 1500;

    public const double MaxOffsetHz = 500;

    /// <param name="downlinkMode">Transponder downlink mode, for example "FM", "USB" or "LSB".</param>
    /// <param name="idealReceiveKHz">Doppler-corrected receive frequency the dial should be on.</param>
    /// <param name="rigReceiveHz">Receive frequency the rig was last set to.</param>
    /// <returns>Hz to subtract from the demodulated audio frequency.</returns>
    public static double AudioOffsetHz(string? downlinkMode, double? idealReceiveKHz, long? rigReceiveHz)
    {
        if (idealReceiveKHz is not { } ideal || rigReceiveHz is not { } rig || !double.IsFinite(ideal))
            return 0;

        var sign = SidebandSign(downlinkMode);
        if (sign == 0)
            return 0;

        var gap = ideal * 1000.0 - rig;
        if (Math.Abs(gap) > MaxPlausibleGapHz)
            return 0;

        return Math.Clamp(sign * gap, -MaxOffsetHz, MaxOffsetHz);
    }

    /// <summary>+1 for upper sideband (and data/digital modes on USB), −1 for lower, 0 for FM and anything else.</summary>
    public static int SidebandSign(string? mode)
    {
        var m = mode?.Trim().ToUpperInvariant() ?? "";
        if (m.StartsWith("USB", StringComparison.Ordinal) || m is "DATA-U" or "PKTUSB" or "SSB")
            return 1;
        if (m.StartsWith("LSB", StringComparison.Ordinal) || m is "DATA-L" or "PKTLSB")
            return -1;
        return 0;
    }
}
