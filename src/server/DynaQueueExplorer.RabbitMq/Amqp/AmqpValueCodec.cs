using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RabbitMQ.Client;

namespace DynaQueueExplorer.RabbitMq.Amqp;

/// <summary>
/// Converts AMQP field-table values to tagged JSON and back, so header types survive a round trip:
/// <c>{"t":"s","v":"text"}</c>. Tags: s (UTF-8 long string), b (binary, base64), i (int32), l (int64),
/// h (int16), y (byte), f (float), d (double), m (decimal), z (bool), ts (timestamp, unix seconds),
/// a (array), o (table), n (null).
/// </summary>
public static class AmqpValueCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public static Dictionary<string, JsonElement> EncodeTable(IDictionary<string, object?> table) =>
        table.ToDictionary(kv => kv.Key, kv => ToElement(Encode(kv.Value)));

    public static Dictionary<string, object?> DecodeTable(IReadOnlyDictionary<string, JsonElement> table) =>
        table.ToDictionary(kv => kv.Key, kv => Decode(kv.Value));

    public static JsonNode Encode(object? value) => value switch
    {
        null => Tag("n", null),
        byte[] bytes => TryUtf8(bytes, out var text) ? Tag("s", text) : Tag("b", Convert.ToBase64String(bytes)),
        string s => Tag("s", s),
        bool b => Tag("z", b),
        int i => Tag("i", i),
        long l => Tag("l", l),
        short h => Tag("h", h),
        byte y => Tag("y", y),
        sbyte sb => Tag("h", sb),
        uint ui => Tag("l", ui),
        ushort us => Tag("i", us),
        float f => Tag("f", f),
        double d => Tag("d", d),
        decimal m => Tag("m", m.ToString(CultureInfo.InvariantCulture)),
        AmqpTimestamp ts => Tag("ts", ts.UnixTime),
        BinaryTableValue bin => Tag("b", Convert.ToBase64String(bin.Bytes)),
        IDictionary<string, object?> table => Tag("o", new JsonObject(table.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)Encode(kv.Value))))),
        IDictionary table => Tag("o", new JsonObject(table.Keys.Cast<object>().Select(k => KeyValuePair.Create(k.ToString()!, (JsonNode?)Encode(table[k]))))),
        IEnumerable list => Tag("a", new JsonArray(list.Cast<object?>().Select(v => (JsonNode?)Encode(v)).ToArray())),
        _ => Tag("s", value.ToString()),
    };

    public static object? Decode(JsonElement tagged)
    {
        var tag = tagged.GetProperty("t").GetString();
        tagged.TryGetProperty("v", out var v);
        return tag switch
        {
            "n" => null,
            "s" => Encoding.UTF8.GetBytes(v.GetString() ?? string.Empty),
            "b" => Convert.FromBase64String(v.GetString() ?? string.Empty),
            "z" => v.GetBoolean(),
            "i" => v.GetInt32(),
            "l" => v.GetInt64(),
            "h" => v.GetInt16(),
            "y" => v.GetByte(),
            "f" => v.GetSingle(),
            "d" => v.GetDouble(),
            "m" => decimal.Parse(v.GetString()!, CultureInfo.InvariantCulture),
            "ts" => new AmqpTimestamp(v.GetInt64()),
            "a" => v.EnumerateArray().Select(Decode).ToList(),
            "o" => v.EnumerateObject().ToDictionary(p => p.Name, p => Decode(p.Value)),
            _ => throw new FormatException($"Unknown AMQP value tag '{tag}'."),
        };
    }

    /// <summary>Plain JSON for display (strings, numbers, nested arrays/objects), as the management API shows headers.</summary>
    public static JsonElement ToDisplay(JsonElement tagged)
    {
        var tag = tagged.GetProperty("t").GetString();
        tagged.TryGetProperty("v", out var v);
        JsonNode? node = tag switch
        {
            "n" => null,
            "a" => new JsonArray(v.EnumerateArray().Select(e => (JsonNode?)JsonNode.Parse(ToDisplay(e).GetRawText())).ToArray()),
            "o" => new JsonObject(v.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, JsonNode.Parse(ToDisplay(p.Value).GetRawText())))),
            _ => JsonNode.Parse(v.GetRawText()),
        };
        return ToElement(node);
    }

    /// <summary>Header value as text, the form the NServiceBus decoder works with.</summary>
    public static string? ToText(JsonElement tagged)
    {
        var display = ToDisplay(tagged);
        return display.ValueKind switch
        {
            JsonValueKind.String => display.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => display.GetRawText(),
        };
    }

    /// <summary>
    /// The tagged value for an edited header given as text: unchanged text keeps the original value untouched,
    /// changed text keeps the original AMQP type when it parses as such (binary values are edited as base64),
    /// new headers become strings. Arrays and tables can only be kept as they are.
    /// </summary>
    public static JsonElement FromEditedText(JsonElement? original, string? text)
    {
        if (original is { } existing && ToText(existing) == text)
            return existing;
        if (text is null)
            return ToElement(Tag("n", null));

        var tag = original?.GetProperty("t").GetString() ?? "s";
        var inv = CultureInfo.InvariantCulture;
        JsonNode node = tag switch
        {
            "s" or "n" => Tag("s", text),
            "b" => Tag("b", Convert.ToBase64String(Convert.FromBase64String(text))),
            "z" => Tag("z", bool.Parse(text)),
            "i" => Tag("i", int.Parse(text, inv)),
            "l" => Tag("l", long.Parse(text, inv)),
            "h" => Tag("h", short.Parse(text, inv)),
            "y" => Tag("y", byte.Parse(text, inv)),
            "f" => Tag("f", float.Parse(text, inv)),
            "d" => Tag("d", double.Parse(text, inv)),
            "m" => Tag("m", decimal.Parse(text, inv).ToString(inv)),
            "ts" => Tag("ts", long.Parse(text, inv)),
            _ => throw new FormatException("Array and table headers cannot be edited as text."),
        };
        return ToElement(node);
    }

    private static JsonObject Tag(string tag, JsonNode? value) => new() { ["t"] = tag, ["v"] = value };

    private static JsonElement ToElement(JsonNode? node) =>
        node is null ? JsonDocument.Parse("null").RootElement.Clone() : JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private static bool TryUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }
}
