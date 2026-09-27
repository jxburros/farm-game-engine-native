using FarmEngine.Content;
using FarmEngine.Core;
using FarmEngine.Rendering;
using FarmEngine.Schemas;
using SkiaSharp;

namespace FarmingRpgMaker.App.Tests.Rendering;

/// <summary>The embedded pixel-art pack: manifest integrity and content-id coverage.</summary>
public sealed class BuiltinArtTests
{
    [Fact]
    public void ManifestLoadsAndEveryEntryLiesInsideItsSheet()
    {
        var art = BuiltinArt.Default;
        Assert.Equal(32, art.TileSize);
        Assert.NotEmpty(art.Entries);
        Assert.Equal(48, art.Manifest.Palette.Count);

        var sheets = new Dictionary<string, SKImage>(StringComparer.Ordinal);
        foreach (var sheet in art.Manifest.Sheets)
        {
            var bytes = art.SheetBytes(sheet.File);
            Assert.True(bytes is { Length: > 0 }, $"sheet {sheet.File} is missing");
            var image = SKImage.FromEncodedData(bytes);
            Assert.NotNull(image);
            Assert.Equal((sheet.W, sheet.H), (image.Width, image.Height));
            sheets[sheet.File] = image;
        }

        foreach (var entry in art.Entries.Values)
        {
            Assert.True(sheets.TryGetValue(entry.File, out var sheet), $"{entry.Name}: unknown sheet {entry.File}");
            Assert.True(entry.W > 0 && entry.H > 0 && entry.Frames > 0 && entry.Rows > 0, $"{entry.Name}: empty cell grid");
            Assert.True(entry.X + (entry.W * entry.Frames) <= sheet!.Width, $"{entry.Name} overflows {entry.File} horizontally");
            Assert.True(entry.Y + (entry.H * entry.Rows) <= sheet.Height, $"{entry.Name} overflows {entry.File} vertically");
            Assert.Equal(entry.Directional, entry.Rows == 4);
            if (entry.Animated)
            {
                Assert.True(entry.TicksPerFrame > 0);
            }
        }

        // Every sheet decodes through the image store under its builtin:// source.
        using var store = new ImageStore();
        foreach (var sheet in art.Manifest.Sheets)
        {
            Assert.NotNull(store.Get(BuiltinArt.SheetUrl(sheet.File)));
        }

        foreach (var image in sheets.Values)
        {
            image.Dispose();
        }
    }

    [Fact]
    public void SpritesUseOnlyThePalettePlusTransparency()
    {
        var art = BuiltinArt.Default;
        var palette = art.Manifest.Palette.Select(hex => CssColor.Parse(hex)).ToHashSet();
        foreach (var sheet in art.Manifest.Sheets)
        {
            using var bitmap = SKBitmap.Decode(art.SheetBytes(sheet.File));
            var offPalette = 0;
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.Alpha == 0)
                    {
                        continue;
                    }

                    Assert.Equal(255, pixel.Alpha);
                    if (!palette.Contains(pixel.WithAlpha(255)))
                    {
                        offPalette++;
                    }
                }
            }

            Assert.True(offPalette == 0, $"{sheet.File}: {offPalette} pixels outside the shared palette");
        }
    }

    [Fact]
    public void EveryBuiltInContentIdResolvesToASprite()
    {
        var art = BuiltinArt.Default;
        var project = DefaultContent.CreateInitialProject(0);
        var content = EngineState.CreateContentFromProject(project);

        foreach (var type in TileTypes.All)
        {
            Assert.NotNull(art.Tile(type, null, 0, 0, 0));
            foreach (var season in ContentBuiltin.SeasonOrder)
            {
                Assert.NotNull(art.Tile(type, season, 3, 7, 12));
            }
        }

        Assert.NotNull(art.Soil(false, 0, 0));
        Assert.NotNull(art.Soil(true, 0, 0));
        Assert.NotNull(art.FertilizedMarker());
        Assert.NotNull(art.Ladder());
        Assert.NotNull(art.ReadyMarker());

        foreach (var crop in content.Crops.Values)
        {
            Assert.True(art.Entries.ContainsKey($"crop-{crop.Id}"), $"no dedicated art for crop {crop.Id}");
            for (var stage = 0; stage < crop.Stages; stage++)
            {
                Assert.NotNull(art.Crop(crop.Id, stage, (int)crop.Stages, false));
            }

            Assert.NotNull(art.Crop(crop.Id, 0, (int)crop.Stages, true));
        }

        foreach (var node in content.NodeTypes)
        {
            Assert.True(art.Entries.ContainsKey(node.Id), $"no dedicated art for node {node.Id}");
            Assert.NotNull(art.Node(node.Id, 1, 2));
        }

        foreach (var machine in content.MachineTypes)
        {
            Assert.True(art.Entries.ContainsKey(machine.Id), $"no dedicated art for machine {machine.Id}");
            Assert.NotNull(art.Machine(machine.Id, false));
            Assert.NotNull(art.Machine(machine.Id, true));
        }

        foreach (var species in content.AnimalSpecies)
        {
            Assert.True(art.Entries.ContainsKey(species.Id), $"no dedicated art for animal {species.Id}");
            foreach (var direction in Directions.All)
            {
                Assert.NotNull(art.Animal(species.Id, direction, 0, false));
            }
        }

        var appearances = content.Npcs.Select(n => n.Appearance).Concat(Templates.CreateQuestRpgProject(0).Npcs.Select(n => n.Appearance)).Distinct().ToList();
        Assert.Contains("farmer", appearances);
        Assert.Contains("merchant", appearances);
        foreach (var appearance in appearances)
        {
            Assert.True(art.Entries.ContainsKey($"npc-{appearance}"), $"no dedicated art for appearance {appearance}");
            foreach (var direction in Directions.All)
            {
                Assert.NotNull(art.Npc(appearance, direction, 5, true));
            }
        }

        foreach (var direction in Directions.All)
        {
            Assert.NotNull(art.Player(direction, 0, false));
        }

        foreach (var itemType in content.Items.Select(i => i.Type).Distinct())
        {
            Assert.NotNull(art.Item(itemType));
        }
    }

    [Fact]
    public void UnknownIdsFallBackToGenericSprites()
    {
        var art = BuiltinArt.Default;
        Assert.Equal("crop-generic", CropName(art, "mod-dragonfruit"));
        Assert.Equal("node-rock", art.NodeEntry("mod-strange-thing")!.Name);
        Assert.Equal("node-tree", art.NodeEntry("mod-oak-tree")!.Name);
        Assert.Equal("node-weeds", art.NodeEntry("mod-bramble-bush")!.Name);
        Assert.Equal("machine-generic", art.MachineEntry("mod-quantum-box")!.Name);
        Assert.Equal("machine-furnace", art.MachineEntry("mod-blast-furnace")!.Name);
        Assert.Equal("animal-chicken", art.AnimalEntry("mod-axolotl")!.Name);
        Assert.Equal("animal-cow", art.AnimalEntry("mod-sheep")!.Name);
        Assert.Equal("npc-villager", art.NpcEntry("wizard")!.Name);
        Assert.Equal("npc-villager", art.NpcEntry("")!.Name);
        Assert.Equal("npc-merchant", art.NpcEntry("shopkeeper")!.Name);
        Assert.NotNull(art.Item("mystery"));
        Assert.Null(art.Tile("lava", null, 0, 0, 0));
    }

    [Fact]
    public void WalkCyclesPickFramesFromTheTickAndIdleWhenStill()
    {
        var art = BuiltinArt.Default;
        var entry = art.Entries["char-player"];
        Assert.True(entry.Directional);
        Assert.Equal(4, entry.Frames);
        var ticks = entry.TicksPerFrame!.Value;

        var idle = art.Player("left", ticks * 3, false)!;
        Assert.Equal((entry.IdleFrame ?? 0, 1), ((int)idle.Frame, (int)idle.Row));
        var walking = art.Player("up", ticks * 3, true)!;
        Assert.Equal((3, 3), ((int)walking.Frame, (int)walking.Row));
        Assert.Equal(0, (int)art.Player("down", ticks * 4, true)!.Frame);

        // Frames are explicit source rectangles on the sheet.
        Assert.Equal(entry.X + (3 * entry.W), walking.SourceX);
        Assert.Equal(entry.Y + (3 * entry.H), walking.SourceY);
    }

    [Fact]
    public void VariantsComeFromTheTileCoordinateAndWaterFromTheTick()
    {
        var art = BuiltinArt.Default;
        var grass = art.Entries["tile-grass"];
        Assert.True(grass.Frames > 1);
        var columns = Enumerable.Range(0, 40).Select(i => BuiltinArt.Column(grass, i, i * 7, 0)).Distinct().Count();
        Assert.True(columns > 1, "grass variants should differ across tiles");
        Assert.Equal(BuiltinArt.Column(grass, 5, 9, 0), BuiltinArt.Column(grass, 5, 9, 999));

        var water = art.Entries["tile-water"];
        Assert.True(water.Animated);
        Assert.Equal(0, BuiltinArt.Column(water, 0, 0, 0));
        Assert.Equal(1, BuiltinArt.Column(water, 0, 0, water.TicksPerFrame!.Value));
        Assert.Equal(0, BuiltinArt.Column(water, 0, 0, water.TicksPerFrame.Value * water.Frames));
    }

    [Fact]
    public void CropStagesMapOntoTheSheetAndMatureShowsTheLastColumn()
    {
        var art = BuiltinArt.Default;
        var wheat = art.Entries["crop-wheat"];
        Assert.Equal(5, wheat.Frames);
        // 4-stage wheat: engine stages 0..3 spread over 5 columns, the last being ripe.
        Assert.Equal(0, (int)art.Crop("wheat", 0, 4, false)!.Frame);
        Assert.Equal(4, (int)art.Crop("wheat", 3, 4, false)!.Frame);
        Assert.Equal(4, (int)art.Crop("wheat", 1, 4, false, mature: true)!.Frame);
        Assert.Equal(BuiltinArt.SheetUrl(art.Entries["crop-withered"].File), art.Crop("wheat", 2, 4, true)!.ImageUrl);
        Assert.Equal(art.Entries["crop-withered"].X, art.Crop("wheat", 2, 4, true)!.SourceX);
    }

    private static string CropName(BuiltinArt art, string cropId)
    {
        var sprite = art.Crop(cropId, 0, 4, false)!;
        return art.Entries.Values.Single(e => BuiltinArt.SheetUrl(e.File) == sprite.ImageUrl && e.X == sprite.SourceX && e.Y == sprite.SourceY).Name;
    }
}
