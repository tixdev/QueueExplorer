using System.Text;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.RabbitMq.Management;

namespace DynaQueueExplorer.Tests;

public class MessageBodyFormatterTests
{
    [Fact]
    public void Indents_json()
    {
        var body = MessageBodyFormatter.Format("{\"a\":1,\"b\":\"è\"}", isBase64: false, "application/json", truncated: false);

        Assert.Equal(BodyFormat.Json, body.Format);
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": \"è\"\n}", body.Text!.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Detects_json_without_content_type_and_strips_bom() =>
        Assert.Equal(BodyFormat.Json, MessageBodyFormatter.Format("﻿[1,2]", false, null, false).Format);

    [Fact]
    public void Indents_xml()
    {
        var body = MessageBodyFormatter.Format("<a><b>1</b></a>", false, "text/xml", false);

        Assert.Equal(BodyFormat.Xml, body.Format);
        Assert.Contains("\n  <b>1</b>", body.Text!.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Truncated_payloads_are_shown_as_text() =>
        Assert.Equal(BodyFormat.Text, MessageBodyFormatter.Format("{\"a\":", false, "application/json", truncated: true).Format);

    [Fact]
    public void Invalid_json_falls_back_to_text() =>
        Assert.Equal(BodyFormat.Text, MessageBodyFormatter.Format("{not json", false, "application/json", false).Format);

    [Fact]
    public void Base64_utf8_text_is_decoded()
    {
        var body = MessageBodyFormatter.Format(Convert.ToBase64String(Encoding.UTF8.GetBytes("ciao")), true, "text/plain", false);

        Assert.Equal(BodyFormat.Text, body.Format);
        Assert.Equal("ciao", body.Text);
    }

    [Fact]
    public void Binary_stays_base64()
    {
        var payload = Convert.ToBase64String([0x00, 0xFF, 0x10, 0x80]);
        var body = MessageBodyFormatter.Format(payload, true, "application/octet-stream", false);

        Assert.Equal(BodyFormat.Binary, body.Format);
        Assert.Equal(payload, body.Base64);
        Assert.Null(body.Text);
    }

    [Theory]
    [InlineData("amqp://user:secret@host:5672/vh", "amqp://user:***@host:5672/vh")]
    [InlineData("amqps://u:p%40ss@host/vh", "amqps://u:***@host/vh")]
    [InlineData("amqp:///sales", "amqp:///sales")]
    [InlineData("amqp://user@host", "amqp://user@host")]
    public void Masks_passwords_in_uris(string uri, string expected) =>
        Assert.Equal(expected, CredentialMasking.MaskUri(uri));
}
