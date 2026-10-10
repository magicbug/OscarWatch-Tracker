using OscarWatch.Core.Radio;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

internal sealed class RecordingYaesuCatTransport : IYaesuCatTransport
{
    public List<byte[]> SentFrames { get; } = [];
    public bool IsOpen { get; private set; }

    /// <summary>Returned once by the next <see cref="QueryFrame"/> call, ahead of the default identity reply.</summary>
    public byte[]? NextQueryResponse { get; set; }

    private bool _answeredIdentity;

    public void Open() => IsOpen = true;

    public bool SendFrame(ReadOnlySpan<byte> frame, int postDelayMs = 50)
    {
        SentFrames.Add(frame.ToArray());
        return true;
    }

    public byte[]? QueryFrame(ReadOnlySpan<byte> pollFrame, int postDelayMs = 50)
    {
        SentFrames.Add(pollFrame.ToArray());
        if (NextQueryResponse is not null)
        {
            var scripted = NextQueryResponse;
            NextQueryResponse = null;
            return scripted;
        }

        // The controller's first poll is the identity check. Later reads stay empty, as before.
        if (_answeredIdentity)
            return null;

        _answeredIdentity = true;
        var frame = new byte[5];
        YaesuFt817CatCodec.EncodeFrequency10Hz(145_900_000, frame);
        frame[4] = 0x08;
        return frame;
    }

    public void Dispose() => IsOpen = false;
}
