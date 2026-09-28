//! Port of `tests/FarmEngine.Core.Tests/Runtime/PluginsTests.cs` and the web
//! `packages/engine-runtime/src/plugins.test.ts`, plus the WebAssembly sandbox's own
//! guarantees (hardening, budgets, determinism).

mod common;

use common::{host_with, pack, plugin, texts, DAY3};
use farm_plugins::{
    parse_mutation, plugin_specs_from_packs, to_engine_json, PluginErrorKind, PluginHost, PluginHostOptions,
    WasmPluginHost,
};
use farm_sim::schema::PluginMutation;
use serde_json::json;

const OK_PLUGIN: &str = "api.on('onDayStart', () => [{ type: 'message', text: 'ok' }])";

fn kinds(errors: &[farm_plugins::PluginError]) -> Vec<PluginErrorKind> {
    errors.iter().map(|error| error.kind).collect()
}

// --- plugins.test.ts --------------------------------------------------------------------

#[test]
fn returns_results_in_stable_spec_order_regardless_of_handler_content() {
    let specs = plugin_specs_from_packs([&pack(
        "pack-a",
        &["onDayStart"],
        vec![
            plugin("p1", r#"api.on("onDayStart", () => [{ type: "message", text: "from p1" }])"#, &[]),
            plugin("p2", r#"api.on("onDayStart", () => [{ type: "message", text: "from p2" }])"#, &[]),
        ],
    )]);
    let mut host = WasmPluginHost::new(specs, PluginHostOptions::default());
    let results = host.dispatch("onDayStart", "{}");
    let ids: Vec<&str> = results.iter().map(|r| r.plugin_id.as_str()).collect();
    assert_eq!(ids, ["pack-a:p1", "pack-a:p2"]);
}

// --- spec extraction & payloads --------------------------------------------------------

#[test]
fn grants_are_the_intersection_of_plugin_hooks_and_manifest_permissions() {
    let specs = plugin_specs_from_packs([&pack(
        "pack-a",
        &["onDayStart", "onAction"],
        vec![plugin("p", "", &["onDayStart", "onCropHarvest", "onAction"])],
    )]);
    assert_eq!(specs.len(), 1);
    assert_eq!(specs[0].id, "pack-a:p");
    assert_eq!(specs[0].pack_id, "pack-a");
    assert_eq!(specs[0].granted_hooks, ["onDayStart", "onAction"]);
}

#[test]
fn handlers_receive_the_camel_case_payload_and_only_granted_hooks_fire() {
    let specs = plugin_specs_from_packs([&pack(
        "pack-a",
        &["onDayStart"],
        vec![plugin(
            "p",
            "api.on('onDayStart', p => [{ type: 'message', text: p.season + ':' + p.day + ':' + p.year }]);\
             api.on('onCropHarvest', () => [{ type: 'message', text: 'not granted' }]);",
            &["onDayStart", "onCropHarvest"],
        )],
    )]);
    let mut host = WasmPluginHost::new(specs, PluginHostOptions::default());
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "spring:3:1");
    assert!(host.dispatch("onCropHarvest", r#"{"cropType":"wheat","quantity":1,"quality":"normal"}"#).is_empty());
}

#[test]
fn api_on_ignores_hooks_the_plugin_was_not_granted() {
    // The host never dispatches ungranted hooks; the guest's own grant check keeps a handler
    // registered under another name from being reachable either.
    let specs = plugin_specs_from_packs([&pack(
        "pack-a",
        &["onDayStart", "onAction"],
        vec![plugin(
            "p",
            "var seen = []; api.on('onAction', () => []); api.on('onCropHarvest', () => []); api.on(7, () => []);\
             api.on('onDayStart', () => [{ type: 'message', text: 'day' }]);",
            &["onDayStart"],
        )],
    )]);
    assert_eq!(specs[0].granted_hooks, ["onDayStart"]);
    let mut host = WasmPluginHost::new(specs, PluginHostOptions::default());
    assert!(host.dispatch("onAction", r#"{"actionId":"a"}"#).is_empty());
    assert!(host.dispatch("onCropHarvest", "{}").is_empty());
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "day");
}

#[test]
fn plugins_without_a_handler_or_with_non_array_results_yield_nothing() {
    let mut host = common::host(vec![
        plugin("none", "api.on('onAction', () => [{ type: 'giveMoney', amount: 5 }])", &[]),
        plugin("object", "api.on('onDayStart', () => ({ type: 'giveMoney', amount: 5 }))", &[]),
        plugin("undefined", "api.on('onDayStart', () => undefined)", &[]),
        plugin("async", "api.on('onDayStart', async () => [{ type: 'giveMoney', amount: 5 }])", &[]),
    ]);
    assert!(host.dispatch("onDayStart", DAY3).is_empty());
}

#[test]
fn all_mutation_kinds_round_trip_through_validation() {
    let mut host = common::host(vec![plugin(
        "all",
        "api.on('onDayStart', function () {
          return [
            { type: 'giveItem', itemId: 'seed-wheat', quantity: 2, extra: 'stripped' },
            { type: 'takeItem', itemId: 'seed-wheat', quantity: 1 },
            { type: 'giveMoney', amount: 10 },
            { type: 'takeMoney', amount: 3 },
            { type: 'setFlag', flag: 'f', value: 'v' },
            { type: 'message', text: 'hi' },
            { type: 'setWeather', weatherId: 'rain' },
            { type: 'modifyFriendship', npcId: 'n', delta: -5 },
            { type: 'grantXp', skill: 'farming', amount: 4 },
            { type: 'modifyEnergy', delta: -3 },
            { type: 'startQuest', questId: 'q' },
            { type: 'warpPlayer', sceneId: 's', x: 0, y: 2 },
            { type: 'startDialogue', npcId: 'n' },
            { type: 'playSound', soundId: 'coin' },
            { type: 'performAction', actionId: 'a' },
            { type: 'startMinigame', minigameId: 'fishing' },
          ];
        });",
        &[],
    )]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(results.len(), 1);
    assert!(results[0].errors.is_empty());
    assert_eq!(results[0].mutations.len(), 16);
    assert_eq!(
        to_engine_json(&results[0].mutations),
        r#"[{"type":"giveItem","itemId":"seed-wheat","quantity":2},{"type":"takeItem","itemId":"seed-wheat","quantity":1},{"type":"giveMoney","amount":10},{"type":"takeMoney","amount":3},{"type":"setFlag","flag":"f","value":"v"},{"type":"message","text":"hi"},{"type":"setWeather","weatherId":"rain"},{"type":"modifyFriendship","npcId":"n","delta":-5},{"type":"grantXp","skill":"farming","amount":4},{"type":"modifyEnergy","delta":-3},{"type":"startQuest","questId":"q"},{"type":"warpPlayer","sceneId":"s","x":0,"y":2},{"type":"startDialogue","npcId":"n"},{"type":"playSound","soundId":"coin"},{"type":"performAction","actionId":"a"},{"type":"startMinigame","minigameId":"fishing"}]"#
    );
}

// --- sandbox: invalid mutations -----------------------------------------------------------

#[test]
fn invalid_mutation_objects_are_rejected_with_errors_while_valid_ones_pass() {
    let mut host = common::host(vec![plugin(
        "bad",
        "api.on('onDayStart', function () {
          return [
            { type: 'giveMoney', amount: 1.5 },
            { type: 'giveMoney', amount: 0 },
            { type: 'giveMoney', amount: '100' },
            { type: 'giveItem', itemId: 'x', quantity: 1000 },
            { type: 'message', text: 'x'.repeat(501) },
            { type: 'setFlag', flag: 'f', value: null },
            { type: 'setFlag', flag: 'f', value: { nested: true } },
            { type: 'startDialogue', npcId: 'n', dialogueId: 7 },
            { type: 'warpPlayer', sceneId: 's', x: -1, y: 0 },
            { type: 'setState', path: 'player.money', value: 1e9 },
            { amount: 5 },
            'giveMoney',
            null,
            [1, 2],
            function () {},
            { type: 'giveMoney', amount: NaN },
            { type: 'message', text: 'ok' },
          ];
        });",
        &[],
    )]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(results.len(), 1);
    assert_eq!(results[0].mutations, [PluginMutation::Message { text: "ok".to_owned() }]);
    assert!(results[0].errors.iter().all(|e| e.kind == PluginErrorKind::InvalidMutation
        && e.plugin_id == "pack-a:bad"
        && e.hook.as_deref() == Some("onDayStart")));
    let messages: Vec<&str> = results[0].errors.iter().map(|e| e.message.as_str()).collect();
    assert_eq!(
        messages,
        [
            "mutation [0] dropped: giveMoney: amount: expected integer",
            "mutation [1] dropped: giveMoney: amount: must be >= 1",
            "mutation [2] dropped: giveMoney: amount: expected number",
            "mutation [3] dropped: giveItem: quantity: must be <= 999",
            "mutation [4] dropped: message: text: at most 500 characters",
            "mutation [5] dropped: setFlag: value: expected boolean | number | string",
            "mutation [6] dropped: setFlag: value: expected boolean | number | string",
            "mutation [7] dropped: startDialogue: dialogueId: expected string",
            "mutation [8] dropped: warpPlayer: x: must be >= 0",
            "mutation [9] dropped: invalid discriminator value (type 'setState')",
            "mutation [10] dropped: invalid discriminator value (type)",
            "mutation [11] dropped: expected object, received string",
            "mutation [12] dropped: expected object, received null",
            "mutation [13] dropped: expected object, received array",
            // Functions and NaN are not JSON: they arrive as null.
            "mutation [14] dropped: expected object, received null",
            "mutation [15] dropped: giveMoney: amount: expected number",
        ]
    );
}

#[test]
fn try_parse_mutation_mirrors_the_zod_schema() {
    let ok = |value: serde_json::Value| parse_mutation(&value).is_ok();
    assert!(ok(json!({"type":"giveItem","itemId":"a","quantity":999})));
    assert!(!ok(json!({"type":"giveItem","itemId":"a"})));
    assert!(ok(json!({"type":"modifyEnergy","delta":-1000})));
    assert!(!ok(json!({"type":"modifyEnergy","delta":-1001})));
    assert!(ok(json!({"type":"setFlag","flag":"f","value":false})));
    assert!(ok(json!({"type":"setFlag","flag":"f","value":0})));
    assert!(ok(json!({"type":"startDialogue","npcId":"n","dialogueId":"d"})));
    assert!(!ok(json!({"type":"startDialogue","npcId":"n","dialogueId":null})));
    assert!(ok(json!({"type":"message","text":""})));
    assert!(!ok(json!({"type":"MESSAGE","text":""})));
}

// --- sandbox: no host escape --------------------------------------------------------------

#[test]
fn no_access_to_host_or_web_globals() {
    let mut host = common::host(vec![plugin(
        "probe",
        "api.on('onDayStart', function () {
          var names = ['importNamespace', 'System', 'clr', 'require', 'process', 'fetch', 'XMLHttpRequest',
            'WebSocket', 'Worker', 'indexedDB', 'navigator', 'ArrayBuffer', 'SharedArrayBuffer', 'Proxy', 'host',
            'Uint8Array', 'DataView', 'Atomics', 'WeakRef', 'FinalizationRegistry', 'performance', 'self',
            'postMessage', 'importScripts', 'WebAssembly', 'std', 'os', 'print', 'scriptArgs'];
          var seen = names.filter(function (n) { return typeof globalThis[n] !== 'undefined'; });
          var ctor = (function () {}).constructor;
          return [{ type: 'message', text: 'seen=' + seen.join(',') + ';getType=' + typeof ({}).GetType + ';ctor=' + typeof ctor }];
        });",
        &[],
    )]);
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "seen=;getType=undefined;ctor=function");
}

#[test]
fn referencing_host_namespaces_throws_inside_the_sandbox_only() {
    let mut host = common::host(vec![
        plugin(
            "clr",
            "api.on('onDayStart', function () { var file = importNamespace('System.IO').File; return [{ type: 'message', text: 'escaped' }]; })",
            &[],
        ),
        plugin(
            "sys",
            "api.on('onDayStart', function () { return [{ type: 'message', text: String(System.Environment.MachineName) }]; })",
            &[],
        ),
        plugin("ok", "api.on('onDayStart', function () { return [{ type: 'message', text: 'still fine' }]; })", &[]),
    ]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(texts(&results), "still fine");
    let failed: Vec<&str> = results.iter().filter(|r| !r.errors.is_empty()).map(|r| r.plugin_id.as_str()).collect();
    assert_eq!(failed, ["pack-a:clr", "pack-a:sys"]);
    assert!(results.iter().flat_map(|r| &r.errors).all(|e| e.kind == PluginErrorKind::Threw));
}

#[test]
fn eval_and_the_function_constructor_are_disabled() {
    let mut host = common::host(vec![plugin(
        "eval",
        "api.on('onDayStart', function () {
          var out = [];
          try { eval('1 + 1'); out.push('eval ran'); } catch (e) { out.push('eval blocked'); }
          try { new Function('return 1')(); out.push('Function ran'); } catch (e) { out.push('Function blocked'); }
          try { (function () {}).constructor('return this')(); out.push('ctor ran'); } catch (e) { out.push('ctor blocked'); }
          try { (async function () {}).constructor('return 1'); out.push('async ran'); } catch (e) { out.push('async blocked'); }
          try { (function* () {}).constructor('yield 1'); out.push('generator ran'); } catch (e) { out.push('generator blocked'); }
          try { (0, eval)('1'); out.push('indirect ran'); } catch (e) { out.push('indirect blocked'); }
          try { Reflect.construct(Function, ['return 1']); out.push('reflect ran'); } catch (e) { out.push('reflect blocked'); }
          return [{ type: 'message', text: out.join(',') }];
        });",
        &[],
    )]);
    assert_eq!(
        texts(&host.dispatch("onDayStart", DAY3)),
        "eval blocked,Function blocked,ctor blocked,async blocked,generator blocked,indirect blocked,reflect blocked"
    );
}

#[test]
fn string_compilation_is_blocked_at_the_top_level_too() {
    let mut host = common::host(vec![plugin("top", "eval('api.on(\"onDayStart\", () => [])')", &[])]);
    let errors = host.init_errors().to_vec();
    assert_eq!(errors.len(), 1);
    assert_eq!(errors[0].kind, PluginErrorKind::Init);
    assert!(
        errors[0].message.starts_with("plugin pack-a:top failed to initialize — disabled: "),
        "{}",
        errors[0].message
    );
    assert!(host.dispatch("onDayStart", DAY3).is_empty());
}

#[test]
fn hardening_removes_prototype_setters_and_caps_string_amplifiers() {
    let mut host = common::host(vec![plugin(
        "hard",
        "api.on('onDayStart', function () {
          var out = [typeof Object.setPrototypeOf, typeof Reflect.setPrototypeOf,
            String(Object.getOwnPropertyDescriptor(Object.prototype, '__proto__'))];
          var o = {}; o.__proto__ = { hacked: true }; out.push(String(o.hacked));
          try { 'x'.repeat(1000001); out.push('repeat ran'); } catch (e) { out.push(e.name); }
          try { ''.padStart(1000001); out.push('padStart ran'); } catch (e) { out.push(e.name); }
          try { ''.padEnd(1000001); out.push('padEnd ran'); } catch (e) { out.push(e.name); }
          out.push(String('ab'.repeat(3)));
          out.push(typeof console.log, String(console.log('hi')), String(Object.isFrozen(console)));
          try { globalThis.fetch = function () {}; out.push('fetch replaced'); } catch (e) { out.push('fetch pinned'); }
          return [{ type: 'message', text: out.join(',') }];
        });",
        &[],
    )]);
    assert_eq!(
        texts(&host.dispatch("onDayStart", DAY3)),
        "undefined,undefined,undefined,undefined,RangeError,RangeError,RangeError,ababab,function,undefined,true,fetch pinned"
    );
}

#[test]
fn strict_mode_is_enforced() {
    let mut host = common::host(vec![plugin(
        "sloppy",
        "api.on('onDayStart', function () { leaked = 1; return [{ type: 'message', text: 'no' }]; })",
        &[],
    )]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(results.len(), 1);
    assert!(results[0].mutations.is_empty());
    assert_eq!(kinds(&results[0].errors), [PluginErrorKind::Threw]);
    assert!(results[0].errors[0].message.starts_with("threw in onDayStart: "), "{}", results[0].errors[0].message);

    // `this` is undefined in a plain call, and `with` does not parse.
    let mut host = common::host(vec![
        plugin("this", "api.on('onDayStart', function () { return [{ type: 'message', text: String(this) }]; })", &[]),
        plugin("with", "with ({}) {}", &[]),
    ]);
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "undefined");
    assert_eq!(kinds(host.init_errors()), [PluginErrorKind::Init]);
}

#[test]
fn sources_cannot_break_out_of_the_function_wrapper() {
    let breakouts = [
        // C# SourcesCannotBreakOutOfTheFunctionWrapper.
        "}); globalThis.pwned = true; (function (api) {",
        // The task's example.
        "}); evil(); (function(){",
        // Closing early with the tail swallowed by a comment.
        "}); globalThis.pwned = true; //",
        // A regular expression in one wrapper is a division in a block-statement wrapper;
        // here `)` hides inside what a block wrapper would read as a regex.
        "} /'(')/ + 1; globalThis.pwned = true; (function(){",
        // An unbalanced closing bracket of another kind.
        "}]); globalThis.pwned = true; ([function(){",
    ];
    for (index, source) in breakouts.iter().enumerate() {
        let mut host = common::host(vec![
            plugin("breakout", source, &[]),
            plugin(
                "probe",
                "api.on('onDayStart', () => [{ type: 'message', text: String(typeof globalThis.pwned) }])",
                &[],
            ),
        ]);
        let errors = host.init_errors().to_vec();
        assert_eq!(errors.len(), 1, "source {index}: {errors:?}");
        assert_eq!(errors[0].kind, PluginErrorKind::Init);
        assert_eq!(errors[0].plugin_id, "pack-a:breakout");
        assert!(errors[0].hook.is_none());
        assert_eq!(host.disabled_plugins(), ["pack-a:breakout"]);
        assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "undefined", "source {index}");
    }

    // Balanced sources with odd shapes are still function bodies.
    let fine = [
        "var o = { a: { b: [1, 2] } }; api.on('onDayStart', () => [{ type: 'message', text: 'fine' }]);",
        "api.on('onDayStart', () => [{ type: 'message', text: '})' + \"}) \" + `})${1}` + /[)}\\]]/.source }]); // })",
        "/* }) */ api.on('onDayStart', function () { return [{ type: 'message', text: 'fine' }]; }); return;",
    ];
    for source in fine {
        let mut host = common::host(vec![plugin("fine", source, &[])]);
        assert!(host.init_errors().is_empty(), "{source}: {:?}", host.init_errors());
        assert_eq!(host.dispatch("onDayStart", DAY3).len(), 1, "{source}");
    }
}

#[test]
fn a_wrapper_that_evaluates_to_something_else_is_rejected() {
    // `} + … + function (api) {` keeps both wrappers well-formed, but the result is a string.
    let host = common::host(vec![plugin("concat", "} + 'x' + function (api) {", &[])]);
    let errors = host.init_errors().to_vec();
    assert_eq!(errors.len(), 1);
    assert_eq!(
        errors[0].message,
        "plugin pack-a:concat failed to initialize — disabled: plugin source must be a function body (unbalanced braces?)"
    );
}

#[test]
fn a_throwing_initializer_disables_only_that_plugin() {
    let mut host = common::host(vec![
        plugin("boom", "throw new Error('nope')", &[]),
        plugin("syntax", "api.on('onDayStart', function ( { ", &[]),
        plugin("ok", OK_PLUGIN, &[]),
    ]);
    let ids: Vec<&str> = host.init_errors().iter().map(|e| e.plugin_id.as_str()).collect();
    assert_eq!(ids, ["pack-a:boom", "pack-a:syntax"]);
    assert_eq!(host.init_errors()[0].message, "plugin pack-a:boom failed to initialize — disabled: nope");
    assert!(host.init_errors().iter().all(|e| e.kind == PluginErrorKind::Init && e.hook.is_none()));
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "ok");
}

#[test]
fn a_plugin_cannot_mutate_another_plugins_state() {
    let mut host = common::host(vec![
        plugin(
            "a",
            "var counter = 0;
             globalThis.shared = 'a';
             Object.prototype.polluted = 'a';
             api.on('onDayStart', function () { counter++; return [{ type: 'message', text: 'a' + counter }]; });",
            &[],
        ),
        plugin(
            "b",
            "api.on('onDayStart', function () {
               var seen = [typeof counter, typeof globalThis.shared, typeof ({}).polluted];
               try { counter = 100; } catch (e) { seen.push('no counter'); }
               return [{ type: 'message', text: 'b:' + seen.join(',') }];
             });",
            &[],
        ),
    ]);
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "a1|b:undefined,undefined,undefined,no counter");
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "a2|b:undefined,undefined,undefined,no counter");
}

#[test]
fn payloads_are_copies_handlers_cannot_share_mutable_objects_with_the_host() {
    let payload = r#"{"nodeTypeId":"node-tree","drops":[{"itemId":"material-wood","quantity":2}]}"#;
    let mut host = common::host(vec![plugin(
        "m",
        "var last = null;
         api.on('onResourceGather', function (p) {
           var before = last === null ? 'first' : String(last.drops[0].quantity);
           p.drops[0].quantity = 999; p.nodeTypeId = 'x'; last = p;
           return [{ type: 'message', text: before + ':' + JSON.stringify(p).length }];
         });",
        &["onResourceGather"],
    )]);
    assert_eq!(texts(&host.dispatch("onResourceGather", payload)), "first:70");
    // The second call gets a fresh copy of the same text, not the object the handler changed.
    let second = texts(&host.dispatch("onResourceGather", payload));
    assert_eq!(second, "999:70");
    assert!(payload.contains(r#""quantity":2"#));
}

#[test]
fn payloads_that_are_not_json_are_reported_to_every_granted_plugin() {
    let mut host = common::host(vec![
        plugin("a", OK_PLUGIN, &[]),
        plugin("b", OK_PLUGIN, &[]),
        plugin("other", OK_PLUGIN, &["onAction"]),
    ]);
    let results = host.dispatch("onDayStart", "{not json");
    let ids: Vec<&str> = results.iter().map(|r| r.plugin_id.as_str()).collect();
    assert_eq!(ids, ["pack-a:a", "pack-a:b"]);
    for result in &results {
        assert!(result.mutations.is_empty());
        assert_eq!(kinds(&result.errors), [PluginErrorKind::Threw]);
        assert!(
            result.errors[0].message.starts_with("payload is not JSON-serializable: "),
            "{}",
            result.errors[0].message
        );
    }
}

#[test]
fn results_that_are_not_json_are_reported_as_throws() {
    let mut host = common::host(vec![
        plugin(
            "cyclic",
            "api.on('onDayStart', () => { var a = [{ type: 'message', text: 'x' }]; a.push(a); return a; })",
            &[],
        ),
        plugin("bigint", "api.on('onDayStart', () => [{ type: 'giveMoney', amount: 5n }])", &[]),
        // JSON.stringify gives undefined here, which reads as "no handler", as in the C# host.
        plugin("tojson", "api.on('onDayStart', () => { var a = []; a.toJSON = () => undefined; return a; })", &[]),
        plugin("ok", OK_PLUGIN, &[]),
    ]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(texts(&results), "ok");
    let errors: Vec<(&str, PluginErrorKind)> =
        results.iter().flat_map(|r| r.errors.iter().map(move |e| (r.plugin_id.as_str(), e.kind))).collect();
    assert_eq!(errors, [("pack-a:cyclic", PluginErrorKind::Threw), ("pack-a:bigint", PluginErrorKind::Threw)]);
    assert!(results[0].errors[0].message.starts_with("threw in onDayStart: "), "{}", results[0].errors[0].message);
    assert!(host.disabled_plugins().is_empty());
}

// --- sandbox: budgets ---------------------------------------------------------------------

#[test]
fn an_infinite_loop_is_stopped_by_the_fuel_budget() {
    let mut host = common::host(vec![
        plugin("loop", "api.on('onDayStart', function () { while (true) {} })", &[]),
        plugin("ok", OK_PLUGIN, &[]),
    ]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(texts(&results), "ok");
    let looping = results.iter().find(|r| r.plugin_id == "pack-a:loop").expect("loop reported");
    assert_eq!(kinds(&looping.errors), [PluginErrorKind::Timeout]);
    assert_eq!(looping.errors[0].message, "exceeded its fuel budget in onDayStart");
    assert_eq!(looping.errors[0].hook.as_deref(), Some("onDayStart"));
}

#[test]
fn three_consecutive_overruns_disable_a_plugin_and_overruns_reset_its_state() {
    let mut host = common::host(vec![plugin(
        "flaky",
        "var calls = 0;
         api.on('onDayStart', function () { calls++; if (calls >= 2) { while (true) {} } return [{ type: 'message', text: 'call ' + calls }]; });",
        &[],
    )]);
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "call 1");
    // Strike 1: the instance is rebuilt from source, so `calls` starts over.
    let strike = host.dispatch("onDayStart", DAY3);
    assert_eq!(kinds(&strike[0].errors), [PluginErrorKind::Timeout]);
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "call 1");
    assert!(host.disabled_plugins().is_empty());

    let mut looping = host_with(
        PluginHostOptions { fuel_per_call: 1_000_000, ..PluginHostOptions::default() },
        vec![plugin("loop", "api.on('onDayStart', function () { while (true) {} })", &[])],
    );
    assert_eq!(kinds(&looping.dispatch("onDayStart", DAY3)[0].errors), [PluginErrorKind::Timeout]);
    assert_eq!(kinds(&looping.dispatch("onDayStart", DAY3)[0].errors), [PluginErrorKind::Timeout]);
    let third = looping.dispatch("onDayStart", DAY3);
    assert_eq!(third.len(), 1);
    assert_eq!(kinds(&third[0].errors), [PluginErrorKind::Timeout, PluginErrorKind::Disabled]);
    assert_eq!(third[0].errors[1].message, "plugin pack-a:loop exceeded its budget 3× — disabled");
    assert_eq!(looping.disabled_plugins(), ["pack-a:loop"]);
    assert!(looping.dispatch("onDayStart", DAY3).is_empty());
}

#[test]
fn a_success_between_overruns_resets_the_strike_count() {
    let mut host = host_with(
        PluginHostOptions { fuel_per_call: 1_000_000, max_strikes: 2, ..PluginHostOptions::default() },
        vec![plugin(
            "alternating",
            "api.on('onDayStart', function (p) { if (p.day % 2 === 0) { while (true) {} } return [{ type: 'message', text: 'odd' }]; });",
            &[],
        )],
    );
    let even = r#"{"day":2,"season":"spring","year":1}"#;
    for _ in 0..5 {
        assert_eq!(kinds(&host.dispatch("onDayStart", even)[0].errors), [PluginErrorKind::Timeout]);
        assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "odd");
    }
    assert!(host.disabled_plugins().is_empty());
}

#[test]
fn a_handler_that_throws_is_never_struck() {
    let mut host =
        common::host(vec![plugin("thrower", "api.on('onDayStart', () => { throw new Error('kaput'); })", &[])]);
    for _ in 0..5 {
        let results = host.dispatch("onDayStart", DAY3);
        assert_eq!(kinds(&results[0].errors), [PluginErrorKind::Threw]);
        assert_eq!(results[0].errors[0].message, "threw in onDayStart: kaput");
    }
    assert!(host.disabled_plugins().is_empty());

    // Non-Error values are described with String(value).
    let mut host = common::host(vec![plugin("string", "api.on('onDayStart', () => { throw 'plain'; })", &[])]);
    assert_eq!(host.dispatch("onDayStart", DAY3)[0].errors[0].message, "threw in onDayStart: plain");
}

#[test]
fn deep_recursion_is_a_strike() {
    let mut host = common::host(vec![
        plugin(
            "rec",
            "function f(n) { return f(n + 1) + 1; } api.on('onDayStart', function () { return [{ type: 'message', text: String(f(0)) }]; })",
            &[],
        ),
        plugin(
            "getter",
            "var o = { get x() { return this.x; } }; api.on('onDayStart', function () { return [{ type: 'message', text: String(o.x) }]; })",
            &[],
        ),
        plugin(
            "caught",
            "function f(n) { return f(n + 1) + 1; } api.on('onDayStart', function () { try { f(0); } catch (e) {} return [{ type: 'message', text: 'survived' }]; })",
            &[],
        ),
        plugin("ok", OK_PLUGIN, &[]),
    ]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(texts(&results), "ok");
    assert_eq!(results.len(), 4);
    for result in &results[..3] {
        assert_eq!(kinds(&result.errors), [PluginErrorKind::Budget], "{}", result.plugin_id);
        assert_eq!(result.errors[0].message, "exceeded its recursion budget in onDayStart");
    }

    // Moderate recursion is fine.
    let mut host = common::host(vec![plugin(
        "fib",
        "function depth(n) { return n === 0 ? 0 : 1 + depth(n - 1); } api.on('onDayStart', () => [{ type: 'message', text: String(depth(1000)) }])",
        &[],
    )]);
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "1000");
}

#[test]
fn huge_allocations_are_stopped() {
    let mut host = common::host(vec![
        plugin("doubling", "api.on('onDayStart', function () { var s = 'x'; while (true) s += s; })", &[]),
        plugin(
            "array",
            "api.on('onDayStart', function () { var a = []; while (true) a.push({ i: a.length, pad: 'xxxxxxxxxxxxxxxx' }); })",
            &[],
        ),
        plugin("repeat", "api.on('onDayStart', function () { return [{ type: 'message', text: String('x'.repeat(5e8).length) }]; })", &[]),
        plugin("sparse", "api.on('onDayStart', function () { return [{ type: 'message', text: String(new Array(1e9).fill(0).length) }]; })", &[]),
        plugin("pad", "api.on('onDayStart', function () { return [{ type: 'message', text: String(''.padStart(1e9).length) }]; })", &[]),
        plugin("ok", OK_PLUGIN, &[]),
    ]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(texts(&results), "ok");
    // Every bomb is stopped one way or another (QuickJS builds `s += s` as a rope, so the
    // doubling one hits the string length limit before the heap limit).
    assert_eq!(results.iter().filter(|r| !r.errors.is_empty()).count(), 5);
}

#[test]
fn a_memory_bomb_is_a_strike() {
    let mut host = common::host(vec![
        plugin(
            "bomb",
            "var chunk = 'x'.repeat(1000); api.on('onDayStart', function () { var keep = []; while (true) keep.push(chunk.repeat(1000)); })",
            &[],
        ),
        plugin("ok", OK_PLUGIN, &[]),
    ]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(texts(&results), "ok");
    assert_eq!(results[0].plugin_id, "pack-a:bomb");
    assert_eq!(kinds(&results[0].errors), [PluginErrorKind::Budget]);
    assert_eq!(results[0].errors[0].message, "exceeded its memory budget in onDayStart");
    // The instance was rebuilt: the plugin still answers (and fails again).
    assert_eq!(kinds(&host.dispatch("onDayStart", DAY3)[0].errors), [PluginErrorKind::Budget]);
}

#[test]
fn a_memory_bomb_cannot_catch_its_way_past_the_heap_limit() {
    let mut host = common::host(vec![plugin(
        "stubborn",
        "var chunk = 'y'.repeat(1000);
         api.on('onDayStart', function () {
           var keep = [];
           for (var i = 0; i < 100; i++) {
             try { while (true) keep.push(chunk.repeat(1000)); } catch (e) { keep = []; }
           }
           return [{ type: 'message', text: 'escaped' }];
         });",
        &[],
    )]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(results.len(), 1);
    assert!(results[0].mutations.is_empty());
    assert_eq!(kinds(&results[0].errors), [PluginErrorKind::Budget]);
    assert_eq!(results[0].errors[0].message, "exceeded its memory budget in onDayStart");
}

#[test]
fn the_heap_limit_is_configurable() {
    let bomb = "var chunk = 'x'.repeat(1000); api.on('onDayStart', function () { var keep = []; for (var i = 0; i < 8; i++) keep.push(chunk.repeat(1000)); return [{ type: 'message', text: 'kept ' + keep.length }]; })";
    let mut roomy = host_with(PluginHostOptions::default(), vec![plugin("p", bomb, &[])]);
    assert_eq!(texts(&roomy.dispatch("onDayStart", DAY3)), "kept 8");
    let mut tight = host_with(
        PluginHostOptions { heap_limit_bytes: 4_000_000, ..PluginHostOptions::default() },
        vec![plugin("p", bomb, &[])],
    );
    assert_eq!(kinds(&tight.dispatch("onDayStart", DAY3)[0].errors), [PluginErrorKind::Budget]);
}

#[test]
fn deep_native_recursion_does_not_crash_the_host() {
    let mut host = common::host(vec![
        plugin(
            "json",
            "api.on('onDayStart', function () { var o = {}; var c = o; for (var i = 0; i < 1e6; i++) { c.a = {}; c = c.a; } return [{ type: 'message', text: JSON.stringify(o) }]; })",
            &[],
        ),
        plugin(
            "proto",
            "api.on('onDayStart', function () { var o = {}; for (var i = 0; i < 1e6; i++) o = Object.create(o); return [{ type: 'message', text: String(o.missing) }]; })",
            &[],
        ),
        plugin(
            "result",
            "api.on('onDayStart', function () { var o = []; var c = o; for (var i = 0; i < 1e6; i++) { c[0] = []; c = c[0]; } return o; })",
            &[],
        ),
        plugin(
            "json-small",
            "api.on('onDayStart', function () { var o = {}; var c = o; for (var i = 0; i < 20000; i++) { c.a = {}; c = c.a; } return [{ type: 'message', text: String(JSON.stringify(o).length) }]; })",
            &[],
        ),
        plugin("ok", OK_PLUGIN, &[]),
    ]);
    let results = host.dispatch("onDayStart", DAY3);
    assert_eq!(texts(&results), "ok");
    assert_eq!(results.iter().filter(|r| !r.errors.is_empty()).count(), 4);
    assert!(results
        .iter()
        .flat_map(|r| &r.errors)
        .all(|e| matches!(e.kind, PluginErrorKind::Budget | PluginErrorKind::Timeout | PluginErrorKind::Threw)));
}

#[test]
fn budgets_reset_between_dispatches() {
    let source = "api.on('onDayStart', function () { var n = 0; for (var i = 0; i < 10000; i++) n += i; return [{ type: 'message', text: String(n) }]; })";
    // Measure one call, then give each call a budget of three calls: three in a row all pass.
    let mut probe = common::host(vec![plugin("busy", source, &[])]);
    assert_eq!(texts(&probe.dispatch("onDayStart", DAY3)), "49995000");
    let one_call = probe.last_fuel_used("pack-a:busy").expect("fuel measured");
    let mut host = host_with(
        PluginHostOptions { fuel_per_call: one_call * 3, ..PluginHostOptions::default() },
        vec![plugin("busy", source, &[])],
    );
    for _ in 0..3 {
        assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "49995000");
    }
    // Half of one call is an overrun. (Checks sit at loop heads and function entries, so a
    // budget a few instructions short of a call can still finish it.)
    let mut tight = host_with(
        PluginHostOptions { fuel_per_call: one_call / 2, ..PluginHostOptions::default() },
        vec![plugin("busy", source, &[])],
    );
    assert_eq!(kinds(&tight.dispatch("onDayStart", DAY3)[0].errors), [PluginErrorKind::Timeout]);
}

#[test]
fn a_trivial_handler_uses_a_tiny_fraction_of_the_default_budget() {
    let mut host = common::host(vec![plugin(
        "trivial",
        "api.on('onDayStart', p => [{ type: 'message', text: 'day ' + p.day }])",
        &[],
    )]);
    let options = host.options().clone();
    let init = host.last_fuel_used("pack-a:trivial").expect("init measured");
    assert!(init * 20 < options.init_fuel, "init used {init} of {}", options.init_fuel);
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "day 3");
    let call = host.last_fuel_used("pack-a:trivial").expect("call measured");
    assert!(call * 100 < options.fuel_per_call, "call used {call} of {}", options.fuel_per_call);
    assert_eq!(host.last_fuel_used("pack-a:missing"), None);
}

#[test]
fn init_is_budgeted_too() {
    let mut host = host_with(
        PluginHostOptions { init_fuel: 20_000_000, ..PluginHostOptions::default() },
        vec![plugin("slow-start", "while (true) {}", &[]), plugin("ok", OK_PLUGIN, &[])],
    );
    let errors = host.init_errors().to_vec();
    assert_eq!(errors.len(), 1);
    assert_eq!(errors[0].message, "plugin pack-a:slow-start failed to initialize — disabled: exceeded its fuel budget");
    assert_eq!(texts(&host.dispatch("onDayStart", DAY3)), "ok");
}

#[test]
fn hosts_without_plugins_dispatch_nothing() {
    let mut host = WasmPluginHost::new(Vec::new(), PluginHostOptions::default());
    assert!(host.dispatch("onDayStart", DAY3).is_empty());
    assert!(host.init_errors().is_empty());
}

// --- determinism ----------------------------------------------------------------------------

#[test]
fn the_same_inputs_give_the_same_outputs() {
    let plugins = || {
        vec![
            plugin(
                "random",
                "var calls = 0;
                 api.on('onDayStart', function (p) {
                   calls++;
                   if (p.day === 4) { while (true) {} }
                   var r = [Math.random(), Math.random(), Date.now(), new Date().toISOString(), calls];
                   return [{ type: 'message', text: JSON.stringify(r) }, { type: 'giveMoney', amount: 1 + Math.floor(Math.random() * 100) }];
                 });",
                &[],
            ),
            plugin(
                "order",
                "api.on('onDayStart', function (p) { var o = {}; o.z = 1; o.a = 2; o[10] = 3; o[2] = 4; return [{ type: 'setFlag', flag: 'keys', value: Object.keys(o).join(',') + '|' + JSON.stringify(p) }]; })",
                &[],
            ),
        ]
    };
    let days = [3, 4, 5, 4, 6];
    let run = || {
        let mut host = common::host(plugins());
        days.iter()
            .map(|day| {
                format!("{:?}", host.dispatch("onDayStart", &format!(r#"{{"day":{day},"season":"spring","year":1}}"#)))
            })
            .collect::<Vec<_>>()
    };
    let first = run();
    assert_eq!(first, run());
    // Math.random is seeded the same way for every plugin instance; the clock reads the epoch.
    assert!(first[0].contains(r#"1970-01-01T00:00:00.000Z"#), "{}", first[0]);
    assert!(first[0].contains(",0,"), "Date.now() is 0: {}", first[0]);
    assert!(first[0].contains("keys"));
    assert!(first[0].contains(r#"2,10,z,a|{\"day\":3,\"season\":\"spring\",\"year\":1}"#), "{}", first[0]);

    // The plugin's instance is rebuilt after its day-4 overrun and starts over from the same
    // seed: the calls after it repeat the first ones exactly.
    assert!(first[1].contains("Timeout"), "{}", first[1]);
    assert_eq!(first[2].replace(r#"\"day\":5"#, r#"\"day\":3"#), first[0]);
}

#[test]
fn hosts_can_move_between_threads() {
    fn assert_send<T: Send>() {}
    assert_send::<WasmPluginHost>();
    assert_send::<farm_plugins::PluginRuntime>();

    let mut host = common::host(vec![plugin("ok", OK_PLUGIN, &[])]);
    let results = std::thread::spawn(move || host.dispatch("onDayStart", DAY3)).join().expect("thread");
    assert_eq!(texts(&results), "ok");
}

#[test]
fn error_kinds_use_the_shared_strings() {
    let all = [
        (PluginErrorKind::Init, "init"),
        (PluginErrorKind::Threw, "threw"),
        (PluginErrorKind::Timeout, "timeout"),
        (PluginErrorKind::Budget, "budget"),
        (PluginErrorKind::InvalidMutation, "invalidMutation"),
        (PluginErrorKind::Disabled, "disabled"),
    ];
    for (kind, text) in all {
        assert_eq!(kind.as_str(), text);
        assert_eq!(kind.to_string(), text);
        assert_eq!(serde_json::to_value(kind).unwrap(), json!(text));
    }
    let error = farm_plugins::PluginError {
        plugin_id: "p:q".to_owned(),
        kind: PluginErrorKind::Threw,
        message: "m".to_owned(),
        hook: Some("onAction".to_owned()),
    };
    assert_eq!(
        serde_json::to_value(&error).unwrap(),
        json!({"pluginId": "p:q", "kind": "threw", "message": "m", "hook": "onAction"})
    );
}
