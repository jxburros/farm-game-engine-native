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

    /// <summary>The way out of the unsaved-changes prompt when opening another project.</summary>
    private const string DiscardAndContinue = "Continue without them";

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
        if (!await ResolveUnsavedChangesAsync(shell, DiscardAndContinue).ConfigureAwait(true))
        {
            return;
        }

        var project = _workspace.CreateProject(choice.TemplateId, choice.Name);
        _workspace.Open(project);
        var template = ProjectCatalog.TemplateInfo.FirstOrDefault(t => t.Id == choice.TemplateId)?.Name ?? choice.TemplateId;
        ShowOpened(shell, project, $"Created \"{project.Name}\" from the {template} template.");
    }

    public async Task OpenProjectAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _workspace.FlushPendingSave();
        var id = await _dialogs.ChooseProjectAsync(shell, _workspace.Store, _workspace.Current?.Id).ConfigureAwait(true);
        // The list may have renamed the open project on disk; the open document takes that name.
        if (_workspace.Current is { } open && !string.IsNullOrWhiteSpace(open.Name) && _workspace.StoredName(open.Id) is { } stored && stored != open.Name)
        {
            // The name is read before leaving Play Mode: keeping the playtest's changes saves the
            // pre-play project, old name included, so the new name is applied after it.
            _workspace.AcceptStoreChange();
            LeavePlayMode(shell);
            if (_workspace.AdoptStoredName(stored))
            {
                shell.ShowStatus($"Renamed the project to \"{_workspace.Current!.Name}\".");
            }
        }

        if (id is null)
        {
            return;
        }

        LeavePlayMode(shell);
        if (!await ResolveUnsavedChangesAsync(shell, DiscardAndContinue).ConfigureAwait(true))
        {
            return;
        }

        var loaded = _workspace.OpenById(id);
        if (!loaded.Ok)
        {
            await _dialogs.ShowErrorsAsync(shell, "This project could not be opened", loaded.Errors).ConfigureAwait(true);
            return;
        }

        ShowOpened(shell, loaded.Project!, loaded.MigratedFrom is { } from
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
        if (!await ResolveUnsavedChangesAsync(shell, DiscardAndContinue).ConfigureAwait(true))
        {
            return;
        }

        _workspace.Open(imported.Project!);
        ShowOpened(shell, imported.Project!, imported.MigratedFrom is { } from
            ? $"Imported {file.FileName} as \"{imported.Project!.Name}\" (upgraded from schema v{from})."
            : $"Imported {file.FileName} as \"{imported.Project!.Name}\".");
    }

    public async Task ExportProjectJsonAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (_workspace.Current is null)
        {
            shell.ShowStatus("There is no project to export.");
            return;
        }

        await ExportProjectJsonCoreAsync(shell).ConfigureAwait(true);
    }

    private async Task<string?> ExportProjectJsonCoreAsync(IShellHost shell)
    {
        if (_workspace.Current is not { } project)
        {
            return null;
        }

        var json = ProjectStore.ToJson(project);
        var saved = await _dialogs.SaveExportAsync(shell, SuggestedFileName(project.Name), json).ConfigureAwait(true);
        if (saved is not null)
        {
            shell.ShowStatus($"Exported \"{project.Name}\" to {saved}.");
        }

        return saved;
    }

    /// <summary>After switching projects: the success message, unless the project could not be saved.</summary>
    private void ShowOpened(IShellHost shell, FarmEngine.Schemas.GameProject project, string success) =>
        shell.ShowStatus(_workspace.SaveError is { } error
            ? $"\"{project.Name}\" is open, but it could not be saved: {error}"
            : success);

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

    /// <summary>
    /// Makes sure nothing is lost by leaving the open project: writes pending edits, and while
    /// saving still fails asks what to do (Retry, Export Project JSON…, go on without them, or
    /// stay). True when it is fine to go on. Used before closing the window, installing an
    /// update and opening another project.
    /// </summary>
    public async Task<bool> ResolveUnsavedChangesAsync(IShellHost shell, string discardText)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _workspace.FlushPendingSave();
        string? note = null;
        while (_workspace.HasUnsavedChanges)
        {
            var choice = await _dialogs.AskUnsavedChangesAsync(shell, _workspace.SaveError!, discardText, note).ConfigureAwait(true);
            switch (choice)
            {
                case UnsavedChangesChoice.Retry:
                    note = _workspace.RetrySave() ? null : $"Still can't save: {_workspace.SaveError}";
                    break;
                case UnsavedChangesChoice.Export:
                    var exported = await ExportProjectJsonCoreAsync(shell).ConfigureAwait(true);
                    note = exported is null ? note : $"A copy was exported to {exported}.";
                    break;
                case UnsavedChangesChoice.Discard:
                    System.Diagnostics.Trace.TraceWarning($"Unsaved changes to project {_workspace.Current?.Id} were left behind ({discardText}).");
                    return true;
                default:
                    shell.ShowStatus($"Your project could not be saved: {_workspace.SaveError}");
                    return false;
            }
        }

        return true;
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
