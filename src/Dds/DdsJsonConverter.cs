using Rti.Dds.Topics;
using Rti.Types.Dynamic;
using System.Text;
using System.Text.Json;

namespace ASAP.Dds;

/// <summary>
/// DynamicData ↔ JSON 변환. RTI Connext의 내장 PrintFormat/FromString을 활용.
/// - DynamicData → JSON: ToString(PrintFormatProperty { Kind = Json })
/// - JSON → DynamicData: FromString(json, PrintFormatKind.Json)
/// 직접 멤버를 순회하지 않으므로 모든 RTI 지원 타입(struct/enum/sequence/array/union/alias)을 자동 처리.
/// </summary>
public static class DdsJsonConverter
{
    private static readonly PrintFormatProperty _jsonCompact = new()
    {
        Kind = PrintFormatKind.Json,
        PrettyPrint = false,
        EnumAsInt = false,
        IncludeRootElements = false,
    };

    private static readonly PrintFormatProperty _jsonPretty = new()
    {
        Kind = PrintFormatKind.Json,
        PrettyPrint = true,
        EnumAsInt = false,
        IncludeRootElements = false,
    };

    public static string ToJson(DynamicData data, bool pretty = false)
        => NormalizeJson(data.ToString(pretty ? _jsonPretty : _jsonCompact), pretty);

    public static void ApplyJson(DynamicData target, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        target.FromString(json, PrintFormatKind.Json);
    }

    private static string NormalizeJson(string json, bool pretty)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";

        if (TryFormatJson(json, pretty, out var formatted))
            return formatted;

        var repaired = RepairQuotedJsonContainers(json);
        if (TryFormatJson(repaired, pretty, out formatted))
            return formatted;

        var wrapped = "{" + repaired.Trim().Trim(',') + "}";
        return TryFormatJson(wrapped, pretty, out formatted) ? formatted : json;
    }

    private static bool TryFormatJson(string json, bool pretty, out string formatted)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = pretty });
            return true;
        }
        catch
        {
            formatted = string.Empty;
            return false;
        }
    }

    private static string RepairQuotedJsonContainers(string json)
    {
        var sb = new StringBuilder(json.Length);
        var i = 0;
        while (i < json.Length)
        {
            if (json[i] == '"')
            {
                var nameEnd = FindStringEnd(json, i);
                var colon = SkipWhitespace(json, nameEnd + 1);
                if (nameEnd > i && colon < json.Length && json[colon] == ':')
                {
                    var valueQuote = SkipWhitespace(json, colon + 1);
                    if (valueQuote + 1 < json.Length
                        && json[valueQuote] == '"'
                        && (json[valueQuote + 1] == '{' || json[valueQuote + 1] == '['))
                    {
                        var valueStart = valueQuote + 1;
                        var valueEnd = FindBalancedJsonContainer(json, valueStart);
                        if (valueEnd >= 0 && valueEnd + 1 < json.Length && json[valueEnd + 1] == '"')
                        {
                            sb.Append(json, i, valueQuote - i);
                            sb.Append(json, valueStart, valueEnd - valueStart + 1);
                            i = valueEnd + 2;
                            continue;
                        }
                    }
                }
            }

            sb.Append(json[i]);
            i++;
        }

        return sb.ToString();
    }

    private static int SkipWhitespace(string value, int start)
    {
        while (start < value.Length && char.IsWhiteSpace(value[start]))
            start++;
        return start;
    }

    private static int FindStringEnd(string value, int quoteStart)
    {
        var escaped = false;
        for (var i = quoteStart + 1; i < value.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (value[i] == '\\')
            {
                escaped = true;
                continue;
            }

            if (value[i] == '"')
                return i;
        }

        return -1;
    }

    private static int FindBalancedJsonContainer(string value, int start)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        for (var i = start; i < value.Length; i++)
        {
            var ch = value[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
                stack.Push('}');
            else if (ch == '[')
                stack.Push(']');
            else if ((ch == '}' || ch == ']') && stack.Count > 0 && stack.Peek() == ch)
            {
                stack.Pop();
                if (stack.Count == 0)
                    return i;
            }
        }

        return -1;
    }
}
