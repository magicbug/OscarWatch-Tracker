namespace OscarWatch.Core.Terminal.Modem;

/// <summary>How hard the receiver tries to repair a frame that failed its FCS.</summary>
public enum AfskBitFixMode
{
    Off,
    SingleBit,
    SingleAndAdjacentPair
}

/// <summary>
/// Repairs frames with one flipped bit (or two adjacent bits) by trial. A repaired frame is only
/// accepted when it is a plausible UI frame, because with hundreds of trials a random block can pass
/// the 16-bit FCS by chance.
/// </summary>
public static class FrameBitFixer
{
    /// <summary>Returns the repaired frame without its FCS, or null.</summary>
    public static byte[]? TryFix(ReadOnlySpan<byte> frameWithFcs, AfskBitFixMode mode)
    {
        if (mode == AfskBitFixMode.Off || frameWithFcs.Length < HdlcDecoder.MinFrameBytes)
            return null;

        var work = frameWithFcs.ToArray();
        var bits = work.Length * 8;
        for (var i = 0; i < bits; i++)
        {
            Flip(work, i);
            if (Accept(work) is { } fixedSingle)
                return fixedSingle;

            if (mode == AfskBitFixMode.SingleAndAdjacentPair && i + 1 < bits)
            {
                Flip(work, i + 1);
                if (Accept(work) is { } fixedPair)
                    return fixedPair;
                Flip(work, i + 1);
            }

            Flip(work, i);
        }

        return null;
    }

    /// <summary>
    /// Soft-decision pre-NRZI symbol repair. <paramref name="rawDemod"/> starts with the last symbol of the
    /// opening flag (index 0, seeding NRZI) followed by the raw body symbols before the closing flag.
    /// Flips the lowest-confidence tone symbol(s) before NRZI decoding and HDLC unstuffing, so a single noisy
    /// tone (which corrupts two adjacent NRZI bits and can disturb bit stuffing) is repaired cleanly.
    /// </summary>
    public static byte[]? TryFixRawDemod(ReadOnlySpan<float> rawDemod, AfskBitFixMode mode)
    {
        if (mode == AfskBitFixMode.Off || rawDemod.Length < HdlcDecoder.MinFrameBytes * 8 + 1)
            return null;

        var bodyCount = rawDemod.Length - 1;
        var indices = new int[bodyCount];
        var confidences = new float[bodyCount];
        for (var i = 0; i < bodyCount; i++)
        {
            indices[i] = i + 1;
            confidences[i] = MathF.Abs(rawDemod[i + 1]);
        }

        // Sort body symbol positions by |demod| ascending (lowest-confidence symbols first).
        Array.Sort(confidences, indices);

        var tones = new bool[rawDemod.Length];
        for (var i = 0; i < rawDemod.Length; i++)
            tones[i] = rawDemod[i] > 0;

        var work = new byte[HdlcDecoder.MaxFrameBytes + 2];

        // Single pre-NRZI symbol flip: try up to the 64 weakest symbols.
        var singleLimit = Math.Min(bodyCount, 64);
        for (var k = 0; k < singleLimit; k++)
        {
            var idx = indices[k];
            tones[idx] = !tones[idx];
            if (TryUnstuffAndAccept(tones, work) is { } fixedSingle)
                return fixedSingle;
            tones[idx] = !tones[idx];
        }

        // Double pre-NRZI symbol flip in SingleAndAdjacentPair mode: try pairs among the 14 weakest symbols.
        if (mode == AfskBitFixMode.SingleAndAdjacentPair)
        {
            var pairLimit = Math.Min(bodyCount, 14);
            for (var a = 0; a < pairLimit; a++)
            {
                var ia = indices[a];
                tones[ia] = !tones[ia];
                for (var b = a + 1; b < pairLimit; b++)
                {
                    var ib = indices[b];
                    tones[ib] = !tones[ib];
                    if (TryUnstuffAndAccept(tones, work) is { } fixedDouble)
                        return fixedDouble;
                    tones[ib] = !tones[ib];
                }

                tones[ia] = !tones[ia];
            }
        }

        return null;
    }

    private static byte[]? TryUnstuffAndAccept(bool[] tones, byte[] work)
    {
        var ones = 0;
        var bitCount = 0;
        var maxBits = HdlcDecoder.MaxFrameBytes * 8;
        var prevTone = tones[0];
        for (var i = 1; i < tones.Length; i++)
        {
            var tone = tones[i];
            var bit = tone == prevTone;
            prevTone = tone;

            if (bit)
            {
                if (++ones >= 6)
                    return null; // Unstuffed 6+ ones inside body means invalid flag/abort.
            }
            else
            {
                if (ones == 5)
                {
                    ones = 0;
                    continue; // Remove stuffed zero.
                }

                ones = 0;
            }

            if (bitCount >= maxBits)
                return null;

            var byteIndex = bitCount >> 3;
            if ((bitCount & 7) == 0)
                work[byteIndex] = 0;
            if (bit)
                work[byteIndex] |= (byte)(1 << (bitCount & 7));
            bitCount++;
        }

        if (bitCount == 0 || (bitCount & 7) != 0)
            return null;

        var bytes = bitCount >> 3;
        if (bytes < HdlcDecoder.MinFrameBytes || bytes > HdlcDecoder.MaxFrameBytes)
            return null;

        var span = work.AsSpan(0, bytes);
        if (!Fcs16.Check(span))
            return null;
        var frame = span[..^2];
        return IsPlausibleUi(frame) ? frame.ToArray() : null;
    }

    /// <summary>
    /// A UI frame whose every address is 1 to 6 letters or digits (space padded) and whose source
    /// looks like a real callsign.
    /// </summary>
    public static bool IsPlausibleUi(ReadOnlySpan<byte> ax25)
    {
        if (!Ax25Frame.TryParse(ax25, out var frame) || frame is null || !frame.IsUi)
            return false;

        var addressBytes = (2 + frame.Digipeaters.Count) * Ax25Address.EncodedLength;
        for (var a = 0; a < addressBytes; a += Ax25Address.EncodedLength)
        {
            var seenSpace = false;
            for (var i = 0; i < 6; i++)
            {
                var c = (char)(ax25[a + i] >> 1);
                if ((ax25[a + i] & 1) != 0)
                    return false;
                if (c == ' ')
                {
                    seenSpace = true;
                    continue;
                }

                if (seenSpace || c is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')))
                    return false;
            }
        }

        return TerminalText.LooksLikeCallsign(frame.Source.Callsign);
    }

    private static byte[]? Accept(byte[] work)
    {
        if (!Fcs16.Check(work))
            return null;
        var frame = work.AsSpan(0, work.Length - 2);
        return IsPlausibleUi(frame) ? frame.ToArray() : null;
    }

    private static void Flip(byte[] data, int bit) => data[bit >> 3] ^= (byte)(1 << (bit & 7));
}
