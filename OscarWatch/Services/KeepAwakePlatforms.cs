using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Serilog;
using Tmds.DBus.Protocol;

namespace OscarWatch.Services;

/// <summary>Windows: <c>SetThreadExecutionState</c>, which must be cleared on the thread that set it.</summary>
internal sealed class WindowsKeepAwake : IKeepAwakePlatform
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    public void Begin(string reason)
    {
        if (SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void End() => SetThreadExecutionState(EsContinuous);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}

/// <summary>macOS: an <c>NSProcessInfo</c> activity, which also opts the app out of App Nap.</summary>
internal sealed class MacKeepAwake : IKeepAwakePlatform
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const ulong NSActivityIdleSystemSleepDisabled = 1UL << 20;
    private const ulong NSActivityUserInitiated = 0x00FFFFFFUL | NSActivityIdleSystemSleepDisabled;
    private const ulong NSActivityIdleDisplaySleepDisabled = 1UL << 40;

    private IntPtr _activity;

    public void Begin(string reason)
    {
        if (_activity != IntPtr.Zero)
            return;

        var pool = objc_autoreleasePoolPush();
        try
        {
            var nsReason = SendUtf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), reason);
            var activity = SendOptionsReason(
                ProcessInfo(),
                sel_registerName("beginActivityWithOptions:reason:"),
                NSActivityUserInitiated | NSActivityIdleDisplaySleepDisabled,
                nsReason);
            if (activity == IntPtr.Zero)
                throw new InvalidOperationException("NSProcessInfo did not start the activity");
            _activity = Send(activity, sel_registerName("retain"));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public void End()
    {
        if (_activity == IntPtr.Zero)
            return;

        var activity = _activity;
        _activity = IntPtr.Zero;
        var pool = objc_autoreleasePoolPush();
        try
        {
            SendVoid(ProcessInfo(), sel_registerName("endActivity:"), activity);
            Send(activity, sel_registerName("release"));
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static IntPtr ProcessInfo() =>
        Send(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));

    [DllImport(ObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendOptionsReason(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr argument);
}

/// <summary>
/// Linux: a systemd-logind sleep and idle inhibitor (held as a file descriptor) and a desktop
/// screensaver inhibit, so the display stays on too. Either may be missing on a given desktop.
/// </summary>
internal sealed class LinuxKeepAwake : IKeepAwakePlatform
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LinuxKeepAwake>();
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private Connection? _systemBus;
    private SafeFileHandle? _sleepInhibitor;
    private Connection? _sessionBus;
    private uint? _screenSaverCookie;

    public void Begin(string reason)
    {
        End();

        Exception? sleepError = null;
        Exception? screenError = null;
        try
        {
            InhibitSleep(reason);
        }
        catch (Exception ex)
        {
            sleepError = ex;
            CloseSystemBus();
        }

        try
        {
            InhibitScreenSaver(reason);
        }
        catch (Exception ex)
        {
            screenError = ex;
            CloseSessionBus();
        }

        if (sleepError is not null && screenError is not null)
            throw new AggregateException("Neither systemd-logind nor the desktop screensaver accepted the request", sleepError, screenError);
        if (sleepError is not null)
            Log.Warning(sleepError, "systemd-logind did not accept the sleep inhibitor; the computer may still sleep while idle");
        if (screenError is not null)
            Log.Warning(screenError, "The desktop screensaver did not accept the inhibit; the display may still sleep");
    }

    public void End()
    {
        if (_screenSaverCookie is { } cookie && _sessionBus is not null)
        {
            try
            {
                var writer = ScreenSaverWriter(_sessionBus, "UnInhibit", "u");
                writer.WriteUInt32(cookie);
                Call(_sessionBus, writer.CreateMessage());
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Screensaver UnInhibit failed; closing the bus connection releases it");
            }
        }

        CloseSessionBus();
        CloseSystemBus();
    }

    private void InhibitSleep(string reason)
    {
        var address = Address.System ?? throw new InvalidOperationException("No D-Bus system bus address");
        _systemBus = Connect(address);
        var writer = _systemBus.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.login1",
            path: "/org/freedesktop/login1",
            @interface: "org.freedesktop.login1.Manager",
            member: "Inhibit",
            signature: "ssss");
        writer.WriteString("sleep:idle");
        writer.WriteString("OscarWatch");
        writer.WriteString(reason);
        writer.WriteString("block");
        _sleepInhibitor = Wait(_systemBus.CallMethodAsync(
            writer.CreateMessage(),
            static (Message m, object? _) => m.GetBodyReader().ReadHandle<SafeFileHandle>()));
    }

    private void InhibitScreenSaver(string reason)
    {
        var address = Address.Session ?? throw new InvalidOperationException("No D-Bus session bus address");
        _sessionBus = Connect(address);
        var writer = ScreenSaverWriter(_sessionBus, "Inhibit", "ss");
        writer.WriteString("OscarWatch");
        writer.WriteString(reason);
        _screenSaverCookie = Wait(_sessionBus.CallMethodAsync(
            writer.CreateMessage(),
            static (Message m, object? _) => m.GetBodyReader().ReadUInt32()));
    }

    private static MessageWriter ScreenSaverWriter(Connection bus, string member, string signature)
    {
        var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.ScreenSaver",
            path: "/org/freedesktop/ScreenSaver",
            @interface: "org.freedesktop.ScreenSaver",
            member: member,
            signature: signature);
        return writer;
    }

    private static Connection Connect(string address)
    {
        var connection = new Connection(address);
        try
        {
            Wait(connection.ConnectAsync().AsTask());
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Call(Connection bus, MessageBuffer message) => Wait(bus.CallMethodAsync(message));

    private static void Wait(Task task) => task.WaitAsync(CallTimeout).GetAwaiter().GetResult();

    private static T Wait<T>(Task<T> task) => task.WaitAsync(CallTimeout).GetAwaiter().GetResult();

    private void CloseSystemBus()
    {
        _sleepInhibitor?.Dispose();
        _sleepInhibitor = null;
        _systemBus?.Dispose();
        _systemBus = null;
    }

    private void CloseSessionBus()
    {
        _screenSaverCookie = null;
        _sessionBus?.Dispose();
        _sessionBus = null;
    }
}

internal sealed class UnsupportedKeepAwake : IKeepAwakePlatform
{
    public void Begin(string reason) =>
        throw new PlatformNotSupportedException("Keeping the computer awake is not supported on this system");

    public void End()
    {
    }
}
