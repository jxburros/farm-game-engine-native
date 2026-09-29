using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using FarmEngine.Interop;
using FarmingRpgMaker.App.Localization;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Play Mode: the Rust game player (<see cref="RustPlayer"/>) filling the surface — world, HUD,
/// dialogue, shops, crafting, inventory, quests, minigames and toasts are all drawn by the
/// player, exactly as the exported game shows them — plus the editor's own tools: Restart, Keep
/// changes and the debug drawer.
/// <para>
/// Frames run on a worker thread (plugin hooks never block the UI); the UI thread only queues
/// input and copies finished frames to the screen. Every call into the player holds one lock,
/// so the debug drawer and "keep changes" wait for a frame in flight.
/// </para>
/// </summary>
public sealed class PlayModeView : UserControl
{
    private readonly object _gate = new();
    private readonly PlayerSurface _surface = new();
    private readonly Panel _overlayLayer = new() { Name = "PlayOverlayLayer" };
    private readonly ToastHost _toasts = new() { Margin = new Thickness(16) };
    private readonly ToggleButton _keepChanges;
    private readonly List<PlayerInput> _pending = [];
    private RustPlayer _player;
    private Control? _debugView;
    private TopLevel? _topLevel;
    private bool _running;
    private bool _inFlight;
    private bool _faulted;
    private TimeSpan? _lastFrameTime;
    private byte[]? _pixels;

    public PlayModeView(RustPlayer player, bool autoRun = true)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        AutoRun = autoRun;
        Name = "PlayModeView";

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Button Tool(string name, object content, Action action, string tip)
        {
            var button = Ui.Button(content, action, "tool");
            button.Name = name;
            button.Margin = new Thickness(6, 3, 0, 3);
            button.Focusable = false;
            ToolTip.SetTip(button, tip);
            toolbar.Children.Add(button);
            return button;
        }

        // Labels in the editor's language (EditorStrings); a playtest starts a new view.
        Tool("RestartButton", Ui.IconLabel("IconRefresh", EditorStrings.Get("toolbar.restart")), () => RestartRequested?.Invoke(this, EventArgs.Empty), EditorStrings.Get("toolbar.restartTip"));
        _keepChanges = new ToggleButton { Name = "KeepChangesButton", Content = Ui.IconLabel("IconCheckCircle", EditorStrings.Get("toolbar.keepChanges")), Margin = new Thickness(6, 3, 0, 3), Focusable = false };
        _keepChanges.Classes.Add("tool");
        ToolTip.SetTip(_keepChanges, EditorStrings.Get("toolbar.keepChangesTip"));
        _keepChanges.IsCheckedChanged += (_, _) =>
        {
            ShowToast(_keepChanges.IsChecked == true
                ? new ToastMessage(EditorStrings.Get("toolbar.keepOn"), ToastKind.Success)
                : new ToastMessage(EditorStrings.Get("toolbar.keepOff"), ToastKind.Info));
        };
        toolbar.Children.Add(_keepChanges);
        Tool("DebugButton", EditorStrings.Get("toolbar.debug"), ToggleDebug, EditorStrings.Get("toolbar.debugTip"));

        var hint = Ui.Text(EditorStrings.Get("toolbar.playHint"), "muted", "small");
        hint.VerticalAlignment = VerticalAlignment.Center;
        var barGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        barGrid.Children.Add(hint);
        Grid.SetColumn(toolbar, 1);
        barGrid.Children.Add(toolbar);
        var bar = new Border { Name = "PlayToolbar", Child = barGrid, Margin = new Thickness(0, 0, 0, 8) }.WithClasses("hud");
        DockPanel.SetDock(bar, Dock.Top);

        var frame = new Border { Name = "GameFrame", Child = _surface }.WithClasses("game-frame");
        var playArea = new Grid { Name = "PlayArea" };
        playArea.Children.Add(frame);
        playArea.Children.Add(_overlayLayer);
        playArea.Children.Add(_toasts);

        var root = new DockPanel();
        root.Children.Add(bar);
        root.Children.Add(playArea);
        Content = root;

        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerExited += (_, _) => _pending.Add(PlayerInput.PointerLeft());
        _surface.PointerWheelChanged += OnPointerWheel;
        _surface.LostFocus += (_, _) => _pending.Add(PlayerInput.FocusLost());
        // Take the keyboard from whatever was focused (the Play button): Space and Enter belong to the game.
        _surface.AttachedToVisualTree += (_, _) => _surface.Focus();
    }

    /// <summary>When false, no frame loop runs; drive frames with <see cref="AdvanceFrame"/> (tests).</summary>
    public bool AutoRun { get; set; }

    public PlayerSurface Surface => _surface;

    public ToastHost Toasts => _toasts;

    public bool IsDebugOpen => _debugView is not null;

    /// <summary>What the last frame reported (screen, open modals, sounds).</summary>
    public PlayerFrameInfo LastFrame { get; private set; } = PlayerFrameInfo.Empty;

    /// <summary>The "Keep changes" toggle.</summary>
    public bool KeepChanges
    {
        get => _keepChanges.IsChecked == true;
        set => _keepChanges.IsChecked = value;
    }

    /// <summary>The user asked to restart the playtest from the pre-play snapshot.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>
    /// The game stopped (an engine or plugin failure). The loop has stopped; the host ends the
    /// playtest without keeping changes so the editor (and the autosave) survive.
    /// </summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>Runs <paramref name="action"/> on the player while no frame is in flight.</summary>
    public T Use<T>(Func<RustPlayer, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            return action(_player);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the player while no frame is in flight.</summary>
    public void Use(Action<RustPlayer> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            action(_player);
        }
    }

    /// <summary>Swaps in a new player (restart) and frees the old one; the debug drawer refreshes.</summary>
    public void Attach(RustPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        lock (_gate)
        {
            _player.Dispose();
            _player = player;
        }

        _faulted = false;
        _pending.Clear();
        LastFrame = PlayerFrameInfo.Empty;
        if (_debugView is not null)
        {
            RebuildDebug();
        }

        StartLoop();
        if (!AutoRun)
        {
            AdvanceFrame(0);
        }
    }

    /// <summary>Stops the loop and frees the player (the host read what it needed first).</summary>
    public void Close()
    {
        _running = false;
        lock (_gate)
        {
            _player.Dispose();
        }
    }

    public void ShowToast(ToastMessage message) => _toasts.Show(message);

    /// <summary>Queues an input event for the next frame (tests; keys and the pointer arrive here too).</summary>
    public void Send(PlayerInput input) => _pending.Add(input);

    /// <summary>
    /// One frame on the calling thread: the queued input goes in, the frame is shown. Tests call
    /// it with fixed deltas; the display loop runs the same frame on a worker thread instead.
    /// </summary>
    public void AdvanceFrame(double deltaSeconds)
    {
        if (_faulted)
        {
            return;
        }

        var events = TakeInput();
        var size = _surface.FrameSize();
        PlayerFrame frame;
        try
        {
            lock (_gate)
            {
                frame = _player.Frame(deltaSeconds, events, size.Width, size.Height, render: true, reuse: _pixels);
            }
        }
        catch (FarmFfiException ex)
        {
            Fault(ex);
            return;
        }

        Show(frame);
    }

    public void ToggleDebug()
    {
        if (_debugView is null)
        {
            RebuildDebug();
        }
        else
        {
            _debugView = null;
            _overlayLayer.Children.Clear();
        }
    }

    /// <summary>Rebuilds the debug drawer from the live game (after a debug action).</summary>
    internal void RebuildDebug()
    {
        PlayerSummary summary;
        try
        {
            summary = Use(player => player.Summary());
        }
        catch (FarmFfiException ex)
        {
            ShowToast(new ToastMessage(ex.Message, ToastKind.Error));
            return;
        }

        _debugView = DebugDrawer.Build(this, summary, ToggleDebug);
        _overlayLayer.Children.Clear();
        _overlayLayer.Children.Add(_debugView);
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
        if (!AutoRun)
        {
            // Show the first frame so the surface is never blank.
            AdvanceFrame(0);
        }
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
    }

    private List<PlayerInput> TakeInput()
    {
        var events = new List<PlayerInput>(_pending);
        _pending.Clear();
        return events;
    }

    private void StartLoop()
    {
        if (!AutoRun || _running || _topLevel is null || _faulted)
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

        if (_inFlight)
        {
            _topLevel.RequestAnimationFrame(OnAnimationFrame);
            return;
        }

        var delta = _lastFrameTime is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.1) : 0;
        _lastFrameTime = time;
        var events = TakeInput();
        var size = _surface.FrameSize();
        var reuse = _pixels;
        var player = _player;
        _inFlight = true;
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                // A restart swapped the player while this frame waited: skip it.
                return ReferenceEquals(player, _player) ? _player.Frame(delta, events, size.Width, size.Height, render: true, reuse: reuse) : null;
            }
        }).ContinueWith(task => Dispatcher.UIThread.Post(() => OnFrameDone(task)), TaskScheduler.Default);
    }

    private void OnFrameDone(Task<PlayerFrame?> task)
    {
        _inFlight = false;
        if (!_running)
        {
            return;
        }

        if (task.Exception?.GetBaseException() is { } error)
        {
            Fault(error);
            return;
        }

        if (task.Result is { } frame)
        {
            Show(frame);
        }

        _topLevel?.RequestAnimationFrame(OnAnimationFrame);
    }

    private void Show(PlayerFrame frame)
    {
        _pixels = frame.Pixels;
        LastFrame = frame.Info;
        _surface.Present(frame);
    }

    private void Fault(Exception exception)
    {
        _running = false;
        _faulted = true;
        Faulted?.Invoke(this, exception);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => _pending.Add(PlayerInput.FocusLost());

    private static bool IsTextInput(object? source) => source is TextBox;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsEffectivelyVisible || IsTextInput(e.Source))
        {
            return;
        }

        // Editor shortcuts (Ctrl+N, F5/F6, Alt+F4) stay with the editor.
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
        {
            return;
        }

        if (PlayerKeys.FromAvalonia(e.Key) is not { } key)
        {
            return;
        }

        _pending.Add(PlayerInput.KeyDown(key));
        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        // Always released, so a key never sticks when focus moved meanwhile.
        if (!IsEffectivelyVisible || PlayerKeys.FromAvalonia(e.Key) is not { } key)
        {
            return;
        }

        _pending.Add(PlayerInput.KeyUp(key));
        // A focused button must not see Space or Enter come up (it would click).
        if (!IsTextInput(e.Source) && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
        {
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = _surface.ToFramePixels(e.GetPosition(_surface));
        _pending.Add(PlayerInput.PointerMove(point.X, point.Y));
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _surface.Focus();
        var point = _surface.ToFramePixels(e.GetPosition(_surface));
        _pending.Add(PlayerInput.PointerDown(point.X, point.Y, ButtonOf(e.GetCurrentPoint(_surface).Properties.PointerUpdateKind)));
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var point = _surface.ToFramePixels(e.GetPosition(_surface));
        _pending.Add(PlayerInput.PointerUp(point.X, point.Y, e.InitialPressMouseButton switch
        {
            MouseButton.Right => PlayerPointerButton.Secondary,
            MouseButton.Middle => PlayerPointerButton.Middle,
            _ => PlayerPointerButton.Primary,
        }));
        e.Handled = true;
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        _pending.Add(PlayerInput.Wheel(e.Delta.X, e.Delta.Y));
        e.Handled = true;
    }

    private static PlayerPointerButton ButtonOf(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.RightButtonPressed => PlayerPointerButton.Secondary,
        PointerUpdateKind.MiddleButtonPressed => PlayerPointerButton.Middle,
        _ => PlayerPointerButton.Primary,
    };
}
