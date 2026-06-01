namespace ASAP.Models.Dds;

public sealed class DdsSubscriptionInfo
{
    public required string SubscriptionId { get; init; }
    public required string SessionId { get; init; }
    public required string TopicName { get; init; }
    public required string TypeName { get; init; }
    public required DateTime StartedAt { get; init; }

    public long ReceivedCount;     // Interlocked로 증가
    public long LatencySampleCount;
    public double TotalLatencyMs;
    public DdsSampleEntry? LastSample { get; set; }
    public bool IsActive { get; set; } = true;
}
