using FarmEngine.Authoring;
using FarmingRpgMaker.App.Hosting;
using FarmingRpgMaker.App.Services;
using FarmingRpgMaker.App.ViewModels;

namespace FarmingRpgMaker.App.Projects;

/// <summary>
/// File-menu project commands (web ProjectManager): New (templates/samples), Open (project
/// list with delete), Import/Export Project JSON through the platform file pickers, and
/// Export Game.
/// </summary>
public sealed class ProjectCommandHandler : IProjectCommandHandler
{
    private readonly ProjectWorkspace _workspace;
    private readonly IProjectDialogs _dialogs;
    private readonly IUrlLauncher _launcher;

    public ProjectCommandHandler(ProjectWorkspace workspace, IProjectDialogs? dialogs = null, IUrlLauncher? launcher = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _dialogs = dialogs ?? new AvaloniaProjectDialogs();
        _launcher = launcher ?? new ShellUrlLauncher();
    }

    /// <summary>
    /// Where Export Game finds player templates; null for
    /// <see cref="FarmEngine.Export.GameExporter.DefaultTemplatesFolder"/> (<c>players/</c> next to the app).
    /// </summary>
    public string? PlayerTemplatesFolder { get; set; }

    public async Task NewProjectAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var choice = await _dialogs.ChooseNewProjectAsync(shell, ProjectCatalog.TemplateInfo).ConfigureAwait(true);
        if (choice is null)
        {
            return;
        }

        LeavePlayMode(shell);
        var project = _workspace.CreateProject(choice.TemplateId, choice.Name);
        _workspace.Open(project);
        var template = ProjectCatalog.TemplateInfo.FirstOrDefault(t => t.Id == choice.TemplateId)?.Name ?? choice.TemplateId;
        shell.ShowStatus($"Created \"{project.Name}\" from the {template} template.");
    }

    public async Task OpenProjectAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _workspace.FlushPendingSave();
        var id = await _dialogs.ChooseProjectAsync(shell, _workspace.Store, _workspace.Current?.Id).ConfigureAwait(true);
        // The list may have renamed the open project on disk; the open document takes that name.
        if (_workspace.Current is { } open && !string.IsNullOrWhiteSpace(open.Name) && _workspace.StoredName(open.Id) is { } stored && stored != open.Name)
        {
            LeavePlayMode(shell);
            if (_workspace.AdoptStoredName())
            {
                shell.ShowStatus($"Renamed the project to \"{_workspace.Current!.Name}\".");
            }
        }

        if (id is null)
        {
            return;
        }

        LeavePlayMode(shell);
        var loaded = _workspace.OpenById(id);
        if (!loaded.Ok)
        {
            await _dialogs.ShowErrorsAsync(shell, "This project could not be opened", loaded.Errors).ConfigureAwait(true);
            return;
        }

        shell.ShowStatus(loaded.MigratedFrom is { } from
            ? $"Opened \"{loaded.Project!.Name}\" (upgraded from schema v{from})."
            : $"Opened \"{loaded.Project!.Name}\".");
    }

    public async Task ImportProjectJsonAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var picked = await _dialogs.PickImportFileAsync(shell).ConfigureAwait(true);
        if (picked is not { } file)
        {
            return;
        }

        var imported = _workspace.Store.Import(file.Json);
        if (!imported.Ok)
        {
            await _dialogs.ShowErrorsAsync(shell, $"{file.FileName} could not be imported", imported.Errors).ConfigureAwait(true);
            return;
        }

        LeavePlayMode(shell);
        _workspace.Open(imported.Project!);
        shell.ShowStatus(imported.MigratedFrom is { } from
            ? $"Imported {file.FileName} as \"{imported.Project!.Name}\" (upgraded from schema v{from})."
            : $"Imported {file.FileName} as \"{imported.Project!.Name}\".");
    }

    public async Task ExportProjectJsonAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (_workspace.Current is not { } project)
        {
            shell.ShowStatus("There is no project to export.");
            return;
        }

        var json = ProjectStore.ToJson(project);
        var saved = await _dialogs.SaveExportAsync(shell, SuggestedFileName(project.Name), json).ConfigureAwait(true);
        if (saved is not null)
        {
            shell.ShowStatus($"Exported \"{project.Name}\" to {saved}.");
        }
    }

    public async Task ExportGameAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (_workspace.Current is null)
        {
            shell.ShowStatus("There is no project to export.");
            return;
        }

        // Export the edited project, not a running playtest (Keep changes is honoured first).
        LeavePlayMode(shell);
        var viewModel = new ExportGameViewModel(
            _workspace,
            start => _dialogs.PickFolderAsync(shell, "Export the game to", start),
            _launcher,
            PlayerTemplatesFolder);
        await _dialogs.ShowExportGameAsync(shell, viewModel).ConfigureAwait(true);
        if (viewModel.Report is { } report)
        {
            shell.ShowStatus(report.Ok ? $"{viewModel.StatusText} Files are in {report.OutputFolder}." : viewModel.StatusText);
        }
    }

    /// <summary>"Sunny Acres" → "sunny-acres.json".</summary>
    public static string SuggestedFileName(string name)
    {
        var slug = new string((name ?? "").Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return (slug.Length == 0 ? "farming-game" : slug) + ".json";
    }

    /// <summary>Project switches happen in Edit Mode (the playtest ends first, honouring Keep changes).</summary>
    private static void LeavePlayMode(IShellHost shell)
    {
        if (shell.Mode == EditorMode.Play)
        {
            shell.Mode = EditorMode.Edit;
        }
    }
}
