using Avalonia.Automation;
using Avalonia.Automation.Peers;
using OscarWatch.Controls;

namespace OscarWatch.Tests;

/// <summary>
/// The globe is focusable, so a screen reader user lands on it. These check it has an automation peer that
/// carries the name and help text the window binds to it.
/// </summary>
public sealed class EarthViewAutomationPeerTests
{
    [Fact]
    public void The_globe_has_a_peer_that_exposes_its_name_and_help_text()
    {
        var globe = new EarthViewControl();
        AutomationProperties.SetName(globe, "Earth View, ISS");
        AutomationProperties.SetHelpText(globe, "ISS is 30 degrees above your horizon.");

        var peer = ControlAutomationPeer.CreatePeerForElement(globe);

        Assert.IsType<EarthViewAutomationPeer>(peer);
        Assert.Equal("Earth View, ISS", peer.GetName());
        Assert.Equal("ISS is 30 degrees above your horizon.", peer.GetHelpText());
        Assert.Equal(AutomationControlType.Custom, peer.GetAutomationControlType());
        Assert.True(peer.IsControlElement());
    }
}
