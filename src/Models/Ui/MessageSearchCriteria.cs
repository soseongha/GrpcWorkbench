namespace ASAP.Models.Ui;

public sealed class MessageSearchCriteria
{
    public string Direction { get; set; } = string.Empty;
    public DateTime? StartLocal { get; set; }
    public DateTime? EndLocal { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string PayloadKey { get; set; } = string.Empty;
    public string PayloadValue { get; set; } = string.Empty;

    public bool HasAnyFilter =>
        StartLocal.HasValue
        || EndLocal.HasValue
        || !string.IsNullOrWhiteSpace(Topic)
        || !string.IsNullOrWhiteSpace(PayloadKey)
        || !string.IsNullOrWhiteSpace(PayloadValue);

    public MessageSearchCriteria Clone() => new()
    {
        Direction = Direction,
        StartLocal = StartLocal,
        EndLocal = EndLocal,
        Topic = Topic,
        PayloadKey = PayloadKey,
        PayloadValue = PayloadValue,
    };
}
