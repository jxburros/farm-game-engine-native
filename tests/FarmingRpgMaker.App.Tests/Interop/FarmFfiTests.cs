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

    /// <summary>
    /// The build wrote <c>farm_ffi.status</c> next to the assembly: "built" means the library
    /// must load (a broken cargo step must not turn this test green), "skipped" means no Rust
    /// toolchain was available.
    /// </summary>
    [Fact]
    public void LibraryLoadsWhenTheBuildProducedIt()
    {
        var statusFile = Path.Combine(AppContext.BaseDirectory, "farm_ffi.status");
        Assert.True(File.Exists(statusFile), "FarmEngine.Interop did not write farm_ffi.status");
        var status = File.ReadAllText(statusFile).Trim();
        Assert.Contains(status, new[] { "built", "skipped" });
        if (status == "built")
        {
            Assert.True(FarmFfi.IsAvailable, "farm_ffi was built but the P/Invoke probe failed to load it");
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
