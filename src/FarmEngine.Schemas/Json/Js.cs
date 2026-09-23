using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FarmEngine.Json;

/// <summary>
/// JavaScript semantics the port depends on. The TypeScript engine is the
/// reference implementation; wherever C# and JS differ (rounding, number
/// formatting, sort stability, string ordering) the port calls into here so
/// behavior — and therefore state hashes — stay byte-identical.
/// </summary>
public static class Js
{
    /// <summary>JS <c>Math.round</c>: halves round toward +∞ (C# rounds to even).</summary>
    public static double Round(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return x;
        var floor = Math.Floor(x);
        return x - floor >= 0.5 ? floor + 1 : floor;
    }

    /// <summary>JS <c>Math.trunc</c>.</summary>
    public static double Trunc(double x) => Math.Truncate(x);

    /// <summary>JS <c>Number.isInteger</c>.</summary>
    public static bool IsInteger(double x) => double.IsFinite(x) && Math.Floor(x) == x;

    /// <summary>JS <c>x % y</c> (sign follows the dividend, like C#'s % on doubles).</summary>
    public static double Mod(double x, double y) => x % y;

    /// <summary>JS default <c>Array.prototype.sort</c> comparison / <c>&lt;</c> on strings: UTF-16 code units.</summary>
    public static int CompareStrings(string? a, string? b) => string.CompareOrdinal(a, b);

    /// <summary>
    /// Approximation of <c>a.localeCompare(b)</c> for the engine's use (sorting
    /// display names). Invariant culture, which matches ICU for ASCII text.
    /// </summary>
    public static int LocaleCompare(string a, string b) =>
        string.Compare(a, b, CultureInfo.InvariantCulture, CompareOptions.None);

    /// <summary>
    /// Stable sort (JS <c>Array.prototype.sort</c> is stable since ES2019;
    /// <c>List&lt;T&gt;.Sort</c> is not). Returns a new list.
    /// </summary>
    public static List<T> StableSort<T>(IEnumerable<T> source, Comparison<T> comparison)
    {
        var indexed = source.Select((item, index) => (item, index)).ToList();
        indexed.Sort((a, b) =>
        {
            var c = comparison(a.item, b.item);
            return c != 0 ? c : a.index.CompareTo(b.index);
        });
        return indexed.Select(pair => pair.item).ToList();
    }

    /// <summary>JS <c>String(number)</c> / template-literal formatting of a number.</summary>
    public static string Num(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return "0";

        // .NET Core 3.0+ "R" yields the shortest round-trippable digits, the
        // same digit string ECMAScript's Number::toString picks.
        var r = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = r.StartsWith('-');
        if (negative) r = r[1..];

        var exponent = 0;
        var ePos = r.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            exponent = int.Parse(r[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..ePos];
        }
        var dot = r.IndexOf('.');
        string digits;
        int intLen;
        if (dot >= 0)
        {
            digits = r[..dot] + r[(dot + 1)..];
            intLen = dot;
        }
        else
        {
            digits = r;
            intLen = r.Length;
        }
        // Strip leading zeros (e.g. "0.001" → digits "0001").
        var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0') leading++;
        digits = digits[leading..];
        intLen -= leading;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";

        // value = 0.d1d2…dk × 10^n
        var k = digits.Length;
        var n = intLen + exponent;
        var sb = new StringBuilder();
        if (negative) sb.Append('-');
        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            var e = n - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(e >= 0 ? "+" : "-").Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>JS <c>JSON.stringify</c> escaping of a string, including quotes.</summary>
    public static string QuoteString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                        {
                            sb.Append(c).Append(value[i + 1]);
                            i++;
                        }
                        else
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>JS truthiness of an arbitrary JSON value (flags, plugin payloads).</summary>
    public static bool Truthy(JsonElement? value)
    {
        if (value is not { } v) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Number => v.GetDouble() is var d && d != 0 && !double.IsNaN(d),
            JsonValueKind.String => v.GetString()!.Length > 0,
            _ => true,
        };
    }

    /// <summary>A JSON element holding a JS value, for loosely typed fields (flags, plugin payloads).</summary>
    public static JsonElement Value(bool value) => JsonSerializer.SerializeToElement(value);
    public static JsonElement Value(double value) => JsonSerializer.SerializeToElement(value);
    public static JsonElement Value(string value) => JsonSerializer.SerializeToElement(value);
}
