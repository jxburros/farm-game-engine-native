using System.Text.Json;
using FarmEngine.Content;
using FarmEngine.Json;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Content;

/// <summary>
/// Port of tests/unit/content-default.test.ts — M5 acceptance tests: the
/// default game IS a content pack, and the demo mod (new crop + machine +
/// onDayStart plugin) loads, merges, runs sandboxed mutations and keeps
/// working after an engine "reload" (re-parse).
/// </summary>
public class ContentDefaultTests
{
    private static JsonElement LoadDemoMod() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "demo-mod.json"))).RootElement;

    private static readonly JintPluginHostOptions Relaxed = new() { TimeoutMs = 5000, InitTimeoutMs = 5000 };

    // --- content-default pack (the format acceptance test) ---

    [Fact]
    public void ValidatesAgainstContentPackSchemaAndIsJsonRoundTripSafe()
    {
        var pack = DefaultContent.CreateContentDefaultPack();
        var roundTripped = PacksSchema.ValidateContentPack(JsonDefaults.ToElement(pack));
        Assert.True(roundTripped.Ok);
        Assert.Empty(roundTripped.Errors);
        Assert.Equal(StableJson.Stringify(pack), StableJson.Stringify(roundTripped.Pack));
    }

    [Fact]
    public void ExpressesTheCompleteBuiltInCatalog()
    {
        var pack = DefaultContent.CreateContentDefaultPack();
        Assert.Equal("content-default", pack.Manifest.Id);
        Assert.True(pack.Manifest.Base);
        // Built-in items first, then the crafting + extensibility showcases appended.
        var defaults = ContentBuiltin.CreateDefaultItems();
        Assert.Equal(StableJson.Stringify(defaults), StableJson.Stringify(pack.Content.Items.Take(defaults.Count).ToList()));
        Assert.Equal(defaults.Count + 9, pack.Content.Items.Count);
        Assert.Equal(9, pack.Content.Crops.Count);
        Assert.NotEmpty(pack.Content.Recipes);
        Assert.NotEmpty(pack.Content.MachineTypes);
        Assert.NotEmpty(pack.Content.NodeTypes);
        Assert.NotEmpty(pack.Content.AnimalSpecies);
        Assert.NotEmpty(pack.Content.FishTables);
        Assert.Equal(["sun", "rain", "storm", "snow"], pack.Content.WeatherTypes.Select(w => w.Id));
        Assert.Equal("scene-farm", pack.Content.Scenes[0].Id);
        Assert.Equal(["npc-farmer", "npc-merchant"], pack.Content.Npcs.Select(npc => npc.Id));
        Assert.Equal(["quest-first-harvest", "quest-go-shopping"], pack.Content.Quests.Select(quest => quest.Id));
        Assert.Equal("shop-general", pack.Content.Shops[0].Id);
        Assert.Equal(6, pack.Content.PlayerStart?.Inventory.Count);
    }

    [Fact]
    public void SeedsTheStarterProjectEntirelyFromThePack()
    {
        var project = DefaultContent.CreateInitialProject();
        Assert.Equal("scene-farm", project.Scenes[0].Id);
        Assert.Equal("scene-farm", project.StartSceneId);
        Assert.Equal("scene-farm", project.Player.SceneId);
        Assert.Equal(58, project.Items.Count);
        Assert.Equal(2, project.Quests.Count);
        Assert.Equal(9, project.CustomCrops?.Count);
    }

    [Fact]
    public void WithoutThePackTheEngineStillRunsFunctionalEmptyEngine()
    {
        var blank = DefaultContent.CreateBlankProject();
        var ctx = new EngineContext(EngineState.CreateContentFromProject(blank));
        var state = EngineState.CreateGameState(blank, seed: "empty");
        state = Engine.ApplyCommand(ctx, state, new MoveCommand("up")).State;
        state = Engine.AdvanceTick(ctx, state, 100).State;
        var slept = Engine.ApplyCommand(ctx, state, new SleepCommand());
        Assert.Equal(2, slept.State.Clock.Day);
    }

    [Fact]
    public void ProductionFactoriesStampTheCurrentTime()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var project = DefaultContent.CreateInitialProject();
        Assert.InRange(project.GameStartTime, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(project.GameStartTime, project.CurrentTime);
    }

    // --- demo mod (M5 exit criterion) ---

    private static GameProject WithPack(ContentPack pack) =>
        DefaultContent.CreateInitialProject() with { ContentPacks = [new PackInstallation { Pack = pack, Enabled = true }] };

    [Fact]
    public void InstallsValidatesMergesItsCropMachineRecipeAndNamespacesIds()
    {
        var validated = PacksSchema.ValidateContentPack(LoadDemoMod());
        Assert.Empty(validated.Errors);
        var content = EngineState.CreateContentFromProject(WithPack(validated.Pack!));
        Assert.True(content.Crops.ContainsKey("demo-glow-farm:glowshroom"));
        Assert.Contains(content.MachineTypes, machine => machine.Id == "demo-glow-farm:machine-glow-vat");
        var recipe = content.Recipes.First(r => r.Id == "demo-glow-farm:recipe-glow-jelly");
        Assert.Equal("demo-glow-farm:machine-glow-vat", recipe.MachineTypeId);
        Assert.Equal("demo-glow-farm:crop-glowshroom", recipe.Inputs[0].ItemId);
    }

    [Fact]
    public void ItsPluginRunsInAHostRespectsCapabilityGrantsAndItsMutationsApply()
    {
        var pack = PacksSchema.ValidateContentPack(LoadDemoMod()).Pack!;
        var specs = Plugins.PluginSpecsFromPacks([pack]);
        var spec = Assert.Single(specs);
        Assert.Equal("demo-glow-farm:morning-hum", spec.Id);
        Assert.Equal(["onDayStart"], spec.GrantedHooks);
        using var host = new JintPluginHost(specs, Relaxed);

        // Day 1: message only.
        var day1 = host.Dispatch("onDayStart", new DayHookPayload(1, "spring", 1));
        Assert.Equal(
            """[{"type":"message","text":"The glowshrooms hum softly on day 1..."}]""",
            JsonDefaults.Serialize(day1[0].Mutations));

        // Day 7: message + a namespaced seed gift, applied through the command pipeline.
        var day7 = host.Dispatch("onDayStart", new DayHookPayload(7, "spring", 1));
        Assert.Equal(2, day7[0].Mutations.Count);

        var project = WithPack(pack);
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project));
        var state = EngineState.CreateGameState(project, seed: "demo");
        foreach (var mutation in day7[0].Mutations)
        {
            state = Engine.ApplyCommand(ctx, state, new PluginMutationCommand(day7[0].PluginId, mutation)).State;
        }
        Assert.Contains(state.Player.Inventory, slot => slot.Item.Id == "demo-glow-farm:seed-glowshroom");

        // Hooks outside the grant never reach the plugin.
        Assert.Empty(host.Dispatch("onCropHarvest", new CropHarvestHookPayload("wheat", 1, "normal")));
    }

    [Fact]
    public void SurvivesEngineReloadSerializeReloadSameMergeResult()
    {
        var pack = PacksSchema.ValidateContentPack(LoadDemoMod()).Pack!;
        var project = WithPack(pack);
        var before = EngineState.CreateContentFromProject(project);
        var reloaded = JsonDefaults.Deserialize<GameProject>(JsonDefaults.Serialize(project))!;
        var after = EngineState.CreateContentFromProject(reloaded);
        Assert.Equal(
            StableJson.Stringify(before.Recipes.First(r => r.Id == "demo-glow-farm:recipe-glow-jelly")),
            StableJson.Stringify(after.Recipes.First(r => r.Id == "demo-glow-farm:recipe-glow-jelly")));
        Assert.Equal(before.Crops.Keys, after.Crops.Keys);
    }

    [Fact]
    public void BrokenPluginsAreDisabledAtInitAndNeverTakeTheHostDown()
    {
        using var host = new JintPluginHost(
        [
            new PluginSpec("bad:boom", "bad", "throw new Error(\"boom\")", ["onDayStart"]),
            new PluginSpec("ok:fine", "ok", "api.on('onDayStart', function () { return [{ type: 'message', text: 'ok' }] })", ["onDayStart"]),
        ], Relaxed);
        var results = host.Dispatch("onDayStart", new { day = 1 });
        var result = Assert.Single(results);
        Assert.Equal("ok:fine", result.PluginId);
    }

    [Fact]
    public void InvalidMutationsAreDroppedValidOnesFromTheSameBatchSurvive()
    {
        using var host = new JintPluginHost(
        [
            new PluginSpec("mixed:bag", "mixed",
                "api.on('onDayStart', function () { return [{ type: 'launchMissiles' }, { type: 'message', text: 'still here' }] })",
                ["onDayStart"]),
        ], Relaxed);
        var results = host.Dispatch("onDayStart", new { day = 1 });
        Assert.Equal("""[{"type":"message","text":"still here"}]""", JsonDefaults.Serialize(results[0].Mutations));
    }
}
