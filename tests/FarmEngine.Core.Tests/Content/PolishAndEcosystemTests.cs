using System.Text.Json;
using FarmEngine.Content;
using FarmEngine.Json;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Content;

/// <summary>
/// Ports of tests/unit/m7-polish.test.ts (sprite sheets, audio mapping, pack
/// locale strings) and the sample-template half of m8-ecosystem.test.ts.
/// (The m8 mod-registry test covers src/lib/mod-registry.ts, an editor
/// module that is not part of the native port.)
/// </summary>
public class PolishAndEcosystemTests
{
    // --- sprite sheets (M7) ---

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
        var project = DefaultContent.CreateInitialProject();
        project = project with
        {
            CustomAssets = [.. project.CustomAssets, new CustomAsset { Id = "a1", Name = "Hero", Type = "player", DataUrl = "data:image/png;base64,x" }],
        };
        // No throw, no sheet: the renderer path treats it as a plain image.
        Assert.Null(project.CustomAssets[0].Sheet);
    }

    // --- audio effect mapping (M7) ---

    [Fact]
    public void MapsEngineEffectsToSynthesizedSfxPresets()
    {
        Assert.Equal("till", Audio.SfxForEffect(new SoundEffect("till")));
        Assert.Equal("success", Audio.SfxForEffect(new MessageEffect("success", "x")));
        Assert.Equal("error", Audio.SfxForEffect(new MessageEffect("error", "x")));
        Assert.Null(Audio.SfxForEffect(new MessageEffect("info", "x")));
        Assert.Equal("harvest", Audio.SfxForEffect(new CropHarvestedEffect("wheat", 1)));
        Assert.Equal("quest", Audio.SfxForEffect(new QuestCompletedEffect("q")));
        Assert.Equal("sleep", Audio.SfxForEffect(new DayStartedEffect(2, "spring", 1)));
        Assert.Null(Audio.SfxForEffect(new PlayerMovedEffect(0, 0)));
    }

    // --- pack locale strings (M7 i18n) ---

    private static ContentPack MakeLocalizedPack() => PacksSchema.ValidateContentPack(JsonDocument.Parse("""
        {
          "manifest": { "id": "translations", "name": "Translations", "version": "1.0.0" },
          "content": {
            "items": [{ "id": "charm", "name": "Charm", "description": "A charm", "type": "material", "stackable": true, "maxStack": 99, "value": 5 }],
            "strings": {
              "es": {
                "item:charm:name": "Amuleto",
                "item:crop-wheat:name": "Trigo",
                "dialogue:dialogue-farmer-greeting:text": "¡Bienvenido a la granja!",
                "quest:quest-first-harvest:name": "Primera Cosecha"
              }
            }
          },
          "plugins": []
        }
        """).RootElement).Pack!;

    private static GameContent LocalizedContent(string locale)
    {
        var project = DefaultContent.CreateInitialProject();
        project = project with
        {
            ContentPacks = [new PackInstallation { Pack = MakeLocalizedPack(), Enabled = true }],
            Settings = project.Settings with { Locale = locale },
        };
        return EngineState.CreateContentFromProject(project);
    }

    [Fact]
    public void AppliesPackStringTablesForTheProjectLocaleWithAuthoredFallback()
    {
        var content = LocalizedContent("es");
        // Pack's own item: key was namespaced along with the id.
        Assert.Equal("Amuleto", content.Items.First(item => item.Id == "translations:charm").Name);
        // Base-content reference: stays fully qualified, applies directly.
        Assert.Equal("Trigo", content.Items.First(item => item.Id == "crop-wheat").Name);
        // Untranslated content keeps authored text (fallback).
        Assert.Equal("Corn", content.Items.First(item => item.Id == "crop-corn").Name);
        // Dialogue and quest text translate too (npc-owned dialogue included).
        Assert.Equal("¡Bienvenido a la granja!", content.Dialogues.First(d => d.Id == "dialogue-farmer-greeting").Text);
        Assert.Equal("¡Bienvenido a la granja!", content.Npcs.First(n => n.Id == "npc-farmer").Dialogue[0].Text);
        Assert.Equal("Primera Cosecha", content.Quests.First(q => q.Id == "quest-first-harvest").Name);
    }

    [Fact]
    public void UnknownLocalesChangeNothing() =>
        Assert.Equal("Wheat", LocalizedContent("fr").Items.First(item => item.Id == "crop-wheat").Name);

    [Fact]
    public void ApplyLocaleStringsIsANoOpWithoutTables()
    {
        var content = EngineState.CreateContentFromProject(DefaultContent.CreateInitialProject());
        Assert.Same(content, Packs.ApplyLocaleStrings(content, [], "es"));
    }

    // --- sample templates (M8) ---

    [Fact]
    public void EveryAdvertisedTemplateResolvesToAFactoryOrABuiltInPath()
    {
        Assert.Equal(["starter", "cozy", "quest", "blank"], Templates.TemplateInfo.Select(template => template.Id));
        Assert.Equal("Cozy Garden", Templates.CreateProjectFromTemplate("cozy")?.Name);
        Assert.Equal("Quest RPG", Templates.CreateProjectFromTemplate("quest")?.Name);
        Assert.Null(Templates.CreateProjectFromTemplate("starter"));
        Assert.Null(Templates.CreateProjectFromTemplate("blank"));
    }

    public static TheoryData<string> TemplateFactories() => ["cozy", "quest"];

    [Theory]
    [MemberData(nameof(TemplateFactories))]
    public void TemplateValidatesAndHasZeroContentProblems(string name)
    {
        var project = name == "cozy" ? Templates.CreateCozyFarmProject() : Templates.CreateQuestRpgProject();
        var result = Migrations.MigrateProject(JsonSerializer.SerializeToNode(project, JsonDefaults.Options));
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Empty(Validation.ValidateProjectContent(project));
    }

    [Fact]
    public void CozyTemplateTurnsHeavySystemsOffButStaysPlayable()
    {
        var project = Templates.CreateCozyFarmProject();
        Assert.False(project.Settings.EnergyEnabled);
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "cozy");
        state = Engine.ApplyCommand(ctx, state, new SleepCommand()).State;
        Assert.Equal(2, state.Clock.Day);
    }

    [Fact]
    public void QuestTemplateWiresTheElderChainEndToEnd()
    {
        var content = EngineState.CreateContentFromProject(Templates.CreateQuestRpgProject());
        var elder = content.Npcs.FirstOrDefault(npc => npc.Id == "npc-elder");
        Assert.Equal("quest-rebuild-square", elder?.Dialogue[0].Options[0].OfferQuestId);
        var feast = content.Quests.FirstOrDefault(quest => quest.Id == "quest-festival-feast");
        Assert.Equal(["quest-rebuild-square"], feast?.Prerequisites!);
    }
}
