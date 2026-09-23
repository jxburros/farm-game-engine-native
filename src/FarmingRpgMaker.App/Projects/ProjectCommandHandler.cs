using FarmEngine.Content;
using FarmingRpgMaker.App.Hosting;

namespace FarmingRpgMaker.App.Projects;

/// <summary>
/// File-menu project commands (web ProjectManager): New (templates/samples), Open (project
/// list with delete), Import/Export Project JSON through the platform file pickers.
/// </summary>
public sealed class ProjectCommandHandler : IProjectCommandHandler
{
    private readonly ProjectWorkspace _workspace;
    private readonly IProjectDialogs _dialogs;

    public ProjectCommandHandler(ProjectWorkspace workspace, IProjectDialogs? dialogs = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _dialogs = dialogs ?? new AvaloniaProjectDialogs();
    }

    public async Task NewProjectAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var choice = await _dialogs.ChooseNewProjectAsync(shell, Templates.TemplateInfo).ConfigureAwait(true);
        if (choice is null)
        {
            return;
        }

        LeavePlayMode(shell);
        var project = _workspace.CreateProject(choice.TemplateId, choice.Name);
        _workspace.Open(project);
        var template = Templates.TemplateInfo.FirstOrDefault(t => t.Id == choice.TemplateId)?.Name ?? choice.TemplateId;
        shell.ShowStatus($"Created \"{project.Name}\" from the {template} template.");
    }

    public async Task OpenProjectAsync(IShellHost shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _workspace.FlushPendingSave();
        var id = await _dialogs.ChooseProjectAsync(shell, _workspace.Store, _workspace.Current?.Id).ConfigureAwait(true);
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
