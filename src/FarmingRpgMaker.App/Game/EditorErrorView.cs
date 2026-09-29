using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// What Edit Mode shows after an unexpected error (web <c>ErrorFallback</c>): what went wrong,
/// whether the project is saved, the error details, and Try Again (a fresh editor over the same
/// project) or Undo last change and try again (when the last edit left the editor unable to show
/// the project).
/// </summary>
public sealed class EditorErrorView : UserControl
{
    public EditorErrorView(Exception error, string? saveError, bool canUndo, Action tryAgain, Action undoAndTryAgain)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(tryAgain);
        ArgumentNullException.ThrowIfNull(undoAndTryAgain);
        Name = "EditorErrorView";
        Error = error;

        var title = Ui.Wrapped("The editor ran into a problem", "h2");
        title.Name = "EditorErrorTitle";
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Assertive);
        var saved = saveError is null
            ? "Something unexpected happened while editing. Your project is safe: every change before the error has been saved."
            : $"Something unexpected happened while editing, and your project could not be saved ({saveError}). Your changes are still open: fix the problem and choose Try Again, then check that saving works.";
        var summary = Ui.Wrapped(saved, "muted");
        summary.Name = "EditorErrorSummary";

        var details = new SelectableTextBlock
        {
            Name = "EditorErrorDetails",
            Text = $"{error.GetType().Name}: {error.Message}",
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 12,
        };
        AutomationProperties.SetName(details, "Error details");
        details.Classes.Add("error");
        var detailsBox = new Border
        {
            Child = new ScrollViewer { Content = details, MaxHeight = 140 },
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6),
            Background = (IBrush?)Application.Current?.FindResource("FarmCodeBackgroundBrush") ?? Brushes.WhiteSmoke,
        };

        var retry = Ui.Button(Ui.IconLabel("IconRefresh", "Try Again"), tryAgain, "accent");
        retry.Name = "TryAgainButton";
        AutomationProperties.SetName(retry, "Try Again");
        ToolTip.SetTip(retry, "Open the editor again on the same project");
        var undo = Ui.Button("Undo last change and try again", undoAndTryAgain, "tool");
        undo.Name = "UndoAndTryAgainButton";
        undo.IsEnabled = canUndo;
        ToolTip.SetTip(undo, "Use this when the error came right after an edit");

        var card = new Border
        {
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            Child = Ui.VStack(14,
                Ui.HStack(10, Ui.Icon("IconAlertCircle", 24), title),
                summary,
                Ui.Text("ERROR DETAILS", "section"),
                detailsBox,
                Ui.HStack(8, retry, undo)),
        }.WithClasses("side-panel");
        Content = card;
    }

    /// <summary>The error shown.</summary>
    public Exception Error { get; }
}
