using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FarmEngine.Authoring;
using FarmingRpgMaker.App.Game;
using FarmingRpgMaker.App.Hosting;
using static FarmingRpgMaker.App.Tests.Ui.UiTestHelpers;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>Save failures are announced and retried; editor errors show a screen with Try Again.</summary>
public sealed class ErrorHandlingTests
{
    /// <summary>Makes the project file unwritable: a folder takes its place.</summary>
    private static string BlockProjectFile(GameTestHost host)
    {
        var path = host.Workspace.Store.PathFor(host.Workspace.Current!.Id);
        File.Delete(path);
        Directory.CreateDirectory(path);
        return path;
    }

    [AvaloniaFact]
    public void AFailedSave_ShowsTheBanner_KeepsTheEdits_AndRetrySaves()
    {
        using var host = new GameTestHost();
        var workspace = host.Workspace;
        var path = BlockProjectFile(host);
        var banner = FindByName<Border>(host.Window, "SaveErrorBanner");
        Assert.False(banner.IsVisible);

        Assert.True(workspace.Apply(Edits.SetProjectInfo("Unsaved Farm", workspace.Current!.Version)));
        Pump();

        Assert.NotNull(workspace.SaveError);
        Assert.True(workspace.HasPendingSave);
        Assert.True(banner.IsVisible);
        Assert.Contains("could not be saved", FindByName<TextBlock>(host.Window, "SaveErrorText").Text, StringComparison.Ordinal);
        Assert.Contains("could not be saved", host.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("Unsaved Farm", workspace.Current!.Name);

        // Still failing: Retry keeps the banner.
        Click(host.Window, FindByName<Button>(host.Window, "RetrySaveButton"));
        Assert.True(banner.IsVisible);

        Directory.Delete(path);
        Click(host.Window, FindByName<Button>(host.Window, "RetrySaveButton"));
        Assert.Null(workspace.SaveError);
        Assert.False(workspace.HasPendingSave);
        Assert.False(banner.IsVisible);
        Assert.Contains("Saving works again", host.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("Unsaved Farm", workspace.Store.Load(workspace.Current!.Id).Project!.Name);
    }

    [AvaloniaFact]
    public void AFailedSave_OnAProjectSwitch_DoesNotThrow()
    {
        using var host = new GameTestHost();
        var path = BlockProjectFile(host);
        host.Workspace.Apply(Edits.SetProjectInfo("Pending", host.Workspace.Current!.Version));
        host.Workspace.FlushPendingSave();
        host.Composition.PrepareForShutdown();
        Assert.NotNull(host.Workspace.SaveError);
        Directory.Delete(path);
    }

    [AvaloniaFact]
    public void AnEditorError_ShowsTheErrorScreen_AndTryAgainReopensTheEditor()
    {
        using var host = new GameTestHost();
        var before = host.Surface.EditView;

        Assert.True(host.Composition.TryRecover(new InvalidOperationException("The map broke.")));
        Pump();

        var screen = host.Surface.ErrorView;
        Assert.NotNull(screen);
        var text = AllVisibleText(host.Window);
        Assert.Contains("The editor ran into a problem", text, StringComparison.Ordinal);
        Assert.Contains("every change before the error has been saved", text, StringComparison.Ordinal);
        Assert.Contains("The map broke.", FindByName<SelectableTextBlock>(host.Window, "EditorErrorDetails").Text, StringComparison.Ordinal);
        Assert.Contains("ran into a problem", host.ViewModel.StatusMessage, StringComparison.Ordinal);
        // An error raised while the screen is up is not caught again.
        Assert.False(host.Composition.TryRecover(new InvalidOperationException("again")));

        Click(host.Window, FindByName<Button>(host.Window, "TryAgainButton"));

        Assert.Null(host.Surface.ErrorView);
        Assert.NotSame(before, host.Surface.EditView);
        Assert.NotNull(host.Surface.EditView.Canvas.Geometry);
        Assert.Equal("Starter Farm", FindByName<TextBlock>(host.Window, "ProjectInfoName").Text);
    }

    [AvaloniaFact]
    public void AViewThatFailsToRefresh_ShowsTheErrorScreen_InsteadOfFailingTheEdit()
    {
        using var host = new GameTestHost();
        EventHandler<FarmingRpgMaker.App.Projects.ProjectChangedEventArgs> broken = (_, _) => throw new InvalidOperationException("A view could not refresh.");
        host.Workspace.ProjectChanged += broken;

        Assert.True(host.Workspace.Apply(Edits.SetProjectInfo("Edited Farm", host.Workspace.Current!.Version)));
        Pump();

        Assert.Equal("Edited Farm", host.Workspace.Current!.Name);
        Assert.Contains("A view could not refresh.", FindByName<SelectableTextBlock>(host.Window, "EditorErrorDetails").Text, StringComparison.Ordinal);
        host.Workspace.ProjectChanged -= broken;
        Click(host.Window, FindByName<Button>(host.Window, "TryAgainButton"));
        Assert.Equal("Edited Farm", FindByName<TextBlock>(host.Window, "ProjectInfoName").Text);
    }

    [AvaloniaFact]
    public void UndoAndTryAgain_UndoesTheLastEdit()
    {
        using var host = new GameTestHost();
        host.Workspace.Apply(Edits.SetProjectInfo("Broken Farm", host.Workspace.Current!.Version));
        host.Composition.TryRecover(new InvalidOperationException("The last edit broke the editor."));
        Pump();

        var undo = FindByName<Button>(host.Window, "UndoAndTryAgainButton");
        Assert.True(undo.IsEnabled);
        Click(host.Window, undo);

        Assert.Null(host.Surface.ErrorView);
        Assert.Equal("Starter Farm", host.Workspace.Current!.Name);
    }

    [AvaloniaFact]
    public void AnErrorDuringAPlaytest_EndsItWithoutKeepingChanges()
    {
        using var host = new GameTestHost();
        var before = host.Workspace.Current;
        host.EnterPlay();
        Assert.NotNull(host.Surface.PlayView);

        Assert.True(host.Composition.TryRecover(new InvalidOperationException("The debug drawer broke.")));
        Pump();

        Assert.Null(host.Surface.PlayView);
        Assert.Equal(EditorMode.Edit, host.ViewModel.Mode);
        Assert.NotNull(host.Surface.ErrorView);
        Assert.Same(before, host.Workspace.Current);
    }
}
