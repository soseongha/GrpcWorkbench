using System.Text.Json;

namespace ASAP.Models.Ui;

public static class MessageSearchMatcher
{
    public static bool Matches(MessageSearchCriteria criteria, DateTime timestamp, string topic, MessagePayloadSearchIndex payloadIndex)
    {
        if (!BasicMatches(criteria, timestamp, topic))
            return false;

        var keyQuery = criteria.PayloadKey.Trim();
        var valueQuery = criteria.PayloadValue.Trim();
        if (string.IsNullOrWhiteSpace(keyQuery) && string.IsNullOrWhiteSpace(valueQuery))
            return true;

        if (!string.IsNullOrWhiteSpace(keyQuery) && !payloadIndex.Keys.Any(key => ContainsIgnoreCase(key, keyQuery)))
            return false;

        if (!string.IsNullOrWhiteSpace(valueQuery)
            && !payloadIndex.Values.Any(value => ContainsIgnoreCase(value, valueQuery))
            && !ContainsIgnoreCase(payloadIndex.RawText, valueQuery))
        {
            return false;
        }

        return true;
    }

    public static bool Matches(MessageSearchCriteria criteria, DateTime timestamp, string topic, string? payloadJson, string? rawPayload = null)
    {
        if (!BasicMatches(criteria, timestamp, topic))
            return false;

        var keyQuery = criteria.PayloadKey.Trim();
        var valueQuery = criteria.PayloadValue.Trim();
        if (string.IsNullOrWhiteSpace(keyQuery) && string.IsNullOrWhiteSpace(valueQuery))
            return true;

        if (TryParseJson(payloadJson, out var root))
        {
            using var document = root;
            if (!string.IsNullOrWhiteSpace(keyQuery) && !JsonKeyMatches(document.RootElement, keyQuery, string.Empty))
                return false;

            if (!string.IsNullOrWhiteSpace(valueQuery) && !JsonValueMatches(document.RootElement, valueQuery))
                return false;

            return true;
        }

        if (!string.IsNullOrWhiteSpace(keyQuery))
            return false;

        return ContainsIgnoreCase(rawPayload ?? payloadJson ?? string.Empty, valueQuery);
    }

    private static bool BasicMatches(MessageSearchCriteria criteria, DateTime timestamp, string topic)
    {
        if (!criteria.HasAnyFilter)
            return true;

        var local = timestamp.ToLocalTime();
        if (criteria.StartLocal.HasValue && local < criteria.StartLocal.Value)
            return false;

        if (criteria.EndLocal.HasValue && local > criteria.EndLocal.Value)
            return false;

        return ContainsIgnoreCase(topic, criteria.Topic);
    }

    private static bool TryParseJson(string? payload, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            document = JsonDocument.Parse(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool JsonKeyMatches(JsonElement element, string query, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var nextPath = string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}";
                if (ContainsIgnoreCase(property.Name, query)
                    || ContainsIgnoreCase(nextPath, query)
                    || JsonKeyMatches(property.Value, query, nextPath))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var nextPath = $"{path}[{index}]";
                if (JsonKeyMatches(item, query, nextPath))
                    return true;
                index++;
            }
        }

        return false;
    }

    private static bool JsonValueMatches(JsonElement element, string query)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(property => JsonValueMatches(property.Value, query)),
            JsonValueKind.Array => element.EnumerateArray().Any(item => JsonValueMatches(item, query)),
            JsonValueKind.String => ContainsIgnoreCase(element.GetString() ?? string.Empty, query),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => ContainsIgnoreCase(element.ToString(), query),
            _ => false,
        };
    }

    private static bool ContainsIgnoreCase(string value, string query)
        => string.IsNullOrWhiteSpace(query)
            || (!string.IsNullOrWhiteSpace(value) && value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class MessagePayloadSearchIndex
{
    public static readonly MessagePayloadSearchIndex Empty = new([], [], string.Empty);

    public MessagePayloadSearchIndex(IReadOnlyList<string> keys, IReadOnlyList<string> values, string rawText)
    {
        Keys = keys;
        Values = values;
        RawText = rawText;
    }

    public IReadOnlyList<string> Keys { get; }
    public IReadOnlyList<string> Values { get; }
    public string RawText { get; }

    public static MessagePayloadSearchIndex Create(string? payloadJson, string? rawPayload)
    {
        var keys = new List<string>();
        var values = new List<string>();
        var raw = rawPayload ?? payloadJson ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                Collect(document.RootElement, string.Empty, keys, values);
            }
            catch
            {
                values.Add(raw);
            }
        }
        else if (!string.IsNullOrWhiteSpace(raw))
        {
            values.Add(raw);
        }

        return new MessagePayloadSearchIndex(keys, values, raw);
    }

    private static void Collect(JsonElement element, string path, List<string> keys, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var nextPath = string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}";
                    keys.Add(property.Name);
                    keys.Add(nextPath);
                    Collect(property.Value, nextPath, keys, values);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, $"{path}[{index}]", keys, values);
                    index++;
                }
                break;
            case JsonValueKind.String:
                values.Add(element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                values.Add(element.ToString());
                break;
        }
    }
}
