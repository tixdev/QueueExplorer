using System.Globalization;
using DynaQueueExplorer.IbmMq.Client;
using IBM.WMQ;
using IBM.WMQ.PCF;

namespace DynaQueueExplorer.IbmMq.Snapshots;

/// <summary>Reads queue manager and queue information, through PCF or (without command authority) MQINQ.</summary>
internal static class MqInquiry
{
    public static async Task<(MqQueueManagerInfo Info, string? Limitation)> QueueManagerAsync(IbmMqConnections connections, string cluster, IbmMqOptions options, CancellationToken ct)
    {
        try
        {
            var result = await connections.PcfAsync(cluster, new PCFMessage(MQC.MQCMD_INQUIRE_Q_MGR), options.PcfResponseTimeout, ct);
            if (result.Responses.Count > 0)
            {
                var p = Params(result.Responses[0]);
                return (new MqQueueManagerInfo(Str(p, MQC.MQCA_Q_MGR_NAME) ?? cluster, Str(p, MQC.MQCA_Q_MGR_DESC), Int(p, MQC.MQIA_COMMAND_LEVEL),
                    Version(Str(p, MQC.MQCA_VERSION)), Platform(Int(p, MQC.MQIA_PLATFORM)), Str(p, MQC.MQCA_DEAD_LETTER_Q_NAME)), null);
            }
        }
        catch (IbmMqException e) when (e.IsNotAuthorized)
        {
        }
        // Without PCF the connection still tells the name, the command level and the dead-letter queue (MQINQ on the qmgr).
        var info = await connections.RunAsync(cluster, qm => new MqQueueManagerInfo(qm.Name.Trim(), null, qm.CommandLevel, null, null,
            TryInquire(() => qm.DeadLetterQueueName)), ct);
        return (info, "Queue manager details are limited: PCF commands are not authorised (put on SYSTEM.ADMIN.COMMAND.QUEUE, +inq on the model queue).");
    }

    /// <summary>All queues with depth and status through PCF; partial results when some objects may not be displayed.</summary>
    public static async Task<(List<MqQueueInfo>? Queues, List<string> Limitations)> QueuesByPcfAsync(IbmMqConnections connections, string cluster, IbmMqOptions options, CancellationToken ct)
    {
        var limitations = new List<string>();
        PcfResult definitions;
        try
        {
            var request = new PCFMessage(MQC.MQCMD_INQUIRE_Q);
            request.AddParameter(MQC.MQCA_Q_NAME, "*");
            definitions = await connections.PcfAsync(cluster, request, options.PcfResponseTimeout, ct);
        }
        catch (IbmMqException e) when (e.IsNotAuthorized)
        {
            return (null, limitations);
        }
        var timeZone = TimeZoneOf(connections.Options(cluster).TimeZone, limitations);
        if (definitions.Errors.Count > 0)
            limitations.Add(definitions.Errors.All(r => r == MQC.MQRC_NOT_AUTHORIZED)
                ? "Some queues are not listed: the account lacks +dsp on them."
                : $"Some queues could not be listed ({string.Join(", ", definitions.Errors.Distinct().Select(Formats.MqReasons.Describe))}).");

        Dictionary<string, Dictionary<int, object>> status = new(StringComparer.Ordinal);
        try
        {
            var request = new PCFMessage(MQC.MQCMD_INQUIRE_Q_STATUS);
            request.AddParameter(MQC.MQCA_Q_NAME, "*");
            var result = await connections.PcfAsync(cluster, request, options.PcfResponseTimeout, ct);
            foreach (var r in result.Responses)
            {
                var p = Params(r);
                if (Str(p, MQC.MQCA_Q_NAME) is { } name)
                    status[name] = p;
            }
            if (result.Errors.Count > 0 && result.Responses.Count == 0)
                limitations.Add("Queue status (uncommitted messages, last put/get) is not available: the account lacks +dsp for queue status.");
        }
        catch (IbmMqException e) when (e.IsNotAuthorized)
        {
            limitations.Add("Queue status (uncommitted messages, last put/get) is not available: the account lacks +dsp for queue status.");
        }

        // Our own PCF reply queues (one per connection, including the one answering this very command) are noise.
        var queues = definitions.Responses.Where(r => Str(Params(r), MQC.MQCA_Q_NAME)?.StartsWith(MqConnection.ReplyQueuePrefix, StringComparison.Ordinal) != true).Select(r =>
        {
            var p = Params(r);
            var name = Str(p, MQC.MQCA_Q_NAME) ?? "?";
            var type = Int(p, MQC.MQIA_Q_TYPE) switch
            {
                MQC.MQQT_LOCAL => "local",
                MQC.MQQT_ALIAS => "alias",
                MQC.MQQT_REMOTE => "remote",
                MQC.MQQT_MODEL => "model",
                MQC.MQQT_CLUSTER => "cluster",
                var t => $"type {t}",
            };
            status.TryGetValue(name, out var s);
            var target = type switch
            {
                "alias" => Str(p, MQC.MQCA_BASE_OBJECT_NAME) ?? Str(p, MQC.MQCA_BASE_Q_NAME),
                "remote" => $"{Str(p, MQC.MQCA_REMOTE_Q_NAME)}@{Str(p, MQC.MQCA_REMOTE_Q_MGR_NAME)}" +
                            (Str(p, MQC.MQCA_XMIT_Q_NAME) is { Length: > 0 } x ? $" via {x}" : ""),
                _ => null,
            };
            var oldest = s is null ? null : Int(s, MQC.MQIACF_OLDEST_MSG_AGE);
            return new MqQueueInfo(
                name, type, Int(p, MQC.MQIA_USAGE) == MQC.MQUS_TRANSMISSION,
                Int(p, MQC.MQIA_CURRENT_Q_DEPTH), Int(p, MQC.MQIA_MAX_Q_DEPTH),
                Int(p, MQC.MQIA_OPEN_INPUT_COUNT), Int(p, MQC.MQIA_OPEN_OUTPUT_COUNT),
                s is null ? null : Int(s, MQC.MQIACF_UNCOMMITTED_MSGS),
                Int(p, MQC.MQIA_INHIBIT_GET) == MQC.MQQA_GET_INHIBITED, Int(p, MQC.MQIA_INHIBIT_PUT) == MQC.MQQA_PUT_INHIBITED,
                Str(p, MQC.MQCA_Q_DESC), target,
                s is null ? null : Timestamp(Str(s, MQC.MQCACF_LAST_PUT_DATE), Str(s, MQC.MQCACF_LAST_PUT_TIME), timeZone),
                s is null ? null : Timestamp(Str(s, MQC.MQCACF_LAST_GET_DATE), Str(s, MQC.MQCACF_LAST_GET_TIME), timeZone),
                oldest is >= 0 ? oldest : null);
        }).OrderBy(q => q.Name, StringComparer.Ordinal).ToList();
        return (queues, limitations);
    }

    /// <summary>Fallback without PCF: the configured queues, one MQINQ each (needs +inq on every queue).</summary>
    public static async Task<(List<MqQueueInfo> Queues, List<string> Limitations)> QueuesByInquireAsync(IbmMqConnections connections, string cluster, IReadOnlyList<string> names, CancellationToken ct)
    {
        var limitations = new List<string>
        {
            names.Count == 0
                ? "No queues listed: PCF commands are not authorised and no queues are configured for this connection (Clusters[].Queues)."
                : "Queue list from configuration: PCF commands are not authorised (put on SYSTEM.ADMIN.COMMAND.QUEUE, +inq on the model queue).",
        };
        var failed = new List<string>();
        var queues = await connections.RunAsync(cluster, qm => names.Select(name =>
        {
            try
            {
                var q = qm.AccessQueue(name, MQC.MQOO_INQUIRE | MQC.MQOO_FAIL_IF_QUIESCING);
                try
                {
                    var type = q.QueueType switch { MQC.MQQT_LOCAL => "local", MQC.MQQT_ALIAS => "alias", MQC.MQQT_REMOTE => "remote", _ => "local" };
                    var local = type == "local";
                    return new MqQueueInfo(name, type, false, local ? q.CurrentDepth : null, local ? q.MaximumDepth : null,
                        local ? q.OpenInputCount : null, local ? q.OpenOutputCount : null, null,
                        q.InhibitGet == MQC.MQQA_GET_INHIBITED, q.InhibitPut == MQC.MQQA_PUT_INHIBITED, null, null, null, null, null);
                }
                finally
                {
                    q.Close();
                }
            }
            catch (MQException)
            {
                failed.Add(name);
                return new MqQueueInfo(name, "local", false, null, null, null, null, null, false, false, null, null, null, null, null);
            }
        }).ToList(), ct);
        if (failed.Count > 0)
            limitations.Add($"Depth not available for {failed.Count} queue(s) (+inq missing or queue unknown): {string.Join(", ", failed.Take(5))}.");
        return (queues, limitations);
    }

    private static Dictionary<int, object> Params(PCFMessage message) =>
        message.GetParameters().GroupBy(p => p.Parameter).ToDictionary(g => g.Key, g => g.First().GetValue());

    private static string? Str(Dictionary<int, object> p, int id) => p.TryGetValue(id, out var v) && v is string s ? s.Trim() : null;

    private static int? Int(Dictionary<int, object> p, int id) => p.TryGetValue(id, out var v) && v is int i ? i : null;

    /// <summary>Date and time in the queue manager's local time (as MQ reports them) to an absolute instant.</summary>
    internal static DateTimeOffset? Timestamp(string? date, string? time, TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(date) || string.IsNullOrWhiteSpace(time) ||
            !DateTime.TryParseExact($"{date.Trim()} {time.Trim()}", ["yyyy-MM-dd HH.mm.ss", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return null;
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    private static TimeZoneInfo TimeZoneOf(string? id, List<string> limitations)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            return zone;
        limitations.Add($"Unknown time zone '{id}' in the connection settings: last put/get times are shown as UTC.");
        return TimeZoneInfo.Utc;
    }

    /// <summary>"09040400" → "9.4.4.0".</summary>
    private static string? Version(string? vrmf) =>
        vrmf is { Length: 8 } && vrmf.All(char.IsDigit)
            ? string.Join('.', Enumerable.Range(0, 4).Select(i => int.Parse(vrmf.Substring(i * 2, 2), CultureInfo.InvariantCulture)))
            : vrmf;

    private static string? Platform(int? platform) => platform switch
    {
        null => null,
        MQC.MQPL_UNIX => "Unix/Linux",
        MQC.MQPL_WINDOWS_NT => "Windows",
        MQC.MQPL_ZOS => "z/OS",
        MQC.MQPL_OS400 => "IBM i",
        MQC.MQPL_APPLIANCE => "Appliance",
        var p => $"platform {p}",
    };

    private static string? TryInquire(Func<string> read)
    {
        try { return read().Trim(); }
        catch (MQException) { return null; }
    }
}
