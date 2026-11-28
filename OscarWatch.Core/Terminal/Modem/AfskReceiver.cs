namespace OscarWatch.Core.Terminal.Modem;

/// <summary>A decoded AX.25 frame (no FCS) and where it came from.</summary>
public sealed record AfskDecodedFrame(
    byte[] Ax25,
    long EndSample,
    string DecoderName,
    int Quality,
    bool Fixed)
{
    /// <summary>Seconds of input (at the demodulator rate) before the closing flag.</summary>
    public double EndSeconds => EndSample / (double)AfskDemodulator.SampleRate;
}

/// <summary>
/// Complete 1200 baud AFSK receiver: resample to 12 kHz, run the decoder bank, check the FCS, merge the
/// same frame decoded by several paths, and repair a frame no path decoded cleanly when bit fixing is on.
/// <see cref="AfskDecodedFrame.Quality"/> is the percentage of centre-bank paths that decoded the frame cleanly.
/// Not thread safe; feed it from one thread.
/// </summary>
public sealed class AfskReceiver
{
    /// <summary>Candidates closing within this many samples (20 ms) are the same transmission.</summary>
    private const int ClusterSamples = 240;

    /// <summary>The same frame again within one second is a duplicate from another path, not a repeat.</summary>
    private const int DuplicateSamples = AfskDemodulator.SampleRate;

    private const int DcdHoldSamples = AfskDemodulator.SampleRate / 10;
    private const int MaxFixAttemptsPerCluster = 12;

    private readonly PolyphaseResampler _resampler;
    private readonly AfskSlicerBank _bank;
    private readonly List<float> _resampled = new(4096);
    private readonly List<AfskCandidate> _candidates = new();
    private readonly List<(ulong Hash, long Sample)> _recent = new();
    private readonly int[] _pathCounts;
    private long _lastDcdSample = long.MinValue / 2;
    private float _peak;
    private bool _blanked;

    public AfskReceiver(int inputSampleRate, AfskBitFixMode bitFix = AfskBitFixMode.SingleBit, bool frequencyOffsetDecoders = true)
    {
        InputSampleRate = inputSampleRate;
        _resampler = new PolyphaseResampler(inputSampleRate, AfskDemodulator.SampleRate);
        BitFix = bitFix;
        FrequencyOffsetDecoders = frequencyOffsetDecoders;
        _bank = new AfskSlicerBank(frequencyOffsetDecoders);
        _pathCounts = new int[_bank.PathCount];
    }

    public int InputSampleRate { get; }
    public AfskBitFixMode BitFix { get; set; }
    public bool FrequencyOffsetDecoders { get; }

    public int PathCount => _bank.PathCount;
    public string PathName(int path) => _bank.PathName(path);

    /// <summary>Frames each path was first or joint decoder of.</summary>
    public int PathDecodeCount(int path) => _pathCounts[path];

    public int FrameCount { get; private set; }
    public int FixedCount { get; private set; }

    /// <summary>Samples at 12 kHz since construction.</summary>
    public long Sample => _bank.Sample;

    /// <summary>Data carrier detect, held for 100 ms after the last path dropped it.</summary>
    public bool Dcd => _bank.Sample - _lastDcdSample <= DcdHoldSamples;

    /// <summary>Recent input peak (0 to 1), falling slowly.</summary>
    public float InputPeak => _peak;

    /// <summary>While true the input is counted but not decoded (half duplex, while the transmitter is keyed).</summary>
    public bool Blanked
    {
        get => _blanked;
        set
        {
            if (_blanked == value)
                return;
            _blanked = value;
            if (!value)
                _bank.Reset();
        }
    }

    /// <summary>Clears demodulator, clock and HDLC state; counters are kept.</summary>
    public void Reset()
    {
        _resampler.Reset();
        _bank.Reset();
        _candidates.Clear();
    }

    public void Process(ReadOnlySpan<float> input, List<AfskDecodedFrame> output)
    {
        var peak = _peak;
        foreach (var s in input)
        {
            var a = Math.Abs(s);
            peak = a > peak ? a : peak * 0.99995f;
        }

        _peak = peak;

        _resampled.Clear();
        _resampler.Process(input, _resampled);
        if (_blanked)
        {
            _bank.Skip(_resampled.Count);
        }
        else
        {
            foreach (var s in _resampled)
            {
                _bank.Process(s, _candidates);
                if (_bank.Dcd)
                    _lastDcdSample = _bank.Sample;
            }
        }

        Resolve(output, flush: false);
    }

    /// <summary>Emits anything still waiting for other paths (end of a file).</summary>
    public void Flush(List<AfskDecodedFrame> output) => Resolve(output, flush: true);

    /// <summary>Decodes a whole buffer, for recordings and tests.</summary>
    public static List<AfskDecodedFrame> DecodeAll(
        ReadOnlySpan<float> samples,
        int sampleRate,
        AfskBitFixMode bitFix = AfskBitFixMode.SingleBit,
        bool frequencyOffsetDecoders = true)
    {
        var receiver = new AfskReceiver(sampleRate, bitFix, frequencyOffsetDecoders);
        var frames = new List<AfskDecodedFrame>();
        const int block = 4096;
        for (var i = 0; i < samples.Length; i += block)
            receiver.Process(samples.Slice(i, Math.Min(block, samples.Length - i)), frames);

        // Push the tail through the filters before flushing.
        receiver.Process(new float[sampleRate / 10], frames);
        receiver.Flush(frames);
        return frames;
    }

    private void Resolve(List<AfskDecodedFrame> output, bool flush)
    {
        var now = _bank.Sample;
        _recent.RemoveAll(r => now - r.Sample > DuplicateSamples);

        while (_candidates.Count > 0)
        {
            var first = _candidates[0].Sample;
            if (!flush && now - first <= ClusterSamples * 2)
                break;

            var end = 0;
            while (end < _candidates.Count && _candidates[end].Sample - first <= ClusterSamples)
                end++;

            ResolveCluster(_candidates.GetRange(0, end), output);
            _candidates.RemoveRange(0, end);
        }
    }

    private void ResolveCluster(List<AfskCandidate> cluster, List<AfskDecodedFrame> output)
    {
        var good = new List<(ulong Hash, byte[] Ax25, long Sample, List<int> Paths)>();
        foreach (var c in cluster)
        {
            if (!Fcs16.Check(c.FrameWithFcs))
                continue;
            var ax25 = c.FrameWithFcs.AsSpan(0, c.FrameWithFcs.Length - 2);
            if (!Ax25Frame.TryParse(ax25, out _))
                continue;

            var hash = Hash(ax25);
            var existing = good.FindIndex(g => g.Hash == hash);
            if (existing >= 0)
            {
                if (!good[existing].Paths.Contains(c.Path))
                    good[existing].Paths.Add(c.Path);
            }
            else
            {
                good.Add((hash, ax25.ToArray(), c.Sample, [c.Path]));
            }
        }

        if (good.Count > 0)
        {
            foreach (var g in good)
            {
                if (IsRecent(g.Hash))
                    continue;
                _recent.Add((g.Hash, g.Sample));
                foreach (var p in g.Paths)
                    _pathCounts[p]++;
                FrameCount++;
                var centrePaths = g.Paths.Count(p => p < _bank.QualityPathCount);
                var quality = centrePaths > 0
                    ? (int)Math.Round(100.0 * centrePaths / _bank.QualityPathCount)
                    : (int)Math.Round(100.0 * g.Paths.Count / 3.0);
                quality = Math.Min(100, quality);
                output.Add(new AfskDecodedFrame(g.Ax25, g.Sample, _bank.PathName(g.Paths[0]), quality, Fixed: false));
            }

            return;
        }

        if (BitFix == AfskBitFixMode.Off)
            return;

        // Rank distinct candidate frames by how many paths agreed on them (tie-breaking toward centre-bank paths)
        // so the highest-confidence near-miss is always attempted first rather than extreme-twist edge paths.
        var ranked = new List<(ulong Hash, AfskCandidate Representative, int Votes, bool HasCentre)>();
        foreach (var c in cluster)
        {
            var h = Hash(c.FrameWithFcs);
            var isCentre = c.Path < _bank.QualityPathCount;
            var existing = ranked.FindIndex(r => r.Hash == h);
            if (existing >= 0)
            {
                var item = ranked[existing];
                ranked[existing] = (item.Hash, item.Representative, item.Votes + 1, item.HasCentre || isCentre);
            }
            else
            {
                ranked.Add((h, c, 1, isCentre));
            }
        }

        ranked.Sort((a, b) =>
        {
            var byVotes = b.Votes.CompareTo(a.Votes);
            if (byVotes != 0)
                return byVotes;
            return b.HasCentre.CompareTo(a.HasCentre);
        });

        var attempts = Math.Min(ranked.Count, MaxFixAttemptsPerCluster);
        for (var i = 0; i < attempts; i++)
        {
            var c = ranked[i].Representative;
            if (FrameBitFixer.TryFix(c.FrameWithFcs, BitFix) is not { } repaired)
                continue;

            var hash = Hash(repaired);
            if (IsRecent(hash))
                return;
            _recent.Add((hash, c.Sample));
            _pathCounts[c.Path]++;
            FrameCount++;
            FixedCount++;
            output.Add(new AfskDecodedFrame(repaired, c.Sample, _bank.PathName(c.Path) + " fixed", 0, Fixed: true));
            return;
        }
    }

    private bool IsRecent(ulong hash)
    {
        foreach (var r in _recent)
        {
            if (r.Hash == hash)
                return true;
        }

        return false;
    }

    private static ulong Hash(ReadOnlySpan<byte> data)
    {
        var h = 14695981039346656037UL;
        foreach (var b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }

        return h ^ (ulong)data.Length;
    }
}
