using System.Text.Json;
using FarmEngine.Json;
using FarmEngine.Schemas;
using static FarmEngine.Core.Tests.Core.CoreTestHelpers;

namespace FarmEngine.Core.Tests.Core;

/// <summary>
/// Port of m5-systems.test.ts. M5 system tests: content packs (validation,
/// load order, namespacing, merging, materializing, save stamping,
/// quarantine), plugin mutations, onWeatherRoll rerolls, determinism under mods.
/// </summary>
public class M5SystemsTests
{
    /// <summary>TS <c>makePack</c>: a pack through the schema (defaults applied).</summary>
    private static ContentPack MakePack(string id, Func<PackManifest, PackManifest>? manifest = null, PackContent? content = null)
    {
        var raw = new ContentPack
        {
            Manifest = (manifest ?? (m => m))(new PackManifest { Id = id, Name = id, Version = "1.0.0" }),
            Content = content ?? new PackContent(),
            Plugins = [],
        };
        var result = PacksSchema.ValidateContentPack(JsonDefaults.ToElement(raw));
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        return result.Pack!;
    }

    private static PackInstallation Install(ContentPack pack, bool enabled = true) => new() { Pack = pack, Enabled = enabled };

    private static Item TestItem(string id, double value = 10, string type = "material") => new()
    {
        Id = id, Name = id, Description = id, Type = type, Stackable = true, MaxStack = 99, Value = value,
    };

    private static PackDependency Dep(string packId) => new() { PackId = packId };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // --- pack validation ---

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

    // --- pack load order ---

    [Fact]
    public void IsInstallOrderWithDependenciesHoistedBeforeDependents()
    {
        var a = MakePack("pack-a", m => m with { Dependencies = [Dep("pack-c")] });
        var b = MakePack("pack-b");
        var c = MakePack("pack-c");
        var (packs, problems) = Packs.ResolvePackOrder([Install(a), Install(b), Install(c)]);
        Assert.Empty(problems);
        Assert.Equal(["pack-c", "pack-a", "pack-b"], packs.Select(p => p.Manifest.Id));
    }

    [Fact]
    public void SurfacesMissingDependenciesAndCyclesAsProblemsWithoutDroppingPacks()
    {
        var a = MakePack("pack-a", m => m with { Dependencies = [Dep("pack-b")] });
        var b = MakePack("pack-b", m => m with { Dependencies = [Dep("pack-a")] });
        var missing = Packs.ResolvePackOrder([Install(MakePack("lonely", m => m with { Dependencies = [Dep("ghost")] }))]);
        Assert.Contains("'ghost'", missing.Problems[0].Message);
        Assert.Single(missing.Packs);
        var cycle = Packs.ResolvePackOrder([Install(a), Install(b)]);
        Assert.Contains(cycle.Problems, p => p.Message.Contains("cycle"));
        Assert.Equal(2, cycle.Packs.Count);
    }

    [Fact]
    public void WarnsOnEngineIncompatiblePacks()
    {
        var old = MakePack("too-new", m => m with { EngineCompatibility = ">=99.0.0" });
        var (_, problems) = Packs.ResolvePackOrder([Install(old)]);
        Assert.Contains(problems, p => p.Severity == "warning" && p.Message.Contains("99.0.0"));
    }

    [Fact]
    public void SkipsDisabledPacksEntirely()
    {
        var (packs, _) = Packs.ResolvePackOrder([Install(MakePack("off"), false)]);
        Assert.Empty(packs);
    }

    // --- pack namespacing ---

    [Fact]
    public void PrefixesIdsAndRewritesIntraPackReferencesLeavingGlobalRefsAlone()
    {
        var pack = MakePack("glow", content: new PackContent
        {
            Items = [TestItem("shroom"), TestItem("jelly")],
            Recipes =
            [
                new RecipeDefinition
                {
                    Id = "make-jelly",
                    Name = "Jelly",
                    Inputs = [new RecipeIngredient { ItemId = "shroom", Quantity = 2 }, new RecipeIngredient { ItemId = "crop-wheat", Quantity = 1 }],
                    Outputs = [new RecipeIngredient { ItemId = "jelly", Quantity = 1 }],
                    ProcessingMinutes = 0,
                    Category = "crafting",
                },
            ],
        });
        var namespaced = Packs.NamespacePack(pack);
        Assert.Equal(["glow:shroom", "glow:jelly"], namespaced.Content.Items.Select(i => i.Id));
        var recipe = namespaced.Content.Recipes[0];
        Assert.Equal("glow:make-jelly", recipe.Id);
        Assert.Equal("glow:shroom", recipe.Inputs[0].ItemId);
        // crop-wheat is not defined in the pack → global reference, untouched
        Assert.Equal("crop-wheat", recipe.Inputs[1].ItemId);
        Assert.Equal("glow:jelly", recipe.Outputs[0].ItemId);
    }

    [Fact]
    public void LeavesBasePacksUntouchedSameReferences()
    {
        var @base = MakePack("base-pack", m => m with { Base = true }, new PackContent { Items = [TestItem("plain")] });
        Assert.Same(@base, Packs.NamespacePack(@base));
    }

    // --- pack merging & overrides ---

    [Fact]
    public void LayersPackContentOntoGameContentAtPlayTime()
    {
        var project = EngineTests.MakeProject() with
        {
            ContentPacks = [Install(MakePack("glow", content: new PackContent { Items = [TestItem("shroom", 80)] }))],
        };
        var content = EngineState.CreateContentFromProject(project);
        Assert.Contains(content.Items, item => item.Id == "glow:shroom");
    }

    [Fact]
    public void KeepsTheEarlierDefinitionAndReportsAConflictForUndeclaredOverrides()
    {
        var @base = EngineState.CreateContentFromProject(EngineTests.MakeProject());
        var sneaky = MakePack("sneaky", m => m with { Base = true }, new PackContent { Items = [TestItem("crop-wheat", 9999, "crop")] });
        var (content, problems) = Packs.MergePacksIntoContent(@base, [Install(sneaky)]);
        Assert.NotEqual(9999, content.Items.First(item => item.Id == "crop-wheat").Value);
        Assert.Contains(problems, p => p.Message.Contains("without declaring it in manifest.overrides"));
    }

    [Fact]
    public void HonorsDeclaredOverridesSilently()
    {
        var @base = EngineState.CreateContentFromProject(EngineTests.MakeProject());
        var balance = MakePack("rebalance", m => m with { Base = true, Overrides = ["crop-wheat"] },
            new PackContent { Items = [TestItem("crop-wheat", 9999, "crop")] });
        var (content, problems) = Packs.MergePacksIntoContent(@base, [Install(balance)]);
        Assert.Equal(9999, content.Items.First(item => item.Id == "crop-wheat").Value);
        Assert.Empty(problems);
    }

    [Fact]
    public void SurfacesPackProblemsInTheProblemsPanelValidation()
    {
        var project = EngineTests.MakeProject() with
        {
            ContentPacks = [Install(MakePack("needy", m => m with { Dependencies = [Dep("nope")] }))],
        };
        var problems = Validation.ValidateProjectContent(project);
        Assert.Contains(problems, p => p.Category == "packs" && p.Message.Contains("'nope'"));
    }

    // --- applyPackToProject (materialize) ---

    [Fact]
    public void ImportsContentIntoTheProjectWithNamespacingAndSeedsPlayerStart()
    {
        var project = EngineTests.MakeProject();
        var moneyBefore = project.Player.Money;
        var pack = MakePack("starter-plus", content: new PackContent
        {
            Items = [TestItem("charm")],
            PlayerStart = new PackPlayerStart { Inventory = [new PackStartItem { ItemId = "charm", Quantity = 2 }] },
        });
        var (next, problems) = Packs.ApplyPackToProject(project, pack);
        Assert.Empty(problems);
        Assert.Contains(next.Items, item => item.Id == "starter-plus:charm");
        var slot = next.Player.Inventory.FirstOrDefault(s => s.Item.Id == "starter-plus:charm");
        Assert.Equal(2, slot?.Quantity);
        // No money in playerStart → untouched
        Assert.Equal(moneyBefore, next.Player.Money);
        // Original project untouched (pure)
        Assert.DoesNotContain(project.Items, item => item.Id == "starter-plus:charm");
    }

    [Fact]
    public void ReportsUnknownPlayerStartItemsAsErrorsInsteadOfCrashing()
    {
        var (_, problems) = Packs.ApplyPackToProject(EngineTests.MakeProject(), MakePack("broken", content: new PackContent
        {
            PlayerStart = new PackPlayerStart { Inventory = [new PackStartItem { ItemId = "no-such-item", Quantity = 1 }] },
        }));
        Assert.Contains(problems, p => p.Severity == "error" && p.Message.Contains("no-such-item"));
    }

    // --- save stamping & quarantine ---

    [Fact]
    public void StampsEnabledPacksIntoNewSaves()
    {
        var project = EngineTests.MakeProject() with
        {
            ContentPacks = [Install(MakePack("active")), Install(MakePack("dormant"), false)],
        };
        var state = EngineState.CreateGameState(project, seed: "stamp");
        AssertDeepEqual(new List<SavePackRef> { new() { Id = "active", Version = "1.0.0" } }, state.Meta.Packs);
        AssertDeepEqual(new List<SavePackRef> { new() { Id = "active", Version = "1.0.0" } }, Packs.StampPacks(project.ContentPacks));
    }

    [Fact]
    public void QuarantinesItemsFromMissingPacksAndRestoresThemWhenThePackReturns()
    {
        var pack = MakePack("glow", content: new PackContent { Items = [TestItem("shroom")] });
        var project = EngineTests.MakeProject() with { ContentPacks = [Install(pack)] };
        var state = EngineState.CreateGameState(project, seed: "q");
        var shroom = EngineState.CreateContentFromProject(project).Items.First(item => item.Id == "glow:shroom");
        state = state with { Player = state.Player with { Inventory = [.. state.Player.Inventory, new InventorySlot { Item = shroom, Quantity = 3 }] } };

        // Pack disabled → the item leaves the inventory but is NOT dropped.
        var without = Packs.ReconcilePackItems(state, new HashSet<string>());
        Assert.DoesNotContain(without.Player.Inventory, slot => slot.Item.Id == "glow:shroom");
        AssertDeepEqual(new List<InventorySlot> { new() { Item = shroom, Quantity = 3 } }, without.QuarantinedItems);

        // Pack returns → the item comes back.
        var restored = Packs.ReconcilePackItems(without, new HashSet<string> { "glow" });
        Assert.Contains(restored.Player.Inventory, slot => slot.Item.Id == "glow:shroom");
        Assert.Empty(restored.QuarantinedItems!);
    }

    // --- plugin mutations through the command pipeline ---

    private static (EngineContext Ctx, GameState State) MakeCtxState()
    {
        var project = EngineTests.MakeProject();
        return (new EngineContext(EngineState.CreateContentFromProject(project)), EngineState.CreateGameState(project, seed: "plug"));
    }

    [Fact]
    public void GiveItemAddsToTheInventoryUnknownItemsFailSoft()
    {
        var (ctx, state) = MakeCtxState();
        var ok = Engine.ApplyCommand(ctx, state, new PluginMutationCommand("p", new GiveItemMutation { ItemId = "crop-wheat", Quantity = 2 }));
        Assert.Equal(2, ok.State.Player.Inventory.FirstOrDefault(slot => slot.Item.Id == "crop-wheat")?.Quantity);
        var bad = Engine.ApplyCommand(ctx, state, new PluginMutationCommand("p", new GiveItemMutation { ItemId = "nope", Quantity = 1 }));
        Assert.Same(state, bad.State);
        Assert.Equal("error", Assert.IsType<MessageEffect>(bad.Effects[0]).Level);
    }

    [Fact]
    public void SetFlagMessageAndSetWeatherApply()
    {
        var (ctx, state) = MakeCtxState();
        var flagged = Engine.ApplyCommand(ctx, state, new PluginMutationCommand("p", new SetFlagMutation { Flag = "mod:seen", Value = Js.Value(7) }));
        Assert.Equal(7, flagged.State.Flags["mod:seen"].GetDouble());
        var message = Engine.ApplyCommand(ctx, state, new PluginMutationCommand("p", new MessageMutation { Text = "hi" }));
        Assert.Equal("hi", Assert.IsType<MessageEffect>(message.Effects[0]).Text);
        var weather = Engine.ApplyCommand(ctx, state, new PluginMutationCommand("p", new SetWeatherMutation { WeatherId = "sun" }));
        Assert.Equal("sun", weather.State.Clock.WeatherId);
    }

    // --- onWeatherRoll reroll capability ---

    private sealed record WeatherOverride(string WeatherId);

    [Fact]
    public void AHookListenerCanOverrideTheRolledWeatherWithAValidType()
    {
        var project = EngineTests.MakeProject();
        var hooks = new HookBus();
        hooks.On(HookNames.OnWeatherRoll, _ => new WeatherOverride("sun"));
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project), hooks);
        var state = EngineState.CreateGameState(project, seed: "wx");
        var next = GameTime.PerformSleep(ctx, state, new SleepOptions(Collapsed: false)).State;
        Assert.Equal("sun", next.Clock.WeatherId);
    }

    [Fact]
    public void InvalidOverridesAreIgnored()
    {
        var project = EngineTests.MakeProject();
        var hooks = new HookBus();
        hooks.On(HookNames.OnWeatherRoll, _ => new WeatherOverride("sharknado"));
        var ctx = new EngineContext(EngineState.CreateContentFromProject(project), hooks);
        var state = EngineState.CreateGameState(project, seed: "wx2");
        var next = GameTime.PerformSleep(ctx, state, new SleepOptions(Collapsed: false)).State;
        Assert.Contains(ctx.Content.Weather.Types, type => type.Id == next.Clock.WeatherId);
        Assert.NotEqual("sharknado", next.Clock.WeatherId);
    }

    // --- determinism under mods (M5 exit criterion) ---

    [Fact]
    public void ReplaysWithAModPackPlusPluginMutationsHashIdenticallyAcrossRuns()
    {
        (EngineContext Ctx, GameState State) MakeModdedEngine()
        {
            var project = EngineTests.MakeProject() with
            {
                ContentPacks = [Install(MakePack("glow", content: new PackContent { Items = [TestItem("shroom", 80)] }))],
            };
            return (new EngineContext(EngineState.CreateContentFromProject(project)), EngineState.CreateGameState(project, seed: "modded"));
        }
        // The command log a plugin host would produce: hook fires → mutations
        // become pluginMutation commands in the log.
        List<ReplayInput> script =
        [
            Replay.Cmd(new MoveCommand("up")),
            Replay.Ticks(100),
            Replay.Cmd(new SleepCommand()),
            Replay.Cmd(new PluginMutationCommand("glow:daily", new GiveItemMutation { ItemId = "glow:shroom", Quantity = 1 })),
            Replay.Cmd(new PluginMutationCommand("glow:daily", new SetFlagMutation { Flag = "glow:day", Value = Js.Value(2) })),
            Replay.Ticks(60),
            Replay.Cmd(new SleepCommand()),
            Replay.Cmd(new PluginMutationCommand("glow:daily", new GiveItemMutation { ItemId = "glow:shroom", Quantity = 1 })),
        ];
        var a = MakeModdedEngine();
        var b = MakeModdedEngine();
        var runA = Replay.RunReplay(a.Ctx, a.State, script);
        var runB = Replay.RunReplay(b.Ctx, b.State, script);
        Assert.Equal(runA.Hash, runB.Hash);
        Assert.Equal(2, runA.State.Player.Inventory.FirstOrDefault(slot => slot.Item.Id == "glow:shroom")?.Quantity);
        Assert.Equal(2, runA.State.Flags["glow:day"].GetDouble());
        AssertDeepEqual(new List<SavePackRef> { new() { Id = "glow", Version = "1.0.0" } }, runA.State.Meta.Packs);
    }
}
