using System.Text;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.NServiceBus;
using DynaQueueExplorer.RabbitMq.Amqp;
using RabbitMQ.Client;

namespace DynaQueueExplorer.Tests;

public class AmqpRoundTripTests
{
    private static readonly Dictionary<string, object?> SampleHeaders = new()
    {
        ["NServiceBus.MessageId"] = Encoding.UTF8.GetBytes("abc"),
        ["binary"] = new byte[] { 0x00, 0xFF, 0x80 },
        ["int"] = 42,
        ["long"] = 9_000_000_000L,
        ["bool"] = true,
        ["double"] = 1.5,
        ["decimal"] = 12.34m,
        ["timestamp"] = new AmqpTimestamp(1_700_000_000),
        ["null"] = null,
        ["x-death"] = new List<object?>
        {
            new Dictionary<string, object?> { ["count"] = 3L, ["queue"] = Encoding.UTF8.GetBytes("orders") },
        },
    };

    [Fact]
    public void Header_types_survive_encode_and_decode()
    {
        var decoded = AmqpValueCodec.DecodeTable(AmqpValueCodec.EncodeTable(SampleHeaders));

        Assert.Equal("abc", Encoding.UTF8.GetString((byte[])decoded["NServiceBus.MessageId"]!));
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x80 }, decoded["binary"]);
        Assert.Equal(42, decoded["int"]);
        Assert.Equal(9_000_000_000L, decoded["long"]);
        Assert.Equal(true, decoded["bool"]);
        Assert.Equal(1.5, decoded["double"]);
        Assert.Equal(12.34m, decoded["decimal"]);
        Assert.Equal(new AmqpTimestamp(1_700_000_000), decoded["timestamp"]);
        Assert.Null(decoded["null"]);
        var death = Assert.IsType<Dictionary<string, object?>>(Assert.Single((List<object?>)decoded["x-death"]!));
        Assert.Equal(3L, death["count"]);
    }

    [Fact]
    public void Display_and_text_forms_are_plain()
    {
        var encoded = AmqpValueCodec.EncodeTable(SampleHeaders);

        Assert.Equal("abc", AmqpValueCodec.ToText(encoded["NServiceBus.MessageId"]));
        Assert.Equal("42", AmqpValueCodec.ToText(encoded["int"]));
        Assert.Equal("""[{"count":3,"queue":"orders"}]""", AmqpValueCodec.ToDisplay(encoded["x-death"]).GetRawText());
    }

    [Fact]
    public void Republishing_drops_broker_headers_and_optionally_failure_headers()
    {
        var properties = new StoredProperties
        {
            MessageId = "m",
            Headers = AmqpValueCodec.EncodeTable(new Dictionary<string, object?>
            {
                ["x-delivery-count"] = 4L,
                ["x-death"] = new List<object?>(),
                ["x-last-death-queue"] = Encoding.UTF8.GetBytes("q"),
                ["NServiceBus.MessageId"] = Encoding.UTF8.GetBytes("m"),
                ["NServiceBus.FailedQ"] = Encoding.UTF8.GetBytes("Sales"),
                ["NServiceBus.ExceptionInfo.Message"] = Encoding.UTF8.GetBytes("boom"),
            }),
        };

        var plain = AmqpProperties.ToAmqp(properties);
        var retry = AmqpProperties.ToAmqp(properties, NServiceBusHeaders.IsFailureHeader);

        Assert.Equal(["NServiceBus.ExceptionInfo.Message", "NServiceBus.FailedQ", "NServiceBus.MessageId"], plain.Headers!.Keys.Order());
        Assert.Equal(["NServiceBus.MessageId"], retry.Headers!.Keys);
        Assert.Equal("m", retry.MessageId);
    }

    [Fact]
    public async Task Mqx_archive_round_trip_is_lossless()
    {
        var body = new byte[] { 1, 2, 3, 0, 255 };
        var message = new RawMessage("ex", "rk", true, new StoredProperties
        {
            ContentType = "application/octet-stream",
            DeliveryMode = 2,
            Priority = 5,
            Timestamp = 1_700_000_000,
            Headers = AmqpValueCodec.EncodeTable(SampleHeaders),
        }, body);

        using var file = new MemoryStream();
        await MqxArchive.WriteAsync(file, [message, message with { Body = "second"u8.ToArray() }], "tester", new MqxSource("c", "v", "q"), CancellationToken.None);
        file.Position = 0;
        var (manifest, messages) = await MqxArchive.ReadAsync(file, CancellationToken.None);

        Assert.Equal(2, manifest.Count);
        Assert.Equal("q", manifest.Source!.Queue);
        Assert.Equal(body, messages[0].Body);
        Assert.Equal("second", Encoding.UTF8.GetString(messages[1].Body));
        Assert.Equal(message.Properties.Priority, messages[0].Properties.Priority);
        Assert.Equal(12.34m, AmqpValueCodec.DecodeTable(messages[0].Properties.Headers!)["decimal"]);
    }

    [Fact]
    public async Task Reading_something_else_fails_clearly()
    {
        using var notZip = new MemoryStream("hello"u8.ToArray());
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => MqxArchive.ReadAsync(notZip, CancellationToken.None));
    }

    [Fact]
    public void Keys_prefer_ids_and_fall_back_to_a_body_hash()
    {
        var body = "{\"a\":1}"u8.ToArray();
        Assert.Equal("nsb:1", MessageKeys.Compute("1", "2", body, body.Length));
        Assert.Equal("id:2", MessageKeys.Compute(null, "2", body, body.Length));
        Assert.Equal(MessageKeys.Compute(null, null, body, body.Length), MessageKeys.Compute(null, "", body, body.Length));
        Assert.NotEqual(MessageKeys.Compute(null, null, body, body.Length), MessageKeys.Compute(null, null, "{\"a\":2}"u8, body.Length));

        // The management API truncates large payloads: keys only hash the prefix, plus the total length.
        var large = new byte[MessageKeys.HashedBodyBytes + 10];
        Assert.Equal(MessageKeys.Compute(null, null, large, large.Length),
            MessageKeys.Compute(null, null, large.AsSpan(0, MessageKeys.HashedBodyBytes), large.Length));
    }

    [Fact]
    public void Edited_headers_keep_their_amqp_type()
    {
        var encoded = AmqpValueCodec.EncodeTable(SampleHeaders);

        Assert.Equal(encoded["int"].GetRawText(), AmqpValueCodec.FromEditedText(encoded["int"], "42").GetRawText());
        Assert.Equal(43, AmqpValueCodec.Decode(AmqpValueCodec.FromEditedText(encoded["int"], "43")));
        Assert.Equal(false, AmqpValueCodec.Decode(AmqpValueCodec.FromEditedText(encoded["bool"], "false")));
        Assert.Equal(9.5m, AmqpValueCodec.Decode(AmqpValueCodec.FromEditedText(encoded["decimal"], "9.5")));
        Assert.Equal("changed", Encoding.UTF8.GetString((byte[])AmqpValueCodec.Decode(AmqpValueCodec.FromEditedText(encoded["NServiceBus.MessageId"], "changed"))!));
        Assert.Equal(new byte[] { 1, 2 }, AmqpValueCodec.Decode(AmqpValueCodec.FromEditedText(encoded["binary"], Convert.ToBase64String([1, 2]))));
        Assert.Equal("new", Encoding.UTF8.GetString((byte[])AmqpValueCodec.Decode(AmqpValueCodec.FromEditedText(null, "new"))!));
    }

    [Fact]
    public void Invalid_edits_are_rejected()
    {
        var encoded = AmqpValueCodec.EncodeTable(SampleHeaders);

        Assert.Throws<FormatException>(() => AmqpValueCodec.FromEditedText(encoded["int"], "not a number"));
        Assert.Throws<FormatException>(() => AmqpValueCodec.FromEditedText(encoded["x-death"], "[]"));
        // Unchanged complex values are kept as they are.
        var death = encoded["x-death"];
        Assert.Equal(death.GetRawText(), AmqpValueCodec.FromEditedText(death, AmqpValueCodec.ToText(death)).GetRawText());
    }
}
