using System.Text.Json;

namespace DynaQueueExplorer.RabbitMq.Amqp;

/// <summary>
/// AMQP 0-9-1 basic properties in a serializable form. Header values keep their AMQP type
/// (see <see cref="AmqpValueCodec"/>) so a message can be stored, exported and republished losslessly.
/// </summary>
public sealed record StoredProperties
{
    public string? ContentType { get; init; }
    public string? ContentEncoding { get; init; }
    public byte? DeliveryMode { get; init; }
    public byte? Priority { get; init; }
    public string? CorrelationId { get; init; }
    public string? ReplyTo { get; init; }
    public string? Expiration { get; init; }
    public string? MessageId { get; init; }
    public long? Timestamp { get; init; }
    public string? Type { get; init; }
    public string? UserId { get; init; }
    public string? AppId { get; init; }
    public string? ClusterId { get; init; }
    public Dictionary<string, JsonElement>? Headers { get; init; }
}

/// <summary>A message with its exact body bytes, as read from or written to a queue.</summary>
public sealed record RawMessage(string Exchange, string RoutingKey, bool Redelivered, StoredProperties Properties, byte[] Body);
