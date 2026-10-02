using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// The question a form asks before something would replace fields the creator has not saved
/// (#87): another entry or panel chosen, or a jump from Problems or search. "Save and
/// continue" saves the fields first, "Discard changes" drops them, "Keep editing" stays. The
/// owner calls <see cref="Ask"/> with what would happen next and does nothing until the creator
/// answers.
/// </summary>
internal sealed class DraftBar : Border
{
    private readonly TextBlock _text = Ui.Wrapped("", "small");
    private readonly Func<bool> _save;
    private readonly Action _discard;
    private Action? _next;

    /// <param name="prefix">Names the bar and its buttons (<c>{prefix}DraftBar</c>, <c>{prefix}DraftSave</c>…).</param>
    /// <param name="save">Saves the fields; false when they could not be saved (the form says why).</param>
    /// <param name="discard">Drops the fields (rebuilds them from the project).</param>
    public DraftBar(string prefix, Func<bool> save, Action discard)
    {
        _save = save;
        _discard = discard;
        Name = $"{prefix}DraftBar";
        IsVisible = false;
        _text.Name = $"{prefix}DraftText";
        AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Assertive);
        var saveButton = Ui.Button("Save and continue", SaveAndContinue, "accent", "small");
        saveButton.Name = $"{prefix}DraftSave";
        var discardButton = Ui.Button("Discard changes", DiscardAndContinue, "tool", "small");
        discardButton.Name = $"{prefix}DraftDiscard";
        var keep = Ui.Button("Keep editing", Hide, "tool", "small");
        keep.Name = $"{prefix}DraftKeep";
        Child = Ui.VStack(8, _text, Ui.HStack(8, saveButton, discardButton, keep));
        Classes.Add("notice");
        Margin = new Thickness(0, 0, 0, 4);
    }

    /// <summary>Asks about the unsaved changes to <paramref name="what"/>; <paramref name="next"/> runs once they are saved or dropped.</summary>
    public void Ask(string what, Action next)
    {
        _next = next;
        _text.Text = $"You have unsaved changes to {what}. Save them before you go on?";
        IsVisible = true;
    }

    /// <summary>Stops asking (the fields were saved or reverted another way).</summary>
    public void Hide()
    {
        _next = null;
        IsVisible = false;
    }

    // Saving or discarding rebuilds the form, which hides the bar: take what comes next first.
    private void SaveAndContinue()
    {
        var next = _next;
        if (!_save())
        {
            // Not saved: the form says why, and the question stays.
            _next = next;
            IsVisible = true;
            return;
        }

        Hide();
        next?.Invoke();
    }

    private void DiscardAndContinue()
    {
        var next = _next;
        _discard();
        Hide();
        next?.Invoke();
    }
}
