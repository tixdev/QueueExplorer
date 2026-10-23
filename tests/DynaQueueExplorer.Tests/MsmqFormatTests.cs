using System.Text;
using DynaQueueExplorer.Core.Clusters;
using DynaQueueExplorer.Msmq;
using DynaQueueExplorer.NServiceBus;

namespace DynaQueueExplorer.Tests;

public class MsmqFormatTests
{
    // The extension of an NServiceBus 4–7 message on MSMQ, as written by its XmlSerializer.
    private const string NsbExtension =
        """
        <?xml version="1.0"?>
        <ArrayOfHeaderInfo xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <HeaderInfo><Key>NServiceBus.MessageId</Key><Value>8c7f1e2a-0000-4b6e-9d3a-a1b2c3d4e5f6</Value></HeaderInfo>
          <HeaderInfo><Key>NServiceBus.EnclosedMessageTypes</Key><Value>Sales.Messages.PlaceOrder, Sales.Messages, Version=1.0.0.0</Value></HeaderInfo>
          <HeaderInfo><Key>NServiceBus.ExceptionInfo.ExceptionType</Key><Value>System.InvalidOperationException</Value></HeaderInfo>
          <HeaderInfo><Key>NServiceBus.FailedQ</Key><Value>itta.t1.ch.sales@DYN-BA-WXI001</Value></HeaderInfo>
          <HeaderInfo><Key>NServiceBus.RelatedTo</Key><Value xsi:nil="true" /></HeaderInfo>
        </ArrayOfHeaderInfo>
        """;

    [Fact]
    public void NServiceBus_headers_are_read_from_the_extension()
    {
        var headers = MsmqFormats.ParseNServiceBusHeaders(Encoding.UTF8.GetBytes(NsbExtension));

        Assert.NotNull(headers);
        Assert.Equal("8c7f1e2a-0000-4b6e-9d3a-a1b2c3d4e5f6", headers["NServiceBus.MessageId"]);
        Assert.Equal("itta.t1.ch.sales@DYN-BA-WXI001", headers["NServiceBus.FailedQ"]);
        Assert.True(headers.ContainsKey("NServiceBus.RelatedTo"));
        Assert.Null(headers["NServiceBus.RelatedTo"]);

        var nsb = NServiceBusMessageDecoder.TryDecode(headers);
        Assert.NotNull(nsb);
        Assert.Equal("PlaceOrder", nsb.MessageTypes[0].Name);
    }

    [Fact]
    public void Utf16_and_bom_extensions_are_read()
    {
        Assert.NotNull(MsmqFormats.ParseNServiceBusHeaders(Encoding.Unicode.GetBytes(NsbExtension)));
        Assert.NotNull(MsmqFormats.ParseNServiceBusHeaders([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(NsbExtension)]));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00, 0x01, 0xFF, 0x10 })]
    public void Empty_or_binary_extensions_are_not_headers(byte[] extension) =>
        Assert.Null(MsmqFormats.ParseNServiceBusHeaders(extension));

    [Fact]
    public void Malformed_xml_and_dtds_are_not_headers()
    {
        Assert.Null(MsmqFormats.ParseNServiceBusHeaders(Encoding.UTF8.GetBytes("<ArrayOfHeaderInfo><HeaderInfo><Key>a</Key>")));
        var dtd = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e "boom">]><ArrayOfHeaderInfo><HeaderInfo><Key>k</Key><Value>&e;</Value></HeaderInfo></ArrayOfHeaderInfo>""";
        Assert.Null(MsmqFormats.ParseNServiceBusHeaders(Encoding.UTF8.GetBytes(dtd)));
    }

    [Theory]
    [InlineData(0, "normal message")]
    [InlineData(0x8000, "NACK: bad destination queue (the queue does not exist or the format name is invalid)")]
    [InlineData(0xC002, "NACK: time to be received expired in the destination queue")]
    [InlineData(0x1234, "class 0x1234")]
    public void Message_classes_explain_dead_letters(int messageClass, string expected) =>
        Assert.Equal(expected, MsmqFormats.DescribeClass(messageClass));

    [Theory]
    // T1 = both machines ending in wxi001; CH = ".ch." in the name; BS = ".bs." or a name starting with "bs".
    [InlineData("msmq-ba-wxi001", @"private$\itta.t1.ch.acm.error", "T1 CH")]
    [InlineData("dyn-ba-wxi001", @"private$\itta.t1.ch.currentaccount.processing", "T1 CH")]
    [InlineData("dyn-ba-wxi001", @"private$\itta.t1.bs.audit", "T1 BS")]
    [InlineData("msmq-ba-wxi001", @"private$\bs.integration", "T1 BS")]
    [InlineData("msmq-ba-wxi002", @"private$\itta.t1.ch.acm.error", "T2 CH")]
    [InlineData("dyn-ba-wxi005", @"private$\itta.t3.bs.audit", "T3 BS")]
    [InlineData("msmq-ba-wxi003", @"msmq-ba-wxi001.moon.corner.tech\private$\itta.t1.ch.x", "T3 CH")]
    public void Msmq_rules_select_by_machine_and_queue_name(string machine, string queue, string expected)
    {
        var groups = new[] { Group("T1", ["*-ba-wxi001"]), Group("T2", ["*-ba-wxi002"]), Group("T3", ["msmq-ba-wxi003", "dyn-ba-wxi005"]) }
            .SelectMany(g => g)
            .ToList();

        Assert.Equal([expected], groups.Where(g => g.Contains("msmq", machine, machine, queue)).Select(g => g.Name));
    }

    private static IEnumerable<QueueGroup> Group(string env, string[] machines)
    {
        yield return new QueueGroup { Name = $"{env} CH", Rules = [.. machines.Select(m => new QueueGroupRule { Broker = "msmq", Cluster = m, Queue = "*.ch.*" })] };
        yield return new QueueGroup
        {
            Name = $"{env} BS",
            Rules = [.. machines.SelectMany(m => new[] { "*.bs.*", @"private$\bs*" }.Select(q => new QueueGroupRule { Broker = "msmq", Cluster = m, Queue = q }))],
        };
    }
}
