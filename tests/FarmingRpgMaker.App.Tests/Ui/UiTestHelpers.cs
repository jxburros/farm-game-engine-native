using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FarmingRpgMaker.App.Services;

namespace FarmingRpgMaker.App.Tests.Ui;

internal static class UiTestHelpers
{
    /// <summary>Runs dispatcher jobs until <paramref name="condition"/> holds (or fails after ~5 s).</summary>
    public static void PumpUntil(Func<bool> condition, string because = "condition")
    {
        for (var i = 0; i < 500; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"Timed out waiting for {because}.");
    }

    public static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Clicks the centre of <paramref name="control"/> with the headless mouse.</summary>
    public static void Click(TopLevel window, Control control)
    {
        Assert.True(control.IsEffectivelyVisible, $"{control.Name} should be visible before clicking");
        Assert.True(control.IsEffectivelyEnabled, $"{control.Name} should be enabled before clicking");
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Control is not in the window.");
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    public static T Find<T>(Window window, string name)
        where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    /// <summary>
    /// Finds a named control anywhere under <paramref name="root"/> (code-built views don't
    /// register names in the window's name scope).
    /// </summary>
    public static T FindByName<T>(Visual root, string name)
        where T : Control =>
        TryFindByName<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    public static T? TryFindByName<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants((Avalonia.LogicalTree.ILogical)root).OfType<T>().FirstOrDefault(c => c.Name == name);

    /// <summary>Text of every visible TextBlock/SelectableTextBlock under <paramref name="root"/>.</summary>
    public static List<string> VisibleTexts(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible)
            .Select(t => t.Text ?? t.Inlines?.Text ?? "")
            .Where(t => t.Length > 0)
            .ToList();

    public static string AllVisibleText(Visual root) => string.Join("\n", VisibleTexts(root));
}

internal sealed class RecordingUrlLauncher : IUrlLauncher
{
    public List<string> Opened { get; } = [];

    public void Open(string url) => Opened.Add(url);
}
