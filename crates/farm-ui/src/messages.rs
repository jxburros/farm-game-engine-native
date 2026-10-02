//! The engine's messages in the player's language: the simulation's toasts, the built-in
//! minigames' texts and the save-load notices (the catalog in `farm_sim::messages`).
//!
//! The engine records every message in English (replays, goldens and logs keep that text) and
//! carries the catalog message beside it. A player translates it here, with one table per
//! language keyed by the catalog keys; English is the catalog itself. A key a table lacks falls
//! back to its English template, so a message added to the catalog shows in English until it is
//! translated (a test lists every missing key).

use crate::i18n::Lang;
use farm_sim::messages::{Message, Template};
use std::collections::BTreeMap;
use std::sync::OnceLock;

impl Lang {
    /// A catalog message in this language (nested messages too).
    pub fn message(self, message: &Message) -> String {
        message.render(&|template| self.template(template))
    }

    /// The text to show for an English `text` that may come with its catalog message: the
    /// message in this language when there is one, else `text` (a creator's own words).
    pub fn localize(self, message: Option<&Message>, text: &str) -> String {
        match message {
            Some(message) if self != Lang::En => self.message(message),
            _ => text.to_owned(),
        }
    }

    /// A catalog template in this language: its translation, else the English template.
    pub fn template(self, template: &'static Template) -> &'static str {
        let table = match self {
            Lang::En => return template.english,
            Lang::Es => table(&ES_MAP, ES),
        };
        table.get(template.key).copied().unwrap_or(template.english)
    }
}

static ES_MAP: OnceLock<BTreeMap<&'static str, &'static str>> = OnceLock::new();

fn table(
    cell: &'static OnceLock<BTreeMap<&'static str, &'static str>>,
    entries: &'static [(&'static str, &'static str)],
) -> &'static BTreeMap<&'static str, &'static str> {
    cell.get_or_init(|| entries.iter().copied().collect())
}

/// Spanish.
const ES: &[(&str, &str)] = &[
    ("msg.inventoryFull", "\u{a1}El inventario est\u{e1} lleno!"),
    ("msg.inventoryFullShort", "\u{a1}Inventario lleno!"),
    ("msg.notEnoughMoney", "\u{a1}No tienes suficiente dinero!"),
    ("msg.receivedItem", "Recibiste {0}"),
    ("msg.receivedItems", "Recibiste {0} x{1}"),
    ("msg.receivedCount", "Recibiste {0}\u{d7} {1}"),
    ("msg.receivedMoney", "Recibiste ${0}"),
    ("msg.paidMoney", "Pagaste ${0}"),
    ("msg.pickedUp", "Recogiste {0}"),
    ("msg.dontHaveItem", "No tienes ese objeto."),
    ("msg.cantUseLikeThat", "{0} no se puede usar as\u{ed}."),
    ("msg.needTool", "\u{a1}Necesitas esta herramienta: {0}!"),
    ("msg.toolBroken", "\u{a1}Tu herramienta ({0}) est\u{e1} rota! Una tienda puede repararla."),
    ("msg.cantUseHere", "No puedes usar {0} aqu\u{ed}"),
    ("msg.watered", "\u{a1}Regado!"),
    ("msg.tilled", "\u{a1}Tierra arada!"),
    ("msg.clearedWithered", "Quitaste el cultivo marchito."),
    ("msg.cropNotReady", "El cultivo a\u{fa}n no est\u{e1} listo para cosechar"),
    ("msg.cropWithered", "Este cultivo se marchit\u{f3}: qu\u{ed}talo con una guada\u{f1}a."),
    ("msg.harvestItemMissing", "{0} no se puede cosechar: falta su objeto de cosecha \u{ab}{1}\u{bb}."),
    ("msg.harvested", "Cosechaste {0}x {1}{2}{3} (vale ~${4})"),
    ("msg.noSeeds", "No tienes semillas en el inventario"),
    ("msg.invalidCrop", "\u{a1}Tipo de cultivo no v\u{e1}lido!"),
    ("msg.cantGrowInSeason", "\u{a1}{0} no puede crecer en {1}! Crece en {2}."),
    ("msg.cannotGrowInSeason", "\u{a1}{0} no puede crecer en {1}!"),
    ("msg.noSpaceForCrop", "\u{a1}No hay espacio suficiente para este cultivo!"),
    ("msg.planted", "\u{a1}Plantaste {0}! Ri\u{e9}galo para que crezca."),
    ("msg.plantedFertilized", "\u{a1}Plantaste {0}! (Abonado) Ri\u{e9}galo para que crezca."),
    ("msg.noSeedToPlant", "No tienes {0} para plantar."),
    ("msg.noFertilizer", "No tienes {0} para usar."),
    ("msg.machineStillWorkingShort", "Todav\u{ed}a trabajando\u{2026}"),
    ("msg.machineIdle", "{0} est\u{e1} parada: c\u{e1}rgale una receta."),
    ("msg.tool.wateringCan", "regadera"),
    ("msg.tool.hoe", "azada"),
    ("msg.tool.axe", "hacha"),
    ("msg.tool.pickaxe", "pico"),
    ("msg.tool.scythe", "guada\u{f1}a"),
    ("msg.tool.fishingRod", "ca\u{f1}a de pescar"),
    ("msg.season.spring", "primavera"),
    ("msg.season.summer", "verano"),
    ("msg.season.fall", "oto\u{f1}o"),
    ("msg.season.winter", "invierno"),
    ("msg.skill.farming", "Agricultura"),
    ("msg.skill.fishing", "Pesca"),
    ("msg.skill.foraging", "Recolecci\u{f3}n"),
    ("msg.skill.mining", "Miner\u{ed}a"),
    ("msg.skill.social", "Social"),
    ("msg.skill.combat", "Combate"),
    ("msg.levelUp", "\u{a1}{0}: nivel {1}!"),
    ("msg.nodeHealth", "{0}: {1}/{2}"),
    ("msg.nodeNeedsTool", "{0} necesita esta herramienta: {1}."),
    ("msg.toolTooWeak", "Tu herramienta ({0}) no es lo bastante fuerte para {1}."),
    ("msg.nodeCleared", "\u{a1}{0} despejado!"),
    ("msg.nodeClearedGot", "\u{a1}{0} despejado! Obtuviste {1}"),
    ("msg.fedAnimal", "Diste de comer a {0}"),
    ("msg.collectedProduct", "Recogiste {0} de {1}"),
    ("msg.animalHappy", "\u{a1}{0} se ve feliz! \u{2665}"),
    ("msg.animalContent", "{0} est\u{e1} contento."),
    ("msg.crafted", "Fabricaste {0}x {1}"),
    ("msg.unknownRecipe", "Receta desconocida."),
    ("msg.recipeNeedsMachine", "Esa receta necesita una m\u{e1}quina: c\u{e1}rgala all\u{ed}."),
    ("msg.cannotCraft", "No puedes fabricar eso ahora."),
    ("msg.recipeLocked", "Receta a\u{fa}n no desbloqueada."),
    ("msg.missingIngredients", "Faltan ingredientes."),
    ("msg.needStation", "Tienes que estar cerca de {0} para fabricar eso."),
    ("msg.needStationCategory", "Tienes que estar cerca de una estaci\u{f3}n de {0} para fabricar eso."),
    ("msg.unknownMachine", "M\u{e1}quina desconocida."),
    ("msg.needMachineItem", "Necesitas {0} en tu inventario."),
    ("msg.noRoomToPlace", "No hay sitio para colocarlo ah\u{ed}."),
    ("msg.spotMustStayClear", "Ese lugar tiene que quedar despejado."),
    ("msg.placed", "Colocaste {0}"),
    ("msg.noMachine", "Ah\u{ed} no hay ninguna m\u{e1}quina."),
    ("msg.machineStillWorking", "Todav\u{ed}a est\u{e1} trabajando."),
    ("msg.machineBusy", "Ya est\u{e1} trabajando."),
    ("msg.collectFirst", "Primero recoge los productos terminados."),
    ("msg.machineCannotRun", "Esta m\u{e1}quina no puede hacer esa receta."),
    ("msg.startedRecipe", "Empezaste {0}"),
    ("msg.shopMissing", "Esa tienda no existe."),
    ("msg.talkToShopkeeper", "Habla con el tendero para comprar."),
    ("msg.noShopOpen", "No hay ninguna tienda abierta."),
    ("msg.notSoldHere", "Aqu\u{ed} no se vende."),
    ("msg.notAvailableIn", "No disponible en {0}."),
    ("msg.soldOut", "\u{a1}Agotado por hoy!"),
    ("msg.onlyLeft", "Solo quedan {0} por hoy."),
    ("msg.unknownItem", "Objeto desconocido."),
    ("msg.bought", "Compraste {0}x {1} por ${2}"),
    ("msg.shopDoesntBuy", "{0} no compra objetos."),
    ("msg.notThatMany", "No tienes tantos."),
    ("msg.sold", "Vendiste {0}x {1} por ${2}"),
    ("msg.shopDoesntRepair", "{0} no repara herramientas."),
    ("msg.cantRepair", "Eso no se puede reparar."),
    ("msg.perfectShape", "{0} est\u{e1} en perfecto estado."),
    ("msg.repairTooExpensive", "La reparaci\u{f3}n cuesta ${0}: \u{a1}no tienes suficiente dinero!"),
    ("msg.repaired", "Reparaste {0} por ${1}"),
    ("msg.exhausted", "Te est\u{e1}s agotando: piensa en ir a dormir."),
    ("msg.collapsed", "\u{a1}Te desmayaste de cansancio! Perdiste ${0}."),
    ("msg.weatherToday", "Tiempo: {0}"),
    ("msg.seasonArrived", "\u{a1}Lleg\u{f3} {0}!"),
    ("msg.yearBegins", "\u{a1}Empieza el a\u{f1}o {0}!"),
    ("msg.dayOfSeason", "D\u{ed}a {0} de {1}, a\u{f1}o {2}"),
    ("msg.festivalToday", "\u{a1}Hoy es {0}!"),
    (
        "msg.actionChainLimit",
        "Se alcanz\u{f3} el l\u{ed}mite de acciones encadenadas en \u{ab}{0}\u{bb}: las acciones pueden llamar a otras hasta {1} niveles de profundidad y {2} veces en total.",
    ),
    ("msg.soilWatered", "La tierra de alrededor est\u{e1} regada."),
    ("msg.unknownAction", "Acci\u{f3}n desconocida \u{ab}{0}\u{bb}"),
    ("msg.unknownMinigame", "Minijuego desconocido \u{ab}{0}\u{bb}"),
    ("msg.nothingToPlay", "Aqu\u{ed} no hay nada a qu\u{e9} jugar."),
    ("msg.pluginError", "Plugin {0}: {1}"),
    ("msg.pluginUnknownItem", "objeto desconocido \u{ab}{0}\u{bb}"),
    ("msg.pluginUnknownWeather", "tiempo desconocido \u{ab}{0}\u{bb}"),
    ("msg.pluginUnknownNpc", "PNJ desconocido \u{ab}{0}\u{bb}"),
    ("msg.waterQuiet", "El agua est\u{e1} en calma: aqu\u{ed} no parece vivir nada."),
    ("msg.fishedUp", "Pescaste {0}\u{2026}"),
    ("msg.notANibble", "Ni un mordisco."),
    ("msg.gotAway", "\u{a1}Se escap\u{f3}!"),
    ("msg.caughtFish", "\u{a1}Pescaste {0}!"),
    ("msg.mineFloor", "Mina: piso {0}"),
    ("msg.mineFloorCheckpoint", "Mina: piso {0} (parada del ascensor)"),
    ("msg.mineNotAtEntrance", "Tienes que estar en la mina para bajar."),
    ("msg.mineNotThatDeep", "Todav\u{ed}a no encontraste el camino hasta tan abajo."),
    ("msg.mineNotInMine", "No est\u{e1}s en la mina."),
    ("msg.mineClimbBack", "Vuelves a subir a la superficie."),
    ("msg.mineLadder", "\u{a1}Aparece una escalera al siguiente piso!"),
    ("msg.questRewardLost", "Inventario lleno: se perdi\u{f3} la recompensa de la misi\u{f3}n: {0}\u{d7} {1}"),
    ("msg.newQuest", "Nueva misi\u{f3}n: {0}"),
    ("msg.noOneToGive", "No hay nadie a quien d\u{e1}rselo."),
    ("msg.alreadyGifted", "{0} ya recibi\u{f3} un regalo hoy."),
    ("msg.giftReaction", "{0}: {1}{2} ({3})"),
    ("msg.gift.loved", "\u{a1}Le encanta!"),
    ("msg.gift.liked", "Le gusta."),
    ("msg.gift.neutral", "Lo acepta con cortes\u{ed}a."),
    ("msg.gift.disliked", "No parece muy entusiasmado\u{2026}"),
    ("msg.gift.hated", "\u{a1}Lo odia!"),
    ("msg.gift.birthday", " (\u{a1}Cumplea\u{f1}os!)"),
    ("msg.landedElsewhere", "No se puede estar en ({0},{1}) de {2}; llegaste a ({3},{4})."),
    ("msg.entered", "Entraste en {0}"),
    ("msg.minigame.ready", "\u{bf}Listo?"),
    ("msg.minigame.go", "\u{a1}Vamos!"),
    ("msg.minigame.timingPrompt", "\u{a1}Det\u{e9}n el marcador dentro de la zona!"),
    ("msg.minigame.timingButton", "\u{a1}Para! (Espacio)"),
    ("msg.minigame.holdPrompt", "Mant\u{e9}n pulsado {0} segundos y suelta para recoger el sedal."),
    ("msg.minigame.holdButton", "Mant\u{e9}n para recoger (Espacio)"),
    ("msg.minigame.holdReeling", "Recogiendo\u{2026} \u{a1}suelta!"),
    ("msg.minigame.battleStatus", "T\u{fa}: {0} de vida \u{b7} {1} de magia | {2}: {3} de vida"),
    ("msg.minigame.battleEnemy", "Limo del bosque"),
    ("msg.minigame.battleHeavy", "{0} prepara un ataque fuerte. \u{a1}Defi\u{e9}ndete en el pr\u{f3}ximo turno!"),
    ("msg.minigame.battleAttacks", "{0} ataca. Elige tu siguiente movimiento."),
    ("msg.minigame.rhythmPrompt", "\u{a1}Pulsa cuando una nota llegue a la l\u{ed}nea!"),
    ("msg.minigame.rhythmButton", "\u{a1}Pulsa! (Espacio)"),
    ("msg.minigame.rhythmHits", "Aciertos: {0}/{1}"),
    (
        "msg.minigame.movingPrompt",
        "Mant\u{e9}n la barra bajo el objetivo: mant\u{e9}n pulsado para ir a la derecha, suelta para volver a la izquierda.",
    ),
    ("msg.minigame.movingButton", "Mant\u{e9}n para mover (Espacio)"),
    ("msg.minigame.movingTimeLeft", "Quedan {0} s"),
    ("msg.minigame.memoryPrompt", "Mira los s\u{ed}mbolos y rep\u{ed}telos en orden."),
    ("msg.minigame.memoryWatch", "Mira con atenci\u{f3}n\u{2026}"),
    ("msg.minigame.memoryTurn", "Tu turno: {0}/{1}"),
    ("msg.save.damaged", "La partida est\u{e1} da\u{f1}ada: {0}"),
    ("msg.save.tooLarge", "El archivo de la partida es demasiado grande."),
    ("msg.save.stateTooLarge", "El estado de la partida es demasiado grande."),
    ("msg.save.notASave", "No es una partida de Farm Engine (falta el identificador FGSV)."),
    (
        "msg.save.formatNewer",
        "El formato {0} de esta partida es m\u{e1}s nuevo que el que admite el juego ({1}). Actualiza el juego.",
    ),
    ("msg.save.otherGame", "Esta partida es de otro juego (\u{ab}{0}\u{bb}, no \u{ab}{1}\u{bb})."),
    (
        "msg.save.newerVersion",
        "Esta partida se hizo con una versi\u{f3}n m\u{e1}s nueva del juego ({0}; esta es la {1}). Puede que no se cargue todo el progreso.",
    ),
    ("msg.save.mapRepaired", "El mapa de {0} en esta partida no ten\u{ed}a el tama\u{f1}o correcto y se repar\u{f3}."),
    ("msg.save.newRng", "Esta partida no ten\u{ed}a estado aleatorio; se empez\u{f3} uno nuevo."),
    ("msg.save.itemSetAside", "{0} objeto de esta partida ya no existe en el juego y se apart\u{f3}: {1}."),
    ("msg.save.itemsSetAside", "{0} objetos de esta partida ya no existen en el juego y se apartaron: {1}."),
];

#[cfg(test)]
mod tests {
    use super::*;
    use farm_sim::messages::{self, Arg, ALL};
    use std::collections::BTreeSet;

    fn placeholders(text: &str) -> BTreeSet<String> {
        text.match_indices('{')
            .filter_map(|(start, _)| {
                let rest = &text[start + 1..];
                rest.find('}').map(|end| rest[..end].to_owned())
            })
            .collect()
    }

    #[test]
    fn every_catalog_message_is_translated_once_with_its_placeholders() {
        let keys: BTreeMap<&str, &Template> = ALL.iter().map(|template| (template.key, *template)).collect();
        assert_eq!(table(&ES_MAP, ES).len(), ES.len(), "Spanish has a duplicate key");
        for (key, text) in ES {
            let template = keys.get(key).unwrap_or_else(|| panic!("Spanish has {key}, which the catalog lacks"));
            assert!(!text.is_empty(), "{key} is empty");
            assert_eq!(placeholders(text), placeholders(template.english), "{key}: placeholders differ");
        }
        let spanish = table(&ES_MAP, ES);
        let missing: Vec<&str> =
            ALL.iter().map(|template| template.key).filter(|key| !spanish.contains_key(key)).collect();
        assert!(missing.is_empty(), "Spanish lacks {missing:?}");
    }

    #[test]
    fn messages_render_in_the_language_with_nested_arguments() {
        let need = messages::NEED_TOOL.with_args(vec![messages::tool_noun("watering-can")]);
        assert_eq!(Lang::En.message(&need), "You need a watering can!");
        assert_eq!(Lang::Es.message(&need), "\u{a1}Necesitas esta herramienta: regadera!");
        let day = messages::DAY_OF_SEASON.with_args(vec![Arg::text(3), messages::season_noun("fall"), Arg::text(2)]);
        assert_eq!(Lang::Es.message(&day), "D\u{ed}a 3 de oto\u{f1}o, a\u{f1}o 2");
        // Text that is not engine text stays as it is.
        assert_eq!(Lang::Es.localize(None, "A creator's words"), "A creator's words");
        assert_eq!(Lang::Es.localize(Some(&day), "Day 3 of fall, Year 2"), "D\u{ed}a 3 de oto\u{f1}o, a\u{f1}o 2");
        assert_eq!(Lang::En.localize(Some(&day), "Day 3 of fall, Year 2"), "Day 3 of fall, Year 2");
    }
}
