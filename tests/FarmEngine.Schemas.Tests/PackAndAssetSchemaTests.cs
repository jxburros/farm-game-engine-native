using System.Text.Json;
using FarmEngine.Json;

namespace FarmEngine.Schemas.Tests;

/// <summary>
/// The schema cases of m5-systems.test.ts (pack validation, engine compatibility
/// ranges) and m7-polish.test.ts (sprite sheet slicing defaults).
/// </summary>
public class PackAndAssetSchemaTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void RejectsMalformedPacksWithActionablePathsNeverThrows()
    {
        Assert.False(PacksSchema.ValidateContentPack(Parse("null")).Ok);
        Assert.False(PacksSchema.ValidateContentPack(Parse("\"junk\"")).Ok);
        var missing = PacksSchema.ValidateContentPack(Parse("""{ "manifest": { "id": "BAD ID!", "version": "1.0.0" } }"""));
        Assert.False(missing.Ok);
        Assert.Contains(missing.Errors, e => e.Contains("manifest.id"));
        Assert.Contains(missing.Errors, e => e.Contains("manifest.name"));
    }

    [Fact]
    public void ChecksEngineCompatibilityRanges()
    {
        Assert.True(PacksSchema.IsEngineCompatible("*", "0.5.0"));
        Assert.True(PacksSchema.IsEngineCompatible("0.5.0", "0.5.0"));
        Assert.True(PacksSchema.IsEngineCompatible(">=0.4.0", "0.5.0"));
        Assert.False(PacksSchema.IsEngineCompatible(">=0.6.0", "0.5.0"));
        Assert.True(PacksSchema.IsEngineCompatible("^0.5.0", "0.5.3"));
        Assert.False(PacksSchema.IsEngineCompatible("^1.0.0", "0.5.0"));
    }

    [Fact]
    public void SpriteSheetSchemaAppliesSlicingDefaults()
    {
        var sheet = JsonDefaults.Deserialize<SpriteSheet>("""{ "frameWidth": 32, "frameHeight": 32, "frames": 4 }""")!;
        Assert.Equal(6, sheet.TicksPerFrame);
        Assert.True(sheet.Directional);
    }

    [Fact]
    public void AssetsWithoutSheetMetadataStayStatic()
    {
        var asset = JsonDefaults.Deserialize<CustomAsset>("""{ "id": "a1", "name": "Hero", "type": "player", "dataUrl": "data:image/png;base64,x" }""")!;
        // No throw, no sheet: the renderer treats it as a plain image.
        Assert.Null(asset.Sheet);
        Assert.DoesNotContain("\"sheet\"", JsonDefaults.Serialize(asset));
    }
}
