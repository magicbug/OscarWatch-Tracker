using OscarWatch.Recording;

namespace OscarWatch.Tests;

public sealed class RecordingDeviceResolverTests
{
    private static RecordingDeviceResolver.InputDeviceSnapshot Dev(
        int index,
        string rawName,
        double latency = 0.003,
        int channels = 2,
        int hostApiType = 0) =>
        new(index, rawName, latency, channels, hostApiType);

    [Fact]
    public void Resolve_MatchesRawNameAtNewIndex_AfterUsbReenumeration()
    {
        // Saved as raw name while device was at index 5; after reboot it is at index 2
        var inputs = new[]
        {
            Dev(0, "Built-in Mic"),
            Dev(2, "USB Audio CODEC", 0.005),
            Dev(7, "Other USB")
        };

        var index = RecordingDeviceResolver.ResolveIndex(
            "USB Audio CODEC",
            "USB Audio CODEC",
            inputs);

        Assert.Equal(2, index);
    }

    [Fact]
    public void Resolve_PrefersHighestLatencyWhenRawNameDuplicated()
    {
        var inputs = new[]
        {
            Dev(1, "Line In (USB Audio)", 0.090),
            Dev(4, "Line In (USB Audio)", 0.003),
            Dev(8, "Line In (USB Audio)", 0.012)
        };

        var index = RecordingDeviceResolver.ResolveIndex(
            "Line In (USB Audio)",
            "Line In (USB Audio)",
            inputs);

        Assert.Equal(1, index);
    }

    [Fact]
    public void Resolve_DoesNotOpenLegacyIndexWhenNameNoLongerMatches()
    {
        // Old saved index 5 now points at a different device after USB reorder
        var inputs = new[]
        {
            Dev(0, "Microphone"),
            Dev(5, "Built-in Line In"),
            Dev(9, "USB Audio CODEC")
        };

        var index = RecordingDeviceResolver.ResolveIndex(
            "5",
            "USB Audio CODEC",
            inputs);

        Assert.Equal(9, index);
    }

    [Fact]
    public void Resolve_LegacyIndexOnlyWhenDisplayNameStillMatches()
    {
        var inputs = new[]
        {
            Dev(5, "USB Audio CODEC")
        };

        var index = RecordingDeviceResolver.ResolveIndex(
            "5",
            "USB Audio CODEC",
            inputs);

        Assert.Equal(5, index);
    }

    [Fact]
    public void Resolve_LegacyIndexAloneWithoutDisplayName_ReturnsUnavailable()
    {
        var inputs = new[]
        {
            Dev(5, "Whatever is at five now")
        };

        var index = RecordingDeviceResolver.ResolveIndex("5", null, inputs);
        Assert.Equal(-1, index);
    }

    [Fact]
    public void Resolve_FallsBackToFormattedDisplayName()
    {
        var raw = "Microphone (@System32\\drivers\\bthhefenum.sys,#2;%1 Hands-Free%0 ;(WF-C700N))";
        var inputs = new[]
        {
            Dev(3, raw, 0.004)
        };

        var index = RecordingDeviceResolver.ResolveIndex(
            "",
            "WF-C700N",
            inputs);

        Assert.Equal(3, index);
    }

    [Fact]
    public void Resolve_Ft4PrefersWasapiOverSlowerSharedAndExclusiveCopies()
    {
        var inputs = new[]
        {
            Dev(1, "Speakers (USB audio CODEC)", 0.120, hostApiType: RecordingDeviceResolver.HostApiDirectSound),
            Dev(8, "Speakers (USB audio CODEC)", 0.090, hostApiType: RecordingDeviceResolver.HostApiMme),
            Dev(20, "Speakers (USB audio CODEC)", 0.003, hostApiType: RecordingDeviceResolver.HostApiWasapi),
            Dev(41, "Speakers (USB audio CODEC)", 0.001, hostApiType: RecordingDeviceResolver.HostApiWdmks)
        };

        var index = RecordingDeviceResolver.ResolveIndex(
            "Speakers (USB audio CODEC)",
            "Speakers (USB audio CODEC)",
            inputs,
            preferLowLatencyShared: true);

        Assert.Equal(20, index);
    }

    [Fact]
    public void Resolve_Ft4SkipsExclusiveApiWhenWasapiIsMissing()
    {
        var inputs = new[]
        {
            Dev(4, "CABLE Input", 0.090, hostApiType: RecordingDeviceResolver.HostApiMme),
            Dev(9, "CABLE Input", 0.002, hostApiType: RecordingDeviceResolver.HostApiWdmks)
        };

        var index = RecordingDeviceResolver.ResolveIndex(
            "CABLE Input",
            "CABLE Input",
            inputs,
            preferLowLatencyShared: true);

        Assert.Equal(4, index);
    }

    [Fact]
    public void IsLegacyNumericDeviceId_DetectsPureDigits()
    {
        Assert.True(RecordingDeviceResolver.IsLegacyNumericDeviceId("5"));
        Assert.True(RecordingDeviceResolver.IsLegacyNumericDeviceId("12"));
        Assert.False(RecordingDeviceResolver.IsLegacyNumericDeviceId("USB Audio"));
        Assert.False(RecordingDeviceResolver.IsLegacyNumericDeviceId(""));
        Assert.False(RecordingDeviceResolver.IsLegacyNumericDeviceId(null));
    }
}
