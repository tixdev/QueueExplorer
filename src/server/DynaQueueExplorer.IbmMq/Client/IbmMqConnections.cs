using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DynaQueueExplorer.Core.Clusters;
using IBM.WMQ;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.IbmMq.Client;

/// <summary>
/// One long-lived connection per IBM MQ queue manager, opened on first use and re-opened after the channel breaks.
/// Before connecting over TLS the server certificate is checked by us (chain against the cluster's CA, host name):
/// the IBM .NET client accepts any CA when a client key file is used, so it cannot be relied upon for that.
/// </summary>
public sealed class IbmMqConnections(IOptions<ClustersOptions> clusters, IHostEnvironment environment, ILogger<IbmMqConnections> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, MqConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectLocks = new(StringComparer.Ordinal);

    public IReadOnlyList<ClusterOptions> QueueManagers { get; } = clusters.Value.Items.Where(c => c.Broker == "ibmmq").ToList();

    public bool Exists(string cluster) => QueueManagers.Any(c => c.Name == cluster);

    public ClusterOptions Options(string cluster) =>
        QueueManagers.FirstOrDefault(c => c.Name == cluster) ?? throw new KeyNotFoundException($"Unknown queue manager '{cluster}'.");

    /// <summary>Runs work on the queue manager's connection, connecting (or reconnecting) first when needed.</summary>
    public async Task<T> RunAsync<T>(string cluster, Func<MQQueueManager, T> work, CancellationToken ct)
    {
        var connection = await GetAsync(cluster, ct);
        try
        {
            return await connection.RunAsync(work, ct);
        }
        catch (MQException e)
        {
            Evict(cluster, connection);
            throw IbmMqException.From(e, $"Queue manager '{cluster}'");
        }
    }

    public async Task<PcfResult> PcfAsync(string cluster, IBM.WMQ.PCF.PCFMessage command, TimeSpan timeout, CancellationToken ct)
    {
        var connection = await GetAsync(cluster, ct);
        try
        {
            return await connection.PcfAsync(command, timeout, ct);
        }
        catch (MQException e)
        {
            Evict(cluster, connection);
            throw IbmMqException.From(e, $"PCF {Formats.MqConstants.Command(command.GetCommand())} on '{cluster}'");
        }
    }

    private async Task<MqConnection> GetAsync(string cluster, CancellationToken ct)
    {
        if (_connections.TryGetValue(cluster, out var existing) && !existing.Broken)
            return existing;

        var gate = _connectLocks.GetOrAdd(cluster, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_connections.TryGetValue(cluster, out existing) && !existing.Broken)
                return existing;
            if (existing is not null)
                Evict(cluster, existing);

            var options = Options(cluster);
            if (!string.IsNullOrEmpty(options.CipherSpec))
                await VerifyServerCertificateAsync(options, ct);

            var queueManager = await Task.Run(() =>
            {
                try
                {
                    // The IBM client only accepts fully qualified key file paths.
                    var properties = MqConnectionProperties.For(options);
                    if (!string.IsNullOrEmpty(options.ClientCertificatePath) && !string.IsNullOrEmpty(options.CipherSpec))
                        properties[MQC.SSL_CERT_STORE_PROPERTY] = ResolvePath(options.ClientCertificatePath);
                    return new MQQueueManager(options.QueueManager, properties);
                }
                catch (MQException e)
                {
                    throw IbmMqException.From(e, $"Connecting to {options.QueueManager} on {options.Host}({options.Port}) channel {options.Channel}");
                }
            }, ct);
            var connection = new MqConnection(queueManager);
            _connections[cluster] = connection;
            logger.LogInformation("Connected to IBM MQ queue manager {QueueManager} ({Cluster})", connection.QueueManagerName, cluster);
            return connection;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Our own TLS handshake: same chain and host-name rules as for RabbitMQ; the MQ connection follows only if it passes.</summary>
    private async Task VerifyServerCertificateAsync(ClusterOptions options, CancellationToken ct)
    {
        var validator = ClusterTls.CreateValidator(options, environment.ContentRootPath);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(options.Host!, options.Port, ct);
        await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        var clientCertificates = new X509CertificateCollection();
        if (!string.IsNullOrEmpty(options.ClientCertificatePath))
            clientCertificates.Add(X509CertificateLoader.LoadPkcs12FromFile(ResolvePath(options.ClientCertificatePath), options.ClientCertificatePassword));
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.Host,
                ClientCertificates = clientCertificates,
                RemoteCertificateValidationCallback = validator,
            }, ct);
        }
        catch (Exception e) when (e is System.Security.Authentication.AuthenticationException or IOException)
        {
            throw new IbmMqException($"TLS check of {options.Host}({options.Port}) failed: {e.Message}", 2393, e);
        }
    }

    private string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));

    private void Evict(string cluster, MqConnection connection)
    {
        if (!connection.Broken)
            return;
        if (_connections.TryRemove(new KeyValuePair<string, MqConnection>(cluster, connection)))
            connection.Dispose();
    }

    public void Dispose()
    {
        foreach (var c in _connections.Values)
            c.Dispose();
    }
}
