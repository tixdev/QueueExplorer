using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DynaQueueExplorer.Core.Messages;

public enum BodyFormat
{
    Json,
    Xml,
    Text,
    Binary,
}

/// <param name="Text">Pretty-printed body for JSON/XML, the decoded text otherwise; null for binary payloads.</param>
/// <param name="Base64">Raw payload for binary bodies.</param>
public sealed record FormattedBody(BodyFormat Format, string? Text, string? Base64, bool Truncated);

public static class MessageBodyFormatter
{
    private static readonly JsonWriterOptions IndentedJson = new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="payload">The payload as returned by the broker.</param>
    /// <param name="isBase64">True when the broker could not represent the payload as UTF-8 text.</param>
    /// <param name="contentType">Content type hint (message property or transport header).</param>
    public static FormattedBody Format(string payload, bool isBase64, string? contentType, bool truncated)
    {
        string text;
        if (isBase64)
        {
            var bytes = Convert.FromBase64String(payload);
            if (!TryDecodeUtf8(bytes, out text))
                return new FormattedBody(BodyFormat.Binary, null, payload, truncated);
        }
        else
        {
            text = payload;
        }

        text = text.TrimStart('﻿');
        var trimmed = text.AsSpan().TrimStart();
        var looksJson = trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
        var looksXml = trimmed.Length > 0 && trimmed[0] == '<';

        // A truncated payload is not parseable; show it as text rather than failing.
        if (!truncated && (IsJson(contentType) || looksJson) && TryIndentJson(text, out var json))
            return new FormattedBody(BodyFormat.Json, json, null, truncated);
        if (!truncated && (IsXml(contentType) || looksXml) && TryIndentXml(text, out var xml))
            return new FormattedBody(BodyFormat.Xml, xml, null, truncated);

        return new FormattedBody(BodyFormat.Text, text, null, truncated);
    }

    private static bool IsJson(string? contentType) => contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsXml(string? contentType) => contentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return !text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryIndentJson(string text, out string result)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, IndentedJson))
                document.WriteTo(writer);
            result = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return true;
        }
        catch (JsonException)
        {
            result = string.Empty;
            return false;
        }
    }

    private static bool TryIndentXml(string text, out string result)
    {
        try
        {
            result = XDocument.Parse(text).ToString();
            return true;
        }
        catch (XmlException)
        {
            result = string.Empty;
            return false;
        }
    }
}
