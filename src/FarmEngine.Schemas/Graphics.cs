namespace FarmEngine.Schemas;

// Port of packages/engine-schemas/src/graphics.ts.

/// <summary>Rectangles are in source pixels; display size remains independent of art resolution.</summary>
public sealed record ArtFrame
{
    public string? AssetId { get; init; }
    /// <summary>int, nonnegative.</summary>
    public double X { get; init; }
    /// <summary>int, nonnegative.</summary>
    public double Y { get; init; }
    /// <summary>int, positive.</summary>
    public double Width { get; init; }
    /// <summary>int, positive.</summary>
    public double Height { get; init; }
    /// <summary>int, positive.</summary>
    public double Ticks { get; init; } = 6;
}

public sealed record AnimationClip
{
    /// <summary>min length 1.</summary>
    public string Name { get; init; } = "";
    public bool Loop { get; init; } = true;
    /// <summary>1..1024 frames.</summary>
    public List<ArtFrame> Frames { get; init; } = [];
}

public sealed record VisualRef
{
    public string AssetId { get; init; } = "";
    public string? Animation { get; init; }
    public ArtFrame? Frame { get; init; }
}

public sealed record GraphicsSettings
{
    public bool PixelArt { get; init; } = true;
}
