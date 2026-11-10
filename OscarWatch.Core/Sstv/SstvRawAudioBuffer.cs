namespace OscarWatch.Core.Sstv;

/// <summary>
/// Resampled audio addressed by absolute sample index, kept so a picture can be demodulated again with a
/// filter matched to its mode. It is only ever read sample by sample, so unlike <see cref="SstvSignalBuffer"/>
/// it keeps no running sums: 4 bytes a sample instead of 40.
/// </summary>
internal sealed class SstvRawAudioBuffer
{
    private float[] _data = new float[SstvBufferCapacity.Minimum];
    private int _count;

    /// <summary>Absolute index of the first retained sample.</summary>
    public long Start { get; private set; }

    /// <summary>Absolute index one past the newest sample.</summary>
    public long End => Start + _count;

    public float this[long index] => _data[index - Start];

    internal int Capacity => _data.Length;

    public void Append(IReadOnlyList<float> samples)
    {
        var needed = _count + samples.Count;
        if (needed > _data.Length)
        {
            var size = _data.Length;
            while (size < needed)
                size *= 2;
            Array.Resize(ref _data, size);
        }

        for (var i = 0; i < samples.Count; i++)
            _data[_count + i] = samples[i];
        _count += samples.Count;
    }

    /// <summary>Drop samples older than <paramref name="absoluteIndex"/>, and give back memory that is no longer needed.</summary>
    public void TrimBefore(long absoluteIndex)
    {
        var drop = (int)Math.Clamp(absoluteIndex - Start, 0, _count);
        if (drop == 0)
            return;

        _count -= drop;
        Start += drop;
        var capacity = SstvBufferCapacity.After(_count, _data.Length);
        if (capacity == _data.Length)
        {
            Array.Copy(_data, drop, _data, 0, _count);
            return;
        }

        var smaller = new float[capacity];
        Array.Copy(_data, drop, smaller, 0, _count);
        _data = smaller;
    }
}
