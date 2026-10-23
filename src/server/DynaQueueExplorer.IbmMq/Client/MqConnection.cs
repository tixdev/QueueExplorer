using System.Collections;
using IBM.WMQ;
using IBM.WMQ.PCF;

namespace DynaQueueExplorer.IbmMq.Client;

/// <param name="Responses">Successful responses.</param>
/// <param name="Errors">Reason codes of the responses that failed (e.g. objects the account may not display).</param>
public sealed record PcfResult(IReadOnlyList<PCFMessage> Responses, IReadOnlyList<int> Errors);

/// <summary>
/// One connection to a queue manager. IBM MQ handles must not be used concurrently, so every operation runs under a
/// lock. The PCF command and reply queues stay open for the life of the connection (two calls fewer per command).
/// </summary>
public sealed class MqConnection(MQQueueManager queueManager) : IDisposable
{
    /// <summary>Prefix of the temporary dynamic queues that receive PCF replies; they are hidden from queue lists.</summary>
    public const string ReplyQueuePrefix = "DQE.PCF.";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private MQQueue? _commandQueue;
    private MQQueue? _replyQueue;

    public string QueueManagerName => queueManager.Name.Trim();
    public bool Broken { get; private set; }

    /// <summary>Runs work on this connection, serialized, off the request thread (the MQ client API is blocking).</summary>
    public async Task<T> RunAsync<T>(Func<MQQueueManager, T> work, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    return work(queueManager);
                }
                catch (MQException e) when (MqErrors.BreaksConnection(e.ReasonCode))
                {
                    Broken = true;
                    throw;
                }
            }, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Sends a PCF command and collects every response. Unlike the IBM PCF agent it keeps the successful responses when
    /// some fail, so a partially authorised account still gets what it may see.
    /// </summary>
    public Task<PcfResult> PcfAsync(PCFMessage command, TimeSpan responseTimeout, CancellationToken ct) => RunAsync(qm =>
    {
        _commandQueue ??= qm.AccessQueue(qm.CommandInputQueueName, MQC.MQOO_OUTPUT | MQC.MQOO_FAIL_IF_QUIESCING);
        _replyQueue ??= qm.AccessQueue("SYSTEM.DEFAULT.MODEL.QUEUE", MQC.MQOO_INPUT_EXCLUSIVE | MQC.MQOO_FAIL_IF_QUIESCING, null, ReplyQueuePrefix + "*", null);

        var request = new MQMessage
        {
            ReplyToQueueName = _replyQueue.Name,
            MessageType = MQC.MQMT_REQUEST,
            Feedback = MQC.MQFB_NONE,
            Format = MQC.MQFMT_ADMIN,
            Report = MQC.MQRO_NONE,
        };
        command.Write(request);
        _commandQueue.Put(request, new MQPutMessageOptions { Options = MQC.MQPMO_NO_SYNCPOINT | MQC.MQPMO_FAIL_IF_QUIESCING });

        var gmo = new MQGetMessageOptions
        {
            Options = MQC.MQGMO_WAIT | MQC.MQGMO_FAIL_IF_QUIESCING | MQC.MQGMO_CONVERT,
            WaitInterval = (int)responseTimeout.TotalMilliseconds,
            MatchOptions = MQC.MQMO_MATCH_CORREL_ID,
        };
        var responses = new List<PCFMessage>();
        var errors = new List<int>();
        while (true)
        {
            var reply = new MQMessage { CorrelationId = request.MessageId };
            _replyQueue.Get(reply, gmo);
            var pcf = new PCFMessage(reply);
            if (pcf.GetCompCode() == MQC.MQCC_OK)
                responses.Add(pcf);
            else
                errors.Add(pcf.GetReason());
            if (pcf.GetControl() == MQC.MQCFC_LAST)
                return new PcfResult(responses, errors);
        }
    }, ct);

    public void Dispose()
    {
        foreach (var queue in new[] { _commandQueue, _replyQueue })
        {
            try { queue?.Close(); } catch (MQException) { }
        }
        try { queueManager.Disconnect(); } catch (MQException) { }
    }
}

public static class MqConnectionProperties
{
    public static Hashtable For(Core.Clusters.ClusterOptions c)
    {
        var props = new Hashtable
        {
            [MQC.TRANSPORT_PROPERTY] = MQC.TRANSPORT_MQSERIES_MANAGED,
            [MQC.HOST_NAME_PROPERTY] = c.Host,
            [MQC.PORT_PROPERTY] = c.Port,
            [MQC.CHANNEL_PROPERTY] = c.Channel,
            [MQC.CONNECT_OPTIONS_PROPERTY] = MQC.MQCNO_HANDLE_SHARE_BLOCK,
        };
        if (!string.IsNullOrEmpty(c.Username))
        {
            props[MQC.USER_ID_PROPERTY] = c.Username;
            props[MQC.PASSWORD_PROPERTY] = c.Password;
            props[MQC.USE_MQCSP_AUTHENTICATION_PROPERTY] = true;
        }
        if (!string.IsNullOrEmpty(c.CipherSpec))
        {
            props[MQC.SSL_CIPHER_SPEC_PROPERTY] = c.CipherSpec;
            // With a key file the client presents that certificate; without one it uses the user's certificate store.
            props[MQC.SSL_CERT_STORE_PROPERTY] = string.IsNullOrEmpty(c.ClientCertificatePath) ? "*USER" : c.ClientCertificatePath;
            if (!string.IsNullOrEmpty(c.ClientCertificatePassword))
                props[MQC.SSL_CERT_STORE_PASSWORD_PROPERTY] = c.ClientCertificatePassword;
        }
        return props;
    }
}
