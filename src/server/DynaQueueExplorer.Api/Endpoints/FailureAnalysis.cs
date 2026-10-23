using System.Collections.Concurrent;
using System.Text.Json;
using DynaQueueExplorer.NServiceBus;
using DynaQueueExplorer.RabbitMq;
using DynaQueueExplorer.RabbitMq.Snapshots;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.Api.Endpoints;

/// <param name="Endpoint">The queue the messages failed in (NServiceBus.FailedQ), or null for non-NServiceBus messages.</param>
public sealed record FailureGroup(string? Endpoint, string ExceptionType, int Count, DateTimeOffset? LastFailure, string? SampleMessage, string? Handler);

/// <summary>Result of analysing one error queue; kept until the next analysis of that queue.</summary>
public sealed record QueueFailureAnalysis(
    string Vhost,
    string Queue,
    DateTimeOffset AnalyzedAt,
    string AnalyzedBy,
    long DepthWhenAnalyzed,
    int Analyzed,
    string? Skipped,
    IReadOnlyList<FailureGroup> Groups);

/// <summary>
/// Groups the messages of NServiceBus error queues by failing endpoint and exception. Reading messages requeues them
/// (redelivered flag, delivery count), so it only runs on request, skips queues whose delivery limit could dead-letter
/// messages, reads headers only (payload truncated) and keeps the result for every authorised user.
/// </summary>
public sealed class FailureAnalysis(ClusterClients clients, IOptions<RabbitMqOptions> options)
{
    private const int PayloadBytes = 16;
    private readonly ConcurrentDictionary<(string Cluster, string Vhost, string Queue), QueueFailureAnalysis> _results = new();

    public QueueFailureAnalysis? Get(string cluster, string vhost, string queue) => _results.GetValueOrDefault((cluster, vhost, queue));

    public async Task<QueueFailureAnalysis> AnalyzeAsync(string cluster, string vhost, string queue, string user, TimeProvider time, CancellationToken ct)
    {
        var client = clients.Get(cluster);
        var info = await client.GetQueueBrowseSafetyAsync(vhost, queue, ct);
        var depth = info.Messages ?? 0;

        QueueFailureAnalysis result;
        if (info.HasLowDeliveryLimit)
        {
            result = new QueueFailureAnalysis(vhost, queue, time.GetUtcNow(), user, depth, 0,
                $"Skipped: delivery-limit {info.DeliveryLimit} — reading would bring messages closer to being dead-lettered.", []);
        }
        else if (depth == 0)
        {
            result = new QueueFailureAnalysis(vhost, queue, time.GetUtcNow(), user, 0, 0, null, []);
        }
        else
        {
            var count = (int)Math.Min(depth, options.Value.ErrorAnalysisMaxMessages);
            var messages = await client.GetMessagesAsync(vhost, queue, count, PayloadBytes, ct);
            var groups = messages
                .Select(m => NServiceBusMessageDecoder.TryDecode(HeaderText(m.Properties)))
                .GroupBy(n => (Endpoint: n?.Failure?.FailedQueue, Exception: n?.Failure?.ExceptionType ?? (n is null ? "(not an NServiceBus message)" : "(no exception information)")))
                .Select(g => new FailureGroup(
                    g.Key.Endpoint,
                    g.Key.Exception,
                    g.Count(),
                    g.Max(n => n?.Failure?.TimeOfFailure),
                    g.Select(n => n?.Failure?.Message).FirstOrDefault(m => !string.IsNullOrEmpty(m)),
                    g.Select(n => n?.Failure?.Handler).FirstOrDefault(h => h is not null)))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Endpoint, StringComparer.Ordinal)
                .ToList();
            result = new QueueFailureAnalysis(vhost, queue, time.GetUtcNow(), user, depth, messages.Count, null, groups);
        }

        _results[(cluster, vhost, queue)] = result;
        return result;
    }

    private static Dictionary<string, string?> HeaderText(Dictionary<string, JsonElement>? properties)
    {
        if (properties is null || !properties.TryGetValue("headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
            return [];
        return headers.EnumerateObject().ToDictionary(h => h.Name, h => h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString() : h.Value.GetRawText());
    }
}
