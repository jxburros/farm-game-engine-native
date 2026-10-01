module FarmEngine.Authoring.Tests.PackMergeTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

// ── TypeScript reference: fixtures/projects/packs-project.json ───────────────
// `packs-project.ts-reference.json` holds the TS engine's output for that project (recorded,
// never hand-edited); the Rust `packs_and_state` tests check the same file.

let private reference (key: string) : Json = Json.get key (readJson [ "Fixtures"; "packs-project.ts-reference.json" ])

let private packsProject () = projectOf (readJson [ "Fixtures"; "packs-project.json" ])

/// The TS base content, so the merge is checked on exactly the TS input.
let private tsBaseContent () = decodeJson SchemaJson.decodeGameContent (reference "base")

let private matches (label: string) (expected: Json) (actual: Json) =
    assertSameStable label (Json.stableStringify expected) (Json.stableStringify actual)

let private referenceProblems (json: Json) =
    [ for p in Json.elements "problems" json ->
          Json.toJsString (Json.get "packId" p), Json.toJsString (Json.get "severity" p), Json.toJsString (Json.get "message" p) ]

let private problemTuples (problems: PackProblem list) =
    problems |> List.map (fun p -> p.PackId, p.Severity, p.Message)

[<Fact>]
let ``base and compiled content match TypeScript`` () =
    let project = packsProject ()
    matches "base" (reference "base") (SchemaJson.encodeGameContent (ContentCompiler.baseContent project))
    matches "content" (reference "content") (SchemaJson.encodeGameContent (ContentCompiler.compile project))

[<Fact>]
let ``pack order matches TypeScript`` () =
    let packs, problems = PackMerge.resolveOrder (packsProject ()).ContentPacks
    let expected = reference "order"
    matches "order" (Json.get "packs" expected) (Encode.list SchemaJson.encodeContentPack packs)
    Assert.Equal<(string * string * string) list>(referenceProblems (Json.get "problems" expected), problemTuples problems)

[<Fact>]
let ``namespacing matches TypeScript`` () =
    let project = packsProject ()
    matches "namespacedA" (reference "namespacedA") (SchemaJson.encodeContentPack (PackRules.namespacePack project.ContentPacks.[1].Pack))
    matches "namespacedB" (reference "namespacedB") (SchemaJson.encodeContentPack (PackRules.namespacePack project.ContentPacks.[0].Pack))

[<Fact>]
let ``pack merge matches TypeScript`` () =
    let merged, problems = PackMerge.mergeIntoContent (tsBaseContent ()) (packsProject ()).ContentPacks
    let expected = reference "merged"
    matches "merged" (Json.get "content" expected) (SchemaJson.encodeGameContent merged)
    Assert.Equal<(string * string * string) list>(referenceProblems (Json.get "problems" expected), problemTuples problems)

[<Fact>]
let ``localization matches TypeScript and unknown locales change nothing`` () =
    let installs = (packsProject ()).ContentPacks
    let merged, _ = PackMerge.mergeIntoContent (tsBaseContent ()) installs
    matches "localized" (reference "localized") (SchemaJson.encodeGameContent (PackMerge.applyLocaleStrings merged installs "fr"))
    Assert.Same(merged, PackMerge.applyLocaleStrings merged installs "xx")
    Assert.Same(merged, PackMerge.applyLocaleStrings merged installs "")

[<Fact>]
let ``pack import matches TypeScript`` () =
    let project = packsProject ()
    let imported, problems = PackMerge.applyToProject project project.ContentPacks.[1].Pack
    let expected = reference "applyA"
    matches "applyA" (Json.get "project" expected) (SchemaJson.encodeGameProject imported)
    Assert.Equal<(string * string * string) list>(referenceProblems (Json.get "problems" expected), problemTuples problems)

// ── Order, overrides and conflicts ───────────────────────────────────────────

[<Fact>]
let ``pack order and merge handle overrides missing dependencies and cycles`` () =
    let baseContent = ContentCompiler.baseContent (blank ())
    let alpha =
        { ContentPack.Default with Manifest = { PackManifest.Default with Id = "alpha"; Name = "Alpha"; Version = "1.0.0" }; Content = { PackContent.Default with Items = [ item "ore" "Alpha Ore" ] } }
    let beta =
        { ContentPack.Default with Manifest = { PackManifest.Default with Id = "beta"; Name = "Beta"; Version = "1.0.0"; Dependencies = [ { PackDependency.Default with PackId = "alpha" } ]; Overrides = [ "alpha:ore" ] }; Content = { PackContent.Default with Items = [ item "alpha:ore" "Better Ore" ] } }
    let gamma =
        { ContentPack.Default with Manifest = { PackManifest.Default with Id = "gamma"; Name = "Gamma"; Version = "1.0.0"; Dependencies = [ { PackDependency.Default with PackId = "missing" } ] }; Content = { PackContent.Default with Items = [ item "alpha:ore" "Undeclared Ore" ] } }
    let delta =
        { ContentPack.Default with Manifest = { PackManifest.Default with Id = "delta"; Name = "Delta"; Version = "1.0.0"; Dependencies = [ { PackDependency.Default with PackId = "epsilon" } ] } }
    let epsilon =
        { ContentPack.Default with Manifest = { PackManifest.Default with Id = "epsilon"; Name = "Epsilon"; Version = "1.0.0"; Dependencies = [ { PackDependency.Default with PackId = "delta" } ] } }
    let installs =
        [ { PackInstallation.Default with Pack = gamma }
          { PackInstallation.Default with Pack = beta }
          { PackInstallation.Default with Pack = alpha }
          { PackInstallation.Default with Pack = delta }
          { PackInstallation.Default with Pack = epsilon } ]
    let ordered, _ = PackMerge.resolveOrder installs
    // Dependencies load before their dependents; the cycle is broken where it was found.
    Assert.Equal<string list>([ "gamma"; "alpha"; "beta"; "epsilon"; "delta" ], ordered |> List.map (fun p -> p.Manifest.Id))
    let merged, problems = PackMerge.mergeIntoContent baseContent installs
    Assert.Equal("Better Ore", merged.Items |> Seq.find (fun i -> i.Id = "alpha:ore") |> fun i -> i.Name)
    Assert.Contains(problems, fun p -> p.Message.Contains "not installed/enabled")
    Assert.Contains(problems, fun p -> p.Message.Contains "Dependency cycle")
    Assert.Contains(problems, fun p -> p.Message.Contains "without declaring")

[<Fact>]
let ``disabled packs leave content intact and report no problems`` () =
    let baseContent = ContentCompiler.baseContent (blank ())
    let disabled =
        [ { PackInstallation.Default with Enabled = false; Pack = { ContentPack.Default with Manifest = { PackManifest.Default with Id = "disabled"; Name = "Disabled"; Version = "1.0.0" }; Content = { PackContent.Default with Items = [ item "ore" "Ore" ] } } } ]
    let merged, problems = PackMerge.mergeIntoContent baseContent disabled
    Assert.Same(baseContent, merged)
    Assert.Empty problems

[<Fact>]
let ``packs without the contentInject permission load no content`` () =
    let baseContent = ContentCompiler.baseContent (blank ())
    let manifest id = { PackManifest.Default with Id = id; Name = id; Version = "1.0.0" }
    let blocked =
        { ContentPack.Default with
            Manifest = { manifest "blocked" with Permissions = { PackPermissions.Default with ContentInject = false } }
            Content = { PackContent.Default with Items = [ item "ore" "Ore" ]; Strings = [ "fr", [ "item:ore:name", "Minerai" ] ] } }
    let allowed = { ContentPack.Default with Manifest = manifest "allowed"; Content = { PackContent.Default with Items = [ item "ore" "Ore" ] } }
    let installs = [ { PackInstallation.Default with Pack = blocked }; { PackInstallation.Default with Pack = allowed } ]
    let merged, problems = PackMerge.mergeIntoContent baseContent installs
    Assert.DoesNotContain(merged.Items, fun i -> i.Id = "blocked:ore")
    Assert.Contains(merged.Items, fun i -> i.Id = "allowed:ore")
    let warning = "Pack 'blocked' ships content but does not have the contentInject permission — its content is not loaded"
    Assert.Equal<PackProblem list>([ { PackId = "blocked"; Severity = "warning"; Message = warning } ], problems)
    Assert.Same(merged, PackMerge.applyLocaleStrings merged installs "fr")
    let project = blank ()
    let imported, importProblems = PackMerge.applyToProject project blocked
    Assert.Same(project, imported)
    Assert.Equal<string list>([ warning ], importProblems |> List.map (fun p -> p.Message))
    // Plugins only: nothing to warn about.
    let pluginsOnly = { blocked with Content = PackContent.Default }
    let _, quiet = PackMerge.mergeIntoContent baseContent [ { PackInstallation.Default with Pack = pluginsOnly } ]
    Assert.Empty quiet

[<Fact>]
let ``importing the starter pack into a blank project materializes its content`` () =
    let pack = ProjectCatalog.CreateContentDefaultPack()
    let project, problems = PackMerge.applyToProject (blank ()) pack
    // The blank project already carries the built-in catalog: those ids collide and the earlier
    // definition stays (a warning each), never an error.
    Assert.All(problems, fun p ->
        Assert.Equal("warning", p.Severity)
        Assert.Contains("without declaring it in manifest.overrides", p.Message))
    let ids (items: seq<'T>) (id: 'T -> string) = items |> Seq.map id |> List.ofSeq
    Assert.Equal<string list>(ids pack.Content.Items (fun i -> i.Id), ids project.Items (fun i -> i.Id))
    Assert.Equal<string list>([ "npc-farmer"; "npc-merchant" ], ids project.Npcs (fun n -> n.Id))
    Assert.Equal<string list>([ "quest-first-harvest"; "quest-go-shopping" ], ids project.Quests (fun q -> q.Id))
    Assert.Contains(project.Shops, fun s -> s.Id = "shop-general")
    Assert.Equal(pack.Content.PlayerStart.Value.Inventory.Length, project.Player.Inventory.Length)
    Assert.Empty(errors project)

// ── Mods and locale strings through the content compiler ─────────────────────

/// Port of the C# ContentDefaultTests demo-mod case (m5 acceptance test): the demo mod installs,
/// validates and merges its crop, machine and recipe under namespaced ids.
[<Fact>]
let ``the demo mod validates merges its crop machine and recipe and namespaces ids`` () =
    let pack = validPack (readJson [ "Fixtures"; "demo-mod.json" ])
    let project = { (starter ()) with ContentPacks = [ { Pack = pack; Enabled = true } ] }
    let content = ContentCompiler.compile project
    Assert.True(content.Crops |> List.exists (fun (id, _) -> id = "demo-glow-farm:glowshroom"))
    Assert.Contains(content.MachineTypes, fun m -> m.Id = "demo-glow-farm:machine-glow-vat")
    let recipe = content.Recipes |> Seq.find (fun r -> r.Id = "demo-glow-farm:recipe-glow-jelly")
    Assert.Equal(Some "demo-glow-farm:machine-glow-vat", recipe.MachineTypeId)
    Assert.Equal("demo-glow-farm:crop-glowshroom", recipe.Inputs.[0].ItemId)
    // A reload (serialize, parse) compiles to the same content.
    let reloaded = roundTrip project
    Assert.Equal(stableOf SchemaJson.encodeGameContent content, stableOf SchemaJson.encodeGameContent (ContentCompiler.compile reloaded))

let private localizedPack () =
    validPack (
        Json.parse
            """{
              "manifest": { "id": "translations", "name": "Translations", "version": "1.0.0" },
              "content": {
                "items": [{ "id": "charm", "name": "Charm", "description": "A charm", "type": "material", "stackable": true, "maxStack": 99, "value": 5 }],
                "strings": {
                  "es": {
                    "item:charm:name": "Amuleto",
                    "item:crop-wheat:name": "Trigo",
                    "dialogue:dialogue-farmer-greeting:text": "¡Bienvenido a la granja!",
                    "quest:quest-first-harvest:name": "Primera Cosecha"
                  }
                }
              },
              "plugins": []
            }"""
        |> Result.defaultWith failwith
    )

let private localizedContent (locale: string) =
    let project = starter ()
    let project =
        { project with
            ContentPacks = [ { Pack = localizedPack (); Enabled = true } ]
            Settings = { project.Settings with Locale = locale } }
    ContentCompiler.compile project

/// Port of the C# PolishAndEcosystemTests locale cases (m7 i18n).
[<Fact>]
let ``pack string tables apply for the project locale with authored fallback`` () =
    let content = localizedContent "es"
    let itemName id = (content.Items |> Seq.find (fun i -> i.Id = id)).Name
    // The pack's own item: its key was namespaced along with the id.
    Assert.Equal("Amuleto", itemName "translations:charm")
    // A base-content reference stays fully qualified and applies directly.
    Assert.Equal("Trigo", itemName "crop-wheat")
    // Untranslated content keeps its authored text.
    Assert.Equal("Corn", itemName "crop-corn")
    // Dialogue (NPC-owned included) and quest text translate too.
    Assert.Equal("¡Bienvenido a la granja!", (content.Dialogues |> Seq.find (fun d -> d.Id = "dialogue-farmer-greeting")).Text)
    Assert.Equal("¡Bienvenido a la granja!", (content.Npcs |> Seq.find (fun n -> n.Id = "npc-farmer")).Dialogue.[0].Text)
    Assert.Equal("Primera Cosecha", (content.Quests |> Seq.find (fun q -> q.Id = "quest-first-harvest")).Name)
    // Unknown locales change nothing.
    Assert.Equal("Wheat", ((localizedContent "fr").Items |> Seq.find (fun i -> i.Id = "crop-wheat")).Name)
