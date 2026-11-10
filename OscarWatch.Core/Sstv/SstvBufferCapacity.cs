namespace OscarWatch.Core.Sstv;

/// <summary>
/// When the SSTV sample buffers give back memory. They grow by doubling while a long picture is
/// decoded and are then trimmed, but a trimmed array keeps its capacity, so without this a decoder
/// that once decoded a long picture holds that much memory for as long as it lives.
/// </summary>
internal static class SstvBufferCapacity
{
    /// <summary>Capacity a buffer starts with and is never shrunk below.</summary>
    public const int Minimum = 1 << 16;

    /// <summary>
    /// The capacity to shrink to, or the current one when it should stay. Shrinks only when fewer than a
    /// quarter of the slots are in use, and then leaves room for twice the samples held, so a buffer that is
    /// trimmed and refilled every few seconds (the search keeps 14 to 28 seconds) does not reallocate each time.
    /// </summary>
    public static int After(int count, int capacity)
    {
        if (capacity <= Minimum || (long)count * 4 >= capacity)
            return capacity;

        var wanted = Math.Max(Minimum, (long)count * 2);
        var size = (long)Minimum;
        while (size < wanted)
            size <<= 1;
        return (int)Math.Min(size, capacity);
    }
}
