using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;

namespace FarmEngine.Interop;

/// <summary>A pointer button (<c>farm_player::PointerButton</c>).</summary>
public enum PlayerPointerButton
{
    Primary,
    Secondary,
    Middle,
}

/// <summary>
/// One input event for <see cref="RustPlayer.Frame"/> (<c>farm_player::InputEvent</c>). Keys use
/// the engine's names (<c>"w"</c>, <c>"arrowup"</c>, <c>" "</c>, <c>"enter"</c>, <c>"escape"</c>);
/// pointer positions are pixels of the frame.
/// </summary>
public readonly record struct PlayerInput
{
    private PlayerInput(string type, string? text = null, bool flag = false, double x = 0, double y = 0, PlayerPointerButton button = PlayerPointerButton.Primary)
    {
        Type = type;
        Text = text;
        Flag = flag;
        X = x;
        Y = y;
        Button = button;
    }

    /// <summary>The event's <c>type</c> tag (<c>keyDown</c>, <c>pointerMove</c>, …).</summary>
    public string Type { get; }

    /// <summary>The key of a key event, or the text of a text event.</summary>
    public string? Text { get; }

    /// <summary>Auto-repeat for <c>keyDown</c>.</summary>
    public bool Flag { get; }

    /// <summary>Pointer x, or the horizontal wheel delta.</summary>
    public double X { get; }

    /// <summary>Pointer y, or the vertical wheel delta.</summary>
    public double Y { get; }

    public PlayerPointerButton Button { get; }

    public static PlayerInput KeyDown(string key, bool repeat = false) => new("keyDown", key, repeat);

    public static PlayerInput KeyUp(string key) => new("keyUp", key);

    public static PlayerInput TextInput(string text) => new("text", text);

    public static PlayerInput PointerMove(double x, double y) => new("pointerMove", x: x, y: y);

    public static PlayerInput PointerDown(double x, double y, PlayerPointerButton button = PlayerPointerButton.Primary) => new("pointerDown", x: x, y: y, button: button);

    public static PlayerInput PointerUp(double x, double y, PlayerPointerButton button = PlayerPointerButton.Primary) => new("pointerUp", x: x, y: y, button: button);

    public static PlayerInput PointerLeft() => new("pointerLeft");

    /// <summary>Scroll in lines (+<paramref name="dy"/> scrolls content up, like a wheel turned away from the user).</summary>
    public static PlayerInput Wheel(double dx, double dy) => new("wheel", x: dx, y: dy);

    /// <summary>The surface lost focus: every held key is released.</summary>
    public static PlayerInput FocusLost() => new("focusLost");

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", Type);
        switch (Type)
        {
            case "keyDown":
                writer.WriteString("key", Text);
                writer.WriteBoolean("repeat", Flag);
                break;
            case "keyUp":
                writer.WriteString("key", Text);
                break;
            case "text":
                writer.WriteString("text", Text);
                break;
            case "pointerMove":
                writer.WriteNumber("x", Finite(X));
                writer.WriteNumber("y", Finite(Y));
                break;
            case "pointerDown" or "pointerUp":
                writer.WriteNumber("x", Finite(X));
                writer.WriteNumber("y", Finite(Y));
                writer.WriteString("button", Button switch
                {
                    PlayerPointerButton.Secondary => "secondary",
                    PlayerPointerButton.Middle => "middle",
                    _ => "primary",
                });
                break;
            case "wheel":
                writer.WriteNumber("dx", Finite(X));
                writer.WriteNumber("dy", Finite(Y));
                break;
        }

        writer.WriteEndObject();
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}

/// <summary>A sound cue the frame asked for, with its gain (0–1).</summary>
public sealed record PlayerSound(string Cue, double Gain);

/// <summary>What a frame reports besides its pixels.</summary>
/// <param name="Sounds">Sound cues to play.</param>
/// <param name="Requests"><c>quit</c>, <c>fullscreen:on</c>, <c>fullscreen:off</c>, <c>title:…</c>.</param>
/// <param name="Screen">The shell screen (<c>playing</c>, <c>pause</c>, <c>settings</c>, …).</param>
/// <param name="Modal">An in-game panel or an engine modal (dialogue, shop, minigame) is open.</param>
public sealed record PlayerFrameInfo(IReadOnlyList<PlayerSound> Sounds, IReadOnlyList<string> Requests, string Screen, bool Modal)
{
    public static readonly PlayerFrameInfo Empty = new([], [], "playing", false);
}

/// <summary>
/// A frame: <see cref="Pixels"/> holds <see cref="Width"/> × <see cref="Height"/> premultiplied
/// RGBA8 pixels, row by row (empty when the frame was only stepped).
/// </summary>
public sealed record PlayerFrame(int Width, int Height, byte[] Pixels, PlayerFrameInfo Info);

/// <summary>
/// A frame that stayed in Rust (<see cref="RustPlayer.Advance"/>): its size (0 × 0 when it was
/// only stepped) and info; <see cref="RustPlayer.CopyPixels"/> copies its pixels.
/// </summary>
public sealed record PlayerStep(int Width, int Height, PlayerFrameInfo Info);

/// <summary>A scene or season id with its display name.</summary>
public sealed record PlayerNamed(string Id, string Name);

/// <summary>The live game at a glance (the Play Mode debug drawer).</summary>
public sealed record PlayerSummary(
    double Tick,
    double Day,
    string Season,
    double Year,
    string TimeText,
    string SceneId,
    double X,
    double Y,
    double Money,
    string Seed,
    IReadOnlyList<PlayerNamed> Scenes,
    IReadOnlyList<PlayerNamed> Seasons);

/// <summary>A rectangle in frame pixels.</summary>
public sealed record PlayerRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;

    public double CenterY => Y + Height / 2;
}

/// <summary>A toast the game showed (<c>info</c>, <c>success</c>, <c>error</c>).</summary>
public sealed record PlayerToast(string Text, string Kind);

/// <summary>Options for a new <see cref="RustPlayer"/>.</summary>
/// <param name="Seed">Seed of the game (the project's own when null).</param>
/// <param name="ReducedMotion">No floating pops, fades or flashes.</param>
/// <param name="UiScale">Interface size (1 = 100 %; null keeps the default).</param>
/// <param name="Audio">Play the game's sounds on the default output device (silent without one).</param>
/// <param name="Locale">
/// The language the game's interface follows while the player's own setting is automatic (the
/// editor's, like <c>es</c>); null uses the game's locale, else English.
/// </param>
public sealed record RustPlayerOptions(string? Seed = null, bool ReducedMotion = false, double? UiScale = null, bool Audio = false, string? Locale = null);

/// <summary>
/// The Rust game player embedded in the editor (<c>farm_player::Player</c> through
/// <c>fe_player_*</c>): the whole game — world, HUD, dialogue, shops, crafting, inventory,
/// quests, minigames, toasts — driven one <see cref="Frame"/> at a time and drawn by the Rust
/// renderer. The editor keeps only its own tools (restart, keep changes, the debug drawer).
/// Calls are serialized, and disposing while a frame runs on another thread frees the player
/// once that frame returns; dispose it to free the Rust side (its finalizer does when nobody did).
/// </summary>
public sealed unsafe class RustPlayer : IDisposable
{
    private readonly NativeHandle<NativeMethods.PlayerHandle> _native;

    private RustPlayer(NativeMethods.PlayerHandle handle)
    {
        _native = new NativeHandle<NativeMethods.PlayerHandle>(handle, this, prefixMessages: false);
    }

    /// <summary>
    /// True once the game stopped (an engine or plugin failure, or a Rust panic): its state is not
    /// trustworthy and every later call throws.
    /// </summary>
    public bool IsPoisoned => _native.IsPoisoned;

    /// <summary>Why the game stopped (null while it runs).</summary>
    public string? Fault => _native.Fault;

    /// <summary>Starts a player for a (migrated) editor project.</summary>
    public static RustPlayer Create(GameProject project, RustPlayerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return CreateFromBytes(RecordJson.ToUtf8(project), options);
    }

    /// <summary>Starts a player for a compiled cartridge (no project to keep changes into).</summary>
    public static RustPlayer CreateCartridge(byte[] cartridge, RustPlayerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        return CreateFromBytes(cartridge, options);
    }

    private static RustPlayer CreateFromBytes(byte[] game, RustPlayerOptions? options)
    {
        FarmFfi.EnsureAvailable();
        options ??= new RustPlayerOptions();
        var settings = new JsonObject { ["reducedMotion"] = options.ReducedMotion, ["audio"] = options.Audio };
        if (!string.IsNullOrEmpty(options.Seed))
        {
            settings["seed"] = options.Seed;
        }

        if (options.UiScale is { } scale)
        {
            settings["uiScale"] = scale;
        }

        if (!string.IsNullOrEmpty(options.Locale))
        {
            settings["locale"] = options.Locale;
        }

        var optionsJson = Encoding.UTF8.GetBytes(settings.ToJsonString());
        fixed (byte* gamePtr = game)
        fixed (byte* optionsPtr = optionsJson)
        {
            NativeMethods.FeBytes error = default;
            var result = NativeMethods.fe_player_new(gamePtr, (nuint)game.Length, optionsPtr, (nuint)optionsJson.Length, out var handle, &error);
            var message = Encoding.UTF8.GetString(RustRender.TakeBytes(error));
            if (result != NativeMethods.FeResult.Ok)
            {
                handle.Dispose();
                throw new FarmFfiException(string.IsNullOrEmpty(message) ? $"fe_player_new failed: {result}." : message);
            }

            return new RustPlayer(handle);
        }
    }

    /// <summary>
    /// Runs one frame of <paramref name="deltaSeconds"/> (clamped to 0–0.25 s) with the input
    /// that arrived since the last one, at <paramref name="width"/> × <paramref name="height"/>
    /// pixels. With <paramref name="render"/> false the game only steps (no pixels). Pass the
    /// previous frame's pixel array as <paramref name="reuse"/> to avoid a new allocation when the
    /// size is unchanged. The pixels are copied once, from the player into the array; a host
    /// with its own bitmap uses <see cref="Advance"/> and <see cref="CopyPixels"/> instead.
    /// </summary>
    public PlayerFrame Frame(double deltaSeconds, IReadOnlyList<PlayerInput> events, int width, int height, bool render = true, byte[]? reuse = null)
    {
        return _native.Locked(() =>
        {
            var step = Advance(deltaSeconds, events, width, height, render);
            var length = step.Width * step.Height * 4;
            var pixels = reuse is not null && reuse.Length == length ? reuse : new byte[length];
            if (length > 0)
            {
                fixed (byte* destination = pixels)
                {
                    CopyPixels((nint)destination, length, step.Width * 4);
                }
            }

            return new PlayerFrame(step.Width, step.Height, pixels, step.Info);
        });
    }

    /// <summary>
    /// Runs one frame like <see cref="Frame"/>, but its pixels stay in the player until
    /// <see cref="CopyPixels"/> copies them (Play Mode copies them straight into its bitmap).
    /// </summary>
    public PlayerStep Advance(double deltaSeconds, IReadOnlyList<PlayerInput> events, int width, int height, bool render = true)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var request = new ArrayBufferWriter<byte>(128 + events.Count * 48);
        using (var writer = new Utf8JsonWriter(request))
        {
            writer.WriteStartObject();
            writer.WriteNumber("dt", double.IsFinite(deltaSeconds) ? deltaSeconds : 0);
            writer.WriteStartArray("events");
            foreach (var input in events)
            {
                input.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteNumber("width", width);
            writer.WriteNumber("height", height);
            writer.WriteBoolean("render", render);
            writer.WriteEndObject();
        }

        return _native.Read(nameof(Frame), request.WrittenSpan, NativeMethods.fe_player_frame_info, static bytes =>
        {
            var frameWidth = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]);
            var frameHeight = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
            var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..12]);
            var info = JsonSerializer.Deserialize<PlayerFrameInfo>(bytes.Slice(12, jsonLength), InteropJson.Options) ?? PlayerFrameInfo.Empty;
            return new PlayerStep(frameWidth, frameHeight, info);
        });
    }

    /// <summary>
    /// Copies the last rendered frame (the size <see cref="Advance"/> reported, premultiplied
    /// RGBA8) to <paramref name="destination"/>: one row of width × 4 bytes every
    /// <paramref name="stride"/> bytes, within <paramref name="length"/> bytes (a locked bitmap).
    /// Throws <see cref="FarmFfiException"/> when the frame does not fit, writing nothing.
    /// </summary>
    public void CopyPixels(nint destination, int length, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 0);
        if (destination == 0)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        _native.Call(nameof(CopyPixels), (handle, output) =>
            NativeMethods.fe_player_copy_pixels(handle, (byte*)destination, (nuint)length, (nuint)stride, output));
    }

    /// <summary>
    /// A creator debug action, bypassing the command log on purpose:
    /// <c>{"type":"addMoney","amount":500}</c>, <c>fullEnergy</c>, <c>addMinutes</c> (<c>minutes</c>),
    /// <c>setSeason</c> (<c>season</c>), <c>giveFirst</c> (<c>itemType</c>), <c>teleport</c>
    /// (<c>sceneId</c>), <c>setFlag</c> (<c>flag</c>), <c>skipDay</c>.
    /// </summary>
    public void Debug(object action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _native.Call(nameof(Debug), JsonSerializer.SerializeToUtf8Bytes(action, InteropJson.Options), NativeMethods.fe_player_debug);
    }

    /// <summary>Runs engine commands (a JSON array of <c>{"type":…}</c> objects) as if the player had done them.</summary>
    public void RunCommands(string commandsJson)
    {
        ArgumentNullException.ThrowIfNull(commandsJson);
        _native.Call(nameof(RunCommands), Encoding.UTF8.GetBytes(commandsJson), NativeMethods.fe_player_commands);
    }

    /// <summary>Runs engine commands given as objects serialized with the schema's JSON options.</summary>
    public void RunCommands(params object[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        RunCommands(JsonSerializer.Serialize(commands, InteropJson.Options));
    }

    /// <summary>The live game state as stable JSON.</summary>
    public string StateJson() => _native.CallText(nameof(StateJson), NativeMethods.fe_player_state_json);

    /// <summary>The live game state as a JSON node (tests, tools).</summary>
    public JsonObject State() => JsonNode.Parse(StateJson())!.AsObject();

    /// <summary>The state hash (<c>hashState</c>).</summary>
    public string StateHash() => _native.CallText(nameof(StateHash), NativeMethods.fe_player_hash);

    /// <summary>
    /// The editor project with the live state written back ("keep changes",
    /// <c>applyStateToProject</c>). Throws for a player started from a cartridge.
    /// </summary>
    public GameProject SyncedProject() =>
        RecordJson.ParseUtf8<GameProject>(_native.Call(nameof(SyncedProject), NativeMethods.fe_player_synced_project));

    /// <summary>Clock, place, seed, scenes and seasons of the live game.</summary>
    public PlayerSummary Summary() => Query<PlayerSummary>(new { type = "summary" })!;

    /// <summary>
    /// Where a UI widget was drawn last frame, by its id path (<c>"pause", "Save"</c>; numbers are
    /// slot or list indices), in frame pixels; null when it was not drawn.
    /// </summary>
    public PlayerRect? WidgetRect(params object[] path) => Query<PlayerRect?>(new { type = "widgetRect", path });

    /// <summary>The toasts shown so far, oldest first.</summary>
    public IReadOnlyList<PlayerToast> Toasts() => Query<List<PlayerToast>>(new { type = "toasts" }) ?? [];

    /// <summary>Recent plugin errors, oldest first.</summary>
    public IReadOnlyList<string> PluginErrors() => Query<List<string>>(new { type = "pluginErrors" }) ?? [];

    /// <summary>Makes the Rust side panic inside a call (tests of the poisoning path): throws, and the player is poisoned.</summary>
    internal void PanicForTests() => Query<object>(new { type = "panic" });

    private T? Query<T>(object query)
    {
        var answer = _native.Call(nameof(Query), JsonSerializer.SerializeToUtf8Bytes(query, InteropJson.Options), NativeMethods.fe_player_query_json);
        return JsonSerializer.Deserialize<T>(answer, InteropJson.Options);
    }

    public void Dispose() => _native.Dispose();
}
