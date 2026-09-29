#!/usr/bin/env node
// Smoke test of tools/wasm/build.sh's output (`--target web`), run in Node 22:
//
//   node tools/wasm/smoke.mjs [dist folder]    # default tools/wasm/dist
//
// - a Player renders frames of fixtures/projects/project-v8.json and takes input;
// - a standalone Player autosaves into its in-memory storage, and a new Player continues from
//   the exported storage with the same state hash;
// - pack plugins run in the player (QuickJS in wasmi, inside WebAssembly);
// - Session replays of the goldens in fixtures/golden/replays reproduce every step's
//   state hash and effects, the final state and the final project;
// - Preview, renderJson, hashText and the sound cues answer.
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../..");
const dist = path.resolve(process.argv[2] ?? path.join(here, "dist"));
const farm = await import(pathToFileURL(path.join(dist, "farm_wasm.js")).href);
farm.initSync({ module: readFileSync(path.join(dist, "farm_wasm_bg.wasm")) });

const fixture = (...parts) => path.join(root, "fixtures", ...parts);
const json = (...parts) => JSON.parse(readFileSync(fixture(...parts), "utf8"));

/** Stable JSON as the engine writes it: keys sorted by code unit, JavaScript numbers. */
function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value !== null && typeof value === "object") {
    const keys = Object.keys(value).sort();
    return `{${keys.map((key) => `${JSON.stringify(key)}:${stable(value[key])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}

let failures = 0;
async function test(name, body) {
  const started = performance.now();
  try {
    await body();
    console.log(`ok   ${name} (${Math.round(performance.now() - started)} ms)`);
  } catch (error) {
    failures++;
    console.error(`FAIL ${name}\n${error?.stack ?? error}`);
  }
}

const FRAME = 1 / 60;
const frame = (player, events = [], size = [320, 200], render = true, reuse = undefined) =>
  player.frame({ dt: FRAME, events, width: size[0], height: size[1], render }, reuse);
const press = (player, key) => {
  frame(player, [{ type: "keyDown", key }], [1280, 800], false);
  frame(player, [{ type: "keyUp", key }], [1280, 800], false);
  for (let i = 0; i < 2; i++) frame(player, [], [1280, 800], false);
};

await test("a player renders project-v8.json and takes input", () => {
  const project = readFileSync(fixture("projects", "project-v8.json"), "utf8");
  const player = new farm.Player(project, { seed: "wasm-smoke" });
  const first = frame(player);
  assert.equal(first.width, 320);
  assert.equal(first.height, 200);
  assert.ok(first.pixels instanceof Uint8ClampedArray);
  assert.equal(first.pixels.length, 320 * 200 * 4);
  assert.equal(first.info.screen, "playing");
  assert.equal(first.info.modal, false);
  assert.ok(first.pixels.some((value, i) => i % 4 !== 3 && value !== 0), "the frame is not black");
  for (let i = 3; i < first.pixels.length; i += 4) assert.equal(first.pixels[i], 255, "frames are opaque");

  const x = JSON.parse(player.stateJson()).player.x;
  frame(player, [{ type: "keyDown", key: "d" }]);
  for (let i = 0; i < 30; i++) frame(player, [], [320, 200], false);
  frame(player, [{ type: "keyUp", key: "d" }]);
  assert.ok(JSON.parse(player.stateJson()).player.x > x, "held D walked right");
  const stepped = frame(player, [], [8, 8], false);
  assert.equal(stepped.pixels, null, "render: false steps without pixels");
  assert.equal(stepped.width, 0);

  // Reusing the pixel array; a different size allocates a new one.
  const a = frame(player);
  const b = frame(player, [], [320, 200], true, a.pixels);
  assert.equal(b.pixels, a.pixels);
  assert.notEqual(frame(player, [], [64, 40], true, a.pixels).pixels, a.pixels);

  // The state hash is xxh3-64 over the binary state since v9 (docs/NUMERICS.md).
  assert.match(player.hash(), /^[0-9a-f]{16}$/);
  player.debug({ type: "addMoney", amount: 500 });
  const summary = JSON.parse(player.queryJson({ type: "summary" }));
  assert.equal(summary.seed, "wasm-smoke");
  player.commands([{ type: "sleep" }]);
  const synced = JSON.parse(player.syncedProject());
  assert.equal(synced.currentDay, 2);
  assert.equal(player.gameInfo().gameId, "project-1");

  // Bad input throws a FarmError and keeps the player.
  assert.throws(() => player.frame({ dt: 0, width: 0, height: 10 }), { name: "FarmError", kind: "invalid" });
  assert.throws(() => player.debug("{oops"), { name: "FarmError", kind: "invalid" });
  frame(player);

  // 1280×800 frame time.
  const started = performance.now();
  let pixels;
  for (let i = 0; i < 30; i++) pixels = frame(player, [], [1280, 800], true, pixels).pixels;
  console.log(`     1280×800 frame: ${((performance.now() - started) / 30).toFixed(1)} ms`);
  player.free();
});

await test("a cartridge player has no project to write back", () => {
  const cart = readFileSync(fixture("golden", "cartridges", "project-v8.cart"));
  const player = new farm.Player(new Uint8Array(cart));
  assert.equal(frame(player).pixels.length, 320 * 200 * 4);
  assert.throws(() => player.syncedProject(), /not started from an editor project/);
  assert.throws(() => new farm.Player("{not json"), { name: "FarmError", kind: "invalid" });
  player.free();
});

await test("standalone saves live in exported storage", () => {
  const project = JSON.stringify(json("golden", "content", "starter-farm.json").project);
  const player = new farm.Player(project, { mode: "standalone", seed: "wasm-storage" });
  const title = frame(player);
  assert.equal(title.info.screen, "title");
  assert.equal(title.storageChanged, false);
  assert.deepEqual(JSON.parse(player.exportStorage()).slots, {});

  press(player, "enter"); // New Game in the first free slot
  assert.equal(frame(player).info.screen, "playing");
  player.commands([{ type: "sleep" }]);
  let changed = false;
  for (let i = 0; i < 3; i++) changed = frame(player).storageChanged || changed;
  assert.ok(changed, "the morning autosave changed storage");
  const storage = player.exportStorage();
  const document = JSON.parse(storage);
  assert.deepEqual(Object.keys(document.slots), ["1"]);
  // A binary save: FlatBuffers with the FGSV file identifier.
  assert.equal(Buffer.from(document.slots["1"], "base64").subarray(4, 8).toString(), "FGSV");
  assert.equal(frame(player).storageChanged, false, "exporting acknowledges the change");
  const saved = JSON.parse(player.stateJson());

  // A new page load: Continue restores the autosave.
  const again = new farm.Player(project, { mode: "standalone", storage: document });
  frame(again);
  press(again, "enter");
  assert.equal(frame(again).info.screen, "playing");
  assert.equal(JSON.parse(again.stateJson()).clock.day, saved.clock.day);
  assert.equal(again.exportStorage(), storage);

  // importStorage replaces the slots of a running player.
  const empty = new farm.Player(project, { mode: "standalone" });
  empty.importStorage(storage);
  assert.equal(empty.exportStorage(), storage);
  assert.throws(() => empty.importStorage({ slots: { 7: "AAAA" } }), { kind: "invalid" });
  for (const p of [player, again, empty]) p.free();
});

await test("pack plugins run in the player", () => {
  const project = json("golden", "replays", "content-packs-and-plugins.json").project;
  const player = new farm.Player(project);
  player.commands([{ type: "sleep" }]);
  frame(player, [], [320, 200], false);
  frame(player, [], [320, 200], false);
  assert.deepEqual(JSON.parse(player.queryJson({ type: "pluginErrors" })), []);
  const toasts = JSON.parse(player.queryJson({ type: "toasts" }));
  assert.ok(toasts.some((toast) => toast.text.includes("glowshrooms hum")), JSON.stringify(toasts));
  player.free();
});

const replays = readdirSync(fixture("golden", "replays"))
  .filter((name) => name.endsWith(".json") && name !== "index.json")
  .sort();
for (const name of replays) {
  await test(`session replays golden ${name}`, () => {
    const replay = json("golden", "replays", name);
    const session = new farm.Session(replay.project, replay.seed ?? undefined, replay.autoStartQuests === true);
    assert.equal(session.hash(), replay.initialHash, "initial state");
    replay.steps.forEach((step, index) => {
      const { input } = step;
      const effects =
        input.kind === "command" ? session.apply([input.command]) : session.tick(input.ticks);
      assert.equal(session.hash(), step.hash, `state hash after step ${index} (${JSON.stringify(input)})`);
      assert.equal(stable(JSON.parse(effects)), stable(step.effects), `effects of step ${index}`);
    });
    assert.equal(session.hash(), replay.finalHash);
    assert.equal(stable(JSON.parse(session.stateJson())), stable(replay.finalState));
    assert.equal(stable(JSON.parse(session.projectJson())), stable(replay.finalProject));
    session.free();
  });
}

await test("sessions save, load and run cartridges", () => {
  const cart = readFileSync(fixture("golden", "cartridges", "project-v8.cart"));
  const replay = json("golden", "cartridges", "twenty-ticks.json");
  const session = new farm.Session(cart);
  const before = session.hash();
  const save = session.save();
  for (const input of replay.inputs) session.tick(input.ticks);
  assert.notEqual(session.hash(), before);
  assert.match(session.hash(), /^[0-9a-f]{16}$/);
  assert.throws(() => session.projectJson(), { kind: "invalid" });
  const report = JSON.parse(session.loadSave(save));
  assert.equal(report.migrated, false);
  assert.equal(session.hash(), before);
  assert.throws(() => session.loadSave(save.replace('"gameId":"', '"gameId":"other-')), /different game/);
  session.free();
});

await test("previews, render requests, hashes and sounds", () => {
  const project = json("golden", "content", "starter-farm.json").project;
  const preview = new farm.Preview(project);
  const image = preview.render({ sceneId: project.player.sceneId, camera: { x: 40, y: 30, width: 100.5, height: 60 }, scale: 2 });
  assert.equal(image.width, 201);
  assert.equal(image.height, 120);
  assert.equal(image.pixels.length, 201 * 120 * 4);
  assert.throws(() => preview.render({ sceneId: "missing" }), /Scene missing not found/);
  const empty = preview.renderVisual({ visual: { assetId: "missing" }, size: 16 });
  assert.equal(empty.width, 0);
  preview.free();

  const snapshot = farm.renderJson({ type: "editorSnapshot", project, sceneId: project.player.sceneId });
  assert.equal(typeof snapshot, "string");
  const png = farm.renderJson({ type: "rasterize", snapshot: JSON.parse(snapshot), scale: 1 });
  assert.ok(png instanceof Uint8Array);
  assert.deepEqual([...png.subarray(0, 4)], [0x89, 0x50, 0x4e, 0x47]);

  assert.equal(farm.hashText("{}"), "5465b8257807bf56");
  assert.ok(farm.sfxCues().includes("coin"));
  const samples = farm.sfxSamples("coin", 48000);
  assert.ok(samples instanceof Float32Array && samples.length > 1000);
  assert.equal(farm.sfxSamples("no-such-cue", 48000), undefined);
  assert.equal(farm.lastPanic(), undefined);
  assert.match(farm.version(), /^\d+\.\d+\.\d+/);
});

if (failures > 0) {
  console.error(`${failures} failed`);
  process.exit(1);
}
console.log("farm-wasm smoke test passed");
