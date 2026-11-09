using OscarWatch.Core.Sstv;
using OscarWatch.Services;

namespace OscarWatch.Tests;

public sealed class KeepAwakeServiceTests
{
    private sealed class FakePlatform : IKeepAwakePlatform
    {
        public List<string> Calls { get; } = [];
        public bool FailBegin { get; set; }

        public void Begin(string reason)
        {
            Calls.Add("begin:" + reason);
            if (FailBegin)
                throw new InvalidOperationException("refused");
        }

        public void End() => Calls.Add("end");
    }

    private static (KeepAwakeService Service, FakePlatform Platform) Create()
    {
        var platform = new FakePlatform();
        return (new KeepAwakeService(platform, work => work()), platform);
    }

    [Fact]
    public void First_lease_begins_and_last_lease_ends()
    {
        var (service, platform) = Create();

        var a = service.Acquire("sstv");
        var b = service.Acquire("ft4");
        Assert.Equal(["begin:sstv"], platform.Calls);

        a.Dispose();
        Assert.Equal(["begin:sstv"], platform.Calls);

        b.Dispose();
        Assert.Equal(["begin:sstv", "end"], platform.Calls);
    }

    [Fact]
    public void Disposing_a_lease_twice_releases_it_once()
    {
        var (service, platform) = Create();

        var a = service.Acquire("sstv");
        var b = service.Acquire("sstv");
        a.Dispose();
        a.Dispose();

        Assert.Equal(["begin:sstv"], platform.Calls);
        b.Dispose();
        Assert.Equal(["begin:sstv", "end"], platform.Calls);
    }

    [Fact]
    public void Acquiring_again_after_release_begins_again()
    {
        var (service, platform) = Create();

        service.Acquire("one").Dispose();
        service.Acquire("two").Dispose();

        Assert.Equal(["begin:one", "end", "begin:two", "end"], platform.Calls);
    }

    [Fact]
    public void A_refused_assertion_does_not_throw_to_the_caller()
    {
        var (service, platform) = Create();
        platform.FailBegin = true;

        var lease = service.Acquire("sstv");
        lease.Dispose();

        Assert.Equal(["begin:sstv", "end"], platform.Calls);
    }

    [Fact]
    public void Hold_takes_one_lease_and_releases_it_when_switched_off()
    {
        var (service, platform) = Create();
        var hold = new KeepAwakeHold(service, "sstv");

        hold.Set(true);
        hold.Set(true);
        Assert.True(hold.IsHeld);
        Assert.Equal(["begin:sstv"], platform.Calls);

        hold.Set(false);
        Assert.False(hold.IsHeld);
        Assert.Equal(["begin:sstv", "end"], platform.Calls);

        hold.Set(true);
        hold.Dispose();
        Assert.Equal(["begin:sstv", "end", "begin:sstv", "end"], platform.Calls);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void Sstv_keeps_awake_only_while_listening_live_with_the_option_on(
        bool option, bool running, bool decodingFile, bool expected) =>
        Assert.Equal(expected, SstvSettings.ShouldKeepAwake(option, running, decodingFile));

    [Fact]
    public void Sstv_keep_awake_is_off_by_default() =>
        Assert.False(new SstvSettings().KeepAwakeWhileListening);
}
