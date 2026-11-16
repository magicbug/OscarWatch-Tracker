using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Localization;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests;

public sealed class SettingsDualRadioDraftTests
{
    [Fact]
    public void Conflict_draft_accepts_icom_lan_downlink_with_dummy_uplink()
    {
        using var _ = TestUiCulture.Apply(LocalizationCulture.DefaultLanguage);
        using var vm = SettingsRecordingDevicesTests.CreateViewModel(new CountingAudioRecordingService());

        vm.RigEnabled = true;
        vm.DualRadioEnabled = true;
        vm.SelectedDownlinkRigTypeChoice = FindDualLeg(vm.RigDualDownlinkTypeChoices, RigType.IcomIc705);
        vm.SelectedUplinkRigTypeChoice = FindDualLeg(vm.RigDualUplinkTypeChoices, RigType.Dummy);
        vm.DownlinkIcomLan.SelectedConnectionChoice =
            vm.DownlinkIcomLan.ConnectionChoices.First(o => o.Value == RigConnectionKind.Lan);
        vm.DownlinkIcomLan.LanHost = "192.168.1.21";
        vm.DownlinkIcomLan.LanPort = RigSettings.IcomLanDefaultPort;
        vm.DownlinkIcomLan.LanUser = "user";
        vm.DownlinkIcomLan.LanPassword = "pass";

        var draft = vm.BuildRigSettingsForConflictCheck();

        Assert.False(DualRadioConfigHelper.IsIncomplete(draft));
        Assert.True(draft.Downlink.IsConfigured);
        Assert.True(draft.Uplink.IsConfigured);
        Assert.True(draft.Downlink.UsesIcomLan);
        Assert.Equal("", draft.Downlink.SerialPortInUse);
    }

    [Fact]
    public void Conflict_draft_accepts_icom_lan_downlink_with_serial_uplink()
    {
        using var _ = TestUiCulture.Apply(LocalizationCulture.DefaultLanguage);
        using var vm = SettingsRecordingDevicesTests.CreateViewModel(new CountingAudioRecordingService());

        vm.RigEnabled = true;
        vm.DualRadioEnabled = true;
        vm.SelectedDownlinkRigTypeChoice = FindDualLeg(vm.RigDualDownlinkTypeChoices, RigType.IcomIc705);
        vm.SelectedUplinkRigTypeChoice = FindDualLeg(vm.RigDualUplinkTypeChoices, RigType.YaesuFt818);
        vm.DownlinkIcomLan.SelectedConnectionChoice =
            vm.DownlinkIcomLan.ConnectionChoices.First(o => o.Value == RigConnectionKind.Lan);
        vm.DownlinkIcomLan.LanHost = "192.168.1.21";
        vm.DownlinkIcomLan.LanPort = RigSettings.IcomLanDefaultPort;
        vm.DownlinkIcomLan.LanUser = "user";
        vm.DownlinkIcomLan.LanPassword = "pass";
        vm.SelectedUplinkComPort = "COM4";

        var draft = vm.BuildRigSettingsForConflictCheck();

        Assert.False(DualRadioConfigHelper.IsIncomplete(draft));
        Assert.True(draft.Downlink.IsConfigured);
        Assert.True(draft.Uplink.IsConfigured);
    }

    [Fact]
    public void Conflict_draft_still_requires_serial_downlink_port_when_not_on_lan()
    {
        using var _ = TestUiCulture.Apply(LocalizationCulture.DefaultLanguage);
        using var vm = SettingsRecordingDevicesTests.CreateViewModel(new CountingAudioRecordingService());

        vm.RigEnabled = true;
        vm.DualRadioEnabled = true;
        vm.SelectedDownlinkRigTypeChoice = FindDualLeg(vm.RigDualDownlinkTypeChoices, RigType.IcomIc705);
        vm.SelectedUplinkRigTypeChoice = FindDualLeg(vm.RigDualUplinkTypeChoices, RigType.Dummy);
        vm.DownlinkIcomLan.SelectedConnectionChoice =
            vm.DownlinkIcomLan.ConnectionChoices.First(o => o.Value == RigConnectionKind.Serial);
        vm.SelectedDownlinkComPort = null;

        var draft = vm.BuildRigSettingsForConflictCheck();

        Assert.True(DualRadioConfigHelper.IsIncomplete(draft));
        Assert.Equal(DualRadioConfigHelper.MissingDownlinkCode, DualRadioConfigHelper.IncompleteCode(draft));
    }

    private static RigTypeOption FindDualLeg(IReadOnlyList<RigTypeOption> choices, RigType type) =>
        choices.First(o => o.Value == type);

    private sealed class CountingAudioRecordingService : IAudioRecordingService
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public bool IsRecording => false;
        public string? ActiveNoradId => null;
        public string? ActiveOutputPath => null;
        public string? LastCompletedOutputPath => null;

        public bool TryInitialize() => true;

        public IReadOnlyList<AudioInputDevice> GetInputDevices() =>
            [new AudioInputDevice("Fake Input", "Fake Input")];

        public Task StartAsync(
            string noradId,
            string satelliteName,
            string deviceId,
            RecordingFormatPreset format,
            string outputPath,
            string? deviceName = null,
            RecordingContainerFormat container = RecordingContainerFormat.Wav,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}