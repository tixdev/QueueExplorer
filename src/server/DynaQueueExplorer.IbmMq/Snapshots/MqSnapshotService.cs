using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using DynaQueueExplorer.IbmMq.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.IbmMq.Snapshots;

/// <summary>
/// In-memory snapshot of every IBM MQ queue manager, polled only while someone uses it, refreshed before answering
/// when stale, with concurrent refreshes coalesced (same behaviour as the RabbitMQ snapshots).
/// </summary>
public sealed class MqSnapshotService(
    IbmMqConnections connections,
    IOptions<IbmMqOptions> options,
    TimeProvider time,
    ILogger<MqSnapshotService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public bool Exists(string cluster) => connections.Exists(cluster);

    public (DateTimeOffset? LastErrorAt, string? LastError) GetHealth(string cluster)
    {
        var s = GetState(cluster);
        return (s.LastErrorAt, s.LastError);
    }

    public async Task<MqSnapshot> GetSnapshotAsync(string cluster, CancellationToken ct)
    {
        var state = GetState(cluster);
        var wasIdle = time.GetUtcNow() - state.LastAccess > options.Value.IdleAfter;
        state.LastAccess = time.GetUtcNow();
        if (wasIdle)
            state.Wake();

        var snapshot = state.Snapshot;
        if (snapshot is not null && time.GetUtcNow() - snapshot.FetchedAt <= options.Value.ActiveRefreshInterval * 2)
            return snapshot;
        try
        {
            return await RefreshAsync(cluster, ct);
        }
        catch (Exception) when (snapshot is not null && !ct.IsCancellationRequested)
        {
            return snapshot;
        }
    }

    public Task<MqSnapshot> RefreshAsync(string cluster, CancellationToken ct)
    {
        var state = GetState(cluster);
        Task<MqSnapshot> refresh;
        lock (state)
        {
            if (state.InFlight is { } running)
            {
                refresh = running;
            }
            else
            {
                refresh = RefreshCoreAsync(state);
                if (!refresh.IsCompleted)
                    state.InFlight = refresh;
            }
        }
        return refresh.WaitAsync(ct);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(connections.QueueManagers.Select(c => LoopAsync(GetState(c.Name), stoppingToken)));

    private async Task LoopAsync(State state, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var active = time.GetUtcNow() - state.LastAccess <= options.Value.IdleAfter;
            if (active)
            {
                try
                {
                    await RefreshAsync(state.Cluster, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Recorded on the state.
                }
            }
            await state.WaitAsync(active ? options.Value.ActiveRefreshInterval : Timeout.InfiniteTimeSpan, time, stoppingToken);
        }
    }

    private async Task<MqSnapshot> RefreshCoreAsync(State state)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = cts.Token;
            var limitations = new List<string>();

            if (state.QueueManager is null || time.GetUtcNow() - state.QueueManagerReadAt >= options.Value.StructureRefreshInterval)
            {
                var (info, limitation) = await MqInquiry.QueueManagerAsync(connections, state.Cluster, options.Value, ct);
                state.QueueManager = info;
                state.QueueManagerLimitation = limitation;
                state.QueueManagerReadAt = time.GetUtcNow();
            }
            if (state.QueueManagerLimitation is { } qmLimitation)
                limitations.Add(qmLimitation);

            var (queues, pcfLimitations) = await MqInquiry.QueuesByPcfAsync(connections, state.Cluster, options.Value, ct);
            limitations.AddRange(pcfLimitations);
            if (queues is null)
            {
                var (fallback, fallbackLimitations) = await MqInquiry.QueuesByInquireAsync(connections, state.Cluster, connections.Options(state.Cluster).Queues, ct);
                queues = fallback;
                limitations.AddRange(fallbackLimitations);
            }

            var snapshot = new MqSnapshot(state.Cluster, time.GetUtcNow(), Stopwatch.GetElapsedTime(started),
                XxHash3.HashToUInt64(JsonSerializer.SerializeToUtf8Bytes(queues)), state.QueueManager!, queues, limitations);
            state.Snapshot = snapshot;
            state.LastError = null;
            logger.LogDebug("Refreshed queue manager {Cluster} in {Elapsed} ms ({Queues} queues)", state.Cluster,
                (int)snapshot.FetchDuration.TotalMilliseconds, queues.Count);
            return snapshot;
        }
        catch (Exception e)
        {
            state.LastError = e.Message;
            state.LastErrorAt = time.GetUtcNow();
            logger.LogWarning("Refresh of queue manager {Cluster} failed: {Error}", state.Cluster, e.Message);
            throw;
        }
        finally
        {
            lock (state)
                state.InFlight = null;
        }
    }

    private State GetState(string cluster) =>
        connections.Exists(cluster) ? _states.GetOrAdd(cluster, n => new State(n)) : throw new KeyNotFoundException($"Unknown queue manager '{cluster}'.");

    private sealed class State(string cluster)
    {
        private readonly SemaphoreSlim _wake = new(0, 1);
        public string Cluster { get; } = cluster;
        public volatile MqSnapshot? Snapshot;
        public Task<MqSnapshot>? InFlight;
        public MqQueueManagerInfo? QueueManager;
        public string? QueueManagerLimitation;
        public DateTimeOffset QueueManagerReadAt;
        public DateTimeOffset LastAccess = DateTimeOffset.MinValue;
        public string? LastError;
        public DateTimeOffset? LastErrorAt;

        public void Wake()
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }

        public async Task WaitAsync(TimeSpan interval, TimeProvider time, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wake = _wake.WaitAsync(cts.Token);
            var delay = Task.Delay(interval, time, cts.Token);
            await Task.WhenAny(wake, delay);
            await cts.CancelAsync();
            try { await Task.WhenAll(wake, delay); } catch (OperationCanceledException) { }
        }
    }
}
