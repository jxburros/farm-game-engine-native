using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;

namespace FarmEngine.Rendering;

/// <summary>
/// Parses CSS color strings the way a canvas <c>fillStyle</c> would for the forms game content
/// uses: <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c> (alpha LAST, unlike
/// <see cref="SKColor.Parse"/>), <c>rgb()/rgba()</c> and a few named colors.
/// Unparseable input yields <see cref="Fallback"/>.
/// </summary>
public static class CssColor
{
    public static readonly SKColor Fallback = new(0x80, 0x80, 0x80);

    private static readonly ConcurrentDictionary<string, SKColor> Cache = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, SKColor> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = SKColors.Black,
        ["white"] = SKColors.White,
        ["red"] = SKColors.Red,
        ["green"] = new SKColor(0, 128, 0),
        ["blue"] = SKColors.Blue,
        ["yellow"] = SKColors.Yellow,
        ["orange"] = SKColors.Orange,
        ["purple"] = new SKColor(128, 0, 128),
        ["brown"] = SKColors.Brown,
        ["gray"] = SKColors.Gray,
        ["grey"] = SKColors.Gray,
        ["pink"] = SKColors.Pink,
        ["gold"] = SKColors.Gold,
        ["transparent"] = SKColors.Transparent,
    };

    public static SKColor Parse(string? css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return Fallback;
        }

        return Cache.GetOrAdd(css, static value => TryParse(value, out var color) ? color : Fallback);
    }

    public static bool TryParse(string css, out SKColor color)
    {
        color = Fallback;
        var text = css.Trim();
        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (!hex.All(Uri.IsHexDigit))
            {
                return false;
            }

            switch (hex.Length)
            {
                case 3:
                case 4:
                {
                    var r = Nibble(hex[0]);
                    var g = Nibble(hex[1]);
                    var b = Nibble(hex[2]);
                    var a = hex.Length == 4 ? Nibble(hex[3]) : (byte)0xFF;
                    color = new SKColor(r, g, b, a);
                    return true;
                }

                case 6:
                case 8:
                {
                    var r = Byte(hex, 0);
                    var g = Byte(hex, 2);
                    var b = Byte(hex, 4);
                    var a = hex.Length == 8 ? Byte(hex, 6) : (byte)0xFF;
                    color = new SKColor(r, g, b, a);
                    return true;
                }

                default:
                    return false;
            }
        }

        if (Named.TryGetValue(text, out var named))
        {
            color = named;
            return true;
        }

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var open = text.IndexOf('(', StringComparison.Ordinal);
            var close = text.LastIndexOf(')');
            if (open < 0 || close <= open)
            {
                return false;
            }

            var parts = text[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                return false;
            }

            if (!TryChannel(parts[0], out var r) || !TryChannel(parts[1], out var g) || !TryChannel(parts[2], out var b))
            {
                return false;
            }

            var alpha = 1.0;
            if (parts.Length >= 4 && !double.TryParse(parts[3].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out alpha))
            {
                return false;
            }

            if (parts.Length >= 4 && parts[3].EndsWith('%'))
            {
                alpha /= 100;
            }

            color = new SKColor(r, g, b, (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255));
            return true;
        }

        return false;
    }

    /// <summary>Multiplies the color's alpha by <paramref name="opacity"/> (canvas <c>globalAlpha</c>).</summary>
    public static SKColor WithOpacity(SKColor color, double opacity) =>
        color.WithAlpha((byte)Math.Clamp(Math.Round(color.Alpha * opacity), 0, 255));

    private static byte Nibble(char c)
    {
        var v = Convert.ToByte(c.ToString(), 16);
        return (byte)((v << 4) | v);
    }

    private static byte Byte(string hex, int start) =>
        byte.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static bool TryChannel(string text, out byte value)
    {
        value = 0;
        var percent = text.EndsWith('%');
        if (!double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        value = (byte)Math.Clamp(Math.Round(percent ? number * 2.55 : number), 0, 255);
        return true;
    }
}
