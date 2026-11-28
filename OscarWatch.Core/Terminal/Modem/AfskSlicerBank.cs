namespace OscarWatch.Core.Terminal.Modem;

/// <summary>A candidate frame (FCS still attached) from one decoder path, with optional raw pre-NRZI demod samples.</summary>
public readonly record struct AfskCandidate(byte[]? FrameWithFcs, int Path, long Sample, float[]? RawDemod = null);

/// <summary>
/// Parallel decoder paths over shared band-pass filtering and one or more tone-offset correlator banks.
/// Each path has its own decision, clock recovery and HDLC state.
/// </summary>
public sealed class AfskSlicerBank
{
    private const int CorrelatorA = 0;
    private const int CorrelatorB = 1;
    private const int CorrelatorC = 2;

    private static readonly int[] BalancesADb = [-6, -4, -2, 0, 2, 4, 6, 8, 10, 12];
    private static readonly int[] BalancesBDb = [-3, 0, 3, 6];
    private static readonly int[] BalancesCDb = [-3, 0, 3, 6];

    private readonly AfskBandPassFilter _filter = new();
    private readonly ToneChannel[] _channels;
    private readonly Path[] _paths;
    private long _sample;

    public AfskSlicerBank(bool frequencyOffsetDecoders = true)
    {
        var channels = new List<ToneChannel>();
        foreach (var offset in AfskFrequencyOffsetDecoders.AllOffsetsHz(frequencyOffsetDecoders))
        {
            var tone = offset == 0
                ? new AfskToneDemodulator(
                    AfskFrequencyOffsetDecoders.MarkHz,
                    AfskFrequencyOffsetDecoders.SpaceHz,
                    [12, 10, 12],
                    [1600, 0, 950])
                : new AfskToneDemodulator(
                    AfskFrequencyOffsetDecoders.MarkHz + offset,
                    AfskFrequencyOffsetDecoders.SpaceHz + offset,
                    [12],
                    [1600]);
            channels.Add(new ToneChannel(offset, tone));
        }

        _channels = channels.ToArray();
        var paths = new List<Path>();
        foreach (var ch in _channels)
        {
            if (ch.OffsetHz == 0)
            {
                foreach (var db in BalancesADb)
                    paths.Add(new Path(ch, $"A{db:+0;-0;0}", PathKind.Balance, CorrelatorA, (float)Math.Pow(10, db / 20.0)));
                foreach (var db in BalancesBDb)
                    paths.Add(new Path(ch, $"B{db:+0;-0;0}", PathKind.Balance, CorrelatorB, (float)Math.Pow(10, db / 20.0)));
                foreach (var db in BalancesCDb)
                    paths.Add(new Path(ch, $"C{db:+0;-0;0}", PathKind.Balance, CorrelatorC, (float)Math.Pow(10, db / 20.0), lockedInertia: 0.85, searchingInertia: 0.55));
                paths.Add(new Path(ch, "AGC", PathKind.Agc, CorrelatorA, 1));
                paths.Add(new Path(ch, "D", PathKind.Discriminator, 0, 1));
            }
            else
            {
                var tag = $"F{ch.OffsetHz:+0;-0;0}";
                paths.Add(new Path(ch, $"{tag} A0", PathKind.Balance, CorrelatorA, 1));
                paths.Add(new Path(ch, $"{tag} A+6", PathKind.Balance, CorrelatorA, (float)Math.Pow(10, 6 / 20.0)));
                paths.Add(new Path(ch, $"{tag} AGC", PathKind.Agc, CorrelatorA, 1));
                paths.Add(new Path(ch, $"{tag} D", PathKind.Discriminator, 0, 1));
            }
        }

        _paths = paths.ToArray();
    }

    /// <summary>Paths on the centre (0 Hz offset) bank; used as the quality percentage denominator.</summary>
    public int QualityPathCount { get; } = BalancesADb.Length + BalancesBDb.Length + BalancesCDb.Length + 2;

    public int PathCount => _paths.Length;

    public string PathName(int path) => _paths[path].Name;

    /// <summary>True while any path holds DCD.</summary>
    public bool Dcd
    {
        get
        {
            foreach (var p in _paths)
            {
                if (p.Hdlc.Dcd)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Samples processed at 12 kHz since construction (not cleared by <see cref="Reset"/>).</summary>
    public long Sample => _sample;

    public float Filtered => _filter.Last;

    public void Reset()
    {
        _filter.Reset();
        foreach (var ch in _channels)
            ch.Tone.Reset();
        foreach (var p in _paths)
        {
            p.Clock.Reset();
            p.Hdlc.Reset();
        }
    }

    /// <summary>Counts a sample without decoding it (receive blanked while transmitting).</summary>
    public void Skip(int samples) => _sample += samples;

    public void Process(float sample, List<AfskCandidate> output)
    {
        var x = _filter.Process(sample);
        foreach (var ch in _channels)
            ch.Tone.ProcessFiltered(x);

        _sample++;
        for (var i = 0; i < _paths.Length; i++)
        {
            var p = _paths[i];
            var tone = p.Channel.Tone;
            var demod = p.Kind switch
            {
                PathKind.Balance => tone.MarkAmplitude(p.Correlator) - tone.SpaceAmplitude(p.Correlator) * p.SpaceGain,
                PathKind.Agc => tone.AgcOutput(p.Correlator),
                _ => tone.Discriminator
            };

            var bit = p.Clock.Push(demod, p.Hdlc.Dcd);
            if (bit < 0)
                continue;
            var frame = p.Hdlc.PushBit(bit == 1, p.Clock.LastSampledDemod, out var rawDemod);
            if (frame is not null || rawDemod is not null)
                output.Add(new AfskCandidate(frame, i, _sample, rawDemod));
        }
    }

    private enum PathKind
    {
        Balance,
        Agc,
        Discriminator
    }

    private sealed class ToneChannel(int offsetHz, AfskToneDemodulator tone)
    {
        public int OffsetHz { get; } = offsetHz;
        public AfskToneDemodulator Tone { get; } = tone;
    }

    private sealed class Path
    {
        public Path(
            ToneChannel channel,
            string name,
            PathKind kind,
            int correlator,
            float spaceGain,
            double lockedInertia = AfskClockRecovery.LockedInertia,
            double searchingInertia = AfskClockRecovery.SearchingInertia)
        {
            Channel = channel;
            Name = name;
            Kind = kind;
            Correlator = correlator;
            SpaceGain = spaceGain;
            Clock = new AfskClockRecovery(AfskDemodulator.SampleRate, lockedInertia: lockedInertia, searchingInertia: searchingInertia);
        }

        public ToneChannel Channel { get; }
        public string Name { get; }
        public PathKind Kind { get; }
        public int Correlator { get; }
        public float SpaceGain { get; }
        public AfskClockRecovery Clock { get; }
        public HdlcDecoder Hdlc { get; } = new();
    }
}
