using OscarWatch.Core.Cw;
using OscarWatch.Core.Models;
using OscarWatch.Core.Services;
using OscarWatch.Hardware;
using OscarWatch.Localization;
using OscarWatch.Services;
using OscarWatch.Tests.Hardware;
using OscarWatch.ViewModels;

namespace OscarWatch.Tests.Cw;

public sealed class CwKeyerPanelTests
{
    [Fact]
    public void Row_is_hidden_unless_keyer_enabled_and_mode_is_cw()
    {
        var tracker = new FakeTracker("USB");
        using var harness = Create(tracker);

        Assert.False(harness.Panel.ShowCwKeyer);
        Assert.False(harness.Panel.TryHandleCwFunctionKey(0));

        tracker.Mode = "CW";
        harness.Panel.Refresh(tracker.GetCurrent());
        Assert.True(harness.Panel.ShowCwKeyer);

        tracker.Mode = "FM";
        harness.Panel.Refresh(tracker.GetCurrent());
        Assert.False(harness.Panel.ShowCwKeyer);
        Assert.False(harness.Panel.TryHandleCwFunctionKey(0));
    }

    [Fact]
    public void Send_with_attached_entry_expands_call_and_shared_state_stays_in_step()
    {
        var tracker = new FakeTracker("CW");
        using var harness = Create(tracker);
        harness.Panel.AttachHost(CwKeyerHost.Logbook);
        harness.Panel.AttachHost(CwKeyerHost.Decoder);

        var entry = new FakeEntrySource(
            new CwMacroContext(MyCall: "MM9SQL", Call: "JE2WYA", RstSent: "599", MyGrid: "IO85"));
        harness.Bridge.AttachKeyerEntry(entry);

        Assert.True(harness.Panel.TryHandleCwFunctionKey(1)); // F2 Call = {CALL}
        Assert.True(harness.Panel.CwSending);
        Assert.True(harness.Panel.CwButtons[1].IsActive);
        Assert.Contains("JE2WYA", harness.Panel.StatusText, StringComparison.Ordinal);

        harness.Panel.DetachHost(CwKeyerHost.Decoder);
        Assert.True(harness.Keyer.IsSending);

        Assert.True(harness.Panel.TryHandleCwEscape());
        Assert.False(harness.Panel.CwSending);
        Assert.False(harness.Keyer.IsSending);
    }

    [Fact]
    public void Send_without_entry_expands_mycall_and_leaves_call_empty()
    {
        var tracker = new FakeTracker("CW");
        using var harness = Create(tracker, station: station =>
        {
            station.Callsign = "MM9SQL";
            station.GridSquare = "IO85";
        });
        harness.Panel.AttachHost(CwKeyerHost.Decoder);

        harness.Settings.Current.QsoLogbook.CwKeyer.Messages[2].Text = "{CALL} {MYCALL} {MYGRID}";
        harness.Panel.Reload();

        Assert.True(harness.Panel.TryHandleCwFunctionKey(2));
        Assert.Contains("MM9SQL", harness.Panel.StatusText, StringComparison.Ordinal);
        Assert.Contains("IO85", harness.Panel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("JE2WYA", harness.Panel.StatusText, StringComparison.Ordinal);

        harness.Panel.TryHandleCwEscape();
    }

    [Fact]
    public void Closing_last_host_stops_send_but_closing_one_of_two_leaves_it_running()
    {
        var tracker = new FakeTracker("CW");
        using var harness = Create(tracker, cw => cw.Wpm = 5);
        harness.Panel.AttachHost(CwKeyerHost.Logbook);
        harness.Panel.AttachHost(CwKeyerHost.Decoder);

        Assert.True(harness.Panel.TryHandleCwFunctionKey(0));
        Assert.True(harness.Keyer.IsSending);

        harness.Panel.DetachHost(CwKeyerHost.Logbook);
        Assert.True(harness.Keyer.IsSending);
        Assert.True(harness.Panel.CwSending);

        harness.Panel.DetachHost(CwKeyerHost.Decoder);
        Assert.False(harness.Keyer.IsSending);
        Assert.False(harness.Panel.CwSending);
    }

    [Fact]
    public void Leaving_cw_mode_stops_the_keyer()
    {
        var tracker = new FakeTracker("CW");
        using var harness = Create(tracker, cw => cw.Wpm = 5);
        harness.Panel.AttachHost(CwKeyerHost.Decoder);

        Assert.True(harness.Panel.TryHandleCwFunctionKey(0));
        Assert.True(harness.Panel.CwSending);

        tracker.Mode = "USB";
        harness.Panel.Refresh(tracker.GetCurrent());
        Assert.False(harness.Panel.ShowCwKeyer);
        Assert.False(harness.Panel.CwSending);
        Assert.False(harness.Keyer.IsSending);
    }

    [Fact]
    public void Ctrl_k_is_ignored_when_the_strip_is_hidden()
    {
        var tracker = new FakeTracker("USB");
        using var harness = Create(tracker);
        harness.Panel.AttachHost(CwKeyerHost.Logbook);

        Assert.False(harness.Panel.ShowCwKeyer);
        Assert.False(harness.Panel.TryHandleCtrlK(null!));
        Assert.False(harness.Panel.KeyboardOpen);
    }

    [Fact]
    public void Visible_buttons_exclude_empty_slots_and_reload_updates_them()
    {
        var tracker = new FakeTracker("CW");
        using var harness = Create(tracker);

        Assert.Equal(12, harness.Panel.CwButtons.Count);
        Assert.All(harness.Panel.VisibleCwButtons, b => Assert.True(b.HasMessage));
        Assert.Equal(
            harness.Panel.CwButtons.Count(b => b.HasMessage),
            harness.Panel.VisibleCwButtons.Count);

        harness.Settings.Current.QsoLogbook.CwKeyer.Messages[0].Text = "";
        harness.Settings.Current.QsoLogbook.CwKeyer.Messages[0].Label = "";
        harness.Panel.Reload();

        Assert.DoesNotContain(harness.Panel.VisibleCwButtons, b => b.Slot == 0);
        Assert.True(harness.Panel.TryHandleCwFunctionKey(0));
        Assert.False(harness.Panel.CwSending);
    }

    [Fact]
    public void Try_begin_edit_messages_asks_for_the_logbook_when_it_is_closed()
    {
        var tracker = new FakeTracker("CW");
        using var harness = Create(tracker);

        Assert.False(harness.Panel.TryBeginEditMessages());
        Assert.Contains("logbook", harness.Panel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Function_key_during_keyboard_session_appends_instead_of_replacing()
    {
        var tracker = new FakeTracker("CW");
        using var harness = Create(tracker, cw => cw.Wpm = 60);
        harness.Panel.AttachHost(CwKeyerHost.Logbook);

        var entry = new FakeEntrySource(
            new CwMacroContext(MyCall: "MM9SQL", Call: "JE2WYA", RstSent: "599", MyGrid: "IO85"));
        harness.Bridge.AttachKeyerEntry(entry);

        Assert.Equal(CwKeyerStartResult.Started, harness.Keyer.BeginKeyboard());
        var keyboard = new CwKeyboardViewModel(
            harness.Keyer,
            () => entry.BuildCwMacroContext(),
            LocalizationService.Instance);
        harness.Panel.AttachKeyboardForTests(keyboard);

        var progressed = new List<string>();
        harness.Keyer.KeyboardProgress += (_, e) =>
            progressed.Add(e.Symbol.IsWordGap ? " " : e.Symbol.Value);

        Assert.True(harness.Keyer.Enqueue(CwSymbol.Character('A')));
        Assert.True(harness.Panel.TryHandleCwFunctionKey(1)); // F2 Call = {CALL} -> JE2WYA
        Assert.True(SpinWait.SpinUntil(
            () => progressed.Count >= 1 + "JE2WYA".Length,
            TimeSpan.FromSeconds(5)));

        Assert.Contains("A", progressed);
        Assert.Contains("J", progressed);
        Assert.Contains("Y", progressed);
        Assert.DoesNotContain(harness.Panel.CwButtons, b => b.IsActive);

        harness.Keyer.Stop();
        harness.Panel.DetachKeyboardForTests();
    }

    private static Harness Create(
        FakeTracker tracker,
        Action<CwKeyerSettings>? cw = null,
        Action<GroundStation>? station = null)
    {
        var settings = new FakeSettings();
        settings.Current.Rig.Enabled = true;
        settings.Current.Rig.Type = RigType.KenwoodTs590sg;
        settings.Current.QsoLogbook.CwKeyer.Enabled = true;
        settings.Current.QsoLogbook.CwKeyer.Wpm = 60;
        cw?.Invoke(settings.Current.QsoLogbook.CwKeyer);
        station?.Invoke(settings.Current.GroundStation);

        var rig = new RecordingRigController();
        var keyer = new CwKeyerService(rig, settings);
        var bridge = new QsoLogbookEntryBridge();
        var panel = new CwKeyerPanelViewModel(
            keyer,
            settings,
            tracker,
            bridge,
            LocalizationService.Instance);
        panel.Refresh(tracker.GetCurrent());
        return new Harness(panel, keyer, bridge, settings, tracker);
    }

    private sealed class Harness : IDisposable
    {
        public Harness(
            CwKeyerPanelViewModel panel,
            CwKeyerService keyer,
            QsoLogbookEntryBridge bridge,
            FakeSettings settings,
            FakeTracker tracker)
        {
            Panel = panel;
            Keyer = keyer;
            Bridge = bridge;
            Settings = settings;
            Tracker = tracker;
        }

        public CwKeyerPanelViewModel Panel { get; }
        public CwKeyerService Keyer { get; }
        public QsoLogbookEntryBridge Bridge { get; }
        public FakeSettings Settings { get; }
        public FakeTracker Tracker { get; }

        public void Dispose() => Keyer.Dispose();
    }

    private sealed class FakeEntrySource(CwMacroContext context) : ICwKeyerEntrySource
    {
        public CwMacroContext BuildCwMacroContext() => context;
        public bool CanCommitQso => false;
        public Task CommitQsoFromCwAsync() => Task.CompletedTask;
    }

    private sealed class FakeTracker(string mode) : ILiveTrackerSnapshotProvider
    {
        public string Mode { get; set; } = mode;
        public string? FocusedNoradId { get; set; }

        public LiveTrackerSnapshot GetCurrent() =>
            new("JO-97", Mode, Mode, 145_900_000, 435_800_000, "2m", "70cm", "CW");
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public string SettingsPath => "";
        public string? LoadError => null;
        public bool CanPersist => true;
        public string SerializeCurrent() => "";
        public Task ReplaceAndSaveAsync(AppSettings imported, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
