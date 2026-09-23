using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using FarmEngine.Core;
using FarmEngine.Json;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Playtest debug drawer (web DebugDrawer, M3): money, energy, skip day, +1 hour, season,
/// items, teleport, flags. These mutations bypass the command pipeline ON PURPOSE (creator
/// tooling, not gameplay) through <see cref="PlaySession.DebugMutate"/>.
/// </summary>
internal static class DebugDrawer
{
    public static Control Build(PlaySession session, Action<ToastMessage> toast, Action onClose)
    {
        void Mutate(Func<GameState, EngineContext, GameState> transform, string message)
        {
            session.DebugMutate(transform);
            toast(new ToastMessage(message, ToastKind.Success));
        }

        var stack = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Ui.Text("Playtest Debug", "h2"));
        var close = Ui.Button(Ui.Icon("IconClose", 12), onClose, "subtle");
        close.Padding = new Thickness(6);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        stack.Children.Add(header);

        var quick = new UniformGrid { Columns = 2 };
        void Quick(string label, Func<GameState, EngineContext, GameState> transform, string message)
        {
            var button = Ui.Button(label, () => Mutate(transform, message), "tool");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 0, 4, 4);
            quick.Children.Add(button);
        }

        Quick("+$500", (s, _) => s with { Player = s.Player with { Money = s.Player.Money + 500 } }, "+$500");
        Quick("Full energy", (s, _) => s with { Player = s.Player with { Energy = s.Player.MaxEnergy } }, "Energy restored");
        Quick("Skip day", (s, ctx) => GameTime.PerformSleep(ctx, s, new SleepOptions(Collapsed: false)).State, "Advanced one day");
        Quick("+1 hour", (s, _) => s with { Clock = s.Clock with { TimeMinutes = s.Clock.TimeMinutes + 60 } }, "+1 hour");
        stack.Children.Add(quick);

        stack.Children.Add(Ui.Text("SEASON", "section"));
        var seasons = new WrapPanel();
        foreach (var season in GameTime.CalendarSeasons(session.Content.Settings.Calendar))
        {
            var id = season.Id;
            var button = Ui.Button(season.Name.Length > 3 ? season.Name[..3] : season.Name, () => Mutate((s, _) => s with { Clock = s.Clock with { Season = id } }, $"Season: {season.Name}"), "tool", "small");
            button.Margin = new Thickness(0, 0, 4, 4);
            if (session.State.Clock.Season == id)
            {
                button.Classes.Add("accent");
            }

            seasons.Children.Add(button);
        }

        stack.Children.Add(seasons);

        stack.Children.Add(Ui.Text("GIVE 5 OF FIRST SEED / MATERIAL", "section"));
        var give = new UniformGrid { Columns = 2 };
        void Give(string label, string type, string message)
        {
            var button = Ui.Button(label, () => Mutate((s, ctx) => GiveFirst(s, ctx, type), message), "tool");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 0, 4, 0);
            give.Children.Add(button);
        }

        Give("Seeds ×5", "seed", "Seeds granted");
        Give("Materials ×5", "material", "Materials granted");
        stack.Children.Add(give);

        stack.Children.Add(Ui.Text("TELEPORT", "section"));
        var scenes = new WrapPanel();
        foreach (var scene in session.State.World.Scenes)
        {
            var target = scene;
            var button = Ui.Button(scene.Name, () => Mutate((s, _) => s with
            {
                Player = s.Player with
                {
                    SceneId = target.Id,
                    // Free movement: land on the center tile's center.
                    X = Math.Floor(target.Width / 2) + 0.5,
                    Y = Math.Floor(target.Height / 2) + 0.5,
                },
            }, $"Teleported to {target.Name}"), "tool", "small");
            button.Margin = new Thickness(0, 0, 4, 4);
            if (session.State.Player.SceneId == scene.Id)
            {
                button.Classes.Add("accent");
            }

            scenes.Children.Add(button);
        }

        stack.Children.Add(scenes);

        stack.Children.Add(Ui.Text("SET FLAG", "section"));
        var flagBox = new TextBox { Watermark = "flag-name", MinWidth = 150, FontSize = 12.5 };
        var set = Ui.Button("Set", () =>
        {
            var flag = flagBox.Text?.Trim();
            if (string.IsNullOrEmpty(flag))
            {
                return;
            }

            Mutate((s, _) => s with { Flags = new OrderedDictionary<string, System.Text.Json.JsonElement>(s.Flags) { [flag] = Js.Value(true) } }, $"Flag \"{flag}\" set");
            flagBox.Text = "";
        }, "accent", "small");
        var flagRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        flagRow.Children.Add(flagBox);
        set.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(set, 1);
        flagRow.Children.Add(set);
        stack.Children.Add(flagRow);

        var state = session.State;
        stack.Children.Add(Ui.Wrapped(
            $"Tick {Js.Num(state.Clock.Tick)} · {state.Player.SceneId} ({state.Player.X:0.00}, {state.Player.Y:0.00}) · seed {state.Meta.EngineSeed}",
            "muted", "small"));

        return new Border
        {
            Name = "DebugDrawer",
            Width = 300,
            Child = new ScrollViewer { Content = stack, MaxHeight = 520 },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
        }.WithClasses("debug");
    }

    private static GameState GiveFirst(GameState state, EngineContext ctx, string type)
    {
        var item = ctx.Content.Items.FirstOrDefault(i => i.Type == type);
        if (item is null)
        {
            return state;
        }

        var hasSlot = state.Player.Inventory.Any(s => s.Item.Id == item.Id);
        var inventory = hasSlot
            ? state.Player.Inventory.Select(s => s.Item.Id == item.Id ? s with { Quantity = s.Quantity + 5 } : s).ToList()
            : [.. state.Player.Inventory, new InventorySlot { Item = item, Quantity = 5 }];
        return state with { Player = state.Player with { Inventory = inventory } };
    }
}
