using System.Text;
using DynaQueueExplorer.IbmMq.Formats;
using DynaQueueExplorer.IbmMq.Snapshots;

namespace DynaQueueExplorer.Tests;

public class IbmMqFormatTests
{
    private const int LittleEndian = 546; // MQENC_NATIVE on x86/ARM
    private const int BigEndian = 273;    // MQENC_NATIVE on z/OS, AIX

    [Fact]
    public void Dead_letter_then_rfh2_then_string()
    {
        var body = """{"orderId":42}""";
        var bytes = new MqWriter()
            .DeadLetterHeader(2053, "ORDERS.IN", "QMPAY1", MqFormat.Rfh2, 1208, LittleEndian, putTime: new DateTime(2026, 10, 23, 9, 30, 15))
            .Rfh2(MqFormat.String, 1208, LittleEndian,
                "<mcd><Msd>jms_text</Msd></mcd>",
                "<jms><Dst>queue:///ORDERS.IN</Dst><Tms>1761211815000</Tms><Dlv>2</Dlv></jms>",
                "<usr><OrderType>express</OrderType><Retries dt='i4'>3</Retries></usr>")
            .Bytes(Encoding.UTF8.GetBytes(body))
            .ToArray();

        var parsed = MqHeaderChain.Parse(bytes, MqFormat.DeadLetter, 1208, LittleEndian);

        Assert.Null(parsed.Warning);
        Assert.Equal([MqFormat.DeadLetter, MqFormat.Rfh2], parsed.Headers.Select(h => h.Format));
        var dlh = parsed.Headers[0].Fields.ToDictionary(f => f.Name, f => f.Value);
        Assert.StartsWith("2053 MQRC_Q_FULL — The destination queue was full", dlh["Reason"]);
        Assert.Equal("ORDERS.IN@QMPAY1", dlh["Destination"]);
        Assert.Equal("2026-10-23T09:30:15.00Z", dlh["Put at"]);

        var folders = parsed.Headers[1].Folders.ToDictionary(f => f.Name);
        Assert.Equal(["mcd", "jms", "usr"], parsed.Headers[1].Folders.Select(f => f.Name));
        Assert.Contains(folders["usr"].Properties, p => p is { Name: "Retries", Value: "3", Type: "i4" });
        Assert.Contains(folders["jms"].Properties, p => p is { Name: "Dst", Value: "queue:///ORDERS.IN" });

        Assert.Equal(MqFormat.String, parsed.BodyFormat);
        Assert.Equal(body, Encoding.UTF8.GetString(bytes.AsSpan(parsed.BodyOffset)));
    }

    [Fact]
    public void Ebcdic_big_endian_headers_from_a_mainframe()
    {
        var ebcdic = Ccsids.GetEncoding(500)!;
        var text = "ORDINE 0042 QTA 0003";
        var bytes = new MqWriter(bigEndian: true, ebcdic)
            .DeadLetterHeader(2085, "MISSING.QUEUE", "MVSQM1", MqFormat.String, 500, BigEndian)
            .Bytes(ebcdic.GetBytes(text))
            .ToArray();

        var parsed = MqHeaderChain.Parse(bytes, MqFormat.DeadLetter, 500, BigEndian);

        var dlh = parsed.Headers.Single().Fields.ToDictionary(f => f.Name, f => f.Value);
        Assert.StartsWith("2085 MQRC_UNKNOWN_OBJECT_NAME", dlh["Reason"]);
        Assert.Equal("MISSING.QUEUE@MVSQM1", dlh["Destination"]);
        Assert.Equal(500, parsed.BodyCcsid);
        Assert.Equal(text, ebcdic.GetString(bytes.AsSpan(parsed.BodyOffset)));
    }

    [Theory]
    [InlineData(280, "Città: Perché")] // pre-euro Italian EBCDIC: no € in this code page (1144 is the euro variant)
    [InlineData(1144, "Città: Perché €")]
    [InlineData(37, "Hello, mainframe")]
    [InlineData(1208, "Città: Perché €")]
    public void Ccsids_round_trip(int ccsid, string text)
    {
        var encoding = Ccsids.GetEncoding(ccsid);
        Assert.NotNull(encoding);
        Assert.Equal(text, encoding.GetString(encoding.GetBytes(text)));
    }

    [Fact]
    public void Unknown_ccsids_are_reported_as_such() => Assert.Null(Ccsids.GetEncoding(99999));

    [Fact]
    public void Rfh2_null_properties()
    {
        var bytes = new MqWriter()
            .Rfh2(MqFormat.String, 1208, LittleEndian, "<usr><Note xsi:nil='true' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'></Note></usr>")
            .ToArray();

        var folder = MqHeaderChain.Parse(bytes, MqFormat.Rfh2, 1208, LittleEndian).Headers.Single().Folders.Single();

        Assert.Contains(folder.Properties, p => p is { Name: "Note", Value: null });
    }

    [Fact]
    public void Transmission_header_exposes_the_original_destination()
    {
        var bytes = new MqWriter()
            .TransmissionHeader("ORDERS.IN", "QM2", MqFormat.String, 1208, LittleEndian, "billing-service")
            .Bytes("payload"u8)
            .ToArray();

        var parsed = MqHeaderChain.Parse(bytes, MqFormat.Transmission, 1208, LittleEndian);

        var xqh = parsed.Headers.Single().Fields.ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("ORDERS.IN@QM2", xqh["Remote queue"]);
        Assert.StartsWith("billing-service", xqh["Original put by"]);
        Assert.Equal("payload", Encoding.UTF8.GetString(bytes.AsSpan(parsed.BodyOffset)));
    }

    [Fact]
    public void Truncated_headers_degrade_to_a_warning()
    {
        var bytes = new MqWriter().DeadLetterHeader(2053, "Q", "QM", MqFormat.String, 1208, LittleEndian).ToArray()[..40];

        var parsed = MqHeaderChain.Parse(bytes, MqFormat.DeadLetter, 1208, LittleEndian);

        Assert.Empty(parsed.Headers);
        Assert.NotNull(parsed.Warning);
        Assert.Equal(0, parsed.BodyOffset);
    }

    [Fact]
    public void Plain_strings_have_no_headers()
    {
        var parsed = MqHeaderChain.Parse("hello"u8, MqFormat.String, 819, LittleEndian);
        Assert.Empty(parsed.Headers);
        Assert.Equal(MqFormat.String, parsed.BodyFormat);
        Assert.Equal(0, parsed.BodyOffset);
    }

    [Fact]
    public void Pcf_messages_are_decoded_with_parameter_names()
    {
        // MQCFH (response to INQUIRE_Q) + MQCFST Q name + MQCFIN current depth
        var name = "ORDERS.IN".PadRight(48);
        var bytes = new MqWriter()
            .Int32(2).Int32(36).Int32(1).Int32(13).Int32(1).Int32(1).Int32(0).Int32(0).Int32(2)
            .Int32(4).Int32(20 + 48).Int32(2016).Int32(1208).Int32(48).String(name, 48)
            .Int32(3).Int32(16).Int32(3).Int32(17)
            .ToArray();

        var pcf = Pcf.Decode(bytes, LittleEndian);

        Assert.Equal("MQCMD_INQUIRE_Q", pcf.CommandName);
        Assert.True(pcf.Last);
        Assert.Contains(pcf.Parameters, p => p is { Name: "MQCA_Q_NAME", Value: "ORDERS.IN" });
        Assert.Contains(pcf.Parameters, p => p is { Name: "MQIA_CURRENT_Q_DEPTH", Value: "17" });
    }

    [Theory]
    [InlineData(2035, "MQRC_NOT_AUTHORIZED")]
    [InlineData(2053, "MQRC_Q_FULL")]
    [InlineData(258, "MQFB_EXPIRATION")]
    public void Reason_codes_have_names(int code, string name) => Assert.Contains(name, MqReasons.Describe(code));

    [Theory]
    [InlineData("2026-07-15", "10.30.00", "Europe/Rome", "2026-07-15T08:30:00Z")] // summer time, UTC+2
    [InlineData("2026-01-15", "10.30.00", "Europe/Rome", "2026-01-15T09:30:00Z")] // winter time, UTC+1
    [InlineData("2026-01-15", "10:30:00", "UTC", "2026-01-15T10:30:00Z")]
    public void Queue_status_times_are_read_in_the_queue_manager_time_zone(string date, string time, string zone, string expected)
    {
        var at = MqInquiry.Timestamp(date, time, TimeZoneInfo.FindSystemTimeZoneById(zone));

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), at);
    }

    [Fact]
    public void Missing_queue_status_time_is_null() =>
        Assert.Null(MqInquiry.Timestamp("", "", TimeZoneInfo.Utc));
}
