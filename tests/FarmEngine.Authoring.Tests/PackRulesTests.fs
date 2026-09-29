module FarmEngine.Authoring.Tests.PackRulesTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

[<Fact>]
let ``compatibility range language matches web behavior including zero major and suffixes`` () =
    let versions = ["0.5.0"; "0.9.0"; "1.0.0"; "1.2.3-beta"; "2.3.4"; "broken"; " 1.2.3 "]
    let everything = versions
    // Each range, and the versions it accepts (an unparseable version is always accepted; an
    // unparseable or unsupported range accepts only that).
    let expected =
        [ None, everything
          Some "", everything
          Some "*", everything
          Some "^0.5.0", ["0.5.0"; "0.9.0"; "broken"]
          Some ">=0.5.1", ["0.9.0"; "1.0.0"; "1.2.3-beta"; "2.3.4"; "broken"; " 1.2.3 "]
          Some "0.5.0", ["0.5.0"; "broken"]
          Some "1.2.3-beta", ["1.2.3-beta"; "broken"; " 1.2.3 "]
          Some "^1.2.0", ["1.2.3-beta"; "broken"; " 1.2.3 "]
          Some "~1.0.0", ["broken"]
          Some ">=garbage", ["broken"]
          Some "^garbage", ["broken"]
          Some "  >=1.0.0  ", ["1.0.0"; "1.2.3-beta"; "2.3.4"; "broken"; " 1.2.3 "] ]
    for range, accepted in expected do
        Assert.Equal<string list>(accepted, versions |> List.filter (PackRules.isEngineCompatible range))
    Assert.True(PackRules.isEngineCompatible (Some "^0.5.0") "0.9.0")
    Assert.False(PackRules.isEngineCompatible (Some "^0.5.0") "1.0.0")

let private packOf (text: string) : ContentPack =
    match Json.parse text with
    | Ok json -> validPack json
    | Error message -> failwith message

let private pack () =
    packOf ("""{
      "manifest":{"id":"orchard","name":"Orchard","version":"1.0.0"},
      "content":{
        "items":[{"id":"fruit","name":"Fruit","cropType":"tree","useActionId":"eat"}],
        "crops":[{"id":"tree","name":"Tree"}],
        "npcs":[{"id":"farmer","name":"Farmer","sceneId":"grove","dialogue":[
          {"id":"hello","text":"Hello","options":[{"id":"reply","text":"Yes","nextDialogueId":"hello","offerQuestId":"visit"}]}]}],
        "quests":[{"id":"visit","prerequisites":["visit","base-quest","other:quest"]}],
        "scenes":[{"id":"grove","tiles":[],"transitions":[{"targetSceneId":"grove"}]}],
        "actions":[{"id":"eat","outcomes":[{"type":"giveItem","itemId":"fruit"}]}],
        "nodeTypes":[{"id":"log","drops":[{"itemId":"fruit","min":1,"max":2,"weight":1}]}],
        "playerStart":{"sceneId":"grove","inventory":[{"itemId":"fruit","quantity":2},{"itemId":"base-item","quantity":1}]},
        "strings":{"fr":{"item:fruit:name":"Pomme","item:orchard:fruit:description":"Description","dialogue:hello:text":"Bonjour","npc:farmer:name":"Fermier"}}
      },
      "plugins":[{"id":"script","source":"fruit and hello are source text"}]
    }""")

[<Fact>]
let ``namespace rewrites nested references while retaining absolute and base references`` () =
    let original = pack ()
    let before = stableOf SchemaJson.encodeContentPack original
    let actual = PackRules.namespacePack original
    Assert.Equal("orchard:fruit", actual.Content.Items[0].Id)
    Assert.Equal(Some "orchard:tree", actual.Content.Items[0].CropType)
    Assert.Equal(Some "orchard:hello", actual.Content.Npcs[0].Dialogue[0].Options[0].NextDialogueId)
    Assert.Equal("hello", actual.Content.Npcs[0].Dialogue[0].Id)
    Assert.Equal("grove", actual.Content.Npcs[0].SceneId)
    Assert.Equal("base-item", actual.Content.PlayerStart.Value.Inventory[1].ItemId)
    let fr = actual.Content.Strings |> List.find (fst >> (=) "fr") |> snd
    Assert.Equal(Some "Pomme", fr |> List.tryFind (fst >> (=) "item:orchard:fruit:name") |> Option.map snd)
    Assert.Equal(before, stableOf SchemaJson.encodeContentPack original)
    Assert.Equal(stableOf SchemaJson.encodeContentPack actual, stableOf SchemaJson.encodeContentPack (PackRules.namespacePack actual))

[<Fact>]
let ``base packs are identity transforms`` () =
    let original = pack ()
    let basePack = { original with Manifest = { original.Manifest with Base = true } }
    Assert.Same(basePack, PackRules.namespacePack basePack)

[<Fact>]
let ``localization respects load order disabled packs missing locales and empty strings`` () =
    let first = pack ()
    let second = packOf ("""{
      "manifest":{"id":"translation","name":"Translation","version":"1.0.0","dependencies":[{"packId":"orchard"}]},
      "content":{"strings":{"fr":{"item:orchard:fruit:name":"","npc:orchard:farmer:name":"Alice","dialogue:hello:text":"Salut"}}}
    }""")
    let installs = [{ PackInstallation.Default with Pack = second }; { PackInstallation.Default with Pack = first }]
    let content, _ = PackMerge.mergeIntoContent (ContentCompiler.baseContent(blank ())) installs
    let before = stableOf SchemaJson.encodeGameContent content
    // No locale, or one no enabled pack translates: the content itself.
    for locale in [""; "en"; "missing"] do
        Assert.Same(content, PackMerge.applyLocaleStrings content installs locale)
    let translated = PackMerge.applyLocaleStrings content installs "fr"
    Assert.Equal("", (translated.Items |> Seq.find (fun i -> i.Id = "orchard:fruit")).Name)
    Assert.Equal("Alice", translated.Npcs[0].Name)
    Assert.Equal("Salut", translated.Npcs[0].Dialogue[0].Text)
    Assert.Equal(before, stableOf SchemaJson.encodeGameContent content)
    let disabled = [{ PackInstallation.Default with Pack = first }; { PackInstallation.Default with Pack = second; Enabled = false }]
    let translated = PackMerge.applyLocaleStrings content disabled "fr"
    Assert.Equal("Pomme", (translated.Items |> Seq.find (fun i -> i.Id = "orchard:fruit")).Name)
