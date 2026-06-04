namespace ASAP.Models.Ui;

public sealed class MessageSearchResultItem
{
    public required string Id { get; init; }
    public required string Direction { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Topic { get; init; }
    public required string Summary { get; init; }
    public string? PayloadJson { get; init; }
    public string? RawPayload { get; init; }
    public string? Base64Payload { get; init; }
    public string? HexPayload { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}
