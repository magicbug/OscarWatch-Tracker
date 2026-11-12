using System.Text;

namespace OscarWatch.Core.CwDecode;

/// <summary>
/// Decoded text, and whether a word gap comes before it. <paramref name="Confidence"/> holds the
/// model's probability for each character of <paramref name="Text"/> (1 for spaces), when known.
/// </summary>
public readonly record struct CwLaneText(string Text, bool PrependSpace, float[]? Confidence = null)
{
    public static CwLaneText Empty { get; } = new("", false);
}

/// <summary>
/// Joins the overlapping model windows of a stream into one posterior track, then reads text off
/// it with greedy CTC. Text is only committed once it is a few seconds behind the newest audio,
/// so the newest letters can still change. A port of the DeepCW reference decoder.
/// </summary>
public sealed class CwPosteriorStitcher
{
    public const double MaxSegmentSeconds = 12.0;
    private const double LeftContextSeconds = 4.0;
    private const double FreezeGuardSeconds = 2.0;
    private const double LeftRampSeconds = 2.0;
    private const double RightRampSeconds = 1.0;
    private const int DilationFrames = 1;
    private const int WordBoundaryBlankHops = 134;
    private const float MinViewWeight = 1e-3f;
    private const float UncoveredLogProb = -30f;

    private enum Symbol { None, Char, Space }

    private readonly string[] _chars;
    private readonly int _blank;
    private readonly int _space;
    private readonly int _classes;
    private readonly double _framesPerSecond;
    private readonly int _leftContextFrames;
    private readonly int _freezeGuardFrames;
    private readonly int _wordBoundaryFrames;

    private int _rowBase;
    private int _rowCount;
    private float[] _sums = [];
    private float[] _weights = [];
    private int _coveredEnd;
    private int _frozenEnd;
    private int _committedEnd;
    private Symbol _previousSymbol = Symbol.None;
    private int _blankFrames;
    private bool _hasCommittedText;

    public CwPosteriorStitcher(CwModelMetadata metadata)
    {
        _chars = [.. metadata.Chars];
        _blank = metadata.BlankIndex;
        _space = Array.IndexOf(_chars, " ");
        _classes = metadata.NumClasses;
        HopSamples = metadata.OutputHopSamples;
        _framesPerSecond = (double)metadata.SampleRate / HopSamples;
        MaxSegmentSamples = (int)(MaxSegmentSeconds * metadata.SampleRate);
        _leftContextFrames = (int)Math.Round(LeftContextSeconds * _framesPerSecond);
        _freezeGuardFrames = (int)Math.Round(FreezeGuardSeconds * _framesPerSecond);
        _wordBoundaryFrames = (int)Math.Ceiling(WordBoundaryBlankHops / (double)metadata.OutputTimeStride);
    }

    /// <summary>Audio samples per output frame.</summary>
    public int HopSamples { get; }

    public int MaxSegmentSamples { get; }

    /// <summary>First frame a new window must start at, so committed text keeps its left context.</summary>
    public int ContextStartFrame => Math.Max(0, _frozenEnd - _leftContextFrames);

    public int WindowStartFrame(long endSample) =>
        Math.Max(ContextStartFrame, (int)Math.Ceiling((endSample - MaxSegmentSamples) / (double)HopSamples));

    /// <param name="logits">Model output, row-major [frame, class].</param>
    public void AddWindow(int startFrame, float[] logits, int frameCount, bool isFinal)
    {
        if (frameCount <= 0)
        {
            if (isFinal)
                _frozenEnd = _coveredEnd;
            return;
        }

        var endFrame = startFrame + frameCount;
        EnsureRows(endFrame);
        var logProbs = DilatedLogProbs(logits, frameCount);

        for (var f = Math.Max(0, _frozenEnd - startFrame); f < frameCount; f++)
        {
            var left = startFrame == 0 ? double.PositiveInfinity : f / _framesPerSecond;
            var right = isFinal ? double.PositiveInfinity : (frameCount - 1 - f) / _framesPerSecond;
            var weight = (float)Math.Max(
                MinViewWeight,
                SmoothStep(left / LeftRampSeconds) * SmoothStep(right / RightRampSeconds));
            var row = startFrame + f - _rowBase;
            if (row < 0)
                continue;

            var dst = row * _classes;
            var src = f * _classes;
            for (var c = 0; c < _classes; c++)
                _sums[dst + c] += weight * logProbs[src + c];
            _weights[row] += weight;
        }

        _coveredEnd = Math.Max(_coveredEnd, endFrame);
        _frozenEnd = isFinal ? _coveredEnd : Math.Max(_frozenEnd, endFrame - _freezeGuardFrames);
    }

    /// <summary>
    /// Text that is now settled, and the tail that may still change.
    /// With <paramref name="final"/>, everything decoded so far is settled.
    /// </summary>
    public (CwLaneText? Committed, CwLaneText Pending) Take(bool final = false)
    {
        var startFrame = _committedEnd;
        var frameCount = _coveredEnd - startFrame;
        if (frameCount <= 0)
            return (null, CwLaneText.Empty);

        var startsWord = !_hasCommittedText || _previousSymbol == Symbol.Space;
        var scores = ReadScores(startFrame, _coveredEnd);
        var (spans, argmax) = DecodeGreedy(scores, frameCount);
        foreach (var span in spans)
        {
            span.Start += startFrame;
            span.End += startFrame;
        }

        var commitLimit = final ? int.MaxValue : _frozenEnd;
        var committedCount = 0;
        for (var i = 0; i < spans.Count; i++)
        {
            if ((long)spans[i].End + 1 >= commitLimit)
                break;
            if (!spans[i].IsSpace)
                committedCount = i + 1;
        }

        CwLaneText? committed = null;
        if (committedCount > 0)
        {
            committed = ToLaneText(spans.Take(committedCount), startsWord);
            _hasCommittedText = true;
            _committedEnd = spans[committedCount - 1].End + 1;
            _previousSymbol = Symbol.Char;
            _blankFrames = 0;
        }

        var remaining = spans.Skip(committedCount).ToList();
        var nextCharacter = remaining.FirstOrDefault(s => !s.IsSpace);
        var releaseEnd = Math.Min(
            final ? _coveredEnd : _frozenEnd,
            nextCharacter?.Start ?? int.MaxValue) - 1;

        var previousSymbol = _previousSymbol;
        var blankFrames = _blankFrames;
        for (var frame = _committedEnd; frame < releaseEnd; frame++)
        {
            if (argmax[frame - startFrame] == _space)
            {
                previousSymbol = Symbol.Space;
                blankFrames = 0;
            }
            else
            {
                blankFrames++;
            }
        }

        if (releaseEnd > _committedEnd)
        {
            _previousSymbol = previousSymbol;
            _blankFrames = blankFrames;
            _committedEnd = releaseEnd;
        }

        DropRowsBefore(_committedEnd);

        var pending = ToLaneText(
            remaining.Where(s => s.End >= _committedEnd),
            !_hasCommittedText || _previousSymbol == Symbol.Space);
        return (committed, pending);
    }

    private sealed class Span(string ch, int start, int end, float confidence)
    {
        public string Char { get; } = ch;
        public int Start { get; set; } = start;
        public int End { get; set; } = end;
        /// <summary>Peak probability of this character across its frames.</summary>
        public float Confidence { get; set; } = confidence;
        public bool IsSpace => string.IsNullOrWhiteSpace(Char);
    }

    private (List<Span> Spans, int[] Argmax) DecodeGreedy(float[] scores, int frameCount)
    {
        var argmax = new int[frameCount];
        for (var t = 0; t < frameCount; t++)
        {
            var row = t * _classes;
            var best = 0;
            var bestScore = scores[row];
            for (var c = 1; c < _classes; c++)
            {
                if (scores[row + c] > bestScore)
                {
                    bestScore = scores[row + c];
                    best = c;
                }
            }

            argmax[t] = best;
        }

        var indices = (int[])argmax.Clone();
        if (_space >= 0)
        {
            var previous = _previousSymbol switch
            {
                Symbol.Char => -2,
                Symbol.Space => _space,
                _ => -1,
            };
            var frame = 0;
            while (frame < frameCount)
            {
                if (indices[frame] != _blank)
                {
                    previous = indices[frame];
                    frame++;
                    continue;
                }

                var runStart = frame;
                while (frame < frameCount && indices[frame] == _blank)
                    frame++;
                var following = frame < frameCount ? indices[frame] : -1;
                var runLength = frame - runStart + (runStart == 0 ? _blankFrames : 0);
                if (runLength >= _wordBoundaryFrames
                    && previous != -1 && previous != _space
                    && following >= 0 && following != _space)
                {
                    for (var i = runStart; i < frame; i++)
                        indices[i] = _space;
                }
            }
        }

        var spans = new List<Span>();
        var previousIndex = -1;
        Span? active = null;
        for (var t = 0; t < frameCount; t++)
        {
            var index = indices[t];
            if (index == _blank)
            {
                previousIndex = -1;
                active = null;
                continue;
            }

            var probability = (float)Math.Exp(scores[t * _classes + index]);
            if (index == previousIndex)
            {
                if (active is not null)
                {
                    active.End = t;
                    active.Confidence = Math.Max(active.Confidence, probability);
                }

                continue;
            }

            previousIndex = index;
            if (index >= _chars.Length)
            {
                active = null;
                continue;
            }

            active = new Span(_chars[index], t, t, probability);
            spans.Add(active);
        }

        return (spans, argmax);
    }

    private static CwLaneText ToLaneText(IEnumerable<Span> spans, bool prependSpace)
    {
        var output = new StringBuilder();
        var confidence = new List<float>();
        var hasLeadingSpace = prependSpace;
        var pendingSpace = false;
        foreach (var span in spans)
        {
            if (span.IsSpace)
            {
                if (output.Length == 0)
                    hasLeadingSpace = true;
                else
                    pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                output.Append(' ');
                confidence.Add(1f);
                pendingSpace = false;
            }

            output.Append(span.Char);
            for (var i = 0; i < span.Char.Length; i++)
                confidence.Add(span.Confidence);
        }

        return new CwLaneText(output.ToString(), hasLeadingSpace, [.. confidence]);
    }

    private float[] DilatedLogProbs(float[] logits, int frameCount)
    {
        var logProbs = new float[frameCount * _classes];
        Array.Copy(logits, logProbs, logProbs.Length);
        LogSoftmaxRows(logProbs, frameCount);

        var dilated = new float[logProbs.Length];
        for (var t = 0; t < frameCount; t++)
        {
            var lo = Math.Max(0, t - DilationFrames);
            var hi = Math.Min(frameCount - 1, t + DilationFrames);
            for (var c = 0; c < _classes; c++)
            {
                if (c == _blank)
                {
                    dilated[t * _classes + c] = logProbs[t * _classes + c];
                    continue;
                }

                var max = float.NegativeInfinity;
                for (var s = lo; s <= hi; s++)
                    max = Math.Max(max, logProbs[s * _classes + c]);
                dilated[t * _classes + c] = max;
            }
        }

        LogSoftmaxRows(dilated, frameCount);
        return dilated;
    }

    private void LogSoftmaxRows(float[] values, int rows)
    {
        for (var t = 0; t < rows; t++)
        {
            var offset = t * _classes;
            var peak = float.NegativeInfinity;
            for (var c = 0; c < _classes; c++)
                peak = Math.Max(peak, values[offset + c]);

            var sum = 0.0;
            for (var c = 0; c < _classes; c++)
                sum += Math.Exp(values[offset + c] - peak);

            var logSum = (float)(peak + Math.Log(sum));
            for (var c = 0; c < _classes; c++)
                values[offset + c] -= logSum;
        }
    }

    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private void EnsureRows(int endFrame)
    {
        var needed = endFrame - _rowBase;
        if (needed <= _rowCount)
            return;

        if (needed > _weights.Length)
        {
            var capacity = Math.Max(needed, _weights.Length * 2);
            Array.Resize(ref _weights, capacity);
            Array.Resize(ref _sums, capacity * _classes);
        }

        Array.Clear(_weights, _rowCount, needed - _rowCount);
        Array.Clear(_sums, _rowCount * _classes, (needed - _rowCount) * _classes);
        _rowCount = needed;
    }

    private void DropRowsBefore(int frame)
    {
        var drop = Math.Min(frame - _rowBase, _rowCount);
        if (drop <= 0)
            return;

        var keep = _rowCount - drop;
        Array.Copy(_weights, drop, _weights, 0, keep);
        Array.Copy(_sums, drop * _classes, _sums, 0, keep * _classes);
        _rowCount = keep;
        _rowBase += drop;
    }

    private float[] ReadScores(int startFrame, int endFrame)
    {
        var frames = endFrame - startFrame;
        var scores = new float[frames * _classes];
        for (var t = 0; t < frames; t++)
        {
            var row = startFrame - _rowBase + t;
            var dst = t * _classes;
            var weight = row >= 0 && row < _rowCount ? _weights[row] : 0f;
            if (weight > 0)
            {
                var src = row * _classes;
                for (var c = 0; c < _classes; c++)
                    scores[dst + c] = _sums[src + c] / weight;
            }
            else
            {
                for (var c = 0; c < _classes; c++)
                    scores[dst + c] = UncoveredLogProb;
                scores[dst + _blank] = 0f;
            }
        }

        return scores;
    }
}
