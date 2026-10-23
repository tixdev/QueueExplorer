using System.Globalization;
using static DynaQueueExplorer.NServiceBus.NServiceBusHeaders;

namespace DynaQueueExplorer.NServiceBus;

/// <summary>
/// Recognizes messages produced by NServiceBus from their transport headers and extracts a structured view.
/// </summary>
public static class NServiceBusMessageDecoder
{
    // NServiceBus wire format for dates, e.g. "2024-05-02 14:03:11:482913 Z".
    private static readonly string[] WireDateFormats = ["yyyy-MM-dd HH:mm:ss:ffffff 'Z'", "yyyy-MM-dd HH:mm:ss:ffffff Z"];

    public static bool IsNServiceBusMessage(IReadOnlyDictionary<string, string?> headers) =>
        headers.ContainsKey(MessageId) || headers.ContainsKey(EnclosedMessageTypes) || headers.ContainsKey(ControlMessage);

    public static NServiceBusMessageInfo? TryDecode(IReadOnlyDictionary<string, string?> headers)
    {
        if (!IsNServiceBusMessage(headers))
            return null;

        var failure = DecodeFailure(headers);
        var intent = Get(headers, MessageIntent);

        return new NServiceBusMessageInfo(
            Kind: ClassifyKind(headers, intent, failure),
            MessageId: Get(headers, MessageId),
            Intent: intent,
            MessageTypes: ParseMessageTypes(Get(headers, EnclosedMessageTypes)),
            ConversationId: Get(headers, ConversationId),
            CorrelationId: Get(headers, CorrelationId),
            RelatedTo: Get(headers, RelatedTo),
            ContentType: Get(headers, ContentType),
            NServiceBusVersion: Get(headers, NServiceBusHeaders.NServiceBusVersion),
            TimeSent: ParseWireDate(Get(headers, TimeSent)),
            DeliverAt: ParseWireDate(Get(headers, DeliverAt)),
            TimeToBeReceived: Get(headers, TimeToBeReceived),
            OriginatingEndpoint: Get(headers, OriginatingEndpoint),
            OriginatingMachine: Get(headers, OriginatingMachine),
            OriginatingHost: Get(headers, HostDisplayName),
            ReplyToAddress: Get(headers, ReplyToAddress),
            Saga: DecodeSaga(headers),
            Failure: failure,
            HeaderGroups: GroupHeaders(headers));
    }

    public static DateTimeOffset? ParseWireDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTimeOffset.TryParseExact(value, WireDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var wire))
            return wire;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var other) ? other : null;
    }

    /// <summary>Parses a <c>;</c>-separated list of assembly-qualified type names.</summary>
    public static IReadOnlyList<MessageTypeName> ParseMessageTypes(string? enclosedMessageTypes)
    {
        if (string.IsNullOrWhiteSpace(enclosedMessageTypes))
            return [];

        return SplitTopLevel(enclosedMessageTypes, ';')
            .Select(ParseTypeName)
            .ToList();
    }

    private static MessageTypeName ParseTypeName(string assemblyQualifiedName)
    {
        var parts = SplitTopLevel(assemblyQualifiedName, ',');
        var fullName = parts[0];
        var assembly = parts.Count > 1 ? parts[1] : null;
        var version = parts.Skip(2).FirstOrDefault(p => p.StartsWith("Version=", StringComparison.Ordinal))?["Version=".Length..];

        // Nested types use '+'; generic arguments (after the backtick) are kept in the name but not searched for dots.
        var tick = fullName.IndexOf('`');
        var lastDot = fullName.LastIndexOf('.', tick >= 0 ? tick : fullName.Length - 1);
        var name = lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
        var ns = lastDot >= 0 ? fullName[..lastDot] : null;
        return new MessageTypeName(fullName, name.Replace('+', '.'), ns, assembly, version);
    }

    /// <summary>Splits on <paramref name="separator"/> outside of the square brackets of generic type arguments.</summary>
    private static List<string> SplitTopLevel(string value, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '[')
                depth++;
            else if (value[i] == ']')
                depth--;
            else if (value[i] == separator && depth == 0)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }
        parts.Add(value[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private static NServiceBusMessageKind ClassifyKind(IReadOnlyDictionary<string, string?> headers, string? intent, NServiceBusFailure? failure)
    {
        if (failure is not null)
            return NServiceBusMessageKind.FailedMessage;
        if (intent is "Subscribe" or "Unsubscribe")
            return NServiceBusMessageKind.Subscription;
        if (string.Equals(Get(headers, ControlMessage), "true", StringComparison.OrdinalIgnoreCase))
            return NServiceBusMessageKind.ControlMessage;
        return NServiceBusMessageKind.Message;
    }

    private static NServiceBusFailure? DecodeFailure(IReadOnlyDictionary<string, string?> headers)
    {
        if (!headers.ContainsKey(ExceptionType) && !headers.ContainsKey(FailedQueue))
            return null;

        return new NServiceBusFailure(
            Get(headers, ExceptionType),
            Get(headers, ExceptionMessage),
            Get(headers, ExceptionSource),
            Get(headers, ExceptionStackTrace),
            Get(headers, InnerExceptionType),
            Get(headers, FailedQueue),
            ParseWireDate(Get(headers, TimeOfFailure)),
            Get(headers, ProcessingEndpoint),
            Get(headers, ProcessingMachine),
            Get(headers, FailedHandlerType),
            int.TryParse(Get(headers, DelayedRetries), CultureInfo.InvariantCulture, out var retries) ? retries : null,
            ParseWireDate(Get(headers, DelayedRetriesTimestamp)),
            headers
                .Where(h => IsApplicationExceptionData(h.Key))
                .OrderBy(h => h.Key, StringComparer.Ordinal)
                .ToDictionary(h => h.Key[ExceptionDataPrefix.Length..], h => h.Value));
    }

    private static NServiceBusSaga? DecodeSaga(IReadOnlyDictionary<string, string?> headers)
    {
        var saga = new NServiceBusSaga(Get(headers, SagaId), Get(headers, SagaType), Get(headers, OriginatingSagaId), Get(headers, OriginatingSagaType));
        return saga is { SagaId: null, SagaType: null, OriginatingSagaId: null, OriginatingSagaType: null } ? null : saga;
    }

    private static IReadOnlyList<HeaderGroup> GroupHeaders(IReadOnlyDictionary<string, string?> headers)
    {
        var groups = new[]
        {
            ("Exception data", (Func<string, bool>)IsApplicationExceptionData),
            ("Failure", k => k.StartsWith(ExceptionInfoPrefix, StringComparison.Ordinal)
                             || k is FailedQueue or TimeOfFailure or DelayedRetries or DelayedRetriesTimestamp),
            ("Saga", k => k.Contains("Saga", StringComparison.Ordinal) && k.StartsWith(Prefix, StringComparison.Ordinal)),
            ("Diagnostics", k => k.StartsWith(DiagnosticsPrefix, StringComparison.Ordinal)),
            ("ServiceControl", k => k.StartsWith(ServiceControlPrefix, StringComparison.Ordinal)),
            ("NServiceBus", k => k.StartsWith(Prefix, StringComparison.Ordinal)),
            ("Other", _ => true),
        };

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<HeaderGroup>();
        foreach (var (name, predicate) in groups)
        {
            var entries = headers.Keys
                .Where(k => !assigned.Contains(k) && predicate(k))
                .Order(StringComparer.Ordinal)
                .Select(k => new HeaderEntry(k, Label(k), headers[k]))
                .ToList();
            assigned.UnionWith(entries.Select(e => e.Key));
            if (entries.Count > 0)
                result.Add(new HeaderGroup(name, entries));
        }
        return result;
    }

    private static bool IsApplicationExceptionData(string key) =>
        key.StartsWith(ExceptionDataPrefix, StringComparison.Ordinal) && !BuiltInExceptionData.Contains(key[ExceptionDataPrefix.Length..]);

    private static string Label(string key) =>
        key.StartsWith(ExceptionDataPrefix, StringComparison.Ordinal) ? key[ExceptionDataPrefix.Length..]
        : key.StartsWith(ExceptionInfoPrefix, StringComparison.Ordinal) ? key[ExceptionInfoPrefix.Length..]
        : key.StartsWith(Prefix, StringComparison.Ordinal) ? key[Prefix.Length..]
        : key.StartsWith(DiagnosticsPrefix, StringComparison.Ordinal) ? key[DiagnosticsPrefix.Length..]
        : key;

    private static string? Get(IReadOnlyDictionary<string, string?> headers, string key) =>
        headers.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;
}
