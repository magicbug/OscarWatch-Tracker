using OscarWatch.Ft4.Core.Models;

namespace OscarWatch.Ft4.Core;

public sealed class Ft4QsoSequencer
{
    private readonly string?[] _messages = new string[7];
    private string _myCall = "";
    private string _mySquare = "";

    public string MyCall
    {
        get => _myCall;
        set { _myCall = value.Trim(); GenerateMessages(); }
    }

    public string MySquare
    {
        get => _mySquare;
        set { _mySquare = value.Length >= 4 ? value[..4] : value; GenerateMessages(); }
    }

    public string? HisCall { get; private set; }
    public string? HisSquare { get; private set; }
    public string? LastHisCall { get; private set; }
    public int? MySnr { get; private set; }
    public int? HisSnr { get; private set; }
    public Ft4MessageType MessageType { get; private set; } = Ft4MessageType.CQ;
    public string? Message => _messages[(int)MessageType];
    public int RxAudioFrequencyHz { get; private set; } = Native.Ft4Constants.DefaultAudioFrequencyHz;

    public Ft4QsoSequencer(string call, string square)
    {
        MyCall = call;
        MySquare = square;
        Reset();
    }

    public void Reset()
    {
        if (!string.IsNullOrEmpty(HisCall))
            LastHisCall = HisCall;

        HisCall = null;
        HisSquare = null;
        HisSnr = null;
        MySnr = null;
        GenerateMessages();
        MessageType = Ft4MessageType.CQ;
    }

    public bool ProcessMessage(Ft4DecodeLine line, bool forceReply)
    {
        if (line.IsTransmit)
            return false;

        var deCall = ExtractCallsign(line.Message, 0);
        var dxCall = ExtractDxCall(line.Message);

        if (!forceReply && HisCall is not null && deCall != HisCall)
            return false;

        if (line.Message.Contains(" 73", StringComparison.Ordinal) || line.Message.EndsWith(" 73", StringComparison.Ordinal))
            return false;

        HisSnr = line.Snr;
        RxAudioFrequencyHz = line.FrequencyHz > 0 ? line.FrequencyHz : RxAudioFrequencyHz;
        HisCall = deCall;
        if (dxCall == MyCall && TryParseReport(line.Message, out var report))
            MySnr = report;

        GenerateMessages();

        if (!string.Equals(dxCall, MyCall, StringComparison.OrdinalIgnoreCase))
            MessageType = Ft4MessageType.De;
        else
            MessageType = GuessReplyType(line.Message);

        return true;
    }

    public bool ForceMessage(Ft4MessageType messageType)
    {
        if (!IsMessageAvailable(messageType))
            return false;

        MessageType = messageType;
        return true;
    }

    public bool IsMessageAvailable(Ft4MessageType type) =>
        _messages[(int)type] is not null;

    private Ft4MessageType GuessReplyType(string message)
    {
        if (message.Contains("RR73", StringComparison.Ordinal))
            return Ft4MessageType.RR73;
        if (message.Contains(" R+", StringComparison.Ordinal) || message.Contains(" R-", StringComparison.Ordinal) ||
            message.Contains(" R0", StringComparison.Ordinal))
            return Ft4MessageType.R_dB;
        if (message.Contains('+') || message.Contains('-'))
            return Ft4MessageType.dB;
        return Ft4MessageType.De;
    }

    private static string? ExtractCallsign(string message, int wordIndex)
    {
        var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return wordIndex < parts.Length ? parts[wordIndex] : null;
    }

    private static string? ExtractDxCall(string message)
    {
        var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;
        if (parts[0].Equals("CQ", StringComparison.OrdinalIgnoreCase))
            return parts.Length > 2 ? parts[2] : null;
        return parts[1];
    }

    private static bool TryParseReport(string message, out int report)
    {
        report = 0;
        foreach (var token in message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var t = token;
            if (t.StartsWith('R'))
                t = t[1..];
            if (t.Length is 3 or 4 && (t[0] == '+' || t[0] == '-' || char.IsDigit(t[0])))
            {
                if (int.TryParse(t, out report))
                    return true;
            }
        }

        return false;
    }

    private void GenerateMessages()
    {
        _messages[(int)Ft4MessageType.CQ] = $"CQ {MyCall} {MySquare}";

        if (HisCall is not null)
        {
            _messages[(int)Ft4MessageType.De] = $"{HisCall} {MyCall} {MySquare}";
            _messages[(int)Ft4MessageType.dB] = $"{HisCall} {MyCall} {IntToReport(HisSnr)}";
            _messages[(int)Ft4MessageType.R_dB] = $"{HisCall} {MyCall} R{IntToReport(HisSnr)}";
            _messages[(int)Ft4MessageType.RR73] = $"{HisCall} {MyCall} RR73";
            _messages[(int)Ft4MessageType._73] = $"{HisCall} {MyCall} 73";
        }
        else
        {
            for (var i = 0; i < _messages.Length; i++)
                _messages[i] = null;
            _messages[(int)Ft4MessageType.CQ] = $"CQ {MyCall} {MySquare}";
            if (LastHisCall is not null)
                _messages[(int)Ft4MessageType._73] = $"{LastHisCall} {MyCall} 73";
        }
    }

    private static string IntToReport(int? snr)
    {
        if (snr is null)
            return "00";
        var clamped = Math.Clamp(snr.Value, -24, 49);
        return clamped >= 0 ? $"+{clamped:D2}" : $"{clamped:D2}";
    }
}
