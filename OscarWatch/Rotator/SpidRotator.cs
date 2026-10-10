using OscarWatch.Core.Models;

namespace OscarWatch.Rotator;

/// <summary>
/// SPID Rot1Prog / Rot2Prog native binary protocol over serial or TCP.
/// MD-01/MD-02 Ethernet replies to set-position with a 12-byte Rot2 status frame.
/// </summary>
public sealed class SpidRotator : IRotatorDriver
{
    private readonly IRotatorSerialTransport _port;
    private readonly bool _expectSetPositionResponse;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _commandBuffer = new byte[SpidRotatorCodec.CommandLength];
    private readonly byte[] _responseBuffer = new byte[SpidRotatorCodec.Rot2ResponseLength];

    private bool _variantDetected;
    private bool _rot1Mode;
    private int _pulsesPerDegree = 2;

    public SpidRotator(string portName, int baudRate)
        : this(new SerialRotatorTransport(portName, baudRate, 2000, 2000, "\n"), expectSetPositionResponse: false)
    {
    }

    internal SpidRotator(IRotatorSerialTransport transport, bool expectSetPositionResponse = false)
    {
        _port = transport;
        _expectSetPositionResponse = expectSetPositionResponse;
    }

    public bool TryConfirmLink()
    {
        var (az, el) = GetPosition();
        return az is not null || el is not null;
    }

    public void Open()
    {
        _port.Open();
        if (_expectSetPositionResponse)
        {
            // Cancel any leftover motion from a previous TCP client session.
            try { Stop(); } catch { /* ignore */ }
        }

        EnsureVariantDetected();
    }

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        EnsureVariantDetected();

        var az = Math.Clamp(azimuthDeg, 0, settings.MaxAzimuthDeg);
        var el = Math.Clamp(elevationDeg, 0, settings.MaxElevationDeg);

        _gate.Wait();
        try
        {
            SpidRotatorCodec.BuildSetCommand(_commandBuffer, az, el, _pulsesPerDegree, _rot1Mode);
            WriteCommand(_commandBuffer);
            if (_expectSetPositionResponse)
            {
                // MD-01/MD-02 return current position after set; drain it so GetPosition stays in sync.
                ReadAndApplyResponse();
            }
            else
            {
                Thread.Sleep(150);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        _gate.Wait();
        try
        {
            SpidRotatorCodec.BuildStopCommand(_commandBuffer);
            WriteCommand(_commandBuffer);
            ReadAndApplyResponse();
        }
        catch
        {
            // ignore stop errors
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
            EnsureVariantDetectedWithinGate();

            SpidRotatorCodec.BuildStatusCommand(_commandBuffer);
            WriteCommand(_commandBuffer);
            return ReadPosition();
        }
        catch
        {
            return (null, null);
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

    private void EnsureVariantDetected()
    {
        if (_variantDetected)
            return;

        _gate.Wait();
        try
        {
            EnsureVariantDetectedWithinGate();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureVariantDetectedWithinGate()
    {
        if (_variantDetected)
            return;

        SpidRotatorCodec.BuildStatusCommand(_commandBuffer);
        WriteCommand(_commandBuffer);
        var length = ReadResponseLength();
        ApplyVariantFromResponseLength(length);
    }

    private void WriteCommand(byte[] command)
    {
        _port.DiscardInBuffer();
        _port.Write(command, 0, command.Length);
    }

    private int ReadResponseLength()
    {
        var offset = 0;
        while (offset < SpidRotatorCodec.Rot2ResponseLength)
        {
            var read = _port.Read(_responseBuffer, offset, SpidRotatorCodec.Rot2ResponseLength - offset);
            if (read <= 0)
                break;
            offset += read;
        }

        if (offset == SpidRotatorCodec.Rot1ResponseLength)
            return SpidRotatorCodec.Rot1ResponseLength;

        if (offset >= SpidRotatorCodec.Rot2ResponseLength)
            return SpidRotatorCodec.Rot2ResponseLength;

        throw new InvalidOperationException($"Unexpected SPID response length: {offset} bytes.");
    }

    private void ReadAndApplyResponse()
    {
        var length = ReadResponseLength();
        ApplyVariantFromResponseLength(length);
    }

    private void ApplyVariantFromResponseLength(int length)
    {
        if (length == SpidRotatorCodec.Rot1ResponseLength)
        {
            _rot1Mode = true;
            _pulsesPerDegree = 1;
            _variantDetected = true;
            return;
        }

        if (length == SpidRotatorCodec.Rot2ResponseLength
            && SpidRotatorCodec.TryParseRot2Status(
                _responseBuffer.AsSpan(0, length),
                out _,
                out _,
                out var pulse))
        {
            _rot1Mode = false;
            _pulsesPerDegree = pulse;
            _variantDetected = true;
        }
    }

    private (int? Azimuth, int? Elevation) ReadPosition()
    {
        var length = ReadResponseLength();

        if (length == SpidRotatorCodec.Rot1ResponseLength
            && SpidRotatorCodec.TryParseRot1Status(_responseBuffer.AsSpan(0, length), out var rot1Az))
        {
            _rot1Mode = true;
            _pulsesPerDegree = 1;
            _variantDetected = true;
            return ((int)Math.Round(rot1Az), null);
        }

        if (length == SpidRotatorCodec.Rot2ResponseLength
            && SpidRotatorCodec.TryParseRot2Status(
                _responseBuffer.AsSpan(0, length),
                out var az,
                out var el,
                out var pulse))
        {
            _rot1Mode = false;
            _pulsesPerDegree = pulse;
            _variantDetected = true;
            return ((int)Math.Round(az), (int)Math.Round(el));
        }

        return (null, null);
    }
}
