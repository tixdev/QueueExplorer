using System.Collections.Concurrent;
using System.Security.Authentication;
using DynaQueueExplorer.Core.Clusters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DynaQueueExplorer.RabbitMq.Amqp;

/// <summary>
/// Long-lived AMQP connections, one per cluster and vhost (an AMQP connection is bound to a vhost), opened on
/// first use, shared by every operation and closed when idle. Operations open short-lived channels on them.
/// </summary>
public sealed class AmqpConnections(
    IOptions<ClustersOptions> clusters,
    IOptions<RabbitMqOptions> options,
    TimeProvider time,
    IHostEnvironment environment,
    ILogger<AmqpConnections> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<(string Cluster, string Vhost), Entry> _entries = new();

    public async Task<IConnection> GetAsync(string cluster, string vhost, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd((cluster, vhost), _ => new Entry());
        entry.LastUsed = time.GetUtcNow();
        if (entry.Connection is { IsOpen: true } open)
            return open;

        await entry.Lock.WaitAsync(ct);
        try
        {
            if (entry.Connection is { IsOpen: true } existing)
                return existing;
            if (entry.Connection is not null)
                await entry.Connection.DisposeAsync();

            var config = clusters.Value.Items.FirstOrDefault(c => c.Name == cluster)
                         ?? throw new KeyNotFoundException($"Unknown cluster '{cluster}'.");
            var uri = config.AmqpUrl ?? throw new InvalidOperationException($"Cluster '{cluster}' has no AmqpUrl configured.");
            var factory = new ConnectionFactory
            {
                Uri = uri,
                UserName = config.Username,
                Password = config.Password,
                VirtualHost = vhost,
                ClientProvidedName = $"DynaQueueExplorer ({vhost})",
                AutomaticRecoveryEnabled = true,
            };
            // amqps:// enables TLS with the host as server name; add the cluster's CA validation on top.
            if (factory.Ssl.Enabled)
            {
                // Let the OS negotiate the best version (TLS 1.3 where available) instead of the client's TLS 1.2 default.
                factory.Ssl.Version = SslProtocols.None;
                if (ClusterTls.CreateValidator(config, environment.ContentRootPath) is { } validator)
                    factory.Ssl.CertificateValidationCallback = validator;
            }
            entry.Connection = await factory.CreateConnectionAsync(ct);
            logger.LogInformation("Opened AMQP connection to {Cluster}/{Vhost}", cluster, vhost);
            return entry.Connection;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await CloseIdleAsync(time.GetUtcNow() - options.Value.AmqpIdleTimeout);
        }
        catch (OperationCanceledException)
        {
        }
        await CloseIdleAsync(DateTimeOffset.MaxValue);
    }

    private async Task CloseIdleAsync(DateTimeOffset usedBefore)
    {
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastUsed >= usedBefore || entry.Connection is null || !await entry.Lock.WaitAsync(0))
                continue;
            try
            {
                if (entry.LastUsed < usedBefore && entry.Connection is { } connection)
                {
                    entry.Connection = null;
                    await connection.CloseAsync();
                    await connection.DisposeAsync();
                    logger.LogInformation("Closed idle AMQP connection to {Cluster}/{Vhost}", key.Cluster, key.Vhost);
                }
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Error while closing AMQP connection to {Cluster}/{Vhost}", key.Cluster, key.Vhost);
            }
            finally
            {
                entry.Lock.Release();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public IConnection? Connection;
        public DateTimeOffset LastUsed;
    }
}
