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
  // The goldens are the TypeScript v8 results; the F# pipeline continues to v9 (docs/NUMERICS.md),
  // which for a v8 golden is just the v8 → v9 step.
  const expected = JSON.parse(WebApi.migrateProject(golden.stable)).data;
  check(result.data !== null && WebApi.stableJson(JSON.stringify(result.data)) === WebApi.stableJson(JSON.stringify(expected)), `${name}: stable JSON differs`);
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

// Hostile input (#84, #85, #135): deep nesting is an error result, not a RangeError, and a
// number beyond the double range is refused instead of turning into null on the next save.
const deep = '{"schemaVersion":9,"a":' + "[".repeat(10000) + "]".repeat(10000) + "}";
for (const [name, call] of [["migrateProject", WebApi.migrateProject], ["problems", WebApi.problems], ["compileContent", WebApi.compileContent]]) {
  let result;
  try {
    result = JSON.parse(call(deep));
  } catch (error) {
    check(false, `${name} threw on deep nesting: ${error}`);
    continue;
  }
  check(result.ok === false && result.errors.some((e) => e.includes("Too deeply nested")), `${name}: deep nesting not reported`);
}
const huge = WebApi.createProject("starter", 0).replace(/"money":\s*\d+/, '"money":1e400');
const refused = JSON.parse(WebApi.migrateProject(huge));
check(refused.ok === false && refused.errors.some((e) => e.includes("Number out of range")), "1e400 was accepted");

if (failures > 0) {
  console.error(`${failures} check(s) failed`);
  process.exit(1);
}
console.log("Fable authoring smoke test passed");
