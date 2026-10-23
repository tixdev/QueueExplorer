using System.Collections.Concurrent;
using DynaQueueExplorer.Core.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.RabbitMq.Snapshots;

public sealed record TrendSample(DateTimeOffset At, long Messages, long Ready, long Unacked, long ErrorMessages);

/// <summary>
/// Samples message totals and error-queue depths of every cluster at a fixed interval and keeps them in memory for the
/// overview trend. A fresh snapshot is reused when there is one; otherwise two small, filtered calls are made, so the
/// history keeps going while nobody uses the explorer. History starts when the API starts.
/// </summary>
public sealed class ClusterTrends(
    ClusterClients clients,
    ClusterSnapshotService snapshots,
    IOptions<RabbitMqOptions> options,
    TimeProvider time,
    ILogger<ClusterTrends> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, List<TrendSample>> _samples = new(StringComparer.Ordinal);

    public IReadOnlyList<TrendSample> Get(string cluster, TimeSpan window)
    {
        if (!_samples.TryGetValue(cluster, out var samples))
            return [];
        var since = time.GetUtcNow() - window;
        lock (samples)
            return samples.Where(s => s.At >= since).ToList();
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
            TrendSample sample;
            if (snapshots.Peek(cluster) is { } snapshot && now - snapshot.FetchedAt <= interval && snapshot.Overview.QueueTotals is { } totals)
            {
                sample = new TrendSample(now, totals.Messages ?? 0, totals.MessagesReady ?? 0, totals.MessagesUnacknowledged ?? 0,
                    snapshot.Queues.Where(q => QueueNaming.IsErrorQueue(q.Name)).Sum(q => q.Messages ?? 0));
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
}
