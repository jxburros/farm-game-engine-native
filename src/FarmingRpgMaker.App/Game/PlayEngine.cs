using System.Text.Json;
using FarmEngine.Core;
using FarmEngine.Interop;
using FarmEngine.Rendering;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>Which simulation runs a playtest.</summary>
public enum PlayEngineKind
{
    /// <summary>The Rust engine (<c>farm-sim</c> through <c>farm-ffi</c>): the default when its library loaded.</summary>
    Rust,

    /// <summary>The C# engine (<c>FarmEngine.Core</c>): the fallback, until it is deleted (roadmap phase 4).</summary>
    CSharp,
}

/// <summary>
/// The simulation behind a <see cref="PlaySession"/>: commands and ticks in, effects out, the
/// live state readable as C# records. Both engines emit their hook events on the session's
/// <see cref="EngineContext.Hooks"/> bus (where the plugin bridge listens) before the call
/// returns. One thread only.
/// </summary>
internal interface IPlayEngine : IDisposable
{
    PlayEngineKind Kind { get; }

    /// <summary>The live state (a mirror for Rust). Treat it as immutable.</summary>
    GameState State { get; }

    List<Effect> Apply(Command command);

    List<Effect> Tick(int ticks);

    /// <summary>Replaces the state as it is (creator debug tools only).</summary>
    void ReplaceState(GameState state);

    /// <summary>The project with the live state written back (<c>applyStateToProject</c>).</summary>
    GameProject SyncedProject();

    /// <summary>Rule-derived values for the currently displayed play overlays.</summary>
    PlayOverlayView OverlayView();

    WorldSnapshot Snapshot(ShellSnapshotOptions options);

    PlayRuntimeView RuntimeView(bool hostModalOpen);

    /// <summary>Creator debug action: advance through the overnight pass.</summary>
    void SkipDay();
}

/// <summary>One snapshot of the rule queries used by dialogue, shop and crafting views.</summary>
internal sealed record PlayOverlayView
{
    public Dialogue? Dialogue { get; init; }
    public List<DialogueOption> VisibleDialogueOptions { get; init; } = [];
    public ShopDefinition? Shop { get; init; }
    /// <summary>A null remainder denotes unlimited stock.</summary>
    public Dictionary<string, double?> StockRemaining { get; init; } = [];
    public GridPoint Facing { get; init; } = new();
    public Dictionary<string, CraftableStatus> Craftable { get; init; } = [];
    public Dictionary<string, bool> HasIngredients { get; init; } = [];

    public double Remaining(string itemId) => StockRemaining.GetValueOrDefault(itemId) ?? double.PositiveInfinity;

    public CraftableStatus Status(string recipeId) => Craftable.GetValueOrDefault(recipeId) ?? new CraftableStatus(false);

    public bool Ingredients(string recipeId) => HasIngredients.GetValueOrDefault(recipeId);

    public static PlayOverlayView FromCSharp(EngineContext context, GameState state)
    {
        var dialogue = state.Dialogue is { } activeDialogue
            ? DialogueSystem.FindDialogue(context, activeDialogue.NpcId, activeDialogue.DialogueId)
            : null;
        var shop = state.Shop is { } activeShop ? Economy.FindShop(context, activeShop.ShopId) : null;
        var facing = WorldMovement.FacingTarget(state);
        return new PlayOverlayView
        {
            Dialogue = dialogue,
            VisibleDialogueOptions = dialogue is null ? [] : Social.VisibleDialogueOptions(context, state, dialogue),
            Shop = shop,
            StockRemaining = shop?.Stock.ToDictionary(
                entry => entry.ItemId,
                entry =>
                {
                    var remaining = Economy.RemainingDailyStock(state, shop.Id, entry.ItemId, entry.DailyLimit);
                    return double.IsFinite(remaining) ? (double?)remaining : null;
                }) ?? [],
            Facing = new GridPoint { X = facing.X, Y = facing.Y },
            Craftable = context.Content.Recipes.ToDictionary(recipe => recipe.Id, recipe => Crafting.CraftableStatus(context, state, recipe)),
            HasIngredients = context.Content.Recipes.ToDictionary(recipe => recipe.Id, recipe => Crafting.HasIngredients(state, recipe)),
        };
    }
}

/// <summary>Plain view data; the selected engine evaluates the calendar and panel rules.</summary>
internal sealed record PlayRuntimeView
{
    public string? SeasonName { get; init; }
    public double SeasonDays { get; init; }
    public double DayOfSeason { get; init; }
    public string TimeLabel { get; init; } = "";
    public List<CalendarSeason> CalendarSeasons { get; init; } = [];
    public List<PanelView> Panels { get; init; } = [];

    public static PlayRuntimeView FromCSharp(GameProject project, GameContent content, GameState state, bool hostModalOpen)
    {
        var calendar = content.Settings.Calendar;
        var season = GameTime.SeasonById(calendar, state.Clock.Season);
        return new()
        {
            SeasonName = season?.Name,
            SeasonDays = season?.Days ?? ContentBuiltin.DaysPerSeason,
            DayOfSeason = GameTime.DayOfSeason(calendar, state.Clock.Day),
            TimeLabel = GameTime.FormatTimeOfDay(Math.Floor(state.Clock.TimeMinutes)),
            CalendarSeasons = GameTime.CalendarSeasons(calendar).ToList(),
            Panels = GamePanels.Render(project.GamePanels ?? [], PanelState.FromGameState(state, hostModalOpen)),
        };
    }
}

internal static class PlayEngines
{
    /// <summary>Environment variable that picks the engine: <c>csharp</c> or <c>rust</c>.</summary>
    public const string EnvironmentVariable = "FARM_ENGINE";

    /// <summary>
    /// The engine a new playtest uses: <c>FARM_ENGINE=csharp</c> forces the C# engine (handy
    /// when comparing behaviour); otherwise Rust whenever its library loaded.
    /// </summary>
    public static PlayEngineKind Default
    {
        get
        {
            var forced = Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim();
            if (string.Equals(forced, "csharp", StringComparison.OrdinalIgnoreCase) || string.Equals(forced, "c#", StringComparison.OrdinalIgnoreCase))
            {
                return PlayEngineKind.CSharp;
            }

            return FarmFfi.IsAvailable ? PlayEngineKind.Rust : PlayEngineKind.CSharp;
        }
    }

    /// <summary>
    /// Starts <paramref name="project"/> on <paramref name="kind"/> with auto-start quests begun.
    /// A project the Rust engine refuses to load (while both engines coexist) still plays on the
    /// C# engine; the refusal is traced.
    /// </summary>
    public static IPlayEngine Create(PlayEngineKind kind, EngineContext context, GameProject project)
    {
        if (kind == PlayEngineKind.Rust)
        {
            try
            {
                return new RustPlayEngine(context, project);
            }
            catch (FarmFfiException ex)
            {
                System.Diagnostics.Trace.TraceWarning($"The Rust engine could not start this project; playing it on the C# engine. {ex.Message}");
            }
        }

        return new CSharpPlayEngine(context, project);
    }
}

/// <summary>The C# engine: pure functions over immutable <see cref="GameState"/> records.</summary>
internal sealed class CSharpPlayEngine : IPlayEngine
{
    private readonly EngineContext _context;
    private readonly GameProject _project;

    public CSharpPlayEngine(EngineContext context, GameProject project)
    {
        _context = context;
        _project = project;
        // Auto-start quests activate when play begins (availability + prerequisites respected).
        State = Quests.AutoStartQuests(context, EngineState.CreateGameState(project));
    }

    public PlayEngineKind Kind => PlayEngineKind.CSharp;

    public GameState State { get; private set; }

    public List<Effect> Apply(Command command)
    {
        var step = Engine.ApplyCommand(_context, State, command);
        State = step.State;
        return step.Effects;
    }

    public List<Effect> Tick(int ticks)
    {
        var step = Engine.AdvanceTick(_context, State, ticks);
        State = step.State;
        return step.Effects;
    }

    public void ReplaceState(GameState state) => State = state;

    public GameProject SyncedProject() => EngineState.ApplyStateToProject(_project, State);

    public PlayOverlayView OverlayView() => PlayOverlayView.FromCSharp(_context, State);

    public PlayRuntimeView RuntimeView(bool hostModalOpen) => PlayRuntimeView.FromCSharp(_project, _context.Content, State, hostModalOpen);

    public WorldSnapshot Snapshot(ShellSnapshotOptions options)
    {
        var scene = State.World.Scenes.FirstOrDefault(scene => scene.Id == State.Player.SceneId)
            ?? State.World.Scenes.First();
        var moving = State.Player.MoveIntent.Dx != 0 || State.Player.MoveIntent.Dy != 0;
        var snapshot = ShellSnapshot.BuildShellSnapshot(_context.Content, State, scene, options);
        return Graphics.ApplyGraphics(snapshot, GraphicsSource.FromState(_project, _context.Content, State), scene, State.Clock.Tick, moving);
    }

    public void SkipDay() => State = GameTime.PerformSleep(_context, State, new SleepOptions(Collapsed: false)).State;

    public void Dispose()
    {
    }
}

/// <summary>
/// The Rust engine through <see cref="RustSession"/>. After every call the changed state
/// sections are copied into a <see cref="GameStateMirror"/> (unchanged ones keep their
/// objects), and the hook events Rust collected are replayed on the C# hook bus, so the plugin
/// bridge dispatches them exactly as it does for the C# engine.
/// </summary>
internal sealed class RustPlayEngine : IPlayEngine
{
    private readonly EngineContext _context;
    private readonly RustSession _session;
    private readonly GameStateMirror _mirror = new();

    public RustPlayEngine(EngineContext context, GameProject project)
    {
        _context = context;
        // No seed: the project's own seed applies, as EngineState.CreateGameState(project) does.
        _session = RustSession.Create(project, seed: null, autoStartQuests: true);
        try
        {
            AfterCall();
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public PlayEngineKind Kind => PlayEngineKind.Rust;

    public GameState State => _mirror.State;

    /// <summary>The underlying session (tests: hashes, saves).</summary>
    public RustSession Session => _session;

    public List<Effect> Apply(Command command)
    {
        var effects = _session.Apply(command);
        AfterCall();
        return effects;
    }

    public List<Effect> Tick(int ticks)
    {
        if (ticks <= 0)
        {
            return [];
        }

        var effects = _session.Tick((uint)ticks);
        AfterCall();
        return effects;
    }

    public void ReplaceState(GameState state)
    {
        _session.SetState(state);
        AfterCall();
    }

    public GameProject SyncedProject() => _session.SyncedProject();

    public PlayOverlayView OverlayView() => JsonSerializer.Deserialize<PlayOverlayView>(_session.OverlayJson(), FarmEngine.Json.JsonDefaults.Options)!;

    public PlayRuntimeView RuntimeView(bool hostModalOpen) =>
        JsonSerializer.Deserialize<PlayRuntimeView>(_session.RuntimeJson(hostModalOpen), FarmEngine.Json.JsonDefaults.Options)!;

    public WorldSnapshot Snapshot(ShellSnapshotOptions options) =>
        JsonSerializer.Deserialize<WorldSnapshot>(_session.SnapshotJson(options), FarmEngine.Json.JsonDefaults.Options)!;

    public void SkipDay()
    {
        _session.SkipDay();
        AfterCall();
    }

    public void Dispose() => _session.Dispose();

    private void AfterCall()
    {
        _mirror.Apply(_session.StateChanges());

        // Rust collects hook events during the step; the C# engine emits them on the bus as they
        // happen. Replaying them here, before the step's effects are handled, keeps the order the
        // plugin bridge sees (engine hooks, then one onEffect per effect).
        var events = _session.DrainHookEvents();
        if (_context.Hooks is not { } hooks || events.GetArrayLength() == 0)
        {
            return;
        }

        foreach (var hookEvent in events.EnumerateArray())
        {
            var hook = hookEvent.GetProperty("hook").GetString();
            if (hook is not null && hookEvent.TryGetProperty("payload", out var payload))
            {
                hooks.Emit(hook, payload);
            }
        }
    }
}
