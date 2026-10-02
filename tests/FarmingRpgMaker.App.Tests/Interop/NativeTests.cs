using FarmEngine.Interop;

namespace FarmingRpgMaker.App.Tests.Interop;

/// <summary>
/// Tests that need the Rust library (<c>farm_ffi</c>). A missing library fails them, so a broken
/// cargo step or a wrong <c>CargoProfile</c> path can never turn them green; set
/// <c>FARM_ALLOW_MISSING_NATIVE=1</c> to build and test without a Rust toolchain, which reports
/// them as skipped instead.
/// </summary>
internal static class NativeTests
{
    public const string OptOutVariable = "FARM_ALLOW_MISSING_NATIVE";

    /// <summary>Whether missing native code is allowed (the opt-out variable is set to 1).</summary>
    public static bool MissingAllowed => Environment.GetEnvironmentVariable(OptOutVariable) == "1";

    /// <summary>Why native tests are skipped, or null when they run.</summary>
    public static string? SkipReason =>
        FarmFfi.IsAvailable || !MissingAllowed ? null : $"farm_ffi is not available ({FarmFfi.LoadError}) and {OptOutVariable}=1 allows that.";

    /// <summary>
    /// For tests that check managed code first and then native code: true when the library is
    /// available, false when it is missing and that is allowed; otherwise the test fails here
    /// with the reason the library did not load.
    /// </summary>
    public static bool Available()
    {
        if (FarmFfi.IsAvailable)
        {
            return true;
        }

        Assert.True(MissingAllowed, $"The Rust library is required: {FarmFfi.LoadError} (set {OptOutVariable}=1 to skip native tests).");
        return false;
    }
}

/// <summary>A <see cref="FactAttribute"/> for tests that need the Rust library (see <see cref="NativeTests"/>).</summary>
public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (NativeTests.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
