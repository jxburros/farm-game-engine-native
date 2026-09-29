// The web demo of a game made with Farming RPG Maker: farm-wasm's standalone player (title
// screen, save slots, settings) drawing into a canvas. Saves and settings live in this browser's
// localStorage, one entry per game id. See docs/EXPORT.md ("Web demo").
import init, { Player, sfxSamples } from "./farm_wasm.js";

const canvas = document.getElementById("game");
const status = document.getElementById("status");
const context = canvas.getContext("2d");

/** The largest frame the player draws; bigger canvases are scaled up (as on the desktop). */
const PIXEL_BUDGET = 1920 * 1200;

function show(text) {
  status.textContent = text;
  status.hidden = false;
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

function frameSize() {
  const box = canvas.getBoundingClientRect();
  const ratio = window.devicePixelRatio || 1;
  const w = Math.max(1, box.width * ratio);
  const h = Math.max(1, box.height * ratio);
  const shrink = Math.min(1, Math.sqrt(PIXEL_BUDGET / (w * h)));
  return { width: Math.max(1, Math.round(w * shrink)), height: Math.max(1, Math.round(h * shrink)) };
}

async function main() {
  await init();
  const cart = new Uint8Array(await (await fetch("game.cart")).arrayBuffer());
  const player = new Player(cart, {
    mode: "standalone",
    locale: navigator.language,
    reducedMotion: window.matchMedia("(prefers-reduced-motion: reduce)").matches || undefined,
  });
  const info = player.gameInfo();
  document.title = info.title;
  const storageKey = "farm-game:" + info.gameId;
  try {
    const stored = localStorage.getItem(storageKey);
    if (stored) player.importStorage(stored);
  } catch {
    // Unreadable or blocked storage: the game starts with fresh saves and settings.
  }

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
  const point = (event) => {
    const box = canvas.getBoundingClientRect();
    return { x: ((event.clientX - box.left) / box.width) * canvas.width, y: ((event.clientY - box.top) / box.height) * canvas.height };
  };
  const button = (b) => (b === 2 ? "secondary" : b === 1 ? "middle" : "primary");
  canvas.addEventListener("pointermove", (event) => send({ type: "pointerMove", ...point(event) }));
  canvas.addEventListener("pointerdown", (event) => {
    unlockAudio();
    canvas.focus();
    canvas.setPointerCapture?.(event.pointerId);
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

  const handleRequest = (request) => {
    if (request === "fullscreen:on") document.documentElement.requestFullscreen?.().catch(() => {});
    else if (request === "fullscreen:off" && document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else if (request.startsWith("title:")) document.title = request.slice(6);
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
      frame = player.frame({ dt, events, width: size.width, height: size.height }, pixels);
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
    if (frame.storageChanged) {
      try {
        localStorage.setItem(storageKey, player.exportStorage());
      } catch {
        // Storage full or blocked: this session's saves last until the page closes.
      }
    }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}

main().catch((error) => {
  console.error(error);
  show("This game could not start: " + (error && error.message ? error.message : error));
});
