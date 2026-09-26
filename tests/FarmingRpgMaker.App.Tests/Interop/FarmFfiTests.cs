using FarmEngine.Core;
using FarmEngine.Interop;

namespace FarmingRpgMaker.App.Tests.Interop;

/// <summary>
/// The Rust library (<c>farm_ffi</c>) is built by FarmEngine.Interop's cargo target. When a Rust
/// toolchain is installed these tests exercise the P/Invoke path; without one they only check
/// that the managed side degrades to "not available" instead of crashing.
/// </summary>
public sealed class FarmFfiTests
{
    [Fact]
    public void ProbeNeverThrows()
    {
        _ = FarmFfi.IsAvailable;
        if (!FarmFfi.IsAvailable)
        {
            Assert.Null(FarmFfi.Version);
            Assert.Throws<FarmFfiException>(() => FarmFfi.HashText("{}"));
        }
    }

    [Fact]
    public void RustAndCSharpAgreeOnTheStateHash()
    {
        if (!FarmFfi.IsAvailable)
        {
            return;
        }

        Assert.Matches(@"^\d+\.\d+\.\d+", FarmFfi.Version);
        foreach (var text in new[] { "{}", "[]", "null", "{\"a\":[1,2.5,\"x\"],\"é\":\"農場🌾\"}" })
        {
            Assert.Equal(Hash.HashText(text), FarmFfi.HashText(text));
        }
    }
}
