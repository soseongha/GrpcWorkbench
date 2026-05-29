namespace ASAP.Models.Nats;

public enum NatsStepMode
{
    Manual,
    Periodic,
    Bulk,
    OnIncoming,
}

public sealed class NatsScenarioStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public bool UseBridgeMessage { get; set; }
    public string? BridgeMessageType { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string PayloadText { get; set; } = "{}";

    public int RepeatCount { get; set; } = 1;
    public int DelayAfterMs { get; set; }

    public NatsStepMode Mode { get; set; } = NatsStepMode.Manual;
    public bool AutoEnabled { get; set; }
    public int IntervalMs { get; set; } = 1000;
    public int? MaxFires { get; set; }
    public int BulkCount { get; set; } = 10;
    public bool BulkParallel { get; set; }
    public string? MatchSubject { get; set; }
    public bool BlockSelfSubjectLoop { get; set; } = true;
    public int MinFireIntervalMs { get; set; }
    public int MaxFiresPerMinute { get; set; }
}