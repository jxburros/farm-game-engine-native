using System.Text.Json.Serialization;

namespace FarmEngine.Schemas;

/// <summary>Default window for a standalone exported game.</summary>
public sealed record ExportWindow
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 800;
    public bool Fullscreen { get; init; }
}

/// <summary>
/// Additive project export settings. The web project schema is passthrough, so the web editor
/// can keep this block even before it exposes native export controls.
/// </summary>
public sealed record ExportSettings
{
    public string? Title { get; init; }
    public string? ExecutableName { get; init; }
    public string? Version { get; init; }
    /// <summary>Stable save-folder identity; never derive it from a later project rename.</summary>
    public string GameId { get; init; } = "";
    public string? Author { get; init; }
    public string? Company { get; init; }
    [JsonPropertyName("icon")]
    public string? IconAssetId { get; init; }
    public ExportWindow Window { get; init; } = new();
    /// <summary><c>integer</c> or <c>fit</c>.</summary>
    public string PixelScale { get; init; } = "integer";
    public string? Credits { get; init; }
    public List<string> Targets { get; init; } = ["windows-x64", "linux-x64"];
}
