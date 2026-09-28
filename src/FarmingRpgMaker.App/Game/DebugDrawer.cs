using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using FarmEngine.Interop;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Playtest debug drawer: money, energy, skip day, +1 hour, season, items, teleport, flags.
/// These bypass the command log ON PURPOSE (creator tooling, not gameplay) through
/// <see cref="RustPlayer.Debug"/>.
/// </summary>
internal static class DebugDrawer
{
    public static Control Build(PlayModeView view, PlayerSummary summary, Action onClose)
    {
        void Act(object action, string message)
        {
            try
            {
                view.Use(player => player.Debug(action));
                view.ShowToast(new ToastMessage(message, ToastKind.Success));
            }
            catch (FarmFfiException ex)
            {
                view.ShowToast(new ToastMessage(ex.Message, ToastKind.Error));
            }

            view.RebuildDebug();
        }

        var stack = new StackPanel { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(Ui.Text("Playtest Debug", "h2"));
        var close = Ui.Button(Ui.Icon("IconClose", 12), onClose, "subtle");
        close.Name = "DebugCloseButton";
        close.Padding = new Thickness(6);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        stack.Children.Add(header);

        var quick = new UniformGrid { Columns = 2 };
        void Quick(string name, string label, object action, string message)
        {
            var button = Ui.Button(label, () => Act(action, message), "tool");
            button.Name = name;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 0, 4, 4);
            quick.Children.Add(button);
        }

        Quick("DebugAddMoney", "+$500", new { type = "addMoney", amount = 500 }, "+$500");
        Quick("DebugFullEnergy", "Full energy", new { type = "fullEnergy" }, "Energy restored");
        Quick("DebugSkipDay", "Skip day", new { type = "skipDay" }, "Advanced one day");
        Quick("DebugAddHour", "+1 hour", new { type = "addMinutes", minutes = 60 }, "+1 hour");
        stack.Children.Add(quick);

        stack.Children.Add(Ui.Text("SEASON", "section"));
        var seasons = new WrapPanel { Name = "DebugSeasons" };
        foreach (var season in summary.Seasons)
        {
            var button = Ui.Button(season.Name.Length > 3 ? season.Name[..3] : season.Name, () => Act(new { type = "setSeason", season = season.Id }, $"Season: {season.Name}"), "tool", "small");
            button.Margin = new Thickness(0, 0, 4, 4);
            if (summary.Season == season.Id)
            {
                button.Classes.Add("accent");
            }

            seasons.Children.Add(button);
        }

        stack.Children.Add(seasons);

        stack.Children.Add(Ui.Text("GIVE 5 OF FIRST SEED / MATERIAL", "section"));
        var give = new UniformGrid { Columns = 2 };
        void Give(string name, string label, string type, string message)
        {
            var button = Ui.Button(label, () => Act(new { type = "giveFirst", itemType = type }, message), "tool");
            button.Name = name;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 0, 4, 0);
            give.Children.Add(button);
        }

        Give("DebugGiveSeeds", "Seeds ×5", "seed", "Seeds granted");
        Give("DebugGiveMaterials", "Materials ×5", "material", "Materials granted");
        stack.Children.Add(give);

        stack.Children.Add(Ui.Text("TELEPORT", "section"));
        var scenes = new WrapPanel { Name = "DebugScenes" };
        foreach (var scene in summary.Scenes)
        {
            var button = Ui.Button(scene.Name, () => Act(new { type = "teleport", sceneId = scene.Id }, $"Teleported to {scene.Name}"), "tool", "small");
            button.Margin = new Thickness(0, 0, 4, 4);
            if (summary.SceneId == scene.Id)
            {
                button.Classes.Add("accent");
            }

            scenes.Children.Add(button);
        }

        stack.Children.Add(scenes);

        stack.Children.Add(Ui.Text("SET FLAG", "section"));
        var flagBox = new TextBox { Name = "DebugFlagName", Watermark = "flag-name", MinWidth = 150, FontSize = 12.5 };
        var set = Ui.Button("Set", () =>
        {
            var flag = flagBox.Text?.Trim();
            if (string.IsNullOrEmpty(flag))
            {
                return;
            }

            Act(new { type = "setFlag", flag }, $"Flag \"{flag}\" set");
        }, "accent", "small");
        set.Name = "DebugSetFlag";
        var flagRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        flagRow.Children.Add(flagBox);
        set.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(set, 1);
        flagRow.Children.Add(set);
        stack.Children.Add(flagRow);

        var status = Ui.Wrapped(
            string.Create(CultureInfo.InvariantCulture, $"Day {summary.Day:0} · {summary.TimeText} · tick {summary.Tick:0} · {summary.SceneId} ({summary.X:0.00}, {summary.Y:0.00}) · ${summary.Money:0} · seed {summary.Seed}"),
            "muted", "small");
        status.Name = "DebugStatus";
        stack.Children.Add(status);

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
}
