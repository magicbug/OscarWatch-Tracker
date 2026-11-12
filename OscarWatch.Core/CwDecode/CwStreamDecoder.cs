using System.Text;

namespace OscarWatch.Core.CwDecode;

/// <summary>Runs the CW model on one spectrogram.</summary>
public interface ICwModelRunner
{
    /// <param name="spectrogram">Row-major [frame, bin].</param>
    /// <returns>Logits, row-major [output frame, class].</returns>
    float[] Run(float[] spectrogram, int frames, int bins, out int outputFrames);
}

/// <summary>Text settled by one decode step, and the tail that may still change.</summary>
public readonly record struct CwDecodeUpdate(string CommittedText, CwLaneText Pending);

/// <summary>
/// Streaming DeepCW decode: buffer audio at the model rate, run a window of up to 12 s once a
/// second, and stitch the overlapping windows into text.
/// </summary>
public sealed class CwStreamDecoder
{
    public const double StepSeconds = 1.0;
    public const double MinPendingSeconds = 2.0;

    private readonly CwModelMetadata _metadata;
    private readonly ICwModelRunner _runner;
    private readonly int _stepSamples;
    private CwPosteriorStitcher _stitcher;
    private readonly StringBuilder _transcript = new();
    private float[] _buffer = new float[16384];
    private int _bufferCount;
    private long _bufferStartSample;
    private long _nextStepEnd;

    public CwStreamDecoder(CwModelMetadata metadata, ICwModelRunner runner)
    {
        _metadata = metadata;
        _runner = runner;
        _stepSamples = (int)(StepSeconds * metadata.SampleRate);
        _stitcher = new CwPosteriorStitcher(metadata);
        Reset();
    }

    public int SampleRate => _metadata.SampleRate;

    /// <summary>Every sample appended since the last reset.</summary>
    public long TotalSamples => _bufferStartSample + _bufferCount;

    public string Transcript => _transcript.ToString();

    public void Reset()
    {
        _stitcher = new CwPosteriorStitcher(_metadata);
        _transcript.Clear();
        _bufferCount = 0;
        _bufferStartSample = 0;
        _nextStepEnd = (long)(MinPendingSeconds * _metadata.SampleRate);
    }

    /// <summary>Audio at <see cref="SampleRate"/>.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        if (_bufferCount + samples.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _bufferCount + samples.Length));
        samples.CopyTo(_buffer.AsSpan(_bufferCount));
        _bufferCount += samples.Length;
    }

    public bool IsStepDue => TotalSamples >= _nextStepEnd;

    /// <summary>
    /// Decode up to the newest audio when a step is due. A caller that fell behind decodes once,
    /// up to the newest sample, rather than once for every second it missed.
    /// </summary>
    public CwDecodeUpdate? Step()
    {
        if (!IsStepDue)
            return null;

        var end = TotalSamples;
        _nextStepEnd = end + _stepSamples;
        var (committed, pending) = RunWindow(_stitcher.WindowStartFrame(end), end, isFinal: false);
        return new CwDecodeUpdate(committed, pending);
    }

    /// <summary>Decode the rest of the buffered audio and settle every letter.</summary>
    public string Finish()
    {
        var output = new StringBuilder();
        var end = TotalSamples;
        while (true)
        {
            var startFrame = _stitcher.ContextStartFrame;
            var segmentEnd = Math.Min(end, (long)startFrame * _stitcher.HopSamples + _stitcher.MaxSegmentSamples);
            var isFinal = segmentEnd >= end;
            output.Append(RunWindow(startFrame, segmentEnd, isFinal).Committed);
            if (isFinal)
                return output.ToString();
        }
    }

    private (string Committed, CwLaneText Pending) RunWindow(int startFrame, long endSample, bool isFinal)
    {
        var startSample = (long)startFrame * _stitcher.HopSamples;
        var offset = (int)Math.Max(0, startSample - _bufferStartSample);
        var length = (int)Math.Max(0, endSample - _bufferStartSample) - offset;

        if (length >= _metadata.FftLength)
        {
            var spectrogram = CwSpectrogram.Compute(_buffer.AsSpan(offset, length), _metadata, out var frames);
            var logits = _runner.Run(spectrogram, frames, _metadata.SpectrogramFrequencyBins, out var outputFrames);
            _stitcher.AddWindow(startFrame, logits, outputFrames, isFinal);
        }
        else if (isFinal)
        {
            _stitcher.AddWindow(startFrame, [], 0, isFinal: true);
        }

        var (committed, pending) = _stitcher.Take(isFinal);
        var text = "";
        if (committed is { Text.Length: > 0 } c)
        {
            text = (c.PrependSpace && _transcript.Length > 0 ? " " : "") + c.Text;
            _transcript.Append(text);
        }

        TrimBuffer();
        return (text, pending);
    }

    private void TrimBuffer()
    {
        var keepFrom = (long)_stitcher.ContextStartFrame * _stitcher.HopSamples;
        var drop = (int)Math.Min(_bufferCount, keepFrom - _bufferStartSample);
        if (drop <= 0)
            return;

        Array.Copy(_buffer, drop, _buffer, 0, _bufferCount - drop);
        _bufferCount -= drop;
        _bufferStartSample += drop;
    }
}
