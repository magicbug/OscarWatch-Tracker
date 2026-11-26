using OscarWatch.Core.Cloudlog;
using OscarWatch.Core.Models;
using OscarWatch.Core.Radio;

namespace OscarWatch.Tests;

public class CloudlogRadioMapperTests
{
    [Fact]
    public void TryCreate_maps_uplink_and_downlink_hz_and_modes()
    {
        var mode = new SatelliteTransponderMode
        {
            Type = "V/U",
            DownlinkKHz = 435_850,
            UplinkKHz = 145_950,
            DownlinkMode = "USB",
            UplinkMode = "LSB",
            Doppler = "REV"
        };

        var corrected = DopplerFrequencyCalculator.Compute(mode, 0, 0);
        var update = CloudlogRadioMapper.TryCreate("AO-07", mode, corrected);

        Assert.NotNull(update);
        Assert.Equal("AO-07", update.SatelliteName);
        Assert.Equal(145_950_000, update.UplinkHz);
        Assert.Equal(435_850_000, update.DownlinkHz);
        Assert.Equal("LSB", update.UplinkMode);
        Assert.Equal("USB", update.DownlinkMode);
    }

    [Fact]
    public void MapMode_converts_FMN_to_FM()
    {
        Assert.Equal("FM", CloudlogRadioMapper.MapMode("FMN"));
        Assert.Equal("USB", CloudlogRadioMapper.MapMode("DATA-USB"));
    }

    [Fact]
    public void MapMode_converts_DV_to_DIGITALVOICE()
    {
        Assert.Equal("DIGITALVOICE", CloudlogRadioMapper.MapMode("DV"));
        Assert.Equal("DIGITALVOICE", CloudlogRadioMapper.MapMode("D-STAR"));
    }

    [Fact]
    public void ToApiRequest_serializes_key_field_for_cloudlog()
    {
        var update = new CloudlogRadioUpdate("SO-50", 145_850_000, 436_795_000, "FM", "FM");
        var settings = new CloudlogSettings
        {
            ApiKey = "my-secret-key",
            RadioName = "OscarWatch"
        };

        var request = CloudlogRadioMapper.ToApiRequest(update, settings);
        var json = System.Text.Json.JsonSerializer.Serialize(request);

        Assert.Contains("\"key\":\"my-secret-key\"", json, StringComparison.Ordinal);
        Assert.Contains("\"radio\":\"OscarWatch\"", json, StringComparison.Ordinal);
        Assert.Contains("\"frequency\":\"145850000\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sat_name\":\"SO-50\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFailure_explains_cloudlog_key_message()
    {
        var msg = CloudlogApiErrorHelper.DescribeFailure(401, """{"status":"failed","reason":"missing api key"}""", 12);
        Assert.Contains("read/write", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFailure_503_html_does_not_dump_response_body()
    {
        const string html = """
            <!DOCTYPE HTML PUBLIC "-//IETF//DTD HTML 2.0//EN">
            <html><head><title>503 Service Unavailable</title></head>
            <body><h1>Service Unavailable</h1></body></html>
            """;

        var msg = CloudlogApiErrorHelper.DescribeFailure(503, html, 12);

        Assert.Contains("temporarily unavailable", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<html", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<!DOCTYPE", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRadioEndpoint_normalizes_base_url()
    {
        var url = OscarWatch.Cloudlog.CloudlogRadioClient.BuildRadioEndpoint("https://log.example.com/");
        Assert.Equal("https://log.example.com/index.php/api/radio", url);
    }
}
