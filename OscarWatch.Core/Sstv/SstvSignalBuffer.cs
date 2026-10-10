namespace OscarWatch.Core.Sstv;

/// <summary>
/// Demodulated frequency samples addressed by absolute sample index, so positions stay
/// valid after old samples are trimmed. Keeps a running sum for fast window means.
/// </summary>
public sealed class SstvSignalBuffer
{
    /// <summary>Squares are taken about this value to keep the running sum small.</summary>
    private const double SquareCentre = 1900;

    private float[] _data = new float[1 << 16];
    private double[] _cumulative = new double[(1 << 16) + 1];
    private double[] _cumulativeSquares = new double[(1 << 16) + 1];
    private int _count;

    /// <summary>Absolute index of the first retained sample.</summary>
    public long Start { get; private set; }

    /// <summary>Absolute index one past the newest sample.</summary>
    public long End => Start + _count;

    public float this[long index] => _data[index - Start];

    public bool Contains(long start, long end) => start >= Start && end <= End && end >= start;

    public void Append(IReadOnlyList<float> samples)
    {
        EnsureCapacity(_count + samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var value = samples[i];
            _data[_count] = value;
            _cumulative[_count + 1] = _cumulative[_count] + value;
            var d = value - SquareCentre;
            _cumulativeSquares[_count + 1] = _cumulativeSquares[_count] + d * d;
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
        return (_cumulative[b] - _cumulative[a]) / (b - a);
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
        Array.Copy(_data, drop, _data, 0, keep);
        var baseSum = _cumulative[drop];
        var baseSquares = _cumulativeSquares[drop];
        for (var i = 0; i <= keep; i++)
        {
            _cumulative[i] = _cumulative[i + drop] - baseSum;
            _cumulativeSquares[i] = _cumulativeSquares[i + drop] - baseSquares;
        }
        _count = keep;
        Start += drop;
    }

    public void Clear()
    {
        Start = End;
        _count = 0;
        _cumulative[0] = 0;
        _cumulativeSquares[0] = 0;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _data.Length)
            return;

        var size = _data.Length;
        while (size < needed)
            size *= 2;
        Array.Resize(ref _data, size);
        Array.Resize(ref _cumulative, size + 1);
        Array.Resize(ref _cumulativeSquares, size + 1);
    }
}
