using System.Reflection;
using IBM.WMQ;

namespace DynaQueueExplorer.IbmMq.Formats;

/// <summary>Names of MQ constants (reason codes, commands, PCF parameters), read once from the IBM client's MQC class.</summary>
public static class MqConstants
{
    private static readonly Lazy<Dictionary<string, Dictionary<int, string>>> ByPrefix = new(Load);

    private static Dictionary<string, Dictionary<int, string>> Load()
    {
        var result = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        foreach (var field in typeof(MQC).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(int)))
        {
            var prefix = field.Name[..(field.Name.IndexOf('_') is var i and > 0 ? i : field.Name.Length)];
            var value = (int)field.GetRawConstantValue()!;
            var map = result.TryGetValue(prefix, out var m) ? m : result[prefix] = [];
            map.TryAdd(value, field.Name); // the first declared name wins (MQ declares preferred names first)
        }
        return result;
    }

    /// <summary>First name among the given prefixes, e.g. Name(2053, "MQRC") = "MQRC_Q_FULL".</summary>
    public static string? Name(int value, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
            if (ByPrefix.Value.TryGetValue(prefix, out var map) && map.TryGetValue(value, out var name))
                return name;
        return null;
    }

    public static string? Reason(int reason) => Name(reason, "MQRC", "MQRCCF");

    public static string? Command(int command) => Name(command, "MQCMD");

    /// <summary>PCF parameter name; string and integer parameter ids live in different ranges with overlapping prefixes.</summary>
    public static string? Parameter(int id, bool isString) => isString
        ? Name(id, "MQCA", "MQCACF", "MQCACH", "MQCAMO", "MQBACF", "MQGACF")
        : Name(id, "MQIA", "MQIACF", "MQIACH", "MQIAMO", "MQIAMO64", "MQGACF");
}

/// <summary>Plain-language meaning of the reason and feedback codes most often seen on dead-letter queues.</summary>
public static class MqReasons
{
    private static readonly Dictionary<int, string> Meanings = new()
    {
        [2035] = "The user is not authorised for the operation (check queue and connection authorities).",
        [2051] = "Put is inhibited on the destination queue.",
        [2052] = "The destination queue was deleted.",
        [2053] = "The destination queue was full (max depth reached).",
        [2056] = "No space left on the queue manager's disk for the queue.",
        [2058] = "Queue manager name not valid or not known.",
        [2059] = "Queue manager not available.",
        [2085] = "The destination queue does not exist on the receiving queue manager.",
        [2087] = "Unknown remote queue manager (no route or transmission queue).",
        [2030] = "The message is longer than the destination queue's maximum message length.",
        [2031] = "The message is longer than the queue manager's maximum message length.",
        [2189] = "Cluster resolution error: no instance of the clustered queue could be found.",
        [2195] = "Unexpected error in the queue manager.",
        [2393] = "TLS initialisation error.",
        [2397] = "TLS: the certificate store could not be opened.",
        [2400] = "TLS: the CipherSpec is not supported by the client.",
        [2402] = "TLS: certificate store or validation error.",
        [258] = "MQFB_EXPIRATION: the message expired.",
        [265] = "MQFB_APPL_CANNOT_BE_STARTED: a triggered application could not be started.",
        [266] = "MQFB_TM_ERROR: trigger monitor error.",
        [271] = "MQFB_STOPPED_BY_MSG_EXIT: a message exit stopped the message.",
        [274] = "MQFB_ACTIVITY: activity report.",
        [280] = "MQFB_BUFFER_OVERFLOW: a data conversion buffer overflowed.",
        [291] = "MQFB_XMIT_Q_MSG_ERROR: a message on a transmission queue was not valid.",
    };

    /// <summary>"2053 MQRC_Q_FULL — The destination queue was full …" (feedback codes MQFB_* use the same field in the DLH).</summary>
    public static string Describe(int code)
    {
        var name = MqConstants.Reason(code) ?? MqConstants.Name(code, "MQFB");
        var meaning = Meanings.GetValueOrDefault(code);
        return $"{code}{(name is null ? "" : " " + name)}{(meaning is null ? "" : " — " + meaning)}";
    }
}
