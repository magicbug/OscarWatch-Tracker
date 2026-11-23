using System.Xml.Linq;
using OscarWatch.Core.Cw;
using OscarWatch.Core.CwDecode;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Cw;
using OscarWatch.Hardware;
using OscarWatch.Localization;
using OscarWatch.Services;
using OscarWatch.Sstv;
using OscarWatch.Tests.CwDecode;
using OscarWatch.Tests.Hardware;
using OscarWatch.ViewModels;
using Xunit;

namespace OscarWatch.Tests.Cw;

public sealed class CwUplinkAlignServiceTests
{
    private const double BinHz =
        (CwReceiverService.WaterfallMaxHz - CwReceiverService.WaterfallMinHz) / CwReceiverService.WaterfallColumns;

    [Theory]
    [InlineData("FM", "FM", 145_850, "CW", true, false, "MM9SQL", true, true, false, CwAlignRefusalReason.NotLinearTransponder)]
    [InlineData("USB", "USB", 0, "CW", true, false, "MM9SQL", true, true, false, CwAlignRefusalReason.NotLinearTransponder)]
    [InlineData("DATA-USB", "DATA-USB", 435_300, "CW", true, false, "MM9SQL", true, true, false, CwAlignRefusalReason.NotLinearTransponder)]
    [InlineData("LSB", "USB", 435_300, "USB", true, false, "MM9SQL", true, true, false, CwAlignRefusalReason.NotCwMode)]
    [InlineData("LSB", "USB", 435_300, "CW", false, false, "MM9SQL", true, true, false, CwAlignRefusalReason.RadioNotConnected)]
    [InlineData("LSB", "USB", 435_300, "CW", true, true, "MM9SQL", true, true, false, CwAlignRefusalReason.InStandby)]
    [InlineData("LSB", "USB", 435_300, "CW", true, false, "", true, true, false, CwAlignRefusalReason.NoCallsign)]
    [InlineData("LSB", "USB", 435_300, "CW", true, false, "MM9SQL", false, true, false, CwAlignRefusalReason.DecoderNotRunning)]
    [InlineData("LSB", "USB", 435_300, "CW", true, false, "MM9SQL", true, false, false, CwAlignRefusalReason.NoDownlinkAudio)]
    [InlineData("LSB", "USB", 435_300, "CW", true, false, "MM9SQL", true, true, true, CwAlignRefusalReason.NoDownlinkAudio)]
    public void Prerequisites_refuse_and_never_transmit_when_unmet(
        string uplinkMode,
        string downlinkMode,
        double uplinkKHz,
        string liveMode,
        bool rigConnected,
        bool standby,
        string callsign,
        bool decoderRunning,
        bool receivingAudio,
        bool inputSilent,
        CwAlignRefusalReason expectedReason)
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "TestMode",
            Doppler = "REV",
            UplinkMode = uplinkMode,
            DownlinkMode = downlinkMode,
            UplinkKHz = uplinkKHz,
            DownlinkKHz = 145_950
        };

        using var harness = CreateHarness(mode, liveMode, modeRx: "CW");
        harness.Rig.Connected = rigConnected;
        harness.Tracker.Standby = standby;
        harness.Settings.Current.GroundStation.Callsign = callsign;
        harness.Receiver.IsRunning = decoderRunning;
        harness.Receiver.CaptureHealth = new CwCaptureHealth(receivingAudio, false);
        harness.Receiver.IsInputSilent = inputSilent;

        Assert.Equal(expectedReason, harness.Align.CheckPrerequisites());
        Assert.False(harness.Align.Start());
        Assert.Empty(harness.Rig.Calls);
        Assert.Equal(CwAlignState.Idle, harness.Align.State);
    }

    [Fact]
    public void Keyer_rts_blocked_is_refused_without_transmitting()
    {
        var mode = LinearMode("REV");
        using var harness = CreateHarness(mode, "CW", "CW", rigType: RigType.YaesuFt991a);
        harness.Settings.Current.QsoLogbook.CwKeyer.KeyLine = PttLine.Rts;

        Assert.Equal(CwAlignRefusalReason.KeyerUnavailable, harness.Align.CheckPrerequisites());
        Assert.False(harness.Align.Start());
        Assert.Empty(harness.Rig.Calls);
    }

    [Theory]
    [InlineData("REV", 950.0, 700.0, +0.250)]
    [InlineData("NOR", 950.0, 700.0, -0.250)]
    public void Successful_alignment_applies_persists_and_undo_restores(
        string doppler,
        double echoHz,
        int targetPitchHz,
        double expectedDeltaKHz)
    {
        using var model = LoadRealModel();
        var mode = LinearMode(doppler);
        using var harness = CreateHarness(mode, "CW", "CW", modelRunner: model, modelMetadata: model.Metadata);
        harness.Settings.Current.QsoLogbook.CwKeyer.AlignTargetPitchHz = targetPitchHz;
        harness.Frequencies.SetCwUplink(true);

        var nominalUplink = mode.UplinkKHz;
        var nominalDownlink = mode.DownlinkKHz;
        harness.Frequencies.TransmitOffsetKHz = 0.100;

        harness.Receiver.PrepareEchoAudio("MM9SQL", echoHz, wpm: 22);

        var completed = new ManualResetEventSlim();
        harness.Align.StateChanged += (_, _) =>
        {
            if (!harness.Align.IsActive && harness.Align.CanUndo)
                completed.Set();
        };

        Assert.True(harness.Align.Start());
        Assert.True(completed.Wait(TimeSpan.FromSeconds(15)));

        // Live TX offset updated in the expected direction within ±10 Hz.
        var expectedNewOffset = 0.100 + expectedDeltaKHz;
        Assert.InRange(Math.Abs(harness.Frequencies.TransmitOffsetKHz - expectedNewOffset), 0, 0.010);

        // Persisted to SatelliteFrequencySelection CW TX offset.
        var selection = harness.Settings.Current.FrequencySelections["JO-97"];
        Assert.True(selection.RememberOffsets);
        Assert.Equal(
            harness.Frequencies.TransmitOffsetKHz,
            selection.GetTransmitOffsetForMode(mode.Type, cwOperatingStyle: true),
            precision: 4);

        // Nominal transponder frequencies untouched.
        Assert.Equal(nominalUplink, mode.UplinkKHz);
        Assert.Equal(nominalDownlink, mode.DownlinkKHz);

        // Undo restores both live and stored TX offsets cleanly.
        Assert.True(harness.Align.CanUndo);
        Assert.True(harness.Align.Undo());
        Assert.False(harness.Align.CanUndo);
        Assert.Equal(0.100, harness.Frequencies.TransmitOffsetKHz, precision: 4);
        Assert.False(selection.CwTransmitOffsetKHzByMode.ContainsKey(mode.Type));
    }

    [Fact]
    public void Abort_on_escape_and_runtime_guards_unkeys_and_keeps_tx_offset_unchanged()
    {
        var mode = LinearMode("REV");
        using var harness = CreateHarness(mode, "CW", "CW", wpm: 5);
        harness.Frequencies.TransmitOffsetKHz = 0.250;

        var panel = new CwKeyerPanelViewModel(
            harness.Keyer,
            harness.Settings,
            harness.Tracker,
            new QsoLogbookEntryBridge(),
            LocalizationService.Instance,
            harness.Align);
        panel.AttachHost(CwKeyerHost.Decoder);

        Assert.True(harness.Align.Start());
        Assert.Equal(CwAlignState.Sending, harness.Align.State);
        Assert.True(panel.IsAligning);
        Assert.False(panel.AlignEnabled);
        Assert.True(panel.StopEnabled);

        // Escape aborts immediately and leaves key up.
        Assert.True(panel.TryHandleCwEscape());
        Assert.Equal(CwAlignState.Idle, harness.Align.State);
        Assert.False(harness.Keyer.IsSending);
        Assert.Equal(0.250, harness.Frequencies.TransmitOffsetKHz, precision: 4);

        // Standby aborts active alignment immediately.
        Assert.True(harness.Align.Start());
        harness.Tracker.Standby = true;
        harness.Align.CheckRuntimeGuards(harness.Tracker.GetCurrent());
        Assert.Equal(CwAlignState.Idle, harness.Align.State);
        Assert.False(harness.Keyer.IsSending);

        // Disconnect aborts active alignment immediately.
        harness.Tracker.Standby = false;
        Assert.True(harness.Align.Start());
        harness.Rig.Connected = false;
        harness.Align.CheckRuntimeGuards(harness.Tracker.GetCurrent());
        Assert.Equal(CwAlignState.Idle, harness.Align.State);
        Assert.False(harness.Keyer.IsSending);

        // Leaving CW mode aborts active alignment immediately.
        harness.Rig.Connected = true;
        Assert.True(harness.Align.Start());
        harness.Tracker.Mode = "USB";
        harness.Align.CheckRuntimeGuards(harness.Tracker.GetCurrent());
        Assert.Equal(CwAlignState.Idle, harness.Align.State);
        Assert.False(harness.Keyer.IsSending);

        // Switching transponder aborts active alignment immediately and leaves offsets untouched.
        harness.Tracker.Mode = "CW";
        Assert.True(harness.Align.Start());
        harness.Frequencies.SelectedMode = harness.Frequencies.AvailableModes[1];
        harness.Align.CheckRuntimeGuards(harness.Tracker.GetCurrent());
        Assert.Equal(CwAlignState.Idle, harness.Align.State);
        Assert.False(harness.Keyer.IsSending);
        Assert.Equal(0.0, harness.Frequencies.TransmitOffsetKHz, precision: 4);
        Assert.False(harness.Align.CanUndo);
    }

    [Fact]
    public void Stale_measurement_after_abort_never_modifies_tx_offset()
    {
        using var realModel = LoadRealModel();
        var enteredModel = new ManualResetEventSlim();
        var releaseModel = new ManualResetEventSlim();
        var gatedRunner = new GatedModelRunner(realModel, enteredModel, releaseModel);

        var mode = LinearMode("REV");
        using var harness = CreateHarness(
            mode,
            "CW",
            "CW",
            modelRunner: gatedRunner,
            modelMetadata: realModel.Metadata);
        harness.Frequencies.TransmitOffsetKHz = 0.0;
        harness.Receiver.PrepareEchoAudio("MM9SQL", 950.0, wpm: 22);

        Assert.True(harness.Align.Start());

        // Wait until background analyzer is actively inside model inference.
        Assert.True(enteredModel.Wait(TimeSpan.FromSeconds(10)));

        // Abort while analysis is in flight, then release inference to complete late.
        harness.Align.Abort();
        releaseModel.Set();
        Thread.Sleep(250);

        Assert.Equal(CwAlignState.Idle, harness.Align.State);
        Assert.Equal(0.0, harness.Frequencies.TransmitOffsetKHz, precision: 6);
        Assert.False(harness.Align.CanUndo);
    }

    [Fact]
    public void All_new_alignment_localization_keys_exist_in_all_ten_locales()
    {
        string[] expectedKeys =
        [
            "Logbook.Cw.Align",
            "Logbook.Cw.Align.Tip",
            "Logbook.Cw.Align.Undo",
            "Logbook.Cw.Align.Undo.Tip",
            "Logbook.Cw.Align.Refusal.NotLinearTransponder",
            "Logbook.Cw.Align.Refusal.NotCwMode",
            "Logbook.Cw.Align.Refusal.RadioNotConnected",
            "Logbook.Cw.Align.Refusal.InStandby",
            "Logbook.Cw.Align.Refusal.NoCallsign",
            "Logbook.Cw.Align.Refusal.DecoderNotRunning",
            "Logbook.Cw.Align.Refusal.NoDownlinkAudio",
            "Logbook.Cw.Align.Refusal.Busy",
            "Logbook.Cw.Align.Status.Sending",
            "Logbook.Cw.Align.Status.Listening",
            "Logbook.Cw.Align.Status.Applied",
            "Logbook.Cw.Align.Status.AlreadyAligned",
            "Logbook.Cw.Align.Status.NoEcho",
            "Logbook.Cw.Align.Status.Ambiguous",
            "Logbook.Cw.Align.Status.ExceedsCap",
            "Logbook.Cw.Align.Status.OutOfBounds",
            "Logbook.Cw.Align.Status.TimedOut",
            "Logbook.Cw.Align.Status.Aborted",
            "Logbook.Cw.Align.Status.AbortedStandby",
            "Logbook.Cw.Align.Status.AbortedDisconnected",
            "Logbook.Cw.Align.Status.AbortedModeChanged",
            "Logbook.Cw.Align.Status.AbortedTransponderChanged",
            "Logbook.Cw.Align.Status.AbortedDecoderStopped",
            "Logbook.Cw.Align.Status.Undone",
            "Logbook.Cw.Align.Status.UndoUnavailable",
            "Cw.Settings.Alignment",
            "Cw.Align.TargetPitch",
            "Cw.Align.MaxDuration",
            "Cw.Align.SafetyNote"
        ];

        var resourcesDir = FindResourcesDirectory();
        var files = Directory.GetFiles(resourcesDir, "Strings*.resx");
        Assert.Equal(10, files.Length);

        foreach (var file in files)
        {
            var doc = XDocument.Load(file);
            var keys = doc.Descendants("data")
                .Select(d => d.Attribute("name")?.Value)
                .Where(k => !string.IsNullOrEmpty(k))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var key in expectedKeys)
                Assert.True(keys.Contains(key), $"Missing localization key '{key}' in {Path.GetFileName(file)}");
        }
    }

    private static string FindResourcesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "OscarWatch", "Resources");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate OscarWatch/Resources directory.");
    }

    private static CwOnnxModel LoadRealModel()
    {
        var dir = CwTestAudio.FindModelDirectory();
        Assert.NotNull(dir);
        return CwOnnxModel.Load(dir);
    }

    private static SatelliteTransponderMode LinearMode(string doppler) =>
        new()
        {
            Type = "Linear",
            Doppler = doppler,
            UplinkMode = "LSB",
            DownlinkMode = "USB",
            UplinkKHz = 435_300,
            DownlinkKHz = 145_950
        };

    private static Harness CreateHarness(
        SatelliteTransponderMode primaryMode,
        string mode,
        string modeRx,
        RigType rigType = RigType.YaesuFt817,
        int wpm = 60,
        ICwModelRunner? modelRunner = null,
        CwModelMetadata? modelMetadata = null)
    {
        var settings = new FakeSettings();
        settings.Current.Rig.Enabled = true;
        settings.Current.Rig.Type = rigType;
        settings.Current.GroundStation.Callsign = "MM9SQL";
        settings.Current.QsoLogbook.CwKeyer.Enabled = true;
        settings.Current.QsoLogbook.CwKeyer.Wpm = wpm;

        var secondaryMode = new SatelliteTransponderMode
        {
            Type = "OtherLinear",
            Doppler = "NOR",
            UplinkMode = "USB",
            DownlinkMode = "USB",
            UplinkKHz = 145_920,
            DownlinkKHz = 435_120
        };

        var db = new SingleSatDatabase("JO-97", "43803", [primaryMode, secondaryMode]);
        var frequencies = new FrequencyOverlayViewModel(settings, db, LocalizationService.Instance);
        frequencies.Update(new SatelliteTrackState
        {
            Name = "JO-97",
            NoradId = "43803",
            Subpoint = new GeoCoordinate(0, 0, 0),
            LookAngles = new LookAngles(180, 45, 800)
        });

        var rig = new RecordingRigController { Connected = true };
        var keyer = new CwKeyerService(rig, settings);
        var receiver = new FakeProbeReceiver(modelRunner ?? new DummyModelRunner(), modelMetadata ?? CwModelMetadata.DeepCw());
        var tracker = new FakeTracker("JO-97", mode, modeRx);

        var align = new CwUplinkAlignService(
            keyer,
            receiver,
            rig,
            settings,
            frequencies,
            tracker,
            LocalizationService.Instance,
            dispatchToUi: action => action(),
            earlyCheckDelay: TimeSpan.FromMilliseconds(20),
            finalCheckDelay: TimeSpan.FromMilliseconds(60));

        return new Harness(align, keyer, receiver, rig, settings, frequencies, tracker);
    }

    private sealed class Harness(
        CwUplinkAlignService align,
        CwKeyerService keyer,
        FakeProbeReceiver receiver,
        RecordingRigController rig,
        FakeSettings settings,
        FrequencyOverlayViewModel frequencies,
        FakeTracker tracker) : IDisposable
    {
        public CwUplinkAlignService Align { get; } = align;
        public CwKeyerService Keyer { get; } = keyer;
        public FakeProbeReceiver Receiver { get; } = receiver;
        public RecordingRigController Rig { get; } = rig;
        public FakeSettings Settings { get; } = settings;
        public FrequencyOverlayViewModel Frequencies { get; } = frequencies;
        public FakeTracker Tracker { get; } = tracker;

        public void Dispose()
        {
            Align.Dispose();
            Keyer.Dispose();
        }
    }

    private sealed class FakeProbeReceiver(ICwModelRunner runner, CwModelMetadata metadata) : ICwProbeReceiver
    {
        private float[] _samples = [];
        private IReadOnlyList<float[]> _rows = [];

        public bool IsRunning { get; set; } = true;
        public CwCaptureHealth CaptureHealth { get; set; } = new(true, false);
        public bool IsInputSilent { get; set; }
        public ICwModelRunner? ActiveModelRunner => IsRunning ? runner : null;
        public CwModelMetadata ActiveModelMetadata => metadata;

        public void PrepareEchoAudio(string callsign, double toneHz, int wpm = 22)
        {
            _samples = CwTestAudio.Morse(
                callsign,
                wpm,
                toneHz,
                CwEchoProbeAnalyzer.SampleRate,
                leadSeconds: 0.6,
                tailSeconds: 1.2,
                noiseRms: 0.02,
                seed: 21);
            var tap = new SstvSpectrumTap(
                CwReceiverService.WaterfallRate,
                CwReceiverService.SpectrumRowsPerSecond,
                CwReceiverService.WaterfallMinHz,
                CwReceiverService.WaterfallMaxHz,
                CwReceiverService.WaterfallColumns,
                windowSamples: 1024,
                hopSamples: 256);
            var rows = new List<float[]>();
            tap.Add(_samples, rows);
            _rows = rows;
        }

        public CwProbeCaptureHandle? BeginProbeCapture(double maxCaptureSeconds = 12.0) =>
            IsRunning ? new CwProbeCaptureHandle(CwEchoProbeAnalyzer.SampleRate * 12, []) : null;

        public CwProbeCaptureData? SnapshotProbeCapture(CwProbeCaptureHandle handle) =>
            new(_samples, _rows, [], BinHz, 800.0, handle.IsAborted);

        public void EndProbeCapture(CwProbeCaptureHandle handle) { }
    }

    private sealed class DummyModelRunner : ICwModelRunner
    {
        public float[] Run(float[] spectrogram, int frames, int bins, out int outputFrames)
        {
            outputFrames = 0;
            return [];
        }
    }

    private sealed class GatedModelRunner(
        ICwModelRunner inner,
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : ICwModelRunner
    {
        public float[] Run(float[] spectrogram, int frames, int bins, out int outputFrames)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return inner.Run(spectrogram, frames, bins, out outputFrames);
        }
    }

    private sealed class SingleSatDatabase(string name, string noradId, List<SatelliteTransponderMode> modes) : ISatelliteDatabaseService
    {
        private readonly SatelliteRadioEntry _entry = new()
        {
            Name = name,
            NoradId = noradId,
            Modes = modes
        };

        public IReadOnlyList<SatelliteRadioEntry> Entries => [_entry];
        public string ActiveDatabasePath => "";
        public bool IsUsingUserDatabase => false;
        public SatelliteRadioEntry? TryGetEntry(string satelliteName, string? noradIdArg = null) => _entry;
        public void Reload() { }
    }

    private sealed class FakeTracker(string satellite, string mode, string modeRx) : ILiveTrackerSnapshotProvider
    {
        public string Mode { get; set; } = mode;
        public string ModeRx { get; set; } = modeRx;
        public bool Standby { get; set; }
        public string? FocusedNoradId { get; set; } = "43803";

        public LiveTrackerSnapshot GetCurrent() =>
            new(satellite, Mode, ModeRx, 435_300_000, 145_950_000, "70cm", "2m", "Linear", 45, 0, Standby);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsPath => "";
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SerializeCurrent() => "";
        public Task ReplaceAndSaveAsync(AppSettings imported, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Load() { }
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RequestSave() { }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SyncGridFromLatLon() { }
        public void SyncLatLonFromGrid() { }
        public void EnsureSavedStations() { }
        public void ApplyActiveStation() { }
        public void SyncActiveStationFromGroundStation() { }
    }
}
