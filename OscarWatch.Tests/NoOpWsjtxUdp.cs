using OscarWatch.Core.Models;
using OscarWatch.Core.Services;

namespace OscarWatch.Tests;

internal sealed class NoOpWsjtxUdp : IWsjtxUdpBroadcastService
{
    public void ApplySettings(WsjtxUdpSettings settings)
    {
    }

    public void PublishLoggedQso(QsoRecord record, QsoLogbook logbook)
    {
    }

    public Task StopAsync() => Task.CompletedTask;
}
