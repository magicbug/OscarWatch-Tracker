using OscarWatch.Core.Models;

namespace OscarWatch.Rotator;

public static class RotatorDriverFactory
{
    public static IRotatorDriver Create(RotatorSettings settings) =>
        settings.Type switch
        {
            RotatorType.EasyComm => new EasyCommRotator(CreateTransport(settings, 1000, 1000, "\n")),
            RotatorType.Spid => new SpidRotator(
                CreateTransport(settings, 1000, 1000, "\n"),
                baudRate: settings.TransportKind == RotatorTransportKind.Tcp ? 0 : settings.BaudRate),
            RotatorType.SpidMd01 => CreateSpidMd01(settings),
            RotatorType.Saebrt => new SaebrtRotator(
                CreateTransport(settings, 200, 200, "\n", dtrEnable: false, rtsEnable: false)),
            RotatorType.UrcTcp => new UrcTcpRotator(
                settings.NetworkHost,
                settings.NetworkPort > 0 ? settings.NetworkPort : RotatorSettings.DefaultNetworkPort),
            RotatorType.GreenHeronRt21 => new GreenHeronRt21Rotator(
                CreateTransport(settings, settings.Port, 1000, 1000, ";"),
                CreateTransport(settings, settings.ElevationPort, 1000, 1000, ";")),
            RotatorType.AntRunner => new GrblRotator(CreateTransport(settings, 500, 500, "\n")),
            _ => new Gs232Rotator(CreateTransport(settings, 1000, 1000, "\r"), settings.IsAzimuthOnly)
        };

    private static SpidRotator CreateSpidMd01(RotatorSettings settings)
    {
        var host = string.IsNullOrWhiteSpace(settings.NetworkHost)
            ? RotatorSettings.DefaultNetworkHost
            : settings.NetworkHost.Trim();
        var port = settings.NetworkPort > 0
            ? settings.NetworkPort
            : RotatorSettings.DefaultSpidMd01NetworkPort;
        var transport = new TracingRotatorTransport(new TcpRotatorTransport(host, port, 2000, 2000, "\n"), $"{host}:{port}");
        return new SpidRotator(transport, expectSetPositionResponse: true);
    }

    private static IRotatorSerialTransport CreateTransport(
        RotatorSettings settings,
        int readTimeoutMs,
        int writeTimeoutMs,
        string newLine,
        bool dtrEnable = false,
        bool rtsEnable = false) =>
        RotatorSerialTransportFactory.Create(
            settings,
            readTimeoutMs,
            writeTimeoutMs,
            newLine,
            dtrEnable,
            rtsEnable);

    private static IRotatorSerialTransport CreateTransport(
        RotatorSettings settings,
        string portName,
        int readTimeoutMs,
        int writeTimeoutMs,
        string newLine,
        bool dtrEnable = false,
        bool rtsEnable = false) =>
        RotatorSerialTransportFactory.Create(
            settings,
            portName,
            readTimeoutMs,
            writeTimeoutMs,
            newLine,
            dtrEnable,
            rtsEnable);
}
