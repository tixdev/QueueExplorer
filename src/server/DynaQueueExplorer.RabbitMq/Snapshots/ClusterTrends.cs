using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using DynaQueueExplorer.Core.Clusters;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.RabbitMq.Management;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.RabbitMq.Snapshots;

public sealed record TrendSample(DateTimeOffset At, long Messages, long Ready, long Unacked, long ErrorMessages)
{
    /// <summary>The same totals restricted to each queue group, by group name; served one group at a time.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, TrendSample>? Groups { get; init; }
}

/// <summary>
/// Samples message totals and error-queue depths of every cluster at a fixed interval and keeps them in memory for the
/// overview trend. A fresh snapshot is reused when there is one; otherwise two small, filtered calls are made, so the
/// history keeps going while nobody uses the explorer. History starts when the API starts.
/// </summary>
public sealed class ClusterTrends(
    ClusterClients clients,
    ClusterSnapshotService snapshots,
    IOptions<RabbitMqOptions> options,
    IOptions<QueueGroupsOptions> groups,
    TimeProvider time,
    ILogger<ClusterTrends> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, List<TrendSample>> _samples = new(StringComparer.Ordinal);

    /// <param name="group">A queue group: only the totals of its queues (samples taken before it existed are skipped).</param>
    public IReadOnlyList<TrendSample> Get(string cluster, TimeSpan window, string? group = null)
    {
        if (!_samples.TryGetValue(cluster, out var samples))
            return [];
        var since = time.GetUtcNow() - window;
        lock (samples)
        {
            var recent = samples.Where(s => s.At >= since);
            return group is null
                ? recent.ToList()
                : recent.Select(s => s.Groups?.GetValueOrDefault(group)).OfType<TrendSample>().ToList();
        }
    }

    public TimeSpan? Interval => options.Value.TrendSampleInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.TrendSampleInterval is not { } interval || interval <= TimeSpan.Zero)
            return;

        // One loop per cluster: a slow or unreachable cluster never delays the others.
        await Task.WhenAll(clients.Clusters.Select(c => RunAsync(c.Name, interval, stoppingToken)));
    }

    private async Task RunAsync(string cluster, TimeSpan interval, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        do
        {
            await SampleAsync(cluster, interval, stoppingToken);
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SampleAsync(string cluster, TimeSpan interval, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var source = "snapshot";
        try
        {
            var now = time.GetUtcNow();
            var queueGroups = groups.Value.Items;
            TrendSample sample;
            if (snapshots.Peek(cluster) is { } snapshot && now - snapshot.FetchedAt <= interval && snapshot.Overview.QueueTotals is { } totals)
            {
                sample = new TrendSample(now, totals.Messages ?? 0, totals.MessagesReady ?? 0, totals.MessagesUnacknowledged ?? 0,
                    snapshot.Queues.Where(q => QueueNaming.IsErrorQueue(q.Name)).Sum(q => q.Messages ?? 0))
                {
                    Groups = GroupTotals(now, cluster, queueGroups, snapshot.Queues),
                };
            }
            else if (queueGroups.Count > 0)
            {
                // Per-group totals need every queue's depth: one call with five columns instead of overview + error queues.
                source = "management API";
                var queues = await clients.Get(cluster).GetQueueDepthsAsync(ct);
                sample = Totals(now, queues) with { Groups = GroupTotals(now, cluster, queueGroups, queues) };
            }
            else
            {
                source = "management API";
                var client = clients.Get(cluster);
                var overview = client.GetOverviewAsync(ct);
                var errors = client.GetErrorQueueDepthsAsync(ct);
                await Task.WhenAll(overview, errors);
                var t = overview.Result.QueueTotals;
                sample = new TrendSample(now, t?.Messages ?? 0, t?.MessagesReady ?? 0, t?.MessagesUnacknowledged ?? 0,
                    errors.Result.Sum(q => q.Messages ?? 0));
            }

            var samples = _samples.GetOrAdd(cluster, _ => []);
            lock (samples)
            {
                samples.Add(sample);
                var oldest = now - options.Value.TrendRetention;
                var expired = samples.FindIndex(s => s.At >= oldest);
                if (expired > 0)
                    samples.RemoveRange(0, expired);
            }
            logger.LogDebug("Trend sample of {Cluster} from {Source} in {Elapsed} ms", cluster, source,
                (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // A missing point is fine: the chart shows the gap.
            logger.LogWarning("Trend sample of {Cluster} from {Source} failed after {Elapsed} ms: {Error}", cluster, source,
                (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, e.Message);
        }
    }

    private static TrendSample Totals(DateTimeOffset at, IEnumerable<RmqQueue> queues)
    {
        long messages = 0, ready = 0, unacked = 0, errors = 0;
        foreach (var q in queues)
        {
            messages += q.Messages ?? 0;
            ready += q.MessagesReady ?? 0;
            unacked += q.MessagesUnacknowledged ?? 0;
            if (QueueNaming.IsErrorQueue(q.Name))
                errors += q.Messages ?? 0;
        }
        return new TrendSample(at, messages, ready, unacked, errors);
    }

    private static Dictionary<string, TrendSample>? GroupTotals(DateTimeOffset at, string cluster, List<QueueGroup> groups, IReadOnlyList<RmqQueue> queues) =>
        groups.Count == 0
            ? null
            : groups.ToDictionary(
                g => g.Name,
                g => Totals(at, queues.Where(q => g.Contains(BrokerTypes.RabbitMq, cluster, q.Vhost, q.Name))),
                StringComparer.Ordinal);
}
