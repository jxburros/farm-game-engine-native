using Avalonia;
using Avalonia.Controls;
using FarmEngine.Authoring;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>The F# schema and content checks, refreshed when this tab is opened.</summary>
public sealed class ProblemsView : UserControl
{
    private readonly ProjectWorkspace _workspace;
    private readonly Action<Problem> _navigate;
    private readonly StackPanel _rows = new() { Name = "ProblemRows", Spacing = 8 };
    private readonly TextBlock _summary = Ui.Text("", "h2");
    private bool _dirty = true;

    public IReadOnlyList<Problem> CurrentProblems { get; private set; } = [];

    public ProblemsView(ProjectWorkspace workspace, Action<Problem> navigate)
    {
        _workspace = workspace;
        _navigate = navigate;
        Name = "ProblemsView";
        _summary.Name = "ProblemsSummary";
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        stack.Children.Add(_summary);
        stack.Children.Add(Ui.Wrapped("Errors block export. Warnings and tips help you improve the project.", "muted", "small"));
        stack.Children.Add(_rows);
        Content = new ScrollViewer { Content = stack };
        _workspace.ProjectChanged += (_, _) => _dirty = true;
    }

    public void Refresh()
    {
        if (!_dirty) return;
        _dirty = false;
        _rows.Children.Clear();
        if (_workspace.Current is not { } project)
        {
            _summary.Text = "No project open";
            return;
        }

        var problems = Problems.Collect(project);
        CurrentProblems = problems;
        var errors = problems.Count(problem => problem.IsError);
        var warnings = problems.Count(problem => problem.IsWarning);
        _summary.Text = $"{errors} errors · {warnings} warnings · {problems.Count - errors - warnings} tips";
        if (problems.Count == 0)
        {
            _rows.Children.Add(Ui.Empty("IconStar", "No problems found", "Your project passed the current checks."));
            return;
        }

        for (var index = 0; index < problems.Count; index++)
        {
            var problem = problems[index];
            var title = Ui.Wrapped($"{problem.SeverityName.ToUpperInvariant()} · {problem.Message}", "small");
            if (problem.IsError) title.Classes.Add("error");
            var path = Ui.Text(problem.Path, "muted", "small");
            var text = Ui.VStack(4, title, path);
            if (problem.HasTarget)
            {
                var go = Ui.Button("Go to", () => _navigate(problem), "tool", "small");
                go.Name = $"ProblemGo_{index}";
                _rows.Children.Add(Ui.Row(text, go));
            }
            else
            {
                _rows.Children.Add(Ui.Row(text));
            }
        }
    }
}
