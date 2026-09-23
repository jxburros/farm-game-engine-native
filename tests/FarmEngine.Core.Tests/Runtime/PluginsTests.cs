using System.Text.Json;
using FarmEngine.Core.Tests.Core;
using FarmEngine.Json;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmEngine.Core.Tests.Runtime;

/// <summary>Port of plugins.test.ts, plus the Jint sandbox guarantees.</summary>
public class PluginsTests
{
    /// <summary>Generous budgets for behavior tests (CI machines are noisy); budget tests set their own.</summary>
    private static readonly JintPluginHostOptions Relaxed = new() { TimeoutMs = 5000, InitTimeoutMs = 5000 };

    private static PluginDispatchResult Result(string pluginId, params string[] texts) =>
        new(pluginId, texts.Select(text => (PluginMutation)new MessageMutation { Text = text }).ToList());

    private static ContentPack Pack(string id, IEnumerable<string> grantedHooks, params PackPlugin[] plugins) => new()
    {
        Manifest = new PackManifest
        {
            Id = id, Name = id, Version = "1.0.0",
            Permissions = new PackPermissions { Hooks = [.. grantedHooks] },
        },
        Plugins = [.. plugins],
    };

    private static PackPlugin Plugin(string id, string source, params string[] hooks) =>
        new() { Id = id, Hooks = hooks.Length == 0 ? ["onDayStart"] : [.. hooks], Source = source };

    private static JintPluginHost Host(JintPluginHostOptions? options, params PackPlugin[] plugins) =>
        new(Plugins.PluginSpecsFromPacks([Pack("pack-a", HookNames.All, plugins)]), options ?? Relaxed);

    private static readonly DayHookPayload Day3 = new(3, "spring", 1);

    private static string Texts(IEnumerable<PluginDispatchResult> results) =>
        string.Join("|", results.SelectMany(r => r.Mutations).OfType<MessageMutation>().Select(m => m.Text));

    // --- plugins.test.ts ---

    [Fact]
    public void DrainsEverythingInArrivalOrderAndEmptiesItself()
    {
        var queue = new PluginMutationQueue();
        queue.Enqueue([Result("a", "one"), Result("b", "two", "three")]);
        queue.Enqueue([Result("a", "four")]);
        Assert.Equal(4, queue.Size);

        var drained = queue.Drain();
        Assert.Equal(
            [("a", "one"), ("b", "two"), ("b", "three"), ("a", "four")],
            drained.Select(q => (q.PluginId, ((MessageMutation)q.Mutation).Text)));
        Assert.Equal(0, queue.Size);
        Assert.Empty(queue.Drain());
    }

    [Fact]
    public void ReturnsResultsInStableSpecOrderRegardlessOfHandlerContent()
    {
        var specs = Plugins.PluginSpecsFromPacks([
            Pack("pack-a", ["onDayStart"],
                Plugin("p1", "api.on(\"onDayStart\", () => [{ type: \"message\", text: \"from p1\" }])"),
                Plugin("p2", "api.on(\"onDayStart\", () => [{ type: \"message\", text: \"from p2\" }])")),
        ]);
        using var host = new JintPluginHost(specs, Relaxed);
        var results = host.Dispatch("onDayStart", new { });
        Assert.Equal(["pack-a:p1", "pack-a:p2"], results.Select(r => r.PluginId));
    }

    // --- spec extraction & payloads ---

    [Fact]
    public void GrantsAreTheIntersectionOfPluginHooksAndManifestPermissions()
    {
        var specs = Plugins.PluginSpecsFromPacks([
            Pack("pack-a", ["onDayStart", "onAction"], Plugin("p", "", "onDayStart", "onCropHarvest", "onAction")),
        ]);
        var spec = Assert.Single(specs);
        Assert.Equal("pack-a:p", spec.Id);
        Assert.Equal("pack-a", spec.PackId);
        Assert.Equal(["onDayStart", "onAction"], spec.GrantedHooks);
    }

    [Fact]
    public void HandlersReceiveTheCamelCasePayloadAndOnlyGrantedHooksFire()
    {
        using var host = new JintPluginHost(Plugins.PluginSpecsFromPacks([
            Pack("pack-a", ["onDayStart"], Plugin("p",
                "api.on('onDayStart', p => [{ type: 'message', text: p.season + ':' + p.day + ':' + p.year }]);" +
                "api.on('onCropHarvest', () => [{ type: 'message', text: 'not granted' }]);",
                "onDayStart", "onCropHarvest")),
        ]), Relaxed);
        Assert.Equal("spring:3:1", Texts(host.Dispatch("onDayStart", Day3)));
        Assert.Empty(host.Dispatch("onCropHarvest", new CropHarvestHookPayload("wheat", 1, "normal")));
    }

    [Fact]
    public void PluginsWithoutAHandlerOrWithNonArrayResultsYieldNothing()
    {
        using var host = Host(null,
            Plugin("none", "api.on('onAction', () => [{ type: 'giveMoney', amount: 5 }])"),
            Plugin("object", "api.on('onDayStart', () => ({ type: 'giveMoney', amount: 5 }))"));
        Assert.Empty(host.Dispatch("onDayStart", Day3));
    }

    [Fact]
    public void AllMutationKindsRoundTripThroughValidation()
    {
        using var host = Host(null, Plugin("all", """
            api.on('onDayStart', function () {
              return [
                { type: 'giveItem', itemId: 'seed-wheat', quantity: 2, extra: 'stripped' },
                { type: 'takeItem', itemId: 'seed-wheat', quantity: 1 },
                { type: 'giveMoney', amount: 10 },
                { type: 'takeMoney', amount: 3 },
                { type: 'setFlag', flag: 'f', value: 'v' },
                { type: 'message', text: 'hi' },
                { type: 'setWeather', weatherId: 'rain' },
                { type: 'modifyFriendship', npcId: 'n', delta: -5 },
                { type: 'grantXp', skill: 'farming', amount: 4 },
                { type: 'modifyEnergy', delta: -3 },
                { type: 'startQuest', questId: 'q' },
                { type: 'warpPlayer', sceneId: 's', x: 0, y: 2 },
                { type: 'startDialogue', npcId: 'n' },
                { type: 'playSound', soundId: 'coin' },
                { type: 'performAction', actionId: 'a' },
                { type: 'startMinigame', minigameId: 'fishing' },
              ];
            });
            """));
        var result = Assert.Single(host.Dispatch("onDayStart", Day3));
        Assert.Empty(result.Errors);
        Assert.Equal(16, result.Mutations.Count);
        Assert.Equal(
            """[{"type":"giveItem","itemId":"seed-wheat","quantity":2},{"type":"takeItem","itemId":"seed-wheat","quantity":1},{"type":"giveMoney","amount":10},{"type":"takeMoney","amount":3},{"type":"setFlag","flag":"f","value":"v"},{"type":"message","text":"hi"},{"type":"setWeather","weatherId":"rain"},{"type":"modifyFriendship","npcId":"n","delta":-5},{"type":"grantXp","skill":"farming","amount":4},{"type":"modifyEnergy","delta":-3},{"type":"startQuest","questId":"q"},{"type":"warpPlayer","sceneId":"s","x":0,"y":2},{"type":"startDialogue","npcId":"n"},{"type":"playSound","soundId":"coin"},{"type":"performAction","actionId":"a"},{"type":"startMinigame","minigameId":"fishing"}]""",
            JsonDefaults.Serialize(result.Mutations));
    }

    // --- sandbox: invalid mutations ---

    [Fact]
    public void InvalidMutationObjectsAreRejectedWithErrorsWhileValidOnesPass()
    {
        using var host = Host(null, Plugin("bad", """
            api.on('onDayStart', function () {
              return [
                { type: 'giveMoney', amount: 1.5 },
                { type: 'giveMoney', amount: 0 },
                { type: 'giveMoney', amount: '100' },
                { type: 'giveItem', itemId: 'x', quantity: 1000 },
                { type: 'message', text: 'x'.repeat(501) },
                { type: 'setFlag', flag: 'f', value: null },
                { type: 'setFlag', flag: 'f', value: { nested: true } },
                { type: 'startDialogue', npcId: 'n', dialogueId: 7 },
                { type: 'warpPlayer', sceneId: 's', x: -1, y: 0 },
                { type: 'setState', path: 'player.money', value: 1e9 },
                { amount: 5 },
                'giveMoney',
                null,
                [1, 2],
                function () {},
                { type: 'giveMoney', amount: NaN },
                { type: 'message', text: 'ok' },
              ];
            });
            """));
        var result = Assert.Single(host.Dispatch("onDayStart", Day3));
        var mutation = Assert.Single(result.Mutations);
        Assert.Equal("ok", Assert.IsType<MessageMutation>(mutation).Text);
        Assert.Equal(16, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Equal(PluginErrorKinds.InvalidMutation, e.Kind));
    }

    [Fact]
    public void TryParseMutationMirrorsTheZodSchema()
    {
        static bool Ok(string json) => Plugins.TryParseMutation(JsonDocument.Parse(json).RootElement, out _, out _);
        Assert.True(Ok("""{"type":"giveItem","itemId":"a","quantity":999}"""));
        Assert.False(Ok("""{"type":"giveItem","itemId":"a"}"""));
        Assert.True(Ok("""{"type":"modifyEnergy","delta":-1000}"""));
        Assert.False(Ok("""{"type":"modifyEnergy","delta":-1001}"""));
        Assert.True(Ok("""{"type":"setFlag","flag":"f","value":false}"""));
        Assert.True(Ok("""{"type":"setFlag","flag":"f","value":0}"""));
        Assert.True(Ok("""{"type":"startDialogue","npcId":"n","dialogueId":"d"}"""));
        Assert.False(Ok("""{"type":"startDialogue","npcId":"n","dialogueId":null}"""));
        Assert.True(Ok("""{"type":"message","text":""}"""));
        Assert.False(Ok("""{"type":"MESSAGE","text":""}"""));
    }

    // --- sandbox: no CLR, no host escape ---

    [Fact]
    public void NoAccessToSystemOrTheClr()
    {
        using var host = Host(null, Plugin("probe", """
            api.on('onDayStart', function () {
              var names = ['importNamespace', 'System', 'clr', 'require', 'process', 'fetch', 'XMLHttpRequest',
                'WebSocket', 'Worker', 'indexedDB', 'navigator', 'ArrayBuffer', 'SharedArrayBuffer', 'Proxy', 'host'];
              var seen = names.filter(function (n) { return typeof globalThis[n] !== 'undefined'; });
              var ctor = (function () {}).constructor;
              return [{ type: 'message', text: 'seen=' + seen.join(',') + ';getType=' + typeof ({}).GetType + ';ctor=' + typeof ctor }];
            });
            """));
        Assert.Equal("seen=;getType=undefined;ctor=function", Texts(host.Dispatch("onDayStart", Day3)));
    }

    [Fact]
    public void ReferencingClrNamespacesThrowsInsideTheSandboxOnly()
    {
        using var host = Host(null,
            Plugin("clr", "api.on('onDayStart', function () { var file = importNamespace('System.IO').File; return [{ type: 'message', text: 'escaped' }]; })"),
            Plugin("sys", "api.on('onDayStart', function () { return [{ type: 'message', text: String(System.Environment.MachineName) }]; })"),
            Plugin("ok", "api.on('onDayStart', function () { return [{ type: 'message', text: 'still fine' }]; })"));
        var results = host.Dispatch("onDayStart", Day3);
        Assert.Equal("still fine", Texts(results));
        Assert.Equal(["pack-a:clr", "pack-a:sys"], results.Where(r => r.Errors.Count > 0).Select(r => r.PluginId));
        Assert.All(results.SelectMany(r => r.Errors), e => Assert.Equal(PluginErrorKinds.Threw, e.Kind));
    }

    [Fact]
    public void EvalAndTheFunctionConstructorAreDisabled()
    {
        using var host = Host(null, Plugin("eval", """
            api.on('onDayStart', function () {
              var out = [];
              try { eval('1 + 1'); out.push('eval ran'); } catch (e) { out.push('eval blocked'); }
              try { new Function('return 1')(); out.push('Function ran'); } catch (e) { out.push('Function blocked'); }
              try { (function () {}).constructor('return this')(); out.push('ctor ran'); } catch (e) { out.push('ctor blocked'); }
              try { (async function () {}).constructor('return 1'); out.push('async ran'); } catch (e) { out.push('async blocked'); }
              return [{ type: 'message', text: out.join(',') }];
            });
            """));
        Assert.Equal("eval blocked,Function blocked,ctor blocked,async blocked", Texts(host.Dispatch("onDayStart", Day3)));
    }

    [Fact]
    public void StrictModeIsEnforced()
    {
        using var host = Host(null, Plugin("sloppy", "api.on('onDayStart', function () { leaked = 1; return [{ type: 'message', text: 'no' }]; })"));
        var result = Assert.Single(host.Dispatch("onDayStart", Day3));
        Assert.Empty(result.Mutations);
        Assert.Equal(PluginErrorKinds.Threw, Assert.Single(result.Errors).Kind);
    }

    [Fact]
    public void SourcesCannotBreakOutOfTheFunctionWrapper()
    {
        using var host = Host(null, Plugin("breakout", "}); globalThis.pwned = true; (function (api) {"));
        var error = Assert.Single(host.InitErrors);
        Assert.Equal(PluginErrorKinds.Init, error.Kind);
        Assert.Contains("pack-a:breakout", host.DisabledPlugins);
        Assert.Empty(host.Dispatch("onDayStart", Day3));
    }

    [Fact]
    public void AThrowingInitializerDisablesOnlyThatPlugin()
    {
        using var host = Host(null,
            Plugin("boom", "throw new Error('nope')"),
            Plugin("syntax", "api.on('onDayStart', function ( { "),
            Plugin("ok", "api.on('onDayStart', () => [{ type: 'message', text: 'ok' }])"));
        Assert.Equal(["pack-a:boom", "pack-a:syntax"], host.InitErrors.Select(e => e.PluginId));
        Assert.Equal("ok", Texts(host.Dispatch("onDayStart", Day3)));
    }

    [Fact]
    public void APluginCannotMutateAnotherPluginsState()
    {
        using var host = Host(null,
            Plugin("a", """
                var counter = 0;
                globalThis.shared = 'a';
                Object.prototype.polluted = 'a';
                api.on('onDayStart', function () { counter++; return [{ type: 'message', text: 'a' + counter }]; });
                """),
            Plugin("b", """
                api.on('onDayStart', function () {
                  var seen = [typeof counter, typeof globalThis.shared, typeof ({}).polluted];
                  try { counter = 100; } catch (e) { seen.push('no counter'); }
                  return [{ type: 'message', text: 'b:' + seen.join(',') }];
                });
                """));
        Assert.Equal("a1|b:undefined,undefined,undefined,no counter", Texts(host.Dispatch("onDayStart", Day3)));
        Assert.Equal("a2|b:undefined,undefined,undefined,no counter", Texts(host.Dispatch("onDayStart", Day3)));
    }

    [Fact]
    public void PayloadsAreCopiesHandlersCannotShareMutableObjectsWithTheHost()
    {
        var payload = new ResourceGatherHookPayload("node-tree", [new GatherDrop("material-wood", 2)]);
        using var host = Host(null, Plugin("m", """
            api.on('onResourceGather', function (p) { p.drops[0].quantity = 999; p.nodeTypeId = 'x'; return [{ type: 'message', text: p.drops[0].itemId }]; });
            """, "onResourceGather"));
        Assert.Equal("material-wood", Texts(host.Dispatch("onResourceGather", payload)));
        Assert.Equal(2, payload.Drops[0].Quantity);
        Assert.Equal("node-tree", payload.NodeTypeId);
    }

    // --- sandbox: budgets ---

    [Fact]
    public void AnInfiniteLoopIsTerminatedByTheTimeout()
    {
        using var host = Host(new JintPluginHostOptions { TimeoutMs = 50 },
            Plugin("loop", "api.on('onDayStart', function () { while (true) {} })"),
            Plugin("ok", "api.on('onDayStart', () => [{ type: 'message', text: 'ok' }])"));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var results = host.Dispatch("onDayStart", Day3);
        Assert.True(started.ElapsedMilliseconds < 5000, $"took {started.ElapsedMilliseconds} ms");
        Assert.Equal("ok", Texts(results));
        var error = Assert.Single(results.Single(r => r.PluginId == "pack-a:loop").Errors);
        Assert.Equal(PluginErrorKinds.Timeout, error.Kind);
    }

    [Fact]
    public void ThreeConsecutiveTimeoutsDisableAPluginAndOverrunsResetItsState()
    {
        using var host = Host(new JintPluginHostOptions { TimeoutMs = 30 }, Plugin("flaky", """
            var calls = 0;
            api.on('onDayStart', function () { calls++; if (calls >= 2) { while (true) {} } return [{ type: 'message', text: 'call ' + calls }]; });
            """));
        Assert.Equal("call 1", Texts(host.Dispatch("onDayStart", Day3)));
        // Strike 1: the engine is rebuilt from source, so `calls` starts over.
        Assert.Equal(PluginErrorKinds.Timeout, host.Dispatch("onDayStart", Day3).Single().Errors.Single().Kind);
        Assert.Equal("call 1", Texts(host.Dispatch("onDayStart", Day3)));
        Assert.Empty(host.DisabledPlugins);

        using var looping = Host(new JintPluginHostOptions { TimeoutMs = 30 }, Plugin("loop", "api.on('onDayStart', function () { while (true) {} })"));
        looping.Dispatch("onDayStart", Day3);
        looping.Dispatch("onDayStart", Day3);
        var third = looping.Dispatch("onDayStart", Day3).Single();
        Assert.Equal([PluginErrorKinds.Timeout, PluginErrorKinds.Disabled], third.Errors.Select(e => e.Kind));
        Assert.Equal(["pack-a:loop"], looping.DisabledPlugins);
        Assert.Empty(looping.Dispatch("onDayStart", Day3));
    }

    [Fact]
    public void DeepRecursionIsStopped()
    {
        using var host = Host(null,
            Plugin("rec", "function f(n) { return f(n + 1) + 1; } api.on('onDayStart', function () { return [{ type: 'message', text: String(f(0)) }]; })"),
            Plugin("getter", "var o = { get x() { return this.x; } }; api.on('onDayStart', function () { return [{ type: 'message', text: String(o.x) }]; })"));
        var results = host.Dispatch("onDayStart", Day3);
        Assert.Empty(results.SelectMany(r => r.Mutations));
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Contains(r.Errors, e => e.Kind is PluginErrorKinds.Budget or PluginErrorKinds.Threw));
    }

    [Fact]
    public void HugeAllocationsAreStopped()
    {
        using var host = Host(null,
            Plugin("doubling", "api.on('onDayStart', function () { var s = 'x'; while (true) s += s; })"),
            Plugin("array", "api.on('onDayStart', function () { var a = []; while (true) a.push({ i: a.length, pad: 'xxxxxxxxxxxxxxxx' }); })"),
            Plugin("repeat", "api.on('onDayStart', function () { return [{ type: 'message', text: String('x'.repeat(5e8).length) }]; })"),
            Plugin("sparse", "api.on('onDayStart', function () { return [{ type: 'message', text: String(new Array(1e9).fill(0).length) }]; })"),
            Plugin("pad", "api.on('onDayStart', function () { return [{ type: 'message', text: String(''.padStart(1e9).length) }]; })"),
            Plugin("ok", "api.on('onDayStart', () => [{ type: 'message', text: 'ok' }])"));
        var before = GC.GetTotalMemory(false);
        var results = host.Dispatch("onDayStart", Day3);
        Assert.Equal("ok", Texts(results));
        Assert.Equal(5, results.Count(r => r.Errors.Count > 0));
        Assert.True(GC.GetTotalMemory(true) - before < 200_000_000);
    }

    [Fact]
    public void DeepNativeRecursionDoesNotCrashTheHost()
    {
        using var host = Host(null,
            Plugin("json", "api.on('onDayStart', function () { var o = {}; var c = o; for (var i = 0; i < 1e6; i++) { c.a = {}; c = c.a; } return [{ type: 'message', text: JSON.stringify(o) }]; })"),
            Plugin("proto", "api.on('onDayStart', function () { var o = {}; for (var i = 0; i < 1e6; i++) o = Object.create(o); return [{ type: 'message', text: String(o.missing) }]; })"),
            Plugin("result", "api.on('onDayStart', function () { var o = []; var c = o; for (var i = 0; i < 1e6; i++) { c[0] = []; c = c[0]; } return o; })"),
            Plugin("ok", "api.on('onDayStart', () => [{ type: 'message', text: 'ok' }])"));
        var results = host.Dispatch("onDayStart", Day3);
        Assert.Equal("ok", Texts(results));
        Assert.Equal(3, results.Count(r => r.Errors.Count > 0));
    }

    [Fact]
    public void BudgetsResetBetweenDispatches()
    {
        // ~1/3 of the statement budget per call: three calls in a row must all pass.
        using var host = Host(new JintPluginHostOptions { TimeoutMs = 5000, InitTimeoutMs = 5000, MaxStatements = 30_000 },
            Plugin("busy", "api.on('onDayStart', function () { var n = 0; for (var i = 0; i < 10000; i++) n += i; return [{ type: 'message', text: String(n) }]; })"));
        for (var i = 0; i < 3; i++) Assert.Equal("49995000", Texts(host.Dispatch("onDayStart", Day3)));
    }

    [Fact]
    public void DisposedHostsDispatchNothing()
    {
        var host = Host(null, Plugin("ok", "api.on('onDayStart', () => [{ type: 'message', text: 'ok' }])"));
        host.Dispose();
        Assert.Empty(host.Dispatch("onDayStart", Day3));
        host.Dispose();
    }

    // --- bridge: hooks → queued commands → command log ---

    [Fact]
    public void BridgeQueuesMutationsFromHooksAndDrainsThemAsCommands()
    {
        var project = EngineTests.MakeProject() with
        {
            ContentPacks =
            [
                new PackInstallation
                {
                    Enabled = true,
                    Pack = Pack("gifts", ["onDayStart"], Plugin("daily", "api.on('onDayStart', p => [{ type: 'giveMoney', amount: p.day * 10 }, { type: 'message', text: 'Day ' + p.day }])")),
                },
                new PackInstallation
                {
                    Enabled = false,
                    Pack = Pack("off", ["onDayStart"], Plugin("never", "api.on('onDayStart', () => [{ type: 'giveMoney', amount: 1 }])")),
                },
            ],
        };
        var (ctx, bridge) = PluginBridge.CreateEngineContext(project, Relaxed);
        Assert.NotNull(bridge);
        using (bridge)
        {
            Assert.Equal(["onDayStart"], bridge.HookNames);
            var state = EngineState.CreateGameState(project, seed: "bridge");
            var slept = FarmEngine.Core.Engine.ApplyCommand(ctx, state, new SleepCommand());
            Assert.Equal(2, bridge.Queue.Size);

            var commands = bridge.DrainCommands();
            Assert.Equal(0, bridge.Queue.Size);
            Assert.Equal(["gifts:daily", "gifts:daily"], commands.Select(c => c.PluginId));
            var money = slept.State.Player.Money;
            var next = slept.State;
            foreach (var command in commands) next = FarmEngine.Core.Engine.ApplyCommand(ctx, next, command).State;
            Assert.Equal(money + 20, next.Player.Money);

            // The drained commands are ordinary, serializable command-log entries.
            Assert.Equal(
                """{"type":"pluginMutation","pluginId":"gifts:daily","mutation":{"type":"giveMoney","amount":20}}""",
                JsonDefaults.Serialize<Command>(commands[0]));
        }
    }

    [Fact]
    public void BridgeReportsErrorsAndIgnoresThrowingPlugins()
    {
        var hooks = new HookBus();
        var specs = Plugins.PluginSpecsFromPacks([Pack("p", ["onAction"],
            Plugin("bad", "api.on('onAction', () => { throw new Error('kaput') })", "onAction"),
            Plugin("init", "throw new Error('no init')", "onAction"))]);
        var reported = new List<PluginError>();
        using var bridge = new PluginBridge(hooks, new JintPluginHost(specs, Relaxed), specs);
        bridge.ErrorReported += reported.Add;
        hooks.Emit(HookNames.OnAction, new ActionHookPayload("a"));
        Assert.Equal(0, bridge.Queue.Size);
        Assert.Equal([PluginErrorKinds.Threw], reported.Select(e => e.Kind));
        Assert.Equal([PluginErrorKinds.Init, PluginErrorKinds.Threw], bridge.RecentErrors.Select(e => e.Kind));
    }

    [Fact]
    public void ProjectsWithoutPluginsGetNoBridge()
    {
        var (ctx, bridge) = PluginBridge.CreateEngineContext(EngineTests.MakeProject());
        Assert.Null(bridge);
        Assert.NotNull(ctx.Hooks);
    }
}
