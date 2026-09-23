using FarmEngine.Core;
using FarmEngine.Rendering;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>Severity of a toast (web <c>toast.success/error/info</c>).</summary>
public enum ToastKind
{
    Info,
    Success,
    Error,
}

public sealed record ToastMessage(string Text, ToastKind Kind);

/// <summary>Host UI toggles requested by one frame's keyboard input.</summary>
public sealed record FrameToggles(bool Inventory, bool Quests, bool Crafting, bool Escape)
{
    public static readonly FrameToggles None = new(false, false, false, false);

    public bool Any => Inventory || Quests || Crafting || Escape;
}

/// <summary>
/// One running playtest: the deterministic engine loop of the web app's play mode
/// (src/App.tsx <c>update()</c> + packages/game-shell <c>Shell</c>), without any UI.
/// <list type="bullet">
/// <item>All gameplay goes through <see cref="Engine.ApplyCommand"/> /
/// <see cref="Engine.AdvanceTick"/>; the session never edits <see cref="GameState"/> itself
/// (the only exception is <see cref="DebugMutate"/>, the creator debug drawer, exactly as
/// on the web).</item>
/// <item>Frame time is fed in by the host (<see cref="Update"/>), converted to ticks by
/// <see cref="FixedTimestep"/>, so tests can drive it deterministically.</item>
/// <item>Effects become toasts, sounds, floating pops and interpolation resets.</item>
/// </list>
/// </summary>
public sealed class PlaySession : IDisposable
{
    /// <summary>Play-mode tile size in world pixels (web <c>TILE_SIZE_PLAY</c>).</summary>
    public const double TileSize = 32;

    /// <summary>Canvas padding in world pixels (web <c>CANVAS_PADDING</c>).</summary>
    public const double Padding = 12;

    /// <summary>Preferred viewport in tiles (web GameView <c>VIEW_TILES_X/Y</c>).</summary>
    public const double ViewTilesX = 20;

    public const double ViewTilesY = 13;

    /// <summary>Lifetime of a floating pop, ms (web <c>POP_LIFETIME_MS</c>).</summary>
    public const double PopLifetimeMs = 900;

    private readonly FixedTimestep _timestep = new();
    private readonly List<(SnapshotPop Pop, double BornAt)> _pops = [];
    private readonly IGameAudio _audio;
    private readonly PluginBridge? _plugins;
    private MoveVector _lastIntent = new(0, 0);
    private (double X, double Y, string SceneId)? _prevPlayer;
    private bool _disposed;

    public PlaySession(GameProject project, IGameAudio? audio = null, bool reducedMotion = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project = project;
        _audio = audio ?? NullGameAudio.Instance;
        ReducedMotion = reducedMotion;

        // Content ctx + hook bus + sandboxed plugin host for enabled packs (App.tsx
        // buildPlayEngine): plugin results are queued and drained once per frame.
        var engine = PluginBridge.CreateEngineContext(project);
        Context = engine.Ctx;
        _plugins = engine.Plugins;
        if (_plugins is not null)
        {
            _plugins.ErrorReported += error => PluginErrors.Add(error);
        }

        // Auto-start quests activate when play begins (availability + prerequisites respected).
        State = Quests.AutoStartQuests(Context, EngineState.CreateGameState(project));
    }

    /// <summary>The project the session started from (art, names, settings).</summary>
    public GameProject Project { get; }

    public EngineContext Context { get; }

    public GameContent Content => Context.Content;

    public GameState State { get; private set; }

    public InputManager Input { get; } = new();

    /// <summary>Plugin errors reported so far (init failures, throws, overruns).</summary>
    public List<PluginError> PluginErrors { get; } = [];

    /// <summary>True when enabled content packs run sandboxed plugins in this session.</summary>
    public bool HasPlugins => _plugins is not null;

    public bool ReducedMotion { get; set; }

    /// <summary>Accumulated frame time in ms (drives pops; deterministic under test).</summary>
    public double ElapsedMs { get; private set; }

    /// <summary>Interpolation alpha of the fixed timestep (0..1).</summary>
    public double Alpha => _timestep.Alpha;

    /// <summary>A message effect (or session notice) the host should show.</summary>
    public event EventHandler<ToastMessage>? Toast;

    /// <summary>
    /// Raised after any command or tick batch changed the state — the host refreshes its HUD
    /// and modal overlays (dialogue, shop, minigame) from <see cref="State"/>.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised on <c>sceneChanged</c> effects (the host snaps its camera).</summary>
    public event EventHandler<SceneChangedEffect>? SceneChanged;

    public Scene? CurrentScene =>
        State.World.Scenes.FirstOrDefault(scene => scene.Id == State.Player.SceneId) ?? State.World.Scenes.FirstOrDefault();

    /// <summary>True while an engine-owned modal (dialogue, shop, minigame) is open.</summary>
    public bool EngineModalOpen => State.Dialogue is not null || State.Shop is not null || State.Minigame is not null;

    /// <summary>The project with the live state written back (keep-changes / autosave bridge).</summary>
    public GameProject SyncedProject() => EngineState.ApplyStateToProject(Project, State);

    /// <summary>Run one command through the engine and react to its effects.</summary>
    public void RunCommand(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var step = Engine.ApplyCommand(Context, State, command);
        State = step.State;
        HandleEffects(step.Effects);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creator debug tooling (web <c>debugMutate</c>): applies a pure state transform that
    /// bypasses the command pipeline on purpose. Never used by gameplay UI.
    /// </summary>
    public void DebugMutate(Func<GameState, EngineContext, GameState> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        State = transform(State, Context);
        _prevPlayer = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One display frame (App.tsx <c>gameLoopCallbacks.update</c>): drain plugin mutations,
    /// sync the held movement intent, advance the fixed-timestep simulation, then turn this
    /// frame's one-shot key presses into commands. <paramref name="hostModalOpen"/> is true
    /// while an app panel (inventory, quests, crafting, …) is open: world input pauses.
    /// </summary>
    public FrameToggles Update(double deltaSeconds, bool hostModalOpen = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ElapsedMs += Math.Max(0, deltaSeconds) * 1000;

        // Plugin mutations enter the command log at ONE fixed point per frame.
        if (_plugins is not null)
        {
            foreach (var command in _plugins.DrainCommands())
            {
                RunCommand(command);
            }
        }

        // Free movement: only CHANGES of the held intent become commands; zero while any
        // modal is open.
        var vector = hostModalOpen ? new MoveVector(0, 0) : InputBindings.MoveIntent(Input, State);
        if (vector != _lastIntent)
        {
            _lastIntent = vector;
            RunCommand(new SetMoveIntentCommand(vector.Dx, vector.Dy));
        }

        var ticks = _timestep.Advance(deltaSeconds);
        if (ticks > 0)
        {
            _prevPlayer = (State.Player.X, State.Player.Y, State.Player.SceneId);
            var step = Engine.AdvanceTick(Context, State, ticks);
            State = step.State;
            HandleEffects(step.Effects);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        var frame = InputBindings.PollPlayFrame(Input, State, Content, hostModalOpen);
        foreach (var command in frame.Commands)
        {
            RunCommand(command);
        }

        Input.EndFrame();
        return frame.ToggleInventory || frame.ToggleQuests || frame.ToggleCrafting || frame.Escape
            ? new FrameToggles(frame.ToggleInventory, frame.ToggleQuests, frame.ToggleCrafting, frame.Escape)
            : FrameToggles.None;
    }

    /// <summary>Clears held keys and the sent intent (focus loss, leaving play).</summary>
    public void ReleaseInput()
    {
        Input.Clear();
        if (_lastIntent != new MoveVector(0, 0))
        {
            _lastIntent = new MoveVector(0, 0);
            RunCommand(new SetMoveIntentCommand(0, 0));
        }
    }

    /// <summary>World size of the current scene in pixels (contiguous tiles, no seams).</summary>
    public (double Width, double Height) WorldSize()
    {
        var scene = CurrentScene;
        return (((scene?.Width ?? 1) * TileSize) + (Padding * 2), ((scene?.Height ?? 1) * TileSize) + (Padding * 2));
    }

    /// <summary>
    /// The camera viewport (world pixels) for a host area of the given size at
    /// <paramref name="zoom"/>: never larger than the world, which is centered when smaller.
    /// </summary>
    public (double Width, double Height) ViewportFor(double hostWidth, double hostHeight, double zoom)
    {
        var (worldWidth, worldHeight) = WorldSize();
        return (Math.Min(worldWidth, Math.Max(TileSize, hostWidth / zoom)), Math.Min(worldHeight, Math.Max(TileSize, hostHeight / zoom)));
    }

    /// <summary>
    /// The frame's render snapshot (web <c>getPlayFrame</c> + GameView <c>buildSnapshot</c>):
    /// player interpolated between the last two tick states, follow camera clamped to the
    /// scene, pops, custom art.
    /// </summary>
    public WorldSnapshot BuildSnapshot(double viewWidth, double viewHeight)
    {
        var scene = CurrentScene ?? throw new InvalidOperationException("The game has no scenes.");
        var player = State.Player;
        var ix = player.X;
        var iy = player.Y;
        if (_prevPlayer is { } prev && prev.SceneId == player.SceneId)
        {
            var alpha = _timestep.Alpha;
            ix = prev.X + ((player.X - prev.X) * alpha);
            iy = prev.Y + ((player.Y - prev.Y) * alpha);
        }

        // World-pixel top-left of the player's tile-sized draw box (position is the box
        // CENTER in tile units; play mode has no tile gap).
        var pixelX = Padding + ((ix - 0.5) * TileSize);
        var pixelY = Padding + ((iy - 0.5) * TileSize);
        var (worldWidth, worldHeight) = WorldSize();
        var camera = Canvas2d.ComputeCamera(pixelX + (TileSize / 2), pixelY + (TileSize / 2), worldWidth, worldHeight, viewWidth, viewHeight);

        var snapshot = ShellSnapshot.BuildShellSnapshot(Content, State, scene, new ShellSnapshotOptions(TileSize, Padding, pixelX, pixelY, camera));
        _pops.RemoveAll(pop => ElapsedMs - pop.BornAt >= PopLifetimeMs);
        if (!ReducedMotion && _pops.Count > 0)
        {
            snapshot.Pops = _pops.Select(pop => new SnapshotPop
            {
                X = pop.Pop.X,
                Y = pop.Pop.Y,
                Text = pop.Pop.Text,
                Color = pop.Pop.Color,
                Age = (ElapsedMs - pop.BornAt) / PopLifetimeMs,
            }).ToList();
        }

        var moving = player.MoveIntent.Dx != 0 || player.MoveIntent.Dy != 0;
        return Graphics.ApplyGraphics(snapshot, GraphicsSource.FromState(Project, Content, State), scene, State.Clock.Tick, moving);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _plugins?.Dispose();
    }

    /// <summary>React to engine effects (toasts, sounds, juice, camera snaps).</summary>
    private void HandleEffects(List<Effect> effects)
    {
        foreach (var effect in effects)
        {
            // Observe-only hook for mods (M5).
            Context.Hooks?.Emit(HookNames.OnEffect, new EffectHookPayload(effect.Type));
            _audio.PlayForEffect(effect);
            switch (effect)
            {
                case CropHarvestedEffect harvested:
                    AddPop($"+{FarmEngine.Json.Js.Num(harvested.Quantity)}", "#8fd06c");
                    break;
                case QuestCompletedEffect:
                    AddPop("Quest ✓", "#ffd94a");
                    break;
                case MessageEffect message:
                    Toast?.Invoke(this, new ToastMessage(message.Text, message.Level switch
                    {
                        MessageLevels.Success => ToastKind.Success,
                        MessageLevels.Error => ToastKind.Error,
                        _ => ToastKind.Info,
                    }));
                    break;
                case SceneChangedEffect sceneChanged:
                    // Teleports/transitions never interpolate across scenes.
                    _prevPlayer = null;
                    SceneChanged?.Invoke(this, sceneChanged);
                    break;
            }
        }
    }

    private void AddPop(string text, string color)
    {
        if (ReducedMotion)
        {
            return;
        }

        _pops.Add((new SnapshotPop { X = State.Player.X, Y = State.Player.Y, Text = text, Color = color }, ElapsedMs));
    }
}
