module FarmEngine.Authoring.Tests.PackRulesTests

open System.Text.Json
open Xunit
open FarmEngine.Authoring
open FarmEngine.Core
open FarmEngine.Json
open FarmEngine.Schemas
open FarmEngine.Authoring.Tests.TestProjects

[<Fact>]
let ``compatibility range language matches web behavior including zero major and suffixes`` () =
    let versions = ["0.5.0"; "0.9.0"; "1.0.0"; "1.2.3-beta"; "2.3.4"; "broken"; " 1.2.3 "]
    for range in [null; ""; "*"; "^0.5.0"; ">=0.5.1"; "0.5.0"; "1.2.3-beta"; "^1.2.0"; "~1.0.0"; ">=garbage"; "^garbage"; "  >=1.0.0  "] do
        for version in versions do
            Assert.Equal(PacksSchema.IsEngineCompatible(range, version), PackRules.isEngineCompatible range version)
    Assert.True(PackRules.isEngineCompatible "^0.5.0" "0.9.0")
    Assert.False(PackRules.isEngineCompatible "^0.5.0" "1.0.0")

let private pack () =
    JsonSerializer.Deserialize<ContentPack>("""{
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
    }""", JsonDefaults.Options)

[<Fact>]
let ``namespace rewrites nested references while retaining absolute and base references`` () =
    let original = pack ()
    let before = StableJson.Stringify original
    let actual = PackRules.namespacePack original
    Assert.Equal(StableJson.Stringify(Packs.NamespacePack original), StableJson.Stringify actual)
    Assert.Equal("orchard:fruit", actual.Content.Items[0].Id)
    Assert.Equal("orchard:tree", actual.Content.Items[0].CropType)
    Assert.Equal("orchard:hello", actual.Content.Npcs[0].Dialogue[0].Options[0].NextDialogueId)
    Assert.Equal("hello", actual.Content.Npcs[0].Dialogue[0].Id)
    Assert.Equal("grove", actual.Content.Npcs[0].SceneId)
    Assert.Equal("base-item", actual.Content.PlayerStart.Inventory[1].ItemId)
    Assert.Equal("Pomme", actual.Content.Strings["fr"]["item:orchard:fruit:name"])
    Assert.Equal(before, StableJson.Stringify original)
    Assert.Equal(StableJson.Stringify actual, StableJson.Stringify(PackRules.namespacePack actual))

[<Fact>]
let ``base packs are identity transforms`` () =
    let original = pack ()
    let basePack = Records.withValue original "Manifest" (box (Records.withValue original.Manifest "Base" (box true)))
    Assert.Same(basePack, PackRules.namespacePack basePack)

[<Fact>]
let ``localization respects load order disabled packs missing locales and empty strings`` () =
    let first = pack ()
    let second = JsonSerializer.Deserialize<ContentPack>("""{
      "manifest":{"id":"translation","name":"Translation","version":"1.0.0","dependencies":[{"packId":"orchard"}]},
      "content":{"strings":{"fr":{"item:orchard:fruit:name":"","npc:orchard:farmer:name":"Alice","dialogue:hello:text":"Salut"}}}
    }""", JsonDefaults.Options)
    let installs = listOf [PackInstallation(Pack = second); PackInstallation(Pack = first)]
    let content, _ = PackMerge.mergeIntoContent (ContentCompiler.baseContent(blank ())) installs
    let before = StableJson.Stringify content
    for locale in [null; ""; "en"; "fr"; "missing"] do
        Assert.Equal(StableJson.Stringify(Packs.ApplyLocaleStrings(content, installs, locale)),
            StableJson.Stringify(PackMerge.applyLocaleStrings content installs locale))
    let translated = PackMerge.applyLocaleStrings content installs "fr"
    Assert.Equal("", (translated.Items |> Seq.find (fun i -> i.Id = "orchard:fruit")).Name)
    Assert.Equal("Alice", translated.Npcs[0].Name)
    Assert.Equal("Salut", translated.Npcs[0].Dialogue[0].Text)
    Assert.Equal(before, StableJson.Stringify content)
    let disabled = listOf [PackInstallation(Pack = first); PackInstallation(Pack = second, Enabled = false)]
    let translated = PackMerge.applyLocaleStrings content disabled "fr"
    Assert.Equal("Pomme", (translated.Items |> Seq.find (fun i -> i.Id = "orchard:fruit")).Name)
