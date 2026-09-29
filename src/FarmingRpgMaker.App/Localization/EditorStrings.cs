using System.Globalization;

namespace FarmingRpgMaker.App.Localization;

/// <summary>A language of the editor's chrome: its code and its name in itself.</summary>
public sealed record EditorLanguage(string Code, string NativeName);

/// <summary>
/// String tables of the editor's own chrome (the web version's <c>src/lib/i18n.ts</c>): mode
/// names, the Play Mode toolbar, the Help menu, the welcome tour and the shortcuts list. The
/// game's interface is localized by the Rust player (farm-ui); editor forms stay English.
/// <para>
/// <see cref="Get(string)"/> looks a key up in the current <see cref="Language"/>, falls back to
/// English, then to the key itself. The language is an app setting
/// (<see cref="Projects.WorkspaceSettings.EditorLanguage"/>, Help → Language); without one the
/// app starts in the system's language when there is a table for it.
/// </para>
/// </summary>
public static class EditorStrings
{
    public const string English = "en";

    public static readonly IReadOnlyList<EditorLanguage> Languages =
    [
        new(English, "English"),
        new("es", "Español"),
    ];

    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // Web i18n.ts `mode.*` and `toolbar.*`.
        ["mode.play"] = "Play Mode",
        ["mode.edit"] = "Edit Mode",
        ["mode.projects"] = "Projects",
        ["toolbar.restart"] = "Restart",
        ["toolbar.keepChanges"] = "Keep changes",
        ["toolbar.debug"] = "Debug",
        ["toolbar.restartTip"] = "Restore the pre-playtest snapshot and start over",
        ["toolbar.keepChangesTip"] = "Keep playtest changes when exiting to the editor",
        ["toolbar.debugTip"] = "Playtest debug drawer",
        ["toolbar.playHint"] = "Click the game to play. Esc pauses; F6 returns to the editor.",
        ["toolbar.keepOn"] = "Playtest changes will be kept when you exit",
        ["toolbar.keepOff"] = "Playtest changes will be discarded when you exit",
        // Header.
        ["header.playing"] = "Playing: {0}",
        ["header.editing"] = "Editing: {0}",
        // Menus (mnemonics with "_").
        ["menu.playMode"] = "_Play Mode",
        ["menu.editMode"] = "_Edit Mode",
        ["menu.help"] = "_Help",
        ["menu.welcome"] = "_Welcome Tour",
        ["menu.creatorGuide"] = "_Creator Guide",
        ["menu.shortcuts"] = "_Keyboard Shortcuts",
        ["menu.language"] = "_Language",
        ["menu.updateCenter"] = "_Update Center…",
        ["menu.about"] = "_About Farming RPG Maker",
        // Welcome tour (web WelcomeDialog, adapted to the desktop editor).
        ["welcome.title"] = "Welcome to Farming RPG Maker",
        ["welcome.subtitle"] = "Build and share a farming adventure — no code required.",
        ["welcome.play.title"] = "Play the starter farm",
        ["welcome.play.body"] = "Press F5 (or Play Mode) to playtest. Move with WASD or the arrow keys, use tools with T (hoe) and Q (water), interact with E and sleep with Z. Esc opens the game's menu; F6 returns to the editor.",
        ["welcome.edit.title"] = "Build in Edit Mode",
        ["welcome.edit.body"] = "Paint terrain with the map tools (Brush, Rectangle, Fill area), place NPCs, nodes and items, and add doors between scenes. Undo anything with Ctrl+Z.",
        ["welcome.content.title"] = "Make it yours",
        ["welcome.content.body"] = "Add crops, NPCs with dialogue, quests, shops and events in the Content tab, start from a Workshop pattern, and import art in the Art tab. Changes made while playing are discarded when you return, unless you turn on Keep changes.",
        ["welcome.share.title"] = "Share your game",
        ["welcome.share.body"] = "File → Export Game (Ctrl+Shift+E) builds standalone games for Windows and Linux (Steam Deck too) that anyone can play without the editor.",
        ["welcome.guide"] = "Full creator guide",
        ["welcome.start"] = "Let's farm!",
        // Help windows.
        ["guide.title"] = "Creator Guide",
        ["shortcuts.title"] = "Keyboard Shortcuts",
        ["shortcuts.editor"] = "EDITOR",
        ["shortcuts.game"] = "IN THE GAME (DEFAULT KEYS)",
        ["shortcuts.newProject"] = "New project",
        ["shortcuts.openProject"] = "Open project",
        ["shortcuts.exportGame"] = "Export Game",
        ["shortcuts.playMode"] = "Play Mode (playtest)",
        ["shortcuts.editMode"] = "Back to Edit Mode",
        ["shortcuts.undo"] = "Undo (Edit Mode)",
        ["shortcuts.redo"] = "Redo (Edit Mode)",
        ["shortcuts.mapCursor"] = "Move the map's editing cursor (map focused)",
        ["shortcuts.mapApply"] = "Use the map tool at the cursor",
        ["shortcuts.mapCancel"] = "Cancel a rectangle's first corner",
        ["shortcuts.exit"] = "Quit the editor",
        ["shortcuts.guide"] = "Creator guide",
        ["shortcuts.move"] = "Move",
        ["shortcuts.interact"] = "Interact, talk, confirm",
        ["shortcuts.tools"] = "Watering can · hoe · axe · pickaxe · scythe",
        ["shortcuts.craft"] = "Crafting",
        ["shortcuts.sleep"] = "Sleep",
        ["shortcuts.inventory"] = "Inventory",
        ["shortcuts.quests"] = "Quests",
        ["shortcuts.menu"] = "Close a panel; pause menu",
        ["shortcuts.options"] = "Pick a dialogue option",
        ["shortcuts.rebind"] = "Players can rebind the game's keys in Settings → Controls.",
        ["common.close"] = "Close",
    };

    private static readonly Dictionary<string, string> Es = new(StringComparer.Ordinal)
    {
        ["mode.play"] = "Modo Juego",
        ["mode.edit"] = "Modo Editor",
        ["mode.projects"] = "Proyectos",
        ["toolbar.restart"] = "Reiniciar",
        ["toolbar.keepChanges"] = "Conservar cambios",
        ["toolbar.debug"] = "Depurar",
        ["toolbar.restartTip"] = "Restaura el estado previo a la prueba y empieza de nuevo",
        ["toolbar.keepChangesTip"] = "Conserva los cambios de la prueba al volver al editor",
        ["toolbar.debugTip"] = "Panel de depuración de la prueba",
        ["toolbar.playHint"] = "Haz clic en el juego para jugar. Esc pausa; F6 vuelve al editor.",
        ["toolbar.keepOn"] = "Los cambios de la prueba se conservarán al salir",
        ["toolbar.keepOff"] = "Los cambios de la prueba se descartarán al salir",
        ["header.playing"] = "Jugando: {0}",
        ["header.editing"] = "Editando: {0}",
        ["menu.playMode"] = "Modo _Juego",
        ["menu.editMode"] = "Modo _Editor",
        ["menu.help"] = "Ay_uda",
        ["menu.welcome"] = "_Visita de bienvenida",
        ["menu.creatorGuide"] = "_Guía del creador",
        ["menu.shortcuts"] = "_Atajos de teclado",
        ["menu.language"] = "_Idioma",
        ["menu.updateCenter"] = "_Centro de actualizaciones…",
        ["menu.about"] = "Acerca _de Farming RPG Maker",
        ["welcome.title"] = "Te damos la bienvenida a Farming RPG Maker",
        ["welcome.subtitle"] = "Crea y comparte una aventura en la granja, sin escribir código.",
        ["welcome.play.title"] = "Juega la granja inicial",
        ["welcome.play.body"] = "Pulsa F5 (o Modo Juego) para probar el juego. Muévete con WASD o las flechas, usa las herramientas con T (azada) y Q (regadera), interactúa con E y duerme con Z. Esc abre el menú del juego; F6 vuelve al editor.",
        ["welcome.edit.title"] = "Construye en el Modo Editor",
        ["welcome.edit.body"] = "Pinta el terreno con las herramientas del mapa (pincel, rectángulo, relleno), coloca personajes, recursos y objetos, y añade puertas entre escenas. Deshaz cualquier cambio con Ctrl+Z.",
        ["welcome.content.title"] = "Hazlo tuyo",
        ["welcome.content.body"] = "Añade cultivos, personajes con diálogos, misiones, tiendas y eventos en la pestaña Content, parte de un patrón del Workshop e importa arte en la pestaña Art. Los cambios hechos al jugar se descartan al volver, salvo que actives Conservar cambios.",
        ["welcome.share.title"] = "Comparte tu juego",
        ["welcome.share.body"] = "File → Export Game (Ctrl+Shift+E) crea juegos independientes para Windows y Linux (también Steam Deck) que cualquiera puede jugar sin el editor.",
        ["welcome.guide"] = "Guía completa del creador",
        ["welcome.start"] = "¡A cultivar!",
        ["guide.title"] = "Guía del creador",
        ["shortcuts.title"] = "Atajos de teclado",
        ["shortcuts.editor"] = "EDITOR",
        ["shortcuts.game"] = "EN EL JUEGO (TECLAS PREDETERMINADAS)",
        ["shortcuts.newProject"] = "Proyecto nuevo",
        ["shortcuts.openProject"] = "Abrir un proyecto",
        ["shortcuts.exportGame"] = "Exportar el juego",
        ["shortcuts.playMode"] = "Modo Juego (probar)",
        ["shortcuts.editMode"] = "Volver al Modo Editor",
        ["shortcuts.undo"] = "Deshacer (Modo Editor)",
        ["shortcuts.redo"] = "Rehacer (Modo Editor)",
        ["shortcuts.mapCursor"] = "Mover el cursor de edición del mapa (con el mapa enfocado)",
        ["shortcuts.mapApply"] = "Usar la herramienta del mapa en el cursor",
        ["shortcuts.mapCancel"] = "Cancelar la primera esquina de un rectángulo",
        ["shortcuts.exit"] = "Salir del editor",
        ["shortcuts.guide"] = "Guía del creador",
        ["shortcuts.move"] = "Moverse",
        ["shortcuts.interact"] = "Interactuar, hablar, confirmar",
        ["shortcuts.tools"] = "Regadera · azada · hacha · pico · guadaña",
        ["shortcuts.craft"] = "Fabricación",
        ["shortcuts.sleep"] = "Dormir",
        ["shortcuts.inventory"] = "Inventario",
        ["shortcuts.quests"] = "Misiones",
        ["shortcuts.menu"] = "Cerrar un panel; menú de pausa",
        ["shortcuts.options"] = "Elegir una opción de diálogo",
        ["shortcuts.rebind"] = "Los jugadores pueden reasignar las teclas del juego en Ajustes → Controles.",
        ["common.close"] = "Cerrar",
    };

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new(StringComparer.Ordinal)
    {
        [English] = En,
        ["es"] = Es,
    };

    /// <summary>The current language's code.</summary>
    public static string Language { get; private set; } = English;

    /// <summary>Raised after <see cref="Language"/> changes.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Switches the language (<paramref name="code"/> may be any tag, like <c>es-MX</c>; one without
    /// a table means English).
    /// </summary>
    public static void SetLanguage(string? code)
    {
        var language = FromTag(code) ?? English;
        if (language == Language)
        {
            return;
        }

        Language = language;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The language with a table for a locale tag (<c>es</c>, <c>es-MX</c>, <c>es_ES.UTF-8</c>).</summary>
    public static string? FromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var language = new string(tag.Trim().TakeWhile(char.IsAsciiLetter).ToArray()).ToLowerInvariant();
        return Tables.ContainsKey(language) ? language : null;
    }

    /// <summary>The creator's choice, else the system's language, else English.</summary>
    public static string Resolve(string? preference, string? system) => FromTag(preference) ?? FromTag(system) ?? English;

    /// <summary>The app's language at startup: the saved choice, else the system's language.</summary>
    public static string ResolveForSystem(string? preference) => Resolve(preference, SystemLocale());

    /// <summary>
    /// The system's locale tag. The app runs with invariant globalization, so this reads the
    /// user's locale setting directly: <c>LC_ALL</c>, <c>LC_MESSAGES</c>, <c>LANG</c> on Linux and
    /// macOS, the user's locale name in the registry on Windows.
    /// </summary>
    public static string? SystemLocale()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
                return key?.GetValue("LocaleName") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        return new[] { "LC_ALL", "LC_MESSAGES", "LANG" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>The text of <paramref name="key"/> in the current language.</summary>
    public static string Get(string key) => Get(Language, key);

    /// <summary>The text of <paramref name="key"/> in <paramref name="language"/>, else English, else the key.</summary>
    public static string Get(string language, string key)
    {
        if (Tables.TryGetValue(FromTag(language) ?? English, out var table) && table.TryGetValue(key, out var text))
        {
            return text;
        }

        return En.TryGetValue(key, out var english) ? english : key;
    }

    /// <summary>A template with <c>{0}</c>… filled in.</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>The keys of a language's table (tests).</summary>
    internal static IReadOnlyCollection<string> Keys(string language) =>
        Tables.TryGetValue(language, out var table) ? table.Keys : [];
}
