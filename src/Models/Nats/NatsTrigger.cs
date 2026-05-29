namespace ASAP.Models.Nats;

public enum NatsTriggerType
{
    Periodic,
    Bulk,
    OnIncoming,
}

public sealed class NatsTrigger
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public required string SessionId { get; init; }

    public NatsTriggerType Type { get; set; } = NatsTriggerType.Periodic;
    public bool Enabled { get; set; }

    public bool UseBridgeMessage { get; set; }
    public string? BridgeMessageType { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string PayloadText { get; set; } = "{}";
    public string? Scenario { get; set; }
    public string? SourceStepId { get; set; }

    public int IntervalMs { get; set; } = 1000;
    public int? MaxFires { get; set; }

    public int BulkCount { get; set; } = 10;
    public bool BulkParallel { get; set; }

    public string? MatchSubject { get; set; }
    public bool BlockSelfSubjectLoop { get; set; } = true;
    public int MinFireIntervalMs { get; set; }
    public int MaxFiresPerMinute { get; set; }

    public long TotalFires;
    public long Errors;
    public DateTime? LastFiredAt { get; set; }
    public string? LastError { get; set; }
}