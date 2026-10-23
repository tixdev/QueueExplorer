namespace DynaQueueExplorer.IbmMq;

public sealed class IbmMqOptions
{
    public const string SectionName = "IbmMq";

    /// <summary>Queue list and depths are refreshed this often while someone uses the queue manager.</summary>
    public TimeSpan ActiveRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Queue manager attributes (version, dead-letter queue…) are re-read this often.</summary>
    public TimeSpan StructureRefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A queue manager nobody used for this long is not polled until the next request.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait for each PCF response from the command server.</summary>
    public TimeSpan PcfResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Payloads larger than this are cut when browsing.</summary>
    public int BrowseTruncateBytes { get; set; } = 256 * 1024;
}
