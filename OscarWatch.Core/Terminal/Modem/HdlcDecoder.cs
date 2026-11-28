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

    private readonly byte[] _buffer = new byte[MaxFrameBytes + 2];
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

    public void Reset()
    {
        _bitCount = 0;
        _pattern = 0;
        _ones = 0;
        _inFrame = false;
        _bitsSinceFlag = int.MaxValue / 2;
        _flagRun = 0;
        Dcd = false;
    }

    public byte[]? PushBit(bool bit)
    {
        _pattern = (byte)((_pattern >> 1) | (bit ? 0x80 : 0));
        if (_pattern == HdlcEncoder.Flag)
            return OnFlag();

        if (_bitsSinceFlag < int.MaxValue / 2)
            _bitsSinceFlag++;

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

    private byte[]? OnFlag()
    {
        byte[]? frame = null;
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
        }

        _flagRun = _bitsSinceFlag is 6 or 7 ? _flagRun + 1 : 1;
        if (_flagRun >= 2)
            Dcd = true;
        _bitsSinceFlag = 0;
        _inFrame = true;
        _bitCount = 0;
        _ones = 0;
        return frame;
    }

    private void Abort()
    {
        _inFrame = false;
        _bitCount = 0;
        _flagRun = 0;
        Dcd = false;
    }
}
