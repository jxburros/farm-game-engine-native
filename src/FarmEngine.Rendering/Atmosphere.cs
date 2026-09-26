using SkiaSharp;

namespace FarmEngine.Rendering;

/// <summary>
/// Pure helpers behind the renderer's atmosphere: the day/night multiply tint, season
/// tints for foliage, and the deterministic weather particles (hash of tick + index; no
/// <see cref="Random"/>, so a frame is a pure function of the snapshot).
/// </summary>
public static class Atmosphere
{
    /// <summary>(minute of day, RGB multiplier) keyframes; linear between them.</summary>
    private static readonly (double Minute, float R, float G, float B)[] Daylight =
    [
        (0, 0.42f, 0.50f, 0.86f),      // deep night: cool blue
        (5 * 60, 0.42f, 0.50f, 0.86f),
        (6 * 60 + 30, 1.00f, 0.82f, 0.66f), // dawn: warm
        (8 * 60, 1f, 1f, 1f),          // full day
        (17 * 60, 1f, 1f, 1f),
        (18 * 60 + 40, 1.00f, 0.74f, 0.56f), // dusk: warm
        (20 * 60 + 30, 0.42f, 0.50f, 0.86f),
        (24 * 60, 0.42f, 0.50f, 0.86f),
    ];

    /// <summary>
    /// The multiply tint for a minute of day: white at midday (no tint), warm around dawn
    /// and dusk, cool blue at night.
    /// </summary>
    public static SKColor DaylightTint(double timeMinutes)
    {
        var minute = ((timeMinutes % 1440) + 1440) % 1440;
        for (var i = 1; i < Daylight.Length; i++)
        {
            var (m1, r1, g1, b1) = Daylight[i];
            if (minute > m1)
            {
                continue;
            }

            var (m0, r0, g0, b0) = Daylight[i - 1];
            var t = m1 == m0 ? 0 : (float)((minute - m0) / (m1 - m0));
            return new SKColor(Channel(r0, r1, t), Channel(g0, g1, t), Channel(b0, b1, t));
        }

        return SKColors.White;
    }

    /// <summary>True when <see cref="DaylightTint"/> changes nothing (midday).</summary>
    public static bool IsNeutral(SKColor tint) => tint.Red == 255 && tint.Green == 255 && tint.Blue == 255;

    /// <summary>
    /// Multiply tint for foliage (trees, weeds) per season: autumn ochre, winter frost; null
    /// for spring/summer and unknown seasons. Grass tiles have their own seasonal art.
    /// </summary>
    public static SKColor? FoliageTint(string? season) => season switch
    {
        "fall" or "autumn" => new SKColor(0xF0, 0xB8, 0x70),
        "winter" => new SKColor(0xB8, 0xD0, 0xE8),
        _ => null,
    };

    /// <summary>Overlay kind for a weather id: <c>rain</c>, <c>snow</c> or null.</summary>
    public static string? WeatherOverlay(string? weatherId, string? overlayHint)
    {
        if (overlayHint is "rain" or "snow")
        {
            return overlayHint;
        }

        var id = weatherId?.ToLowerInvariant();
        if (id is null)
        {
            return null;
        }

        if (id.Contains("snow", StringComparison.Ordinal) || id.Contains("blizzard", StringComparison.Ordinal))
        {
            return "snow";
        }

        if (id.Contains("rain", StringComparison.Ordinal) || id.Contains("storm", StringComparison.Ordinal))
        {
            return "rain";
        }

        return null;
    }

    /// <summary>Deterministic 32-bit mix (same inputs ⇒ same output on every platform).</summary>
    public static uint Hash(uint a, uint b)
    {
        var h = (a * 0x9E3779B1u) ^ (b + 0x7F4A7C15u);
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h;
    }

    /// <summary>Uniform [0, 1) from a hash.</summary>
    public static double Unit(uint a, uint b) => Hash(a, b) / 4294967296.0;

    /// <summary>Number of weather particles for a viewport (about one per 40×40 px).</summary>
    public static int ParticleCount(double viewWidth, double viewHeight) =>
        (int)Math.Clamp(Math.Round(viewWidth * viewHeight / 1600), 8, 600);

    private static byte Channel(float a, float b, float t) => (byte)Math.Round(255 * (a + ((b - a) * t)));
}
