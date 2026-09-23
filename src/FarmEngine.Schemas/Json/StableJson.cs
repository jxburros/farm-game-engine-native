using System.Text;
using System.Text.Json;

namespace FarmEngine.Json;

/// <summary>
/// Port of <c>stableStringify</c> (engine-core/src/hash.ts): JSON with object
/// keys sorted by UTF-16 code unit and JS number/string formatting, so a C#
/// state and a TypeScript state that are equal produce identical text.
/// </summary>
public static class StableJson
{
    public static string Stringify<T>(T value) => Stringify(JsonDefaults.ToElement(value));

    public static string Stringify(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(sb, element);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var props = element.EnumerateObject()
                    .Where(p => p.Value.ValueKind != JsonValueKind.Undefined)
                    .GroupBy(p => p.Name)
                    .Select(g => g.Last())
                    .ToList();
                props.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                sb.Append('{');
                for (var i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Js.QuoteString(props[i].Name)).Append(':');
                    Write(sb, props[i].Value);
                }
                sb.Append('}');
                break;
            }
            case JsonValueKind.Array:
            {
                sb.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                break;
            }
            case JsonValueKind.String:
                sb.Append(Js.QuoteString(element.GetString()!));
                break;
            case JsonValueKind.Number:
            {
                var d = element.GetDouble();
                // JSON.stringify writes non-finite numbers as null.
                sb.Append(double.IsFinite(d) ? Js.Num(d) : "null");
                break;
            }
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }
}
