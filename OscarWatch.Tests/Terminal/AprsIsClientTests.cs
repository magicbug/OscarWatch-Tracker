using OscarWatch.Terminal;

namespace OscarWatch.Tests.Terminal;

public sealed class AprsIsClientTests
{
    [Fact]
    public void Short_session_retries_at_the_current_delay_then_backs_off()
    {
        var plan = AprsIsClient.PlanReconnect(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(5), plan.Delay);
        Assert.Equal(TimeSpan.FromSeconds(10), plan.Next);
    }

    [Fact]
    public void Backoff_stops_at_two_minutes()
    {
        var plan = AprsIsClient.PlanReconnect(AprsIsClient.MaxBackoff, TimeSpan.FromSeconds(1));

        Assert.Equal(AprsIsClient.MaxBackoff, plan.Delay);
        Assert.Equal(AprsIsClient.MaxBackoff, plan.Next);
    }

    [Fact]
    public void Stable_session_retries_promptly()
    {
        var plan = AprsIsClient.PlanReconnect(TimeSpan.FromSeconds(40), AprsIsClient.StableSession);

        Assert.Equal(AprsIsClient.InitialBackoff, plan.Delay);
        Assert.Equal(AprsIsClient.InitialBackoff, plan.Next);
    }
}
