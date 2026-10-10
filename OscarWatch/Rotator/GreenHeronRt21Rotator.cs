using OscarWatch.Core.Models;

namespace OscarWatch.Rotator;

/// <summary>
/// Green Heron RT-21 Az-El driver: one DCU-1 serial link for azimuth, one for elevation.
/// </summary>
public sealed class GreenHeronRt21Rotator : IRotatorDriver
{
    private const string LineEnding = ";";

    private readonly IRotatorSerialTransport _azimuth;
    private readonly IRotatorSerialTransport _elevation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _azimuthDisposed;

    public GreenHeronRt21Rotator(string azimuthPort, string elevationPort, int baudRate)
        : this(
            new SerialRotatorTransport(azimuthPort, baudRate, 1000, 1000, LineEnding),
            new SerialRotatorTransport(elevationPort, baudRate, 1000, 1000, LineEnding))
    {
    }

    internal GreenHeronRt21Rotator(
        IRotatorSerialTransport azimuthTransport,
        IRotatorSerialTransport elevationTransport)
    {
        _azimuth = azimuthTransport;
        _elevation = elevationTransport;
    }

    public bool TryConfirmLink()
    {
        var (az, el) = GetPosition();
        return az is not null && el is not null;
    }

    public void Open()
    {
        _azimuth.Open();
        try
        {
            _elevation.Open();
        }
        catch
        {
            try
            {
                _azimuth.Dispose();
                _azimuthDisposed = true;
            }
            catch
            {
                // ignore teardown errors
            }

            throw;
        }
    }

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        var az = Math.Clamp(azimuthDeg, 0, settings.MaxAzimuthDeg);
        var el = Math.Clamp(elevationDeg, 0, settings.MaxElevationDeg);
        var azCommand = GreenHeronRt21Codec.FormatSetPosition(az);
        var elCommand = GreenHeronRt21Codec.FormatSetPosition(el);

        _gate.Wait();
        try
        {
            WriteCommand(_azimuth, azCommand);
            WriteCommand(_elevation, elCommand);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        var stop = GreenHeronRt21Codec.FormatStop();
        _gate.Wait();
        try
        {
            if (!_azimuthDisposed && _azimuth.IsOpen)
                WriteCommand(_azimuth, stop);
            if (_elevation.IsOpen)
                WriteCommand(_elevation, stop);
        }
        finally
        {
            _gate.Release();
        }
    }

    public (int? Azimuth, int? Elevation) GetPosition()
    {
        _gate.Wait();
        try
        {
            var az = QueryHeading(_azimuth);
            var el = QueryHeading(_elevation);
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
            if ((!_azimuthDisposed && _azimuth.IsOpen) || _elevation.IsOpen)
            {
                try { Stop(); } catch { /* ignore */ }
            }
        }
        catch
        {
            // ignore dispose errors
        }

        if (!_azimuthDisposed)
        {
            try { _azimuth.Dispose(); } catch { /* ignore */ }
            _azimuthDisposed = true;
        }

        try { _elevation.Dispose(); } catch { /* ignore */ }
        _gate.Dispose();
    }

    private static void WriteCommand(IRotatorSerialTransport port, string command)
    {
        port.DiscardInBuffer();
        port.Write(command);
        Thread.Sleep(150);
        port.DiscardInBuffer();
    }

    private static int? QueryHeading(IRotatorSerialTransport port)
    {
        if (!port.IsOpen)
            return null;

        port.DiscardInBuffer();
        port.Write(GreenHeronRt21Codec.FormatQueryTenths());
        var line = ReadLineResponse(port);
        if (!GreenHeronRt21Codec.TryParseHeading(line, out var heading))
            return null;

        return GreenHeronRt21Codec.ToDisplayDegrees(heading);
    }

    private static string? ReadLineResponse(IRotatorSerialTransport port)
    {
        try
        {
            Thread.Sleep(150);
            var line = port.ReadLine().Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch (TimeoutException)
        {
            return ReadExistingLine(port);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadExistingLine(IRotatorSerialTransport port)
    {
        try
        {
            var text = port.ReadExisting().Trim('\r', '\n', ' ', ';');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
