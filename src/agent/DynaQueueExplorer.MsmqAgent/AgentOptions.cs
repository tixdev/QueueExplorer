namespace DynaQueueExplorer.MsmqAgent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Shared secret the explorer sends in <c>X-DQE-Agent-Key</c>; at least 24 characters.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// MSMQ machines the agent may read (host names as used in MSMQ format names, e.g. msmq-01.example.com). Empty: only
    /// the machine the agent runs on. Anything else is refused, so the agent cannot be used to reach other machines.
    /// </summary>
    public List<string> Machines { get; set; } = [];

    /// <summary>Queues counted at the same time when performance counters cannot be read (one RPC per active queue).</summary>
    public int DepthParallelism { get; set; } = 16;

    /// <summary>Dead-letter queues have no counter: they are counted by peeking headers, up to this many messages.</summary>
    public int DeadLetterCountLimit { get; set; } = 1000;

    public int MaxBrowseCount { get; set; } = 200;

    public int MaxBodyBytes { get; set; } = 4 * 1024 * 1024;
}
