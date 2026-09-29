using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmEngine.Authoring;
using FarmEngine.Export;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;
using FarmingRpgMaker.App.ViewModels;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>File → Export Game…: the view model over the F# exporter, and the real dialog.</summary>
public sealed class ExportGameTests
{
    private static readonly string? OutputDir = Environment.GetEnvironmentVariable("FRM_SCREENSHOT_DIR");

    /// <summary>
    /// Player templates for both targets at this editor's version: the PE fixture (real
    /// placeholder resources) for Windows and a shell script for Linux.
    /// </summary>
    private static string FakeTemplates(string root)
    {
        Write(root, "windows-x64", "farm-player.exe", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "player-fixture.exe")));
        Write(root, "linux-x64", "farm-player", "#!/bin/sh\necho fake\n"u8.ToArray());
        return root;

        static void Write(string root, string target, string executable, byte[] bytes)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, target)).FullName;
            File.WriteAllBytes(Path.Combine(folder, executable), bytes);
            File.WriteAllText(Path.Combine(folder, "THIRD-PARTY.txt"), "Third-party software\n");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.WriteAllText(
                Path.Combine(folder, "template.json"),
                $$"""{ "target": "{{target}}", "version": "{{GameExporter.EditorVersion}}", "sha256": "{{sha}}" }""");
        }
    }

    private static ExportGameViewModel Create(GameTestHost host, string templates, Func<string?, Task<string?>>? pick = null) =>
        new(host.Workspace, pick ?? (_ => Task.FromResult<string?>(null)), host.Launcher, templates);

    [AvaloniaFact]
    public void Defaults_ComeFromTheProjectAndAppSettings()
    {
        using var host = new GameTestHost();
        using var dir = new TempDir();
        var viewModel = Create(host, dir.Path);
        Assert.True(viewModel.ExportWindows);
        Assert.True(viewModel.ExportLinux);
        Assert.True(viewModel.CreateArchives);
        Assert.Equal(ExportGameViewModel.DefaultOutputFolder, viewModel.OutputFolder);
        Assert.Equal(host.Workspace.Current!.Name, viewModel.Summary.Title);
        Assert.False(viewModel.IsBlockedByProblems);
        Assert.True(viewModel.CanExport);

        host.Workspace.Settings.Update(s => s with { LastExportFolder = dir.Path, ExportArchives = false });
        var remembered = Create(host, dir.Path);
        Assert.Equal(dir.Path, remembered.OutputFolder);
        Assert.False(remembered.CreateArchives);

        remembered.ExportWindows = false;
        remembered.ExportLinux = false;
        Assert.False(remembered.CanExport);
        Assert.False(remembered.ExportCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Browse_UsesTheFolderPicker()
    {
        using var host = new GameTestHost();
        using var dir = new TempDir();
        string? startedAt = "unset";
        var viewModel = Create(host, dir.Path, start =>
        {
            startedAt = start;
            return Task.FromResult<string?>(Path.Combine(dir.Path, "picked"));
        });
        viewModel.OutputFolder = dir.Path;
        viewModel.BrowseCommand.Execute(null);
        PumpUntil(() => viewModel.OutputFolder.EndsWith("picked", StringComparison.Ordinal), "picked folder");
        Assert.Equal(dir.Path, startedAt);
    }

    [AvaloniaFact]
    public void Export_WritesTheGame_RemembersFolderAndTargets_AndOpensTheFolder()
    {
        using var host = new GameTestHost();
        using var dir = new TempDir();
        var output = Path.Combine(dir.Path, "out");
        var viewModel = Create(host, FakeTemplates(Path.Combine(dir.Path, "templates")));
        viewModel.OutputFolder = output;
        viewModel.ExportLinux = false;

        viewModel.ExportCommand.Execute(null);
        PumpUntil(() => viewModel.HasReport && !viewModel.IsExporting, "export");

        Assert.True(viewModel.Succeeded, viewModel.ReportText);
        Assert.Equal("Exported Windows x64.", viewModel.StatusText);
        var result = Assert.Single(viewModel.TargetResults);
        Assert.Equal("windows-x64", result.Target);
        Assert.True(File.Exists(Path.Combine(result.Folder!, viewModel.Summary.ExecutableName + ".exe")));
        Assert.True(File.Exists(result.Archive));
        Assert.Contains(result.Files, f => f.StartsWith("game.cart (", StringComparison.Ordinal));

        Assert.Equal(output, host.Workspace.Settings.Load().LastExportFolder);
        Assert.Equal(["windows-x64"], host.Workspace.Current!.Export.OrNull()!.Targets);
        Assert.True(host.Workspace.CanUndo);

        viewModel.OpenFolderCommand.Execute(result.Folder);
        Assert.Equal([result.Folder!], host.Launcher.OpenedFolders);
    }

    [AvaloniaFact]
    public void ProblemsErrors_BlockExport()
    {
        using var host = new GameTestHost();
        using var dir = new TempDir();
        host.Workspace.Open(host.Workspace.Current!.WithSelectedTileType("lava"));
        var viewModel = Create(host, dir.Path);
        Assert.True(viewModel.IsBlockedByProblems);
        Assert.Contains("must be fixed before export", viewModel.ProblemsText, StringComparison.Ordinal);
        Assert.False(viewModel.CanExport);
        Assert.False(viewModel.ExportCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void MissingTemplate_IsReportedForThatTargetOnly()
    {
        using var host = new GameTestHost();
        using var dir = new TempDir();
        var templates = FakeTemplates(Path.Combine(dir.Path, "templates"));
        Directory.Delete(Path.Combine(templates, "windows-x64"), recursive: true);
        var viewModel = Create(host, templates);
        viewModel.OutputFolder = Path.Combine(dir.Path, "out");
        viewModel.ExportCommand.Execute(null);
        PumpUntil(() => viewModel.HasReport && !viewModel.IsExporting, "export");

        Assert.False(viewModel.Succeeded);
        Assert.Equal("Exported Linux x64; Windows x64 failed.", viewModel.StatusText);
        var windows = viewModel.TargetResults.Single(t => t.Target == "windows-x64");
        Assert.Contains("No Windows x64 player template", windows.Errors[0], StringComparison.Ordinal);
        Assert.True(viewModel.TargetResults.Single(t => t.Target == "linux-x64").Ok);
    }

    [AvaloniaFact]
    public void Dialog_FromTheFileMenu_ExportsAndLinksTheFolder()
    {
        using var host = new GameTestHost();
        using var dir = new TempDir();
        var output = Path.Combine(dir.Path, "exports");
        ((ProjectCommandHandler)host.Composition.ProjectCommands).PlayerTemplatesFolder = FakeTemplates(Path.Combine(dir.Path, "templates"));
        host.Workspace.Settings.Update(s => s with { LastExportFolder = output });

        host.ViewModel.ExportGameCommand.Execute(null);
        Window? dialog = null;
        PumpUntil(() => (dialog = host.Window.OwnedWindows.FirstOrDefault(w => w.Name == "ExportGameWindow")) is not null, "export dialog");
        Assert.Equal(output, FindByName<TextBox>(dialog!, "ExportFolderBox").Text);
        Assert.True(FindByName<CheckBox>(dialog!, "ExportWindowsCheck").IsChecked);
        var viewModel = Assert.IsType<ExportGameViewModel>(dialog!.DataContext);
        Assert.Contains(viewModel.ProblemsText, AllVisibleText(dialog!), StringComparison.Ordinal);
        Assert.Contains(viewModel.GameText, AllVisibleText(dialog!), StringComparison.Ordinal);

        Click(dialog!, FindByName<Button>(dialog!, "ExportButton"));
        PumpUntil(() => TryFindByName<TextBlock>(dialog!, "ExportStatusText")?.Text?.StartsWith("Exported", StringComparison.Ordinal) == true, "report");
        Assert.Equal("Exported Windows x64 and Linux x64.", FindByName<TextBlock>(dialog!, "ExportStatusText").Text);
        if (!string.IsNullOrEmpty(OutputDir))
        {
            GameTestHost.Capture(dialog!).Save(Path.Combine(OutputDir, "export-game.png"));
        }

        var game = host.Workspace.Current!;
        var folder = Path.Combine(output, "linux-x64", GameExporter.Summarize(game).ExecutableName);
        var openLinux = FindByName<Button>(dialog!, "OpenFolder_linux-x64");
        openLinux.BringIntoView();
        Pump();
        Click(dialog!, openLinux);
        Assert.Equal([folder], host.Launcher.OpenedFolders);
        Assert.True(File.Exists(Path.Combine(folder, "game.cart")));

        Click(dialog!, FindByName<Button>(dialog!, "ExportCloseButton"));
        PumpUntil(() => host.ViewModel.StatusMessage.StartsWith("Exported Windows x64 and Linux x64.", StringComparison.Ordinal), "status bar");
    }
}
