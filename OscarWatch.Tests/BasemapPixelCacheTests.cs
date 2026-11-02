using OscarWatch.Controls;

namespace OscarWatch.Tests;

public sealed class BasemapPixelCacheTests
{
    private sealed class CountingLoader
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public BasemapPixels Load()
        {
            Interlocked.Increment(ref _calls);
            return new BasemapPixels([0xFF000000, 0xFF000000, 0xFF000000, 0xFF000000], 2, 2);
        }
    }

    [Fact]
    public void Nothing_is_loaded_until_the_first_lease()
    {
        var loader = new CountingLoader();
        var cache = new BasemapPixelCache(loader.Load);

        Assert.False(cache.IsLoaded);
        Assert.Equal(0, loader.Calls);
    }

    [Fact]
    public void Two_leases_share_one_load()
    {
        var loader = new CountingLoader();
        var cache = new BasemapPixelCache(loader.Load);

        using var first = cache.Acquire();
        using var second = cache.Acquire();

        Assert.Equal(1, loader.Calls);
        Assert.Same(first.Pixels, second.Pixels);
        Assert.Equal(2, cache.ActiveLeaseCount);
    }

    [Fact]
    public void Pixels_are_released_when_the_last_lease_is_disposed()
    {
        var loader = new CountingLoader();
        var cache = new BasemapPixelCache(loader.Load);
        var first = cache.Acquire();
        var second = cache.Acquire();

        first.Dispose();
        Assert.True(cache.IsLoaded, "Still in use by the second lease.");

        second.Dispose();
        Assert.False(cache.IsLoaded);
        Assert.Equal(0, cache.ActiveLeaseCount);
    }

    [Fact]
    public void Acquiring_after_release_loads_again()
    {
        var loader = new CountingLoader();
        var cache = new BasemapPixelCache(loader.Load);

        cache.Acquire().Dispose();
        using var again = cache.Acquire();

        Assert.Equal(2, loader.Calls);
        Assert.True(again.Pixels.IsAvailable);
    }

    [Fact]
    public void Disposing_a_lease_twice_does_not_release_another_holders_pixels()
    {
        var loader = new CountingLoader();
        var cache = new BasemapPixelCache(loader.Load);
        var first = cache.Acquire();
        using var second = cache.Acquire();

        first.Dispose();
        first.Dispose();

        Assert.True(cache.IsLoaded);
        Assert.Equal(1, cache.ActiveLeaseCount);
    }

    [Fact]
    public void A_failed_load_still_gives_a_usable_lease_and_is_retried_after_release()
    {
        var attempts = 0;
        var cache = new BasemapPixelCache(() =>
        {
            attempts++;
            return BasemapPixels.Unavailable;
        });

        using (var lease = cache.Acquire())
        {
            Assert.False(lease.Pixels.IsAvailable);
            Assert.Equal(0u, lease.Pixels.Sample(0, 0));
        }

        cache.Acquire().Dispose();

        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Many_threads_acquiring_and_releasing_leave_the_cache_empty()
    {
        var loader = new CountingLoader();
        var cache = new BasemapPixelCache(loader.Load);

        Parallel.For(0, 400, _ =>
        {
            using var lease = cache.Acquire();
            Assert.True(lease.Pixels.IsAvailable);
        });

        Assert.Equal(0, cache.ActiveLeaseCount);
        Assert.False(cache.IsLoaded);
        Assert.True(loader.Calls >= 1);
    }

    [Fact]
    public void A_null_loader_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => new BasemapPixelCache(null!));
}
