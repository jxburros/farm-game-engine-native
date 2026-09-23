namespace FarmingRpgMaker.Updates;

/// <summary>Which GitHub releases the app follows.</summary>
public enum UpdateChannel
{
    /// <summary>Only full releases (tags like <c>v1.2.3</c>).</summary>
    Stable,

    /// <summary>Full releases plus pre-releases (tags like <c>v1.3.0-beta.1</c>).</summary>
    Prerelease,
}
