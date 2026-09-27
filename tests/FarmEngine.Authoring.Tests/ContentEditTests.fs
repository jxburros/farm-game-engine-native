module FarmEngine.Authoring.Tests.ContentEditTests

open System.Collections.Generic
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Tests.TestProjects
open FarmEngine.Schemas

let private hasId (id: string) (items: seq<'T>) (idOf: 'T -> string) = items |> Seq.exists (fun x -> idOf x = id)

[<Fact>]
let ``upserting an NPC keeps the project dialogue list in step`` () =
    let project = starter ()
    let elder = Defaults.newNpc project "Elder"
    let hello = Defaults.newDialogue project elder.Id
    let elder = Records.withValue elder "Dialogue" (box (listOf [ hello ]))
    let added = project |> apply (UpsertNpc elder)
    Assert.True(hasId elder.Id added.Npcs (fun n -> n.Id))
    Assert.True(hasId hello.Id added.Dialogues (fun d -> d.Id))
    Assert.Same(added, added |> apply (UpsertNpc elder))
    // Replace the dialogue set: the old one leaves the project list, the new one joins it.
    let bye = Records.withValues (Defaults.newDialogue added elder.Id) [ ("Text", box "Bye!") ]
    let swapped = added |> apply (UpsertNpc(Records.withValue elder "Dialogue" (box (listOf [ bye ]))))
    Assert.False(hasId hello.Id swapped.Dialogues (fun d -> d.Id))
    Assert.True(hasId bye.Id swapped.Dialogues (fun d -> d.Id))
    Assert.Equal(project.Dialogues.Count + 1, swapped.Dialogues.Count)

[<Fact>]
let ``removing an NPC drops its dialogues and every reference`` () =
    let project = starter ()
    let quest = Records.withValues (Defaults.newQuest project) [ ("Giver", box "npc-farmer") ]
    let project = project |> apply (UpsertQuest quest)
    Assert.True(project.Dialogues |> Seq.exists (fun d -> d.NpcId = "npc-farmer"))
    let removed = project |> apply (RemoveNpc "npc-farmer")
    Assert.False(hasId "npc-farmer" removed.Npcs (fun n -> n.Id))
    Assert.False(removed.Dialogues |> Seq.exists (fun d -> d.NpcId = "npc-farmer"))
    Assert.Null((removed.Quests |> Seq.find (fun q -> q.Id = quest.Id)).Giver)
    Assert.Same(removed, removed |> apply (RemoveNpc "npc-farmer"))
    let moved = project |> apply (MoveNpc("npc-farmer", "scene-farm", 4, 4))
    Assert.Equal(4.0, (npc moved "npc-farmer").X)
    Assert.Same(project, project |> apply (MoveNpc("npc-farmer", "nope", 4, 4)))

[<Fact>]
let ``dialogues are edited on both lists`` () =
    let project = starter ()
    let greeting = project.Dialogues |> Seq.find (fun d -> d.Id = "dialogue-farmer-greeting")
    let edited = Records.withValue greeting "Text" (box "Howdy!")
    let saved = project |> apply (UpsertDialogue edited)
    Assert.Equal("Howdy!", (saved.Dialogues |> Seq.find (fun d -> d.Id = greeting.Id)).Text)
    Assert.Equal("Howdy!", ((npc saved "npc-farmer").Dialogue |> Seq.find (fun d -> d.Id = greeting.Id)).Text)
    let removed = saved |> apply (RemoveDialogue "dialogue-farmer-crops")
    Assert.False(hasId "dialogue-farmer-crops" removed.Dialogues (fun d -> d.Id))
    Assert.False(hasId "dialogue-farmer-crops" (npc removed "npc-farmer").Dialogue (fun d -> d.Id))
    let greetingAfter = (npc removed "npc-farmer").Dialogue |> Seq.find (fun d -> d.Id = greeting.Id)
    Assert.True(greetingAfter.Options |> Seq.forall (fun o -> isNull o.NextDialogueId))
    Assert.Empty(errors removed |> List.filter (fun p -> p.Code = "content.dialogue"))

[<Fact>]
let ``items are upserted with their placed copies and removed with every reference`` () =
    let project = starter ()
    let wood = project.Items |> Seq.find (fun i -> i.Id = "material-wood")
    let placed = project |> apply (PlaceItem("scene-farm", 5, 5, wood))
    let renamed = placed |> apply (UpsertItem(Records.withValue wood "Name" (box "Timber")))
    Assert.Equal("Timber", (nonNull (tile renamed "scene-farm" 5 5).Item).Name)
    let removed = renamed |> apply (RemoveItem "material-wood")
    Assert.False(hasId "material-wood" removed.Items (fun i -> i.Id))
    Assert.Null((tile removed "scene-farm" 5 5).Item)
    Assert.True(removed.Recipes |> Seq.forall (fun r -> r.Inputs |> Seq.forall (fun i -> i.ItemId <> "material-wood")))
    Assert.True(removed.NodeTypes |> Seq.forall (fun n -> n.Drops |> Seq.forall (fun d -> d.ItemId <> "material-wood")))
    let leftovers = errors removed |> List.filter (fun p -> p.Code.StartsWith "content." || p.Code.StartsWith "recipe.")
    Assert.True(leftovers.IsEmpty, describe leftovers)
    // Inventory slots go too (the web refuses instead).
    let seeds = project |> apply (RemoveItem "seed-wheat")
    Assert.False(seeds.Player.Inventory |> Seq.exists (fun s -> s.Item.Id = "seed-wheat"))

[<Fact>]
let ``add to inventory stacks then opens a slot then refuses`` () =
    let project = starter ()
    let wheat (p: GameProject) = (p.Player.Inventory |> Seq.find (fun s -> s.Item.Id = "seed-wheat")).Quantity
    let stacked = project |> apply (AddToInventory "seed-wheat")
    Assert.Equal(wheat project + 1.0, wheat stacked)
    let newSlot = project |> apply (AddToInventory "material-wood")
    Assert.Equal(project.Player.Inventory.Count + 1, newSlot.Player.Inventory.Count)
    Assert.Same(project, project |> apply (AddToInventory "nope"))
    let fullPlayer = Records.withValue project.Player "MaxInventorySize" (box (float project.Player.Inventory.Count))
    let full = Records.withValue project "Player" (box fullPlayer)
    Assert.Same(full, full |> apply (AddToInventory "material-wood"))

[<Fact>]
let ``crops carry their seed and crop items`` () =
    let project = starter ()
    let crop = Records.withValue (Defaults.newCrop project) "Name" (box "Kale")
    let added = project |> apply (UpsertCrop crop)
    Assert.True(hasId crop.Id (nonNull added.CustomCrops) (fun c -> c.Id))
    let seed = added.Items |> Seq.find (fun i -> i.Id = sprintf "seed-%s" crop.Id)
    Assert.Equal("Kale Seeds", seed.Name)
    Assert.Equal(crop.Id, orEmpty seed.CropType)
    Assert.True(added.Items |> Seq.exists (fun i -> i.Id = sprintf "crop-%s" crop.Id && i.Name = "Kale"))
    Assert.Same(added, added |> apply (UpsertCrop crop))
    let removed = added |> apply (RemoveCrop crop.Id)
    Assert.False(hasId crop.Id (nonNull removed.CustomCrops) (fun c -> c.Id))
    Assert.False(hasId (sprintf "seed-%s" crop.Id) removed.Items (fun i -> i.Id))
    Assert.False(hasId (sprintf "crop-%s" crop.Id) removed.Items (fun i -> i.Id))
    Assert.Same(removed, removed |> apply (RemoveCrop crop.Id))

[<Fact>]
let ``removing a quest scrubs prerequisites, offers and outcomes`` () =
    let project = starter ()
    let chain = Records.withValues (Defaults.newQuest project) [ ("Prerequisites", box (listOf [ "quest-first-harvest" ])) ]
    let startOutcome = EventOutcome(Type = "startQuest", QuestId = "quest-first-harvest")
    let starts = Records.withValues (Defaults.newEvent project) [ ("Outcomes", box (listOf [ startOutcome ])) ]
    let project = project |> apply (Batch("setup", [ UpsertQuest chain; UpsertEvent starts ]))
    let removed = project |> apply (RemoveQuest "quest-first-harvest")
    Assert.Empty(nonNull (removed.Quests |> Seq.find (fun q -> q.Id = chain.Id)).Prerequisites)
    Assert.Empty((removed.Events |> Seq.find (fun e -> e.Id = starts.Id)).Outcomes)
    Assert.Empty(errors removed |> List.filter (fun p -> p.Code = "content.quests" || p.Code = "content.events"))

[<Fact>]
let ``removing an event clears its fired flag and scene lists`` () =
    let project = starter ()
    let event = Defaults.newEvent project
    let flags = OrderedDictionary<string, bool>(project.EventFlags)
    flags[EventsSchema.EventFiredFlag event.Id] <- true
    let project = project |> apply (UpsertEvent event)
    let project = Records.withValue project "EventFlags" (box flags)
    let removed = project |> apply (RemoveEvent event.Id)
    Assert.False(removed.EventFlags.ContainsKey(EventsSchema.EventFiredFlag event.Id))
    Assert.False(hasId event.Id removed.Events (fun e -> e.Id))

[<Fact>]
let ``shops, recipes, node types and machine types`` () =
    let project = starter ()
    let shop = Defaults.newShop project
    let opens = project.Dialogues |> Seq.find (fun d -> d.Id = "dialogue-merchant-greeting")
    let option = Records.withValue opens.Options.[0] "OpenShopId" (box shop.Id)
    let opens = Records.withValue opens "Options" (box (listOf [ option; opens.Options.[1] ]))
    let withShop = project |> apply (Batch("shop", [ UpsertShop shop; UpsertDialogue opens ]))
    let firstOption (p: GameProject) = (npc p "npc-merchant").Dialogue.[0].Options.[0]
    Assert.Equal(shop.Id, orEmpty (firstOption withShop).OpenShopId)
    let noShop = withShop |> apply (RemoveShop shop.Id)
    Assert.Null((firstOption noShop).OpenShopId)
    let recipe = Records.withValue (Defaults.newRecipe project) "MachineTypeId" (box "machine-kitchen")
    let withRecipe = project |> apply (UpsertRecipe recipe)
    let noMachine = withRecipe |> apply (RemoveMachineType "machine-kitchen")
    Assert.Null((noMachine.Recipes |> Seq.find (fun r -> r.Id = recipe.Id)).MachineTypeId)
    Assert.False(hasId "machine-kitchen" noMachine.MachineTypes (fun m -> m.Id))
    let noRecipe = withRecipe |> apply (RemoveRecipe recipe.Id)
    Assert.False(hasId recipe.Id noRecipe.Recipes (fun r -> r.Id))
    let node = Defaults.newNodeType project
    let placed = project |> apply (Batch("node", [ UpsertNodeType node; PlaceNode("scene-farm", 5, 5, node.Id) ]))
    Assert.Equal(node.Id, (nonNull (tile placed "scene-farm" 5 5).Node).TypeId)
    let noNode = placed |> apply (RemoveNodeType node.Id)
    Assert.Null((tile noNode "scene-farm" 5 5).Node)
    Assert.Empty(errors noNode |> List.filter (fun p -> p.Code = "content.nodes"))

[<Fact>]
let ``wildlife, actions and minigames`` () =
    let project = starter ()
    let species = Defaults.newAnimalSpecies project
    let withSpecies = project |> apply (UpsertAnimalSpecies species)
    let animal = Defaults.newAnimal withSpecies species.Id "scene-farm" 4 4
    let withAnimal = withSpecies |> apply (UpsertAnimal animal)
    Assert.True(hasId animal.Id withAnimal.Animals (fun a -> a.Id))
    Assert.Same(project, project |> apply (UpsertAnimal animal))
    let noSpecies = withAnimal |> apply (RemoveAnimalSpecies species.Id)
    Assert.False(noSpecies.Animals |> Seq.exists (fun a -> a.SpeciesId = species.Id))
    Assert.False(hasId animal.Id (withAnimal |> apply (RemoveAnimal animal.Id)).Animals (fun a -> a.Id))
    let table = Defaults.newFishTable project
    let withTable = project |> apply (UpsertFishTable table)
    Assert.True(hasId table.Id withTable.FishTables (fun t -> t.Id))
    Assert.False(hasId table.Id (withTable |> apply (RemoveFishTable table.Id)).FishTables (fun t -> t.Id))
    let action = Defaults.newAction project
    let uses = Records.withValue (Defaults.newItem project) "UseActionId" (box action.Id)
    let withAction = project |> apply (Batch("action", [ UpsertAction action; UpsertItem uses ]))
    let noAction = withAction |> apply (RemoveAction action.Id)
    Assert.Null((noAction.Items |> Seq.find (fun i -> i.Id = uses.Id)).UseActionId)
    let minigame = Defaults.newMinigame project
    let startOutcome = EventOutcome(Type = "startMinigame", MinigameId = minigame.Id)
    let starts = Records.withValues (Defaults.newEvent project) [ ("Outcomes", box (listOf [ startOutcome ])) ]
    let withMinigame = project |> apply (Batch("minigame", [ UpsertMinigame minigame; UpsertEvent starts ]))
    let noMinigame = withMinigame |> apply (RemoveMinigame minigame.Id)
    Assert.Empty((noMinigame.Events |> Seq.find (fun e -> e.Id = starts.Id)).Outcomes)

[<Fact>]
let ``weather weights, mine and panels`` () =
    let project = starter ()
    let stormy = project |> apply (SetWeatherWeight("spring", "storm", 3.0))
    Assert.True(stormy.Weather.Table.["spring"] |> Seq.exists (fun e -> e.WeatherId = "storm" && e.Weight = 3.0))
    let calm = stormy |> apply (SetWeatherWeight("spring", "storm", 0.0))
    Assert.False(calm.Weather.Table.["spring"] |> Seq.exists (fun e -> e.WeatherId = "storm"))
    Assert.Same(calm, calm |> apply (SetWeatherWeight("spring", "storm", 0.0)))
    let mine = Defaults.mineEnabled project true
    let mined = project |> apply (SetMine mine)
    Assert.True mined.Mine.Enabled
    Assert.NotEmpty mined.Mine.Bands
    Assert.Same(mined, mined |> apply (SetMine mine))
    let panel = Defaults.newGamePanel project
    let paneled = project |> apply (SetGamePanels [ panel ])
    Assert.Equal(1, (nonNull paneled.GamePanels).Count)
    Assert.Same(paneled, paneled |> apply (SetGamePanels [ panel ]))
