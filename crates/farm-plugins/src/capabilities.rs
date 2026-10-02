//! Mutation capabilities: what a plugin's answers may do, from the pack manifest's
//! `permissions.mutations`, and the namespacing of the ids those answers name.
//!
//! Hooks only decide what a plugin *hears*. Capabilities decide what it may *answer*:
//!
//! - `"message"`, `"giveMoney"`, … allow one mutation type. For the types that name pack
//!   content (`setFlag`, `giveItem`, `takeItem`, `modifyFriendship`, `startQuest`,
//!   `warpPlayer`, `startDialogue`, `performAction`, `startMinigame`) the plugin may only name
//!   its own pack's ids: a plain id `x` means `packId:x`, `packId:x` is kept, anything else is
//!   refused.
//! - `"giveItem:any"` (and so on) lifts that restriction: ids are used as written, so the
//!   plugin can name base content, the creator's content or other packs.
//! - `"*"` allows everything, with any ids.
//!
//! A manifest without `permissions.mutations` gets [`DEFAULT_MUTATION_CAPABILITIES`], and its
//! plugins' answers to `onEffect` and `onCommand` are dropped (those hooks are for watching).
//! No plugin may write `event:` flags, the bookkeeping of one-shot events.
//!
//! Everything here runs before a mutation becomes a `pluginMutation` command, so the command
//! log holds the resolved, permitted mutation and replays never need the pack.

use crate::{PluginError, PluginErrorKind};
use farm_sim::hooks::hook_names;
use farm_sim::schema::PluginMutation;
use serde_json::Value;
use std::collections::BTreeSet;

/// What a pack's plugins may answer when its manifest declares no `permissions.mutations`:
/// messages, sounds, and the pack's own flags and items.
pub const DEFAULT_MUTATION_CAPABILITIES: &[&str] = &["message", "playSound", "setFlag", "giveItem", "takeItem"];

/// Every mutation type, in schema order.
pub const MUTATION_TYPES: &[&str] = &[
    "giveItem",
    "takeItem",
    "giveMoney",
    "takeMoney",
    "setFlag",
    "message",
    "setWeather",
    "modifyFriendship",
    "grantXp",
    "modifyEnergy",
    "startQuest",
    "warpPlayer",
    "startDialogue",
    "playSound",
    "performAction",
    "startMinigame",
];

/// The mutation types that name pack content, and so are limited to the plugin's own pack
/// unless `type:any` is granted.
pub const SCOPED_MUTATION_TYPES: &[&str] = &[
    "setFlag",
    "giveItem",
    "takeItem",
    "modifyFriendship",
    "startQuest",
    "warpPlayer",
    "startDialogue",
    "performAction",
    "startMinigame",
];

/// Flags no plugin may write: `event:<id>:fired` re-arms or disables one-shot events.
pub const RESERVED_FLAG_PREFIX: &str = "event:";

/// Most mutations kept from one handler call; the rest are dropped with one error.
pub const MAX_MUTATIONS_PER_CALL: usize = 64;

/// Most distinct flags one plugin may write in a session (flags are saved, so they would grow
/// the save without bound).
pub const MAX_FLAG_KEYS_PER_PLUGIN: usize = 256;

/// Most distinct skills one plugin may grant XP in during a session (`grantXp` creates
/// skills it names).
pub const MAX_SKILL_KEYS_PER_PLUGIN: usize = 32;

/// The capabilities granted to one plugin (parsed from `permissions.mutations`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MutationGrants {
    declared: bool,
    /// Types allowed with the plugin's own ids (and the unscoped types).
    own: BTreeSet<&'static str>,
    /// Types allowed with any ids.
    any: BTreeSet<&'static str>,
    unknown: Vec<String>,
}

impl MutationGrants {
    /// Parse a manifest's `permissions.mutations`; `None` gives the defaults. Unknown entries
    /// are ignored (see [`MutationGrants::unknown`]).
    pub fn new(declared: Option<&[String]>) -> Self {
        let mut grants =
            Self { declared: declared.is_some(), own: BTreeSet::new(), any: BTreeSet::new(), unknown: Vec::new() };
        match declared {
            Some(entries) => {
                for entry in entries {
                    grants.grant(entry);
                }
            }
            None => {
                for entry in DEFAULT_MUTATION_CAPABILITIES {
                    grants.grant(entry);
                }
            }
        }
        grants
    }

    /// Everything, with any ids (`["*"]`): for trusted hosts and tests.
    pub fn all() -> Self {
        Self::new(Some(&["*".to_owned()]))
    }

    fn grant(&mut self, entry: &str) {
        let entry = entry.trim();
        if entry == "*" {
            self.own.extend(MUTATION_TYPES);
            self.any.extend(MUTATION_TYPES);
            return;
        }
        let (name, scope) = match entry.split_once(':') {
            Some((name, scope)) => (name, Some(scope)),
            None => (entry, None),
        };
        let Some(&kind) = MUTATION_TYPES.iter().find(|kind| **kind == name) else {
            self.unknown.push(entry.to_owned());
            return;
        };
        match scope {
            None | Some("own") => {
                self.own.insert(kind);
            }
            Some("any") => {
                self.own.insert(kind);
                self.any.insert(kind);
            }
            Some(_) => self.unknown.push(entry.to_owned()),
        }
    }

    /// Whether the manifest declared `permissions.mutations` (rather than getting the defaults).
    pub fn declared(&self) -> bool {
        self.declared
    }

    /// Declared entries that name no capability (a typo, or a newer engine's capability).
    pub fn unknown(&self) -> &[String] {
        &self.unknown
    }

    /// Whether `mutation_type` is allowed at all.
    pub fn allows(&self, mutation_type: &str) -> bool {
        self.own.contains(mutation_type)
    }

    /// Whether `mutation_type` may name any ids (not only the plugin's own pack's).
    pub fn allows_any(&self, mutation_type: &str) -> bool {
        self.any.contains(mutation_type) || !SCOPED_MUTATION_TYPES.contains(&mutation_type)
    }

    /// Whether answers to `hook` are dropped: `onEffect` and `onCommand` are for watching
    /// unless the manifest declares mutations.
    pub fn observe_only(&self, hook: &str) -> bool {
        !self.declared && (hook == hook_names::ON_EFFECT || hook == hook_names::ON_COMMAND)
    }

    /// Check a parsed mutation against the grants, resolving the ids it names against the
    /// plugin's pack. `Err` carries the reason.
    pub fn authorize(&self, pack_id: &str, mutation: PluginMutation) -> Result<PluginMutation, String> {
        let kind = mutation.type_name();
        if !self.allows(kind) {
            return Err(if self.declared {
                format!("{kind}: not in the pack's permissions.mutations")
            } else {
                format!("{kind}: not allowed unless the pack declares it in permissions.mutations")
            });
        }
        let scope = Scope { pack_id, kind, any: self.allows_any(kind) };
        let authorized = match mutation {
            PluginMutation::SetFlag { flag, value } => {
                let flag = scope.resolve("flag", flag)?;
                if flag.starts_with(RESERVED_FLAG_PREFIX) {
                    return Err(format!("{kind}: flags starting with '{RESERVED_FLAG_PREFIX}' belong to events"));
                }
                PluginMutation::SetFlag { flag, value }
            }
            PluginMutation::GiveItem { item_id, quantity } => {
                PluginMutation::GiveItem { item_id: scope.resolve("itemId", item_id)?, quantity }
            }
            PluginMutation::TakeItem { item_id, quantity } => {
                PluginMutation::TakeItem { item_id: scope.resolve("itemId", item_id)?, quantity }
            }
            PluginMutation::ModifyFriendship { npc_id, delta } => {
                PluginMutation::ModifyFriendship { npc_id: scope.resolve("npcId", npc_id)?, delta }
            }
            PluginMutation::StartQuest { quest_id } => {
                PluginMutation::StartQuest { quest_id: scope.resolve("questId", quest_id)? }
            }
            PluginMutation::WarpPlayer { scene_id, x, y } => {
                PluginMutation::WarpPlayer { scene_id: scope.resolve("sceneId", scene_id)?, x, y }
            }
            PluginMutation::StartDialogue { npc_id, dialogue_id } => PluginMutation::StartDialogue {
                npc_id: scope.resolve("npcId", npc_id)?,
                dialogue_id: dialogue_id.map(|id| scope.resolve("dialogueId", id)).transpose()?,
            },
            PluginMutation::PerformAction { action_id } => {
                PluginMutation::PerformAction { action_id: scope.resolve("actionId", action_id)? }
            }
            PluginMutation::StartMinigame { minigame_id } => {
                PluginMutation::StartMinigame { minigame_id: scope.resolve("minigameId", minigame_id)? }
            }
            other => other,
        };
        Ok(authorized)
    }
}

/// Id resolution for one mutation.
struct Scope<'a> {
    pack_id: &'a str,
    kind: &'a str,
    any: bool,
}

impl Scope<'_> {
    /// Own scope: a plain id is the pack's (`packId:id`), `packId:…` is kept, other packs' and
    /// global ids are refused. Any scope: as written.
    fn resolve(&self, key: &str, id: String) -> Result<String, String> {
        if self.any {
            return Ok(id);
        }
        if !id.contains(':') {
            return Ok(format!("{}:{id}", self.pack_id));
        }
        if id.strip_prefix(self.pack_id).is_some_and(|rest| rest.starts_with(':')) {
            return Ok(id);
        }
        Err(format!(
            "{}: {key} '{id}' is not this pack's; the pack needs '{}:any' in permissions.mutations",
            self.kind, self.kind
        ))
    }
}

/// The flags and skills a plugin created this session, capped so a plugin cannot grow the save
/// without bound.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub(crate) struct KeyBudget {
    flags: BTreeSet<String>,
    skills: BTreeSet<String>,
}

impl KeyBudget {
    fn admit(&mut self, mutation: &PluginMutation) -> Result<(), String> {
        let (keys, key, max, what) = match mutation {
            PluginMutation::SetFlag { flag, .. } => (&mut self.flags, flag, MAX_FLAG_KEYS_PER_PLUGIN, "flags"),
            PluginMutation::GrantXp { skill, .. } => (&mut self.skills, skill, MAX_SKILL_KEYS_PER_PLUGIN, "skills"),
            _ => return Ok(()),
        };
        if keys.contains(key) {
            return Ok(());
        }
        if keys.len() >= max {
            return Err(format!(
                "{}: a plugin may use at most {max} different {what} per session",
                mutation.type_name()
            ));
        }
        keys.insert(key.clone());
        Ok(())
    }
}

/// Validate and authorize a handler's answer: parse each entry
/// ([`crate::parse_mutation`]), check it against the grants and the key budget, and keep at
/// most [`MAX_MUTATIONS_PER_CALL`]. Dropped entries become
/// [`PluginErrorKind::InvalidMutation`] errors.
pub(crate) fn check_answer(
    plugin_id: &str,
    pack_id: &str,
    grants: &MutationGrants,
    keys: &mut KeyBudget,
    raw: &Value,
    hook: &str,
) -> (Vec<PluginMutation>, Vec<PluginError>) {
    let mut mutations = Vec::new();
    let mut errors = Vec::new();
    let Value::Array(entries) = raw else {
        return (mutations, errors);
    };
    let error = |message: String| PluginError {
        plugin_id: plugin_id.to_owned(),
        kind: PluginErrorKind::InvalidMutation,
        message,
        hook: Some(hook.to_owned()),
    };
    if grants.observe_only(hook) {
        if !entries.is_empty() {
            errors
                .push(error(format!("answers to {hook} are dropped: the pack does not declare permissions.mutations")));
        }
        return (mutations, errors);
    }
    for (index, entry) in entries.iter().enumerate() {
        if index == MAX_MUTATIONS_PER_CALL {
            errors.push(error(format!(
                "mutations [{index}] and later dropped: at most {MAX_MUTATIONS_PER_CALL} per call ({} returned)",
                entries.len()
            )));
            break;
        }
        let checked = crate::parse_mutation(entry)
            .and_then(|mutation| grants.authorize(pack_id, mutation))
            .and_then(|mutation| keys.admit(&mutation).map(|()| mutation));
        match checked {
            Ok(mutation) => mutations.push(mutation),
            Err(reason) => errors.push(error(format!("mutation [{index}] dropped: {reason}"))),
        }
    }
    (mutations, errors)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn grants(entries: &[&str]) -> MutationGrants {
        MutationGrants::new(Some(&entries.iter().map(|e| (*e).to_owned()).collect::<Vec<_>>()))
    }

    fn flag(name: &str) -> PluginMutation {
        PluginMutation::SetFlag { flag: name.to_owned(), value: json!(true) }
    }

    fn item(id: &str) -> PluginMutation {
        PluginMutation::GiveItem { item_id: id.to_owned(), quantity: 1 }
    }

    #[test]
    fn defaults_allow_messages_sounds_and_own_flags_and_items() {
        let defaults = MutationGrants::new(None);
        assert!(!defaults.declared());
        for kind in ["message", "playSound", "setFlag", "giveItem", "takeItem"] {
            assert!(defaults.allows(kind), "{kind}");
        }
        for kind in ["giveMoney", "takeMoney", "performAction", "startQuest", "warpPlayer", "grantXp", "setWeather"] {
            assert!(!defaults.allows(kind), "{kind}");
        }
        assert_eq!(
            defaults.authorize("mod", PluginMutation::GiveMoney { amount: 5 }),
            Err("giveMoney: not allowed unless the pack declares it in permissions.mutations".to_owned())
        );
        assert!(defaults.observe_only("onEffect") && defaults.observe_only("onCommand"));
        assert!(!defaults.observe_only("onDayStart"));
        assert!(!grants(&["message"]).observe_only("onEffect"));
    }

    #[test]
    fn own_scope_namespaces_plain_ids_and_refuses_other_packs() {
        let own = grants(&["setFlag", "giveItem:own", "startDialogue"]);
        assert_eq!(own.authorize("mod", flag("met")), Ok(flag("mod:met")));
        assert_eq!(own.authorize("mod", flag("mod:met")), Ok(flag("mod:met")));
        assert_eq!(own.authorize("mod", item("seed")), Ok(item("mod:seed")));
        assert_eq!(
            own.authorize("mod", item("other:seed")),
            Err("giveItem: itemId 'other:seed' is not this pack's; the pack needs 'giveItem:any' in permissions.mutations"
                .to_owned())
        );
        // A pack id that merely starts the same way is another pack.
        assert!(own.authorize("mod", item("modder:seed")).is_err());
        assert_eq!(
            own.authorize(
                "mod",
                PluginMutation::StartDialogue { npc_id: "elder".to_owned(), dialogue_id: Some("hello".to_owned()) }
            ),
            Ok(PluginMutation::StartDialogue {
                npc_id: "mod:elder".to_owned(),
                dialogue_id: Some("mod:hello".to_owned())
            })
        );
        assert_eq!(
            own.authorize("mod", PluginMutation::Message { text: "hi".to_owned() }),
            Err("message: not in the pack's permissions.mutations".to_owned())
        );
    }

    #[test]
    fn any_scope_keeps_ids_but_event_flags_stay_reserved() {
        let any = grants(&["setFlag:any", "giveItem:any"]);
        assert_eq!(any.authorize("mod", item("seed-wheat")), Ok(item("seed-wheat")));
        assert_eq!(any.authorize("mod", flag("quest-done")), Ok(flag("quest-done")));
        let reserved = Err("setFlag: flags starting with 'event:' belong to events".to_owned());
        assert_eq!(any.authorize("mod", flag("event:storm:fired")), reserved);
        assert_eq!(MutationGrants::all().authorize("mod", flag("event:storm:fired")), reserved);
        // Own scope refuses it as another namespace; a pack named `event` cannot write it either.
        assert!(grants(&["setFlag"]).authorize("mod", flag("event:storm:fired")).is_err());
        assert!(grants(&["setFlag"]).authorize("event", flag("storm:fired")).is_err());
    }

    #[test]
    fn unscoped_types_and_the_wildcard() {
        let money = grants(&["giveMoney", "grantXp:any", "nonsense", "giveItem:everywhere"]);
        assert_eq!(
            money.authorize("mod", PluginMutation::GiveMoney { amount: 5 }),
            Ok(PluginMutation::GiveMoney { amount: 5 })
        );
        let xp = PluginMutation::GrantXp { skill: "farming".to_owned(), amount: 5 };
        assert_eq!(money.authorize("mod", xp.clone()), Ok(xp));
        assert_eq!(money.unknown(), ["nonsense", "giveItem:everywhere"]);
        assert!(!money.allows("giveItem"));
        let all = MutationGrants::all();
        assert!(MUTATION_TYPES.iter().all(|kind| all.allows(kind) && all.allows_any(kind)));
        assert_eq!(all.authorize("mod", item("other:seed")), Ok(item("other:seed")));
    }

    #[test]
    fn answers_are_capped_and_observe_only_hooks_drop_everything() {
        let entries: Vec<Value> = (0..70).map(|i| json!({ "type": "message", "text": format!("m{i}") })).collect();
        let mut keys = KeyBudget::default();
        let (kept, errors) =
            check_answer("mod:p", "mod", &MutationGrants::all(), &mut keys, &Value::Array(entries), "onDayStart");
        assert_eq!(kept.len(), MAX_MUTATIONS_PER_CALL);
        let messages: Vec<&str> = errors.iter().map(|e| e.message.as_str()).collect();
        assert_eq!(messages, ["mutations [64] and later dropped: at most 64 per call (70 returned)"]);

        let answer = json!([{ "type": "message", "text": "seen" }]);
        let (kept, errors) = check_answer("mod:p", "mod", &MutationGrants::new(None), &mut keys, &answer, "onEffect");
        assert!(kept.is_empty());
        assert_eq!(
            errors[0].message,
            "answers to onEffect are dropped: the pack does not declare permissions.mutations"
        );
        assert_eq!(errors[0].kind, PluginErrorKind::InvalidMutation);
    }

    #[test]
    fn plugins_can_create_only_so_many_flags_and_skills() {
        let mut keys = KeyBudget::default();
        let grants = MutationGrants::all();
        let flags: Vec<Value> = (0..MAX_FLAG_KEYS_PER_PLUGIN + 1)
            .map(|i| json!({ "type": "setFlag", "flag": format!("f{i}"), "value": 1 }))
            .collect();
        let mut kept = 0;
        let mut last_error = None;
        for chunk in flags.chunks(MAX_MUTATIONS_PER_CALL) {
            let (mutations, errors) =
                check_answer("mod:p", "mod", &grants, &mut keys, &Value::Array(chunk.to_vec()), "onDayStart");
            kept += mutations.len();
            last_error = errors.last().map(|e| e.message.clone()).or(last_error);
        }
        assert_eq!(kept, MAX_FLAG_KEYS_PER_PLUGIN);
        assert_eq!(
            last_error.as_deref(),
            Some("mutation [0] dropped: setFlag: a plugin may use at most 256 different flags per session")
        );
        // Writing a flag it already created is fine.
        let again = json!([{ "type": "setFlag", "flag": "f0", "value": 2 }]);
        assert_eq!(check_answer("mod:p", "mod", &grants, &mut keys, &again, "onDayStart").0.len(), 1);

        let skills: Vec<Value> = (0..MAX_SKILL_KEYS_PER_PLUGIN + 1)
            .map(|i| json!({ "type": "grantXp", "skill": format!("s{i}"), "amount": 1 }))
            .collect();
        let (mutations, errors) = check_answer("mod:p", "mod", &grants, &mut keys, &Value::Array(skills), "onDayStart");
        assert_eq!(mutations.len(), MAX_SKILL_KEYS_PER_PLUGIN);
        assert_eq!(errors.len(), 1);
    }
}
