using FarmEngine.Export;
using FarmingRpgMaker.App.Mvvm;
using FarmingRpgMaker.App.Projects;
using FarmingRpgMaker.App.Services;

namespace FarmingRpgMaker.App.ViewModels;

/// <summary>One exported target as the Export Game dialog lists it.</summary>
public sealed record ExportTargetResult(
    string Target,
    string DisplayName,
    bool Ok,
    string? Folder,
    string? Archive,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// File → Export Game… (docs/EXPORT.md): targets, output folder and archives in, the export
/// report out. F# <see cref="GameExporter"/> makes every decision (identity, Problems, templates,
/// files); this only holds the choices, runs the export off the UI thread and remembers the
/// folder and targets.
/// </summary>
public sealed class ExportGameViewModel : ObservableObject
{
    public const string WindowsTarget = "windows-x64";
    public const string LinuxTarget = "linux-x64";
    public const string WebTarget = "web";

    private readonly ProjectWorkspace _workspace;
    private readonly Func<string?, Task<string?>> _pickFolder;
    private readonly IUrlLauncher _launcher;
    private readonly string? _templatesFolder;
    private bool _exportWindows;
    private bool _exportLinux;
    private bool _exportWeb;
    private bool _createArchives;
    private bool _isExporting;
    private string _outputFolder;
    private ExportReport? _report;
    private string? _failure;
    private CancellationTokenSource? _cancel;

    /// <param name="workspace">The open project and app settings.</param>
    /// <param name="pickFolder">Shows a folder picker starting at the given folder; null when cancelled.</param>
    /// <param name="launcher">Opens exported folders.</param>
    /// <param name="templatesFolder">Player templates; null for <see cref="GameExporter.DefaultTemplatesFolder"/>.</param>
    public ExportGameViewModel(ProjectWorkspace workspace, Func<string?, Task<string?>> pickFolder, IUrlLauncher launcher, string? templatesFolder = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _pickFolder = pickFolder ?? throw new ArgumentNullException(nameof(pickFolder));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _templatesFolder = templatesFolder;
        var project = workspace.Current ?? throw new InvalidOperationException("There is no project to export.");
        Summary = GameExporter.Summarize(project);
        _exportWindows = Summary.Targets.Contains(WindowsTarget);
        _exportLinux = Summary.Targets.Contains(LinuxTarget);
        _exportWeb = Summary.Targets.Contains(WebTarget);
        var settings = workspace.Settings.Load();
        _outputFolder = settings.LastExportFolder ?? DefaultOutputFolder;
        _createArchives = settings.ExportArchives ?? true;
        BrowseCommand = new AsyncRelayCommand(BrowseAsync, () => !IsExporting, ex => Fail("The folder could not be chosen", ex));
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => CanExport, ex => Fail("The export failed unexpectedly", ex));
        OpenFolderCommand = new RelayCommand<string>(path => _launcher.OpenFolder(path));
    }

    /// <summary><c>Documents/Farming RPG Maker/Exports</c> until the creator picks a folder.</summary>
    public static string DefaultOutputFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify),
        "Farming RPG Maker",
        "Exports");

    public ExportSummary Summary { get; }

    /// <summary>"Willow Creek Farm 1.2.0".</summary>
    public string GameText => $"{Summary.Title} {Summary.Version}";

    /// <summary>"WillowCreek · game id com.example.willow".</summary>
    public string GameDetail => $"{Summary.ExecutableName} · game id {Summary.GameId}";

    /// <summary>Problems errors stop the export before it starts.</summary>
    public bool IsBlockedByProblems => Summary.ErrorCount > 0;

    public string ProblemsText => Summary.ErrorCount switch
    {
        > 0 => $"{Plural(Summary.ErrorCount, "problem")} must be fixed before export. The Problems tab lists them.",
        _ when Summary.WarningCount > 0 => $"{Plural(Summary.WarningCount, "warning")} will be listed in the export report.",
        _ => "No problems found.",
    };

    public bool ExportWindows
    {
        get => _exportWindows;
        set => SetChoice(ref _exportWindows, value);
    }

    public bool ExportLinux
    {
        get => _exportLinux;
        set => SetChoice(ref _exportLinux, value);
    }

    /// <summary>The web demo: the game in a page, for itch.io and the like.</summary>
    public bool ExportWeb
    {
        get => _exportWeb;
        set => SetChoice(ref _exportWeb, value);
    }

    public bool CreateArchives
    {
        get => _createArchives;
        set => SetChoice(ref _createArchives, value);
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set => SetChoice(ref _outputFolder, value ?? "");
    }

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetProperty(ref _isExporting, value))
            {
                RefreshCommands();
            }
        }
    }

    /// <summary>The chosen target ids, in menu order.</summary>
    public IReadOnlyList<string> SelectedTargets =>
        [.. new[] { (WindowsTarget, ExportWindows), (LinuxTarget, ExportLinux), (WebTarget, ExportWeb) }.Where(t => t.Item2).Select(t => t.Item1)];

    public bool CanExport => !IsExporting && !IsBlockedByProblems && SelectedTargets.Count > 0 && !string.IsNullOrWhiteSpace(OutputFolder);

    /// <summary>The last export's report, or null before the first export.</summary>
    public ExportReport? Report
    {
        get => _report;
        private set
        {
            if (SetProperty(ref _report, value))
            {
                OnPropertiesChanged(nameof(HasReport), nameof(StatusText), nameof(ReportErrors), nameof(ReportWarnings), nameof(TargetResults), nameof(Succeeded));
            }
        }
    }

    public bool HasReport => _report is not null;

    /// <summary>True when every chosen target was exported.</summary>
    public bool Succeeded => _report?.Ok == true;

    /// <summary>One sentence on how the export went.</summary>
    public string StatusText
    {
        get
        {
            if (_failure is not null)
            {
                return "The export failed.";
            }

            if (_report is not { } report)
            {
                return IsExporting ? (IsCancelling ? "Cancelling…" : "Exporting…") : "";
            }

            if (report.Cancelled)
            {
                return "Export cancelled. Targets it had not finished were left as they were.";
            }

            if (report.Blocked)
            {
                return "Export stopped. Fix the errors below and try again.";
            }

            var done = report.Targets.Where(t => t.Ok).Select(t => t.DisplayName).ToList();
            var failed = report.Targets.Where(t => !t.Ok).Select(t => t.DisplayName).ToList();
            return failed.Count == 0
                ? $"Exported {string.Join(" and ", done)}."
                : done.Count == 0 ? $"{string.Join(" and ", failed)} could not be exported." : $"Exported {string.Join(" and ", done)}; {string.Join(" and ", failed)} failed.";
        }
    }

    /// <summary>Errors that stopped the whole export (or the exception that broke it).</summary>
    public IReadOnlyList<string> ReportErrors => _failure is { } failure ? [failure] : _report is { } report ? [.. report.Errors] : [];

    /// <summary>True after <see cref="CancelExport"/> until the running export stops.</summary>
    public bool IsCancelling => _cancel?.IsCancellationRequested == true;

    /// <summary>
    /// Asks the running export to stop (between files). Targets it has not finished are left as
    /// they were; the report says it was cancelled.
    /// </summary>
    public void CancelExport()
    {
        if (_cancel is { IsCancellationRequested: false } cancel)
        {
            cancel.Cancel();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Warnings about the project (Problems and unused assets).</summary>
    public IReadOnlyList<string> ReportWarnings => _report is { } report ? [.. report.Warnings] : [];

    public IReadOnlyList<ExportTargetResult> TargetResults => _report is { } report
        ? [.. report.Targets.Select(t => new ExportTargetResult(
            t.Target,
            t.DisplayName,
            t.Ok,
            t.Folder,
            t.Archive,
            [.. t.Files.Select(f => $"{f.Path} ({GameExporter.FormatSize(f.Size)})")],
            [.. t.Errors],
            [.. t.Warnings]))]
        : [];

    /// <summary>The whole report as text (the same as <c>farmc export</c> prints).</summary>
    public string ReportText => _report is { } report ? GameExporter.Format(report) : "";

    public AsyncRelayCommand BrowseCommand { get; }

    public AsyncRelayCommand ExportCommand { get; }

    /// <summary>Opens a folder (a <see cref="ExportTargetResult.Folder"/>) in the file manager.</summary>
    public RelayCommand<string> OpenFolderCommand { get; }

    private async Task BrowseAsync()
    {
        var picked = await _pickFolder(Directory.Exists(OutputFolder) ? OutputFolder : null).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            OutputFolder = picked;
        }
    }

    private async Task ExportAsync()
    {
        if (_workspace.Current is not { } project)
        {
            return;
        }

        var targets = SelectedTargets;
        var folder = OutputFolder.Trim();
        var archives = CreateArchives;
        var templates = _templatesFolder;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        _failure = null;
        IsExporting = true;
        Report = null;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            var token = cancel.Token;
            var report = await Task.Run(() => GameExporter.Export(project, targets, folder, archives, templates, token)).ConfigureAwait(true);
            // The report first: nothing after a finished export may hide how it went.
            Report = report;
            _workspace.Settings.TryUpdate(s => s with { LastExportFolder = folder, ExportArchives = archives });
            if (!report.Blocked && GameExporter.TryRememberTargets(project, targets, out var edit))
            {
                _workspace.Apply(edit);
            }
        }
        finally
        {
            _cancel = null;
            IsExporting = false;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>
    /// The exporter reports its own failures; anything else (picking the folder, the project
    /// edit) still ends in a message, and the log, rather than a blank dialog.
    /// </summary>
    private void Fail(string what, Exception error)
    {
        System.Diagnostics.Trace.TraceError($"Export Game: {what}: {error}");
        _failure = $"{what}: {error.Message}";
        OnPropertiesChanged(nameof(StatusText), nameof(ReportErrors), nameof(Succeeded));
    }

    private void SetChoice<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertiesChanged(nameof(ExportWindows), nameof(ExportLinux), nameof(ExportWeb), nameof(CreateArchives), nameof(OutputFolder), nameof(SelectedTargets), nameof(CanExport));
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanExport));
        ExportCommand.NotifyCanExecuteChanged();
        BrowseCommand.NotifyCanExecuteChanged();
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
