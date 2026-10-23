using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DynaQueueExplorer.Core.Msmq;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.MsmqAgent.Msmq;

/// <summary>
/// Lists the queues of a machine with their depths. Private queues come from MSMQApplication (one RPC); depths from
/// the "MSMQ Queue" performance counters (one call for every queue) or, when those cannot be read remotely, from
/// MSMQManagement for each active queue: MSMQ keeps every queue that holds messages active, so the others are empty.
/// </summary>
public sealed class QueueLister(IOptions<AgentOptions> options, ILogger<QueueLister> logger)
{
    private const string MessagesCounter = "Messages in Queue";
    private const string BytesCounter = "Bytes in Queue";
    private const string JournalCounter = "Messages in Journal Queue";

    public MsmqQueueListResponse List(string machine)
    {
        var started = Stopwatch.GetTimestamp();
        var limitations = new List<string>();
        dynamic app = MsmqCom.Create("MSMQ.MSMQApplication");
        string[] privatePaths;
        string[] activeFormatNames;
        try
        {
            app.Machine = machine;
            privatePaths = ToStrings(app.PrivateQueues);
            activeFormatNames = ToStrings(app.ActiveQueues);
        }
        catch (COMException e)
        {
            throw MsmqException.From(e, $"Listing the queues of {machine}");
        }
        finally
        {
            MsmqCom.Release(app);
        }

        // "host\private$\name" → "private$\name"; MSMQ names are case-insensitive and counters report them lower-case.
        var privateNames = privatePaths.Select(p => p[(p.IndexOf('\\') + 1)..]).ToList();
        var active = activeFormatNames.Select(f => (FormatName: f, Target: TargetOf(f))).Where(a => a.Target is not null).ToList();
        var outgoing = active.Where(a => !SameHost(a.Target!.Value.Host, machine)).ToList();
        var activeLocal = active.Where(a => SameHost(a.Target!.Value.Host, machine)).Select(a => a.FormatName).ToList();

        var counters = ReadCounters(machine, limitations);
        var (depths, source) = counters is not null
            ? (counters, "performance counters")
            : (ReadManagement(machine, activeLocal), "queue management");

        var queues = new List<MsmqQueueStatus>(privateNames.Count + outgoing.Count + 2);
        foreach (var name in privateNames)
        {
            var d = depths.GetValueOrDefault(name.ToLowerInvariant());
            queues.Add(new MsmqQueueStatus(name, MsmqQueueKinds.Private, $"DIRECT=OS:{machine}\\{name}", d?.Messages ?? 0, d?.Bytes ?? 0, d?.Journal));
        }
        queues.AddRange(ReadOutgoing(machine, outgoing.Select(o => o.FormatName).ToList(), counters));
        queues.AddRange(CountDeadLetters(machine, limitations));

        logger.LogDebug("Listed {Count} queues of {Machine} from {Source} in {Elapsed} ms", queues.Count, machine, source,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new MsmqQueueListResponse(machine, DateTimeOffset.UtcNow, source, queues, limitations);
    }

    private sealed record Depth(long? Messages, long? Bytes, long? Journal);

    /// <summary>
    /// Depths of every active queue: local ones by "private$\name", outgoing ones by <see cref="OutgoingKey"/>, all
    /// lower-case. Null when the counters are not readable.
    /// </summary>
    private Dictionary<string, Depth>? ReadCounters(string machine, List<string> limitations)
    {
        try
        {
            var data = new PerformanceCounterCategory("MSMQ Queue", machine).ReadCategory();
            var messages = data[MessagesCounter];
            var bytes = data[BytesCounter];
            var journal = data[JournalCounter];
            var result = new Dictionary<string, Depth>(StringComparer.Ordinal);
            if (messages is null)
                return result;
            foreach (InstanceData instance in messages.Values)
            {
                // Local queues: "host\private$\name"; outgoing ones: "os:host\private$\name" (also "tcp:", "http:"…).
                var name = instance.InstanceName;
                var slash = name.IndexOf('\\');
                if (slash < 0)
                    continue;
                var colon = name.IndexOf(':');
                var key = colon >= 0 && colon < slash
                    ? OutgoingKey(name[(colon + 1)..slash], name[(slash + 1)..])
                    : name[(slash + 1)..].ToLowerInvariant();
                result[key] = new Depth(instance.RawValue,
                    bytes?.Contains(name) == true ? bytes[name].RawValue : null,
                    journal?.Contains(name) == true ? journal[name].RawValue : null);
            }
            return result;
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or IOException)
        {
            limitations.Add($"Performance counters of {machine} are not readable ({e.Message.Trim()}): depths are read queue by queue, which is slower.");
            return null;
        }
    }

    private Dictionary<string, Depth> ReadManagement(string machine, List<string> formatNames)
    {
        var result = new ConcurrentDictionary<string, Depth>(StringComparer.Ordinal);
        Parallel.ForEach(formatNames, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.DepthParallelism) }, formatName =>
        {
            var management = MsmqCom.Create("MSMQ.MSMQManagement");
            try
            {
                management.Init(machine, null, formatName);
                var name = TargetOf(formatName)!.Value.Name.ToLowerInvariant();
                result[name] = new Depth(
                    MsmqCom.Try(() => (long)management.MessageCount),
                    MsmqCom.Try(() => (long?)Convert.ToInt64(management.BytesInQueue)),
                    MsmqCom.Try(() => (long?)Convert.ToInt64(management.JournalMessageCount)));
            }
            catch (COMException e)
            {
                // A queue that became inactive in the meantime is empty; anything else leaves the depth unknown.
                if (unchecked((uint)e.HResult) != 0xC00E0004)
                    logger.LogDebug("Depth of {FormatName} not read: {Error}", formatName, MsmqCom.Describe(e));
            }
            finally
            {
                MsmqCom.Release(management);
            }
        });
        return new Dictionary<string, Depth>(result, StringComparer.Ordinal);
    }

    /// <summary>
    /// Outgoing queues with their connection state. With counters, only queues holding messages are asked for their
    /// state (an empty outgoing queue is usually just inactive), which keeps machines with hundreds of them fast.
    /// </summary>
    private List<MsmqQueueStatus> ReadOutgoing(string machine, List<string> formatNames, Dictionary<string, Depth>? counters)
    {
        var result = new ConcurrentBag<MsmqQueueStatus>();
        Parallel.ForEach(formatNames, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Value.DepthParallelism) }, formatName =>
        {
            var target = TargetOf(formatName)!.Value;
            if (counters is not null && counters.GetValueOrDefault(OutgoingKey(target.Host, target.Name)) is var depth && (depth?.Messages ?? 0) == 0)
            {
                result.Add(new MsmqQueueStatus($"{target.Host}\\{target.Name}", MsmqQueueKinds.Outgoing, formatName, 0, depth?.Bytes ?? 0));
                return;
            }
            try
            {
                var info = MsmqManagementApi.GetQueueInfo(machine, formatName);
                result.Add(new MsmqQueueStatus($"{target.Host}\\{target.Name}", MsmqQueueKinds.Outgoing, formatName,
                    info.Messages, info.Bytes, State: info.State, NextHops: info.NextHops));
            }
            catch (COMException e)
            {
                result.Add(new MsmqQueueStatus($"{target.Host}\\{target.Name}", MsmqQueueKinds.Outgoing, formatName, null, State: MsmqCom.Describe(e)));
            }
        });
        return result.OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The system dead-letter queues have neither counters nor management info: their messages are counted by peeking.</summary>
    private IEnumerable<MsmqQueueStatus> CountDeadLetters(string machine, List<string> limitations)
    {
        foreach (var name in new[] { "system$;deadxact", "system$;deadletter" })
        {
            var formatName = $"DIRECT=OS:{machine}\\{name}";
            long? count;
            var atLeast = false;
            try
            {
                count = MessagePeeker.Count(formatName, options.Value.DeadLetterCountLimit);
                atLeast = count >= options.Value.DeadLetterCountLimit;
            }
            catch (MsmqException e)
            {
                limitations.Add($"Dead-letter queue {name} not readable: {e.Message}");
                count = null;
            }
            yield return new MsmqQueueStatus(name, MsmqQueueKinds.DeadLetter, formatName, count, AtLeast: atLeast);
        }
    }

    /// <summary>"DIRECT=OS:host\private$\name" → (host, "private$\name"); other format names (public, multicast…) → null.</summary>
    internal static (string Host, string Name)? TargetOf(string formatName)
    {
        var eq = formatName.IndexOf('=');
        var colon = formatName.IndexOf(':', eq + 1);
        if (eq < 0 || colon < 0 || !formatName[..eq].Equals("DIRECT", StringComparison.OrdinalIgnoreCase))
            return null;
        var address = formatName[(colon + 1)..];
        var slash = address.IndexOf('\\');
        return slash < 0 ? null : (address[..slash], address[(slash + 1)..]);
    }

    /// <summary>Host names compared on their first label, so "msmq-01" and "msmq-01.example.com" are the same machine.</summary>
    internal static bool SameHost(string a, string b) =>
        string.Equals(FirstLabel(a), FirstLabel(b), StringComparison.OrdinalIgnoreCase) || (IsLocal(a) && IsLocal(b));

    private static string FirstLabel(string host) => host.Split('.')[0];

    /// <summary>Outgoing queues are named after their destination, with the host in short or full form.</summary>
    private static string OutgoingKey(string host, string name) => $"{FirstLabel(host)}\\{name}".ToLowerInvariant();

    private static bool IsLocal(string host) =>
        host is "." or "localhost" || string.Equals(FirstLabel(host), Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    private static string[] ToStrings(object? value) => value switch
    {
        null => [],
        string s => [s],
        object[] items => items.Select(i => i?.ToString() ?? "").Where(s => s.Length > 0).ToArray(),
        Array array => array.Cast<object?>().Select(i => i?.ToString() ?? "").Where(s => s.Length > 0).ToArray(),
        _ => [],
    };
}
