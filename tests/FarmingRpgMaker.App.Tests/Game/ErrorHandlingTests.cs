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
        // Form confirmations don't claim the change is on disk.
        Assert.Equal("Door applied to Barn (2, 3) (not yet saved to disk).", workspace.SavedText("Door", " to Barn (2, 3)"));

        // Still failing: Retry keeps the banner.
        Click(host.Window, FindByName<Button>(host.Window, "RetrySaveButton"));
        Assert.True(banner.IsVisible);

        Directory.Delete(path);
        Click(host.Window, FindByName<Button>(host.Window, "RetrySaveButton"));
        Assert.Null(workspace.SaveError);
        Assert.False(workspace.HasPendingSave);
        Assert.False(banner.IsVisible);
        Assert.Contains("Saving works again", host.ViewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("Settings saved.", workspace.SavedText("Settings"));
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
        Assert.Contains("every change you applied before the error is kept", text, StringComparison.Ordinal);
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

        // The failure persists (the broken view stays subscribed): Try Again reopens the editor,
        // and the next edit that hits it shows the error screen again instead of closing the app.
        Click(host.Window, FindByName<Button>(host.Window, "TryAgainButton"));
        Assert.Null(host.Surface.ErrorView);
        Assert.Equal("Edited Farm", FindByName<TextBlock>(host.Window, "ProjectInfoName").Text);
        Assert.True(host.Workspace.Apply(Edits.SetProjectInfo("Edited Again", host.Workspace.Current!.Version)));
        Pump();
        Assert.NotNull(host.Surface.ErrorView);

        // Undo and try again with the broken view still there: the undo's error goes back on the
        // error screen too.
        Click(host.Window, FindByName<Button>(host.Window, "UndoAndTryAgainButton"));
        Pump();
        Assert.Equal("Edited Farm", host.Workspace.Current!.Name);
        Assert.NotNull(host.Surface.ErrorView);
        Assert.Contains("A view could not refresh.", FindByName<SelectableTextBlock>(host.Window, "EditorErrorDetails").Text, StringComparison.Ordinal);
        host.Workspace.ProjectChanged -= broken;
    }

    [AvaloniaFact]
    public void TryAgain_WhenTheEditorFailsAgain_KeepsTheErrorScreenWithTheNewError()
    {
        using var host = new GameTestHost();
        var create = host.Surface.CreateEditor;
        host.Surface.CreateEditor = _ => throw new InvalidOperationException("This project can't be shown.");
        Assert.True(host.Composition.TryRecover(new InvalidOperationException("The map broke.")));
        Pump();

        Click(host.Window, FindByName<Button>(host.Window, "TryAgainButton"));
        Pump();
        Assert.NotNull(host.Surface.ErrorView);
        Assert.Contains("This project can't be shown.", FindByName<SelectableTextBlock>(host.Window, "EditorErrorDetails").Text, StringComparison.Ordinal);

        // Fixed: Try Again works.
        host.Surface.CreateEditor = create;
        Click(host.Window, FindByName<Button>(host.Window, "TryAgainButton"));
        Assert.Null(host.Surface.ErrorView);
        Assert.Equal("Starter Farm", FindByName<TextBlock>(host.Window, "ProjectInfoName").Text);
    }

    [AvaloniaFact]
    public void TryAgain_RetiresTheWholeOldEditor()
    {
        using var host = new GameTestHost();
        var handlers = host.Workspace.ProjectChangedHandlerCount;

        for (var i = 0; i < 3; i++)
        {
            Assert.True(host.Composition.TryRecover(new InvalidOperationException("The map broke.")));
            Pump();
            Click(host.Window, FindByName<Button>(host.Window, "TryAgainButton"));
            Pump();
        }

        // Every editor view of the replaced editors let go of the workspace.
        Assert.Equal(handlers, host.Workspace.ProjectChangedHandlerCount);
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
    public void ClosingTheWindow_WhileSavesFail_AsksFirst()
    {
        using var host = new GameTestHost();
        var path = BlockProjectFile(host);
        host.Workspace.Apply(Edits.SetProjectInfo("Unsaved Farm", host.Workspace.Current!.Version));
        Pump();

        host.Window.Close();
        var prompt = WaitForDialog(host.Window, "UnsavedChangesWindow");
        Assert.True(host.Window.IsVisible, "the close waits for an answer");
        Assert.Equal("Quit anyway", FindByName<Button>(prompt, "UnsavedDiscardButton").Content);

        // Cancel: the editor stays, edits and all.
        Click(prompt, FindByName<Button>(prompt, "UnsavedStayButton"));
        PumpUntil(() => !prompt.IsVisible, "prompt closed");
        Assert.True(host.Window.IsVisible);
        Assert.Equal("Unsaved Farm", host.Workspace.Current!.Name);

        // Fixed meanwhile: Retry saves, and the window closes.
        host.Window.Close();
        prompt = WaitForDialog(host.Window, "UnsavedChangesWindow");
        Directory.Delete(path);
        Click(prompt, FindByName<Button>(prompt, "UnsavedRetryButton"));
        PumpUntil(() => !host.Window.IsVisible, "window closed");
        Assert.Equal("Unsaved Farm", host.Workspace.Store.Load(host.Workspace.Current!.Id).Project!.Name);
    }

    [AvaloniaFact]
    public void QuitAnyway_ClosesWithoutTheUnsavedEdits()
    {
        using var host = new GameTestHost();
        var path = BlockProjectFile(host);
        host.Workspace.Apply(Edits.SetProjectInfo("Unsaved Farm", host.Workspace.Current!.Version));
        Pump();

        host.Window.Close();
        var prompt = WaitForDialog(host.Window, "UnsavedChangesWindow");
        Click(prompt, FindByName<Button>(prompt, "UnsavedDiscardButton"));
        PumpUntil(() => !host.Window.IsVisible, "window closed");
        Assert.True(host.Window.IsExitConfirmed);
        Directory.Delete(path);
    }

    [AvaloniaFact]
    public void RestartAndInstall_WhileSavesFail_AsksFirst()
    {
        using var host = new GameTestHost();
        var updates = host.ViewModel.Updates;
        host.Updates.NextResult = FarmingRpgMaker.Updates.Testing.FakeUpdateService.SampleUpdate();
        var check = updates.CheckAsync();
        PumpUntil(() => check.IsCompleted, "check");
        var download = updates.DownloadAsync();
        PumpUntil(() => download.IsCompleted && updates.CanApply, "download");
        var path = BlockProjectFile(host);
        host.Workspace.Apply(Edits.SetProjectInfo("Unsaved Farm", host.Workspace.Current!.Version));
        Pump();

        updates.ApplyAndRestart();
        var prompt = WaitForDialog(host.Window, "UnsavedChangesWindow");
        Assert.Equal(0, host.Updates.ApplyAndRestartCount);
        Assert.Equal("Install anyway", FindByName<Button>(prompt, "UnsavedDiscardButton").Content);

        Click(prompt, FindByName<Button>(prompt, "UnsavedDiscardButton"));
        PumpUntil(() => host.Updates.ApplyAndRestartCount == 1, "restarted");
        Directory.Delete(path);
    }

    private static Window WaitForDialog(Window owner, string name)
    {
        Window? found = null;
        PumpUntil(() => (found = owner.OwnedWindows.FirstOrDefault(w => w.Name == name && w.IsVisible)) is not null, name);
        return found!;
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
