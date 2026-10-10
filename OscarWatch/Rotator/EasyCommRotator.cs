using System.Globalization;
using OscarWatch.Core.Models;

namespace OscarWatch.Rotator;

/// <summary>EasyComm II rotator driver (combined AZ/EL commands).</summary>
public sealed class EasyCommRotator : IRotatorDriver
{
    private const string LineEnding = "\n";

    private readonly IRotatorSerialTransport _port;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EasyCommRotator(string portName, int baudRate)
        : this(new SerialRotatorTransport(portName, baudRate, 1000, 1000, LineEnding))
    {
    }

    internal EasyCommRotator(IRotatorSerialTransport transport)
    {
        _port = transport;
    }

    public void Open() => _port.Open();

    public bool TryConfirmLink()
    {
        var (az, el) = GetPosition();
        return az is not null || el is not null;
    }

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        var az = Math.Clamp(azimuthDeg, 0, settings.MaxAzimuthDeg);
        var el = Math.Clamp(elevationDeg, 0, settings.MaxElevationDeg);
        var command = string.Create(CultureInfo.InvariantCulture, $"AZ{az:F1} EL{el:F1}");
        SendCommand(command);
    }

    public void Stop() => SendCommand("SA SE");

    public (int? Azimuth, int? Elevation) GetPosition()
    {
        _gate.Wait();
        try
        {
            var az = QueryAxis("AZ");
            var el = QueryAxis("EL");
            return (az, el);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_port.IsOpen)
            {
                try { Stop(); } catch { /* ignore */ }
            }
        }
        catch
        {
            // ignore dispose errors
        }

        _port.Dispose();
        _gate.Dispose();
    }

    private int? QueryAxis(string axis)
    {
        _port.DiscardInBuffer();
        _port.Write(axis + LineEnding);
        return EasyCommPositionParser.TryParseAxis(ReadLineResponse(), axis);
    }

    private void SendCommand(string command)
    {
        _gate.Wait();
        try
        {
            _port.DiscardInBuffer();
            _port.Write(command + LineEnding);
            Thread.Sleep(150);
            _port.DiscardInBuffer();
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? ReadLineResponse()
    {
        try
        {
            Thread.Sleep(150);
            var line = _port.ReadLine().Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch (TimeoutException)
        {
            return ReadExistingLine();
        }
        catch
        {
            return null;
        }
    }

    private string? ReadExistingLine()
    {
        try
        {
            var text = _port.ReadExisting().Trim('\r', '\n', ' ');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
