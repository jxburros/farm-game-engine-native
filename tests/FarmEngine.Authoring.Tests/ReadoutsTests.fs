module FarmEngine.Authoring.Tests.ReadoutsTests

open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private ingredients (pairs: (string * float) list) : RecipeIngredient list =
    pairs |> List.map (fun (id, quantity) -> { ItemId = id; Quantity = quantity })

let private recipe (inputs: (string * float) list) (outputs: (string * float) list) (minutes: float) : RecipeDefinition =
    { RecipeDefinition.Default with
        Id = "recipe-test"; Name = "Test"; Inputs = ingredients inputs; Outputs = ingredients outputs; ProcessingMinutes = minutes }

let private builtinCrop (id: string) : CropDefinition = Builtin.crops () |> List.find (fun (cropId, _) -> cropId = id) |> snd

let private builtinNode (id: string) : NodeTypeDefinition =
    Builtin.nodeTypes () @ Builtin.mineNodeTypes () |> List.find (fun node -> node.Id = id)

let private lines (readout: ReadoutLine list) = readout |> List.map (fun line -> line.Label, line.Value)

let private value (label: string) (readout: ReadoutLine list) =
    readout |> List.find (fun line -> line.Label = label) |> fun line -> line.Value

[<Fact>]
let ``recipe profit uses the built-in item values when the project has no items`` () =
    // Two wheat (25 each) into preserves (120).
    let project = { blank () with Items = [] }
    let preserves = recipe [ "crop-wheat", 2.0 ] [ "food-preserves", 1.0 ] 90.0
    Assert.Equal(70.0, Readouts.recipeProfit project preserves)
    Assert.Equal(Some 47.0, Readouts.recipeProfitPerHour project preserves) // 46.67/hr
    Assert.Equal("Profit per craft: 70 · time-adjusted: 47/hr", Readouts.recipeSummary project preserves)
    // Unknown items are worth nothing; an instant craft has no hourly rate.
    let instant = recipe [ "missing-item", 3.0 ] [ "crop-wheat", 1.0 ] 0.0
    Assert.Equal(25.0, Readouts.recipeProfit project instant)
    Assert.Equal(None, Readouts.recipeProfitPerHour project instant)
    Assert.Equal("Profit per craft: 25 · time-adjusted: instant", Readouts.recipeSummary project instant)
    // The project's own items win over the catalog.
    let cheap = { project with Items = [ { item "crop-wheat" "Wheat" with Value = 1.0 }; { item "food-preserves" "Preserves" with Value = 4.0 } ] }
    Assert.Equal(2.0, Readouts.recipeProfit cheap preserves)

[<Fact>]
let ``profit per hour rounds half away from zero like toFixed`` () =
    let project = starter ()
    // Wood is worth 5 and an unknown item nothing: ±5 per craft over two hours is ±2.5/hr.
    let gain = recipe [ "missing-item", 1.0 ] [ "material-wood", 1.0 ] 120.0
    let loss = recipe [ "material-wood", 1.0 ] [ "missing-item", 1.0 ] 120.0
    Assert.Equal(Some 3.0, Readouts.recipeProfitPerHour project gain)
    Assert.Equal(Some -3.0, Readouts.recipeProfitPerHour project loss)
    Assert.Equal("Profit per craft: -5 · time-adjusted: -3/hr", Readouts.recipeSummary project loss)
    Assert.Equal(5.0, ContentReadouts.RecipeProfit(project, gain))
    Assert.Equal(System.Nullable 3.0, ContentReadouts.RecipeProfitPerHour(project, gain))
    Assert.False(ContentReadouts.RecipeProfitPerHour(project, { gain with ProcessingMinutes = 0.0 }).HasValue)

[<Fact>]
let ``the recipe list line says where it is made and signs the profit`` () =
    let project = starter ()
    let preserves = recipe [ "crop-wheat", 2.0 ] [ "food-preserves", 1.0 ] 90.0
    Assert.Equal("hand craft · profit +70", Readouts.recipeNote project { preserves with ProcessingMinutes = 0.0 })
    Assert.Equal("Preserves Jar · 90min · profit +70", Readouts.recipeNote project { preserves with MachineTypeId = Some "machine-preserves" })
    Assert.Equal("machine · 90min · profit +70", Readouts.recipeNote project { preserves with MachineTypeId = Some "machine-gone" })
    Assert.Equal("hand craft · profit +0", Readouts.recipeNote project (recipe [ "material-wood", 1.0 ] [ "material-wood", 1.0 ] 0.0))
    Assert.Equal("hand craft · profit -1", Readouts.recipeNote project (recipe [ "material-wood", 1.0 ] [ "material-stone", 1.0 ] 0.0))
    Assert.Equal("hand craft · profit +70", ContentReadouts.RecipeNote(project, { preserves with ProcessingMinutes = 0.0 }))

[<Fact>]
let ``the crop summary lists what the web card shows`` () =
    let project = starter ()
    let wheat = Readouts.cropSummary project (builtinCrop "wheat")
    Assert.Equal<(string * string) list>(
        [ "Seed cost", "$10"
          "Harvest value", "$25"
          "Profit per harvest", "$15"
          "Growth days", "3"
          "Stages", "4"
          "Seasons", "Spring, Fall"
          "Yield range", "1 – 2"
          "Mutation chance", "1.0%"
          "Can regrow", "No" ],
        lines wheat)
    Assert.Equal("Profit per harvest: $15", Readouts.cropProfitText (builtinCrop "wheat"))
    Assert.Equal("Profit per harvest: -$10", Readouts.cropProfitText { builtinCrop "wheat" with SeedCost = 35.0 })

[<Fact>]
let ``the crop summary adds regrowth and multi-tile rows`` () =
    let project = starter ()
    let tomato = Readouts.cropSummary project (builtinCrop "tomato")
    Assert.Equal("Yes", value "Can regrow" tomato)
    Assert.Equal("2 day(s)", value "Regrowth time" tomato)
    Assert.Equal("2.0%", value "Mutation chance" tomato)
    Assert.DoesNotContain(tomato, fun line -> line.Label = "Multi-tile size")
    // Only the legacy wall-clock time: 12500 ms is 2.5 → 3 days.
    let legacy = { builtinCrop "tomato" with RegrowthDays = None; RegrowthTime = Some 12500.0 }
    Assert.Equal("3 day(s)", value "Regrowth time" (Readouts.cropSummary project legacy))
    // A zero is not shown, like the web's truthiness check.
    let zero = { builtinCrop "tomato" with RegrowthDays = Some 0.0; RegrowthTime = None }
    Assert.DoesNotContain(Readouts.cropSummary project zero, fun line -> line.Label = "Regrowth time")
    let pumpkin = Readouts.cropSummary project (builtinCrop "pumpkin")
    Assert.Equal("2 × 2 tiles", value "Multi-tile size" pumpkin)
    Assert.DoesNotContain(pumpkin, fun line -> line.Label = "Regrowth time")
    Assert.Equal("Multi-tile size", (List.last pumpkin).Label)
    // Growth days from the legacy time (17500 ms → 3.5 → 4), and unknown seasons capitalized.
    let old = { builtinCrop "wheat" with GrowthDays = None; GrowthTime = 17500.0; Seasons = [ "monsoon" ] }
    let summary = Readouts.cropSummary project old
    Assert.Equal("4", value "Growth days" summary)
    Assert.Equal("Monsoon", value "Seasons" summary)
    Assert.Equal("None", value "Seasons" (Readouts.cropSummary project { old with Seasons = [] }))

[<Fact>]
let ``built-in lists leave out the ids the project replaces`` () =
    let project = blank ()
    Assert.Equal<string list>(Readouts.builtinCropIds (), Readouts.builtinCrops project |> List.map (fun crop -> crop.Id))
    let customized = project |> apply (UpsertCrop(Readouts.customOfCrop (builtinCrop "wheat")))
    let remaining = Readouts.builtinCrops customized |> List.map (fun crop -> crop.Id)
    Assert.Equal(8, remaining.Length)
    Assert.DoesNotContain("wheat", remaining)
    // The starter farm's base pack brings every built-in crop and node type as its own.
    Assert.Empty(Readouts.builtinCrops (starter ()))
    Assert.Empty(Readouts.builtinNodeTypes (starter ()))
    Assert.Equal(9, (Readouts.builtinNodeTypes project).Length)
    let withTree = project |> apply (UpsertNodeType { builtinNode "node-tree" with Health = 9.0 })
    Assert.DoesNotContain(Readouts.builtinNodeTypes withTree, fun node -> node.Id = "node-tree")
    Assert.Contains(Readouts.builtinNodeTypes withTree, fun node -> node.Id = "node-quartz")
    Assert.True(ContentReadouts.IsBuiltinCrop "pumpkin")
    Assert.False(ContentReadouts.IsBuiltinCrop "custom-1")
    Assert.True(ContentReadouts.IsBuiltinNodeType "node-copper-ore")

[<Fact>]
let ``list notes say where an entry comes from`` () =
    Assert.Equal("Built-in · 4 stages", Readouts.builtinCropNote (builtinCrop "wheat"))
    Assert.Equal("Replaces built-in · 4 stages", Readouts.cropNote (Readouts.customOfCrop (builtinCrop "wheat")))
    Assert.Equal("Custom · 5 stages", Readouts.cropNote { CustomCropDefinition.Default with Id = "custom-1"; Stages = 5.0 })
    Assert.Equal("4 hp · axe · no respawn", Readouts.nodeTypeNote (builtinNode "node-tree"))
    Assert.Equal("6 hp · pickaxe t2 · no respawn", Readouts.nodeTypeNote (builtinNode "node-boulder"))
    Assert.Equal("2 hp · axe · respawns 3d", Readouts.nodeTypeNote (builtinNode "node-stump"))
    Assert.Equal("Built-in · 1 hp · scythe · respawns 2d", Readouts.nodeTypeListNote (builtinNode "node-weeds") true)
    Assert.Equal("Replaces built-in · 1 hp · scythe · respawns 2d", Readouts.nodeTypeListNote (builtinNode "node-weeds") false)
    Assert.Equal("Custom · 3 hp · axe · no respawn", Readouts.nodeTypeListNote { builtinNode "node-tree" with Id = "node-oak"; Health = 3.0 } false)

[<Fact>]
let ``the node type summary names drops and respawn`` () =
    let summary = Readouts.nodeTypeSummary (starter ()) (builtinNode "node-boulder")
    Assert.Equal<(string * string) list>(
        [ "Health", "6 hits"; "Tool", "pickaxe (tier 2)"; "Drops", "Stone ×4–8"; "Respawn", "Never"; "Blocks movement", "Yes" ],
        lines summary)
    let weeds = Readouts.nodeTypeSummary (starter ()) (builtinNode "node-weeds")
    Assert.Equal("2 day(s)", value "Respawn" weeds)
    Assert.Equal("No", value "Blocks movement" weeds)
    Assert.Equal("Nothing", value "Drops" (Readouts.nodeTypeSummary (starter ()) { builtinNode "node-weeds" with Drops = [] }))

[<Fact>]
let ``customizing a built-in crop keeps its id and values`` () =
    for id, crop in Builtin.crops () do
        let custom = ContentReadouts.CustomizeCrop crop
        Assert.Equal(id, custom.Id)
        Assert.Equal(crop.Name, custom.Name)
        Assert.Equal(crop.SeedCost, custom.SeedCost)
        Assert.Equal(crop.BaseHarvestValue, custom.BaseHarvestValue)
        Assert.Equal(crop.GrowthDays, custom.GrowthDays)
        Assert.Equal(crop.RegrowthDays, custom.RegrowthDays)
        Assert.Equal(crop.MultiTile, custom.MultiTile)
        Assert.Equal<string list>(crop.Seasons, custom.Seasons)
        Assert.Equal(None, custom.CustomAsset)
        Assert.Equal(crop, Readouts.cropOfCustom custom)
    // Saved, it replaces the built-in with the same definition, and its readouts match.
    let project = blank () |> apply (UpsertCrop(ContentReadouts.CustomizeCrop(builtinCrop "tomato")))
    Assert.Equal(builtinCrop "tomato", ContentCompiler.mergeCrops project.CustomCrops |> List.find (fun (id, _) -> id = "tomato") |> snd)
    let saved = project.CustomCrops.Value |> List.find (fun crop -> crop.Id = "tomato")
    Assert.Equal<ReadoutLine seq>(ContentReadouts.CropSummary(project, builtinCrop "tomato"), ContentReadouts.CropSummary(project, saved))
    Assert.Equal("Profit per harvest: $30", ContentReadouts.CropProfitText saved)

[<Fact>]
let ``deleting a replacement brings the built-in crop back without clearing its uses`` () =
    let project = starter ()
    // The starter's wheat replaces the built-in (the base pack merges it in).
    Assert.Contains(project.CustomCrops.Value, fun crop -> crop.Id = "wheat")
    let restored = project |> apply (RemoveCrop "wheat")
    Assert.DoesNotContain(restored.CustomCrops.Value, fun crop -> crop.Id = "wheat")
    Assert.Contains(Readouts.builtinCrops restored, fun crop -> crop.Id = "wheat")
    let seed = restored.Items |> List.find (fun item -> item.Id = "seed-wheat")
    Assert.Equal(Builtin.items () |> List.find (fun item -> item.Id = "seed-wheat"), seed)
    Assert.Contains(restored.Items, fun item -> item.Id = "crop-wheat")
    // A crop of the creator's own still takes its items along.
    let custom = project |> apply (UpsertCrop { CustomCropDefinition.Default with Id = "custom-1"; Name = "Moonmelon"; Stages = 4.0 })
    Assert.Contains(custom.Items, fun item -> item.Id = "seed-custom-1")
    Assert.DoesNotContain((custom |> apply (RemoveCrop "custom-1")).Items, fun item -> item.Id = "seed-custom-1")
    // One undo step.
    let doc = Document.create project |> Document.apply (RemoveCrop "wheat")
    Assert.Equal(1, doc.Past.Length)

[<Fact>]
let ``deleting a replacement node type keeps the placed nodes`` () =
    let project = starter ()
    let trees (p: GameProject) = (farm p).Tiles |> List.sumBy (fun row -> row |> List.filter (fun t -> t.Node |> Option.exists (fun n -> n.TypeId = "node-tree")) |> List.length)
    Assert.True(trees project > 0)
    let restored = project |> apply (RemoveNodeType "node-tree")
    Assert.DoesNotContain(restored.NodeTypes, fun node -> node.Id = "node-tree")
    Assert.Equal(trees project, trees restored)
    Assert.Contains(ContentCompiler.nodeTypes restored, fun node -> node.Id = "node-tree")

[<Fact>]
let ``schedule minutes read as clock times`` () =
    Assert.Equal("12:00 AM", Readouts.clock 0.0)
    Assert.Equal("8:00 AM", Readouts.clock 480.0)
    Assert.Equal("12:05 PM", Readouts.clock 725.0)
    Assert.Equal("11:59 PM", Readouts.clock 1439.5)
    Assert.Equal("12:00 AM (next day)", Readouts.clock 1440.0)
    Assert.Equal("1:00 AM (next day)", Readouts.clock 1500.0)
    Assert.Equal("12:00 AM", Readouts.clock -30.0)
    Assert.Equal("12:00 AM", Readouts.clock nan)
    Assert.Equal("8:00 AM", ContentReadouts.Clock 480.0)

[<Fact>]
let ``money and signs read like the web`` () =
    Assert.Equal("$12", Readouts.money 12.0)
    Assert.Equal("-$5", Readouts.money -5.0)
    Assert.Equal("$0.5", ContentReadouts.Money 0.5)
    Assert.Equal("+12", Readouts.signed 12.0)
    Assert.Equal("-5", Readouts.signed -5.0)
    Assert.Equal("+0", Readouts.signed 0.0)
