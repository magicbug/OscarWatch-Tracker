using OscarWatch.Ft4.Core.Models;

namespace OscarWatch.Ft4.Core.Services;

public interface IFt4AudioService
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }

    IReadOnlyList<Ft4AudioDevice> ListInputDevices();
    IReadOnlyList<Ft4AudioDevice> ListOutputDevices();

    event Action<float[], DateTime>? InputSamples;

    bool IsRunning { get; }
    Task StartAsync(string inputDeviceId, string outputDeviceId, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    void EnqueueOutputSamples(ReadOnlySpan<float> samples);
    int OutputBufferedSamples { get; }
    void ClearOutputBuffer();
}
