namespace OscarWatch.Core.Terminal.Modem;

/// <summary>
/// HDLC deframer for one slicer: flag hunt, bit unstuffing, abort on seven ones, length limits.
/// Bits arrive NRZI-decoded, least significant bit of each byte first. Returns candidate frames with
/// their FCS still attached; the caller checks it.
/// </summary>
public sealed class HdlcDecoder
{
    /// <summary>Two addresses, control and FCS.</summary>
    public const int MinFrameBytes = 17;

    /// <summary>AX.25 allows 256 info bytes and 8 digipeaters; leave headroom.</summary>
    public const int MaxFrameBytes = 340;

    /// <summary>Maximum raw (pre-unstuffing) symbols in a frame including worst-case 1-in-5 stuffing and closing flag.</summary>
    internal const int MaxRawSymbols = MaxFrameBytes * 10 + 16;

    private readonly byte[] _buffer = new byte[MaxFrameBytes + 2];
    private readonly float[] _rawDemod = new float[MaxRawSymbols];
    private int _rawCount;
    private float _prevFlagDemod;
    private float _openingToneDemod;
    private int _bitCount;
    private byte _pattern;
    private int _ones;
    private bool _inFrame;
    private int _bitsSinceFlag = int.MaxValue / 2;
    private int _flagRun;

    /// <summary>
    /// Data carrier detect: true after two flags in a row (a TXDELAY preamble or frames back to back)
    /// until an abort or an over-long frame. Single flags in open-squelch noise do not set it.
    /// </summary>
    public bool Dcd { get; private set; }

    /// <summary>
    /// Raw pre-NRZI demodulator samples for the last candidate emitted by <see cref="PushBit(bool, float, out float[]?)"/>,
    /// starting with the final symbol of the opening flag (needed to seed NRZI) through the last body symbol.
    /// </summary>
    public void Reset()
    {
        _bitCount = 0;
        _rawCount = 0;
        _prevFlagDemod = 0;
        _openingToneDemod = 0;
        _pattern = 0;
        _ones = 0;
        _inFrame = false;
        _bitsSinceFlag = int.MaxValue / 2;
        _flagRun = 0;
        Dcd = false;
    }

    public byte[]? PushBit(bool bit) => PushBit(bit, bit ? 1f : -1f, out _);

    /// <summary>
    /// Pushes one NRZI-decoded bit along with its raw signed demodulator sample (<paramref name="demod"/>).
    /// When a closing flag completes a candidate frame, returns the unstuffed bytes (if byte-aligned and within
    /// length limits) and sets <paramref name="rawDemod"/> when the raw symbol stream is plausible for soft-decision repair.
    /// </summary>
    public byte[]? PushBit(bool bit, float demod, out float[]? rawDemod)
    {
        rawDemod = null;
        _pattern = (byte)((_pattern >> 1) | (bit ? 0x80 : 0));
        if (_pattern == HdlcEncoder.Flag)
            return OnFlag(demod, out rawDemod);

        if (_bitsSinceFlag < int.MaxValue / 2)
            _bitsSinceFlag++;

        if (_inFrame && _rawCount < MaxRawSymbols)
            _rawDemod[_rawCount++] = demod;

        if (bit)
        {
            if (++_ones >= 7)
            {
                Abort();
                return null;
            }
        }
        else
        {
            if (_ones == 5)
            {
                _ones = 0;
                return null;
            }

            _ones = 0;
        }

        if (!_inFrame)
            return null;

        if (_bitCount >= MaxFrameBytes * 8 + 8)
        {
            Abort();
            return null;
        }

        var index = _bitCount >> 3;
        if ((_bitCount & 7) == 0)
            _buffer[index] = 0;
        if (bit)
            _buffer[index] |= (byte)(1 << (_bitCount & 7));
        _bitCount++;
        return null;
    }

    private byte[]? OnFlag(float closingFlagDemod, out float[]? rawDemod)
    {
        byte[]? frame = null;
        rawDemod = null;
        if (_inFrame)
        {
            // The first seven bits of this flag were stored before the pattern completed.
            var bits = _bitCount - 7;
            if (bits > 0 && bits % 8 == 0)
            {
                var bytes = bits / 8;
                if (bytes is >= MinFrameBytes and <= MaxFrameBytes)
                    frame = _buffer.AsSpan(0, bytes).ToArray();
            }

            // Capture raw pre-NRZI symbols (seed symbol from opening flag + body symbols before the 7 flag bits)
            // so soft-decision repair can recover single-symbol NRZI errors even when bit stuffing was disturbed.
            var bodyRaw = _rawCount - 7;
            if (bodyRaw >= MinFrameBytes * 8 && bodyRaw <= MaxRawSymbols - 8 && (frame is not null || Dcd))
            {
                rawDemod = new float[bodyRaw + 1];
                rawDemod[0] = _openingToneDemod;
                Array.Copy(_rawDemod, 0, rawDemod, 1, bodyRaw);
            }
        }

        _flagRun = _bitsSinceFlag is 6 or 7 ? _flagRun + 1 : 1;
        if (_flagRun >= 2)
            Dcd = true;
        _bitsSinceFlag = 0;
        _inFrame = true;
        _bitCount = 0;
        _rawCount = 0;
        _openingToneDemod = closingFlagDemod;
        _prevFlagDemod = closingFlagDemod;
        _ones = 0;
        return frame;
    }

    private void Abort()
    {
        _inFrame = false;
        _bitCount = 0;
        _rawCount = 0;
        _flagRun = 0;
        Dcd = false;
    }
}
