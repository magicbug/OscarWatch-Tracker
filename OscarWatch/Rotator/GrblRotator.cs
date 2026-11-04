using OscarWatch.Core.Models;
using Serilog;

namespace OscarWatch.Rotator;

/// <summary>
/// AntRunner (GRBL 1.1 on ESP32) az/el driver. X is azimuth, Y is elevation, 360° = 40 mm on both axes.
/// Uses absolute jogs so retargeting can cancel the move in flight instead of queueing G0 blocks.
/// Never writes <c>$</c> settings, homes, or changes work offsets: zero is set mechanically before power-on.
/// </summary>
public sealed class GrblRotator : IRotatorDriver
{
    private static readonly ILogger Log = Serilog.Log.ForContext<GrblRotator>();

    private const string LineEnding = "\n";
    private const int MaxReplyLines = 8;
    private const int CancelPollAttempts = 10;
    private const int CancelPollDelayMs = 50;

    /// <summary>Retarget a move in flight only when the new target is at least this far away (about 2°).</summary>
    private const double RetargetThresholdMm = 0.2;

    /// <summary>Step/mm and direction mask the AntRunner firmware needs for the 40 mm/turn scale.</summary>
    private const double ExpectedStepsPerMm = 1776;
    private const int ExpectedDirectionInvertMask = 3;

    private readonly IRotatorSerialTransport _port;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private (double X, double Y)? _target;
    private double? _machineX;
    private bool _jogUnsupported;
    private bool _unlockSent;

    internal GrblRotator(IRotatorSerialTransport transport)
    {
        _port = transport;
    }

    public void Open()
    {
        _port.Open();
        _gate.Wait();
        try
        {
            CheckFirmwareSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SetPosition(double azimuthDeg, double elevationDeg, RotatorSettings settings)
    {
        var el = Math.Clamp(elevationDeg, 0, settings.MaxElevationDeg);

        _gate.Wait();
        try
        {
            var status = QueryStatus();
            if (status is { } s)
            {
                if (s.IsAlarm)
                    UnlockOnce();
                else
                    _unlockSent = false;

                _machineX = s.MachineXMm;
            }

            var x = GrblCommandFormatter.ResolveMachineX(azimuthDeg, _target?.X ?? _machineX);
            var y = GrblCommandFormatter.DegreesToMillimetres(el);

            if (status is { IsMoving: true })
            {
                if (_jogUnsupported || IsNearTarget(x, y))
                    return;

                CancelJog();
            }
            else if (_jogUnsupported && IsNearTarget(x, y) && status is not { IsIdle: true })
            {
                return;
            }

            SendMove(x, y, controllerWasIdle: status is { IsIdle: true });
            _target = (x, y);
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
            if (!_jogUnsupported)
                _port.Write([GrblCommandFormatter.JogCancel], 0, 1);
            _target = null;
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
            if (QueryStatus() is not { } status)
                return (null, null);

            _machineX = status.MachineXMm;

            var az = GrblCommandFormatter.MachineXToAzimuth(status.MachineXMm);
            var el = GrblCommandFormatter.MillimetresToDegrees(status.MachineYMm);
            return ((int)Math.Round(az) % 360, (int)Math.Round(el));
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

    private bool IsNearTarget(double x, double y) =>
        _target is { } t
        && Math.Abs(t.X - x) < RetargetThresholdMm
        && Math.Abs(t.Y - y) < RetargetThresholdMm;

    /// <summary>
    /// Jogs to the target. Falls back to G0 for good only when an idle controller rejects <c>$J=</c>;
    /// rejections in Alarm or Hold are state errors, not a missing jog feature.
    /// </summary>
    private void SendMove(double x, double y, bool controllerWasIdle)
    {
        if (!_jogUnsupported)
        {
            var reply = SendLine(GrblCommandFormatter.FormatJog(x, y));
            if (!GrblStatusParser.IsError(reply) || !controllerWasIdle)
                return;

            Log.Warning("AntRunner rejected jog ({Reply}); falling back to G0 moves", reply);
            _jogUnsupported = true;
        }

        SendLine(GrblCommandFormatter.FormatRapid(x, y));
    }

    private void CancelJog()
    {
        _port.Write([GrblCommandFormatter.JogCancel], 0, 1);
        for (var i = 0; i < CancelPollAttempts; i++)
        {
            Thread.Sleep(CancelPollDelayMs);
            if (QueryStatus() is not { IsMoving: true })
                return;
        }
    }

    private void UnlockOnce()
    {
        if (_unlockSent)
            return;

        _unlockSent = true;
        Log.Warning("AntRunner reported Alarm; sending $X to unlock");
        SendLine("$X");
    }

    private GrblStatus? QueryStatus()
    {
        try
        {
            _port.DiscardInBuffer();
            _port.Write("?");
            for (var i = 0; i < MaxReplyLines; i++)
            {
                if (GrblStatusParser.TryParseStatus(_port.ReadLine()) is { } status)
                    return status;
            }
        }
        catch (TimeoutException)
        {
        }

        return null;
    }

    /// <summary>Sends one line and returns the <c>ok</c> / <c>error:n</c> reply, or null on timeout.</summary>
    private string? SendLine(string line)
    {
        _port.DiscardInBuffer();
        _port.Write(line + LineEnding);
        try
        {
            for (var i = 0; i < MaxReplyLines; i++)
            {
                var reply = _port.ReadLine().Trim();
                if (GrblStatusParser.IsOk(reply) || GrblStatusParser.IsError(reply))
                    return reply;
            }
        }
        catch (TimeoutException)
        {
        }

        return null;
    }

    /// <summary>Reads <c>$$</c> and logs when step/mm or direction differ from the AntRunner defaults.</summary>
    private void CheckFirmwareSettings()
    {
        var settings = new Dictionary<int, double>();
        try
        {
            _port.DiscardInBuffer();
            _port.Write("$$" + LineEnding);
            for (var i = 0; i < 80; i++)
            {
                var line = _port.ReadLine();
                if (GrblStatusParser.IsOk(line) || GrblStatusParser.IsError(line))
                    break;
                if (GrblStatusParser.TryParseSetting(line, out var id, out var value))
                    settings[id] = value;
            }
        }
        catch (TimeoutException)
        {
        }

        if (settings.Count == 0)
        {
            Log.Information("AntRunner did not answer $$; skipping firmware settings check");
            return;
        }

        foreach (var axis in (ReadOnlySpan<int>)[100, 101])
        {
            if (settings.TryGetValue(axis, out var steps) && Math.Abs(steps - ExpectedStepsPerMm) > 0.5)
                Log.Warning(
                    "AntRunner ${Setting}={Value} differs from {Expected}; angles will be scaled wrongly",
                    axis, steps, ExpectedStepsPerMm);
        }

        if (settings.TryGetValue(3, out var mask) && (int)mask != ExpectedDirectionInvertMask)
            Log.Warning(
                "AntRunner $3={Value} differs from {Expected}; an axis may turn the wrong way",
                mask, ExpectedDirectionInvertMask);
    }
}
