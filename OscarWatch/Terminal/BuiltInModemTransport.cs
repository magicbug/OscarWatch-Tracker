using System.Diagnostics;
using System.Threading.Channels;
using OscarWatch.Core.Ft4;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Recording;
using OscarWatch.Core.Services;
using OscarWatch.Core.Terminal;
using OscarWatch.Core.Terminal.Modem;
using OscarWatch.Hardware;
using Serilog;

namespace OscarWatch.Terminal;

public enum TerminalTransmitFailure
{
    /// <summary>Half duplex: the downlink stayed busy for the whole maximum channel wait.</summary>
    ChannelBusy,

    /// <summary>The radio could not move to the uplink before keying (FT-847 same-band hold).</summary>
    UplinkHoldFailed,

    /// <summary>The transmission ran past the watchdog limit and PTT was dropped.</summary>
    Watchdog,

    /// <summary>PTT or the soundcard failed.</summary>
    Hardware
}

public sealed class TerminalTransmitException(TerminalTransmitFailure failure, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public TerminalTransmitFailure Failure { get; } = failure;
}

/// <summary>
/// OscarWatch's built-in soundcard packet modem (1200 baud AFSK and 9600 baud G3RUH). Receives on a
/// decoder thread and transmits from a queue: channel access when half duplex, the before-key callback,
/// PTT, TXDELAY flags, the frames (with frames queued meanwhile chained into the same transmission),
/// TXTAIL flags, then unkey.
/// </summary>
public sealed class BuiltInModemTransport : ITerminalModemTransport
{
    private static readonly ILogger Log = Serilog.Log.ForContext<BuiltInModemTransport>();
    private static readonly TimeSpan CaptureWait = TimeSpan.FromMilliseconds(100);

    /// <summary>Queued audio needed to append another frame without a gap in the carrier.</summary>
    internal const double ChainMarginSeconds = 0.03;

    private readonly IPacketAudio _audio;
    private readonly PttKeyer _ptt;
    private readonly Func<TerminalSettings> _settings;
    private readonly IRigController? _rig;
    private readonly Random _random;
    private readonly bool _ownsAudio;
    private readonly Channel<TxItem> _queue = Channel.CreateUnbounded<TxItem>();
    private readonly List<TxItem> _carry = [];
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private Task? _txTask;
    private Task? _rxTask;
    private Task? _rigTask;
    private volatile bool _rxBlank;
    private long _blankUntilTimestamp;
    private volatile bool _dcd;
    private volatile bool _transmitting;
    private volatile TerminalLinkType _linkType;
    private int _decoded;
    private int _fixed;
    private int _queueDepth;
    private int _disposed;
    private readonly RecentSampleBuffer _recent = new(SpectrumWindow);

    private const int SpectrumWindow = 4096;

    private sealed record TxItem(byte[] Frame, TaskCompletionSource Done);

    public BuiltInModemTransport(
        IPacketAudio audio,
        PttKeyer ptt,
        Func<TerminalSettings> settings,
        IRigController? rig = null,
        Random? random = null,
        bool ownsAudio = true)
    {
        _audio = audio;
        _ptt = ptt;
        _settings = settings;
        _rig = rig;
        _random = random ?? Random.Shared;
        _ownsAudio = ownsAudio;
    }

    public TerminalTransportKind Kind => TerminalTransportKind.BuiltIn;

    public TerminalTransportCapabilities Capabilities { get; } = new(
        RadioPortList: false,
        OwnTxEcho: true,
        SignalQuality: true,
        TxQueueDepth: true);

    public TerminalTransportState State { get; private set; } = TerminalTransportState.Disconnected;

    public string? ModemDescription =>
        State == TerminalTransportState.Connected
            ? DataRate switch
            {
                BuiltInModemDataRate.G3ruh9600 =>
                    $"OscarWatch 9600 G3RUH, {(IsHalfDuplex ? "half duplex" : "full duplex")}",
                _ => $"OscarWatch 1200 AFSK, {(IsHalfDuplex ? "half duplex" : "full duplex")}",
            }
            : null;

    /// <summary>1200 AFSK or 9600 G3RUH, for the Terminal status line.</summary>
    public string RateName =>
        DataRate == BuiltInModemDataRate.G3ruh9600 ? "9600 G3RUH" : "1200 AFSK";

    public event Action<TerminalTransportState>? StateChanged;
    public event Action<TerminalReceivedFrame>? FrameReceived;

    /// <summary>Set from the focused satellite so Auto duplex follows it without a reconnect.</summary>
    public TerminalLinkType LinkType
    {
        get => _linkType;
        set => _linkType = value;
    }

    /// <summary>Profile modem hint for Automatic 1200 vs 9600 selection.</summary>
    public string? ModemHint { get; set; }

    private BuiltInModemDataRate DataRate =>
        BuiltInModemKind.ResolveDataRate(_settings().BuiltIn, ModemHint);

    /// <summary>
    /// Runs just before PTT with the expected time on the air. Returning false cancels the transmission.
    /// Called again, without waiting, when frames are chained onto a transmission already keyed.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task<bool>>? BeforeKey { get; set; }

    public bool IsHalfDuplex => ResolveHalfDuplex(_settings().BuiltIn.Duplex, LinkType);

    public bool IsTransmitting => _transmitting;

    /// <summary>Data carrier detect from the decoder bank (not audio energy).</summary>
    public bool Dcd => _dcd;

    public double InputLevel => _audio.InputPeak;

    public int DecodedFrames => Volatile.Read(ref _decoded);

    public int FixedFrames => Volatile.Read(ref _fixed);

    public int QueueDepth => Volatile.Read(ref _queueDepth);

    /// <summary>dB spectrum of the latest receive audio across <paramref name="bins"/>, for the waterfall.</summary>
    public bool TryBuildSpectrum(Span<float> bins, double minHz, double maxHz)
    {
        if (State != TerminalTransportState.Connected)
            return false;
        var scratch = new float[SpectrumWindow];
        var n = _recent.CopyNewest(scratch);
        return n >= 512 && Ft4SpectrumAnalyzer.TryComputePassband(scratch.AsSpan(0, n), _audio.CaptureSampleRate, bins, minHz, maxHz);
    }

    /// <summary>Auto picks half duplex unless the satellite is crossband; with no satellite it is half duplex.</summary>
    public static bool ResolveHalfDuplex(TerminalDuplexMode mode, TerminalLinkType link) =>
        mode switch
        {
            TerminalDuplexMode.Half => true,
            TerminalDuplexMode.Full => false,
            _ => link != TerminalLinkType.Crossband
        };

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            return Task.CompletedTask;

        SetState(TerminalTransportState.Connecting);
        var modem = _settings().BuiltIn;
        try
        {
            _audio.Start(modem.InputDeviceId, modem.InputDeviceDisplayName, modem.OutputDeviceId, modem.OutputDeviceDisplayName);
            if (DataRate == BuiltInModemDataRate.G3ruh9600
                && _audio.CaptureSampleRate < G3ruhDemodulator.MinSampleRate)
            {
                throw new InvalidOperationException(
                    $"9600 baud G3RUH needs a capture rate of at least {G3ruhDemodulator.MinSampleRate} Hz " +
                    $"(this device is {_audio.CaptureSampleRate} Hz). Choose a 48 kHz data-port device or set the modem to 1200 AFSK.");
            }
        }
        catch
        {
            SetState(TerminalTransportState.Error);
            throw;
        }

        // A radio left keyed over CAT by a crash goes back to receive before anything else.
        _ptt.ForceOff();
        _recent.Clear();

        var cts = new CancellationTokenSource();
        _cts = cts;
        _rxTask = Task.Factory.StartNew(() => ReceiveLoop(cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _txTask = Task.Run(() => TransmitLoopAsync(cts.Token), cts.Token);
        if (_rig is not null)
            _rigTask = Task.Run(() => RigReconnectLoopAsync(cts.Token), cts.Token);
        SetState(TerminalTransportState.Connected);
        Log.Information("Terminal built-in modem started (capture {CaptureRate} Hz, playback {PlaybackRate} Hz)",
            _audio.CaptureSampleRate, _audio.PlaybackSampleRate);
        return Task.CompletedTask;
    }

    public Task WaitForDisconnectAsync(CancellationToken cancellationToken) =>
        _cts is null ? Task.CompletedTask : _disconnected.Task.WaitAsync(cancellationToken);

    public async Task SendAsync(ReadOnlyMemory<byte> ax25Frame, int radioPort, CancellationToken cancellationToken)
    {
        if (State != TerminalTransportState.Connected)
            throw new InvalidOperationException("The built-in modem is not running.");

        var item = new TxItem(ax25Frame.ToArray(), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Interlocked.Increment(ref _queueDepth);
        if (!_queue.Writer.TryWrite(item))
        {
            Interlocked.Decrement(ref _queueDepth);
            throw new InvalidOperationException("The built-in modem is not running.");
        }

        await item.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<TerminalRadioPort>> GetRadioPortsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TerminalRadioPort>>([
            new TerminalRadioPort(0, DataRate == BuiltInModemDataRate.G3ruh9600 ? "OscarWatch 9600 G3RUH" : "OscarWatch 1200 AFSK")
        ]);

    internal TimeSpan CaptureStallTimeout
    {
        get => _stallWatch.Timeout;
        set => _stallWatch.Timeout = value;
    }

    internal int CaptureRestarts => Volatile.Read(ref _captureRestarts);

    private int _captureRestarts;
    private readonly CaptureStallWatch _stallWatch = new(DateTime.UtcNow);

    private void ReceiveLoop(CancellationToken ct)
    {
        BuiltInModemDataRate? activeRate = null;
        bool? activeFrequencyOffsetDecoders = null;
        AfskReceiver? afsk = null;
        G3ruhReceiver? g3ruh = null;
        var buffer = new float[4096];
        var frames = new List<AfskDecodedFrame>();
        _stallWatch.Reset(DateTime.UtcNow);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var rate = DataRate;
                var freqOffset = _settings().BuiltIn.FrequencyOffsetDecoders;
                if (rate != activeRate || (rate == BuiltInModemDataRate.Afsk1200 && freqOffset != activeFrequencyOffsetDecoders))
                {
                    activeRate = rate;
                    activeFrequencyOffsetDecoders = freqOffset;
                    afsk = rate == BuiltInModemDataRate.Afsk1200
                        ? new AfskReceiver(_audio.CaptureSampleRate, _settings().BuiltIn.BitFix, freqOffset)
                        : null;
                    g3ruh = rate == BuiltInModemDataRate.G3ruh9600
                        ? new G3ruhReceiver(_audio.CaptureSampleRate, _settings().BuiltIn.BitFix)
                        : null;
                }

                var gotAudio = _audio.WaitForCapture(CaptureWait, ct);
                if (_stallWatch.OnRead(DateTime.UtcNow, gotAudio, paused: _transmitting)
                    && RestartAudioAfterStall())
                {
                    activeRate = null;
                    continue;
                }

                if (!gotAudio)
                    continue;

                int n;
                while ((n = _audio.ReadCapture(buffer)) > 0)
                {
                    _recent.Write(buffer.AsSpan(0, n));
                    // The turnaround guard delays the next transmission. It does not blank the decoder:
                    // a copy of our own packet is classified as an echo, and a digipeat can be heard at once.
                    var blanked = _rxBlank;
                    var bitFix = _settings().BuiltIn.BitFix;
                    if (g3ruh is not null)
                    {
                        g3ruh.BitFix = bitFix;
                        g3ruh.Blanked = blanked;
                        g3ruh.Process(buffer.AsSpan(0, n), frames);
                        _dcd = !g3ruh.Blanked && g3ruh.Dcd;
                        Volatile.Write(ref _decoded, g3ruh.FrameCount);
                        Volatile.Write(ref _fixed, g3ruh.FixedCount);
                    }
                    else if (afsk is not null)
                    {
                        afsk.BitFix = bitFix;
                        afsk.Blanked = blanked;
                        afsk.Process(buffer.AsSpan(0, n), frames);
                        _dcd = !afsk.Blanked && afsk.Dcd;
                        Volatile.Write(ref _decoded, afsk.FrameCount);
                        Volatile.Write(ref _fixed, afsk.FixedCount);
                    }

                    foreach (var frame in frames)
                    {
                        try
                        {
                            FrameReceived?.Invoke(new TerminalReceivedFrame(
                                frame.Ax25, DateTime.UtcNow, 0, frame.Quality, frame.DecoderName));
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Terminal built-in modem frame handler failed");
                        }
                    }

                    frames.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal built-in modem receive loop failed");
            Fail();
        }
    }

    private bool RestartAudioAfterStall()
    {
        var modem = _settings().BuiltIn;
        Log.Warning("Terminal built-in modem heard no audio from '{Device}' for {Seconds:0} s; reopening the soundcard",
            modem.InputDeviceDisplayName, CaptureStallTimeout.TotalSeconds);
        Interlocked.Increment(ref _captureRestarts);
        try
        {
            _audio.Start(modem.InputDeviceId, modem.InputDeviceDisplayName, modem.OutputDeviceId, modem.OutputDeviceDisplayName);
            _recent.Clear();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Terminal built-in modem could not reopen the soundcard");
            return false;
        }
    }

    private async Task TransmitLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = new List<TxItem>();
                if (_carry.Count > 0)
                {
                    batch.AddRange(_carry);
                    _carry.Clear();
                }
                else
                {
                    batch.Add(await _queue.Reader.ReadAsync(ct).ConfigureAwait(false));
                }

                try
                {
                    await TransmitBatchAsync(batch, ct).ConfigureAwait(false);
                    Complete(batch, null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    Complete(batch, new OperationCanceledException(ct));
                    throw;
                }
                catch (TerminalTransmitException ex)
                {
                    Log.Warning("Terminal built-in modem transmit failed: {Failure} {Message}", ex.Failure, ex.Message);
                    Complete(batch, ex);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Terminal built-in modem transmit failed");
                    Complete(batch, new TerminalTransmitException(TerminalTransmitFailure.Hardware, ex.Message, ex));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            Complete(_carry, new OperationCanceledException());
            _carry.Clear();
            while (_queue.Reader.TryRead(out var item))
                Complete([item], new OperationCanceledException());
        }
    }

    private async Task TransmitBatchAsync(List<TxItem> batch, CancellationToken ct)
    {
        var settings = _settings();
        var modem = settings.BuiltIn;
        var half = ResolveHalfDuplex(modem.Duplex, LinkType);

        if ((half || modem.WaitForClearChannelInFullDuplex) && !await WaitForChannelAsync(modem, ct).ConfigureAwait(false))
        {
            DrainQueue(batch);
            throw new TerminalTransmitException(TerminalTransmitFailure.ChannelBusy,
                $"Channel busy for {modem.MaxChannelWaitMs} ms");
        }

        DrainQueue(batch);
        if (BeforeKey is { } beforeKey
            && !await beforeKey(Airtime(batch, settings), ct).ConfigureAwait(false))
        {
            throw new TerminalTransmitException(TerminalTransmitFailure.UplinkHoldFailed, "Uplink hold did not start");
        }

        var watchdogAt = Stopwatch.GetTimestamp() + (long)(modem.WatchdogSeconds * (double)Stopwatch.Frequency);
        var clean = false;
        if (half)
            _rxBlank = true;
        try
        {
            await _ptt.KeyAsync(ct).ConfigureAwait(false);
            _transmitting = true;
            var baud = BuiltInModemKind.BaudFor(DataRate);
            if (DataRate == BuiltInModemDataRate.G3ruh9600
                && _audio.PlaybackSampleRate < G3ruhDemodulator.MinSampleRate)
            {
                throw new InvalidOperationException(
                    $"9600 baud G3RUH needs playback at least {G3ruhDemodulator.MinSampleRate} Hz.");
            }

            if (DataRate == BuiltInModemDataRate.G3ruh9600)
            {
                var g3 = new G3ruhStreamModulator(_audio.PlaybackSampleRate, modem.TxLevel9600);
                var g3Encode = new G3ruhStreamEncoder();
                var delayFlags = HdlcEncoder.FlagsFor(settings.ModemTxDelayMs, baud);
                _audio.Enqueue(g3.Append(g3Encode.AppendFrames(batch.Select(b => b.Frame), delayFlags, 0)));
                await EnqueueChainedAsync(g3, g3Encode, batch, settings, watchdogAt, modem, ct, g3ruh: true)
                    .ConfigureAwait(false);
                var tailG3 = Math.Max(1, HdlcEncoder.FlagsFor(settings.ModemTxTailMs, baud) - 1);
                _audio.Enqueue(g3.Finish(g3Encode.AppendFlags(tailG3)));
            }
            else
            {
                var modulator = new AfskStreamModulator(_audio.PlaybackSampleRate, modem.TxLevel);
                _audio.Enqueue(modulator.Append(HdlcEncoder.EncodeBits(
                    batch.Select(b => b.Frame), HdlcEncoder.FlagsFor(settings.ModemTxDelayMs), 0)));
                await EnqueueChainedAsync(modulator, null, batch, settings, watchdogAt, modem, ct, g3ruh: false)
                    .ConfigureAwait(false);
                var tailFlags = Math.Max(1, HdlcEncoder.FlagsFor(settings.ModemTxTailMs) - 1);
                _audio.Enqueue(modulator.Finish(HdlcEncoder.FlagBits(tailFlags)));
            }

            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), watchdogAt);
            try
            {
                await _audio.WaitForDrainAsync(ct).WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw Watchdog(modem);
            }

            // The last samples are still in the soundcard's buffer when the queue empties.
            var latency = TimeSpan.FromSeconds(Math.Clamp(_audio.OutputLatencySeconds, 0, 0.5));
            if (latency > TimeSpan.Zero)
                await Task.Delay(latency, ct).ConfigureAwait(false);
            clean = true;
        }
        finally
        {
            if (!clean)
                _audio.ClearPlayback();
            if (clean)
                await _ptt.UnkeyAsync(CancellationToken.None).ConfigureAwait(false);
            else
                _ptt.UnkeyNow();
            _transmitting = false;
            if (half)
            {
                // Holds the next transmission only. Receive blanking ends with _rxBlank.
                var guard = (long)(modem.TurnaroundGuardMs / 1000.0 * Stopwatch.Frequency);
                Interlocked.Exchange(ref _blankUntilTimestamp, Stopwatch.GetTimestamp() + guard);
                _rxBlank = false;
            }
        }
    }

    private async Task EnqueueChainedAsync(
        object modulator,
        G3ruhStreamEncoder? g3Encoder,
        List<TxItem> batch,
        TerminalSettings settings,
        long watchdogAt,
        TerminalBuiltInModemSettings modem,
        CancellationToken ct,
        bool g3ruh)
    {
        var margin = (long)(_audio.PlaybackSampleRate * ChainMarginSeconds);
        while (true)
        {
            if (Stopwatch.GetTimestamp() >= watchdogAt)
                throw Watchdog(modem);

            var more = new List<TxItem>();
            DrainQueue(more);
            if (more.Count > 0)
            {
                if (_audio.QueuedPlaybackSamples > margin)
                {
                    if (g3ruh)
                    {
                        var bits = g3Encoder!.AppendContinuationFrames(more.Select(m => m.Frame));
                        _audio.Enqueue(((G3ruhStreamModulator)modulator).Append(bits));
                    }
                    else
                    {
                        var bits = HdlcEncoder.EncodeBits(more.Select(m => m.Frame), 1, 0);
                        _audio.Enqueue(((AfskStreamModulator)modulator).Append(bits));
                    }
                    batch.AddRange(more);
                    if (BeforeKey is { } extend)
                        _ = extend(Airtime(more, settings, withDelay: false), CancellationToken.None);
                    continue;
                }

                _carry.AddRange(more);
                break;
            }

            if (_audio.QueuedPlaybackSamples <= margin)
                break;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    private static TerminalTransmitException Watchdog(TerminalBuiltInModemSettings modem) =>
        new(TerminalTransmitFailure.Watchdog, $"Transmit watchdog: on the air for {modem.WatchdogSeconds} s");

    /// <summary>p-persistence CSMA: wait for DCD to clear, then key with probability (persist + 1) / 256 per slot.</summary>
    private async Task<bool> WaitForChannelAsync(TerminalBuiltInModemSettings modem, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(modem.MaxChannelWaitMs / 1000.0 * Stopwatch.Frequency);
        while (true)
        {
            if (!_dcd && !InGuard() && _random.Next(256) <= modem.Persist)
                return true;
            if (Stopwatch.GetTimestamp() >= deadline)
                return false;
            await Task.Delay(modem.SlotTimeMs, ct).ConfigureAwait(false);
        }
    }

    private bool InGuard() => Stopwatch.GetTimestamp() < Interlocked.Read(ref _blankUntilTimestamp);

    private void DrainQueue(List<TxItem> into)
    {
        while (_queue.Reader.TryRead(out var item))
            into.Add(item);
    }

    private TimeSpan Airtime(List<TxItem> items, TerminalSettings settings, bool withDelay = true)
    {
        var baud = BuiltInModemKind.BaudFor(DataRate);
        var total = TimeSpan.Zero;
        foreach (var item in items)
            total += TerminalTransmitWindow.FrameAirtime(item.Frame.Length, baud);
        if (withDelay)
            total += TimeSpan.FromMilliseconds(settings.ModemTxDelayMs + settings.ModemTxTailMs + settings.BuiltIn.PttLeadMs);
        return total;
    }

    private void Complete(List<TxItem> items, Exception? error)
    {
        foreach (var item in items)
        {
            if (error is null)
                item.Done.TrySetResult();
            else
                item.Done.TrySetException(error);
            Interlocked.Decrement(ref _queueDepth);
        }
    }

    /// <summary>CAT PTT off whenever the radio reconnects, in case it came back still transmitting.</summary>
    private async Task RigReconnectLoopAsync(CancellationToken ct)
    {
        var wasConnected = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var connected = _rig!.GetStatus().IsConnected;
                if (connected && !wasConnected && !_transmitting && _ptt.Method == PttMethod.Cat)
                {
                    Log.Information("Radio reconnected; sending Terminal PTT off");
                    _ptt.ForceOff();
                }

                wasConnected = connected;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Terminal rig reconnect watch stopped");
        }
    }

    private void Fail()
    {
        SetState(TerminalTransportState.Error);
        _disconnected.TrySetResult();
    }

    private void SetState(TerminalTransportState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var cts = _cts;
        cts?.Cancel();
        _queue.Writer.TryComplete();
        _audio.ClearPlayback();
        _ptt.UnkeyNow();

        foreach (var task in new[] { _txTask, _rxTask, _rigTask })
        {
            if (task is null)
                continue;
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        _ptt.UnkeyNow();
        if (_ownsAudio)
        {
            _audio.Stop();
            _audio.Dispose();
        }

        _ptt.Dispose();
        cts?.Dispose();
        SetState(TerminalTransportState.Disconnected);
        _disconnected.TrySetResult();
    }
}
