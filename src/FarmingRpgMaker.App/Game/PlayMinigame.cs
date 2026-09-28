using FarmEngine.Core;
using FarmEngine.Interop;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>Plain minigame presentation data. Scoring belongs to the selected runtime.</summary>
public sealed record PlayMinigameView
{
    public string Kind { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string ButtonText { get; init; } = "";
    public bool IsDone { get; init; }
    public double? Score { get; init; }
    public double Position { get; init; }
    public double TargetLeft { get; init; }
    public double TargetSize { get; init; }
    public string Status { get; init; } = "";
    public string Log { get; init; } = "";
    public List<string> Choices { get; init; } = [];
}

/// <summary>Host adapter: forwards input and routes one completion through the command log.</summary>
public sealed class PlayMinigame : IMinigameSession
{
    private readonly PlaySession _owner;
    private readonly RustSession? _rust;
    private readonly IMinigameSession? _managed;
    private readonly ulong _generation;
    private bool _disposed;
    private bool _reported;
    private double? _score;
    private bool _cancelled;

    internal PlayMinigame(PlaySession owner, MinigameRegistry registry, MinigameDef? definition, double random)
    {
        _owner = owner;
        if (owner.Engine is RustPlayEngine engine)
        {
            _rust = engine.Session;
            var mounted = _rust.Runtime<Mounted>(new { type = "mountMinigame", random });
            _generation = mounted.Generation;
            View = mounted.View;
        }
        else
        {
            _managed = Minigames.MinigameImplFor(registry, definition).Mount(new MinigameMountOptions(
                definition?.Config ?? new OrderedDictionary<string, System.Text.Json.JsonElement>(),
                score => _score = score, () => _cancelled = true, () => random));
            View = ManagedView();
        }
    }

    private sealed record Mounted(ulong Generation, PlayMinigameView View);
    public PlayMinigameView View { get; private set; }
    public string Kind => View.Kind;
    public string Prompt => View.Prompt;
    public string ButtonText => View.ButtonText;
    public bool IsDone => _disposed || View.IsDone;
    public double? Score => View.Score;

    public void Update(double deltaSeconds) => Send(new { type = "update", seconds = deltaSeconds }, () => _managed!.Update(deltaSeconds));
    public void Press() => Send(new { type = "press" }, () => _managed!.Press());
    public void Release() => Send(new { type = "release" }, () => _managed!.Release());
    public void Act(string choice) => Send(new { type = "act", choice }, () => ((SimpleBattleSession)_managed!).Act(choice));

    private void Send(object input, Action managed) => _owner.RunRuntime(() => SendCore(input, managed));

    private void SendCore(object input, Action managed)
    {
        if (IsDone) return;
        if (_rust is not null)
            View = _rust.Runtime<PlayMinigameView>(new { type = "minigameInput", generation = _generation, input });
        else
        {
            managed();
            View = ManagedView();
        }
        // Publish the view before invoking a command: its StateChanged callback can dispose
        // this overlay synchronously. No FFI calls occur after that callback.
        if (!_reported && View.Score is { } score)
        {
            _reported = true;
            _owner.RunCommand(new ResolveMinigameCommand(score));
        }
        else if (!_reported && _cancelled)
        {
            _reported = true;
            _owner.RunCommand(new CancelMinigameCommand());
        }
    }

    private PlayMinigameView ManagedView()
    {
        var bar = _managed as TimingBarSession;
        var battle = _managed as SimpleBattleSession;
        return new()
        {
            Kind = _managed!.Kind, Prompt = _managed.Prompt, ButtonText = _managed.ButtonText,
            IsDone = _managed.IsDone, Score = _score,
            Position = bar?.Position ?? 0, TargetLeft = bar?.TargetLeft ?? 0, TargetSize = bar?.TargetSize ?? 0,
            Status = battle?.Status ?? "", Log = battle?.Log ?? "", Choices = battle is null ? [] : [.. SimpleBattleSession.Choices],
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _managed?.Dispose();
        // The parent session owns native lifetime. A replacement mount supersedes this token.
        if (_rust is not null && !_rust.IsPoisoned)
        {
            try { _rust.Runtime<object?>(new { type = "disposeMinigame", generation = _generation }); }
            catch (ObjectDisposedException) { }
        }
    }
}
