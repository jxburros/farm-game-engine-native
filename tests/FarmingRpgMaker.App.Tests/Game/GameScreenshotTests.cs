using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FarmingRpgMaker.App.Game;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// Renders Play Mode and Edit Mode with Skia. Always checks a frame can be captured; set
/// <c>FRM_SCREENSHOT_DIR</c> to save the PNGs used in docs/media
/// (play-mode.png, play-dialogue.png, edit-mode.png).
/// </summary>
public sealed class GameScreenshotTests
{
    private static readonly string? OutputDir = Environment.GetEnvironmentVariable("FRM_SCREENSHOT_DIR");

    /// <summary>Holds a movement key until the player passes <paramref name="until"/> (max 3 s).</summary>
    private static void Walk(GameTestHost host, PhysicalKey key, Func<Snapshot, bool> until)
    {
        host.Window.KeyPressQwerty(key, RawInputModifiers.None);
        for (var i = 0; i < 180 && !until(Snapshot.Of(host)); i++)
        {
            host.Play.AdvanceFrame(1.0 / 60);
        }

        host.Window.KeyReleaseQwerty(key, RawInputModifiers.None);
        host.Frames(1);
    }

    private static void Tap(GameTestHost host, PhysicalKey key)
    {
        host.Window.KeyPressQwerty(key, RawInputModifiers.None);
        host.Frames(1);
        host.Window.KeyReleaseQwerty(key, RawInputModifiers.None);
        host.Frames(1);
    }

    /// <summary>Walks along row 9 to column <paramref name="x"/>, faces the field and runs the tool keys.</summary>
    private static void WorkTile(GameTestHost host, int x, params PhysicalKey[] keys)
    {
        // Back onto the path row below the field first (sleeping moves the player home).
        if (Snapshot.Of(host).Player.Y > 9.55)
        {
            Walk(host, PhysicalKey.W, s => s.Player.Y <= 9.55);
        }

        var target = x + 0.5;
        if (Snapshot.Of(host).Player.X > target)
        {
            Walk(host, PhysicalKey.A, s => s.Player.X <= target + 0.05);
        }
        else
        {
            Walk(host, PhysicalKey.D, s => s.Player.X >= target - 0.05);
        }

        Tap(host, PhysicalKey.W); // face the field (row 8)
        foreach (var key in keys)
        {
            Tap(host, key);
        }

        Walk(host, PhysicalKey.S, s => s.Player.Y >= 9.45);
    }

    [AvaloniaFact]
    public void PlayMode_StarterFarm()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        host.Frames(5);

        // Plant and water a row of wheat, sleep, water again — a field in progress.
        for (var x = 6; x <= 10; x++)
        {
            WorkTile(host, x, PhysicalKey.E, PhysicalKey.Q);
        }

        for (var day = 0; day < 2; day++)
        {
            Tap(host, PhysicalKey.Z);
            for (var x = 10; x >= 6; x--)
            {
                WorkTile(host, x, PhysicalKey.Q);
            }
        }

        WorkTile(host, 11, PhysicalKey.E, PhysicalKey.Q);
        Walk(host, PhysicalKey.D, s => s.Player.X >= 12.3);
        host.Frames(20);

        var field = Snapshot.Of(host).State["world"]!["scenes"]![0]!["tiles"]![8]!.AsArray();
        Assert.True(field.Count(t => t!["crop"] is not null) >= 5, "the row should be planted");
        Save(host, "play-mode.png");
    }

    [AvaloniaFact]
    public void PlayMode_Dialogue()
    {
        using var host = new GameTestHost();
        host.EnterPlay();
        host.Frames(2);

        // Walk to the Old Farmer (3, 6) and talk to him.
        Walk(host, PhysicalKey.W, s => s.Player.Y <= 7.55);
        Walk(host, PhysicalKey.A, s => s.Player.X <= 3.55);
        Walk(host, PhysicalKey.W, s => s.Player.Y <= 7.45);
        Tap(host, PhysicalKey.E);
        host.Frames(3);

        var snapshot = Snapshot.Of(host);
        Assert.True((string?)snapshot.State["dialogue"]?["npcId"] == "npc-farmer", $"player at ({snapshot.Player.X}, {snapshot.Player.Y})");
        Save(host, "play-dialogue.png");
    }

    [AvaloniaFact]
    public void EditMode_StarterFarm()
    {
        using var host = new GameTestHost();
        var edit = host.Surface.EditView;
        Pump();
        var point = edit.Canvas.TranslatePoint(edit.Canvas.TileRect(12, 3).Center, host.Window)!.Value;
        host.Window.MouseMove(point);
        Pump();
        Assert.StartsWith("(12, 3)", edit.HoverText, StringComparison.Ordinal);
        Save(host, "edit-mode.png");
    }

    /// <summary>The live state read back from the Rust player.</summary>
    private sealed record Snapshot(System.Text.Json.Nodes.JsonObject State)
    {
        public (double X, double Y) Player => (State["player"]!["x"]!.GetValue<double>(), State["player"]!["y"]!.GetValue<double>());

        public static Snapshot Of(GameTestHost host) => new(host.Play.Use(player => player.State()));
    }

    private static void Save(GameTestHost host, string fileName)
    {
        var frame = GameTestHost.Capture(host.Window);
        Assert.True(frame.PixelSize.Width > 0);
        if (!string.IsNullOrEmpty(OutputDir))
        {
            Directory.CreateDirectory(OutputDir);
            frame.Save(Path.Combine(OutputDir, fileName));
        }
    }
}
