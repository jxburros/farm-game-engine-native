using FarmEngine.Json;

namespace FarmEngine.Core.Tests;

public class JsSemanticsTests
{
    [Theory]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(123.456, "123.456")]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    [InlineData(-5, "-5")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(1.23e22, "1.23e+22")]
    [InlineData(100, "100")]
    [InlineData(4.5, "4.5")]
    [InlineData(-0.0, "0")]
    public void NumberFormattingMatchesJavaScript(double value, string expected) =>
        Assert.Equal(expected, Js.Num(value));

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -2)]
    [InlineData(0.49999999999999994, 0)]
    [InlineData(1.4, 1)]
    public void RoundMatchesJavaScript(double value, double expected) =>
        Assert.Equal(expected, Js.Round(value));

    [Fact]
    public void StableStringifySortsKeysByCodeUnit()
    {
        var element = System.Text.Json.JsonDocument.Parse("""{"b":1,"B":2,"a":[{"y":1,"x":"\u0001é"}]}""").RootElement;
        Assert.Equal("{\"B\":2,\"a\":[{\"x\":\"\\u0001é\",\"y\":1}],\"b\":1}", StableJson.Stringify(element));
    }

    [Fact]
    public void StableSortKeepsEqualElementsInOrder()
    {
        var sorted = Js.StableSort(new[] { ("a", 1), ("b", 0), ("c", 1), ("d", 0) }, (x, y) => x.Item2.CompareTo(y.Item2));
        Assert.Equal(new[] { "b", "d", "a", "c" }, sorted.Select(x => x.Item1));
    }
}
