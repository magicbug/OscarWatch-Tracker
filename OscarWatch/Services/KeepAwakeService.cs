using System.Collections.Concurrent;
using Serilog;

namespace OscarWatch.Services;

/// <summary>Keeps the computer and display out of idle sleep while at least one lease is held.</summary>
public interface IKeepAwake
{
    /// <summary>Holds the computer awake until the returned lease is disposed.</summary>
    IDisposable Acquire(string reason);
}

/// <summary>The OS power assertion behind <see cref="KeepAwakeService"/>.</summary>
internal interface IKeepAwakePlatform
{
    /// <summary>Takes the assertion. Throws when the OS refuses it.</summary>
    void Begin(string reason);

    /// <summary>Releases the assertion. Safe to call when <see cref="Begin"/> failed.</summary>
    void End();
}

/// <summary>
/// Reference-counted keep-awake. The OS assertion is taken when the first lease is acquired and
/// released when the last one is disposed. Platform calls run in order on one dedicated thread,
/// because the Windows execution state belongs to the thread that set it.
/// </summary>
public sealed class KeepAwakeService : IKeepAwake
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KeepAwakeService>();

    private readonly IKeepAwakePlatform _platform;
    private readonly Action<Action> _dispatch;
    private readonly object _gate = new();
    private int _count;

    public KeepAwakeService()
        : this(CreatePlatform(), CreateWorkerDispatch())
    {
    }

    internal KeepAwakeService(IKeepAwakePlatform platform, Action<Action> dispatch)
    {
        _platform = platform;
        _dispatch = dispatch;
    }

    public IDisposable Acquire(string reason)
    {
        lock (_gate)
        {
            if (++_count == 1)
                _dispatch(() => Begin(reason));
        }

        return new Lease(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_count == 0)
                return;
            if (--_count == 0)
                _dispatch(End);
        }
    }

    private void Begin(string reason)
    {
        try
        {
            _platform.Begin(reason);
            Log.Information("Keeping the computer awake: {Reason}", reason);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not keep the computer awake ({Reason}); it may sleep while idle", reason);
        }
    }

    private void End()
    {
        try
        {
            _platform.End();
            Log.Information("No longer keeping the computer awake");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not release the keep-awake request");
        }
    }

    private static IKeepAwakePlatform CreatePlatform()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsKeepAwake();
        if (OperatingSystem.IsMacOS())
            return new MacKeepAwake();
        if (OperatingSystem.IsLinux())
            return new LinuxKeepAwake();
        return new UnsupportedKeepAwake();
    }

    private static Action<Action> CreateWorkerDispatch()
    {
        var queue = new BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable())
                work();
        })
        {
            IsBackground = true,
            Name = "OscarWatch keep awake",
        };
        thread.Start();
        return queue.Add;
    }

    private sealed class Lease(KeepAwakeService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release();
        }
    }
}

/// <summary>Holds at most one lease, switched on and off by <see cref="Set"/>.</summary>
public sealed class KeepAwakeHold : IDisposable
{
    private readonly IKeepAwake _keepAwake;
    private readonly string _reason;
    private IDisposable? _lease;

    public KeepAwakeHold(IKeepAwake keepAwake, string reason)
    {
        _keepAwake = keepAwake;
        _reason = reason;
    }

    public bool IsHeld => _lease is not null;

    public void Set(bool wanted)
    {
        if (wanted && _lease is null)
        {
            _lease = _keepAwake.Acquire(_reason);
        }
        else if (!wanted && _lease is not null)
        {
            _lease.Dispose();
            _lease = null;
        }
    }

    public void Dispose() => Set(false);
}
