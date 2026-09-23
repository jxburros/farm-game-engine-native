using System.Reflection;

namespace FarmingRpgMaker.Updates;

/// <summary>
/// Version stamped into the build (<c>-p:Version=X.Y.Z</c> in the release workflow,
/// <c>0.1.0-dev</c> locally). <c>-p:Version</c> is a global MSBuild property, so this
/// library carries the same version as the app executable.
/// </summary>
public static class AppVersion
{
    /// <summary>Informational version without the <c>+commit</c> suffix.</summary>
    public static string Current { get; } = Compute();

    private static string Compute()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
