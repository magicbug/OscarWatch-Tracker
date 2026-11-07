using System.Buffers.Binary;
using System.Text;

namespace OscarWatch.Rig;

/// <summary>
/// Icom network control (RS-BA1 protocol) packet layouts used by <see cref="IcomLanTransport"/>.
/// Every datagram starts with a 16-byte header: length, type and sequence little-endian,
/// then the sender and receiver session IDs big-endian.
/// </summary>
internal static class IcomLanPackets
{
    public const byte TypeData = 0x00;
    public const byte TypeRetransmit = 0x01;
    public const byte TypeAreYouThere = 0x03;
    public const byte TypeIAmHere = 0x04;
    public const byte TypeDisconnect = 0x05;
    public const byte TypeAreYouReady = 0x06;
    public const byte TypePing = 0x07;

    public const int HeaderLength = 0x10;
    public const int PingLength = 0x15;
    public const int LoginLength = 0x80;
    public const int LoginReplyLength = 0x60;
    public const int TokenLength = 0x40;
    public const int StatusLength = 0x50;
    public const int CapabilitiesLength = 0xA8;
    public const int ConnInfoLength = 0x90;

    public const byte TokenRemove = 0x01;
    public const byte TokenCreate = 0x02;
    public const byte TokenRenew = 0x05;

    public const byte CivDataMarker = 0xC1;
    public const byte CivOpenCloseMarker = 0xC0;
    public const int CivDataOffset = 0x15;

    /// <summary>CI-V controller address used by network clients (RS-BA1, wfview).</summary>
    public const byte ControllerAddress = 0xE0;

    public const int AudioSampleRate = 48000;
    public const int TxBufferMs = 300;

    private static readonly byte[] PasscodeTable =
    [
        0x47, 0x5D, 0x4C, 0x42, 0x66, 0x20, 0x23, 0x46, 0x4E, 0x57, 0x45, 0x3D, 0x67, 0x76, 0x60, 0x41,
        0x62, 0x39, 0x59, 0x2D, 0x68, 0x7E, 0x7C, 0x65, 0x7D, 0x49, 0x29, 0x72, 0x73, 0x78, 0x21, 0x6E,
        0x5A, 0x5E, 0x4A, 0x3E, 0x71, 0x2C, 0x2A, 0x54, 0x3C, 0x3A, 0x63, 0x4F, 0x43, 0x75, 0x27, 0x79,
        0x5B, 0x35, 0x70, 0x48, 0x6B, 0x56, 0x6F, 0x34, 0x32, 0x6C, 0x30, 0x61, 0x6D, 0x7B, 0x2F, 0x4B,
        0x64, 0x38, 0x2B, 0x2E, 0x50, 0x40, 0x3F, 0x55, 0x33, 0x37, 0x25, 0x77, 0x24, 0x26, 0x74, 0x6A,
        0x28, 0x53, 0x4D, 0x69, 0x22, 0x5C, 0x44, 0x31, 0x36, 0x58, 0x3B, 0x7A, 0x51, 0x5F, 0x52
    ];

    /// <summary>
    /// Icom's credential obfuscation: each of the first 16 characters is shifted by its position and
    /// looked up in a fixed table. This is reversible and gives no protection on the wire.
    /// </summary>
    public static byte[] Passcode(string value)
    {
        var result = new byte[16];
        var bytes = Encoding.ASCII.GetBytes(value ?? "");
        for (var i = 0; i < bytes.Length && i < result.Length; i++)
        {
            var p = bytes[i] + i;
            if (p > 126)
                p = 32 + p % 127;
            result[i] = p is >= 32 and <= 126 ? PasscodeTable[p - 32] : (byte)0;
        }

        return result;
    }

    public static void WriteHeader(Span<byte> packet, byte type, ushort seq, uint localSid, uint remoteSid)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[4..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[6..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], localSid);
        BinaryPrimitives.WriteUInt32BigEndian(packet[12..], remoteSid);
    }

    public static byte[] Control(byte type, ushort seq, uint localSid, uint remoteSid)
    {
        var p = new byte[HeaderLength];
        WriteHeader(p, type, seq, localSid, remoteSid);
        return p;
    }

    public static byte[] Ping(ushort seq, uint localSid, uint remoteSid, bool reply, ReadOnlySpan<byte> replyId)
    {
        var p = new byte[PingLength];
        WriteHeader(p, TypePing, seq, localSid, remoteSid);
        p[16] = reply ? (byte)0x01 : (byte)0x00;
        replyId[..4].CopyTo(p.AsSpan(17));
        return p;
    }

    public static byte[] Login(
        uint localSid,
        uint remoteSid,
        ushort innerSeq,
        ReadOnlySpan<byte> tokenRequest,
        string user,
        string password,
        string clientName)
    {
        var p = new byte[LoginLength];
        WriteHeader(p, TypeData, 0, localSid, remoteSid);
        WriteInner(p, payloadSize: 0x70, requestType: 0x00, innerSeq);
        tokenRequest[..2].CopyTo(p.AsSpan(0x1A));
        Passcode(user).CopyTo(p, 0x40);
        Passcode(password).CopyTo(p, 0x50);
        WriteAscii(p.AsSpan(0x60, 16), clientName);
        return p;
    }

    public static byte[] Token(uint localSid, uint remoteSid, byte magic, ushort innerSeq, ReadOnlySpan<byte> authId)
    {
        var p = new byte[TokenLength];
        WriteHeader(p, TypeData, 0, localSid, remoteSid);
        WriteInner(p, payloadSize: 0x30, requestType: magic, innerSeq);
        authId[..6].CopyTo(p.AsSpan(0x1A));
        return p;
    }

    /// <summary>
    /// Stream request sent once the radio has accepted the login. The radio expects the audio fields
    /// to be filled in even though OscarWatch never opens the audio port.
    /// </summary>
    public static byte[] ConnInfoRequest(
        uint localSid,
        uint remoteSid,
        ushort innerSeq,
        ReadOnlySpan<byte> authId,
        ReadOnlySpan<byte> replyId,
        string radioName,
        string user,
        int civPort,
        int audioPort)
    {
        var p = new byte[ConnInfoLength];
        WriteHeader(p, TypeData, 0, localSid, remoteSid);
        WriteInner(p, payloadSize: 0x80, requestType: 0x03, innerSeq);
        authId[..6].CopyTo(p.AsSpan(0x1A));
        replyId[..16].CopyTo(p.AsSpan(0x20));
        WriteAscii(p.AsSpan(0x40, 32), radioName);
        Passcode(user).CopyTo(p, 0x60);
        p[0x70] = 0x01;
        p[0x71] = 0x01;
        p[0x72] = 0x04;
        p[0x73] = 0x04;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x74), AudioSampleRate);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x78), AudioSampleRate);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x7C), (uint)civPort);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x80), (uint)audioPort);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0x84), TxBufferMs);
        p[0x88] = 0x01;
        return p;
    }

    public static byte[] CivData(uint localSid, uint remoteSid, ushort civSeq, ReadOnlySpan<byte> frame)
    {
        var p = new byte[CivDataOffset + frame.Length];
        WriteHeader(p, TypeData, 0, localSid, remoteSid);
        p[0x10] = CivDataMarker;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0x11), (ushort)frame.Length);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(0x13), civSeq);
        frame.CopyTo(p.AsSpan(CivDataOffset));
        return p;
    }

    public static byte[] CivOpenClose(uint localSid, uint remoteSid, ushort civSeq, bool open)
    {
        var p = new byte[0x16];
        WriteHeader(p, TypeData, 0, localSid, remoteSid);
        p[0x10] = CivOpenCloseMarker;
        p[0x11] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(0x13), civSeq);
        p[0x15] = open ? (byte)0x05 : (byte)0x00;
        return p;
    }

    /// <summary>The CI-V bytes carried by a serial-stream data packet, or false for anything else.</summary>
    public static bool TryGetCivPayload(byte[] packet, out ReadOnlySpan<byte> frame)
    {
        frame = default;
        if (packet.Length <= CivDataOffset || packet[0x10] != CivDataMarker)
            return false;

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(0x11));
        if (length == 0 || CivDataOffset + length > packet.Length)
            return false;

        frame = packet.AsSpan(CivDataOffset, length);
        return true;
    }

    public static bool IsType(ReadOnlySpan<byte> packet, byte type) =>
        packet.Length >= HeaderLength && packet[4] == type && packet[5] == 0x00;

    public static bool IsIdle(ReadOnlySpan<byte> packet) =>
        packet.Length == HeaderLength && IsType(packet, TypeData);

    /// <summary>A data packet of <paramref name="length"/> bytes whose header length field agrees.</summary>
    public static bool IsDataPacket(ReadOnlySpan<byte> packet, int length) =>
        packet.Length == length
        && BinaryPrimitives.ReadUInt32LittleEndian(packet) == (uint)length
        && IsType(packet, TypeData);

    public static ushort Seq(ReadOnlySpan<byte> packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]);

    public static uint SenderId(ReadOnlySpan<byte> packet) =>
        BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);

    public static uint ReceiverId(ReadOnlySpan<byte> packet) =>
        BinaryPrimitives.ReadUInt32BigEndian(packet[12..]);

    public static string ReadAscii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end >= 0)
            field = field[..end];
        return Encoding.ASCII.GetString(field).Trim();
    }

    private static void WriteInner(Span<byte> packet, uint payloadSize, byte requestType, ushort innerSeq)
    {
        BinaryPrimitives.WriteUInt32BigEndian(packet[0x10..], payloadSize);
        packet[0x14] = 0x01;
        packet[0x15] = requestType;
        packet[0x17] = (byte)innerSeq;
        packet[0x18] = (byte)(innerSeq >> 8);
    }

    private static void WriteAscii(Span<byte> field, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value ?? "");
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length - 1)).CopyTo(field);
    }
}
