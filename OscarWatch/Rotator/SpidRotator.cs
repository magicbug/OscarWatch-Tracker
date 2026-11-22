using OscarWatch.Core.Models;

namespace OscarWatch.Rotator;

/// <summary>
/// SPID Rot1Prog / Rot2Prog native binary protocol over serial or TCP.
/// MD-01/MD-02 Ethernet replies to set-position with a 12-byte Rot2 status frame.
/// </summary>
public sealed class SpidRotator : IRotatorDriver
{
    internal const int MaxQueryAttempts = 3;
    internal const int StatusCacheMs = 750;
    private const int CommandGapMs = 50;
    private const int MaxSkippedBytes = 32;
    private const double MinValidElevationDeg = -90;

    private readonly IRotatorSerialTransport _port;
    private readonly bool _expectSetPositionResponse;
    private readonly int _commandPacingMs;
    private readonly Func<long> _clockMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _commandBuffer = new byte[SpidRotatorCodec.CommandLength];
    private readonly byte[] _responseBuffer = new byte[SpidRotatorCodec.Rot2ResponseLength];

    private bool _variantDetected;
    private bool _rot1Mode;
    private int _pulsesPerDegree = 2;
    private (int? Azimuth, int? Elevation)? _cachedPosition;
    private long _cachedAtMs;

    public SpidRotator(string portName, int baudRate)
        : this(new SerialRotatorTransport(portName, baudRate, 1000, 1000, "\n"), baudRate: baudRate)
    {
    }

    /// <param name="baudRate">
    /// Serial line speed used to wait out a command's wire time after a set-position.
    /// Zero disables pacing (TCP).
    /// </param>
    internal SpidRotator(
        IRotatorSerialTransport transport,
        bool expectSetPositionResponse = false,
        int baudRate = 0,
        Func<long>? clockMs = null)
    {
        _port = transport;
        _expectSetPositionResponse = expectSetPositionResponse;
        _commandPacingMs = CommandPacingMs(baudRate);
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Time for one 13-byte command at 8-N-1 plus a short gap, or 0 when unpaced.</summary>
    internal static int CommandPacingMs(int baudRate) =>
        baudRate <= 0
            ? 0
            : (int)Math.Ceiling(SpidRotatorCodec.CommandLength * 10 * 1000.0 / baudRate) + CommandGapMs;

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
        // Rot2Prog encodes elevation as 360 + degrees, so a park below the horizon is a normal command.
        var el = Math.Clamp(elevationDeg, RotatorSettings.SpidMinElevationDeg, settings.MaxElevationDeg);

        _gate.Wait();
        try
        {
            SpidRotatorCodec.BuildSetCommand(_commandBuffer, az, el, _pulsesPerDegree, _rot1Mode);
            WriteCommand(_commandBuffer);
            if (_expectSetPositionResponse)
            {
                // MD-01/MD-02 return current position after set; drain it so GetPosition stays in sync.
                if (TryReadFrame(out var length))
                    TryApplyPosition(length, out _);
            }
            else if (_commandPacingMs > 0)
            {
                Thread.Sleep(_commandPacingMs);
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
            var length = Query(_commandBuffer);
            TryApplyPosition(length, out _);
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
            if (_cachedPosition is { } cached && _clockMs() - _cachedAtMs < StatusCacheMs)
                return cached;

            SpidRotatorCodec.BuildStatusCommand(_commandBuffer);
            var length = Query(_commandBuffer);
            return TryApplyPosition(length, out var position) ? position : (null, null);
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
            if (_variantDetected)
                return;

            SpidRotatorCodec.BuildStatusCommand(_commandBuffer);
            var length = Query(_commandBuffer);
            TryApplyPosition(length, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends a command that the controller answers with a status frame, resending when the
    /// reply is missing or garbled. Returns the length of the valid frame in the response buffer.
    /// </summary>
    private int Query(byte[] command)
    {
        for (var attempt = 0; attempt < MaxQueryAttempts; attempt++)
        {
            WriteCommand(command);
            if (TryReadFrame(out var length) && IsValidFrame(length))
                return length;
        }

        throw new InvalidOperationException("SPID rotator did not return a valid status frame.");
    }

    private void WriteCommand(byte[] command)
    {
        _port.DiscardInBuffer();
        _port.Write(command, 0, command.Length);
    }

    /// <summary>
    /// Reads one frame, skipping any bytes before the start byte. A Rot1 frame has the end byte
    /// at index 4; a Rot2 frame has a 0 to 9 digit there and the end byte at index 11.
    /// </summary>
    private bool TryReadFrame(out int length)
    {
        length = 0;
        try
        {
            var skipped = 0;
            while (true)
            {
                if (!ReadExactly(0, 1))
                    return false;
                if (_responseBuffer[0] == SpidRotatorCodec.StartByte)
                    break;
                if (++skipped > MaxSkippedBytes)
                    return false;
            }

            if (!ReadExactly(1, SpidRotatorCodec.Rot1ResponseLength - 1))
                return false;

            if (_responseBuffer[SpidRotatorCodec.Rot1ResponseLength - 1] == SpidRotatorCodec.EndByte)
            {
                length = SpidRotatorCodec.Rot1ResponseLength;
                return true;
            }

            if (!ReadExactly(
                    SpidRotatorCodec.Rot1ResponseLength,
                    SpidRotatorCodec.Rot2ResponseLength - SpidRotatorCodec.Rot1ResponseLength))
                return false;

            if (_responseBuffer[SpidRotatorCodec.Rot2ResponseLength - 1] != SpidRotatorCodec.EndByte)
                return false;

            length = SpidRotatorCodec.Rot2ResponseLength;
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private bool ReadExactly(int offset, int count)
    {
        var end = offset + count;
        while (offset < end)
        {
            var read = _port.Read(_responseBuffer, offset, end - offset);
            if (read <= 0)
                return false;
            offset += read;
        }

        return true;
    }

    private bool IsValidFrame(int length) =>
        length == SpidRotatorCodec.Rot1ResponseLength
            ? SpidRotatorCodec.TryParseRot1Status(_responseBuffer.AsSpan(0, length), out _)
            : SpidRotatorCodec.TryParseRot2Status(_responseBuffer.AsSpan(0, length), out _, out _, out _);

    /// <summary>Decodes the frame in the response buffer, updating the variant and the status cache.</summary>
    private bool TryApplyPosition(int length, out (int? Azimuth, int? Elevation) position)
    {
        position = (null, null);

        if (length == SpidRotatorCodec.Rot1ResponseLength
            && SpidRotatorCodec.TryParseRot1Status(_responseBuffer.AsSpan(0, length), out var rot1Az))
        {
            _rot1Mode = true;
            _pulsesPerDegree = 1;
            _variantDetected = true;
            position = ((int)Math.Round(rot1Az), null);
        }
        else if (length == SpidRotatorCodec.Rot2ResponseLength
            && SpidRotatorCodec.TryParseRot2Status(
                _responseBuffer.AsSpan(0, length),
                out var az,
                out var el,
                out var pulse))
        {
            _rot1Mode = false;
            _pulsesPerDegree = pulse;
            _variantDetected = true;
            // Azimuth-only controllers can send zero elevation digits, which decode to -360°.
            int? elevation = el < MinValidElevationDeg ? null : (int)Math.Round(el);
            position = ((int)Math.Round(az), elevation);
        }
        else
        {
            return false;
        }

        _cachedPosition = position;
        _cachedAtMs = _clockMs();
        return true;
    }
}
