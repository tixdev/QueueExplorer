using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynaQueueExplorer.RabbitMq.Management;

/// <summary>Shovel URIs embed credentials; passwords must never reach the browser.</summary>
public static partial class CredentialMasking
{
    [GeneratedRegex(@"(?<scheme>amqps?://)(?<user>[^:@/]*):(?<password>[^@/]*)@")]
    private static partial Regex UriCredentials();

    public static string? MaskUri(string? uri) => uri is null ? null : UriCredentials().Replace(uri, "${scheme}${user}:***@");

    public static JsonElement MaskJson(JsonElement value)
    {
        using var document = JsonDocument.Parse(UriCredentials().Replace(value.GetRawText(), "${scheme}${user}:***@"));
        return document.RootElement.Clone();
    }
}
