//! String tables of the engine-owned interface: HUD labels, toolbar, panels, the game shell and
//! its dialogs (the web version's `src/lib/i18n.ts`). Game text (item names, dialogue) is not
//! here; content packs localize it with their own string tables (`farm_sim::packs`).
//!
//! A [`Lang`] looks a key up in its table; a key it lacks falls back to English, then to the key
//! itself. Templates take positional arguments (`{0}`, `{1}`), so a translation can reorder them.
//!
//! The player's language is a setting ([`crate::Settings::language`]); empty means automatic:
//! the system's language when the host knows it, else the game's (`settings.locale` of the
//! project), else English ([`Lang::resolve`]).

use std::collections::BTreeMap;
use std::fmt::Display;
use std::sync::OnceLock;

/// A language of the interface.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub enum Lang {
    #[default]
    En,
    Es,
}

impl Lang {
    pub const ALL: [Lang; 2] = [Lang::En, Lang::Es];

    /// The language's code (`en`, `es`), as stored in the settings.
    pub fn code(self) -> &'static str {
        match self {
            Lang::En => "en",
            Lang::Es => "es",
        }
    }

    /// The language's name in itself (what a language picker shows).
    pub fn native_name(self) -> &'static str {
        match self {
            Lang::En => "English",
            Lang::Es => "Espa\u{f1}ol",
        }
    }

    /// The language of a locale tag: `es`, `es-MX`, `es_ES.UTF-8`, `ES`. `None` for a language
    /// without a table (and for `C`/`POSIX`).
    pub fn from_tag(tag: &str) -> Option<Lang> {
        let language: String =
            tag.trim().chars().take_while(|ch| ch.is_ascii_alphabetic()).collect::<String>().to_ascii_lowercase();
        Lang::ALL.into_iter().find(|lang| lang.code() == language)
    }

    /// The language to show: the player's choice (`preference`, empty for automatic), else the
    /// system's locale, else the game's own locale, else English. Tags without a table are
    /// skipped.
    pub fn resolve(preference: &str, system: Option<&str>, game: &str) -> Lang {
        [Some(preference), system, Some(game)].into_iter().flatten().find_map(Lang::from_tag).unwrap_or_default()
    }

    fn table(self) -> &'static BTreeMap<&'static str, &'static str> {
        static EN_MAP: OnceLock<BTreeMap<&'static str, &'static str>> = OnceLock::new();
        static ES_MAP: OnceLock<BTreeMap<&'static str, &'static str>> = OnceLock::new();
        let (cell, entries) = match self {
            Lang::En => (&EN_MAP, EN),
            Lang::Es => (&ES_MAP, ES),
        };
        cell.get_or_init(|| entries.iter().copied().collect())
    }

    /// The text of `key` in this language, if this language or English has it.
    pub fn get(self, key: &str) -> Option<&'static str> {
        self.table().get(key).or_else(|| Lang::En.table().get(key)).copied()
    }

    /// The text of `key`: this language's, else English, else the key itself.
    pub fn tr(self, key: &'static str) -> &'static str {
        self.get(key).unwrap_or(key)
    }

    /// A template with `{0}`, `{1}`… replaced by `args`.
    pub fn format(self, key: &'static str, args: &[&dyn Display]) -> String {
        fill(self.tr(key), args)
    }
}

/// Replaces `{0}`, `{1}`… in `template` with `args` (a placeholder without an argument stays).
pub fn fill(template: &str, args: &[&dyn Display]) -> String {
    let mut out = String::with_capacity(template.len() + 16);
    let mut rest = template;
    while let Some(start) = rest.find('{') {
        out.push_str(&rest[..start]);
        let after = &rest[start + 1..];
        let index = after.find('}').and_then(|end| after[..end].parse::<usize>().ok().map(|index| (index, end)));
        match index.and_then(|(index, end)| args.get(index).map(|arg| (arg, end))) {
            Some((arg, end)) => {
                out.push_str(&arg.to_string());
                rest = &after[end + 1..];
            }
            None => {
                out.push('{');
                rest = after;
            }
        }
    }
    out.push_str(rest);
    out
}

/// English: the reference table. Every key of every other table must be here.
const EN: &[(&str, &str)] = &[
    // HUD (web `hud.*`).
    ("hud.money", "Money:"),
    ("hud.season", "Season:"),
    ("hud.day", "Day:"),
    ("hud.year", "Year:"),
    ("hud.weather", "Weather:"),
    ("hud.time", "Time:"),
    ("hud.energy", "Energy:"),
    ("hud.sunny", "Sunny"),
    ("hud.madeWith", "Made with Farming RPG Maker"),
    // Toolbar (web `toolbar.*`).
    ("toolbar.inventory", "Inventory {0}/{1}"),
    ("toolbar.quests", "Quests"),
    ("toolbar.craft", "Craft"),
    ("toolbar.sleep", "Sleep"),
    ("toolbar.menu", "Menu"),
    ("toolbar.withKey", "{0} ({1})"),
    // Rebindable actions: settings labels and the hint row.
    ("bind.moveUp", "Move up"),
    ("bind.moveDown", "Move down"),
    ("bind.moveLeft", "Move left"),
    ("bind.moveRight", "Move right"),
    ("bind.interact", "Interact"),
    ("bind.water", "Watering can"),
    ("bind.till", "Hoe"),
    ("bind.axe", "Axe"),
    ("bind.pickaxe", "Pickaxe"),
    ("bind.scythe", "Scythe"),
    ("bind.sleep", "Sleep"),
    ("bind.inventory", "Inventory"),
    ("bind.quests", "Quests"),
    ("bind.craft", "Craft"),
    ("bind.menu", "Menu / close"),
    ("hint.move", "Move"),
    ("hint.interact", "Interact"),
    ("hint.water", "Water"),
    ("hint.till", "Till"),
    ("hint.axe", "Axe"),
    ("hint.pickaxe", "Pickaxe"),
    ("hint.scythe", "Scythe"),
    ("hint.sleep", "Sleep"),
    ("hint.inventory", "Inventory"),
    ("hint.quests", "Quests"),
    ("hint.craft", "Craft"),
    ("hint.menu", "Menu"),
    // Keycaps.
    ("key.space", "Space"),
    ("key.enter", "Enter"),
    ("key.backspace", "Backspace"),
    ("key.delete", "Delete"),
    ("key.shift", "Shift"),
    ("key.home", "Home"),
    ("key.end", "End"),
    ("key.pageUp", "PgUp"),
    ("key.pageDown", "PgDn"),
    // Seasons (when the calendar has no names of its own).
    ("season.spring", "Spring"),
    ("season.summer", "Summer"),
    ("season.fall", "Fall"),
    ("season.autumn", "Autumn"),
    ("season.winter", "Winter"),
    // Shared buttons.
    ("common.back", "Back"),
    ("common.cancel", "Cancel"),
    ("common.close", "Close"),
    // Dialogue.
    ("dialogue.goodbye", "Goodbye"),
    // Inventory.
    ("inventory.title", "Inventory"),
    ("inventory.slotsUsed", "{0} / {1} slots used"),
    ("inventory.empty", "Your inventory is empty"),
    ("inventory.emptyDetail", "Explore the world to find items!"),
    ("inventory.use", "Use"),
    ("inventory.gift", "Gift"),
    ("inventory.totalValue", "Total value: {0}"),
    // Quests.
    ("quests.title", "Quest Log"),
    ("quests.subtitle", "{0} active \u{2022} {1} completed"),
    ("quests.empty", "No quests yet"),
    ("quests.emptyDetail", "Talk to NPCs to discover new quests!"),
    ("quests.active", "Active quests"),
    ("quests.completed", "Completed quests"),
    ("quests.rewards", "Rewards: {0}"),
    ("quests.rewardsClaimed", "Rewards claimed: {0}"),
    // Crafting.
    ("crafting.title", "Crafting"),
    ("crafting.facing", "Facing: {0}"),
    ("crafting.subtitle", "Hand crafting & machine placement"),
    ("crafting.loadInto", "Load into {0}"),
    ("crafting.machine", "machine"),
    ("crafting.working", "Working\u{2026} come back later."),
    ("crafting.noMachineRecipes", "No recipes for this machine."),
    ("crafting.load", "Load"),
    ("crafting.minutes", "{0} \u{00b7} {1} min"),
    ("crafting.hand", "Hand crafting"),
    ("crafting.noHandRecipes", "No hand recipes known."),
    ("crafting.craft", "Craft"),
    ("crafting.placeMachine", "Place machine (on the tile you face)"),
    ("crafting.place", "Place"),
    // Shop.
    ("shop.buy", "Buy"),
    ("shop.sell", "Sell"),
    ("shop.repair", "Repair"),
    ("shop.yourMoney", "Your money: {0}"),
    ("shop.nothingToSell", "Nothing to sell."),
    ("shop.nothingToSellDetail", "Harvest crops or gather resources first."),
    ("shop.sellOne", "Sell 1"),
    ("shop.sellAll", "Sell all"),
    ("shop.each", "{0} each"),
    ("shop.toolsFine", "All your tools are in good shape."),
    ("shop.toolsFineDetail", "Come back when something breaks."),
    ("shop.repairFor", "Repair {0}"),
    ("shop.durability", "{0}/{1} durability"),
    ("shop.broken", "BROKEN"),
    ("shop.noStock", "Nothing in stock this season."),
    ("shop.noStockDetail", "Check back when the season turns."),
    ("shop.leftToday", "{0}/{1} left today"),
    // Minigames.
    ("minigame.keys", "Space / Enter"),
    ("minigame.giveUp", "Give up"),
    // Title screen.
    ("title.newGame", "New Game"),
    ("title.continue", "Continue"),
    ("title.load", "Load"),
    ("title.settings", "Settings"),
    ("title.credits", "Credits"),
    ("title.quit", "Quit"),
    ("title.continueDetail", "Continue: {0}"),
    ("title.version", "Version {0}"),
    ("title.versionBy", "Version {0} \u{00b7} by {1}"),
    ("title.dateInSlot", "{0} \u{00b7} slot {1}"),
    // Pause menu.
    ("pause.title", "Paused"),
    ("pause.resume", "Resume"),
    ("pause.save", "Save"),
    ("pause.load", "Load"),
    ("pause.settings", "Settings"),
    ("pause.quitToTitle", "Quit to title"),
    ("pause.quitGame", "Quit game"),
    // Credits.
    ("credits.title", "Credits"),
    ("credits.version", "Version {0}"),
    ("credits.by", "by {0}"),
    ("credits.licenses", "Third-party licenses are in licenses/THIRD-PARTY.txt next to the game."),
    // Save slots.
    ("slots.loadTitle", "Load game"),
    ("slots.saveTitle", "Save game"),
    ("slots.newTitle", "New game"),
    ("slots.loadSubtitle", "Choose a save to continue."),
    ("slots.saveSubtitle", "Choose a slot. The game also saves each morning."),
    ("slots.newSubtitle", "Choose a slot for the new farm."),
    ("slots.load", "Load"),
    ("slots.save", "Save"),
    ("slots.start", "Start"),
    ("slots.delete", "Delete"),
    ("slots.slot", "Slot {0}"),
    ("slots.slotFarm", "Slot {0} \u{00b7} {1}"),
    ("slots.played", "{0} \u{00b7} {1} played"),
    ("slots.unreadable", "This save could not be read."),
    ("slots.empty", "Empty slot"),
    ("slots.date", "Day {0} of {1}, Year {2}"),
    ("time.justNow", "just now"),
    ("time.minutesAgo", "{0} min ago"),
    ("time.hoursAgo", "{0} h ago"),
    ("time.yesterday", "yesterday"),
    ("time.daysAgo", "{0} days ago"),
    // Settings.
    ("settings.title", "Settings"),
    ("settings.subtitle", "Saved automatically"),
    ("settings.tabDisplay", "Display"),
    ("settings.tabAudio", "Audio"),
    ("settings.tabControls", "Controls"),
    ("settings.tabAccessibility", "Accessibility"),
    ("settings.fullscreen", "Fullscreen"),
    ("settings.integerScaling", "Integer scaling (sharpest pixels)"),
    ("settings.interfaceSize", "Interface size"),
    ("settings.fullscreenHint", "Fullscreen also toggles with F11 or Alt+Enter."),
    ("settings.masterVolume", "Master volume"),
    ("settings.music", "Music"),
    ("settings.effects", "Sound effects"),
    ("settings.mute", "Mute"),
    ("settings.keyboard", "Keyboard"),
    ("settings.gamepad", "Gamepad"),
    ("settings.pressKey", "Press a key\u{2026} (Esc or B cancels)"),
    ("settings.notBound", "Not bound"),
    ("settings.resetControls", "Reset to defaults"),
    ("settings.padMove", "Move (or the left stick)"),
    ("settings.padInteract", "Interact, confirm"),
    ("settings.padBack", "Close, back"),
    ("settings.padPreviousTab", "Previous tab in menus"),
    ("settings.padNextTab", "Next tab in menus"),
    ("settings.language", "Language"),
    ("settings.languageAuto", "Automatic"),
    ("settings.textSize", "Text size"),
    ("settings.textSmall", "Small"),
    ("settings.textDefault", "Default"),
    ("settings.textLarge", "Large"),
    ("settings.textLargest", "Largest"),
    ("settings.readableFont", "Readable font (Atkinson Hyperlegible)"),
    ("settings.reducedMotion", "Reduced motion (no pops, fades or flashes)"),
    ("settings.tabsHint", "LB / RB or Tab switch tabs"),
    // Confirmations and messages of the player.
    ("confirm.lostProgress", "Progress since your last save will be lost. The game saves each morning."),
    ("confirm.quitToTitleTitle", "Quit to title?"),
    ("confirm.quitToTitle", "Quit to title"),
    ("confirm.quitGameTitle", "Quit the game?"),
    ("confirm.quitGame", "Quit"),
    ("confirm.overwriteTitle", "Overwrite save?"),
    ("confirm.overwriteMessage", "Slot {0} already holds a save. Replace it?"),
    ("confirm.overwrite", "Overwrite"),
    ("confirm.deleteTitle", "Delete save?"),
    ("confirm.deleteMessage", "The save in slot {0} will be gone for good."),
    ("confirm.delete", "Delete"),
    ("confirm.newGameTitle", "Start over in this slot?"),
    ("confirm.newGameMessage", "Slot {0} already holds a save. A new game here replaces it at its first save."),
    ("confirm.newGame", "Start new game"),
    ("toast.settingsNotSaved", "Settings were not saved: {0}"),
    ("toast.pluginError", "Plugin error: {0}"),
    ("toast.slotEmpty", "Slot {0} is empty."),
    ("toast.loadFailed", "This save could not be loaded."),
    ("toast.loaded", "Loaded slot {0}"),
    ("toast.loadedBackup", "Slot {0} was damaged, so its previous save was loaded."),
    (
        "toast.missingGlyph",
        "The game's fonts can't show \"{0}\" ({1}) or other characters of its script: they appear as boxes.",
    ),
    ("toast.saved", "Saved (slot {0})"),
    ("toast.autosaved", "Autosaved (slot {0})"),
    ("toast.saveFailed", "Could not save: {0}"),
];

/// Spanish (neutral, "tú").
const ES: &[(&str, &str)] = &[
    ("hud.money", "Dinero:"),
    ("hud.season", "Estaci\u{f3}n:"),
    ("hud.day", "D\u{ed}a:"),
    ("hud.year", "A\u{f1}o:"),
    ("hud.weather", "Clima:"),
    ("hud.time", "Hora:"),
    ("hud.energy", "Energ\u{ed}a:"),
    ("hud.sunny", "Soleado"),
    ("hud.madeWith", "Hecho con Farming RPG Maker"),
    ("toolbar.inventory", "Inventario {0}/{1}"),
    ("toolbar.quests", "Misiones"),
    ("toolbar.craft", "Fabricar"),
    ("toolbar.sleep", "Dormir"),
    ("toolbar.menu", "Men\u{fa}"),
    ("toolbar.withKey", "{0} ({1})"),
    ("bind.moveUp", "Mover arriba"),
    ("bind.moveDown", "Mover abajo"),
    ("bind.moveLeft", "Mover a la izquierda"),
    ("bind.moveRight", "Mover a la derecha"),
    ("bind.interact", "Interactuar"),
    ("bind.water", "Regadera"),
    ("bind.till", "Azada"),
    ("bind.axe", "Hacha"),
    ("bind.pickaxe", "Pico"),
    ("bind.scythe", "Guada\u{f1}a"),
    ("bind.sleep", "Dormir"),
    ("bind.inventory", "Inventario"),
    ("bind.quests", "Misiones"),
    ("bind.craft", "Fabricar"),
    ("bind.menu", "Men\u{fa} / cerrar"),
    ("hint.move", "Moverse"),
    ("hint.interact", "Interactuar"),
    ("hint.water", "Regar"),
    ("hint.till", "Arar"),
    ("hint.axe", "Hacha"),
    ("hint.pickaxe", "Pico"),
    ("hint.scythe", "Guada\u{f1}a"),
    ("hint.sleep", "Dormir"),
    ("hint.inventory", "Inventario"),
    ("hint.quests", "Misiones"),
    ("hint.craft", "Fabricar"),
    ("hint.menu", "Men\u{fa}"),
    ("key.space", "Espacio"),
    ("key.enter", "Intro"),
    ("key.backspace", "Retroceso"),
    ("key.delete", "Supr"),
    ("key.shift", "May\u{fa}s"),
    ("key.home", "Inicio"),
    ("key.end", "Fin"),
    ("key.pageUp", "ReP\u{e1}g"),
    ("key.pageDown", "AvP\u{e1}g"),
    ("season.spring", "Primavera"),
    ("season.summer", "Verano"),
    ("season.fall", "Oto\u{f1}o"),
    ("season.autumn", "Oto\u{f1}o"),
    ("season.winter", "Invierno"),
    ("common.back", "Volver"),
    ("common.cancel", "Cancelar"),
    ("common.close", "Cerrar"),
    ("dialogue.goodbye", "Adi\u{f3}s"),
    ("inventory.title", "Inventario"),
    ("inventory.slotsUsed", "{0} / {1} espacios ocupados"),
    ("inventory.empty", "Tu inventario est\u{e1} vac\u{ed}o"),
    ("inventory.emptyDetail", "\u{a1}Explora el mundo para encontrar objetos!"),
    ("inventory.use", "Usar"),
    ("inventory.gift", "Regalar"),
    ("inventory.totalValue", "Valor total: {0}"),
    ("quests.title", "Diario de misiones"),
    ("quests.subtitle", "{0} activas \u{2022} {1} completadas"),
    ("quests.empty", "A\u{fa}n no tienes misiones"),
    ("quests.emptyDetail", "\u{a1}Habla con los personajes para descubrir nuevas misiones!"),
    ("quests.active", "Misiones activas"),
    ("quests.completed", "Misiones completadas"),
    ("quests.rewards", "Recompensas: {0}"),
    ("quests.rewardsClaimed", "Recompensas recibidas: {0}"),
    ("crafting.title", "Fabricaci\u{f3}n"),
    ("crafting.facing", "Frente a: {0}"),
    ("crafting.subtitle", "Fabricaci\u{f3}n a mano y colocaci\u{f3}n de m\u{e1}quinas"),
    ("crafting.loadInto", "Cargar en {0}"),
    ("crafting.machine", "la m\u{e1}quina"),
    ("crafting.working", "Trabajando\u{2026} vuelve m\u{e1}s tarde."),
    ("crafting.noMachineRecipes", "No hay recetas para esta m\u{e1}quina."),
    ("crafting.load", "Cargar"),
    ("crafting.minutes", "{0} \u{00b7} {1} min"),
    ("crafting.hand", "Fabricaci\u{f3}n a mano"),
    ("crafting.noHandRecipes", "No conoces recetas para fabricar a mano."),
    ("crafting.craft", "Fabricar"),
    ("crafting.placeMachine", "Colocar m\u{e1}quina (en la casilla de enfrente)"),
    ("crafting.place", "Colocar"),
    ("shop.buy", "Comprar"),
    ("shop.sell", "Vender"),
    ("shop.repair", "Reparar"),
    ("shop.yourMoney", "Tu dinero: {0}"),
    ("shop.nothingToSell", "No tienes nada que vender."),
    ("shop.nothingToSellDetail", "Primero cosecha cultivos o recoge recursos."),
    ("shop.sellOne", "Vender 1"),
    ("shop.sellAll", "Vender todo"),
    ("shop.each", "{0} cada uno"),
    ("shop.toolsFine", "Todas tus herramientas est\u{e1}n en buen estado."),
    ("shop.toolsFineDetail", "Vuelve cuando algo se rompa."),
    ("shop.repairFor", "Reparar {0}"),
    ("shop.durability", "Durabilidad {0}/{1}"),
    ("shop.broken", "ROTA"),
    ("shop.noStock", "No hay existencias esta estaci\u{f3}n."),
    ("shop.noStockDetail", "Vuelve cuando cambie la estaci\u{f3}n."),
    ("shop.leftToday", "Quedan {0}/{1} hoy"),
    ("minigame.keys", "Espacio / Intro"),
    ("minigame.giveUp", "Rendirse"),
    ("title.newGame", "Nueva partida"),
    ("title.continue", "Continuar"),
    ("title.load", "Cargar"),
    ("title.settings", "Ajustes"),
    ("title.credits", "Cr\u{e9}ditos"),
    ("title.quit", "Salir"),
    ("title.continueDetail", "Continuar: {0}"),
    ("title.version", "Versi\u{f3}n {0}"),
    ("title.versionBy", "Versi\u{f3}n {0} \u{00b7} por {1}"),
    ("title.dateInSlot", "{0} \u{00b7} ranura {1}"),
    ("pause.title", "Pausa"),
    ("pause.resume", "Reanudar"),
    ("pause.save", "Guardar"),
    ("pause.load", "Cargar"),
    ("pause.settings", "Ajustes"),
    ("pause.quitToTitle", "Volver al t\u{ed}tulo"),
    ("pause.quitGame", "Salir del juego"),
    ("credits.title", "Cr\u{e9}ditos"),
    ("credits.version", "Versi\u{f3}n {0}"),
    ("credits.by", "por {0}"),
    ("credits.licenses", "Las licencias de terceros est\u{e1}n en licenses/THIRD-PARTY.txt, junto al juego."),
    ("slots.loadTitle", "Cargar partida"),
    ("slots.saveTitle", "Guardar partida"),
    ("slots.newTitle", "Nueva partida"),
    ("slots.loadSubtitle", "Elige una partida para continuar."),
    ("slots.saveSubtitle", "Elige una ranura. El juego tambi\u{e9}n guarda cada ma\u{f1}ana."),
    ("slots.newSubtitle", "Elige una ranura para la nueva granja."),
    ("slots.load", "Cargar"),
    ("slots.save", "Guardar"),
    ("slots.start", "Empezar"),
    ("slots.delete", "Borrar"),
    ("slots.slot", "Ranura {0}"),
    ("slots.slotFarm", "Ranura {0} \u{00b7} {1}"),
    ("slots.played", "{0} \u{00b7} {1} de juego"),
    ("slots.unreadable", "No se pudo leer esta partida."),
    ("slots.empty", "Ranura vac\u{ed}a"),
    ("slots.date", "D\u{ed}a {0} de {1}, a\u{f1}o {2}"),
    ("time.justNow", "ahora mismo"),
    ("time.minutesAgo", "hace {0} min"),
    ("time.hoursAgo", "hace {0} h"),
    ("time.yesterday", "ayer"),
    ("time.daysAgo", "hace {0} d\u{ed}as"),
    ("settings.title", "Ajustes"),
    ("settings.subtitle", "Se guardan autom\u{e1}ticamente"),
    ("settings.tabDisplay", "Pantalla"),
    ("settings.tabAudio", "Sonido"),
    ("settings.tabControls", "Controles"),
    ("settings.tabAccessibility", "Accesibilidad"),
    ("settings.fullscreen", "Pantalla completa"),
    ("settings.integerScaling", "Escalado entero (p\u{ed}xeles m\u{e1}s n\u{ed}tidos)"),
    ("settings.interfaceSize", "Tama\u{f1}o de la interfaz"),
    ("settings.fullscreenHint", "La pantalla completa tambi\u{e9}n se activa con F11 o Alt+Intro."),
    ("settings.masterVolume", "Volumen general"),
    ("settings.music", "M\u{fa}sica"),
    ("settings.effects", "Efectos de sonido"),
    ("settings.mute", "Silenciar"),
    ("settings.keyboard", "Teclado"),
    ("settings.gamepad", "Mando"),
    ("settings.pressKey", "Pulsa una tecla\u{2026} (Esc o B cancela)"),
    ("settings.notBound", "Sin asignar"),
    ("settings.resetControls", "Restablecer valores predeterminados"),
    ("settings.padMove", "Moverse (o el stick izquierdo)"),
    ("settings.padInteract", "Interactuar, confirmar"),
    ("settings.padBack", "Cerrar, volver"),
    ("settings.padPreviousTab", "Pesta\u{f1}a anterior en los men\u{fa}s"),
    ("settings.padNextTab", "Pesta\u{f1}a siguiente en los men\u{fa}s"),
    ("settings.language", "Idioma"),
    ("settings.languageAuto", "Autom\u{e1}tico"),
    ("settings.textSize", "Tama\u{f1}o del texto"),
    ("settings.textSmall", "Peque\u{f1}o"),
    ("settings.textDefault", "Normal"),
    ("settings.textLarge", "Grande"),
    ("settings.textLargest", "Muy grande"),
    ("settings.readableFont", "Fuente legible (Atkinson Hyperlegible)"),
    ("settings.reducedMotion", "Movimiento reducido (sin textos flotantes, fundidos ni destellos)"),
    ("settings.tabsHint", "LB / RB o Tab cambian de pesta\u{f1}a"),
    (
        "confirm.lostProgress",
        "Se perder\u{e1} el progreso desde la \u{fa}ltima vez que guardaste. El juego guarda cada ma\u{f1}ana.",
    ),
    ("confirm.quitToTitleTitle", "\u{bf}Volver al t\u{ed}tulo?"),
    ("confirm.quitToTitle", "Volver al t\u{ed}tulo"),
    ("confirm.quitGameTitle", "\u{bf}Salir del juego?"),
    ("confirm.quitGame", "Salir"),
    ("confirm.overwriteTitle", "\u{bf}Sobrescribir la partida?"),
    ("confirm.overwriteMessage", "La ranura {0} ya tiene una partida guardada. \u{bf}Quieres reemplazarla?"),
    ("confirm.overwrite", "Sobrescribir"),
    ("confirm.deleteTitle", "\u{bf}Borrar la partida?"),
    ("confirm.deleteMessage", "La partida de la ranura {0} se borrar\u{e1} para siempre."),
    ("confirm.delete", "Borrar"),
    ("confirm.newGameTitle", "\u{bf}Empezar de nuevo en esta ranura?"),
    (
        "confirm.newGameMessage",
        "La ranura {0} ya tiene una partida guardada. Una partida nueva la reemplazar\u{e1} la primera vez que guarde.",
    ),
    ("confirm.newGame", "Empezar partida nueva"),
    ("toast.settingsNotSaved", "No se guardaron los ajustes: {0}"),
    ("toast.pluginError", "Error de un plugin: {0}"),
    ("toast.slotEmpty", "La ranura {0} est\u{e1} vac\u{ed}a."),
    ("toast.loadFailed", "No se pudo cargar esta partida."),
    ("toast.loaded", "Ranura {0} cargada"),
    ("toast.loadedBackup", "La ranura {0} estaba da\u{f1}ada; se carg\u{f3} su partida anterior."),
    ("toast.missingGlyph", "Las fuentes del juego no pueden mostrar \u{ab}{0}\u{bb} ({1}) ni otros caracteres de su escritura: se ven como cuadros."),
    ("toast.saved", "Partida guardada (ranura {0})"),
    ("toast.autosaved", "Guardado autom\u{e1}tico (ranura {0})"),
    ("toast.saveFailed", "No se pudo guardar: {0}"),
];

#[cfg(test)]
mod tests {
    use super::*;

    fn entries(lang: Lang) -> &'static [(&'static str, &'static str)] {
        match lang {
            Lang::En => EN,
            Lang::Es => ES,
        }
    }

    fn placeholders(text: &str) -> Vec<String> {
        let mut found: Vec<String> = text
            .match_indices('{')
            .filter_map(|(start, _)| {
                let rest = &text[start + 1..];
                rest.find('}').map(|end| rest[..end].to_owned())
            })
            .collect();
        found.sort();
        found
    }

    #[test]
    fn every_key_is_in_every_language_once() {
        for lang in Lang::ALL {
            let table = entries(lang);
            assert_eq!(lang.table().len(), table.len(), "{lang:?} has a duplicate key");
            for (key, _) in EN {
                assert!(lang.table().contains_key(key), "{lang:?} lacks {key}");
            }
            for (key, text) in table {
                assert!(Lang::En.table().contains_key(key), "{lang:?} has {key}, which English lacks");
                assert!(!text.is_empty(), "{lang:?} {key} is empty");
                assert_eq!(placeholders(text), placeholders(Lang::En.tr(key)), "{lang:?} {key}: placeholders differ");
            }
        }
    }

    #[test]
    fn missing_keys_fall_back_to_english_then_the_key() {
        assert_eq!(Lang::Es.tr("hud.money"), "Dinero:");
        assert_eq!(Lang::En.tr("hud.money"), "Money:");
        assert_eq!(Lang::Es.tr("no.such.key"), "no.such.key");
        assert_eq!(Lang::Es.get("no.such.key"), None);
        assert_eq!(Lang::Es.get("season.winter"), Some("Invierno"));
    }

    #[test]
    fn templates_take_positional_arguments() {
        assert_eq!(Lang::En.format("slots.date", &[&3, &"Spring", &1]), "Day 3 of Spring, Year 1");
        assert_eq!(Lang::Es.format("slots.date", &[&3, &"Primavera", &1]), "D\u{ed}a 3 de Primavera, a\u{f1}o 1");
        assert_eq!(fill("{1}-{0}", &[&"a", &"b"]), "b-a");
        // Braces that are not placeholders, and placeholders without an argument, stay.
        assert_eq!(fill("{x} {0} {2} {", &[&7]), "{x} 7 {2} {");
    }

    #[test]
    fn locale_tags_resolve_with_fallbacks() {
        assert_eq!(Lang::from_tag("es"), Some(Lang::Es));
        assert_eq!(Lang::from_tag("es-MX"), Some(Lang::Es));
        assert_eq!(Lang::from_tag("es_ES.UTF-8"), Some(Lang::Es));
        assert_eq!(Lang::from_tag("EN-us"), Some(Lang::En));
        assert_eq!(Lang::from_tag("fr-FR"), None);
        assert_eq!(Lang::from_tag("C"), None);
        assert_eq!(Lang::from_tag(""), None);
        // The player's choice first, then the system, then the game, then English.
        assert_eq!(Lang::resolve("es", Some("en-US"), "en"), Lang::Es);
        assert_eq!(Lang::resolve("", Some("es-AR"), "en"), Lang::Es);
        assert_eq!(Lang::resolve("", Some("fr-FR"), "es"), Lang::Es);
        assert_eq!(Lang::resolve("", None, "es"), Lang::Es);
        assert_eq!(Lang::resolve("", None, "de"), Lang::En);
        assert_eq!(Lang::resolve("xx", None, ""), Lang::En);
        for lang in Lang::ALL {
            assert_eq!(Lang::from_tag(lang.code()), Some(lang));
        }
    }
}
