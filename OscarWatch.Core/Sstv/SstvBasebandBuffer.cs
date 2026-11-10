namespace OscarWatch.Core.Sstv;

/// <summary>
/// Filtered I/Q from <see cref="SstvFmDemodulator"/>, addressed by the same absolute sample
/// index as the frequency samples in <see cref="SstvSignalBuffer"/>.
/// </summary>
public sealed class SstvBasebandBuffer
{
    private float[] _i = new float[1 << 16];
    private float[] _q = new float[1 << 16];
    private int _count;

    public long Start { get; private set; }

    public long End => Start + _count;

    internal int Capacity => _i.Length;

    public float I(long index) => _i[index - Start];

    public float Q(long index) => _q[index - Start];

    public void Add(float i, float q)
    {
        if (_count == _i.Length)
        {
            Array.Resize(ref _i, _i.Length * 2);
            Array.Resize(ref _q, _q.Length * 2);
        }

        _i[_count] = i;
        _q[_count] = q;
        _count++;
    }

    public void TrimBefore(long absoluteIndex)
    {
        var drop = (int)Math.Clamp(absoluteIndex - Start, 0, _count);
        if (drop == 0)
            return;
        _count -= drop;
        Start += drop;
        var capacity = SstvBufferCapacity.After(_count, _i.Length);
        if (capacity == _i.Length)
        {
            Array.Copy(_i, drop, _i, 0, _count);
            Array.Copy(_q, drop, _q, 0, _count);
            return;
        }

        var smallerI = new float[capacity];
        var smallerQ = new float[capacity];
        Array.Copy(_i, drop, smallerI, 0, _count);
        Array.Copy(_q, drop, smallerQ, 0, _count);
        _i = smallerI;
        _q = smallerQ;
    }
}
