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

    /// <summary>
    /// JS objects enumerate array-index keys ("0".."4294967294", canonical
    /// form) first in ascending numeric order, then the remaining keys in
    /// insertion order. <c>stableStringify</c> inserts keys sorted, so the
    /// final order is: index keys numerically, then the rest as sorted.
    /// </summary>
    public static List<T> OrderLikeJsObject<T>(List<T> items, Func<T, string> key)
    {
        var indexKeys = new List<(T Item, uint Index)>();
        var rest = new List<T>();
        foreach (var item in items)
        {
            if (IsArrayIndex(key(item), out var index)) indexKeys.Add((item, index));
            else rest.Add(item);
        }
        if (indexKeys.Count == 0) return items;
        return [.. indexKeys.OrderBy(k => k.Index).Select(k => k.Item), .. rest];
    }

    /// <summary>True for canonical array-index strings: "0" or no leading zero, value ≤ 2^32 − 2.</summary>
    public static bool IsArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length == 0 || key.Length > 10) return false;
        if (key.Length > 1 && key[0] == '0') return false;
        ulong value = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9') return false;
            value = value * 10 + (ulong)(c - '0');
        }
        if (value > 4294967294UL) return false;
        index = (uint)value;
        return true;
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
                props = OrderLikeJsObject(props, p => p.Name);
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
