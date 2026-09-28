module FarmEngine.Authoring.Tests.PackMergeTests

open System.IO
open System.Text.Json
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Json
open FarmEngine.Schemas

// ── TypeScript reference: fixtures/projects/packs-project.json ───────────────
// `packs-project.ts-reference.json` holds the TS engine's output for that project (recorded,
// never hand-edited); the Rust `packs_and_state` tests check the same file.

let private reference (key: string) : JsonElement =
    (readElement [ "Fixtures"; "packs-project.ts-reference.json" ]).GetProperty key

let private packsProject () =
    JsonSerializer.Deserialize<GameProject>(File.ReadAllText(testFile [ "Fixtures"; "packs-project.json" ]), JsonDefaults.Options)

/// The TS base content, so the merge is checked on exactly the TS input.
let private tsBaseContent () = (reference "base").Deserialize<GameContent>(JsonDefaults.Options)

let private stableOf (element: JsonElement) = StableJson.Stringify(element: JsonElement)

let private matches (label: string) (expected: JsonElement) (actual: 'T) =
    assertSameStable label (stableOf expected) (StableJson.Stringify<'T> actual)

let private referenceProblems (element: JsonElement) =
    [ for p in element.EnumerateArray() ->
          p.GetProperty("packId").GetString(), p.GetProperty("severity").GetString(), p.GetProperty("message").GetString() ]

let private problemTuples (problems: PackProblem list) =
    problems |> List.map (fun p -> p.PackId, p.Severity, p.Message)

[<Fact>]
let ``base and compiled content match TypeScript`` () =
    let project = packsProject ()
    matches "base" (reference "base") (ContentCompiler.baseContent project)
    matches "content" (reference "content") (ContentCompiler.compile project)

[<Fact>]
let ``pack order matches TypeScript`` () =
    let packs, problems = PackMerge.resolveOrder (packsProject ()).ContentPacks
    let expected = reference "order"
    matches "order" (expected.GetProperty "packs") packs
    Assert.Equal<(string * string * string) list>(referenceProblems (expected.GetProperty "problems"), problemTuples problems)

[<Fact>]
let ``namespacing matches TypeScript`` () =
    let project = packsProject ()
    matches "namespacedA" (reference "namespacedA") (PackRules.namespacePack project.ContentPacks.[1].Pack)
    matches "namespacedB" (reference "namespacedB") (PackRules.namespacePack project.ContentPacks.[0].Pack)

[<Fact>]
let ``pack merge matches TypeScript`` () =
    let merged, problems = PackMerge.mergeIntoContent (tsBaseContent ()) (packsProject ()).ContentPacks
    let expected = reference "merged"
    matches "merged" (expected.GetProperty "content") merged
    Assert.Equal<(string * string * string) list>(referenceProblems (expected.GetProperty "problems"), problemTuples problems)

[<Fact>]
let ``localization matches TypeScript and unknown locales change nothing`` () =
    let installs = (packsProject ()).ContentPacks
    let merged, _ = PackMerge.mergeIntoContent (tsBaseContent ()) installs
    matches "localized" (reference "localized") (PackMerge.applyLocaleStrings merged installs "fr")
    Assert.Same(merged, PackMerge.applyLocaleStrings merged installs "xx")
    Assert.Same(merged, PackMerge.applyLocaleStrings merged installs "")

[<Fact>]
let ``pack import matches TypeScript`` () =
    let project = packsProject ()
    let imported, problems = PackMerge.applyToProject project project.ContentPacks.[1].Pack
    let expected = reference "applyA"
    matches "applyA" (expected.GetProperty "project") imported
    Assert.Equal<(string * string * string) list>(referenceProblems (expected.GetProperty "problems"), problemTuples problems)

// ── Order, overrides and conflicts ───────────────────────────────────────────

[<Fact>]
let ``pack order and merge handle overrides missing dependencies and cycles`` () =
    let baseContent = ContentCompiler.baseContent (blank ())
    let alpha =
        ContentPack(
            Manifest = PackManifest(Id = "alpha", Name = "Alpha", Version = "1.0.0"),
            Content = PackContent(Items = listOf [ item "ore" "Alpha Ore" ]))
    let beta =
        ContentPack(
            Manifest = PackManifest(Id = "beta", Name = "Beta", Version = "1.0.0",
                Dependencies = listOf [ PackDependency(PackId = "alpha") ],
                Overrides = listOf [ "alpha:ore" ]),
            Content = PackContent(Items = listOf [ item "alpha:ore" "Better Ore" ]))
    let gamma =
        ContentPack(
            Manifest = PackManifest(Id = "gamma", Name = "Gamma", Version = "1.0.0",
                Dependencies = listOf [ PackDependency(PackId = "missing") ]),
            Content = PackContent(Items = listOf [ item "alpha:ore" "Undeclared Ore" ]))
    let delta =
        ContentPack(
            Manifest = PackManifest(Id = "delta", Name = "Delta", Version = "1.0.0",
                Dependencies = listOf [ PackDependency(PackId = "epsilon") ]))
    let epsilon =
        ContentPack(
            Manifest = PackManifest(Id = "epsilon", Name = "Epsilon", Version = "1.0.0",
                Dependencies = listOf [ PackDependency(PackId = "delta") ]))
    let installs =
        listOf [ PackInstallation(Pack = gamma)
                 PackInstallation(Pack = beta)
                 PackInstallation(Pack = alpha)
                 PackInstallation(Pack = delta)
                 PackInstallation(Pack = epsilon) ]
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
        listOf [ PackInstallation(
                    Enabled = false,
                    Pack = ContentPack(Manifest = PackManifest(Id = "disabled", Name = "Disabled", Version = "1.0.0"),
                        Content = PackContent(Items = listOf [ item "ore" "Ore" ]))) ]
    let merged, problems = PackMerge.mergeIntoContent baseContent disabled
    Assert.Same(baseContent, merged)
    Assert.Empty problems

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
    Assert.Equal(pack.Content.PlayerStart.Inventory.Count, project.Player.Inventory.Count)
    Assert.Empty(errors project)

// ── Mods and locale strings through the content compiler ─────────────────────

/// Port of the C# ContentDefaultTests demo-mod case (m5 acceptance test): the demo mod installs,
/// validates and merges its crop, machine and recipe under namespaced ids.
[<Fact>]
let ``the demo mod validates merges its crop machine and recipe and namespaces ids`` () =
    let raw = readElement [ "Fixtures"; "demo-mod.json" ]
    let validated = PacksSchema.ValidateContentPack raw
    Assert.Empty validated.Errors
    let project = Records.withValue (starter ()) "ContentPacks" (box (listOf [ PackInstallation(Pack = validated.Pack, Enabled = true) ]))
    let content = ContentCompiler.compile project
    Assert.True(content.Crops.ContainsKey "demo-glow-farm:glowshroom")
    Assert.Contains(content.MachineTypes, fun m -> m.Id = "demo-glow-farm:machine-glow-vat")
    let recipe = content.Recipes |> Seq.find (fun r -> r.Id = "demo-glow-farm:recipe-glow-jelly")
    Assert.Equal("demo-glow-farm:machine-glow-vat", recipe.MachineTypeId)
    Assert.Equal("demo-glow-farm:crop-glowshroom", recipe.Inputs.[0].ItemId)
    // A reload (serialize, parse) compiles to the same content.
    let reloaded = JsonDefaults.Deserialize<GameProject>(JsonDefaults.Serialize project)
    Assert.Equal(StableJson.Stringify content, StableJson.Stringify(ContentCompiler.compile reloaded))

let private localizedPack () =
    PacksSchema.ValidateContentPack(
        JsonDocument.Parse(
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
        ).RootElement
    ).Pack

let private localizedContent (locale: string) =
    let project = starter ()
    let project =
        Records.withValues project
            [ "ContentPacks", box (listOf [ PackInstallation(Pack = localizedPack (), Enabled = true) ])
              "Settings", box (Records.withValue project.Settings "Locale" (box locale)) ]
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
