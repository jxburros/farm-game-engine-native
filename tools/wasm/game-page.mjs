#!/usr/bin/env node
// Runs the web demo's page script (tools/wasm/web-template/game.js) in Node 22 against the real
// module of tools/wasm/build.sh, with a small stand-in for the browser (elements, events,
// localStorage, fetch, requestAnimationFrame, WebAudio):
//
//   node tools/wasm/game-page.mjs [dist folder]    # default tools/wasm/dist
//
// - the page starts, sizes its frames by devicePixelRatio and tells the player its density and
//   touch controls; keys and the touch D-pad walk (a refused pointer capture never stops a press);
// - saves reach localStorage and a reload continues from them; a full storage shows a notice
//   until a later save works; unreadable settings are dropped and the saves kept, and an
//   unreadable document is kept aside;
// - a missing game.cart says so.
//
// Also checks index.html: no inline scripts, styles or handlers (its Content-Security-Policy
// allows neither), and only same-origin resources.
import assert from "node:assert/strict";
import { copyFileSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "../..");
const dist = path.resolve(process.argv[2] ?? path.join(here, "dist"));
const template = path.join(here, "web-template");
const cartridge = readFileSync(path.join(root, "fixtures", "golden", "cartridges", "project-v8.cart"));

// The page's folder: the template's script next to the built module.
const page = mkdtempSync(path.join(tmpdir(), "farm-web-page-"));
process.on("exit", () => rmSync(page, { recursive: true, force: true }));
copyFileSync(path.join(template, "game.js"), path.join(page, "game.js"));
for (const name of ["farm_wasm.js", "farm_wasm_bg.wasm"]) copyFileSync(path.join(dist, name), path.join(page, name));
const farm = await import(pathToFileURL(path.join(page, "farm_wasm.js")).href);

// ── A browser, just enough for game.js ─────────────────────────────────────────────────────────

class Target {
  listeners = [];
  addEventListener(type, listener, options) {
    this.listeners.push({ type, listener, capture: options === true || options?.capture === true });
  }
  removeEventListener() {}
  /** Fires `type` (capturing listeners first, as a window sees them); returns the event. */
  dispatch(type, init = {}) {
    const event = { type, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, target: this, ...init };
    const matching = this.listeners.filter((entry) => entry.type === type);
    for (const entry of [...matching.filter((e) => e.capture), ...matching.filter((e) => !e.capture)]) entry.listener(event);
    return event;
  }
}

class ClassList {
  names = new Set();
  constructor(names = []) { for (const name of names) this.names.add(name); }
  contains(name) { return this.names.has(name); }
  add(name) { this.names.add(name); }
  remove(name) { this.names.delete(name); }
  toggle(name, force = !this.names.has(name)) {
    if (force) this.names.add(name);
    else this.names.delete(name);
    return force;
  }
}

const rect = (left, top, width, height) => ({ left, top, width, height, x: left, y: top, right: left + width, bottom: top + height });

class Element extends Target {
  hidden = false;
  textContent = "";
  dataset = {};
  children = [];
  box = rect(0, 0, 0, 0);
  /** A browser that refuses pointer capture (the regression of commit cbfb03a). */
  refuseCapture = false;
  captured = [];
  constructor(classes = []) {
    super();
    this.classList = new ClassList(classes);
  }
  getBoundingClientRect() { return this.box; }
  focus() {}
  setPointerCapture(id) {
    if (this.refuseCapture) throw Object.assign(new Error("No active pointer with the given id"), { name: "NotFoundError" });
    this.captured.push(id);
  }
  /** The selectors game.js uses: `button[data-action]`, `button.held`, `.pad, .actions`. */
  querySelectorAll(selector) {
    const all = this.children.flatMap((child) => [child, ...child.children]);
    if (selector === "button[data-action]") return all.filter((e) => e.dataset.action);
    if (selector === "button.held") return all.filter((e) => e.dataset.action && e.classList.contains("held"));
    if (selector === ".pad, .actions") return all.filter((e) => e.classList.contains("pad") || e.classList.contains("actions"));
    throw new Error(`selector ${selector} is not stubbed`);
  }
}

class ImageData {
  constructor(data, width, height) {
    assert.equal(data.length, width * height * 4, "ImageData size");
    Object.assign(this, { data, width, height });
  }
}

class MemoryStorage {
  items = new Map();
  /** Set to an Error to make every write throw it. */
  failWith = null;
  writes = 0;
  getItem(key) { return this.items.has(key) ? this.items.get(key) : null; }
  setItem(key, value) {
    if (this.failWith) throw this.failWith;
    this.writes++;
    this.items.set(key, String(value));
  }
  removeItem(key) { this.items.delete(key); }
}

const quotaError = () => Object.assign(new Error("The quota has been exceeded."), { name: "QuotaExceededError", code: 22 });

const audio = { buffers: 0, played: 0 };
class AudioContext {
  state = "running";
  sampleRate = 48000;
  destination = {};
  constructor() { audio.contexts = (audio.contexts ?? 0) + 1; }
  resume() { this.state = "running"; return Promise.resolve(); }
  createBuffer(channels, length) {
    audio.buffers++;
    return { length, copyToChannel(samples) { assert.ok(samples instanceof Float32Array); } };
  }
}
class GainNode { constructor(context, { gain }) { this.gain = gain; } connect(next) { return next; } }
class AudioBufferSourceNode { constructor(context, { buffer }) { this.buffer = buffer; } connect(next) { return next; } start() { audio.played++; } }

const storage = new MemoryStorage();
const define = (name, value) => Object.defineProperty(globalThis, name, { value, configurable: true, writable: true });
define("localStorage", storage);
define("ImageData", ImageData);
define("AudioContext", AudioContext);
define("GainNode", GainNode);
define("AudioBufferSourceNode", AudioBufferSourceNode);

/** The page and the player it made, for one load of game.js. */
let current;
let loads = 0;

/** Every Player the page makes, and each of its frame requests and results. */
const originalFrame = farm.Player.prototype.frame;
farm.Player.prototype.frame = function (request, reuse) {
  const result = originalFrame.call(this, request, reuse);
  if (current) {
    current.player = this;
    current.requests.push(request);
    current.results.push(result);
  }
  return result;
};

/**
 * Loads the page: a canvas of `css` CSS pixels at `ratio` devicePixelRatio, touch controls
 * along the bottom, `cart` as the fetch answer for game.cart. Resolves once the first frame is
 * scheduled (or the page reported a failure).
 */
async function load({ css = [1280, 800], ratio = 1, coarse = false, cart = { status: 200, body: cartridge } } = {}) {
  const canvas = new Element();
  canvas.box = rect(0, 0, css[0], css[1]);
  canvas.width = 300;
  canvas.height = 150;
  const drawn = [];
  canvas.getContext = () => ({ putImageData: (image, x, y) => drawn.push({ image, x, y }) });
  const status = new Element();
  status.textContent = "Loading…";
  const notice = new Element();
  notice.hidden = true;
  const touch = new Element();
  touch.hidden = true;
  // The D-pad bottom left, the action column bottom right: 160 CSS pixels tall.
  const pad = new Element(["pad"]);
  pad.box = rect(16, css[1] - 176, 160, 160);
  const actions = new Element(["actions"]);
  actions.box = rect(css[0] - 196, css[1] - 176, 180, 160);
  const button = (parent, action) => {
    const element = new Element();
    element.dataset.action = action;
    parent.children.push(element);
    return element;
  };
  const buttons = Object.fromEntries(
    [[pad, ["move-up", "move-left", "move-right", "move-down"]], [actions, ["menu", "inventory", "sleep", "interact"]]]
      .flatMap(([parent, names]) => names.map((name) => [name, button(parent, name)])),
  );
  touch.children.push(pad, actions);

  const elements = { game: canvas, status, notice, touch };
  const document = new Target();
  Object.assign(document, {
    hidden: false,
    title: "{{TITLE}}",
    fullscreenElement: null,
    documentElement: { requestFullscreen: () => Promise.resolve() },
    getElementById: (id) => elements[id] ?? null,
  });
  const window = new Target();
  Object.assign(window, {
    devicePixelRatio: ratio,
    matchMedia: (query) => ({ matches: query === "(pointer: coarse)" ? coarse : false, addEventListener() {} }),
  });
  const frames = [];
  define("document", document);
  define("window", window);
  define("requestAnimationFrame", (callback) => frames.push(callback));
  define("fetch", async (url) => {
    const name = String(url).split("/").pop();
    if (name === "game.cart") return new Response(cart.body ?? "Not found", { status: cart.status, statusText: cart.statusText ?? "" });
    if (name === "farm_wasm_bg.wasm") {
      return new Response(readFileSync(path.join(page, name)), { headers: { "Content-Type": "application/wasm" } });
    }
    return new Response("Not found", { status: 404 });
  });

  current = { canvas, status, notice, touch, buttons, document, window, frames, drawn, requests: [], results: [], player: null, time: 0 };
  await import(`${pathToFileURL(path.join(page, "game.js")).href}?load=${++loads}`);
  // Until the first frame is scheduled, or the page shows why it could not start.
  for (let i = 0; i < 4000 && frames.length === 0 && status.textContent === "Loading…"; i++) {
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  return current;
}

/** Runs `count` animation frames of 1/60 s. */
function run(page, count = 1) {
  for (let i = 0; i < count; i++) {
    const callbacks = page.frames.splice(0);
    assert.ok(callbacks.length > 0, "the page scheduled a frame");
    page.time += 1000 / 60;
    for (const callback of callbacks) callback(page.time);
  }
  return page.results.at(-1);
}

const key = (page, type, code, keyName) => page.window.dispatch(type, { code, key: keyName, repeat: false });
function press(page, code, keyName) {
  key(page, "keydown", code, keyName);
  run(page, 1);
  key(page, "keyup", code, keyName);
  run(page, 3);
}
const state = (page) => JSON.parse(page.player.stateJson());

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

/** Runs `body` with console.warn/error collected instead of printed. */
async function quietly(body) {
  const { warn, error } = console;
  const messages = [];
  console.warn = console.error = (...args) => messages.push(args.map(String).join(" "));
  try {
    await body();
  } finally {
    Object.assign(console, { warn, error });
  }
  return messages;
}

// ── The page ───────────────────────────────────────────────────────────────────────────────────

await test("index.html has no inline code or styles and only same-origin resources", () => {
  const html = readFileSync(path.join(template, "index.html"), "utf8");
  const policy = html.match(/<meta http-equiv="Content-Security-Policy" content="([^"]+)">/)?.[1];
  assert.ok(policy, "the page sets a Content-Security-Policy");
  for (const directive of ["default-src 'none'", "script-src 'self' 'wasm-unsafe-eval'", "style-src 'self'", "base-uri 'none'"]) {
    assert.ok(policy.includes(directive), directive);
  }
  assert.ok(!policy.includes("unsafe-inline"), "no inline styles or scripts are allowed");
  assert.doesNotMatch(html, /<style|style="|\son[a-z]+=/i, "inline styles and handlers would be blocked");
  for (const [, body] of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g)) assert.equal(body.trim(), "", "inline scripts would be blocked");
  for (const [, url] of html.matchAll(/(?:src|href)="([^"]+)"/g)) assert.doesNotMatch(url, /^(?:[a-z]+:|\/\/)/i, `${url} is same-origin`);
  for (const id of ["game", "status", "notice", "touch"]) assert.match(html, new RegExp(`id="${id}"`), `game.js needs #${id}`);
});

await test("the page plays: frames sized by density, keys, touch controls and sound", async () => {
  storage.items.clear();
  // A 390×844 phone at devicePixelRatio 2.
  const phone = await load({ css: [390, 844], ratio: 2, coarse: true });
  assert.equal(phone.status.hidden, true, phone.status.textContent);
  assert.equal(phone.touch.hidden, false, "touch controls show on a coarse pointer");
  let frame = run(phone, 2);
  assert.deepEqual([phone.canvas.width, phone.canvas.height], [780, 1688]);
  assert.equal(frame.width, 780);
  assert.ok(phone.drawn.at(-1).image instanceof ImageData);
  const request = phone.requests.at(-1);
  assert.equal(request.density, 2);
  assert.equal(request.touchControls, true);
  assert.equal(request.insetBottom, 176 * 2, "the touch controls' height in frame pixels");
  assert.equal(frame.info.screen, "title");

  // Enter on the title screen: New Game (and the first gesture starts the audio).
  press(phone, "Enter", "Enter");
  assert.equal(run(phone, 2).info.screen, "playing");
  assert.ok(audio.contexts >= 1 && audio.played >= 1, "the menu sound played");
  const x = state(phone).player.x;
  key(phone, "keydown", "KeyD", "d");
  run(phone, 30);
  key(phone, "keyup", "KeyD", "d");
  run(phone, 2);
  assert.ok(state(phone).player.x > x, "held D walked right");

  // The touch D-pad, on a browser that refuses pointer capture.
  const left = phone.buttons["move-left"];
  left.refuseCapture = true;
  const right = state(phone).player.x;
  assert.ok(left.dispatch("pointerdown", { pointerId: 1, pointerType: "touch" }).defaultPrevented);
  assert.ok(left.classList.contains("held"));
  run(phone, 20);
  left.dispatch("pointerup", { pointerId: 1 });
  run(phone, 2);
  assert.equal(left.classList.contains("held"), false);
  assert.ok(state(phone).player.x < right, "the touch D-pad walked left");
  // Losing focus lets go of a held control.
  phone.buttons["move-up"].dispatch("pointerdown", { pointerId: 2 });
  phone.window.dispatch("blur");
  assert.equal(phone.buttons["move-up"].classList.contains("held"), false);
  run(phone, 1);

  // A desktop window: no touch controls until a touch.
  const desktop = await load({ css: [1366, 768] });
  frame = run(desktop, 1);
  assert.equal(desktop.touch.hidden, true);
  assert.deepEqual([frame.width, frame.height], [1366, 768]);
  assert.deepEqual([desktop.requests.at(-1).touchControls, desktop.requests.at(-1).insetBottom], [false, 0]);
  desktop.window.dispatch("pointerdown", { pointerType: "touch" });
  assert.equal(desktop.touch.hidden, false, "the first touch shows the touch controls");
  // A huge 4K canvas is drawn within the pixel budget, at the matching density.
  const big = await load({ css: [3840, 2160], ratio: 2 });
  frame = run(big, 1);
  assert.ok(frame.width * frame.height <= 1920 * 1200, `${frame.width}×${frame.height}`);
  assert.ok(Math.abs(big.requests.at(-1).density - frame.width / 3840) < 1e-9);
});

await test("saves reach localStorage and a reload continues from them", async () => {
  storage.items.clear();
  const first = await load();
  run(first, 1);
  press(first, "Enter", "Enter");
  first.player.commands([{ type: "sleep" }]);
  run(first, 3);
  const key = [...storage.items.keys()].find((name) => name.startsWith("farm-game:"));
  assert.ok(key, "the morning autosave was written");
  const document = JSON.parse(storage.getItem(key));
  assert.deepEqual(Object.keys(document.slots), ["1"]);
  const day = state(first).clock.day;

  const again = await load();
  run(again, 1);
  press(again, "Enter", "Enter"); // Continue
  assert.equal(run(again, 1).info.screen, "playing");
  assert.equal(state(again).clock.day, day);
});

await test("a full storage shows a notice until a later save works", async () => {
  const page = await load();
  run(page, 1);
  press(page, "Enter", "Enter");
  storage.failWith = quotaError();
  const messages = await quietly(() => {
    page.player.commands([{ type: "sleep" }]);
    run(page, 3);
  });
  assert.equal(page.notice.hidden, false);
  assert.match(page.notice.textContent, /storage is full/);
  assert.ok(messages.some((message) => message.includes("quota")), messages.join("\n"));
  storage.failWith = null;
  page.player.commands([{ type: "sleep" }]);
  run(page, 3);
  assert.equal(page.notice.hidden, true, "a save that worked clears the notice");
});

await test("unreadable stored settings are dropped and the saves kept", async () => {
  const key = [...storage.items.keys()].find((name) => name.startsWith("farm-game:"));
  const document = JSON.parse(storage.getItem(key));
  storage.setItem(key, JSON.stringify({ ...document, settings: "[audio\nmaster = " }));
  let page;
  await quietly(async () => {
    page = await load();
  });
  assert.equal(page.status.hidden, true);
  assert.match(page.notice.textContent, /settings could not be read/);
  run(page, 1);
  press(page, "Enter", "Enter"); // Continue: the save is still there
  assert.equal(run(page, 1).info.screen, "playing");
  assert.equal(JSON.parse(page.player.exportStorage()).slots["1"], document.slots["1"]);

  // A document that cannot be read at all is copied aside before anything overwrites it.
  storage.setItem(key, "{not json");
  await quietly(async () => {
    page = await load();
  });
  assert.equal(storage.getItem(key + ":unreadable"), "{not json");
  assert.match(page.notice.textContent, /could not be read/);
  run(page, 1);
  assert.equal(page.results.at(-1).info.screen, "title");
});

await test("a missing game.cart says so", async () => {
  let page;
  await quietly(async () => {
    page = await load({ cart: { status: 404, statusText: "Not Found" } });
  });
  assert.equal(page.status.hidden, false);
  assert.match(page.status.textContent, /game\.cart could not be loaded \(HTTP 404 Not Found\)/);
  assert.equal(page.frames.length, 0, "nothing runs");
});

if (failures > 0) {
  console.error(`${failures} failed`);
  process.exit(1);
}
console.log("web demo page test passed");
