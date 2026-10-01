using FarmEngine.Interop;

namespace FarmingRpgMaker.App.Tests.Interop;

/// <summary>
/// The Rust library (<c>farm_ffi</c>) is built by FarmEngine.Interop's cargo target and loaded
/// from the application's folder. These tests require it (see <see cref="NativeTests"/>); with
/// <c>FARM_ALLOW_MISSING_NATIVE=1</c> they only check that the managed side degrades to "not
/// available", with the reason, instead of crashing.
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
            Assert.False(string.IsNullOrEmpty(FarmFfi.LoadError));
            var error = Assert.Throws<FarmFfiException>(() => FarmFfi.HashText("{}"));
            Assert.Contains(FarmFfi.LoadError!, error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(FarmFfi.LoadError);
        }
    }

    /// <summary>
    /// The build wrote <c>farm_ffi.status</c> next to the assembly: "built" means the library
    /// must load (a broken cargo step must not turn this test green). Anything else fails unless
    /// missing native code is explicitly allowed.
    /// </summary>
    [Fact]
    public void LibraryLoadsWhenTheBuildProducedIt()
    {
        var statusFile = Path.Combine(AppContext.BaseDirectory, "farm_ffi.status");
        Assert.True(File.Exists(statusFile), "FarmEngine.Interop did not write farm_ffi.status");
        var status = File.ReadAllText(statusFile).Trim();
        Assert.Contains(status, new[] { "built", "skipped", "disabled" });
        if (status == "built")
        {
            Assert.True(FarmFfi.IsAvailable, $"farm_ffi was built but did not load: {FarmFfi.LoadError}");
        }
        else
        {
            Assert.True(NativeTests.MissingAllowed, $"farm_ffi was not built (status '{status}'); set {NativeTests.OptOutVariable}=1 to allow that.");
        }
    }

    [NativeFact]
    public void TheLibrarySpeaksTheAbiTheseBindingsExpect()
    {
        Assert.Null(FarmFfi.LoadError);
        Assert.Equal(2u, FarmFfi.AbiVersion);
        // Loaded from the application's folder, not from wherever the OS would search.
        var loaded = System.Diagnostics.Process.GetCurrentProcess().Modules
            .Cast<System.Diagnostics.ProcessModule>()
            .Select(module => module.FileName)
            .Where(path => Path.GetFileNameWithoutExtension(path).Contains("farm_ffi", StringComparison.Ordinal))
            .ToList();
        Assert.Contains(loaded, path => Path.GetDirectoryName(path) == Path.GetDirectoryName(typeof(FarmFfi).Assembly.Location));
    }

    [NativeFact]
    public void HashesTextLikeTheTypeScriptEngine()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", FarmFfi.Version);
        Assert.Equal("5465b8257807bf56", FarmFfi.HashText("{}"));
        Assert.Equal("741638a538a6be56", FarmFfi.HashText("[]"));
        Assert.Equal("77074ba4d9fff516", FarmFfi.HashText("null"));
        Assert.Matches("^[0-9a-f]{16}$", FarmFfi.HashText("{\"a\":[1,2.5,\"x\"],\"é\":\"農場🌾\"}"));
        // An empty array pins to a null pointer; Rust accepts it with length 0.
        Assert.Matches("^[0-9a-f]{16}$", FarmFfi.HashText(""));
    }
}
