using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FarmEngine.Core;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Playtest host for the minigame framework (web MinigameOverlay): mounts the runtime
/// implementation registered for the def's kind (timing-bar, hold-to-catch, simple-battle,
/// fallback), advances it every frame, and forwards its score into the engine as the
/// deterministic <c>resolveMinigame</c> command.
/// </summary>
internal sealed class MinigameOverlay : IDisposable
{
    private const double BarWidth = 320;
    private readonly PlaySession _session;
    private readonly Border _marker;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13.5 };
    private readonly TextBlock _log = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private readonly Button _primary;

    public MinigameOverlay(PlaySession session, MinigameRegistry registry, MinigameSession minigame)
    {
        _session = session;
        State = minigame;
        Definition = session.Content.Minigames.FirstOrDefault(def => def.Id == minigame.MinigameId);
        var impl = Minigames.MinigameImplFor(registry, Definition);
        Session = impl.Mount(new MinigameMountOptions(
            Definition?.Config ?? new OrderedDictionary<string, System.Text.Json.JsonElement>(),
            score => _session.RunCommand(new ResolveMinigameCommand(score)),
            () => _session.RunCommand(new CancelMinigameCommand())));

        var body = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Width = BarWidth + 20 };
        body.Children.Add(Ui.Centered(Ui.Wrapped(Session.Prompt)));
        _marker = new Border { Width = 3, Background = new SolidColorBrush(Color.Parse("#FFD94A")), Height = 26 };
        if (Session is TimingBarSession bar)
        {
            var canvas = new Canvas { Width = BarWidth, Height = 26, ClipToBounds = true };
            canvas.Children.Add(new Rectangle { Width = BarWidth, Height = 26, Fill = new SolidColorBrush(Color.Parse("#3A4033")), RadiusX = 6, RadiusY = 6 });
            var zone = new Border
            {
                Width = bar.TargetSize * BarWidth,
                Height = 26,
                Background = new SolidColorBrush(Color.Parse("#558FD06C")),
                BorderBrush = new SolidColorBrush(Color.Parse("#8FD06C")),
                BorderThickness = new Thickness(1),
            };
            Canvas.SetLeft(zone, bar.TargetLeft * BarWidth);
            canvas.Children.Add(zone);
            canvas.Children.Add(_marker);
            body.Children.Add(new Border { Child = canvas, CornerRadius = new CornerRadius(6), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center });
        }
        else if (Session is SimpleBattleSession battle)
        {
            body.Children.Add(_status);
            body.Children.Add(_log);
            var actions = Ui.HStack(8);
            actions.HorizontalAlignment = HorizontalAlignment.Center;
            foreach (var choice in SimpleBattleSession.Choices)
            {
                actions.Children.Add(Ui.Button(Ui.Capitalize(choice), () => { battle.Act(choice); Refresh(); }, "tool"));
            }

            body.Children.Add(actions);
        }

        _primary = new Button { Name = "MinigamePrimary", HorizontalAlignment = HorizontalAlignment.Center, Content = Session.ButtonText };
        _primary.Classes.Add("accent");
        _primary.Classes.Add("large");
        // Press/release semantics (hold-to-catch needs both).
        _primary.AddHandler(InputElement.PointerPressedEvent, (_, _) => Press(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _primary.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Release(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (Session is not SimpleBattleSession)
        {
            body.Children.Add(_primary);
            body.Children.Add(Ui.Centered(Ui.Text("Space / Enter", "muted", "small")));
        }

        var giveUp = Ui.Button("Give up", () => _session.RunCommand(new CancelMinigameCommand()), "tool");
        View = new Border
        {
            Child = Ui.ModalCard("MinigameOverlay", "IconStar", Definition?.Name ?? minigame.MinigameId, Definition?.Kind, body, () => _session.RunCommand(new CancelMinigameCommand()), 420, giveUp, "Give up (Esc)"),
        }.WithClasses("backdrop");
        Refresh();
    }

    public MinigameSession State { get; }

    public MinigameDef? Definition { get; }

    public IMinigameSession Session { get; }

    public Control View { get; }

    public void Update(double deltaSeconds)
    {
        if (!Session.IsDone)
        {
            Session.Update(deltaSeconds);
        }

        Refresh();
    }

    public void Press()
    {
        if (!Session.IsDone)
        {
            Session.Press();
        }
    }

    public void Release()
    {
        if (!Session.IsDone)
        {
            Session.Release();
        }
    }

    public void Dispose() => Session.Dispose();

    private void Refresh()
    {
        if (Session is TimingBarSession bar)
        {
            Canvas.SetLeft(_marker, Math.Clamp(bar.Position, 0, 1) * (BarWidth - 3));
        }

        if (Session is SimpleBattleSession battle)
        {
            _status.Text = battle.Status;
            _log.Text = battle.Log;
        }

        if (!Equals(_primary.Content, Session.ButtonText))
        {
            _primary.Content = Session.ButtonText;
        }
    }
}
