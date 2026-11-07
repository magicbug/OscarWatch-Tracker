using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Rig;

public static class RigDriverFactory
{
    public static IRigDriver Create(RigSettings settings)
    {
        if (settings.DualRadioEnabled)
            throw new InvalidOperationException("Use Create(RigEndpointSettings) for dual-radio endpoints.");

        if (settings.Type == RigType.FlexSmartSdr)
        {
            return new FlexRadioDriver(
                settings.NetworkHost,
                settings.NetworkPort > 0 ? settings.NetworkPort : RigSettings.FlexSmartSdrDefaultPort,
                settings.CatDelayMs);
        }

        if (settings.UsesIcomLan)
        {
            return CreateIcomLan(
                settings.Type,
                settings.NetworkHost,
                settings.NetworkPort,
                settings.NetworkUser,
                settings.NetworkPassword,
                settings.CivAddress,
                settings.CatDelayMs);
        }

        return CreateSingle(
            settings.Type,
            settings.Port,
            settings.BaudRate,
            settings.Region,
            settings.CatDelayMs,
            settings.CivAddress,
            settings.KenwoodHardwareRtsEnabled,
            settings.KenwoodTraceEnabled);
    }

    public static IRigDriver Create(RigEndpointSettings endpoint) => endpoint.UsesIcomLan
        ? CreateIcomLan(
            endpoint.Type,
            endpoint.NetworkHost,
            endpoint.NetworkPort,
            endpoint.NetworkUser,
            endpoint.NetworkPassword,
            ResolveEndpointCivAddress(endpoint),
            endpoint.CatDelayMs)
        : CreateEndpoint(endpoint);

    /// <summary>Icom IC-9700, IC-705, IC-905 or IC-7300 over network control (CI-V only, no audio).</summary>
    public static IRigDriver CreateIcomLan(
        RigType type,
        string host,
        int port,
        string user,
        string password,
        string civAddressHex,
        int catDelayMs)
    {
        var controlPort = port is > 0 and <= 65535 ? port : RigSettings.IcomLanDefaultPort;
        var civAddress = string.IsNullOrWhiteSpace(civAddressHex)
            ? RigSettings.DefaultCivAddressFor(type)
            : civAddressHex.Trim();
        var civ = IcomCivCodec.ParseCivAddressHex(civAddress);
        var label = $"{host?.Trim()}:{controlPort}";
        Func<IIcomCivTransport> transport = () =>
            new IcomLanTransport(host ?? "", controlPort, user ?? "", password ?? "", civ, catDelayMs);

        return type switch
        {
            RigType.IcomIc9700 => new IcomIc9700Driver(transport, label, civAddress, catDelayMs),
            RigType.IcomIc705 => new IcomIc705Driver(transport, label, civAddress, catDelayMs),
            RigType.IcomIc905 => new IcomIc905Driver(transport, label, civAddress, catDelayMs),
            RigType.IcomIc7300 => new IcomIc7300Driver(transport, label, civAddress, catDelayMs),
            _ => throw new InvalidOperationException($"{type} does not support Icom network control.")
        };
    }

    private static IRigDriver CreateEndpoint(RigEndpointSettings endpoint) => endpoint.Type switch
    {
        RigType.SdrRigCtlTcp => new RigCtlTcpDriver(
            endpoint.NetworkHost,
            endpoint.NetworkPort,
            endpoint.CatDelayMs),
        RigType.Dummy => new DummyRigDriver(),
        RigType.YaesuFt817 => new YaesuFt817Driver(
            RigType.YaesuFt817, endpoint.Port, endpoint.BaudRate, endpoint.Region, endpoint.CatDelayMs),
        RigType.YaesuFt818 => new YaesuFt818Driver(
            endpoint.Port, endpoint.BaudRate, endpoint.Region, endpoint.CatDelayMs),
        RigType.YaesuFt857 => new YaesuFt857Driver(
            endpoint.Port, endpoint.BaudRate, endpoint.Region, endpoint.CatDelayMs),
        RigType.YaesuFt991 => new YaesuFt991Driver(
            RigType.YaesuFt991, endpoint.Port, endpoint.BaudRate, endpoint.Region, endpoint.CatDelayMs),
        RigType.YaesuFt991a => new YaesuFt991aDriver(
            endpoint.Port, endpoint.BaudRate, endpoint.Region, endpoint.CatDelayMs),
        RigType.YaesuFtx1 => new YaesuFtx1Driver(
            endpoint.Port, endpoint.BaudRate, endpoint.Region, endpoint.CatDelayMs),
        RigType.IcomIc705 => new IcomIc705Driver(
            endpoint.Port, endpoint.BaudRate, ResolveEndpointCivAddress(endpoint), endpoint.CatDelayMs),
        RigType.IcomIc7300 => new IcomIc7300Driver(
            endpoint.Port, endpoint.BaudRate, ResolveEndpointCivAddress(endpoint), endpoint.CatDelayMs),
        RigType.IcomIc905 => new IcomIc905Driver(
            endpoint.Port, endpoint.BaudRate, ResolveEndpointCivAddress(endpoint), endpoint.CatDelayMs),
        RigType.IcomIc7100 => new IcomIc7100Driver(
            endpoint.Port, endpoint.BaudRate, ResolveEndpointCivAddress(endpoint), endpoint.CatDelayMs),
        RigType.IcomIc706 or RigType.IcomIc706Mkii or RigType.IcomIc706MkiiG =>
            CreateIc706SeriesDriver(endpoint),
        RigType.KenwoodThD74 or RigType.KenwoodThD75 => new KenwoodThD7xDriver(
            endpoint.Type, endpoint.Port, endpoint.BaudRate, endpoint.CatDelayMs),
        RigType.KenwoodTs990s or RigType.KenwoodTs890s or RigType.KenwoodTs590s or RigType.KenwoodTs590sg =>
            CreateKenwoodHfDriver(endpoint),
        _ => CreateSingle(
            endpoint.Type,
            endpoint.Port,
            endpoint.BaudRate,
            endpoint.Region,
            endpoint.CatDelayMs,
            ResolveEndpointCivAddress(endpoint))
    };

    /// <summary>Dual-radio uplink must be a serial CAT rig or dummy, not SDR rigctl.</summary>
    public static void EnsureValidDualEndpoint(RigEndpointSettings endpoint, bool isDownlink)
    {
        if (endpoint.Type == RigType.SdrRigCtlTcp && !isDownlink)
            throw new InvalidOperationException("SDR rigctl TCP is supported on the downlink only.");

        if (endpoint.Type == RigType.FlexSmartSdr)
            throw new InvalidOperationException("FlexRadio SmartSDR is a single-radio full-duplex driver; disable Dual radio.");

        if (endpoint.Type == RigType.Dummy && isDownlink)
            throw new InvalidOperationException("Dummy radio is supported on the uplink only.");
    }

    private static IRigDriver CreateKenwoodHfDriver(RigEndpointSettings endpoint)
    {
        if (!endpoint.UsesKenwoodLan)
        {
            return new KenwoodHfDriver(
                endpoint.Type,
                endpoint.Port,
                endpoint.BaudRate,
                endpoint.CatDelayMs,
                endpoint.KenwoodHardwareRtsEnabled);
        }

        var transport = new KenwoodLanTransport(
            KenwoodHfCatCodec.GetProfile(endpoint.Type),
            endpoint.NetworkHost,
            endpoint.NetworkPort is > 0 and <= 65535 ? endpoint.NetworkPort : RigSettings.KenwoodLanDefaultPort,
            endpoint.NetworkUser,
            endpoint.NetworkPassword,
            endpoint.NetworkUserIsAdministrator);
        return new KenwoodHfDriver(endpoint.Type, transport, endpoint.CatDelayMs);
    }

    private static IRigDriver CreateIc706SeriesDriver(RigEndpointSettings endpoint) =>
        new IcomIc706SeriesDriver(
            endpoint.Type,
            endpoint.Port,
            endpoint.BaudRate,
            ResolveEndpointCivAddress(endpoint),
            endpoint.CatDelayMs);

    private static string ResolveEndpointCivAddress(RigEndpointSettings endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.CivAddress))
            return endpoint.CivAddress.Trim();

        return RigSettings.DefaultCivAddressFor(endpoint.Type);
    }

    private static IRigDriver CreateSingle(
        RigType type,
        string port,
        int baudRate,
        RigRegion region,
        int catDelayMs,
        string civAddress,
        bool kenwoodHardwareRtsEnabled = true,
        bool kenwoodTraceEnabled = true)
    {
        return type switch
        {
            RigType.IcomIc910 => new IcomIc910Driver(port, baudRate, civAddress, catDelayMs),
            RigType.IcomIc9100 => new IcomIc9100Driver(port, baudRate, civAddress, catDelayMs),
            RigType.IcomIc9700 => new IcomIc9700Driver(port, baudRate, civAddress, catDelayMs),
            RigType.IcomIc821h => new IcomIc821hDriver(port, baudRate, civAddress, catDelayMs),
            RigType.IcomIc705 =>
                throw new InvalidOperationException("IC-705 requires Settings → Radio → Dual radio."),
            RigType.IcomIc7300 =>
                throw new InvalidOperationException("IC-7300 requires Settings → Radio → Dual radio."),
            RigType.IcomIc905 =>
                throw new InvalidOperationException("IC-905 requires Settings → Radio → Dual radio."),
            RigType.IcomIc7100 =>
                throw new InvalidOperationException("IC-7100 requires Settings → Radio → Dual radio."),
            RigType.IcomIc706 or RigType.IcomIc706Mkii or RigType.IcomIc706MkiiG =>
                throw new InvalidOperationException("IC-706 series radios require Settings → Radio → Dual radio."),
            RigType.YaesuFt847 => new YaesuFt847Driver(port, baudRate, catDelayMs),
            RigType.YaesuFt817 or RigType.YaesuFt818 =>
                throw new InvalidOperationException("FT-817/FT-818 require Settings → Radio → Dual radio."),
            RigType.YaesuFt857 =>
                throw new InvalidOperationException("FT-857/FT-857D require Settings → Radio → Dual radio."),
            RigType.YaesuFt991 or RigType.YaesuFt991a =>
                throw new InvalidOperationException("FT-991/FT-991A require Settings → Radio → Dual radio."),
            RigType.YaesuFtx1 =>
                throw new InvalidOperationException("FTX-1 series radios require Settings → Radio → Dual radio."),
            RigType.KenwoodThD74 or RigType.KenwoodThD75 =>
                throw new InvalidOperationException("TH-D74/TH-D75 require Settings → Radio → Dual radio."),
            RigType.KenwoodTs990s or RigType.KenwoodTs890s or RigType.KenwoodTs590s or RigType.KenwoodTs590sg =>
                throw new InvalidOperationException("TS-990S, TS-890S and TS-590 radios require Settings → Radio → Dual radio."),
            RigType.KenwoodTs2000 => new KenwoodTs2000Driver(
                port,
                baudRate,
                catDelayMs,
                hardwareRtsEnabled: kenwoodHardwareRtsEnabled,
                traceEnabled: kenwoodTraceEnabled),
            RigType.FlexSmartSdr =>
                throw new InvalidOperationException("FlexRadio SmartSDR requires network host/port via RigSettings."),
            RigType.SdrRigCtlTcp =>
                throw new InvalidOperationException("SDR rigctl TCP requires Settings → Radio → Dual radio (downlink)."),
            RigType.Dummy => new DummyRigDriver(),
            _ => new DummyRigDriver()
        };
    }
}
