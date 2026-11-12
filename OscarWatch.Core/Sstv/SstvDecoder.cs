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

    /// <summary>Smooth colour-difference channels in the Robot and PD modes to reduce colour speckle.</summary>
    public bool ChromaSmoothing { get; set; } = true;

    /// <summary>
    /// Weigh every sample by the subcarrier power, so the envelope dips where FM clicks and
    /// fades happen count for little in sync detection, the VIS and pixel averages.
    /// Off by default: on recorded passes it lets noise start many short sync-only pictures.
    /// </summary>
    public bool ImpulseSuppression { get; set; }

    /// <summary>
    /// Once the mode is known, demodulate the picture again through a filter only as wide as
    /// the mode needs, which lets less noise through for the slower modes.
    /// </summary>
    public bool AdaptiveBandwidth { get; set; } = true;

    /// <summary>
    /// Score line syncs on how much of the subcarrier power is at the sync tone as well as on
    /// the demodulated frequency. Tone energy holds up better in FM clicks.
    /// </summary>
    public bool ToneSync { get; set; } = true;

    /// <summary>
    /// Read a VIS header from the power of each bit's tone when noise has buried its leader.
    /// Every bit must be clearly one tone and the mode one the station is known to send.
    /// </summary>
    public bool ToneVis { get; set; } = true;

    /// <summary>
    /// When fades take out too many syncs for a regular run of pulses, add up the syncs at each
    /// mode's line period over several lines, so the picture is still found from the rest.
    /// </summary>
    public bool StackedSyncSearch { get; set; } = true;

    /// <summary>
    /// Weigh each sample of a pixel by the subcarrier power, so the envelope dips where FM
    /// clicks happen count for little. Only the picture is weighted, not the search.
    /// </summary>
    public bool WeightedPixels { get; set; } = true;

    /// <summary>
    /// Fade pixels towards a dim grey where the subcarrier envelope says the signal is mostly noise, so
    /// noise reads as grey grain rather than full-strength random colour. Pictures down to about 4 dB
    /// carrier to noise keep their colour.
    /// </summary>
    public bool NoiseFade { get; set; } = true;

    /// <summary>
    /// Once the line timing is well established, keep a picture going through a longer loss
    /// of signal (a deep fade or the antenna pointing away), and give a picture announced by
    /// a VIS header longer to find its first syncs.
    /// </summary>
    public bool HoldThroughFades { get; set; } = true;

    /// <summary>
    /// Follow the Doppler time compression given in <see cref="SstvDecoder.TimeCompressionPpm"/>
    /// when predicting where the next lines start, so the line timing stays right through fades.
    /// </summary>
    public bool DopplerLinePrior { get; set; } = true;

    /// <summary>
    /// Modes the satellite is known to send, or null for no preference. Pictures found without
    /// a VIS are only looked for in these modes, a VIS for another mode must be complete, and
    /// a VIS with one doubtful bit is read as the expected mode it is one bit away from.
    /// </summary>
    public IReadOnlyCollection<SstvModeId>? ExpectedModes { get; set; }

    /// <summary>
    /// A picture found from its syncs (no VIS) whose syncs stop sharply while the signal is
    /// still good was caught part way through at AOS: move it down so it ends on the last row.
    /// </summary>
    public bool AlignAosFragments { get; set; }

    /// <summary>
    /// When the picture is finished, redraw short runs of noisy lines (up to
    /// <see cref="SstvDecoder.MaxRepairedRun"/>) by blending the good lines either side.
    /// </summary>
    public bool RepairBadLines { get; set; }

    /// <summary>
    /// When the picture is finished, take each pixel from its neighbours on the rows above and
    /// below as well as along the row (a 3 × 3 median). Less speckle on weak pictures, less
    /// fine detail on all.
    /// </summary>
    public bool SmoothAcrossLines { get; set; }

    /// <summary>
    /// When the picture is finished, smooth only the rows that are noisy: each row moves towards its 3 × 3 median
    /// by how noisy its neighbourhood is, measured from the picture itself. Strong pictures are left alone and
    /// weak ones get the speckle taken out, so unlike <see cref="SmoothAcrossLines"/> it can stay on. Not used
    /// when <see cref="SmoothAcrossLines"/> is on, which smooths every row.
    /// </summary>
    public bool AdaptiveSmoothing { get; set; } = true;
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
    /// <summary>Why each transmitted line was not locked (or was locked off the line grid).</summary>
    public IReadOnlyList<SstvLineFailure> LineFailures { get; init; } = [];
    /// <summary>Best sync score found for each transmitted line (NaN when none was looked for).</summary>
    public IReadOnlyList<float> LineSyncScores { get; init; } = [];
    /// <summary>
    /// Noise of each transmitted line relative to the picture's typical line (1 is typical,
    /// higher is noisier; NaN where nothing was decoded).
    /// </summary>
    public IReadOnlyList<float> LineNoise { get; init; } = [];
    /// <summary>Lines redrawn from their neighbours by <see cref="SstvDecoderOptions.RepairBadLines"/>.</summary>
    public IReadOnlyList<int> RepairedLines { get; init; } = [];
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
    /// <summary>Locked lines after which the line grid counts as established.</summary>
    private const int EstablishedGridLines = 16;
    /// <summary>With <see cref="SstvDecoderOptions.HoldThroughFades"/>, a VIS picture gets this many lines to find its first syncs.</summary>
    private const int VisFalseStartCheckLine = 32;
    private const int VisFalseStartMinLocked = 2;
    private const int TrailingMissesToDrop = 4;
    /// <summary>Only clean syncs steer the AFC, so a noisy line cannot pull the colours.</summary>
    private const double AfcMinScore = 0.6;
    /// <summary>A best sync score at or below this means nothing pulse-shaped was there at all.</summary>
    private const double NoPulseScore = 0.15;
    /// <summary>Missed lines after which a sync off the established grid needs the next line to confirm it.</summary>
    private const int ReacquireMisses = 3;
    /// <summary>How far a sync may sit from the grid (or from one period after a held sync) and still agree.</summary>
    private const double ReacquireToleranceMs = 2;
    /// <summary>A sync this clean is trusted off the grid without waiting for the next line.</summary>
    private const double ReacquireStrongScore = 0.8;
    /// <summary>
    /// With <see cref="SstvDecoderOptions.HoldThroughFades"/> and an established grid, the longest
    /// fade held before the picture is closed early. Lines after the last sync are dropped then.
    /// </summary>
    private const double MaxHeldFadeSeconds = 45;
    /// <summary>Bandwidth each side of 1900 Hz of the demodulator used for the VIS, sync and search.</summary>
    private const double SearchBandwidthHz = 1200;

    private readonly SstvResampler _resampler;
    private readonly SstvFmDemodulator _demod = new(InternalRate, SearchBandwidthHz);
    private readonly SstvVisDetector _vis = new(InternalRate);
    private readonly SstvSyncTrainDetector _train = new(InternalRate);
    private readonly SstvSignalBuffer _buffer = new();
    private readonly List<float> _resampled = new(4096);
    private readonly List<float> _demodulated = new(4096);
    private readonly List<float> _envelope = new(4096);
    private readonly List<float> _weights = new(4096);
    private readonly SstvEnvelopeWeigher _weigher = new(InternalRate);
    /// <summary>Resampled audio, kept alongside <see cref="_buffer"/> (same indices) for demodulating again. Read by index only.</summary>
    private readonly SstvRawAudioBuffer _raw = new();
    /// <summary>Search demodulator I/Q for the tone power tests, same indices as <see cref="_buffer"/>.</summary>
    private readonly SstvBasebandBuffer _baseband = new();
    private readonly List<float> _pictureValues = new(4096);
    private readonly List<float> _pictureEnvelope = new(4096);
    private readonly List<float> _pictureWeights = new(4096);
    private long _lastTrainCheck;
    private long _trainFrom;
    private double _searchOffsetHz;

    // Current picture.
    private SstvMode? _mode;
    private SstvImage? _image;
    private SstvLineRenderer? _renderer;
    private SstvLineTracker? _tracker;
    /// <summary>Picture demodulated with a filter matched to the mode, or null to render from <see cref="_buffer"/>.</summary>
    private SstvSignalBuffer? _picture;
    private SstvFmDemodulator? _pictureDemod;
    private SstvEnvelopeWeigher? _pictureWeigher;
    private SstvLineQuality[] _quality = [];
    private SstvLineFailure[] _failure = [];
    private float[] _syncScore = [];
    private double[] _syncPosition = [];
    private double[] _lineOffsetHz = [];
    private int _nextLine;
    private int _missesSinceLock;
    private int _lastLockedLine = -1;
    /// <summary>Line whose sync is held until the next line confirms it, or −1.</summary>
    private int _pendingLine = -1;
    private double _pendingPosition;
    private double _pendingScore;
    private double _afcHz;
    // A step in the line timing found after a run of missed lines, until the next line agrees with it. The
    // state taken before it is kept so that a step that does not hold up can be taken back.
    private int _stepStartLine = -1;
    private int _stepCheckLine = -1;
    private int _stepLockedLine = -1;
    private int _stepPriorLastLocked = -1;
    private int _stepPriorMisses;
    private bool _fromVis;
    private bool _visFromTonePower;
    private long _imageStartIndex;
    private double _feedForwardHz;
    private double _timeCompressionPpm;

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

    /// <summary>
    /// Doppler time compression of the received audio in parts per million, from the orbit
    /// (range rate over the speed of light; positive while the satellite moves away).
    /// Used with <see cref="SstvDecoderOptions.DopplerLinePrior"/>.
    /// </summary>
    public double TimeCompressionPpm
    {
        get => Volatile.Read(ref _timeCompressionPpm);
        set => Volatile.Write(ref _timeCompressionPpm, double.IsFinite(value) ? value : 0);
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
        _envelope.Clear();
        _weights.Clear();
        _demod.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_resampled), _demodulated, _envelope, _baseband);
        _weigher.Process(_envelope, _weights);
        var ff = (float)FeedForwardHz;
        if (ff != 0)
        {
            for (var i = 0; i < _demodulated.Count; i++)
                _demodulated[i] -= ff;
        }

        _buffer.Weighted = Options.ImpulseSuppression;
        _buffer.Append(_demodulated, _weights);
        _raw.Append(_resampled);
        if (_picture is not null)
            DemodulatePicture(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_resampled), ff);
        Pump();
    }

    private void DemodulatePicture(ReadOnlySpan<float> audio, float feedForwardHz)
    {
        _pictureValues.Clear();
        _pictureEnvelope.Clear();
        _pictureWeights.Clear();
        _pictureDemod!.Process(audio, _pictureValues, _pictureEnvelope);
        _pictureWeigher!.Process(_pictureEnvelope, _pictureWeights);
        if (feedForwardHz != 0)
        {
            for (var i = 0; i < _pictureValues.Count; i++)
                _pictureValues[i] -= feedForwardHz;
        }

        _picture!.Weighted = Options.ImpulseSuppression;
        _picture.Append(_pictureValues, _pictureWeights);
    }

    /// <summary>
    /// With <see cref="SstvDecoderOptions.AdaptiveBandwidth"/>, demodulate the picture again
    /// with a filter only as wide as the mode needs. The filter has as many taps as the search
    /// demodulator, so both delay the signal equally and positions agree.
    /// </summary>
    private void StartPicturePath(SstvMode mode)
    {
        var bandwidth = mode.PictureBandwidthHz;
        if (!Options.AdaptiveBandwidth || bandwidth >= SearchBandwidthHz - 1)
            return;

        _pictureDemod = new SstvFmDemodulator(InternalRate, bandwidth);
        _pictureWeigher = new SstvEnvelopeWeigher(InternalRate);
        _picture = new SstvSignalBuffer();
        var from = Math.Max(_raw.Start, _imageStartIndex - InternalRate / 2);
        _picture.ResetTo(from);
        const int chunk = 4096;
        var audio = new float[chunk];
        for (var at = from; at < _raw.End; at += chunk)
        {
            var n = (int)Math.Min(chunk, _raw.End - at);
            for (var i = 0; i < n; i++)
                audio[i] = _raw[at + i];
            DemodulatePicture(audio.AsSpan(0, n), (float)FeedForwardHz);
        }
    }

    private void TrimBefore(long absoluteIndex)
    {
        _buffer.TrimBefore(absoluteIndex);
        _raw.TrimBefore(absoluteIndex);
        _baseband.TrimBefore(absoluteIndex);
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
    private int LostSignalLines(SstvMode mode)
    {
        var seconds = HoldingGrid ? MaxHeldFadeSeconds : LostSignalSeconds;
        return LinesFor(mode, seconds);
    }

    private bool HoldingGrid => Options.HoldThroughFades && _tracker is { AcceptedCount: >= EstablishedGridLines };

    private static int LinesFor(SstvMode mode, double seconds) => Math.Max(16, (int)Math.Ceiling(seconds * 1000 / mode.LineMs));

    /// <summary>
    /// A held picture that reached its last line without the signal coming back had really
    /// ended at its last sync (the pass or the sender stopped), so the noise drawn since then
    /// is dropped, as it would have been without the hold.
    /// </summary>
    private bool EndedInHeldFade(SstvMode mode) =>
        HoldingGrid && _missesSinceLock >= LinesFor(mode, LostSignalSeconds);

    private bool IsFalseStart()
    {
        // A mode this satellite is not known to send needs clearly more locked lines.
        if (Options.ExpectedModes is { Count: > 0 } expected && !expected.Contains(_mode!.Id))
            return _nextLine == FalseStartCheckLine && CountLocked() < FalseStartCheckLine / 2;
        // A header read from tone power came through noise the syncs may not survive, so its
        // picture has to lock like one found from its syncs.
        if (Options.HoldThroughFades && _fromVis && !_visFromTonePower)
            return _nextLine == VisFalseStartCheckLine && CountLocked() < VisFalseStartMinLocked;
        return _nextLine == FalseStartCheckLine && CountLocked() < FalseStartMinLocked;
    }

    /// <summary>
    /// The sender stopped (or the pass ended) mid-picture: drop the lines decoded from
    /// noise since the last sync, and keep the rest if there is enough of it.
    /// </summary>
    private void EndAfterLastLock()
    {
        RollBackOpenStep();
        var mode = _mode!;
        var keep = _lastLockedLine + 1;
        for (var i = keep; i < _nextLine; i++)
        {
            _quality[i] = SstvLineQuality.Missing;
            _failure[i] = SstvLineFailure.None;
        }
        _image!.ClearFromRow(keep * mode.RowsPerLine);
        _nextLine = keep;
        if (keep >= MinKeptLines(mode))
            FinishImage(complete: false, alignToBottom: Options.AlignAosFragments && EndedSharply());
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
        var expected = Options.ExpectedModes is { Count: > 0 } e ? e : null;
        _vis.ExpectedModes = expected;
        _vis.Baseband = Options.ToneVis ? _baseband : null;
        _vis.SearchOffsetHz = _searchOffsetHz;
        _vis.BasebandShiftHz = FeedForwardHz;
        if (_vis.TryFind(_buffer, requireFullHeader: false, out var vis))
        {
            var mode = Forced ?? vis.Mode!;
            _searchOffsetHz = vis.OffsetHz;
            var line0Start = vis.ImageStartIndex + mode.LeadInMs * InternalRate / 1000.0;
            StartImage(mode, line0Start + mode.SyncOffsetMs * InternalRate / 1000.0, vis.OffsetHz, fromVis: true);
            _visFromTonePower = vis.FromTonePower;
            return true;
        }

        if (Options.DetectWithoutVis
            && _buffer.End - _lastTrainCheck >= SyncTrainIntervalSeconds * InternalRate)
        {
            _lastTrainCheck = _buffer.End;
            IReadOnlyList<SstvMode> candidates = Forced is { } f ? [f]
                : expected is not null ? SstvModeTable.SyncTrainCandidates.Where(m => expected.Contains(m.Id)).ToArray()
                : SstvModeTable.SyncTrainCandidates;
            var trainFrom = Math.Max(_buffer.Start, _trainFrom);
            if (_train.TryFind(_buffer, trainFrom, _searchOffsetHz, candidates, out var train)
                || (Options.StackedSyncSearch
                    && _train.TryFindStacked(_buffer, trainFrom, _searchOffsetHz, candidates, out train)))
            {
                StartImage(train.Mode, train.FirstSyncIndex, train.OffsetHz, fromVis: false);
                return true;
            }
        }

        var keep = (long)(SearchKeepSeconds * InternalRate);
        if (_buffer.End - _buffer.Start > keep * 2)
            TrimBefore(_buffer.End - keep);
        return false;
    }

    private void StartImage(SstvMode mode, double line0Sync, double offsetHz, bool fromVis)
    {
        _mode = mode;
        _image = new SstvImage(mode.Width, mode.Height);
        _renderer = new SstvLineRenderer(mode, _image, Options.ChromaSmoothing, Options.WeightedPixels, Options.NoiseFade);
        var nominal = mode.LineMs * InternalRate / 1000.0;
        _tracker = new SstvLineTracker(nominal)
        {
            AutoSlant = Options.AutoSlant,
            ManualPeriod = nominal * (1 + Options.SlantTrimPpm * 1e-6),
        };
        _tracker.SetAnchor(line0Sync);
        _quality = new SstvLineQuality[mode.TransmittedLines];
        _failure = new SstvLineFailure[mode.TransmittedLines];
        _syncScore = new float[mode.TransmittedLines];
        Array.Fill(_syncScore, float.NaN);
        _syncPosition = new double[mode.TransmittedLines];
        Array.Fill(_syncPosition, double.NaN);
        _lineOffsetHz = new double[mode.TransmittedLines];
        _nextLine = 0;
        _missesSinceLock = 0;
        _lastLockedLine = -1;
        _pendingLine = -1;
        _stepCheckLine = -1;
        _stepStartLine = -1;
        _afcHz = Options.AutoTune ? Math.Clamp(offsetHz, -MaxOffsetHz, MaxOffsetHz) : 0;
        _fromVis = fromVis;
        _visFromTonePower = false;
        _imageStartIndex = (long)(line0Sync - mode.SyncOffsetMs * InternalRate / 1000.0 - mode.LeadInMs * InternalRate / 1000.0);
        TrimBefore(_imageStartIndex - InternalRate);
        StartPicturePath(mode);
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
            if (Options.DopplerLinePrior)
                tracker.SetDopplerPpm(line, TimeCompressionPpm);
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

            var hit = SstvSyncDetector.Find(
                _buffer, predicted, half, syncLen, _afcHz + Options.TuningOffsetHz, Options.ToneSync ? _baseband : null, FeedForwardHz);
            if (_stepCheckLine == line)
            {
                var agrees = hit is { } next
                    && next.Score >= Options.SyncThreshold
                    && Math.Abs(next.Position - predicted) <= ReacquireToleranceMs * rate / 1000.0;
                _stepCheckLine = -1;
                if (!agrees)
                {
                    // The sync that started the step was not a real step: go back to the old timing and look again.
                    UndoStep();
                    continue;
                }
            }

            if (hit is { } found)
                _syncScore[line] = (float)found.Score;
            if (hit is { } h && h.Score >= Options.SyncThreshold && !HoldForConfirmation(line, h, predicted))
            {
                AcceptSync(line, h.Position, h.Score);
                if (Options.AutoTune && h.Score >= AfcMinScore && !double.IsNaN(h.MeasuredHz))
                {
                    var estimate = h.MeasuredHz - Options.TuningOffsetHz - SstvSyncDetector.SyncHz;
                    _afcHz = Math.Clamp(_afcHz + 0.2 * (estimate - _afcHz), -MaxOffsetHz, MaxOffsetHz);
                }
            }
            else
            {
                if (_pendingLine != line)
                    _pendingLine = -1;
                _quality[line] = SstvLineQuality.Predicted;
                _failure[line] = hit is { Score: > NoPulseScore } ? SstvLineFailure.WeakSync : SstvLineFailure.NoPulse;
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

            if (IsFalseStart())
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
            if (EndedInHeldFade(mode))
                EndAfterLastLock();
            else
                FinishImage(complete: true);
            return true;
        }

        return false;
    }

    private void AcceptSync(int line, double position, double score)
    {
        BeginStepIfLost(line, position);
        _tracker!.Accept(line, position, score);
        _syncPosition[line] = position;
        _failure[line] = SstvLineFailure.None;
        _quality[line] = SstvLineQuality.Locked;
        _missesSinceLock = 0;
        _lastLockedLine = Math.Max(_lastLockedLine, line);
    }

    /// <summary>
    /// A well established grid that was lost for several lines and now has a sync well off where it
    /// predicted: the line timing most likely stepped, which a dropout in the recording or a pause in the
    /// sender's audio does. The grid is restarted from the first missed line, so the old timing does not
    /// keep pulling the predictions back to where the lines used to be. It is a trial: if the next line does
    /// not land one period later on the new grid, the old grid is restored.
    /// </summary>
    private void BeginStepIfLost(int line, double position)
    {
        var tracker = _tracker!;
        if (_stepCheckLine >= 0
            || tracker.AcceptedCount < EstablishedGridLines
            || _missesSinceLock < ReacquireMisses
            || Math.Abs(position - tracker.Predict(line)) <= ReacquireToleranceMs * InternalRate / 1000.0)
            return;

        _stepStartLine = Math.Clamp(_lastLockedLine + 1, 0, line);
        _stepLockedLine = line;
        _stepPriorLastLocked = _lastLockedLine;
        _stepPriorMisses = _missesSinceLock;
        _stepCheckLine = line + 1;
        tracker.StartSegment(_stepStartLine);
    }

    /// <summary>
    /// A trial step still waiting for its next line when the picture ends can no longer be confirmed, and the final
    /// redraw would put the lines since the step on its grid. Take it back, as a lone pulse is taken back.
    /// </summary>
    private void RollBackOpenStep()
    {
        if (_stepCheckLine < 0 || _stepStartLine < 0)
            return;
        _stepCheckLine = -1;
        UndoStep();
    }

    private void UndoStep()
    {
        _tracker!.UndoSegment(_stepStartLine);
        _syncPosition[_stepLockedLine] = double.NaN;
        _quality[_stepLockedLine] = SstvLineQuality.Predicted;
        _failure[_stepLockedLine] = SstvLineFailure.OffGrid;
        _lastLockedLine = _stepPriorLastLocked;
        _missesSinceLock = _stepPriorMisses + 1;
        _stepStartLine = -1;
    }

    /// <summary>
    /// After a fade, noise often throws up one sync-like pulse away from the established line
    /// grid. Such a pulse is only used once the next line's sync lands one line period after it.
    /// </summary>
    /// <returns>True when the sync is held back (the line counts as a miss for now).</returns>
    private bool HoldForConfirmation(int line, SstvSyncHit hit, double predicted)
    {
        var tracker = _tracker!;
        var tolerance = ReacquireToleranceMs * InternalRate / 1000.0;
        if (_pendingLine >= 0 && _pendingLine == line - 1)
        {
            var pending = _pendingLine;
            _pendingLine = -1;
            if (Math.Abs(hit.Position - _pendingPosition - tracker.PeriodAt(line, centred: false)) <= tolerance)
            {
                AcceptSync(pending, _pendingPosition, _pendingScore);
                _stepCheckLine = -1; // this line has just agreed with it
                return false;
            }
        }

        if (tracker.AcceptedCount < EstablishedGridLines
            || _missesSinceLock < ReacquireMisses
            || hit.Score >= ReacquireStrongScore
            || Math.Abs(hit.Position - predicted) <= tolerance)
            return false;

        _pendingLine = line;
        _pendingPosition = hit.Position;
        _pendingScore = hit.Score;
        return true;
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
        _renderer!.Render(_picture ?? _buffer, lineStart, samplesPerMs, _lineOffsetHz[line] + Options.TuningOffsetHz, line);
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

    /// <summary>
    /// A sync-started picture whose last lines were nearly all locked: the sender stopped at
    /// the end of the picture rather than the signal fading away at LOS.
    /// </summary>
    private bool EndedSharply()
    {
        const int window = 16;
        if (_fromVis || _nextLine < window)
            return false;
        var locked = 0;
        for (var i = _nextLine - window; i < _nextLine; i++)
        {
            if (_quality[i] == SstvLineQuality.Locked)
                locked++;
        }

        return locked >= window * 3 / 4;
    }

    private void FinishImage(bool complete, bool alignToBottom = false)
    {
        RollBackOpenStep();
        var mode = _mode!;
        ReSync();
        var tracker = _tracker!;
        MarkOffGridSyncs();
        var endSync = tracker.Fitted(Math.Max(0, _nextLine - 1));
        var end = (long)(endSync - mode.SyncOffsetMs * InternalRate / 1000.0 + tracker.NominalSamplesPerLine);
        var clockPpm = ClockPpm;
        var noise = MeasureLineNoise();
        var lockPercent = LockPercent;
        var repaired = Options.RepairBadLines ? RepairBadLines(noise) : [];
        if (Options.SmoothAcrossLines)
            SstvPictureSmoother.Smooth(_image!, 0, _nextLine * mode.RowsPerLine);
        else if (Options.AdaptiveSmoothing)
            SstvPictureSmoother.SmoothAdaptive(_image!, 0, _nextLine * mode.RowsPerLine);
        if (alignToBottom)
        {
            var shift = mode.TransmittedLines - _nextLine;
            ShiftToBottom();
            if (shift > 0)
            {
                Array.Copy(noise, 0, noise, shift, _nextLine);
                Array.Fill(noise, float.NaN, 0, shift);
                for (var i = 0; i < repaired.Count; i++)
                    repaired[i] += shift;
            }
        }
        var decoded = new SstvDecodedImage
        {
            Mode = mode,
            Image = _image!.Clone(),
            LineQuality = (SstvLineQuality[])_quality.Clone(),
            FromVis = _fromVis,
            Complete = complete,
            LockPercent = lockPercent,
            OffsetHz = OffsetHz,
            ClockPpm = clockPpm,
            StartSeconds = (double)_imageStartIndex / InternalRate,
            EndSeconds = (double)end / InternalRate,
            LineFailures = (SstvLineFailure[])_failure.Clone(),
            LineSyncScores = (float[])_syncScore.Clone(),
            LineNoise = noise,
            RepairedLines = repaired,
        };

        ClearImage();
        _vis.Reset(Math.Max(_buffer.Start, end - InternalRate));
        _lastTrainCheck = _buffer.End;
        _trainFrom = end;
        TrimBefore(Math.Min(_buffer.End, end) - InternalRate);
        ImageCompleted?.Invoke(this, decoded);
    }

    /// <summary>Longest run of noisy lines <see cref="SstvDecoderOptions.RepairBadLines"/> redraws.</summary>
    public const int MaxRepairedRun = 3;
    /// <summary>A line this many times noisier than the picture's typical line counts as bad.</summary>
    private const float BadLineNoise = 2.5f;
    /// <summary>Lines either side of a repair must be no noisier than this.</summary>
    private const float GoodLineNoise = 1.6f;

    /// <summary>
    /// Noise per line: the mean step between neighbouring frequency samples over the picture
    /// part of the line, divided by the median over the picture. FM noise makes the
    /// discriminator jump about; picture content changes far more smoothly.
    /// </summary>
    private float[] MeasureLineNoise()
    {
        var mode = _mode!;
        var tracker = _tracker!;
        var source = _picture ?? _buffer;
        var noise = new float[mode.TransmittedLines];
        Array.Fill(noise, float.NaN);
        var rate = InternalRate / 1000.0;
        var raw = new List<float>(_nextLine);
        for (var line = 0; line < _nextLine; line++)
        {
            var from = (long)(tracker.Fitted(line) + (mode.SyncMs - mode.SyncOffsetMs) * rate) + 1;
            var to = (long)(tracker.Fitted(line) - mode.SyncOffsetMs * rate + tracker.NominalSamplesPerLine);
            from = Math.Max(from, source.Start + 1);
            to = Math.Min(to, source.End);
            if (to - from < 16)
                continue;
            double sum = 0;
            var previous = Math.Clamp(source[from - 1], 1300f, 2500f);
            for (var i = from; i < to; i++)
            {
                var value = Math.Clamp(source[i], 1300f, 2500f);
                sum += Math.Abs(value - previous);
                previous = value;
            }

            noise[line] = (float)(sum / (to - from));
            raw.Add(noise[line]);
        }

        if (raw.Count == 0)
            return noise;
        raw.Sort();
        var median = Math.Max(raw[raw.Count / 2], 1e-3f);
        for (var line = 0; line < _nextLine; line++)
            noise[line] /= median;
        return noise;
    }

    /// <summary>Redraw short runs of bad lines by blending the good rows above and below.</summary>
    private List<int> RepairBadLines(float[] noise)
    {
        var mode = _mode!;
        var image = _image!;
        var repaired = new List<int>();
        var line = 1;
        while (line < _nextLine - 1)
        {
            if (!(noise[line] > BadLineNoise))
            {
                line++;
                continue;
            }

            // Lines only partly hit (between good and bad) join the run.
            var end = line;
            while (end + 1 < _nextLine && !(noise[end + 1] <= GoodLineNoise))
                end++;
            while (line > 1 && !(noise[line - 1] <= GoodLineNoise))
                line--;
            var run = end - line + 1;
            if (run <= MaxRepairedRun && end + 1 < _nextLine
                && noise[line - 1] <= GoodLineNoise && noise[end + 1] <= GoodLineNoise)
            {
                var above = line * mode.RowsPerLine - 1;
                var below = Math.Min(mode.Height - 1, (end + 1) * mode.RowsPerLine);
                for (var row = above + 1; row < below; row++)
                {
                    var t = (double)(row - above) / (below - above);
                    for (var x = 0; x < image.Width; x++)
                    {
                        var (r0, g0, b0) = image.GetPixel(x, above);
                        var (r1, g1, b1) = image.GetPixel(x, below);
                        image.SetPixel(x, row, r0 + t * (r1 - r0), g0 + t * (g1 - g0), b0 + t * (b1 - b0));
                    }
                }

                for (var i = line; i <= end; i++)
                    repaired.Add(i);
            }

            line = end + 1;
        }

        return repaired;
    }

    /// <summary>Move the decoded lines so the last one is the picture's last line.</summary>
    private void ShiftToBottom()
    {
        var mode = _mode!;
        var shift = mode.TransmittedLines - _nextLine;
        if (shift <= 0)
            return;
        _image!.ShiftDown(shift * mode.RowsPerLine);
        Shift(_quality, SstvLineQuality.Missing);
        Shift(_failure, SstvLineFailure.None);
        Shift(_syncScore, float.NaN);
        Shift(_syncPosition, double.NaN);
        Shift(_lineOffsetHz, 0);

        void Shift<T>(T[] values, T empty)
        {
            Array.Copy(values, 0, values, shift, _nextLine);
            Array.Fill(values, empty, 0, shift);
        }
    }

    /// <summary>
    /// Locked lines whose sync sits well off the line grid fitted through the whole picture:
    /// noise that passed for a sync. Uses the same limit as the line tracker's outlier check.
    /// </summary>
    private void MarkOffGridSyncs()
    {
        var tracker = _tracker!;
        var residuals = new List<(int Line, double Residual)>();
        for (var line = 0; line < _nextLine; line++)
        {
            if (_quality[line] == SstvLineQuality.Locked && !double.IsNaN(_syncPosition[line]))
                residuals.Add((line, Math.Abs(_syncPosition[line] - tracker.Fitted(line))));
        }

        if (residuals.Count < 6)
            return;
        var sorted = residuals.Select(r => r.Residual).OrderBy(r => r).ToList();
        var limit = Math.Max(3.0, 3.0 * sorted[sorted.Count / 2]);
        foreach (var (line, residual) in residuals)
        {
            if (residual > limit)
                _failure[line] = SstvLineFailure.OffGrid;
        }
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
        _picture = null;
        _pictureDemod = null;
        _pictureWeigher = null;
        _tracker = null;
        _quality = [];
        _failure = [];
        _syncScore = [];
        _syncPosition = [];
        _lineOffsetHz = [];
        _nextLine = 0;
        _missesSinceLock = 0;
        _lastLockedLine = -1;
        _pendingLine = -1;
    }
}
