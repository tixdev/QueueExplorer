namespace DynaQueueExplorer.Msmq;

public sealed class MsmqOptions
{
    public const string SectionName = "Msmq";

    /// <summary>
    /// Queue list and depths are refreshed this often while someone uses the machine. Reading a machine through the
    /// agent takes seconds (tens of seconds when its performance counters are not readable remotely), hence the
    /// longer default than for the other brokers.
    /// </summary>
    public TimeSpan ActiveRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A machine nobody used for this long is not polled until the next request.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a call to the agent may take (a full listing of a large machine included).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Read every machine once at startup, so the first visitor does not wait for the agent.</summary>
    public bool WarmUpOnStart { get; set; } = true;

    /// <summary>Message bodies larger than this are cut when browsing.</summary>
    public int BrowseTruncateBytes { get; set; } = 256 * 1024;
}
