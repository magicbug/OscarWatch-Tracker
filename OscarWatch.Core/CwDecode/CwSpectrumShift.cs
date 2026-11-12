namespace OscarWatch.Core.CwDecode;

/// <summary>
/// Moves a spectrum row along the frequency axis, so the CW waterfall can show the audio after
/// the rig control correction rather than the sawtooth left by stepped Doppler tuning.
/// </summary>
public static class CwSpectrumShift
{
    /// <param name="row">Power in dB per bin, bin 0 at the lowest frequency.</param>
    /// <param name="shiftHz">Hertz added to every frequency. Negative moves the row down.</param>
    /// <returns>A new row; bins that come from outside the original are filled from its nearest edge.</returns>
    public static float[] Shift(float[] row, double shiftHz, double binHz)
    {
        var shifted = new float[row.Length];
        if (row.Length == 0)
            return shifted;

        var bins = shiftHz / binHz;
        var last = row.Length - 1;
        for (var i = 0; i < row.Length; i++)
        {
            var source = Math.Clamp(i - bins, 0, last);
            var lo = (int)Math.Floor(source);
            var hi = Math.Min(last, lo + 1);
            var t = (float)(source - lo);
            shifted[i] = row[lo] + (row[hi] - row[lo]) * t;
        }

        return shifted;
    }
}
