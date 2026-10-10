namespace OscarWatch.Core.Sstv;

/// <summary>Windowed-sinc low-pass taps (Blackman window, unity DC gain).</summary>
internal static class SstvFirFilter
{
    public static double[] LowPass(double cutoffHz, double sampleRate, int taps)
    {
        if (taps % 2 == 0)
            taps++;

        var h = new double[taps];
        var mid = (taps - 1) / 2.0;
        var fc = cutoffHz / sampleRate;
        var sum = 0.0;
        for (var i = 0; i < taps; i++)
        {
            var n = i - mid;
            var sinc = n == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * n) / (Math.PI * n);
            var window = 0.42
                - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1))
                + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = sinc * window;
            sum += h[i];
        }

        for (var i = 0; i < taps; i++)
            h[i] /= sum;
        return h;
    }
}
