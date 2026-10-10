namespace OscarWatch.Core.Sstv;

public enum SstvDecoderState
{
    Searching,
    Decoding,
}

public sealed class SstvDecoderOptions
{
    /// <summary>Decode as this mode even if the VIS says otherwise. Null picks the mode automatically.</summary>
    public SstvModeId? ForcedMode { get; set; }

    /// <summary>Measure the line period from the sync pulses, so clock error does not slant the picture.</summary>
    public bool AutoSlant { get; set; } = true;

    /// <summary>Clock correction in parts per million, used when <see cref="AutoSlant"/> is off.</summary>
    public double SlantTrimPpm { get; set; }

    /// <summary>Follow the sync tone frequency line by line (audio AFC).</summary>
    public bool AutoTune { get; set; } = true;

    /// <summary>Fixed audio trim in Hz, added to the AFC estimate.</summary>
    public double TuningOffsetHz { get; set; }

    /// <summary>Sync score (0 to 1) a line needs to be placed on its own sync.</summary>
    public double SyncThreshold { get; set; } = 0.45;

    /// <summary>Look for a regular run of sync pulses when the VIS header was lost.</summary>
    public bool DetectWithoutVis { get; set; } = true;
}

public sealed class SstvImageStartedEventArgs(SstvMode mode, bool fromVis, double offsetHz) : EventArgs
{
    public SstvMode Mode { get; } = mode;
    public bool FromVis { get; } = fromVis;
    public double OffsetHz { get; } = offsetHz;
}

public sealed class SstvLinesUpdatedEventArgs(int firstRow, int lastRow) : EventArgs
{
    public int FirstRow { get; } = firstRow;
    public int LastRow { get; } = lastRow;
}

/// <summary>A finished (or cut short) picture.</summary>
public sealed class SstvDecodedImage
{
    public required SstvMode Mode { get; init; }
    public required SstvImage Image { get; init; }
    public required IReadOnlyList<SstvLineQuality> LineQuality { get; init; }
    public required bool FromVis { get; init; }
    public required bool Complete { get; init; }
    public required double LockPercent { get; init; }
    public required double OffsetHz { get; init; }
    public required double ClockPpm { get; init; }
    /// <summary>Seconds into the decoder input where the picture started.</summary>
    public required double StartSeconds { get; init; }
    public required double EndSeconds { get; init; }
}

/// <summary>
/// Streaming SSTV receiver. Feed soundcard audio to <see cref="Process"/>; events report
/// the mode, rows as they are decoded, and the finished picture. Not thread-safe: call
/// from one thread.
/// </summary>
public sealed class SstvDecoder
{
    public const int InternalRate = 12000;

    private const double SearchKeepSeconds = 14;
    private const double SyncTrainIntervalSeconds = 0.5;
    private const int FalseStartCheckLine = 16;
    private const int FalseStartMinLocked = 4;
    private const double MaxOffsetHz = 500;
    private const double LostSignalSeconds = 8;
    private const int TrailingMissesToDrop = 4;
    /// <summary>Only clean syncs steer the AFC, so a noisy line cannot pull the colours.</summary>
    private const double AfcMinScore = 0.6;

    private readonly SstvResampler _resampler;
    private readonly SstvFmDemodulator _demod = new(InternalRate);
    private readonly SstvVisDetector _vis = new(InternalRate);
    private readonly SstvSyncTrainDetector _train = new(InternalRate);
    private readonly SstvSignalBuffer _buffer = new();
    private readonly List<float> _resampled = new(4096);
    private readonly List<float> _demodulated = new(4096);
    private long _lastTrainCheck;
    private long _trainFrom;
    private double _searchOffsetHz;

    // Current picture.
    private SstvMode? _mode;
    private SstvImage? _image;
    private SstvLineRenderer? _renderer;
    private SstvLineTracker? _tracker;
    private SstvLineQuality[] _quality = [];
    private double[] _lineOffsetHz = [];
    private int _nextLine;
    private int _missesSinceLock;
    private int _lastLockedLine = -1;
    private double _afcHz;
    private bool _fromVis;
    private long _imageStartIndex;
    private double _feedForwardHz;

    public SstvDecoder(int inputSampleRate, SstvDecoderOptions? options = null)
    {
        _resampler = new SstvResampler(inputSampleRate, InternalRate);
        Options = options ?? new SstvDecoderOptions();
        _vis.Reset(0);
    }

    public SstvDecoderOptions Options { get; }

    public SstvDecoderState State => _mode is null ? SstvDecoderState.Searching : SstvDecoderState.Decoding;

    public SstvMode? CurrentMode => _mode;

    /// <summary>True when the picture being decoded was announced by a VIS header.</summary>
    public bool CurrentFromVis => _mode is not null && _fromVis;

    /// <summary>The picture being decoded. Read it from the decoder thread or copy it first.</summary>
    public SstvImage? CurrentImage => _image;

    public int LinesDecoded => _nextLine;

    public IReadOnlyList<SstvLineQuality> LineQuality => _quality;

    /// <summary>Audio offset in Hz currently removed (AFC plus trim).</summary>
    public double OffsetHz => _afcHz + Options.TuningOffsetHz;

    /// <summary>Measured clock error of the picture in parts per million.</summary>
    public double ClockPpm =>
        _tracker is null || _nextLine == 0
            ? 0
            : (_tracker.PeriodAt(Math.Max(0, _nextLine - 1), centred: false) / _tracker.NominalSamplesPerLine - 1) * 1e6;

    public double LockPercent
    {
        get
        {
            if (_nextLine == 0)
                return 0;
            var locked = 0;
            for (var i = 0; i < _nextLine; i++)
            {
                if (_quality[i] == SstvLineQuality.Locked)
                    locked++;
            }

            return 100.0 * locked / _nextLine;
        }
    }

    /// <summary>Seconds of input processed so far.</summary>
    public double ElapsedSeconds => (double)_buffer.End / InternalRate;

    /// <summary>
    /// Audio offset in Hz known from rig control (for example the gap between the Doppler
    /// target and the dial on SSB). It is removed from every sample before decoding.
    /// </summary>
    public double FeedForwardHz
    {
        get => Volatile.Read(ref _feedForwardHz);
        set => Volatile.Write(ref _feedForwardHz, double.IsFinite(value) ? value : 0);
    }

    public event EventHandler<SstvImageStartedEventArgs>? ImageStarted;
    public event EventHandler<SstvLinesUpdatedEventArgs>? LinesUpdated;
    public event EventHandler<SstvDecodedImage>? ImageCompleted;
    public event EventHandler? ImageAbandoned;

    public void Process(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return;

        _resampled.Clear();
        _resampler.Process(samples, _resampled);
        if (_resampled.Count == 0)
            return;

        _demodulated.Clear();
        _demod.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_resampled), _demodulated);
        var ff = (float)FeedForwardHz;
        if (ff != 0)
        {
            for (var i = 0; i < _demodulated.Count; i++)
                _demodulated[i] -= ff;
        }

        _buffer.Append(_demodulated);
        Pump();
    }

    /// <summary>End of input: hand over a partly received picture if it is worth keeping.</summary>
    public void Flush()
    {
        if (_mode is null)
            return;

        if (_missesSinceLock >= TrailingMissesToDrop)
        {
            EndAfterLastLock();
            return;
        }

        if (_nextLine >= MinKeptLines(_mode))
            FinishImage(complete: false);
        else
            AbandonImage();
    }

    private static int MinKeptLines(SstvMode mode) => Math.Max(8, mode.TransmittedLines / 10);

    /// <summary>Lines without a sync after which the transmission is taken to have ended.</summary>
    private static int LostSignalLines(SstvMode mode) =>
        Math.Max(16, (int)Math.Ceiling(LostSignalSeconds * 1000 / mode.LineMs));

    /// <summary>
    /// The sender stopped (or the pass ended) mid-picture: drop the lines decoded from
    /// noise since the last sync, and keep the rest if there is enough of it.
    /// </summary>
    private void EndAfterLastLock()
    {
        var mode = _mode!;
        var keep = _lastLockedLine + 1;
        for (var i = keep; i < _nextLine; i++)
            _quality[i] = SstvLineQuality.Missing;
        _image!.ClearFromRow(keep * mode.RowsPerLine);
        _nextLine = keep;
        if (keep >= MinKeptLines(mode))
            FinishImage(complete: false);
        else
            AbandonImage();
    }

    /// <summary>Redraw the current picture using the line period measured across the whole picture.</summary>
    public void ReSync()
    {
        if (_mode is null || _tracker is null || _renderer is null || _nextLine == 0)
            return;

        _tracker.AutoSlant = Options.AutoSlant;
        _tracker.ManualPeriod = _tracker.NominalSamplesPerLine * (1 + Options.SlantTrimPpm * 1e-6);
        for (var line = 0; line < _nextLine; line++)
            RenderLine(line, centred: true);
        LinesUpdated?.Invoke(this, new SstvLinesUpdatedEventArgs(0, Math.Min(_mode.Height, _nextLine * _mode.RowsPerLine) - 1));
    }

    /// <summary>Drop the current picture and listen for the next one.</summary>
    public void Reset()
    {
        if (_mode is not null)
            AbandonImage();
    }

    private void Pump()
    {
        while (true)
        {
            if (_mode is null)
            {
                if (!Search())
                    return;
                continue;
            }

            if (!DecodeAvailableLines())
                return;
        }
    }

    private SstvMode? Forced => Options.ForcedMode is { } id ? SstvModeTable.Get(id) : null;

    private bool Search()
    {
        if (_vis.TryFind(_buffer, requireFullHeader: false, out var vis))
        {
            var mode = Forced ?? vis.Mode!;
            _searchOffsetHz = vis.OffsetHz;
            var line0Start = vis.ImageStartIndex + mode.LeadInMs * InternalRate / 1000.0;
            StartImage(mode, line0Start + mode.SyncOffsetMs * InternalRate / 1000.0, vis.OffsetHz, fromVis: true);
            return true;
        }

        if (Options.DetectWithoutVis
            && _buffer.End - _lastTrainCheck >= SyncTrainIntervalSeconds * InternalRate)
        {
            _lastTrainCheck = _buffer.End;
            var candidates = Forced is { } f ? new[] { f } : SstvModeTable.All;
            if (_train.TryFind(_buffer, Math.Max(_buffer.Start, _trainFrom), _searchOffsetHz, candidates, out var train))
            {
                StartImage(train.Mode, train.FirstSyncIndex, train.OffsetHz, fromVis: false);
                return true;
            }
        }

        var keep = (long)(SearchKeepSeconds * InternalRate);
        if (_buffer.End - _buffer.Start > keep * 2)
            _buffer.TrimBefore(_buffer.End - keep);
        return false;
    }

    private void StartImage(SstvMode mode, double line0Sync, double offsetHz, bool fromVis)
    {
        _mode = mode;
        _image = new SstvImage(mode.Width, mode.Height);
        _renderer = new SstvLineRenderer(mode, _image);
        var nominal = mode.LineMs * InternalRate / 1000.0;
        _tracker = new SstvLineTracker(nominal)
        {
            AutoSlant = Options.AutoSlant,
            ManualPeriod = nominal * (1 + Options.SlantTrimPpm * 1e-6),
        };
        _tracker.SetAnchor(line0Sync);
        _quality = new SstvLineQuality[mode.TransmittedLines];
        _lineOffsetHz = new double[mode.TransmittedLines];
        _nextLine = 0;
        _missesSinceLock = 0;
        _lastLockedLine = -1;
        _afcHz = Options.AutoTune ? Math.Clamp(offsetHz, -MaxOffsetHz, MaxOffsetHz) : 0;
        _fromVis = fromVis;
        _imageStartIndex = (long)(line0Sync - mode.SyncOffsetMs * InternalRate / 1000.0 - mode.LeadInMs * InternalRate / 1000.0);
        _buffer.TrimBefore(_imageStartIndex - InternalRate);
        ImageStarted?.Invoke(this, new SstvImageStartedEventArgs(mode, fromVis, _afcHz));
    }

    /// <returns>True when the picture finished or was dropped, so searching should resume.</returns>
    private bool DecodeAvailableLines()
    {
        var mode = _mode!;
        var tracker = _tracker!;
        var rate = InternalRate;
        var nominal = tracker.NominalSamplesPerLine;
        var syncLen = (int)Math.Round(mode.SyncMs * rate / 1000.0);
        var syncOffset = mode.SyncOffsetMs * rate / 1000.0;
        var firstRow = -1;
        var lastRow = -1;

        while (_nextLine < mode.TransmittedLines)
        {
            var line = _nextLine;
            var predicted = tracker.Predict(line);
            // After a VIS the first sync is placed to within a few ms, and in most modes it
            // runs straight on from the 1200 Hz stop bit, which a wide search would latch onto.
            var half = line == 0 && _fromVis
                ? (int)(0.003 * rate)
                : HalfWindow(nominal, tracker.AcceptedCount);
            var lineStart = predicted - syncOffset;
            if (_buffer.End < lineStart + nominal + half + syncLen)
                break;

            if (line > 0 && ShouldYieldToNewHeader(predicted))
                return true;

            var hit = SstvSyncDetector.Find(_buffer, predicted, half, syncLen, _afcHz + Options.TuningOffsetHz);            if (hit is { } h && h.Score >= Options.SyncThreshold)
            {
                tracker.Accept(line, h.Position, h.Score);
                _quality[line] = SstvLineQuality.Locked;
                _missesSinceLock = 0;
                _lastLockedLine = line;
                if (Options.AutoTune && h.Score >= AfcMinScore && !double.IsNaN(h.MeasuredHz))
                {
                    var estimate = h.MeasuredHz - Options.TuningOffsetHz - SstvSyncDetector.SyncHz;
                    _afcHz = Math.Clamp(_afcHz + 0.2 * (estimate - _afcHz), -MaxOffsetHz, MaxOffsetHz);
                }
            }
            else
            {
                _quality[line] = SstvLineQuality.Predicted;
                _missesSinceLock++;
            }

            _lineOffsetHz[line] = Options.AutoTune ? _afcHz : 0;
            RenderLine(line, centred: false);
            if (mode.Id == SstvModeId.Robot36 && line > 0)
                firstRow = firstRow < 0 ? line - 1 : Math.Min(firstRow, line - 1);
            var row = line * mode.RowsPerLine;
            firstRow = firstRow < 0 ? row : Math.Min(firstRow, row);
            lastRow = Math.Min(mode.Height - 1, row + mode.RowsPerLine - 1);
            _nextLine++;

            if (_nextLine == FalseStartCheckLine && CountLocked() < FalseStartMinLocked)
            {
                AbandonImage();
                return true;
            }

            if (_missesSinceLock >= LostSignalLines(mode))
            {
                if (firstRow >= 0)
                    LinesUpdated?.Invoke(this, new SstvLinesUpdatedEventArgs(firstRow, lastRow));
                EndAfterLastLock();
                return true;
            }
        }

        if (firstRow >= 0)
            LinesUpdated?.Invoke(this, new SstvLinesUpdatedEventArgs(firstRow, lastRow));

        if (_nextLine >= mode.TransmittedLines)
        {
            FinishImage(complete: true);
            return true;
        }

        return false;
    }

    private double HalfWindowSamples(double nominal, int accepted)
    {
        if (accepted < 6)
            return Math.Max(0.020 * InternalRate, 0.03 * nominal);

        var baseWindow = Math.Max(0.004 * InternalRate, 0.005 * nominal);
        return Math.Min(baseWindow * (1 + 0.5 * _missesSinceLock), 0.03 * nominal);
    }

    private int HalfWindow(double nominal, int accepted) => (int)Math.Round(HalfWindowSamples(nominal, accepted));

    /// <summary>
    /// A new full header while the current picture has lost lock means the sender
    /// started a new picture: finish this one and let the search pick up the next.
    /// </summary>
    private bool ShouldYieldToNewHeader(double predictedSync)
    {
        if (_missesSinceLock < 2)
            return false;

        // Never look back as far as this picture's own header.
        var from = Math.Max(_imageStartIndex + InternalRate, (long)predictedSync - 2 * InternalRate);
        _vis.Reset(Math.Max(_buffer.Start, from));
        if (!_vis.TryFind(_buffer, requireFullHeader: true, out _))
            return false;

        // The search rescans from before the new header once this picture is closed.
        EndAfterLastLock();
        return true;
    }

    private void RenderLine(int line, bool centred)
    {
        var mode = _mode!;
        var tracker = _tracker!;
        var sync = centred ? tracker.Fitted(line) : tracker.Predict(line);
        var period = tracker.PeriodAt(line, centred);
        var samplesPerMs = InternalRate / 1000.0 * (period / tracker.NominalSamplesPerLine);
        var lineStart = sync - mode.SyncOffsetMs * samplesPerMs;
        _renderer!.Render(_buffer, lineStart, samplesPerMs, _lineOffsetHz[line] + Options.TuningOffsetHz, line);
    }

    private int CountLocked()
    {
        var n = 0;
        for (var i = 0; i < _nextLine; i++)
        {
            if (_quality[i] == SstvLineQuality.Locked)
                n++;
        }

        return n;
    }

    private void FinishImage(bool complete)
    {
        var mode = _mode!;
        ReSync();
        var tracker = _tracker!;
        var endSync = tracker.Fitted(Math.Max(0, _nextLine - 1));
        var end = (long)(endSync - mode.SyncOffsetMs * InternalRate / 1000.0 + tracker.NominalSamplesPerLine);
        var decoded = new SstvDecodedImage
        {
            Mode = mode,
            Image = _image!.Clone(),
            LineQuality = (SstvLineQuality[])_quality.Clone(),
            FromVis = _fromVis,
            Complete = complete,
            LockPercent = LockPercent,
            OffsetHz = OffsetHz,
            ClockPpm = ClockPpm,
            StartSeconds = (double)_imageStartIndex / InternalRate,
            EndSeconds = (double)end / InternalRate,
        };

        ClearImage();
        _vis.Reset(Math.Max(_buffer.Start, end - InternalRate));
        _lastTrainCheck = _buffer.End;
        _trainFrom = end;
        _buffer.TrimBefore(Math.Min(_buffer.End, end) - InternalRate);
        ImageCompleted?.Invoke(this, decoded);
    }

    private void AbandonImage()
    {
        var restart = _imageStartIndex + InternalRate;
        ClearImage();
        _vis.Reset(Math.Max(_buffer.Start, restart));
        _lastTrainCheck = _buffer.End;
        _trainFrom = restart;
        ImageAbandoned?.Invoke(this, EventArgs.Empty);
    }

    private void ClearImage()
    {
        _searchOffsetHz = _afcHz;
        _mode = null;
        _image = null;
        _renderer = null;
        _tracker = null;
        _quality = [];
        _lineOffsetHz = [];
        _nextLine = 0;
        _missesSinceLock = 0;
        _lastLockedLine = -1;
    }
}
