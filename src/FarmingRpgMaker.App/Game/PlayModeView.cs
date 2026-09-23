using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using FarmEngine.Core;
using FarmEngine.Runtime;
using FarmEngine.Schemas;

namespace FarmingRpgMaker.App.Game;

/// <summary>App-level panels over the game (web <c>showInventory/showQuests/showCrafting</c>).</summary>
public enum HostPanel
{
    None,
    Inventory,
    Quests,
    Crafting,
}

/// <summary>
/// Play Mode (web App.tsx play branch): HUD bar + toolbar, the game canvas driven by a
/// display-rate frame loop (fixed-timestep simulation, interpolated rendering), keyboard
/// input → engine commands, effects → toasts/sounds, and the modal panels (dialogue, shop,
/// crafting, inventory, quests, minigames, debug drawer, creator game panels).
/// </summary>
public sealed class PlayModeView : UserControl
{
    private readonly GameCanvas _canvas = new() { Name = "PlayCanvas", ZoomMode = CanvasZoomMode.Fit };
    private readonly Panel _overlayLayer = new() { Name = "PlayOverlayLayer" };
    private readonly Grid _playArea = new() { Name = "PlayArea" };
    private readonly ToastHost _toasts = new() { Margin = new Thickness(16) };
    private readonly WrapPanel _gamePanels = new() { Name = "GamePanels", HorizontalAlignment = HorizontalAlignment.Center };
    private static readonly object Stale = new();
    private readonly MinigameRegistry _minigames = Minigames.CreateDefaultMinigameRegistry();

    // HUD
    private readonly TextBlock _money = new() { Name = "HudMoney" };
    private readonly TextBlock _season = Ui.Text("", "hud-value", "primary");
    private readonly TextBlock _day = Ui.Text("", "hud-value", "secondary");
    private readonly TextBlock _year = Ui.Text("", "hud-value", "secondary");
    private readonly TextBlock _weather = Ui.Text("", "hud-value");
    private readonly TextBlock _time = Ui.Text("", "hud-value");
    private readonly ProgressBar _energy = new() { Name = "HudEnergy", Minimum = 0, Maximum = 100 };
    private readonly StackPanel _energyGroup;
    private readonly Button _inventoryButton;
    private readonly ToggleButton _keepChanges;

    private PlaySession _session;
    private HostPanel _panel = HostPanel.None;
    private Control? _panelView;
    private object? _panelSignature;
    private bool _debugOpen;
    private object? _debugSignature;
    private Control? _debugView;
    private DialogueState? _shownDialogue;
    private Control? _dialogueView;
    private object? _shopSignature;
    private Control? _shopView;
    private PlayOverlays.ShopTab _shopTab = PlayOverlays.ShopTab.Buy;
    private MinigameOverlay? _minigame;
    private string? _panelsSignature;
    private TopLevel? _topLevel;
    private bool _running;
    private TimeSpan? _lastFrameTime;

    public PlayModeView(PlaySession session, bool autoRun = true)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        AutoRun = autoRun;
        Name = "PlayModeView";
        Focusable = true;

        _money.Classes.Add("hud-value");
        _energy.Classes.Add("energy");
        _energyGroup = Ui.HStack(8, Ui.Text("Energy:", "hud-label"), _energy);
        _energyGroup.Name = "HudEnergyGroup";
        _time.Name = "HudTime";
        _day.Name = "HudDay";
        _season.Name = "HudSeason";
        _weather.Name = "HudWeather";
        _year.Name = "HudYear";

        var stats = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void Stat(Control control)
        {
            control.Margin = new Thickness(0, 3, 18, 3);
            stats.Children.Add(control);
        }

        Stat(Ui.HStack(8, Ui.Text("Money:", "hud-label"), new Border { Child = _money }.WithClasses("money-chip")));
        Stat(Ui.HStack(6, Ui.Text("Season:", "hud-label"), _season));
        Stat(Ui.HStack(6, Ui.Text("Day:", "hud-label"), _day));
        Stat(Ui.HStack(6, Ui.Text("Year:", "hud-label"), _year));
        Stat(Ui.HStack(6, Ui.Text("Weather:", "hud-label"), _weather));
        Stat(Ui.HStack(6, Ui.Text("Time:", "hud-label"), _time));
        Stat(_energyGroup);

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Button Tool(string name, object content, Action action, string? tip)
        {
            var button = Ui.Button(content, action, "tool");
            button.Name = name;
            button.Margin = new Thickness(6, 3, 0, 3);
            if (tip is not null)
            {
                ToolTip.SetTip(button, tip);
            }

            toolbar.Children.Add(button);
            return button;
        }

        Tool("RestartButton", Ui.IconLabel("IconRefresh", "Restart"), () => RestartRequested?.Invoke(this, EventArgs.Empty), "Restore the pre-playtest snapshot and start over");
        _keepChanges = new ToggleButton { Name = "KeepChangesButton", Content = Ui.IconLabel("IconCheckCircle", "Keep changes"), Margin = new Thickness(6, 3, 0, 3) };
        _keepChanges.Classes.Add("tool");
        ToolTip.SetTip(_keepChanges, "Keep playtest changes when exiting to the editor");
        _keepChanges.IsCheckedChanged += (_, _) =>
        {
            ShowToast(_keepChanges.IsChecked == true
                ? new ToastMessage("Playtest changes will be kept when you exit", ToastKind.Success)
                : new ToastMessage("Playtest changes will be discarded when you exit", ToastKind.Info));
        };
        toolbar.Children.Add(_keepChanges);
        Tool("DebugButton", "Debug", ToggleDebug, "Playtest debug drawer");
        Tool("SleepButton", Ui.IconLabel("IconMoon", "Sleep (Z)"), () => Run(new SleepCommand()), "End the day");
        Tool("CraftButton", Ui.IconLabel("IconHammer", "Craft (X)"), () => TogglePanel(HostPanel.Crafting), null);
        Tool("QuestsButton", Ui.IconLabel("IconStar", "Quests (J)"), () => TogglePanel(HostPanel.Quests), null);
        _inventoryButton = Tool("InventoryButton", Ui.IconLabel("IconPackage", "Inventory"), () => TogglePanel(HostPanel.Inventory), "Inventory (I)");

        var hudGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        hudGrid.Children.Add(stats);
        Grid.SetColumn(toolbar, 1);
        hudGrid.Children.Add(toolbar);
        var hud = new Border { Name = "PlayHud", Child = hudGrid, Margin = new Thickness(0, 0, 0, 12) }.WithClasses("hud");
        DockPanel.SetDock(hud, Dock.Top);

        var controls = BuildControlsHelp();
        var bottom = Ui.VStack(10, _gamePanels, controls);
        bottom.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(bottom, Dock.Bottom);

        // The frame hugs the camera viewport (web: the canvas IS the viewport), centered.
        var frame = new Border { Name = "GameFrame", Child = _canvas, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.WithClasses("game-frame");
        var playArea = _playArea;
        playArea.Children.Add(frame);
        playArea.SizeChanged += (_, _) => RenderFrame();
        playArea.Children.Add(_overlayLayer);
        playArea.Children.Add(_toasts);

        var root = new DockPanel();
        root.Children.Add(hud);
        root.Children.Add(bottom);
        root.Children.Add(playArea);
        Content = root;

        Attach(session);
    }

    /// <summary>When false, no display-rate loop runs; drive frames with <see cref="AdvanceFrame"/> (tests).</summary>
    public bool AutoRun { get; set; }

    public PlaySession Session => _session;

    public GameCanvas Canvas => _canvas;

    public ToastHost Toasts => _toasts;

    public HostPanel OpenPanel => _panel;

    public bool IsDebugOpen => _debugOpen;

    /// <summary>The "Keep changes" toggle (web <c>keepPlaytestChangesRef</c>).</summary>
    public bool KeepChanges
    {
        get => _keepChanges.IsChecked == true;
        set => _keepChanges.IsChecked = value;
    }

    /// <summary>The running minigame overlay's session (null when none).</summary>
    public IMinigameSession? Minigame => _minigame?.Session;

    /// <summary>The user asked to restart the playtest from the pre-play snapshot.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>Swap in a new session (restart) — panels close, the HUD refreshes.</summary>
    public void Attach(PlaySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session.Toast -= OnSessionToast;
        _session.StateChanged -= OnSessionStateChanged;

        _session = session;
        _session.Toast += OnSessionToast;
        _session.StateChanged += OnSessionStateChanged;
        _panel = HostPanel.None;
        _debugOpen = false;
        _shownDialogue = null;
        _shopSignature = null;
        _panelSignature = null;
        _minigame?.Dispose();
        _minigame = null;
        RebuildOverlays(force: true);
        RefreshHud();
        RefreshGamePanels();
        RenderFrame();
    }

    public void ShowToast(ToastMessage message) => _toasts.Show(message);

    /// <summary>
    /// One display frame (web game loop <c>update</c> + <c>draw</c>). The loop calls this
    /// at the display rate; tests call it directly with fixed deltas.
    /// </summary>
    public void AdvanceFrame(double deltaSeconds)
    {
        _minigame?.Update(deltaSeconds);

        // While an app panel is open the engine only listens for Escape; the panel's own
        // hotkey (I / J / X) closes it again, like the web toggles.
        var panelOpen = _panel != HostPanel.None;
        var closeOwnPanel = panelOpen && _panel switch
        {
            HostPanel.Inventory => _session.Input.JustPressed(InputBindings.InventoryKey),
            HostPanel.Quests => _session.Input.JustPressed(InputBindings.QuestsKey),
            HostPanel.Crafting => _session.Input.JustPressed(InputBindings.CraftingKey),
            _ => false,
        };
        var toggles = _session.Update(deltaSeconds, hostModalOpen: panelOpen);
        if (closeOwnPanel && _panel != HostPanel.None)
        {
            ClosePanel();
        }
        if (toggles.Any)
        {
            if (toggles.Escape && _panel != HostPanel.None)
            {
                ClosePanel();
            }
            else if (toggles.Escape && _debugOpen)
            {
                ToggleDebug();
            }

            if (toggles.Inventory)
            {
                TogglePanel(HostPanel.Inventory);
            }

            if (toggles.Quests)
            {
                TogglePanel(HostPanel.Quests);
            }

            if (toggles.Crafting)
            {
                TogglePanel(HostPanel.Crafting);
            }
        }

        RenderFrame();
    }

    /// <summary>Run a command from the UI (toolbar, panels).</summary>
    public void Run(Command command) => _session.RunCommand(command);

    public void TogglePanel(HostPanel panel)
    {
        _panel = _panel == panel ? HostPanel.None : panel;
        if (_panel != HostPanel.None)
        {
            _session.ReleaseInput();
        }

        _panelSignature = Stale;
        RebuildOverlays();
    }

    public void ClosePanel()
    {
        _panel = HostPanel.None;
        _panelSignature = Stale;
        RebuildOverlays();
    }

    public void ToggleDebug()
    {
        _debugOpen = !_debugOpen;
        RebuildOverlays(force: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
        {
            _topLevel.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _topLevel.AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
            if (_topLevel is Window window)
            {
                window.Deactivated += OnWindowDeactivated;
            }
        }

        StartLoop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _running = false;
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(KeyDownEvent, OnKeyDown);
            _topLevel.RemoveHandler(KeyUpEvent, OnKeyUp);
            if (_topLevel is Window window)
            {
                window.Deactivated -= OnWindowDeactivated;
            }
        }

        _topLevel = null;
        _minigame?.Dispose();
        _minigame = null;
    }

    private void StartLoop()
    {
        if (!AutoRun || _running || _topLevel is null)
        {
            return;
        }

        _running = true;
        _lastFrameTime = null;
        _topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        if (!_running || _topLevel is null)
        {
            return;
        }

        // Cap deltaTime to prevent a spiral of death (useGameLoop.ts).
        var delta = _lastFrameTime is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.1) : 0;
        _lastFrameTime = time;
        AdvanceFrame(delta);
        _topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => _session.ReleaseInput();

    private static bool IsTextInput(object? source) => source is TextBox;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || IsTextInput(e.Source))
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var meta = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl || alt || meta)
        {
            return;
        }

        var key = KeyNames.FromAvalonia(e.Key.ToString());
        if (key is null)
        {
            return;
        }

        if (_minigame is not null && (key == KeyNames.Space || key == KeyNames.Enter))
        {
            _minigame.Press();
            e.Handled = true;
            return;
        }

        // Number keys pick dialogue options (1 = first visible option).
        if (_session.State.Dialogue is not null && key.Length == 1 && key[0] is >= '1' and <= '9')
        {
            var index = key[0] - '1';
            var options = _dialogueView is null ? null : Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(_dialogueView).OfType<StackPanel>().FirstOrDefault(p => p.Name == "DialogueOptions");
            if (options is not null && index < options.Children.Count && options.Children[index] is Button option)
            {
                option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
                return;
            }
        }

        var prevent = _session.Input.KeyDown(key, ctrl, meta, alt);
        var modalOpen = _panel != HostPanel.None || _session.EngineModalOpen;
        if ((prevent && !modalOpen) || key == KeyNames.Escape)
        {
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var key = KeyNames.FromAvalonia(e.Key.ToString());
        if (key is null)
        {
            return;
        }

        if (_minigame is not null && (key == KeyNames.Space || key == KeyNames.Enter))
        {
            _minigame.Release();
        }

        _session.Input.KeyUp(key);
    }

    private void OnSessionToast(object? sender, ToastMessage message) => ShowToast(message);

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        RefreshHud();
        RebuildOverlays();
        RefreshGamePanels();
    }

    private void RenderFrame()
    {
        if (_session.CurrentScene is null)
        {
            _canvas.Snapshot = null;
            return;
        }

        var size = _playArea.Bounds.Size;
        // The frame border takes 2px per side.
        var width = size.Width > 4 ? size.Width - 4 : 960;
        var height = size.Height > 4 ? size.Height - 4 : 560;
        // Zoom so roughly the web's 20×13-tile viewport fills the area, in half steps
        // (whole-pixel tiles keep pixel art even).
        var preferredArea = PlaySession.ViewTilesX * PlaySession.TileSize * PlaySession.ViewTilesY * PlaySession.TileSize;
        var zoom = Math.Max(1, Math.Floor(Math.Sqrt(width * height / preferredArea) * 2) / 2);
        var (viewWidth, viewHeight) = _session.ViewportFor(width, height, zoom);
        var canvasWidth = Math.Floor(viewWidth * zoom);
        var canvasHeight = Math.Floor(viewHeight * zoom);
        if (_canvas.Width != canvasWidth || _canvas.Height != canvasHeight)
        {
            _canvas.Width = canvasWidth;
            _canvas.Height = canvasHeight;
        }

        _canvas.Snapshot = _session.BuildSnapshot(viewWidth, viewHeight);
    }

    private void RefreshHud()
    {
        var state = _session.State;
        var content = _session.Content;
        var calendar = content.Settings.Calendar;
        SetText(_money, Ui.Money(state.Player.Money));
        SetText(_season, GameTime.SeasonById(calendar, state.Clock.Season)?.Name ?? Ui.Capitalize(state.Clock.Season));
        var seasonDays = GameTime.SeasonById(calendar, state.Clock.Season)?.Days ?? ContentBuiltin.DaysPerSeason;
        SetText(_day, $"{Ui.Num(GameTime.DayOfSeason(calendar, state.Clock.Day))} / {Ui.Num(seasonDays)}");
        SetText(_year, Ui.Num(state.Clock.Year));
        SetText(_weather, content.Weather.Types.FirstOrDefault(w => w.Id == state.Clock.WeatherId)?.Name ?? "Sunny");
        SetText(_time, GameTime.FormatTimeOfDay(Math.Floor(state.Clock.TimeMinutes)));
        _energyGroup.IsVisible = content.Settings.EnergyEnabled;
        var maxEnergy = state.Player.MaxEnergy > 0 ? state.Player.MaxEnergy : content.Settings.MaxEnergy;
        var ratio = maxEnergy > 0 ? state.Player.Energy / maxEnergy : 0;
        _energy.Value = Math.Clamp(ratio * 100, 0, 100);
        _energy.Classes.Set("low", ratio <= 0.2);
        ToolTip.SetTip(_energy, $"{Ui.Num(Math.Floor(state.Player.Energy))} / {Ui.Num(maxEnergy)}");
        var inventoryText = $"Inventory ({state.Player.Inventory.Count}/{Ui.Num(state.Player.MaxInventorySize)})";
        if (_inventoryButton.Content is StackPanel label && label.Children[1] is TextBlock text)
        {
            SetText(text, inventoryText);
        }
    }

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text)
        {
            block.Text = text;
        }
    }

    /// <summary>Keeps the overlay layer in sync with engine modals and the open app panel.</summary>
    private void RebuildOverlays(bool force = false)
    {
        var state = _session.State;
        var changed = force;

        // Dialogue (engine modal).
        if (force || !ReferenceEquals(state.Dialogue, _shownDialogue))
        {
            _shownDialogue = state.Dialogue;
            _dialogueView = PlayOverlays.Dialogue(_session);
            changed = true;
        }

        // Shop (engine modal) — rebuilt when anything it shows changed.
        var shopSignature = state.Shop is null ? null : (object)(state.Shop, state.Player.Money, state.Player.Inventory, state.ShopPurchasesToday, _shopTab);
        if (force || !Equals(shopSignature, _shopSignature))
        {
            if (state.Shop is null)
            {
                _shopTab = PlayOverlays.ShopTab.Buy;
            }

            _shopSignature = state.Shop is null ? null : (state.Shop, state.Player.Money, state.Player.Inventory, state.ShopPurchasesToday, _shopTab);
            _shopView = PlayOverlays.Shop(_session, _shopTab, tab =>
            {
                _shopTab = tab;
                _shopSignature = Stale;
                RebuildOverlays();
            });
            changed = true;
        }

        // Minigame (engine modal): mount/unmount the runtime session.
        if (state.Minigame is null && _minigame is not null)
        {
            _minigame.Dispose();
            _minigame = null;
            changed = true;
        }
        else if (state.Minigame is not null && (_minigame is null || !ReferenceEquals(_minigame.State, state.Minigame)))
        {
            _minigame?.Dispose();
            _minigame = new MinigameOverlay(_session, _minigames, state.Minigame);
            changed = true;
        }

        // App panels.
        object? panelSignature = _panel switch
        {
            HostPanel.Inventory => (_panel, state.Player.Inventory, state.Player.Money),
            HostPanel.Quests => (_panel, state.Quests),
            HostPanel.Crafting => (_panel, state.Player, state.World),
            _ => null,
        };
        if (force || !Equals(panelSignature, _panelSignature))
        {
            _panelSignature = panelSignature;
            _panelView = _panel switch
            {
                HostPanel.Inventory => PlayOverlays.Inventory(_session, ClosePanel),
                HostPanel.Quests => PlayOverlays.Quests(_session, ClosePanel),
                HostPanel.Crafting => PlayOverlays.Crafting(_session, ClosePanel),
                _ => null,
            };
            changed = true;
        }

        // Debug drawer: rebuilt when what it highlights changes (keeps a typed flag name otherwise).
        var debugSignature = _debugOpen ? (object)(state.Clock.Season, state.Player.SceneId, state.Clock.Day) : null;
        if (force || !Equals(debugSignature, _debugSignature))
        {
            _debugSignature = debugSignature;
            _debugView = _debugOpen ? DebugDrawer.Build(_session, ShowToast, ToggleDebug) : null;
            changed = true;
        }

        if (!changed && !force)
        {
            return;
        }

        _overlayLayer.Children.Clear();
        foreach (var view in new[] { _debugView, _panelView, _shopView, _dialogueView, _minigame?.View })
        {
            if (view is not null)
            {
                _overlayLayer.Children.Add(view);
            }
        }
    }

    private void RefreshGamePanels()
    {
        var panels = _session.Project.GamePanels ?? [];
        if (panels.Count == 0)
        {
            _gamePanels.IsVisible = false;
            return;
        }

        var views = GamePanels.Render(panels, PanelState.FromGameState(_session.State, _panel != HostPanel.None));
        var signature = string.Join("|", views.Select(v => $"{v.Id}:{v.Hidden}:{string.Join(",", v.Entries.Select(e => $"{e.Text}/{e.Enabled}"))}"));
        if (signature == _panelsSignature)
        {
            return;
        }

        _panelsSignature = signature;
        _gamePanels.IsVisible = true;
        _gamePanels.Children.Clear();
        foreach (var view in views.Where(v => !v.Hidden))
        {
            var row = Ui.HStack(12, Ui.Text(view.Title, "h3"));
            foreach (var entry in view.Entries)
            {
                if (entry.ActionId is { } actionId)
                {
                    var button = Ui.Button(entry.Text, () => Run(new PerformActionCommand(actionId)), "tool", "small");
                    button.IsEnabled = entry.Enabled;
                    row.Children.Add(button);
                }
                else
                {
                    row.Children.Add(Ui.Text(entry.Text));
                }
            }

            _gamePanels.Children.Add(new Border { Child = row, Margin = new Thickness(0, 0, 8, 8) }.WithClasses("game-panel"));
        }
    }

    private static Border BuildControlsHelp()
    {
        var keys = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        void Key(string key, string label)
        {
            var item = Ui.HStack(5, PlayOverlays.Keycap(key), Ui.Text(label, "muted", "small"));
            item.Margin = new Thickness(7, 2);
            keys.Children.Add(item);
        }

        Key("WASD", "Move");
        Key("E", "Interact");
        Key("Q", "Water");
        Key("T", "Till");
        Key("R", "Axe");
        Key("F", "Pickaxe");
        Key("C", "Scythe");
        Key("X", "Craft");
        Key("Z", "Sleep");
        Key("I", "Inventory");
        Key("J", "Quests");
        Key("Esc", "Close");
        return new Border { Name = "ControlsHelp", Child = keys, HorizontalAlignment = HorizontalAlignment.Center }.WithClasses("controls-help");
    }
}
