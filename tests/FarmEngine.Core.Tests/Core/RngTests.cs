using FarmEngine.Core;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Core;

/// <summary>Port of engine-core/src/rng.test.ts — seeded RNG (xoshiro128**).</summary>
public class RngTests
{
    private static List<double> Draw(Rng rng, int count) => Enumerable.Range(0, count).Select(_ => rng.Float()).ToList();

    [Fact]
    public void IsDeterministicForTheSameSeed()
    {
        var a = new Rng(RngMath.CreateRngState("test-seed"));
        var b = new Rng(RngMath.CreateRngState("test-seed"));
        Assert.Equal(Draw(a, 100), Draw(b, 100));
    }

    [Fact]
    public void DiffersAcrossSeeds()
    {
        var a = new Rng(RngMath.CreateRngState("seed-a"));
        var b = new Rng(RngMath.CreateRngState("seed-b"));
        Assert.NotEqual(Draw(a, 10), Draw(b, 10));
    }

    [Fact]
    public void ResumesDeterministicallyFromSerializedState()
    {
        var rng = new Rng(RngMath.CreateRngState(42));
        for (var i = 0; i < 17; i++) rng.Float();
        var snapshot = JsonDefaults.Deserialize<RngState>(JsonDefaults.Serialize(rng.State))!;

        var resumed = new Rng(snapshot);
        var continued = Draw(rng, 20);
        var replayed = Draw(resumed, 20);
        Assert.Equal(continued, replayed);
    }

    [Fact]
    public void ProducesFloatsInZeroToOne()
    {
        var rng = new Rng(RngMath.CreateRngState("range"));
        for (var i = 0; i < 1000; i++)
        {
            var value = rng.Float();
            Assert.True(value >= 0);
            Assert.True(value < 1);
        }
    }

    [Fact]
    public void ProducesIntsInTheInclusiveRange()
    {
        var rng = new Rng(RngMath.CreateRngState("ints"));
        var seen = new HashSet<double>();
        for (var i = 0; i < 1000; i++)
        {
            var value = rng.Int(1, 3);
            seen.Add(value);
            Assert.True(value >= 1);
            Assert.True(value <= 3);
        }
        Assert.Equal(new HashSet<double> { 1, 2, 3 }, seen);
    }

    [Fact]
    public void ThreadsFunctionalStateWithoutMutation()
    {
        var s0 = RngMath.CreateRngState("functional");
        var first = RngMath.NextFloat(s0);
        var second = RngMath.NextFloat(s0);
        Assert.Equal(first.Value, second.Value); // same input state, same draw
        Assert.NotEqual(first.Value, RngMath.NextFloat(first.State).Value);
    }

    [Fact]
    public void NextIntIsInclusiveAtBothEnds()
    {
        var state = RngMath.CreateRngState("bounds");
        var values = new HashSet<double>();
        for (var i = 0; i < 500; i++)
        {
            var result = RngMath.NextInt(state, 0, 1);
            state = result.State;
            values.Add(result.Value);
        }
        Assert.Equal(new HashSet<double> { 0, 1 }, values);
    }

    [Fact]
    public void WeightedPicksRespectZeroAndEmptyTables()
    {
        var rng = new Rng(RngMath.CreateRngState("weights"));
        Assert.Equal(-1, rng.Weighted([]));
        Assert.Equal(-1, rng.Weighted([0, 0]));
        Assert.Contains(rng.Weighted([1, 1]), new[] { 0, 1 });
        Assert.Equal(1, rng.Weighted([0, 5, 0]));
    }

    [Fact]
    public void HashStringToU32IsStable()
    {
        Assert.Equal(RngMath.HashStringToU32("abc"), RngMath.HashStringToU32("abc"));
        Assert.NotEqual(RngMath.HashStringToU32("abc"), RngMath.HashStringToU32("abd"));
    }

    [Fact]
    public void U32StreamMatchesItselfAfterJsonRoundTripMidStream()
    {
        var state = RngMath.CreateRngState("roundtrip");
        for (var i = 0; i < 5; i++) state = RngMath.NextU32(state).State;
        var clone = JsonDefaults.Deserialize<RngState>(JsonDefaults.Serialize(state))!;
        Assert.Equal(RngMath.NextU32(state).Value, RngMath.NextU32(clone).Value);
    }

    /// <summary>
    /// Not in rng.test.ts: exact values produced by the TS rng.ts (run under
    /// node) so the C# stream is pinned to the reference implementation.
    /// </summary>
    [Fact]
    public void MatchesTypeScriptReferenceStream()
    {
        Assert.Equal(new uint[] { 440920331, 524808426, 2166136261, 2351850323, 4058363231 },
            new[] { "abc", "abd", "", "m9", "héllo" }.Select(RngMath.HashStringToU32).ToArray());
        Assert.Equal(new uint[] { 246976664, 2367717196, 2878732604, 3588527072 }, RngMath.CreateRngState("test-seed").S);
        Assert.Equal(new uint[] { 551831576, 144025891, 322543647, 3034809370 }, RngMath.CreateRngState(42).S);
        Assert.Equal(new uint[] { 1684164658, 3653269916, 2939563536, 2141751570 }, RngMath.CreateRngState(0).S);
        Assert.Equal(new uint[] { 3950124170, 4293442868, 1302505678, 2762329221 }, RngMath.CreateRngState(-1).S);
        Assert.Equal(new uint[] { 3838393609, 4210744933, 801465066, 1648156487 }, RngMath.CreateRngState(4294967296 + 7).S);
        Assert.Equal(new uint[] { 2377132070, 4151384117, 1437520376, 701051360 }, RngMath.CreateRngState(3.7).S);

        var rng = new Rng(RngMath.CreateRngState("m9"));
        Assert.Equal(
            new[] { 0.8431130840908736, 0.3261971096508205, 0.8883587140589952, 0.31222702329978347, 0.7136839465238154, 0.9584276955574751, 0.8464206156786531, 0.20873926975764334 },
            Draw(rng, 8));
        Assert.Equal(new double[] { 1, 2, 3, 3, 3, 5, 4, 6 }, Enumerable.Range(0, 8).Select(_ => rng.Int(1, 6)).ToList());
        Assert.Equal(new[] { 2, 1, 0 }, new[] { rng.Weighted([1, 2, 3]), rng.Weighted([0, 5, 0]), rng.Weighted([0.5, 0.25]) });
        Assert.Equal(new uint[] { 1601829940, 528883471, 1938317341, 1477773383 }, rng.State.S);

        var state = RngMath.CreateRngState("roundtrip");
        for (var i = 0; i < 5; i++) state = RngMath.NextU32(state).State;
        Assert.Equal(1409732268u, RngMath.NextU32(state).Value);
    }
}
