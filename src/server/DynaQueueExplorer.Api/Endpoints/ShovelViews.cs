using System.Text.Json;
using DynaQueueExplorer.Core.Flows;
using DynaQueueExplorer.RabbitMq.Management;
using DynaQueueExplorer.RabbitMq.Snapshots;

namespace DynaQueueExplorer.Api.Endpoints;

/// <summary>
/// A shovel as known from its status and its definition: a shovel that cannot connect has no status, so every field
/// falls back to the definition. <see cref="Vhost"/> is empty for static shovels.
/// </summary>
public sealed record MergedShovel(string Name, string Vhost, RmqShovelStatus? Status, JsonElement? Definition)
{
    public string State => Status?.State ?? "not running";
    public string? SrcUri => Status?.SrcUri ?? ShovelViews.Field(Definition, "src-uri");
    public string? DestUri => Status?.DestUri ?? ShovelViews.Field(Definition, "dest-uri");
    public string? SrcQueue => Status?.SrcQueue ?? ShovelViews.Field(Definition, "src-queue");
    public string? SrcExchange => Status?.SrcExchange ?? ShovelViews.Field(Definition, "src-exchange");
    public string? SrcExchangeKey => Status?.SrcExchangeKey ?? ShovelViews.Field(Definition, "src-exchange-key");
    public string? DestQueue => Status?.DestQueue ?? ShovelViews.Field(Definition, "dest-queue");
    public string? DestExchange => Status?.DestExchange ?? ShovelViews.Field(Definition, "dest-exchange");
    public string? DestExchangeKey => Status?.DestExchangeKey ?? ShovelViews.Field(Definition, "dest-exchange-key");
}

public static class ShovelViews
{
    public static IEnumerable<MergedShovel> Merge(ClusterSnapshot snapshot)
    {
        var definitions = snapshot.ShovelDefinitions.ToDictionary(d => (d.Vhost, d.Name), d => d.Value);
        var statuses = snapshot.ShovelStatuses.ToDictionary(s => (s.Vhost ?? string.Empty, s.Name));
        return statuses.Keys.Union(definitions.Keys)
            .Select(k => new MergedShovel(k.Item2, k.Item1, statuses.GetValueOrDefault(k),
                definitions.TryGetValue(k, out var d) ? d : null));
    }

    /// <summary>The shovel as an edge of the migration flows; null ends when the definition is incomplete.</summary>
    public static FlowShovel ToFlow(MergedShovel s) =>
        new(s.Name, s.Vhost.Length > 0 ? s.Vhost : null, s.State,
            FlowEndpointOf(s.SrcUri, s.SrcQueue, s.SrcExchange, s.SrcExchangeKey, s.Vhost),
            FlowEndpointOf(s.DestUri, s.DestQueue, s.DestExchange, s.DestExchangeKey, s.Vhost));

    /// <summary>
    /// amqp(s)://[user[:password]@]host[:port][/vhost]: the vhost is URL-encoded; without a path it is "/". "amqp://" (no
    /// host) is the local broker, in the shovel's own vhost.
    /// </summary>
    internal static FlowEndpoint? FlowEndpointOf(string? uri, string? queue, string? exchange, string? key, string shovelVhost)
    {
        if (queue is null && exchange is null)
            return null;
        string? host = null;
        var vhost = shovelVhost.Length > 0 ? shovelVhost : "/";
        if (uri is not null)
        {
            var rest = uri[(uri.IndexOf("://", StringComparison.Ordinal) is var i and >= 0 ? i + 3 : 0)..];
            var slash = rest.IndexOf('/');
            var authority = slash >= 0 ? rest[..slash] : rest;
            var path = slash >= 0 ? rest[(slash + 1)..] : null;
            var query = path?.IndexOf('?') ?? -1;
            if (query >= 0)
                path = path![..query];
            var at = authority.LastIndexOf('@');
            var hostPort = at >= 0 ? authority[(at + 1)..] : authority;
            var colon = hostPort.LastIndexOf(':');
            host = (colon > 0 && !hostPort.EndsWith(']') ? hostPort[..colon] : hostPort) is { Length: > 0 } h ? h : null;
            if (host is not null)
                vhost = path is null ? "/" : Uri.UnescapeDataString(path);
        }
        return queue is not null
            ? new FlowEndpoint(host, vhost, "queue", queue, key)
            : new FlowEndpoint(host, vhost, "exchange", exchange!, key);
    }

    /// <summary>A string field of a shovel definition; URI fields may also be arrays, of which the first is used.</summary>
    public static string? Field(JsonElement? definition, string name)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array when value.GetArrayLength() > 0 => value[0].GetString(),
            _ => null,
        };
    }
}
