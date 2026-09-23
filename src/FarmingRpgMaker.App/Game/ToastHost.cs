using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace FarmingRpgMaker.App.Game;

/// <summary>Stacked toasts (web sonner <c>&lt;Toaster /&gt;</c>): success/error/info, auto-dismissed.</summary>
public sealed class ToastHost : StackPanel
{
    /// <summary>Most toasts shown at once; older ones drop off.</summary>
    public const int MaxVisible = 4;

    public ToastHost()
    {
        Name = "ToastHost";
        Spacing = 8;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        IsHitTestVisible = false;
    }

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromSeconds(3.5);

    /// <summary>Every toast shown since creation (tests, diagnostics).</summary>
    public List<ToastMessage> History { get; } = [];

    public IEnumerable<string> VisibleTexts =>
        Children.OfType<Border>().Select(b => (b.Tag as ToastMessage)?.Text ?? "");

    public void Show(ToastMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        History.Add(message);
        var (iconKey, kindClass) = message.Kind switch
        {
            ToastKind.Success => ("IconCheckCircle", "success"),
            ToastKind.Error => ("IconAlertCircle", "error"),
            _ => ("IconInfo", "info"),
        };
        // Repeats of the newest toast collapse into one "×N" toast (tool spam, sleep chains).
        if (Children.Count > 0 && Children[0] is Border { Tag: ToastMessage newest } existing && newest.Text == message.Text && newest.Kind == message.Kind)
        {
            var count = (existing.Child is StackPanel { Children.Count: 3 } row && row.Children[2] is TextBlock counter && int.TryParse(counter.Text?.TrimStart('×'), out var n) ? n : 1) + 1;
            if (existing.Child is StackPanel panel)
            {
                if (panel.Children.Count == 3)
                {
                    ((TextBlock)panel.Children[2]).Text = $"×{count}";
                }
                else
                {
                    panel.Children.Add(Ui.Text($"×{count}", "muted", "small"));
                }
            }

            var generation = (existing.DataContext as int? ?? 0) + 1;
            existing.DataContext = generation;
            DispatcherTimer.RunOnce(() =>
            {
                if (existing.DataContext as int? == generation)
                {
                    Children.Remove(existing);
                }
            }, Lifetime);
            return;
        }

        var text = Ui.Wrapped(message.Text);
        text.FontSize = 13.5;
        var toast = new Border { Tag = message, Child = Ui.HStack(10, Ui.Icon(iconKey, 16), text) }.WithClasses("toast", kindClass);
        // Newest on top (the stack grows downward from the top-right corner).
        Children.Insert(0, toast);
        while (Children.Count > MaxVisible)
        {
            Children.RemoveAt(Children.Count - 1);
        }

        toast.DataContext = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (toast.DataContext as int? == 0)
            {
                Children.Remove(toast);
            }
        }, Lifetime);
    }
}
