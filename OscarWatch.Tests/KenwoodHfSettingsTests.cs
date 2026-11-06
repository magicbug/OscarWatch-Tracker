using System.Text.Json;
using System.Text.Json.Serialization;
using OscarWatch.Core.Hardware;
using OscarWatch.Core.Models;
using OscarWatch.Rig;

namespace OscarWatch.Tests;

public sealed class KenwoodHfSettingsTests
{
    [Theory]
    [InlineData(RigType.KenwoodTs990s)]
    [InlineData(RigType.KenwoodTs890s)]
    [InlineData(RigType.KenwoodTs590s)]
    [InlineData(RigType.KenwoodTs590sg)]
    public void Com_leg_is_configured_with_a_port(RigType type)
    {
        Assert.True(RigSettings.IsKenwoodHfEndpoint(type));
        Assert.True(RigSettings.IsDualCapableSerialEndpoint(type));
        Assert.True(new RigEndpointSettings { Type = type, Port = "COM9" }.IsConfigured);
        Assert.False(new RigEndpointSettings { Type = type }.IsConfigured);
    }

    [Theory]
    [InlineData(RigType.KenwoodTs990s)]
    [InlineData(RigType.KenwoodTs890s)]
    public void Lan_leg_needs_host_port_user_and_password(RigType type)
    {
        var endpoint = new RigEndpointSettings
        {
            Type = type,
            Connection = RigConnectionKind.Lan,
            NetworkHost = "192.168.1.50",
            NetworkPort = RigSettings.KenwoodLanDefaultPort,
            NetworkUser = "admin"
        };
        Assert.False(endpoint.IsConfigured);

        endpoint.NetworkPassword = "secret";
        Assert.True(endpoint.IsConfigured);
        Assert.True(endpoint.UsesNetwork);
        Assert.Equal("", endpoint.SerialPortInUse);
    }

    [Fact]
    public void Ts590_ignores_lan_connection()
    {
        var endpoint = new RigEndpointSettings
        {
            Type = RigType.KenwoodTs590sg,
            Connection = RigConnectionKind.Lan,
            Port = "COM4"
        };

        Assert.False(RigSettings.IsKenwoodLanCapable(RigType.KenwoodTs590sg));
        Assert.False(endpoint.UsesKenwoodLan);
        Assert.Equal("COM4", endpoint.SerialPortInUse);
    }

    [Theory]
    [InlineData(RigType.KenwoodTs990s)]
    [InlineData(RigType.KenwoodTs890s)]
    [InlineData(RigType.KenwoodTs590s)]
    [InlineData(RigType.KenwoodTs590sg)]
    public void Factory_creates_serial_driver(RigType type)
    {
        using var driver = RigDriverFactory.Create(new RigEndpointSettings { Type = type, Port = "COM9", BaudRate = 115200 });

        Assert.IsType<KenwoodHfDriver>(driver);
        Assert.Equal(type, driver.RigType);
    }

    [Fact]
    public void Factory_creates_lan_driver_without_connecting()
    {
        using var driver = RigDriverFactory.Create(new RigEndpointSettings
        {
            Type = RigType.KenwoodTs890s,
            Connection = RigConnectionKind.Lan,
            NetworkHost = "192.0.2.1",
            NetworkPort = RigSettings.KenwoodLanDefaultPort,
            NetworkUser = "admin",
            NetworkPassword = "secret"
        });

        Assert.IsType<KenwoodHfDriver>(driver);
        Assert.False(driver.IsConnected);
    }

    [Fact]
    public void Single_radio_mode_rejects_kenwood_hf()
    {
        Assert.Throws<InvalidOperationException>(() => RigDriverFactory.Create(new RigSettings
        {
            Enabled = true,
            Type = RigType.KenwoodTs890s,
            Port = "COM9"
        }));
    }

    [Fact]
    public void Lan_legs_do_not_hold_com_ports()
    {
        var rig = new RigSettings
        {
            Enabled = true,
            DualRadioEnabled = true,
            Downlink = new RigEndpointSettings
            {
                Type = RigType.KenwoodTs890s,
                Connection = RigConnectionKind.Lan,
                Port = "COM3",
                NetworkHost = "192.168.1.50",
                NetworkPort = RigSettings.KenwoodLanDefaultPort
            },
            Uplink = new RigEndpointSettings { Type = RigType.KenwoodTs590sg, Port = "COM3" }
        };

        Assert.False(SerialPortConflictHelper.HasConflict(new RotatorSettings(), rig));
    }

    [Fact]
    public void Endpoint_round_trips_through_settings_json()
    {
        var options = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var original = new RigEndpointSettings
        {
            Type = RigType.KenwoodTs990s,
            Connection = RigConnectionKind.Lan,
            NetworkHost = "radio.local",
            NetworkPort = 60000,
            NetworkUser = "op",
            NetworkPassword = "pw",
            NetworkUserIsAdministrator = false,
            KenwoodHardwareRtsEnabled = false
        };

        var json = JsonSerializer.Serialize(original, options);
        var copy = JsonSerializer.Deserialize<RigEndpointSettings>(json, options)!;

        Assert.Contains("\"kenwoodTs990s\"", json);
        Assert.Contains("\"lan\"", json);
        Assert.DoesNotContain("SerialPortInUse", json);
        Assert.Equal(RigConnectionKind.Lan, copy.Connection);
        Assert.Equal("radio.local", copy.NetworkHost);
        Assert.Equal("op", copy.NetworkUser);
        Assert.Equal("pw", copy.NetworkPassword);
        Assert.False(copy.NetworkUserIsAdministrator);
        Assert.False(copy.KenwoodHardwareRtsEnabled);
    }

    [Fact]
    public void Missing_kenwood_fields_default_to_com_with_rts()
    {
        var copy = JsonSerializer.Deserialize<RigEndpointSettings>("{\"Type\":\"YaesuFt817\",\"Port\":\"COM3\"}", new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        })!;

        Assert.Equal(RigConnectionKind.Serial, copy.Connection);
        Assert.True(copy.KenwoodHardwareRtsEnabled);
        Assert.True(copy.NetworkUserIsAdministrator);
    }
}
