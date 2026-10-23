using System.Text.Json;
using RabbitMQ.Client;

namespace DynaQueueExplorer.RabbitMq.Amqp;

public static class AmqpProperties
{
    /// <summary>
    /// Headers the broker maintains to describe a message's history in its current queue. They are dropped when
    /// a message is republished: copied over, they would describe deliveries that never happened to the new copy.
    /// </summary>
    public static bool IsBrokerManagedHeader(string key) =>
        key is "x-delivery-count" or "x-death" or "x-received-from"
        || key.StartsWith("x-first-death-", StringComparison.Ordinal)
        || key.StartsWith("x-last-death-", StringComparison.Ordinal);

    public static StoredProperties FromAmqp(IReadOnlyBasicProperties p) => new()
    {
        ContentType = p.IsContentTypePresent() ? p.ContentType : null,
        ContentEncoding = p.IsContentEncodingPresent() ? p.ContentEncoding : null,
        DeliveryMode = p.IsDeliveryModePresent() ? (byte)p.DeliveryMode : null,
        Priority = p.IsPriorityPresent() ? p.Priority : null,
        CorrelationId = p.IsCorrelationIdPresent() ? p.CorrelationId : null,
        ReplyTo = p.IsReplyToPresent() ? p.ReplyTo : null,
        Expiration = p.IsExpirationPresent() ? p.Expiration : null,
        MessageId = p.IsMessageIdPresent() ? p.MessageId : null,
        Timestamp = p.IsTimestampPresent() ? p.Timestamp.UnixTime : null,
        Type = p.IsTypePresent() ? p.Type : null,
        UserId = p.IsUserIdPresent() ? p.UserId : null,
        AppId = p.IsAppIdPresent() ? p.AppId : null,
        ClusterId = p.IsClusterIdPresent() ? p.ClusterId : null,
        Headers = p.IsHeadersPresent() && p.Headers is not null ? AmqpValueCodec.EncodeTable(p.Headers) : null,
    };

    /// <param name="dropHeader">Extra headers to drop on top of the broker-managed ones.</param>
    public static BasicProperties ToAmqp(StoredProperties p, Func<string, bool>? dropHeader = null)
    {
        var props = new BasicProperties();
        if (p.ContentType is not null) props.ContentType = p.ContentType;
        if (p.ContentEncoding is not null) props.ContentEncoding = p.ContentEncoding;
        if (p.DeliveryMode is { } mode) props.DeliveryMode = (DeliveryModes)mode;
        if (p.Priority is { } priority) props.Priority = priority;
        if (p.CorrelationId is not null) props.CorrelationId = p.CorrelationId;
        if (p.ReplyTo is not null) props.ReplyTo = p.ReplyTo;
        if (p.Expiration is not null) props.Expiration = p.Expiration;
        if (p.MessageId is not null) props.MessageId = p.MessageId;
        if (p.Timestamp is { } ts) props.Timestamp = new AmqpTimestamp(ts);
        if (p.Type is not null) props.Type = p.Type;
        if (p.UserId is not null) props.UserId = p.UserId;
        if (p.AppId is not null) props.AppId = p.AppId;
        if (p.ClusterId is not null) props.ClusterId = p.ClusterId;
        if (p.Headers is not null)
        {
            props.Headers = AmqpValueCodec.DecodeTable(p.Headers
                .Where(h => !IsBrokerManagedHeader(h.Key) && dropHeader?.Invoke(h.Key) != true)
                .ToDictionary(h => h.Key, h => h.Value));
        }
        return props;
    }

    /// <summary>Header values as text, keyed by name (input of the NServiceBus decoder).</summary>
    public static Dictionary<string, string?> HeaderText(StoredProperties p) =>
        (p.Headers ?? []).ToDictionary(h => h.Key, h => AmqpValueCodec.ToText(h.Value));

    public static Dictionary<string, JsonElement> HeaderDisplay(StoredProperties p) =>
        (p.Headers ?? []).ToDictionary(h => h.Key, h => AmqpValueCodec.ToDisplay(h.Value));
}
