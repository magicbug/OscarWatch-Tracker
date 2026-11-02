namespace OscarWatch.Controls;

/// <summary>
/// Shares one decoded <see cref="BasemapPixels"/> between everything that is using it, and drops it
/// when the last user lets go. The Earth View window takes a lease when it opens and disposes it when
/// it closes, so the roughly 32 MB of pixels exist only while the window does.
/// </summary>
internal sealed class BasemapPixelCache
{
    private readonly Func<BasemapPixels> _loader;
    private readonly object _gate = new();
    private BasemapPixels? _current;
    private int _leaseCount;

    public BasemapPixelCache(Func<BasemapPixels> loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    /// <summary>The cache used by the application: decodes the bundled world map on first use.</summary>
    public static BasemapPixelCache Default { get; } = new(BasemapPixels.LoadFromAsset);

    /// <summary>Number of leases currently held. Exposed for tests.</summary>
    internal int ActiveLeaseCount
    {
        get { lock (_gate) return _leaseCount; }
    }

    /// <summary>True while pixels are held in memory. Exposed for tests.</summary>
    internal bool IsLoaded
    {
        get { lock (_gate) return _current is not null; }
    }

    /// <summary>
    /// Takes a lease, loading the pixels if nobody else is using them. Dispose the lease when done.
    /// The loader runs under a lock, so a second caller waits for the first load rather than decoding again.
    /// </summary>
    public Lease Acquire()
    {
        lock (_gate)
        {
            _current ??= _loader();
            _leaseCount++;
            return new Lease(this, _current);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_leaseCount == 0)
                return;

            _leaseCount--;
            if (_leaseCount == 0)
                _current = null; // let the garbage collector reclaim the pixel array
        }
    }

    /// <summary>A hold on the shared pixels. Disposing it more than once is harmless.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly BasemapPixelCache _owner;
        private int _disposed;

        internal Lease(BasemapPixelCache owner, BasemapPixels pixels)
        {
            _owner = owner;
            Pixels = pixels;
        }

        public BasemapPixels Pixels { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.Release();
        }
    }
}
