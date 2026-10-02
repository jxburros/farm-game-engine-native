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
    /// <summary>
    /// How long <see cref="PumpUntil"/> waits before it fails: 60 s, or
    /// <c>FARM_TEST_WAIT_SECONDS</c>. A passing wait returns as soon as its condition holds, so
    /// the budget only decides how long a broken test hangs, never how long a slow machine may take.
    /// </summary>
    public static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("FARM_TEST_WAIT_SECONDS"), out var seconds) && seconds > 0 ? seconds : 60);

    /// <summary>
    /// Runs dispatcher jobs until <paramref name="condition"/> holds. The condition is checked
    /// after every round of jobs, with no fixed sleep in between (the work it waits for runs on
    /// the UI thread or on worker threads); it fails after <see cref="WaitBudget"/>.
    /// </summary>
    public static void PumpUntil(Func<bool> condition, string because = "condition")
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var spin = new SpinWait();
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (clock.Elapsed > WaitBudget)
            {
                Assert.Fail($"Timed out after {clock.Elapsed.TotalSeconds:0.0} s waiting for {because}.");
            }

            // Yields to worker threads, backing off to 1 ms sleeps while nothing changes.
            spin.SpinOnce();
        }
    }

    /// <summary>Runs dispatcher jobs until <paramref name="task"/> has finished, then rethrows its failure.</summary>
    public static void PumpUntilDone(Task task, string because = "task")
    {
        ArgumentNullException.ThrowIfNull(task);
        PumpUntil(() => task.IsCompleted, because);
        task.GetAwaiter().GetResult();
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

    public List<string> OpenedFolders { get; } = [];

    public void Open(string url) => Opened.Add(url);

    public void OpenFolder(string path) => OpenedFolders.Add(path);
}
