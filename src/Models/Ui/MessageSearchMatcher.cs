using System.Text.Json;

namespace ASAP.Models.Ui;

public static class MessageSearchMatcher
{
    public static bool Matches(MessageSearchCriteria criteria, DateTime timestamp, string topic, string? payloadJson, string? rawPayload = null)
    {
        if (!criteria.HasAnyFilter)
            return true;

        var local = timestamp.ToLocalTime();
        if (criteria.StartLocal.HasValue && local < criteria.StartLocal.Value)
            return false;

        if (criteria.EndLocal.HasValue && local > criteria.EndLocal.Value)
            return false;

        if (!ContainsIgnoreCase(topic, criteria.Topic))
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
