using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynaQueueExplorer.RabbitMq.Management;

/// <summary>RabbitMQ reports a quorum queue's delivery limit as a number or as the string "unlimited" (mapped to -1).</summary>
public sealed class DeliveryLimitConverter : JsonConverter<int?>
{
    public const int Unlimited = -1;

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number => reader.GetInt32(),
        JsonTokenType.String when string.Equals(reader.GetString(), "unlimited", StringComparison.OrdinalIgnoreCase) => Unlimited,
        JsonTokenType.Null => null,
        _ => throw new JsonException($"Unexpected delivery limit token {reader.TokenType}."),
    };

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteNumberValue(value.Value);
    }
}
