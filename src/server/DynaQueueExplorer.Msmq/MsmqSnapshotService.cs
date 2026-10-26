using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text.Json;
using DynaQueueExplorer.Core.Msmq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.Msmq;

/// <param name="Version">Hash of the queues: unchanged data keeps the same value (used as ETag).</param>
public sealed record MsmqSnapshot(
    string Cluster,
    string Machine,
    DateTimeOffset FetchedAt,
    TimeSpan FetchDuration,
    ulong Version,
    string DepthSource,
    IReadOnlyList<MsmqQueueStatus> Queues,
    IReadOnlyList<string> Limitations);

/// <summary>
/// In-memory snapshot of every MSMQ machine, polled while someone uses it. Reading a machine through the agent takes
/// seconds, so a stale snapshot is answered at once and refreshed in the background (the UI shows its age); only the
/// very first read is waited for. Concurrent refreshes are coalesced.
/// </summary>
public sealed class MsmqSnapshotService(
    MsmqAgentClients agents,
    IOptions<MsmqOptions> options,
    TimeProvider time,
    ILogger<MsmqSnapshotService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public bool Exists(string cluster) => agents.Exists(cluster);

    public (DateTimeOffset? LastErrorAt, string? LastError) GetHealth(string cluster)
    {
        var s = GetState(cluster);
        return (s.LastErrorAt, s.LastError);
    }

    /// <summary>
    /// The current snapshot without waiting for the agent (null when the machine has not been read yet); counts as a
    /// use of the machine, so polling starts or continues.
    /// </summary>
    public MsmqSnapshot? Peek(string cluster)
    {
        var state = GetState(cluster);
        var wasIdle = time.GetUtcNow() - state.LastAccess > options.Value.IdleAfter;
        state.LastAccess = time.GetUtcNow();
        if (wasIdle || state.Snapshot is null)
            state.Wake();
        return state.Snapshot;
    }

    public async Task<MsmqSnapshot> GetSnapshotAsync(string cluster, CancellationToken ct)
    {
        var state = GetState(cluster);
        var wasIdle = time.GetUtcNow() - state.LastAccess > options.Value.IdleAfter;
        state.LastAccess = time.GetUtcNow();
        if (wasIdle)
            state.Wake();

        if (state.Snapshot is { } snapshot)
        {
            if (time.GetUtcNow() - snapshot.FetchedAt > options.Value.ActiveRefreshInterval * 2)
                _ = RefreshAsync(cluster, CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
            return snapshot;
        }
        return await RefreshAsync(cluster, ct);
    }

    public Task<MsmqSnapshot> RefreshAsync(string cluster, CancellationToken ct)
    {
        var state = GetState(cluster);
        Task<MsmqSnapshot> refresh;
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
        Task.WhenAll(agents.Machines.Select(c => LoopAsync(GetState(c.Name), stoppingToken)));

    private async Task LoopAsync(State state, CancellationToken stoppingToken)
    {
        if (options.Value.WarmUpOnStart)
            state.LastAccess = time.GetUtcNow();
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

    private async Task<MsmqSnapshot> RefreshCoreAsync(State state)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            using var cts = new CancellationTokenSource(options.Value.RequestTimeout);
            var list = await agents.ListQueuesAsync(state.Cluster, cts.Token);
            var queues = list.Queues.OrderBy(q => q.Kind == MsmqQueueKinds.Private ? 0 : q.Kind == MsmqQueueKinds.DeadLetter ? 1 : 2)
                .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var snapshot = new MsmqSnapshot(state.Cluster, agents.MachineLabel(state.Cluster), time.GetUtcNow(), Stopwatch.GetElapsedTime(started),
                XxHash3.HashToUInt64(JsonSerializer.SerializeToUtf8Bytes(queues)), list.DepthSource, queues, list.Limitations);
            state.Snapshot = snapshot;
            state.LastError = null;
            logger.LogDebug("Refreshed MSMQ machine {Cluster} in {Elapsed} ms ({Queues} queues, depths from {Source})", state.Cluster,
                (int)snapshot.FetchDuration.TotalMilliseconds, queues.Count, list.DepthSource);
            return snapshot;
        }
        catch (Exception e)
        {
            state.LastError = e is TaskCanceledException ? $"The MSMQ agent did not answer within {options.Value.RequestTimeout.TotalSeconds:0} s." : e.Message;
            state.LastErrorAt = time.GetUtcNow();
            logger.LogWarning("Refresh of MSMQ machine {Cluster} failed: {Error}", state.Cluster, state.LastError);
            throw;
        }
        finally
        {
            lock (state)
                state.InFlight = null;
        }
    }

    private State GetState(string cluster) =>
        agents.Exists(cluster) ? _states.GetOrAdd(cluster, n => new State(n)) : throw new KeyNotFoundException($"Unknown MSMQ connection '{cluster}'.");

    private sealed class State(string cluster)
    {
        private readonly SemaphoreSlim _wake = new(0, 1);
        public string Cluster { get; } = cluster;
        public volatile MsmqSnapshot? Snapshot;
        public Task<MsmqSnapshot>? InFlight;
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
