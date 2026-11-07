using System.IO.Ports;

namespace OscarWatch.Troubleshoot;

/// <summary>Raw bytes in and out, so the probe can log exactly what crossed the wire.</summary>
internal interface IGs232Port : IDisposable
{
    void Open();

    void Write(byte[] data);

    int BytesToRead { get; }

    int Read(byte[] buffer, int offset, int count);
}

/// <summary>Opened the way the tracker opens a GS-232 controller: 8N1, no handshake, DTR and RTS off.</summary>
internal sealed class Gs232SerialPort(string portName, int baudRate) : IGs232Port
{
    public const string LineSettings = "8 data bits, no parity, 1 stop bit, no handshake, DTR and RTS off";

    private readonly SerialPort _port = new(portName, baudRate, Parity.None, 8, StopBits.One)
    {
        Handshake = Handshake.None,
        ReadTimeout = 1000,
        WriteTimeout = 1000,
        DtrEnable = false,
        RtsEnable = false
    };

    public void Open() => _port.Open();

    public void Write(byte[] data) => _port.Write(data, 0, data.Length);

    public int BytesToRead => _port.BytesToRead;

    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);

    public void Dispose() => _port.Dispose();
}
