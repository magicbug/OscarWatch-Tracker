using OscarWatch.Core.Sstv;

namespace OscarWatch.Tests.Sstv;

/// <summary>Test-only SSTV transmitter, so decoder tests do not need recordings.</summary>
internal static class SstvTestEncoder
{
    public sealed class Settings
    {
        /// <summary>Sender clock: every duration is multiplied by this (1.005 runs 0.5% slow).</summary>
        public double ClockRatio { get; init; } = 1.0;
        /// <summary>Audio offset in Hz added to every tone (an off-tune receiver).</summary>
        public double OffsetHz { get; init; }
        /// <summary>Offset change in Hz per second (Doppler drift on SSB audio).</summary>
        public double DriftHzPerSecond { get; init; }
        /// <summary>Gaussian noise RMS. Tones have amplitude 0.5.</summary>
        public double NoiseRms { get; init; }
        public int Seed { get; init; } = 1234;
        public bool IncludeVis { get; init; } = true;
        /// <summary>Overrides the VIS code sent, for example to send a bad parity.</summary>
        public int? VisCodeOverride { get; init; }
        public bool FlipParity { get; init; }
        public double LeadSeconds { get; init; } = 0.5;
        public double TailSeconds { get; init; } = 1.0;
    }

    public delegate (double R, double G, double B) PixelSource(int x, int y);

    /// <summary>Smooth pattern with four horizontal cycles, so position errors show and blur does not.</summary>
    public static (double R, double G, double B) TestPattern(int x, int y, int width, int height)
    {
        var r = 127.5 + 110 * Math.Sin(2 * Math.PI * 4 * x / width);
        var g = 20 + 215.0 * y / Math.Max(1, height - 1);
        var b = 127.5 + 100 * Math.Cos(2 * Math.PI * 3 * x / width) * Math.Cos(2 * Math.PI * y / height);
        return (r, g, b);
    }

    public static float[] Encode(SstvMode mode, int rate, Settings? settings = null)
    {
        settings ??= new Settings();
        return Encode(mode, (x, y) => TestPattern(x, y, mode.Width, mode.Height), rate, settings);
    }

    public static float[] Encode(SstvMode mode, PixelSource pixel, int rate, Settings settings)
    {
        var tone = new ToneWriter(rate, settings);
        tone.Silence(settings.LeadSeconds);

        if (settings.IncludeVis)
        {
            tone.Add(1900, 300);
            tone.Add(1200, 10);
            tone.Add(1900, 300);
            tone.Add(1200, 30);
            var code = settings.VisCodeOverride ?? mode.VisCode;
            var ones = 0;
            for (var i = 0; i < 7; i++)
            {
                var bit = (code >> i) & 1;
                ones += bit;
                tone.Add(bit == 1 ? 1100 : 1300, 30);
            }

            var parity = (ones & 1) ^ (settings.FlipParity ? 1 : 0);
            tone.Add(parity == 1 ? 1100 : 1300, 30);
            tone.Add(1200, 30);
        }

        if (mode.LeadInMs > 0)
            tone.Add(1200, mode.LeadInMs);

        for (var line = 0; line < mode.TransmittedLines; line++)
            EncodeLine(mode, pixel, line, tone);

        tone.Silence(settings.TailSeconds);
        return tone.ToArray();
    }

    private static void EncodeLine(SstvMode mode, PixelSource pixel, int line, ToneWriter tone)
    {
        var row = line * mode.RowsPerLine;
        var segments = mode.Segments;
        for (var s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            switch (seg.Channel)
            {
                case SstvChannel.Sync:
                    tone.Add(1200, seg.Ms);
                    break;
                case SstvChannel.Gap:
                    tone.Add(GapHz(mode, segments, s, line), seg.Ms);
                    break;
                default:
                    var values = new double[mode.Width];
                    for (var x = 0; x < mode.Width; x++)
                        values[x] = ChannelValue(mode, pixel, seg.Channel, x, row, line);
                    tone.Scan(values, seg.Ms);
                    break;
            }
        }
    }

    private static double GapHz(SstvMode mode, IReadOnlyList<SstvSegment> segments, int index, int line)
    {
        if (mode.Id is not (SstvModeId.Robot36 or SstvModeId.Robot72))
            return 1500;

        var seg = segments[index];
        if (seg.Ms == 1.5)
            return 1900;
        if (seg.Ms != 4.5)
            return 1500;

        var next = segments[index + 2].Channel;
        var blue = next == SstvChannel.BMinusY
            || (next == SstvChannel.AlternatingChroma && line % 2 == 1);
        return blue ? 2300 : 1500;
    }

    private static double ChannelValue(SstvMode mode, PixelSource pixel, SstvChannel channel, int x, int row, int line)
    {
        (double R, double G, double B) Px(int y) => pixel(x, Math.Clamp(y, 0, mode.Height - 1));

        switch (channel)
        {
            case SstvChannel.Red: return Px(row).R;
            case SstvChannel.Green: return Px(row).G;
            case SstvChannel.Blue: return Px(row).B;
            case SstvChannel.Y0: return Luma(Px(row));
            case SstvChannel.Y1: return Luma(Px(row + 1));
            case SstvChannel.RMinusY:
                return mode.RowsPerLine == 2 ? (Cr(Px(row)) + Cr(Px(row + 1))) / 2 : Cr(Px(row));
            case SstvChannel.BMinusY:
                return mode.RowsPerLine == 2 ? (Cb(Px(row)) + Cb(Px(row + 1))) / 2 : Cb(Px(row));
            case SstvChannel.AlternatingChroma:
                return line % 2 == 0
                    ? (Cr(Px(row)) + Cr(Px(row + 1))) / 2
                    : (Cb(Px(row - 1)) + Cb(Px(row))) / 2;
            default:
                return 0;
        }
    }

    public static double Luma((double R, double G, double B) p) => 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
    public static double Cb((double R, double G, double B) p) => 128 - 0.168736 * p.R - 0.331264 * p.G + 0.5 * p.B;
    public static double Cr((double R, double G, double B) p) => 128 + 0.5 * p.R - 0.418688 * p.G - 0.081312 * p.B;

    private sealed class ToneWriter(int rate, Settings settings)
    {
        private readonly List<float> _samples = new();
        private readonly Random _random = new(settings.Seed);
        private double _phase;
        private double _timeMs;

        public void Silence(double seconds)
        {
            var n = (int)(seconds * rate);
            for (var i = 0; i < n; i++)
                _samples.Add((float)Noise());
        }

        public void Add(double hz, double ms) => Generate(ms, _ => hz);

        /// <summary>One tone per pixel, 1500 Hz black to 2300 Hz white.</summary>
        public void Scan(double[] values, double ms)
        {
            var w = values.Length;
            Generate(ms, fraction =>
            {
                var x = Math.Clamp((int)(fraction * w), 0, w - 1);
                return 1500 + Math.Clamp(values[x], 0, 255) / 255.0 * 800;
            });
        }

        /// <summary>Emit samples for a span of sender time, keeping the phase continuous.</summary>
        private void Generate(double ms, Func<double, double> hzAt)
        {
            var startMs = _timeMs;
            var endMs = _timeMs + ms * settings.ClockRatio;
            var firstSample = (long)Math.Ceiling(startMs * rate / 1000.0);
            var lastSample = (long)Math.Ceiling(endMs * rate / 1000.0);
            for (var n = firstSample; n < lastSample; n++)
            {
                var tMs = n * 1000.0 / rate;
                var fraction = (tMs - startMs) / (endMs - startMs);
                var seconds = tMs / 1000.0;
                var hz = hzAt(fraction) + settings.OffsetHz + settings.DriftHzPerSecond * seconds;
                _phase += 2 * Math.PI * hz / rate;
                if (_phase > 2 * Math.PI)
                    _phase -= 2 * Math.PI;
                _samples.Add((float)(0.5 * Math.Sin(_phase) + Noise()));
            }

            _timeMs = endMs;
        }

        private double Noise()
        {
            if (settings.NoiseRms <= 0)
                return 0;
            var u1 = 1.0 - _random.NextDouble();
            var u2 = _random.NextDouble();
            return settings.NoiseRms * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        public float[] ToArray() => _samples.ToArray();
    }
}
