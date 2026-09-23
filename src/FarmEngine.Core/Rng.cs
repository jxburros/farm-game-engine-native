using FarmEngine.Schemas;

namespace FarmEngine.Core;

/// <summary>
/// Seeded, serializable PRNG — the single entry point for all randomness in
/// the simulation (port of engine-core/src/rng.ts). Algorithm: xoshiro128**
/// (Blackman &amp; Vigna), 128-bit state. State lives in <c>GameState.Rng</c>
/// so a saved game resumes its random stream deterministically.
/// All arithmetic is unchecked <c>uint</c>, matching <c>Math.imul</c> and <c>&gt;&gt;&gt; 0</c>.
/// </summary>
public static class RngMath
{
    private const double U32 = 4294967296.0;

    private static uint Rotl(uint x, int k) => (x << k) | (x >> (32 - k));

    /// <summary>FNV-1a hash of a string to a u32, for string seeds.</summary>
    public static uint HashStringToU32(string input)
    {
        unchecked
        {
            var hash = 0x811c9dc5u;
            foreach (var c in input)
            {
                hash ^= c;
                hash *= 0x01000193u;
            }
            return hash;
        }
    }

    /// <summary>JS <c>seed &gt;&gt;&gt; 0</c> for a numeric seed (ToUint32).</summary>
    public static uint ToUint32(double seed)
    {
        if (!double.IsFinite(seed)) return 0;
        var t = Math.Truncate(seed);
        var m = t % U32;
        if (m < 0) m += U32;
        return (uint)m;
    }

    public static RngState CreateRngState(string seed) => CreateFromU32(HashStringToU32(seed));

    public static RngState CreateRngState(double seed) => CreateFromU32(ToUint32(seed));

    private static RngState CreateFromU32(uint seedU32)
    {
        unchecked
        {
            var a = seedU32;
            uint Next()
            {
                a += 0x9e3779b9u;
                var t = a;
                t = (t ^ (t >> 16)) * 0x21f0aaadu;
                t = (t ^ (t >> 15)) * 0x735a2d97u;
                return t ^ (t >> 15);
            }
            var s = new[] { Next(), Next(), Next(), Next() };
            // xoshiro must not be seeded with all zeros.
            if (s.All(v => v == 0)) s[0] = 1;
            return new RngState { Algorithm = "xoshiro128ss", S = s };
        }
    }

    /// <summary>Functional draw: returns the value plus the next state.</summary>
    public static (uint Value, RngState State) NextU32(RngState state)
    {
        unchecked
        {
            var (s0, s1, s2, s3) = (state.S[0], state.S[1], state.S[2], state.S[3]);
            var result = Rotl(s1 * 5, 7) * 9;
            var t = s1 << 9;
            var n2 = s2 ^ s0;
            var n3 = s3 ^ s1;
            var n1 = s1 ^ n2;
            var n0 = s0 ^ n3;
            n2 ^= t;
            n3 = Rotl(n3, 11);
            return (result, new RngState { Algorithm = "xoshiro128ss", S = [n0, n1, n2, n3] });
        }
    }

    /// <summary>Uniform float in [0, 1).</summary>
    public static (double Value, RngState State) NextFloat(RngState state)
    {
        var (value, next) = NextU32(state);
        return (value / U32, next);
    }

    /// <summary>Uniform integer in [min, max] inclusive.</summary>
    public static (double Value, RngState State) NextInt(RngState state, double min, double max)
    {
        var (value, next) = NextFloat(state);
        return (Math.Floor(value * (max - min + 1)) + min, next);
    }
}

/// <summary>Minimal random-source interface consumed by game math.</summary>
public interface IRandomSource
{
    double Float();
    double Int(double min, double max);
}

/// <summary>
/// Mutable convenience wrapper for command handlers: draws update
/// <see cref="State"/> in place; the handler stores the final state back into
/// GameState once.
/// </summary>
public sealed class Rng(RngState state) : IRandomSource
{
    public RngState State { get; set; } = state;

    public double Float()
    {
        var (value, next) = RngMath.NextFloat(State);
        State = next;
        return value;
    }

    public double Int(double min, double max)
    {
        var (value, next) = RngMath.NextInt(State, min, max);
        State = next;
        return value;
    }

    /// <summary>Pick an index from weighted entries. Returns -1 for an empty/zero table.</summary>
    public int Weighted(IReadOnlyList<double> weights)
    {
        var total = 0.0;
        foreach (var w in weights) total += w;
        if (total <= 0) return -1;
        var roll = Float() * total;
        for (var i = 0; i < weights.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return i;
        }
        return weights.Count - 1;
    }
}
