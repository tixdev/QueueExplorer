using System.Text;
using System.Text.Json;
using DynaQueueExplorer.Core.Messages;
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
        return new BrowsedMessage(index, key, message.Redelivered, message.Exchange, message.RoutingKey, message.PayloadBytes,
            basicProperties, headers, contentType, body, nsb);
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
            stagedId, discarded, lastPublishedAt, lastPublishedTo, broker, editedAt, editedBy);
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
