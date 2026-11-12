namespace OscarWatch.Core.Sstv;

/// <summary>
/// Demodulated frequency samples addressed by absolute sample index, so positions stay
/// valid after old samples are trimmed. Keeps a running sum for fast window means.
/// Each sample may carry a weight (how much it can be trusted); window means use the
/// weights when <see cref="Weighted"/> is set.
/// </summary>
public sealed class SstvSignalBuffer
{
    /// <summary>Squares are taken about this value to keep the running sum small.</summary>
    private const double SquareCentre = 1900;

    private float[] _data = new float[1 << 16];
    private float[] _weight = new float[1 << 16];
    private double[] _cumulative = new double[(1 << 16) + 1];
    private double[] _cumulativeSquares = new double[(1 << 16) + 1];
    private double[] _cumulativeWeight = new double[(1 << 16) + 1];
    private double[] _cumulativeWeightSquares = new double[(1 << 16) + 1];
    private double[] _cumulativeWeighted = new double[(1 << 16) + 1];
    private int _count;

    /// <summary>Absolute index of the first retained sample.</summary>
    public long Start { get; private set; }

    /// <summary>Absolute index one past the newest sample.</summary>
    public long End => Start + _count;

    /// <summary>When true, window means are weighted by each sample's weight.</summary>
    public bool Weighted { get; set; }

    public float this[long index] => _data[index - Start];

    /// <summary>Weight of a sample: 1 when weighting is off.</summary>
    public float Weight(long index) => Weighted ? _weight[index - Start] : 1f;

    /// <summary>Stored weight of a sample, whether or not <see cref="Weighted"/> is set.</summary>
    public float StoredWeight(long index) => _weight[index - Start];

    /// <summary>Mean stored weight over [start, end), whether or not <see cref="Weighted"/> is set.</summary>
    public double MeanStoredWeight(long start, long end)
    {
        var a = Math.Max(start, Start) - Start;
        var b = Math.Min(end, End) - Start;
        if (b <= a)
            return double.NaN;
        return (_cumulativeWeight[b] - _cumulativeWeight[a]) / (b - a);
    }

    /// <summary>
    /// Standard deviation of the stored weights over [start, end) divided by their mean. The SSTV
    /// subcarrier has a constant envelope, so this is near 0 on a clean signal, whatever the picture,
    /// about 0.25 at 6 dB carrier to noise and about 0.8 on noise alone.
    /// </summary>
    public double WeightVariation(long start, long end)
    {
        var a = Math.Max(start, Start) - Start;
        var b = Math.Min(end, End) - Start;
        if (b <= a)
            return double.NaN;
        var n = b - a;
        var mean = (_cumulativeWeight[b] - _cumulativeWeight[a]) / n;
        if (mean <= 1e-9)
            return double.NaN;
        var meanSquare = (_cumulativeWeightSquares[b] - _cumulativeWeightSquares[a]) / n;
        return Math.Sqrt(Math.Max(0, meanSquare - mean * mean)) / mean;
    }

    internal int Capacity => _data.Length;

    public bool Contains(long start, long end) => start >= Start && end <= End && end >= start;

    public void Append(IReadOnlyList<float> samples) => Append(samples, null);

    /// <param name="weights">Weight per sample, or null for all 1.</param>
    public void Append(IReadOnlyList<float> samples, IReadOnlyList<float>? weights)
    {
        EnsureCapacity(_count + samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var value = samples[i];
            var w = weights is null ? 1f : weights[i];
            _data[_count] = value;
            _weight[_count] = w;
            _cumulative[_count + 1] = _cumulative[_count] + value;
            var d = value - SquareCentre;
            _cumulativeSquares[_count + 1] = _cumulativeSquares[_count] + d * d;
            _cumulativeWeight[_count + 1] = _cumulativeWeight[_count] + w;
            _cumulativeWeightSquares[_count + 1] = _cumulativeWeightSquares[_count] + (double)w * w;
            _cumulativeWeighted[_count + 1] = _cumulativeWeighted[_count] + w * (value - SquareCentre);
            _count++;
        }
    }

    /// <summary>Mean over [start, end). Clipped to the retained range.</summary>
    public double Mean(long start, long end)
    {
        var a = Math.Max(start, Start) - Start;
        var b = Math.Min(end, End) - Start;
        if (b <= a)
            return double.NaN;
        if (Weighted)
        {
            var w = _cumulativeWeight[b] - _cumulativeWeight[a];
            if (w > 1e-9)
                return SquareCentre + (_cumulativeWeighted[b] - _cumulativeWeighted[a]) / w;
        }

        return (_cumulative[b] - _cumulative[a]) / (b - a);
    }

    /// <summary>Mean weight over [start, end): 1 when weighting is off.</summary>
    public double MeanWeight(long start, long end)
    {
        var a = Math.Max(start, Start) - Start;
        var b = Math.Min(end, End) - Start;
        if (b <= a)
            return double.NaN;
        return Weighted ? (_cumulativeWeight[b] - _cumulativeWeight[a]) / (b - a) : 1;
    }

    /// <summary>Standard deviation over [start, end): low on a clean tone, high on noise.</summary>
    public double StdDev(long start, long end)
    {
        var a = Math.Max(start, Start) - Start;
        var b = Math.Min(end, End) - Start;
        if (b <= a)
            return double.NaN;
        var n = b - a;
        var mean = (_cumulative[b] - _cumulative[a]) / n - SquareCentre;
        var meanSquare = (_cumulativeSquares[b] - _cumulativeSquares[a]) / n;
        return Math.Sqrt(Math.Max(0, meanSquare - mean * mean));
    }

    /// <summary>Mean over a fractional window, used for pixel averaging.</summary>
    public double Mean(double start, double end)
    {
        var a = (long)Math.Floor(start);
        var b = (long)Math.Ceiling(end);
        if (b <= a)
            b = a + 1;
        return Mean(a, b);
    }

    /// <summary>Drop samples older than <paramref name="absoluteIndex"/>.</summary>
    public void TrimBefore(long absoluteIndex)
    {
        var drop = (int)Math.Clamp(absoluteIndex - Start, 0, _count);
        if (drop == 0)
            return;

        var keep = _count - drop;
        var baseSum = _cumulative[drop];
        var baseSquares = _cumulativeSquares[drop];
        var baseWeight = _cumulativeWeight[drop];
        var baseWeightSquares = _cumulativeWeightSquares[drop];
        var baseWeighted = _cumulativeWeighted[drop];

        // A buffer that was grown for a long picture and is now mostly empty is rebuilt smaller, so the
        // decoder does not hold the memory of its longest picture for as long as it lives.
        var capacity = SstvBufferCapacity.After(keep, _data.Length);
        var data = capacity == _data.Length ? _data : new float[capacity];
        var weight = capacity == _data.Length ? _weight : new float[capacity];
        var cumulative = capacity == _data.Length ? _cumulative : new double[capacity + 1];
        var squares = capacity == _data.Length ? _cumulativeSquares : new double[capacity + 1];
        var cumulativeWeight = capacity == _data.Length ? _cumulativeWeight : new double[capacity + 1];
        var weightSquares = capacity == _data.Length ? _cumulativeWeightSquares : new double[capacity + 1];
        var weighted = capacity == _data.Length ? _cumulativeWeighted : new double[capacity + 1];

        Array.Copy(_data, drop, data, 0, keep);
        Array.Copy(_weight, drop, weight, 0, keep);
        for (var i = 0; i <= keep; i++)
        {
            cumulative[i] = _cumulative[i + drop] - baseSum;
            squares[i] = _cumulativeSquares[i + drop] - baseSquares;
            cumulativeWeight[i] = _cumulativeWeight[i + drop] - baseWeight;
            weightSquares[i] = _cumulativeWeightSquares[i + drop] - baseWeightSquares;
            weighted[i] = _cumulativeWeighted[i + drop] - baseWeighted;
        }

        _data = data;
        _weight = weight;
        _cumulative = cumulative;
        _cumulativeSquares = squares;
        _cumulativeWeight = cumulativeWeight;
        _cumulativeWeightSquares = weightSquares;
        _cumulativeWeighted = weighted;
        _count = keep;
        Start += drop;
    }

    public void Clear()
    {
        Start = End;
        _count = 0;
        _cumulative[0] = 0;
        _cumulativeSquares[0] = 0;
        _cumulativeWeight[0] = 0;
        _cumulativeWeightSquares[0] = 0;
        _cumulativeWeighted[0] = 0;
    }

    /// <summary>Empty the buffer and number the next sample <paramref name="absoluteIndex"/>.</summary>
    public void ResetTo(long absoluteIndex)
    {
        Clear();
        Start = absoluteIndex;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _data.Length)
            return;

        var size = _data.Length;
        while (size < needed)
            size *= 2;
        Array.Resize(ref _data, size);
        Array.Resize(ref _weight, size);
        Array.Resize(ref _cumulative, size + 1);
        Array.Resize(ref _cumulativeSquares, size + 1);
        Array.Resize(ref _cumulativeWeight, size + 1);
        Array.Resize(ref _cumulativeWeightSquares, size + 1);
        Array.Resize(ref _cumulativeWeighted, size + 1);
    }
}
