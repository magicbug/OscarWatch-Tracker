using OscarWatch.Core.Models;

namespace OscarWatch.Core.Services;

/// <summary>Sends newly logged contacts to another application using the WSJT-X UDP protocol.</summary>
public interface IWsjtxUdpBroadcastService
{
    void ApplySettings(WsjtxUdpSettings settings);

    /// <summary>
    /// Queues a logged-QSO datagram. Does nothing when the feed is off.
    /// A send failure is logged and does not escape to the caller.
    /// </summary>
    void PublishLoggedQso(QsoRecord record, QsoLogbook logbook);

    /// <summary>Stops the heartbeat and tells listeners this source has closed.</summary>
    Task StopAsync();
}
