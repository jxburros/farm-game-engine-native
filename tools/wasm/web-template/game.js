// The web demo of a game made with Farming RPG Maker: farm-wasm's standalone player (title
// screen, save slots, settings) drawing into a canvas. Saves and settings live in this browser's
// localStorage, one entry per game id. See docs/EXPORT.md ("Web demo").
// tools/wasm/game-page.mjs runs this file in Node against the real module (CI).
import init, { Player, sfxSamples } from "./farm_wasm.js";

const canvas = document.getElementById("game");
const status = document.getElementById("status");
const notice = document.getElementById("notice");
const context = canvas.getContext("2d");

/** The largest frame the player draws; bigger canvases are scaled up (as on the desktop). */
const PIXEL_BUDGET = 1920 * 1200;

function show(text) {
  status.textContent = text;
  status.hidden = false;
}

/** A problem the game keeps running through, over the game until `clearNotice`. */
function warn(text) {
  notice.textContent = text;
  notice.hidden = false;
}

function clearNotice() {
  notice.hidden = true;
}

/** The engine's name for a key: letters and digits by physical key, lower-case names otherwise. */
function engineKey(event) {
  const code = event.code || "";
  if (/^Key[A-Z]$/.test(code)) return code.slice(3).toLowerCase();
  if (/^Digit[0-9]$/.test(code)) return code.slice(5);
  if (/^Numpad[0-9]$/.test(code)) return code.slice(6);
  const key = event.key;
  if (!key || key === "Unidentified" || key === "Dead") return null;
  if (key === "Spacebar") return " ";
  if (key === "Esc") return "escape";
  if (key.length === 1) return key.toLowerCase();
  const named = key.toLowerCase();
  const known = ["arrowup", "arrowdown", "arrowleft", "arrowright", "enter", "escape", "tab", "backspace",
    "delete", "insert", "home", "end", "pageup", "pagedown", "shift", "control", "alt"];
  return known.includes(named) ? named : null;
}

/** The frame size in device pixels (within the budget) and its density: frame pixels per CSS pixel. */
function frameSize() {
  const box = canvas.getBoundingClientRect();
  const ratio = window.devicePixelRatio || 1;
  const w = Math.max(1, box.width * ratio);
  const h = Math.max(1, box.height * ratio);
  const shrink = Math.min(1, Math.sqrt(PIXEL_BUDGET / (w * h)));
  const width = Math.max(1, Math.round(w * shrink));
  const height = Math.max(1, Math.round(h * shrink));
  return { width, height, density: box.width > 0 ? width / box.width : 1 };
}

/** Whether a storage write failed because the origin's quota is used up. */
function quotaExceeded(error) {
  return error?.name === "QuotaExceededError" || error?.name === "NS_ERROR_DOM_QUOTA_REACHED" || error?.code === 22;
}

async function loadCartridge() {
  const response = await fetch("game.cart");
  if (!response.ok) {
    throw new Error(`game.cart could not be loaded (HTTP ${response.status}${response.statusText ? " " + response.statusText : ""}).`);
  }
  return new Uint8Array(await response.arrayBuffer());
}

/**
 * Puts the stored saves and settings into the player. Settings that no longer parse are dropped
 * (the saves matter more); a document the player cannot read at all is copied aside first, so
 * the next save does not destroy it.
 */
function restoreStorage(player, storageKey) {
  let stored = null;
  try {
    stored = localStorage.getItem(storageKey);
  } catch {
    // Blocked storage: the game starts with fresh saves and settings.
    return;
  }
  if (!stored) return;
  try {
    player.importStorage(stored);
    return;
  } catch (error) {
    console.warn("Stored saves and settings were refused:", error);
  }
  try {
    const parsed = JSON.parse(stored);
    if (parsed && typeof parsed === "object" && parsed.settings != null) {
      player.importStorage({ ...parsed, settings: null });
      warn("Your settings could not be read and were reset; your saved games are kept.");
      return;
    }
  } catch (error) {
    console.warn("Stored saves were refused:", error);
  }
  try {
    localStorage.setItem(storageKey + ":unreadable", stored);
  } catch {
    // Nowhere to keep them: say so below anyway.
  }
  warn("Saved games in this browser could not be read (they were kept aside). New saves start fresh.");
}

async function main() {
  await init();
  const cart = await loadCartridge();
  const player = new Player(cart, {
    mode: "standalone",
    locale: navigator.language,
    reducedMotion: window.matchMedia?.("(prefers-reduced-motion: reduce)").matches || undefined,
  });
  const info = player.gameInfo();
  document.title = info.title;
  const storageKey = "farm-game:" + info.gameId;
  restoreStorage(player, storageKey);

  let pending = [];
  const send = (event) => pending.push(event);

  // Sounds are synthesized presets, rendered once per cue. Browsers start audio after a gesture.
  let audio = null;
  const buffers = new Map();
  const unlockAudio = () => {
    if (!audio && typeof AudioContext !== "undefined") audio = new AudioContext();
    if (audio && audio.state === "suspended") audio.resume();
  };
  const play = (cue, gain) => {
    if (!audio || audio.state !== "running") return;
    let buffer = buffers.get(cue);
    if (buffer === undefined) {
      const samples = sfxSamples(cue, audio.sampleRate);
      buffer = samples ? audio.createBuffer(1, samples.length, audio.sampleRate) : null;
      if (buffer) buffer.copyToChannel(samples, 0);
      buffers.set(cue, buffer);
    }
    if (!buffer) return;
    const source = new AudioBufferSourceNode(audio, { buffer });
    source.connect(new GainNode(audio, { gain })).connect(audio.destination);
    source.start();
  };

  window.addEventListener("keydown", (event) => {
    if (event.ctrlKey || event.altKey || event.metaKey) return;
    const key = engineKey(event);
    if (key === null) return;
    unlockAudio();
    send({ type: "keyDown", key, repeat: event.repeat });
    event.preventDefault();
  });
  window.addEventListener("keyup", (event) => {
    const key = engineKey(event);
    if (key !== null) send({ type: "keyUp", key });
  });
  window.addEventListener("blur", () => send({ type: "focusLost" }));
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) send({ type: "focusLost" });
  });
  // Pointer capture keeps a held button pressed while the finger slides; a browser that refuses
  // it must not stop the press.
  const capture = (element, pointerId) => {
    try {
      element.setPointerCapture?.(pointerId);
    } catch {
      // No active pointer (synthetic events) or capture unsupported.
    }
  };
  const point = (event) => {
    const box = canvas.getBoundingClientRect();
    return { x: ((event.clientX - box.left) / box.width) * canvas.width, y: ((event.clientY - box.top) / box.height) * canvas.height };
  };
  const button = (b) => (b === 2 ? "secondary" : b === 1 ? "middle" : "primary");
  canvas.addEventListener("pointermove", (event) => send({ type: "pointerMove", ...point(event) }));
  canvas.addEventListener("pointerdown", (event) => {
    unlockAudio();
    canvas.focus();
    capture(canvas, event.pointerId);
    send({ type: "pointerDown", ...point(event), button: button(event.button) });
  });
  canvas.addEventListener("pointerup", (event) => send({ type: "pointerUp", ...point(event), button: button(event.button) }));
  canvas.addEventListener("pointerleave", () => send({ type: "pointerLeft" }));
  canvas.addEventListener("pointercancel", () => send({ type: "pointerLeft" }));
  canvas.addEventListener("contextmenu", (event) => event.preventDefault());
  canvas.addEventListener("wheel", (event) => {
    const scale = event.deltaMode === 1 ? 1 : event.deltaMode === 2 ? 10 : 1 / 100;
    send({ type: "wheel", dx: -event.deltaX * scale, dy: -event.deltaY * scale });
    event.preventDefault();
  }, { passive: false });

  // Touch controls: shown on touch screens (and after the first touch), they hold game actions
  // the way keys do, whatever the player bound those keys to.
  const touch = document.getElementById("touch");
  const coarse = window.matchMedia ? window.matchMedia("(pointer: coarse)") : null;
  const showTouch = () => { touch.hidden = false; };
  if (coarse && coarse.matches) showTouch();
  coarse?.addEventListener?.("change", () => { if (coarse.matches) showTouch(); });
  window.addEventListener("pointerdown", (event) => { if (event.pointerType === "touch") showTouch(); }, { capture: true });
  const held = new Set();
  const hold = (button, pressed) => {
    const action = button.dataset.action;
    if (pressed === held.has(action)) return;
    if (pressed) held.add(action);
    else held.delete(action);
    button.classList.toggle("held", pressed);
    send({ type: "action", action, pressed });
  };
  for (const button of touch.querySelectorAll("button[data-action]")) {
    button.addEventListener("pointerdown", (event) => {
      event.preventDefault();
      unlockAudio();
      hold(button, true);
      capture(button, event.pointerId);
    });
    for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) button.addEventListener(type, () => hold(button, false));
    button.addEventListener("contextmenu", (event) => event.preventDefault());
  }
  const releaseTouch = () => {
    for (const button of touch.querySelectorAll("button.held")) hold(button, false);
  };
  window.addEventListener("blur", releaseTouch);
  document.addEventListener("visibilitychange", () => { if (document.hidden) releaseTouch(); });
  /** Frame pixels the touch controls cover at the bottom of the canvas (0 while hidden). */
  const touchInset = () => {
    if (touch.hidden) return 0;
    const box = canvas.getBoundingClientRect();
    let top = box.bottom;
    for (const group of touch.querySelectorAll(".pad, .actions")) top = Math.min(top, group.getBoundingClientRect().top);
    return box.height > 0 ? Math.max(0, box.bottom - top) * (canvas.height / box.height) : 0;
  };

  const handleRequest = (request) => {
    if (request === "fullscreen:on") document.documentElement.requestFullscreen?.().catch(() => {});
    else if (request === "fullscreen:off" && document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else if (request.startsWith("title:")) document.title = request.slice(6);
  };

  // Saves reach localStorage after the frame that made them; a failed write is shown until a
  // later one works, since the game's own "Saved" toast cannot know.
  let saveFailed = false;
  const persist = () => {
    try {
      localStorage.setItem(storageKey, player.exportStorage());
      if (saveFailed) clearNotice();
      saveFailed = false;
    } catch (error) {
      if (saveFailed) return;
      saveFailed = true;
      console.error("Saving to localStorage failed:", error);
      warn(quotaExceeded(error)
        ? "Your progress could not be saved: this site's browser storage is full. Free some space (or clear other games' data) and save again, or it is lost when the page closes."
        : "Your progress could not be saved: this browser blocks site storage. It is lost when the page closes.");
    }
  };

  let pixels;
  let last = null;
  status.hidden = true;
  canvas.focus();
  const tick = (time) => {
    const dt = last === null ? 0 : Math.min(0.1, Math.max(0, (time - last) / 1000));
    last = time;
    const size = frameSize();
    if (canvas.width !== size.width || canvas.height !== size.height) {
      canvas.width = size.width;
      canvas.height = size.height;
    }
    const events = pending;
    pending = [];
    let frame;
    try {
      frame = player.frame({
        dt,
        events,
        width: size.width,
        height: size.height,
        density: size.density,
        touchControls: !touch.hidden,
        insetBottom: touchInset(),
      }, pixels);
    } catch (error) {
      if (error && error.kind === "invalid") {
        console.error("Frame refused:", error);
        requestAnimationFrame(tick);
        return;
      }
      show("The game stopped with an error: " + (error && error.message ? error.message : error));
      return;
    }
    if (frame.pixels) {
      pixels = frame.pixels;
      context.putImageData(new ImageData(frame.pixels, frame.width, frame.height), 0, 0);
    }
    for (const sound of frame.info.sounds) play(sound.cue, sound.gain);
    for (const request of frame.info.requests) handleRequest(request);
    if (frame.storageChanged) persist();
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}

main().catch((error) => {
  console.error(error);
  show("This game could not start: " + (error && error.message ? error.message : error));
});
