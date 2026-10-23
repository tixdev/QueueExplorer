using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using DynaQueueExplorer.Core.Msmq;

namespace DynaQueueExplorer.MsmqAgent.Msmq;

/// <summary>Peeks messages with a cursor: MSMQ peeking never changes the queue.</summary>
public static class MessagePeeker
{
    public static List<MsmqMessage> Peek(string formatName, int count, int maxBodyBytes)
    {
        var messages = new List<MsmqMessage>(Math.Min(count, 256));
        ForEach(formatName, count, wantBody: true, m => messages.Add(Map(m, maxBodyBytes)));
        return messages;
    }

    /// <summary>Counts messages by peeking headers only, stopping at <paramref name="limit"/>.</summary>
    public static long Count(string formatName, int limit)
    {
        long n = 0;
        ForEach(formatName, limit, wantBody: false, _ => n++);
        return n;
    }

    private static void ForEach(string formatName, int limit, bool wantBody, Action<dynamic> visit)
    {
        dynamic info = MsmqCom.Create("MSMQ.MSMQQueueInfo");
        dynamic? queue = null;
        try
        {
            info.FormatName = formatName;
            queue = info.Open(MsmqCom.PeekAccess, MsmqCom.DenyNone);
            // Arguments: WantDestinationQueue, WantBody, ReceiveTimeout (0: only what is there now).
            var message = queue.PeekCurrent(true, wantBody, 0);
            var seen = 0;
            while (message is not null && seen < limit)
            {
                try
                {
                    visit(message);
                }
                finally
                {
                    MsmqCom.Release(message);
                }
                seen++;
                message = seen < limit ? queue.PeekNext(true, wantBody, 0) : null;
            }
        }
        catch (COMException e)
        {
            throw MsmqException.From(e, $"Peeking {formatName}");
        }
        finally
        {
            if (queue is not null)
            {
                MsmqCom.Try(() => { queue.Close(); return 0; });
                MsmqCom.Release(queue);
            }
            MsmqCom.Release(info);
        }
    }

    private static MsmqMessage Map(dynamic m, int maxBodyBytes)
    {
        var (bodyType, body) = Body(MsmqCom.Try<object?>(() => m.Body));
        var truncated = body.Length > maxBodyBytes;
        var journal = MsmqCom.Try(() => (int?)Convert.ToInt32(m.Journal));
        return new MsmqMessage(
            MessageId(MsmqCom.Try(() => (byte[]?)m.Id)) ?? "",
            MessageId(MsmqCom.Try(() => (byte[]?)m.CorrelationId)),
            NullIfEmpty(MsmqCom.Try(() => (string?)m.Label)),
            MsmqCom.Try(() => (long?)Convert.ToInt64(m.LookupId)),
            MsmqCom.Try(() => (int?)Convert.ToInt32(m.Class)),
            MsmqCom.Try(() => (int?)Convert.ToInt32(m.Priority)),
            MsmqCom.Try(() => (bool?)(Convert.ToInt32(m.Delivery) == 1)),
            journal is null ? null : (journal & 2) != 0,
            journal is null ? null : (journal & 1) != 0,
            MsmqCom.Try(() => (int?)Convert.ToInt32(m.AppSpecific)),
            Time(MsmqCom.Try<object?>(() => m.SentTime)),
            Time(MsmqCom.Try<object?>(() => m.ArrivedTime)),
            Seconds(MsmqCom.Try<object?>(() => m.MaxTimeToReachQueue)),
            Seconds(MsmqCom.Try<object?>(() => m.MaxTimeToReceive)),
            QueueOf(MsmqCom.Try<object?>(() => m.ResponseQueueInfo)),
            QueueOf(MsmqCom.Try<object?>(() => m.AdminQueueInfo)),
            QueueOf(MsmqCom.Try<object?>(() => m.DestinationQueueInfo)),
            MessageId(MsmqCom.Try(() => (byte[]?)m.TransactionId)),
            MsmqCom.Try(() => (bool?)Convert.ToBoolean(m.IsFirstInTransaction2)),
            MsmqCom.Try(() => (bool?)Convert.ToBoolean(m.IsLastInTransaction2)),
            NullIfEmpty(MsmqCom.Try(() => (string?)m.SourceMachineGuid)?.TrimEnd('\0')),
            Sender(MsmqCom.Try(() => (byte[]?)m.SenderId)),
            MsmqCom.Try(() => (int)m.BodyLength),
            bodyType,
            Convert.ToBase64String(truncated ? body.AsSpan(0, maxBodyBytes) : body),
            truncated,
            MsmqCom.Try(() => (byte[]?)m.Extension) is { Length: > 0 } extension ? Convert.ToBase64String(extension) : null);
    }

    /// <summary>Byte bodies are returned as they are; COM string bodies (BSTR) as UTF-8.</summary>
    private static (string Type, byte[] Bytes) Body(object? body) => body switch
    {
        null => ("bytes", []),
        byte[] bytes => ("bytes", bytes),
        string text => ("string", Encoding.UTF8.GetBytes(text)),
        _ => (body.GetType().Name, Encoding.UTF8.GetBytes(Convert.ToString(body, System.Globalization.CultureInfo.InvariantCulture) ?? "")),
    };

    /// <summary>20-byte MSMQ ids in the System.Messaging form: "{guid}\{sequence}".</summary>
    internal static string? MessageId(byte[]? id)
    {
        if (id is not { Length: 20 } || id.All(b => b == 0))
            return null;
        return $"{new Guid(id.AsSpan(0, 16))}\\{BitConverter.ToInt32(id, 16)}";
    }

    private static DateTimeOffset? Time(object? value) =>
        value is DateTime t && t.Year > 1970 ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Local)).ToUniversalTime() : null;

    /// <summary>MSMQ uses INFINITE (0xFFFFFFFF) for "no limit".</summary>
    private static long? Seconds(object? value)
    {
        if (value is null)
            return null;
        var seconds = Convert.ToInt64(value);
        return seconds is < 0 or >= int.MaxValue ? null : seconds;
    }

    private static string? QueueOf(object? queueInfo)
    {
        if (queueInfo is null)
            return null;
        try
        {
            return NullIfEmpty(MsmqCom.Try(() => (string?)((dynamic)queueInfo).FormatName));
        }
        finally
        {
            MsmqCom.Release(queueInfo);
        }
    }

    private static string? Sender(byte[]? sid)
    {
        if (sid is not { Length: > 0 })
            return null;
        SecurityIdentifier identifier;
        try
        {
            identifier = new SecurityIdentifier(sid, 0);
        }
        catch (ArgumentException)
        {
            return null;
        }
        try
        {
            return identifier.Translate(typeof(NTAccount)).Value;
        }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException)
        {
            return identifier.Value;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
