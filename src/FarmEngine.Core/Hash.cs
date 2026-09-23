using System.Globalization;
using System.Text.Json;
using FarmEngine.Json;

namespace FarmEngine.Core;

/// <summary>Stable state hashing for replay/determinism tests (port of hash.ts).</summary>
public static class Hash
{
    /// <summary>JSON with sorted object keys so hashing is order-independent.</summary>
    public static string StableStringify<T>(T value) => StableJson.Stringify(value);

    public static string HashState<T>(T value) => HashText(StableJson.Stringify(value));

    public static string HashState(JsonElement value) => HashText(StableJson.Stringify(value));

    /// <summary>FNV-1a 64-bit (as two 32-bit lanes) over the stable JSON encoding.</summary>
    public static string HashText(string text)
    {
        unchecked
        {
            var h1 = 0x811c9dc5u;
            var h2 = 0xcbf29ce4u;
            foreach (var ch in text)
            {
                uint c = ch;
                h1 = (h1 ^ c) * 0x01000193u;
                h2 = (h2 ^ ((c << 1) | 1)) * 0x01000193u;
            }
            return h1.ToString("x8", CultureInfo.InvariantCulture) + h2.ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}
