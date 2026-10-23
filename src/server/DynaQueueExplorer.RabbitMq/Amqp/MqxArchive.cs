using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynaQueueExplorer.Core.Messages;

namespace DynaQueueExplorer.RabbitMq.Amqp;

public sealed record MqxSource(string? Cluster, string? Vhost, string? Queue);

/// <param name="Broker">Kind of broker the messages come from (see BrokerTypes); missing in older files means RabbitMQ.</param>
public sealed record MqxManifest(string Format, int Version, DateTimeOffset CreatedAt, string? CreatedBy, MqxSource? Source, int Count, string? Broker = null);

/// <summary>
/// The .mqx export format: a zip with <c>manifest.json</c> and, per message, <c>messages/NNNNNN.json</c>
/// (routing and typed properties) plus <c>messages/NNNNNN.body</c> (the exact body bytes).
/// </summary>
public static class MqxArchive
{
    public const string Extension = ".mqx";
    private const string FormatName = "mqx";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Entry(string Exchange, string RoutingKey, bool Redelivered, StoredProperties Properties, string Body);

    public static async Task WriteAsync(Stream output, IReadOnlyList<RawMessage> messages, string? createdBy, MqxSource? source, CancellationToken ct)
    {
        await using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteJsonAsync(zip, "manifest.json", new MqxManifest(FormatName, CurrentVersion, DateTimeOffset.UtcNow, createdBy, source, messages.Count, BrokerTypes.RabbitMq), ct);
        for (var i = 0; i < messages.Count; i++)
        {
            var name = $"messages/{i + 1:D6}";
            var m = messages[i];
            await WriteJsonAsync(zip, name + ".json", new Entry(m.Exchange, m.RoutingKey, m.Redelivered, m.Properties, name + ".body"), ct);
            var body = zip.CreateEntry(name + ".body", CompressionLevel.Fastest);
            await using var stream = await body.OpenAsync(ct);
            await stream.WriteAsync(m.Body, ct);
        }
    }

    public static async Task<(MqxManifest Manifest, List<RawMessage> Messages)> ReadAsync(Stream input, CancellationToken ct)
    {
        await using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = await ReadJsonAsync<MqxManifest>(zip, "manifest.json", ct)
                       ?? throw new InvalidDataException("Not a .mqx file: manifest.json is missing.");
        if (manifest.Format != FormatName || manifest.Version > CurrentVersion)
            throw new InvalidDataException($"Unsupported file format '{manifest.Format}' version {manifest.Version}.");

        var messages = new List<RawMessage>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("messages/") && e.FullName.EndsWith(".json")).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var meta = await ReadJsonAsync<Entry>(zip, entry.FullName, ct) ?? throw new InvalidDataException($"Invalid entry {entry.FullName}.");
            var bodyEntry = zip.GetEntry(meta.Body) ?? throw new InvalidDataException($"Missing body {meta.Body}.");
            await using var stream = await bodyEntry.OpenAsync(ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            messages.Add(new RawMessage(meta.Exchange, meta.RoutingKey, meta.Redelivered, meta.Properties, buffer.ToArray()));
        }
        return (manifest, messages);
    }

    private static async Task WriteJsonAsync<T>(ZipArchive zip, string name, T value, CancellationToken ct)
    {
        await using var stream = await zip.CreateEntry(name, CompressionLevel.Fastest).OpenAsync(ct);
        await JsonSerializer.SerializeAsync(stream, value, Json, ct);
    }

    private static async Task<T?> ReadJsonAsync<T>(ZipArchive zip, string name, CancellationToken ct)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
            return default;
        await using var stream = await entry.OpenAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
    }
}
