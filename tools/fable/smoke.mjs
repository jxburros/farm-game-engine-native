// Checks the Fable-compiled authoring core (tools/fable/build.sh) in Node against the .NET
// results recorded in fixtures/: the sample-game cartridges byte for byte, every golden project
// migration's stable JSON, and a clean Problems list for every template.
import { readFileSync, readdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import * as WebApi from "./dist/WebApi.js";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const fixture = (...parts) => join(root, "fixtures", ...parts);
let failures = 0;
const check = (ok, message) => {
  if (!ok) {
    failures++;
    console.error("FAIL " + message);
  }
};

// Sample games: the carts farm-bench and the Rust tests load (compiled by .NET at now = 1.7e12).
for (const [file, template] of [["starter-farm", "starter"], ["cozy-garden", "cozy"], ["quest-rpg", "quest"]]) {
  const bytes = WebApi.compileCartridge(WebApi.createProject(template, 1.7e12));
  const expected = readFileSync(fixture("golden", "cartridges", file + ".cart"));
  check(Buffer.from(bytes).equals(expected), `${file}.cart differs (${bytes.length} vs ${expected.length} bytes)`);
}

// Migrations: the stable JSON of every golden project migration.
const migrations = fixture("golden", "migrations");
for (const name of readdirSync(migrations).filter((f) => /^project-v\d+\.json$/.test(f)).sort()) {
  const golden = JSON.parse(readFileSync(join(migrations, name), "utf8"));
  const result = JSON.parse(WebApi.migrateProject(JSON.stringify(golden.input)));
  check(result.ok === golden.result.ok, `${name}: ok ${result.ok}`);
  check(result.data !== null && WebApi.stableJson(JSON.stringify(result.data)) === golden.stable, `${name}: stable JSON differs`);
}

// Templates: valid projects with no errors, whose content compiles.
for (const template of ["starter", "blank", "cozy", "quest"]) {
  const project = WebApi.createProject(template, 0);
  const problems = JSON.parse(WebApi.problems(project));
  check(problems.ok && problems.data.every((p) => p.severity !== "error"), `${template}: Problems has errors`);
  check(JSON.parse(WebApi.compileContent(project)).ok, `${template}: content did not compile`);
}

// Refusals are reported, not thrown.
check(JSON.parse(WebApi.migrateProject("garbage")).ok === false, "invalid JSON was accepted");

if (failures > 0) {
  console.error(`${failures} check(s) failed`);
  process.exit(1);
}
console.log("Fable authoring smoke test passed");
