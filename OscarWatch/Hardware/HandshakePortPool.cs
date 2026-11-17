using System.IO.Ports;
using OscarWatch.Core.Hardware;
using Serilog;

namespace OscarWatch.Hardware;

/// <summary>
/// Handshake-only view of a serial port so <see cref="PttKeyer"/> and <see cref="CwKeyerService"/>
/// can share one open COM handle and be tested without real serial hardware.
/// </summary>
public interface IHandshakeSerialPort : IDisposable
{
    string PortName { get; }
    bool IsOpen { get; }
    bool DtrEnable { get; set; }
    bool RtsEnable { get; set; }
    void Open();
}

internal sealed class SystemHandshakeSerialPort : IHandshakeSerialPort
{
    private readonly SerialPort _port;

    public SystemHandshakeSerialPort(string portName)
    {
        _port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false
        };
    }

    public string PortName => _port.PortName;

    public bool IsOpen => _port.IsOpen;

    public bool DtrEnable
    {
        get => _port.DtrEnable;
        set => _port.DtrEnable = value;
    }

    public bool RtsEnable
    {
        get => _port.RtsEnable;
        set => _port.RtsEnable = value;
    }

    public void Open() => _port.Open();

    public void Dispose() => _port.Dispose();
}

/// <summary>
/// Shares open DTR/RTS handshake COM ports across <see cref="PttKeyer"/> (FT4 and Terminal) and
/// <see cref="CwKeyerService"/> (Logbook) inside this process, closing the OS handle when the last
/// lease is released so external applications can use the port too.
/// </summary>
public sealed class HandshakePortPool
{
    private static readonly ILogger Log = Serilog.Log.ForContext<HandshakePortPool>();

    public static HandshakePortPool Shared { get; } = new(name => new SystemHandshakeSerialPort(name));

    private readonly Func<string, IHandshakeSerialPort> _portFactory;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public HandshakePortPool(Func<string, IHandshakeSerialPort> portFactory)
    {
        ArgumentNullException.ThrowIfNull(portFactory);
        _portFactory = portFactory;
    }

    public HandshakePortLease Acquire(string portName, string ownerLabel)
    {
        var name = portName?.Trim() ?? "";
        if (name.Length == 0)
            throw new ArgumentException("Serial port name is empty.", nameof(portName));

        IHandshakeSerialPort? staleToDispose = null;
        HandshakePortLease lease;
        var openedFresh = false;

        lock (_gate)
        {
            if (_entries.TryGetValue(name, out var existing))
            {
                if (existing.IsAlive)
                {
                    lease = new HandshakePortLease(this, existing, ownerLabel);
                    existing.Leases.Add(lease);
                    Log.Information("{Owner} sharing handshake port {Port}", ownerLabel, existing.PortName);
                    return lease;
                }

                _entries.Remove(name);
                lock (existing.LineGate)
                {
                    existing.Disposed = true;
                    staleToDispose = existing.Port;
                }
            }

            var port = _portFactory(name);
            try
            {
                port.Open();
            }
            catch
            {
                try { port.Dispose(); } catch { /* the open already failed */ }
                staleToDispose?.DisposeSafely();
                throw;
            }

            var entry = new Entry(name, port);
            lease = new HandshakePortLease(this, entry, ownerLabel);
            entry.Leases.Add(lease);
            _entries[name] = entry;
            openedFresh = true;
        }

        staleToDispose?.DisposeSafely();
        if (openedFresh)
            Log.Information("{Owner} opened handshake port {Port}", ownerLabel, lease.PortName);
        return lease;
    }

    internal void Release(HandshakePortLease lease)
    {
        IHandshakeSerialPort? toClose = null;
        var entry = lease.Entry;

        lock (_gate)
        {
            if (!entry.Leases.Remove(lease))
                return;

            if (entry.Leases.Count == 0)
            {
                if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                    _entries.Remove(entry.Key);

                lock (entry.LineGate)
                {
                    entry.Disposed = true;
                    toClose = entry.Port;
                }
            }
        }

        if (toClose is null)
        {
            Log.Information("{Owner} released shared lease on {Port}", lease.OwnerLabel, entry.PortName);
            return;
        }

        try
        {
            toClose.Dispose();
            Log.Information("{Owner} released handshake port {Port}", lease.OwnerLabel, entry.PortName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Owner} handshake port {Port} close failed", lease.OwnerLabel, entry.PortName);
        }
    }

    internal sealed class Entry(string key, IHandshakeSerialPort port)
    {
        public string Key { get; } = key;
        public IHandshakeSerialPort Port { get; } = port;
        public string PortName => Port.PortName;
        public object LineGate { get; } = new();
        public HashSet<HandshakePortLease> Leases { get; } = [];
        public bool Disposed { get; set; }

        public bool IsAlive
        {
            get
            {
                lock (LineGate)
                    return !Disposed && Port.IsOpen;
            }
        }
    }
}

public sealed class HandshakePortLease : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<HandshakePortLease>();

    private readonly HandshakePortPool _pool;
    private int _disposed;

    internal HandshakePortLease(HandshakePortPool pool, HandshakePortPool.Entry entry, string ownerLabel)
    {
        _pool = pool;
        Entry = entry;
        OwnerLabel = ownerLabel;
    }

    internal HandshakePortPool.Entry Entry { get; }

    public string OwnerLabel { get; }

    public string PortName => Entry.PortName;

    public bool IsOpen => Volatile.Read(ref _disposed) == 0 && Entry.IsAlive;

    public void SetLine(PttLine line, bool assert)
    {
        lock (Entry.LineGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || Entry.Disposed || !Entry.Port.IsOpen)
                throw new InvalidOperationException($"Serial port {Entry.PortName} is closed.");

            if (line == PttLine.Rts)
                Entry.Port.RtsEnable = assert;
            else
                Entry.Port.DtrEnable = assert;
        }
    }

    public bool TrySetLine(PttLine line, bool assert)
    {
        lock (Entry.LineGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || Entry.Disposed || !Entry.Port.IsOpen)
                return false;

            try
            {
                if (line == PttLine.Rts)
                    Entry.Port.RtsEnable = assert;
                else
                    Entry.Port.DtrEnable = assert;
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "{Owner} handshake line change on {Port} failed", OwnerLabel, Entry.PortName);
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _pool.Release(this);
    }
}

file static class HandshakeSerialPortExtensions
{
    public static void DisposeSafely(this IHandshakeSerialPort port)
    {
        try { port.Dispose(); } catch { /* best-effort cleanup of a dead port */ }
    }
}
