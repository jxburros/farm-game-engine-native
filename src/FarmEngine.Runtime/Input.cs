using FarmEngine.Core;
using FarmEngine.Schemas;

namespace FarmEngine.Runtime;

/// <summary>Held movement vector, per axis −1/0/1.</summary>
public readonly record struct MoveVector(double Dx, double Dy);

/// <summary>
/// Frame-polled keyboard input manager (port of input.ts). Tracks held keys
/// and one-shot presses; the game loop drains it into engine commands.
///
/// Keys are normalized TS <c>KeyboardEvent.key.toLowerCase()</c> values
/// (<c>"w"</c>, <c>"arrowup"</c>, <c>" "</c>, <c>"enter"</c>, <c>"escape"</c>, …) so
/// bindings, creator hotkeys and saved settings are interchangeable with the
/// web version. Hosts translate their native key events with
/// <see cref="KeyNames"/> (<see cref="KeyNames.FromAvalonia"/>,
/// <see cref="KeyNames.FromDomCode"/>) or the <see cref="KeyDownAvalonia"/>
/// convenience overloads.
/// </summary>
public sealed class InputManager
{
    /// <summary>Default gameplay keys whose browser/host default action is suppressed.</summary>
    public static readonly IReadOnlySet<string> DefaultPreventDefaultKeys = new HashSet<string>
    {
        "arrowup", "arrowdown", "arrowleft", "arrowright",
        "w", "a", "s", "d", "e", "q", "t", "r", "f", "c", "x", "z", "i", "j", " ", "enter",
    };

    private readonly IReadOnlySet<string> _preventDefaultKeys;

    public HashSet<string> KeysDown { get; } = [];
    public HashSet<string> KeysJustPressed { get; } = [];

    public InputManager(IReadOnlySet<string>? preventDefaultKeys = null)
    {
        _preventDefaultKeys = preventDefaultKeys ?? DefaultPreventDefaultKeys;
    }

    /// <summary>
    /// Feed a key-down. <paramref name="key"/> is a <c>KeyboardEvent.key</c> value
    /// (any case). Keystrokes aimed at editable elements (text fields, selects,
    /// contenteditable — <paramref name="editableTarget"/>) belong to those
    /// elements, not the game: they are ignored so gameplay-bound letters can be
    /// typed into editor fields. Returns true when the host should suppress the
    /// key's default action (TS <c>preventDefault</c>): gameplay keys without
    /// Ctrl/Meta/Alt modifiers.
    /// </summary>
    public bool KeyDown(string key, bool ctrl = false, bool meta = false, bool alt = false, bool editableTarget = false)
    {
        if (editableTarget) return false;
        var normalized = key.ToLowerInvariant();
        KeysDown.Add(normalized);
        KeysJustPressed.Add(normalized);
        return _preventDefaultKeys.Contains(normalized) && !ctrl && !meta && !alt;
    }

    /// <summary>Feed a key-up (<c>KeyboardEvent.key</c> value, any case).</summary>
    public void KeyUp(string key) => KeysDown.Remove(key.ToLowerInvariant());

    /// <summary>
    /// <see cref="KeyDown"/> for an Avalonia <c>Key</c> enum name (<c>key.ToString()</c>).
    /// Unmapped keys are ignored (returns false).
    /// </summary>
    public bool KeyDownAvalonia(string avaloniaKeyName, bool ctrl = false, bool meta = false, bool alt = false, bool editableTarget = false)
    {
        var key = KeyNames.FromAvalonia(avaloniaKeyName);
        return key is not null && KeyDown(key, ctrl, meta, alt, editableTarget);
    }

    /// <summary><see cref="KeyUp"/> for an Avalonia <c>Key</c> enum name.</summary>
    public void KeyUpAvalonia(string avaloniaKeyName)
    {
        var key = KeyNames.FromAvalonia(avaloniaKeyName);
        if (key is not null) KeyUp(key);
    }

    /// <summary>Held OR tapped since the last frame (so sub-frame taps still register).</summary>
    public bool Pressed(string key) => KeysDown.Contains(key) || KeysJustPressed.Contains(key);

    public bool JustPressed(string key) => KeysJustPressed.Contains(key);

    /// <summary>
    /// Held movement vector from WASD/arrows, per axis −1/0/1 (free movement:
    /// both axes may be active at once; opposing keys cancel).
    /// </summary>
    public MoveVector MoveVector()
    {
        double dx = 0;
        double dy = 0;
        if (Pressed("arrowleft") || Pressed("a")) dx -= 1;
        if (Pressed("arrowright") || Pressed("d")) dx += 1;
        if (Pressed("arrowup") || Pressed("w")) dy -= 1;
        if (Pressed("arrowdown") || Pressed("s")) dy += 1;
        return new MoveVector(dx, dy);
    }

    /// <summary>Current movement direction from WASD/arrows, if any (one of <see cref="Directions"/>).</summary>
    public string? Direction()
    {
        if (Pressed("arrowup") || Pressed("w")) return Directions.Up;
        if (Pressed("arrowdown") || Pressed("s")) return Directions.Down;
        if (Pressed("arrowleft") || Pressed("a")) return Directions.Left;
        if (Pressed("arrowright") || Pressed("d")) return Directions.Right;
        return null;
    }

    /// <summary>Call at the end of each frame.</summary>
    public void EndFrame() => KeysJustPressed.Clear();

    /// <summary>Forget everything (focus loss, leaving play mode).</summary>
    public void Clear()
    {
        KeysDown.Clear();
        KeysJustPressed.Clear();
    }
}

/// <summary>
/// Host key-name translation into the normalized <c>KeyboardEvent.key</c>
/// lowercase vocabulary <see cref="InputManager"/> uses.
/// </summary>
public static class KeyNames
{
    public const string ArrowUp = "arrowup";
    public const string ArrowDown = "arrowdown";
    public const string ArrowLeft = "arrowleft";
    public const string ArrowRight = "arrowright";
    public const string Space = " ";
    public const string Enter = "enter";
    public const string Escape = "escape";

    private static readonly Dictionary<string, string> Avalonia = new(StringComparer.Ordinal)
    {
        ["Up"] = ArrowUp, ["Down"] = ArrowDown, ["Left"] = ArrowLeft, ["Right"] = ArrowRight,
        ["Space"] = Space, ["Enter"] = Enter, ["Return"] = Enter, ["Escape"] = Escape,
        ["Tab"] = "tab", ["Back"] = "backspace", ["Delete"] = "delete", ["Insert"] = "insert",
        ["Home"] = "home", ["End"] = "end",
        ["PageUp"] = "pageup", ["Prior"] = "pageup", ["PageDown"] = "pagedown", ["Next"] = "pagedown",
        ["LeftShift"] = "shift", ["RightShift"] = "shift",
        ["LeftCtrl"] = "control", ["RightCtrl"] = "control",
        ["LeftAlt"] = "alt", ["RightAlt"] = "alt",
        ["LWin"] = "meta", ["RWin"] = "meta",
        ["OemComma"] = ",", ["OemPeriod"] = ".", ["OemMinus"] = "-", ["OemPlus"] = "=",
        ["OemQuestion"] = "/", ["Oem2"] = "/", ["OemSemicolon"] = ";", ["Oem1"] = ";",
        ["OemQuotes"] = "'", ["Oem7"] = "'", ["OemOpenBrackets"] = "[", ["Oem4"] = "[",
        ["OemCloseBrackets"] = "]", ["Oem6"] = "]", ["OemPipe"] = "\\", ["Oem5"] = "\\",
        ["OemBackslash"] = "\\", ["OemTilde"] = "`", ["Oem3"] = "`",
        ["Multiply"] = "*", ["Add"] = "+", ["Subtract"] = "-", ["Divide"] = "/", ["Decimal"] = ".",
    };

    private static readonly Dictionary<string, string> DomCodes = new(StringComparer.Ordinal)
    {
        ["ArrowUp"] = ArrowUp, ["ArrowDown"] = ArrowDown, ["ArrowLeft"] = ArrowLeft, ["ArrowRight"] = ArrowRight,
        ["Space"] = Space, ["Enter"] = Enter, ["NumpadEnter"] = Enter, ["Escape"] = Escape,
        ["Tab"] = "tab", ["Backspace"] = "backspace", ["Delete"] = "delete", ["Insert"] = "insert",
        ["Home"] = "home", ["End"] = "end", ["PageUp"] = "pageup", ["PageDown"] = "pagedown",
        ["ShiftLeft"] = "shift", ["ShiftRight"] = "shift", ["ControlLeft"] = "control", ["ControlRight"] = "control",
        ["AltLeft"] = "alt", ["AltRight"] = "alt", ["MetaLeft"] = "meta", ["MetaRight"] = "meta",
        ["Comma"] = ",", ["Period"] = ".", ["Minus"] = "-", ["Equal"] = "=", ["Slash"] = "/",
        ["Semicolon"] = ";", ["Quote"] = "'", ["BracketLeft"] = "[", ["BracketRight"] = "]",
        ["Backslash"] = "\\", ["Backquote"] = "`",
        ["NumpadMultiply"] = "*", ["NumpadAdd"] = "+", ["NumpadSubtract"] = "-", ["NumpadDivide"] = "/", ["NumpadDecimal"] = ".",
    };

    /// <summary>
    /// Map an Avalonia <c>Key</c> enum name (<c>"W"</c>, <c>"Up"</c>, <c>"Space"</c>,
    /// <c>"Enter"</c>, <c>"D1"</c>, <c>"NumPad1"</c>, <c>"F5"</c>, …) to the normalized
    /// <c>KeyboardEvent.key</c> value (unshifted), or null when unmapped.
    /// </summary>
    public static string? FromAvalonia(string avaloniaKeyName)
    {
        if (string.IsNullOrEmpty(avaloniaKeyName)) return null;
        if (avaloniaKeyName.Length == 1 && char.IsAsciiLetter(avaloniaKeyName[0]))
            return avaloniaKeyName.ToLowerInvariant();
        if (avaloniaKeyName.Length == 2 && avaloniaKeyName[0] == 'D' && char.IsAsciiDigit(avaloniaKeyName[1]))
            return avaloniaKeyName[1..];
        if (avaloniaKeyName.Length == 7 && avaloniaKeyName.StartsWith("NumPad", StringComparison.Ordinal) && char.IsAsciiDigit(avaloniaKeyName[6]))
            return avaloniaKeyName[6..];
        if (IsFunctionKey(avaloniaKeyName)) return avaloniaKeyName.ToLowerInvariant();
        return Avalonia.TryGetValue(avaloniaKeyName, out var key) ? key : null;
    }

    /// <summary>
    /// Map a DOM <c>KeyboardEvent.code</c> (<c>"KeyW"</c>, <c>"ArrowUp"</c>, <c>"Space"</c>,
    /// <c>"Digit1"</c>, <c>"Numpad1"</c>, <c>"F5"</c>, …) to the normalized
    /// <c>KeyboardEvent.key</c> value of a US layout, or null when unmapped.
    /// </summary>
    public static string? FromDomCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && char.IsAsciiLetter(code[3]))
            return code[3..].ToLowerInvariant();
        if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && char.IsAsciiDigit(code[5]))
            return code[5..];
        if (code.Length == 7 && code.StartsWith("Numpad", StringComparison.Ordinal) && char.IsAsciiDigit(code[6]))
            return code[6..];
        if (IsFunctionKey(code)) return code.ToLowerInvariant();
        return DomCodes.TryGetValue(code, out var key) ? key : null;
    }

    /// <summary>Normalize a <c>KeyboardEvent.key</c> value (TS <c>e.key.toLowerCase()</c>).</summary>
    public static string FromDomKey(string key) => key.ToLowerInvariant();

    private static bool IsFunctionKey(string name) =>
        name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24;
}

/// <summary>What a play-mode frame's one-shot input asks the host to do.</summary>
/// <param name="Commands">Engine commands to run, in order.</param>
/// <param name="ToggleInventory">I — inventory panel.</param>
/// <param name="ToggleQuests">J — quest journal.</param>
/// <param name="ToggleCrafting">X — crafting panel.</param>
/// <param name="Escape">Escape with no engine modal open (host menu / close its own modal).</param>
public sealed record PlayFrameInput(
    List<Command> Commands,
    bool ToggleInventory,
    bool ToggleQuests,
    bool ToggleCrafting,
    bool Escape);

/// <summary>
/// The play-mode key → action bindings shared by the editor's play mode
/// (App.tsx) and the exported game shell (game-shell/main.ts), plus
/// <c>RESERVED_ACTION_KEYS</c> (src/lib/reserved-keys.ts).
/// </summary>
public static class InputBindings
{
    /// <summary>
    /// Gameplay keys that creator-defined action hotkeys may never claim.
    /// Shared between the play-mode input handler and the Actions editor, which
    /// warns when an author picks a colliding hotkey.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedActionKeys = new HashSet<string>
    {
        "w", "a", "s", "d", "e", "q", "t", "r", "f", "c", "z", "i", "j", "x",
        " ", "enter", "escape", "arrowup", "arrowdown", "arrowleft", "arrowright",
    };

    /// <summary>Keys that interact (talk, harvest, open, …).</summary>
    public static readonly IReadOnlyList<string> InteractKeys = ["e", " ", "enter"];

    /// <summary>Tool hotkeys → <see cref="UseToolCommand"/> tool type.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> ToolKeys =
    [
        new("q", "watering-can"),
        new("t", "hoe"),
        new("r", "axe"),
        new("f", "pickaxe"),
        new("c", "scythe"),
    ];

    public const string SleepKey = "z";
    public const string InventoryKey = "i";
    public const string QuestsKey = "j";
    public const string CraftingKey = "x";
    public const string EscapeKey = "escape";

    /// <summary>
    /// The movement intent for this frame: keyboard vector (plus any extra
    /// touch/D-pad vector), clamped per axis, and zero while dialogue, a shop
    /// or a minigame is open (App.tsx semantics).
    /// </summary>
    public static MoveVector MoveIntent(InputManager input, GameState state, MoveVector? extra = null)
    {
        if (state.Dialogue is not null || state.Shop is not null || state.Minigame is not null) return new MoveVector(0, 0);
        var key = input.MoveVector();
        var add = extra ?? new MoveVector(0, 0);
        return new MoveVector(
            Math.Max(-1, Math.Min(1, key.Dx + add.Dx)),
            Math.Max(-1, Math.Min(1, key.Dy + add.Dy)));
    }

    /// <summary>
    /// Translate this frame's one-shot presses into commands and UI toggles
    /// (game-shell <c>update()</c> semantics). Does NOT call
    /// <see cref="InputManager.EndFrame"/> — the host does, once per frame.
    ///
    /// While an engine modal (dialogue/shop/minigame) or a host modal
    /// (<paramref name="hostModalOpen"/>) is open, only Escape is handled: it closes
    /// the host modal first (reported via <see cref="PlayFrameInput.Escape"/>),
    /// else cancels the minigame, else closes the shop, else the dialogue.
    /// </summary>
    public static PlayFrameInput PollPlayFrame(InputManager input, GameState state, GameContent content, bool hostModalOpen = false)
    {
        var commands = new List<Command>();
        if (hostModalOpen || state.Dialogue is not null || state.Shop is not null || state.Minigame is not null)
        {
            var escape = false;
            if (input.JustPressed(EscapeKey))
            {
                if (hostModalOpen) escape = true;
                else if (state.Minigame is not null) commands.Add(new CancelMinigameCommand());
                else if (state.Shop is not null) commands.Add(new CloseShopCommand());
                else if (state.Dialogue is not null) commands.Add(new CloseDialogueCommand());
            }
            return new PlayFrameInput(commands, false, false, false, escape);
        }

        if (InteractKeys.Any(input.JustPressed)) commands.Add(new InteractCommand());
        foreach (var (key, tool) in ToolKeys)
        {
            if (input.JustPressed(key)) commands.Add(new UseToolCommand(tool));
        }
        if (input.JustPressed(SleepKey)) commands.Add(new SleepCommand());

        // Creator-defined action hotkeys (extensibility layer) — reserved
        // gameplay keys never fire actions.
        foreach (var action in content.Actions)
        {
            var key = action.Hotkey?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(key) && !ReservedActionKeys.Contains(key) && input.JustPressed(key))
            {
                commands.Add(new PerformActionCommand(action.Id));
            }
        }

        return new PlayFrameInput(
            commands,
            input.JustPressed(InventoryKey),
            input.JustPressed(QuestsKey),
            input.JustPressed(CraftingKey),
            input.JustPressed(EscapeKey));
    }
}
