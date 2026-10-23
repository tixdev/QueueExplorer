using System.Text;
using System.Text.Json;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.IbmMq.Client;
using DynaQueueExplorer.IbmMq.Formats;
using DynaQueueExplorer.NServiceBus;
using DynaQueueExplorer.RabbitMq.Amqp;
using DynaQueueExplorer.RabbitMq.Management;

namespace DynaQueueExplorer.Api.Endpoints;

/// <summary>Turns messages from any source (management API browse, AMQP, staging) into the same view model.</summary>
public static class MessageViews
{
    public static BrowsedMessage FromManagement(int index, RmqGetMessage message, int truncateBytes)
    {
        var properties = message.Properties ?? [];
        var headers = properties.TryGetValue("headers", out var h) && h.ValueKind == JsonValueKind.Object
            ? h.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : [];
        var basicProperties = properties.Where(p => p.Key != "headers").ToDictionary(p => p.Key, p => p.Value);

        var headerText = headers.ToDictionary(p => p.Key, p => HeaderToString(p.Value));
        var nsb = NServiceBusMessageDecoder.TryDecode(headerText);
        var contentType = nsb?.ContentType ?? (basicProperties.TryGetValue("content_type", out var ctProp) ? ctProp.GetString() : null);

        var isBase64 = message.PayloadEncoding == "base64";
        var bodyPrefix = isBase64 ? Convert.FromBase64String(message.Payload) : Encoding.UTF8.GetBytes(message.Payload);
        var messageId = basicProperties.TryGetValue("message_id", out var mid) ? mid.GetString() : null;
        var key = MessageKeys.Compute(headerText.GetValueOrDefault("NServiceBus.MessageId"), messageId, bodyPrefix, message.PayloadBytes);

        var body = MessageBodyFormatter.Format(message.Payload, isBase64, contentType, truncated: message.PayloadBytes > truncateBytes);
        long? timestamp = basicProperties.TryGetValue("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : null;
        return new BrowsedMessage(index, key, message.Redelivered, message.Exchange, message.RoutingKey, message.PayloadBytes,
            basicProperties, headers, contentType, body, nsb, SentAt: SentAt(nsb, timestamp));
    }

    public static BrowsedMessage FromRaw(int index, RawMessage message, long? stagedId = null, bool discarded = false,
        DateTimeOffset? lastPublishedAt = null, string? lastPublishedTo = null, string? broker = null,
        DateTimeOffset? editedAt = null, string? editedBy = null)
    {
        var headerText = AmqpProperties.HeaderText(message.Properties);
        var nsb = NServiceBusMessageDecoder.TryDecode(headerText);
        var contentType = nsb?.ContentType ?? message.Properties.ContentType;
        var body = MessageBodyFormatter.Format(Convert.ToBase64String(message.Body), isBase64: true, contentType, truncated: false);
        return new BrowsedMessage(index, MessageOperations.KeyOf(message), message.Redelivered, message.Exchange, message.RoutingKey,
            message.Body.Length, DisplayProperties(message.Properties), AmqpProperties.HeaderDisplay(message.Properties), contentType, body, nsb,
            stagedId, discarded, lastPublishedAt, lastPublishedTo, broker, editedAt, editedBy, SentAt: SentAt(nsb, message.Properties.Timestamp));
    }

    /// <summary>How much of a text body is also sent as bytes, for the hex view of IBM MQ messages.</summary>
    private const int RawBodyPreviewBytes = 4096;

    public static BrowsedMessage FromMq(int index, MqRawMessage m)
    {
        var parsed = MqHeaderChain.Parse(m.Data, m.Format, m.Ccsid, m.Encoding);
        var body = m.Data.AsSpan(parsed.BodyOffset);
        var truncated = m.Length > m.Data.Length;
        PcfMessage? pcf = null;
        FormattedBody formatted;
        var bodyFormat = parsed.BodyFormat;
        if (MqFormat.IsPcf(bodyFormat))
        {
            try
            {
                pcf = Pcf.Decode(body, parsed.BodyEncoding);
                formatted = new FormattedBody(BodyFormat.Text, string.Join('\n', pcf.Parameters.Select(p => $"{p.Name} = {p.Value}")), null, truncated);
            }
            catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
            {
                formatted = MessageBodyFormatter.Format(Convert.ToBase64String(body), isBase64: true, null, truncated);
            }
        }
        else if ((bodyFormat == MqFormat.String || IsJmsText(parsed)) && Ccsids.GetEncoding(parsed.BodyCcsid) is { } text)
        {
            formatted = MessageBodyFormatter.Format(text.GetString(body), isBase64: false, null, truncated);
        }
        else
        {
            formatted = MessageBodyFormatter.Format(Convert.ToBase64String(body), isBase64: true, null, truncated);
        }

        var mqmd = new List<MqProperty>
        {
            new("Format", m.Format.Trim().Length == 0 ? "(none)" : m.Format.Trim(), null),
            new("CCSID", Ccsids.Describe(m.Ccsid), null),
            new("Encoding", $"{m.Encoding} ({(MqHeaderChain.IsBigEndian(m.Encoding) ? "big-endian" : "little-endian")} integers)", null),
            new("Persistence", m.Persistence switch { 0 => "not persistent", 1 => "persistent", _ => "as queue default" }, null),
            new("Priority", m.Priority.ToString(), null),
            new("Message type", m.MessageType switch { 1 => "request", 2 => "reply", 4 => "report", 8 => "datagram", var t => t.ToString() }, null),
            new("Put by", $"{m.PutApplicationName} (type {m.PutApplicationType})", null),
            new("Put at", m.PutTime.ToString("O"), null),
            new("User", m.UserId, null),
            new("Reply to", m.ReplyToQueue.Length > 0 ? $"{m.ReplyToQueue}@{m.ReplyToQueueManager}" : null, null),
            new("Backout count", m.BackoutCount.ToString(), null),
            new("Expiry", m.Expiry < 0 ? "unlimited" : $"{m.Expiry / 10.0:0.#} s", null),
            new("Feedback", m.Feedback == 0 ? null : MqReasons.Describe(m.Feedback), null),
            new("Length", m.Length.ToString(), null),
        };
        // RFH2 properties also feed the generic Headers tab (folder.name = value).
        var headers = parsed.Headers.SelectMany(h => h.Folders).SelectMany(f => f.Properties.Select(p => (Key: $"{f.Name}.{p.Name}", p.Value)))
            .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => JsonSerializer.SerializeToElement(g.First().Value));
        var properties = mqmd.Where(p => p.Value is not null).ToDictionary(p => p.Name, p => JsonSerializer.SerializeToElement(p.Value));

        var view = new MqMessageView(Convert.ToHexString(m.MessageId), Printable(m.MessageId), Convert.ToHexString(m.CorrelationId), Printable(m.CorrelationId),
            mqmd.Where(p => p.Value is not null).ToList(), parsed.Headers, bodyFormat.Trim().Length == 0 ? "(none)" : bodyFormat.Trim(),
            Ccsids.Describe(parsed.BodyCcsid), parsed.Warning, pcf,
            formatted.Base64 is null ? Convert.ToBase64String(body[..Math.Min(body.Length, RawBodyPreviewBytes)]) : null);
        return new BrowsedMessage(index, "mq:" + Convert.ToHexString(m.MessageId), m.BackoutCount > 0, "", "", m.Length, properties, headers,
            ContentTypeOf(parsed), formatted, null, IbmMq: view, SentAt: m.PutTime);
    }

    private static DateTimeOffset? SentAt(NServiceBusMessageInfo? nsb, long? amqpTimestamp) =>
        nsb?.TimeSent ?? (amqpTimestamp is > 0 and var t ? DateTimeOffset.FromUnixTimeSeconds(t) : null);

    private static bool IsJmsText(MqParsedMessage parsed) =>
        parsed.Headers.SelectMany(h => h.Folders).Where(f => f.Name == "mcd").SelectMany(f => f.Properties).Any(p => p is { Name: "Msd", Value: "jms_text" });

    private static string? ContentTypeOf(MqParsedMessage parsed) =>
        parsed.Headers.SelectMany(h => h.Folders).Where(f => f.Name == "mcd").SelectMany(f => f.Properties).FirstOrDefault(p => p.Name == "Msd")?.Value;

    /// <summary>MsgId/CorrelId as text when they are printable (applications often put readable ids there).</summary>
    private static string? Printable(byte[] id)
    {
        var trimmed = id.AsSpan().TrimEnd((byte)0);
        if (trimmed.IsEmpty)
            return null;
        foreach (var b in trimmed)
            if (b is < 0x20 or > 0x7E)
                return null;
        return Encoding.ASCII.GetString(trimmed).TrimEnd();
    }

    /// <summary>Basic properties with the management API's snake_case names, so both sources look the same.</summary>
    private static Dictionary<string, JsonElement> DisplayProperties(StoredProperties p)
    {
        var values = new Dictionary<string, object?>
        {
            ["content_type"] = p.ContentType,
            ["content_encoding"] = p.ContentEncoding,
            ["delivery_mode"] = p.DeliveryMode,
            ["priority"] = p.Priority,
            ["correlation_id"] = p.CorrelationId,
            ["reply_to"] = p.ReplyTo,
            ["expiration"] = p.Expiration,
            ["message_id"] = p.MessageId,
            ["timestamp"] = p.Timestamp,
            ["type"] = p.Type,
            ["user_id"] = p.UserId,
            ["app_id"] = p.AppId,
            ["cluster_id"] = p.ClusterId,
        };
        return values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => JsonSerializer.SerializeToElement(v.Value));
    }

    private static string? HeaderToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };
}
