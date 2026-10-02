#!/usr/bin/env node
// Regenerates THIRD-PARTY-dotnet.txt: the license notices for the NuGet packages the editor
// (src/FarmingRpgMaker.App) ships, plus the .NET runtime, the Material Design Icons and the Inter
// typeface. The app copies it to licenses/THIRD-PARTY-dotnet.txt next to the executable (the Rust
// engine library's notices are licenses/THIRD-PARTY-rust.txt, from tools/player-licenses).
//
// Reads the package graph from src/FarmingRpgMaker.App/obj/project.assets.json, so restore first.
// Every package with run-time assets is listed with its license and copyright from its .nuspec;
// license files and third-party notices inside the packages are copied in full. Run it after
// changing a PackageVersion in Directory.Packages.props and commit the result.
//
//   dotnet restore src/FarmingRpgMaker.App
//   node tools/editor-licenses/generate.mjs           # rewrite THIRD-PARTY-dotnet.txt
//   node tools/editor-licenses/generate.mjs --check   # fail if it is out of date (CI)
import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..");
const output = join(here, "THIRD-PARTY-dotnet.txt");
const assetsPath = join(root, "src", "FarmingRpgMaker.App", "obj", "project.assets.json");
const rule = "-".repeat(80);

if (!existsSync(assetsPath)) {
  console.error(`${assetsPath} is missing: run dotnet restore src/FarmingRpgMaker.App first.`);
  process.exit(1);
}

const clean = (text) =>
  text
    .replace(/^﻿/, "")
    .replace(/\r/g, "")
    .split("\n")
    .map((line) => line.replace(/\s+$/, ""))
    .join("\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
const text = (name) => clean(readFileSync(join(here, "texts", name), "utf8"));
const hash = (value) => createHash("sha256").update(value).digest("hex");
const decode = (value) =>
  value
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&");
const element = (xml, name) => {
  const match = xml.match(new RegExp(`<${name}(\\s[^>]*)?>([^<]*)</${name}>`));
  return match ? { attributes: match[1] ?? "", value: decode(match[2]).trim() } : null;
};

const assets = JSON.parse(readFileSync(assetsPath, "utf8"));
const packageFolder = Object.keys(assets.packageFolders)[0];
const [target] = Object.values(assets.targets);
const ships = (entry) =>
  ["runtime", "runtimeTargets", "native", "resource"].some((kind) =>
    Object.keys(entry[kind] ?? {}).some((path) => !path.endsWith("/_._")),
  );

// Files in a package's root that carry its license or someone else's.
const noticeFile = /^(licen[cs]e|copying|notice|third-?party-?notices)(\.(txt|md))?$/i;

const packages = [];
for (const [key, entry] of Object.entries(target)) {
  if (entry.type !== "package" || !ships(entry)) continue;
  const [name, version] = key.split("/");
  const folder = join(packageFolder, assets.libraries[key].path);
  const nuspec = readFileSync(join(folder, `${name.toLowerCase()}.nuspec`), "utf8");
  const license = element(nuspec, "license");
  const isFile = license?.attributes.includes('type="file"');
  packages.push({
    name: element(nuspec, "id")?.value ?? name,
    version,
    license: isFile ? null : (license?.value ?? "see its license file"),
    licenseFile: isFile ? license.value : null,
    copyright: element(nuspec, "copyright")?.value || `(no copyright line; authors: ${element(nuspec, "authors")?.value ?? "unknown"})`,
    files: readdirSync(folder)
      .filter((file) => noticeFile.test(file))
      .sort()
      .map((file) => ({ file, text: clean(readFileSync(join(folder, file), "utf8")) })),
  });
}
packages.sort((a, b) => a.name.localeCompare(b.name, "en"));

const standardTexts = { MIT: "MIT.txt", "MS-PL": "MS-PL.txt", "Apache-2.0": "Apache-2.0.txt" };
const out = [];
out.push(
  "Third-party software in the Farming RPG Maker editor",
  "====================================================",
  "",
  "Farming RPG Maker is free software under the MIT License (LICENSE in the",
  "source repository). The editor is a .NET application built on the open-source",
  "NuGet packages listed below; their licenses follow. It also includes the .NET",
  "runtime, icons from Material Design Icons and the Inter typeface, whose licenses",
  "are at the end of this file. The Rust engine library (farm_ffi) and the game",
  "player templates have their own notices in THIRD-PARTY-rust.txt.",
  "",
);
for (const p of packages) {
  out.push(`- ${p.name} ${p.version} (${p.license ?? `license file ${p.licenseFile}`})`);
}

// One section per SPDX license, naming each package and its copyright line.
const byLicense = new Map();
for (const p of packages.filter((p) => p.license)) {
  if (!byLicense.has(p.license)) byLicense.set(p.license, []);
  byLicense.get(p.license).push(p);
}
for (const [license, users] of [...byLicense].sort(([a], [b]) => a.localeCompare(b, "en"))) {
  if (!standardTexts[license]) {
    console.error(`No license text for ${license} (${users.map((p) => p.name).join(", ")}): add one to tools/editor-licenses/texts.`);
    process.exit(1);
  }
  out.push("", rule, `${license}`, "", "Used by:");
  for (const p of users) out.push(`- ${p.name} ${p.version}: ${p.copyright}`);
  out.push("", text(standardTexts[license]));
}

// License files and notices shipped inside the packages, each text once.
const seen = new Map();
for (const p of packages) {
  for (const { file, text: body } of p.files) {
    const id = hash(body);
    if (!seen.has(id)) seen.set(id, { body, users: [] });
    seen.get(id).users.push(`${p.name} ${p.version} (${file})`);
  }
}
for (const { body, users } of seen.values()) {
  out.push("", rule, "Included in the packages:", ...users.map((user) => `- ${user}`), "", body);
}

// Not NuGet license metadata, but shipped with the editor.
out.push(
  "",
  rule,
  "MIT",
  "",
  "Used by:",
  "- the .NET runtime (self-contained in the installed app): Copyright (c) .NET Foundation and Contributors",
  "  Its own third-party notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT",
  "",
  text("MIT.txt"),
  "",
  rule,
  "Apache-2.0",
  "",
  "Used by:",
  "- Material Design Icons (Pictogrammers, https://pictogrammers.com/library/mdi/): the",
  "  editor's toolbar and status icons (src/FarmingRpgMaker.App/App.axaml)",
  "",
  text("Apache-2.0.txt"),
  "",
  rule,
  "OFL-1.1",
  "",
  "Used by:",
  "- the Inter typeface, embedded by Avalonia.Fonts.Inter",
  "",
  clean(readFileSync(join(root, "assets", "fonts", "OFL.txt"), "utf8")),
  "",
);
const generated = out.join("\n");

if (process.argv.includes("--check")) {
  const current = existsSync(output) ? readFileSync(output, "utf8") : "";
  if (current !== generated) {
    console.error("tools/editor-licenses/THIRD-PARTY-dotnet.txt is out of date; run node tools/editor-licenses/generate.mjs");
    process.exit(1);
  }
  console.log("THIRD-PARTY-dotnet.txt is up to date.");
} else {
  writeFileSync(output, generated);
  console.log(`Wrote ${output} (${packages.length} packages)`);
}
