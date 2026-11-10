namespace OscarWatch.Core.Sstv;

/// <summary>
/// Narrowband tone power on the search demodulator's I/Q. Below the FM threshold the
/// discriminator output is mostly clicks, but the energy of a steady tone still adds up
/// over a few milliseconds, so the VIS and sync search can lean on it in deep noise.
/// </summary>
internal static class SstvTonePower
{
    /// <summary>
    /// Tone power is summed coherently over blocks this long and the blocks added by power.
    /// 5 ms puts a tone 200 Hz away (1100 against 1300 Hz) on a null and still tolerates
    /// about 60 Hz of tuning error.
    /// </summary>
    public const double BlockMs = 5;

    /// <summary>
    /// Share of the subcarrier power in [<paramref name="from"/>, <paramref name="to"/>) at
    /// <paramref name="toneHz"/> (audio frequency): 1 for a clean tone, about 0.08 for noise
    /// with 5 ms blocks. NaN when the window is outside the buffer.
    /// </summary>
    public static double Fraction(SstvBasebandBuffer baseband, long from, long to, double toneHz, double blockMs = BlockMs)
    {
        from = Math.Max(from, baseband.Start);
        to = Math.Min(to, baseband.End);
        var length = (int)(to - from);
        if (length <= 0)
            return double.NaN;

        var blockLength = Math.Max(1, (int)Math.Round(blockMs * SstvDecoder.InternalRate / 1000));
        var blocks = Math.Max(1, (int)Math.Round(length / (double)blockLength));
        var omega = 2 * Math.PI * (toneHz - SstvFmDemodulator.CentreHz) / SstvDecoder.InternalRate;
        // Phase follows the absolute index, so adjoining windows use the same reference.
        var stepC = Math.Cos(-omega);
        var stepS = Math.Sin(-omega);
        var c = Math.Cos(-omega * from);
        var s = Math.Sin(-omega * from);
        double coherent = 0, power = 0;
        var at = from;
        for (var b = 0; b < blocks; b++)
        {
            var end = from + (long)length * (b + 1) / blocks;
            double re = 0, im = 0;
            var n = 0;
            for (; at < end; at++, n++)
            {
                double i = baseband.I(at), q = baseband.Q(at);
                re += i * c - q * s;
                im += i * s + q * c;
                power += i * i + q * q;
                var nc = c * stepC - s * stepS;
                s = c * stepS + s * stepC;
                c = nc;
            }

            if (n > 0)
                coherent += (re * re + im * im) / n;
        }

        return power <= 1e-12 ? 0 : coherent / power;
    }
}
