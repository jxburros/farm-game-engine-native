using System.Globalization;
using System.Text.Json;
using FarmEngine.Schemas;

namespace FarmEngine.Runtime;

// Port of minigames.ts + custom-game/minigames.ts.
//
// Host-side minigame framework. The simulation opens a session
// (`state.minigame`); the host mounts the implementation registered for the
// def's `kind`; the player's performance becomes a single score in [0, 1]
// that re-enters the engine as the deterministic `resolveMinigame` command.
//
// The TS implementations own DOM widgets. Here each implementation produces a
// host-agnostic session model: the host advances it with Update(dt), feeds it
// input (Press/Release or kind-specific calls) and draws its public state.
// Only OnComplete(score) crosses back into deterministic simulation.
//
// Game code extends this by registering new kinds:
//
//   registry.Register("my-rhythm-game", new MyRhythmGame());
//   // MyRhythmGame.Mount(options) returns an IMinigameSession that calls
//   // options.OnComplete(score) once.

/// <summary>TS <c>MinigameMountOptions</c>.</summary>
/// <param name="Config">The def's <c>config</c> record, verbatim.</param>
/// <param name="OnComplete">Resolve with a score in [0, 1]. Called at most once.</param>
/// <param name="OnCancel">Abort without resolving (host issues cancelMinigame).</param>
/// <param name="Random">Cosmetic randomness in [0, 1) (e.g. the timing-bar target placement); defaults to <see cref="System.Random.Shared"/>.</param>
public sealed record MinigameMountOptions(
    IReadOnlyDictionary<string, JsonElement> Config,
    Action<double> OnComplete,
    Action OnCancel,
    Func<double>? Random = null);

/// <summary>
/// A mounted minigame (TS <c>MinigameHandle</c> plus its UI state). Disposing
/// ends the session: no completion can be reported afterwards.
/// </summary>
public interface IMinigameSession : IDisposable
{
    /// <summary>The registry kind this session implements.</summary>
    string Kind { get; }
    /// <summary>Instruction text for the player.</summary>
    string Prompt { get; }
    /// <summary>Label of the primary button.</summary>
    string ButtonText { get; }
    /// <summary>True once the score was reported (or the session was disposed).</summary>
    bool IsDone { get; }
    /// <summary>Advance real time (seconds since the previous frame).</summary>
    void Update(double deltaSeconds);
    /// <summary>Primary input pressed (Space / Enter / click / pointer down).</summary>
    void Press();
    /// <summary>Primary input released (Space / Enter key-up / pointer up).</summary>
    void Release();
}

/// <summary>TS <c>MinigameImpl</c>: a registered minigame kind.</summary>
public interface IMinigameImpl
{
    IMinigameSession Mount(MinigameMountOptions options);
}

/// <summary>TS <c>MinigameRegistry</c>.</summary>
public sealed class MinigameRegistry
{
    private readonly Dictionary<string, IMinigameImpl> _kinds = new(StringComparer.Ordinal);

    public void Register(string kind, IMinigameImpl impl) => _kinds[kind] = impl;

    public IMinigameImpl? Get(string kind) => _kinds.TryGetValue(kind, out var impl) ? impl : null;

    public IReadOnlyCollection<string> Kinds => _kinds.Keys;
}

/// <summary>Registry helpers and the built-in kinds.</summary>
public static class Minigames
{
    public const string TimingBarKind = "timing-bar";

    /// <summary>Built-in 'timing-bar' minigame.</summary>
    public static readonly TimingBarMinigame TimingBar = new();

    /// <summary>Fallback for unregistered kinds: a single button that scores a neutral 0.5.</summary>
    public static readonly FallbackMinigame Fallback = new();

    /// <summary>Registry preloaded with the built-in kinds (plus the game's own, see <see cref="CustomGameMinigames"/>).</summary>
    public static MinigameRegistry CreateDefaultMinigameRegistry()
    {
        var registry = new MinigameRegistry();
        registry.Register(TimingBarKind, TimingBar);
        CustomGameMinigames.RegisterGameMinigames(registry);
        return registry;
    }

    /// <summary>Implementation for a def: registered kind, or the neutral fallback.</summary>
    public static IMinigameImpl MinigameImplFor(MinigameRegistry registry, MinigameDef? def) =>
        (def is not null ? registry.Get(def.Kind) : null) ?? Fallback;

    /// <summary>TS <c>numberConfig</c>: a finite number, else the fallback.</summary>
    internal static double NumberConfig(IReadOnlyDictionary<string, JsonElement> config, string key, double fallback) =>
        config.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble())
            ? value.GetDouble()
            : fallback;

    internal static string? StringConfig(IReadOnlyDictionary<string, JsonElement> config, string key) =>
        config.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Shared once-only completion plumbing.</summary>
public abstract class MinigameSessionBase : IMinigameSession
{
    private readonly MinigameMountOptions _options;

    protected MinigameSessionBase(MinigameMountOptions options)
    {
        _options = options;
    }

    public abstract string Kind { get; }
    public abstract string Prompt { get; }
    public virtual string ButtonText { get; protected set; } = "";
    public bool IsDone { get; private set; }

    /// <summary>The score reported via OnComplete, if any.</summary>
    public double? Score { get; private set; }

    public virtual void Update(double deltaSeconds) { }
    public virtual void Press() { }
    public virtual void Release() { }

    /// <summary>Report the score (at most once).</summary>
    protected void Complete(double score)
    {
        if (IsDone) return;
        IsDone = true;
        Score = score;
        _options.OnComplete(score);
    }

    /// <summary>Abort without resolving: the host issues <c>cancelMinigame</c>.</summary>
    public void Cancel()
    {
        if (IsDone) return;
        IsDone = true;
        _options.OnCancel();
    }

    public virtual void Dispose()
    {
        IsDone = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Built-in 'timing-bar' minigame: a marker sweeps across a bar; stop it as
/// close to the target zone's center as possible. Score = 1 inside the zone,
/// else falling off linearly to 0 at a full bar-width from the center.
///
/// Config: <c>speed</c> (sweeps/second, default 0.9), <c>targetSize</c> (target zone
/// width as a bar fraction, default 0.18), <c>prompt</c> (instruction text).
/// Input: <see cref="IMinigameSession.Press"/> (Space / Enter / "Stop!" button) stops the marker.
/// </summary>
public sealed class TimingBarMinigame : IMinigameImpl
{
    public IMinigameSession Mount(MinigameMountOptions options) => new TimingBarSession(options);
}

public sealed class TimingBarSession : MinigameSessionBase
{
    private double _elapsedSeconds;

    public TimingBarSession(MinigameMountOptions options) : base(options)
    {
        Speed = Math.Max(0.1, Minigames.NumberConfig(options.Config, "speed", 0.9));
        TargetSize = Math.Min(0.9, Math.Max(0.02, Minigames.NumberConfig(options.Config, "targetSize", 0.18)));
        Prompt = Minigames.StringConfig(options.Config, "prompt") ?? "Stop the marker in the zone!";
        // Cosmetic placement only; the score is what enters the log.
        TargetCenter = 0.3 + 0.4 * (options.Random ?? System.Random.Shared.NextDouble)();
        ButtonText = "Stop! (Space)";
    }

    public override string Kind => Minigames.TimingBarKind;
    public override string Prompt { get; }
    /// <summary>Sweeps per second.</summary>
    public double Speed { get; }
    /// <summary>Target zone width as a fraction of the bar.</summary>
    public double TargetSize { get; }
    /// <summary>Target zone center as a fraction of the bar.</summary>
    public double TargetCenter { get; }
    /// <summary>Target zone left edge (bar fraction), for drawing.</summary>
    public double TargetLeft => TargetCenter - TargetSize / 2;

    /// <summary>Marker position in [0, 1]: a triangle wave sweeping right then left, continuously.</summary>
    public double Position
    {
        get
        {
            var t = _elapsedSeconds * Speed;
            var phase = t % 2;
            return phase <= 1 ? phase : 2 - phase;
        }
    }

    public override void Update(double deltaSeconds)
    {
        if (!IsDone) _elapsedSeconds += deltaSeconds;
    }

    /// <summary>Stop the marker and score (TS <c>stop()</c>).</summary>
    public override void Press() => Stop();

    public void Stop()
    {
        if (IsDone) return;
        var distance = Math.Abs(Position - TargetCenter);
        var inZone = distance <= TargetSize / 2;
        Complete(inZone ? 1 : Math.Max(0, 1 - distance));
    }
}

/// <summary>Fallback for unregistered kinds: a single "Go!" button that scores a neutral 0.5.</summary>
public sealed class FallbackMinigame : IMinigameImpl
{
    public IMinigameSession Mount(MinigameMountOptions options) => new FallbackSession(options);
}

public sealed class FallbackSession : MinigameSessionBase
{
    public FallbackSession(MinigameMountOptions options) : base(options)
    {
        Prompt = Minigames.StringConfig(options.Config, "prompt") ?? "Ready?";
        ButtonText = "Go!";
    }

    public override string Kind => "fallback";
    public override string Prompt { get; }
    public override void Press() => Complete(0.5);
}

/// <summary>
/// YOUR GAME CODE GOES HERE (port of custom-game/minigames.ts). Both the
/// editor and exported shell call this registration function. Add your own
/// implementations and register them. Each session owns its UI state;
/// Dispose must stop it. Only OnComplete(score) crosses back into
/// deterministic simulation.
/// </summary>
public static class CustomGameMinigames
{
    public static readonly HoldToCatchMinigame HoldToCatch = new();
    public static readonly SimpleBattleMinigame SimpleBattle = new();

    public static void RegisterGameMinigames(MinigameRegistry registry)
    {
        registry.Register("hold-to-catch", HoldToCatch);
        registry.Register("simple-battle", SimpleBattle);
    }

    /// <summary>TS <c>number(value, fallback, max = 100)</c>: a finite number clamped to [1, max], else the fallback.</summary>
    internal static double Number(IReadOnlyDictionary<string, JsonElement> config, string key, double fallback, double max = 100) =>
        config.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble())
            ? Math.Max(1, Math.Min(max, value.GetDouble()))
            : fallback;
}

/// <summary>
/// Example fishing replacement: hold the button, then release near the target.
/// Config: <c>holdMs</c> (target hold, default 1200, 1..10000).
/// Input: <see cref="IMinigameSession.Press"/> starts reeling, <see cref="IMinigameSession.Release"/> finishes;
/// <see cref="HoldToCatchSession.CancelHold"/> is a pointer-cancel.
/// </summary>
public sealed class HoldToCatchMinigame : IMinigameImpl
{
    public IMinigameSession Mount(MinigameMountOptions options) => new HoldToCatchSession(options);
}

public sealed class HoldToCatchSession : MinigameSessionBase
{
    private double _nowMs;
    private double? _startedMs;

    public HoldToCatchSession(MinigameMountOptions options) : base(options)
    {
        TargetMs = CustomGameMinigames.Number(options.Config, "holdMs", 1200, 10000);
        Prompt = $"Hold for {(TargetMs / 1000).ToString("F1", CultureInfo.InvariantCulture)} seconds, then release to reel in.";
        ButtonText = "Hold to reel (Space)";
    }

    public override string Kind => "hold-to-catch";
    public override string Prompt { get; }
    public double TargetMs { get; }
    /// <summary>Milliseconds held so far (null when not holding).</summary>
    public double? HeldMs => _startedMs is { } started ? _nowMs - started : null;

    public override void Update(double deltaSeconds) => _nowMs += deltaSeconds * 1000;

    public override void Press()
    {
        if (!IsDone && _startedMs is null)
        {
            _startedMs = _nowMs;
            ButtonText = "Reeling… release!";
        }
    }

    public override void Release()
    {
        if (IsDone || _startedMs is not { } started) return;
        Complete(Math.Max(0, 1 - Math.Abs(_nowMs - started - TargetMs) / TargetMs));
    }

    /// <summary>Pointer cancelled mid-hold: reset without scoring.</summary>
    public void CancelHold()
    {
        _startedMs = null;
        ButtonText = "Hold to reel (Space)";
    }
}

/// <summary>
/// Small turn-based encounter; win/loss consequences are authored as result
/// tiers (score 1 = win, 0 = loss). Config: <c>playerHealth</c> (30),
/// <c>enemyHealth</c> (24), <c>attack</c> (7), <c>enemyAttack</c> (5), <c>enemyName</c>.
/// Input: <see cref="SimpleBattleSession.Act"/> with "attack" | "guard" | "magic".
/// </summary>
public sealed class SimpleBattleMinigame : IMinigameImpl
{
    public IMinigameSession Mount(MinigameMountOptions options) => new SimpleBattleSession(options);
}

public sealed class SimpleBattleSession : MinigameSessionBase
{
    public static readonly IReadOnlyList<string> Choices = ["attack", "guard", "magic"];

    private readonly double _power;
    private readonly double _foePower;
    private double _turn;

    public SimpleBattleSession(MinigameMountOptions options) : base(options)
    {
        Hp = CustomGameMinigames.Number(options.Config, "playerHealth", 30);
        Enemy = CustomGameMinigames.Number(options.Config, "enemyHealth", 24);
        _power = CustomGameMinigames.Number(options.Config, "attack", 7);
        _foePower = CustomGameMinigames.Number(options.Config, "enemyAttack", 5);
        EnemyName = Minigames.StringConfig(options.Config, "enemyName") ?? "Forest slime";
    }

    public override string Kind => "simple-battle";
    public override string Prompt => Status;
    public double Hp { get; private set; }
    public double Enemy { get; private set; }
    public double Mana { get; private set; } = 3;
    public string EnemyName { get; }
    /// <summary>Latest battle narration (empty before the first turn).</summary>
    public string Log { get; private set; } = "";

    public string Status =>
        $"You: {FarmEngine.Json.Js.Num(Math.Max(0, Hp))} health · {FarmEngine.Json.Js.Num(Mana)} magic | {EnemyName}: {FarmEngine.Json.Js.Num(Math.Max(0, Enemy))} health";

    /// <summary>Primary input defaults to a plain attack.</summary>
    public override void Press() => Act("attack");

    public void Act(string kind)
    {
        if (IsDone || (kind == "magic" && Mana == 0)) return;
        var heavy = _turn % 3 == 2;
        if (kind == "attack") Enemy -= _power;
        if (kind == "magic")
        {
            Mana--;
            Enemy -= _power * 2;
        }
        if (Enemy > 0) Hp -= kind == "guard" ? 1 : _foePower * (heavy ? 2 : 1);
        _turn++;
        Log = _turn % 3 == 2
            ? $"{EnemyName} is preparing a heavy attack. Guard next turn!"
            : $"{EnemyName} attacks. Choose your next move.";
        if (Hp <= 0 || Enemy <= 0) Complete(Enemy <= 0 ? 1 : 0);
    }
}
