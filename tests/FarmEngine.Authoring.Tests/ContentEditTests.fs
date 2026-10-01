module FarmEngine.Authoring.Tests.ContentEditTests

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
    let elder = { elder with Dialogue = ([ hello ]) }
    let added = project |> apply (UpsertNpc elder)
    Assert.True(hasId elder.Id added.Npcs (fun n -> n.Id))
    Assert.True(hasId hello.Id added.Dialogues (fun d -> d.Id))
    Assert.Same(added, added |> apply (UpsertNpc elder))
    // Replace the dialogue set: the old one leaves the project list, the new one joins it.
    let bye = { Defaults.newDialogue added elder.Id with Text = "Bye!" }
    let swapped = added |> apply (UpsertNpc({ elder with Dialogue = ([ bye ]) }))
    Assert.False(hasId hello.Id swapped.Dialogues (fun d -> d.Id))
    Assert.True(hasId bye.Id swapped.Dialogues (fun d -> d.Id))
    Assert.Equal(project.Dialogues.Length + 1, swapped.Dialogues.Length)

[<Fact>]
let ``removing an NPC drops its dialogues and every reference`` () =
    let project = starter ()
    let quest = { Defaults.newQuest project with Giver = Some "npc-farmer" }
    let project = project |> apply (UpsertQuest quest)
    Assert.True(project.Dialogues |> Seq.exists (fun d -> d.NpcId = "npc-farmer"))
    let removed = project |> apply (RemoveNpc "npc-farmer")
    Assert.False(hasId "npc-farmer" removed.Npcs (fun n -> n.Id))
    Assert.False(removed.Dialogues |> Seq.exists (fun d -> d.NpcId = "npc-farmer"))
    Assert.True((removed.Quests |> Seq.find (fun q -> q.Id = quest.Id)).Giver.IsNone)
    Assert.Same(removed, removed |> apply (RemoveNpc "npc-farmer"))
    let moved = project |> apply (MoveNpc("npc-farmer", "scene-farm", 4, 4))
    Assert.Equal(4.0, (npc moved "npc-farmer").X)
    Assert.Same(project, project |> apply (MoveNpc("npc-farmer", "nope", 4, 4)))

[<Fact>]
let ``dialogues are edited on both lists`` () =
    let project = starter ()
    let greeting = project.Dialogues |> Seq.find (fun d -> d.Id = "dialogue-farmer-greeting")
    let edited = { greeting with Text = "Howdy!" }
    let saved = project |> apply (UpsertDialogue edited)
    Assert.Equal("Howdy!", (saved.Dialogues |> Seq.find (fun d -> d.Id = greeting.Id)).Text)
    Assert.Equal("Howdy!", ((npc saved "npc-farmer").Dialogue |> Seq.find (fun d -> d.Id = greeting.Id)).Text)
    let removed = saved |> apply (RemoveDialogue "dialogue-farmer-crops")
    Assert.False(hasId "dialogue-farmer-crops" removed.Dialogues (fun d -> d.Id))
    Assert.False(hasId "dialogue-farmer-crops" (npc removed "npc-farmer").Dialogue (fun d -> d.Id))
    let greetingAfter = (npc removed "npc-farmer").Dialogue |> Seq.find (fun d -> d.Id = greeting.Id)
    Assert.True(greetingAfter.Options |> Seq.forall (fun o -> o.NextDialogueId.IsNone))
    Assert.Empty(errors removed |> List.filter (fun p -> p.Code = "content.dialogue"))

[<Fact>]
let ``items are upserted with their placed copies and removed with every reference`` () =
    let project = starter ()
    let wood = project.Items |> Seq.find (fun i -> i.Id = "material-wood")
    let placed = project |> apply (PlaceItem("scene-farm", 5, 5, wood))
    let renamed = placed |> apply (UpsertItem({ wood with Name = "Timber" }))
    Assert.Equal("Timber", (tile renamed "scene-farm" 5 5).Item.Value.Name)
    let removed = renamed |> apply (RemoveItem "material-wood")
    Assert.False(hasId "material-wood" removed.Items (fun i -> i.Id))
    Assert.True((tile removed "scene-farm" 5 5).Item.IsNone)
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
    Assert.Equal(project.Player.Inventory.Length + 1, newSlot.Player.Inventory.Length)
    Assert.Same(project, project |> apply (AddToInventory "nope"))
    let fullPlayer = { project.Player with MaxInventorySize = (float project.Player.Inventory.Length) }
    let full = { project with Player = fullPlayer }
    Assert.Same(full, full |> apply (AddToInventory "material-wood"))

[<Fact>]
let ``crops carry their seed and crop items`` () =
    let project = starter ()
    let crop = { (Defaults.newCrop project) with Name = "Kale" }
    let added = project |> apply (UpsertCrop crop)
    Assert.True(hasId crop.Id added.CustomCrops.Value (fun c -> c.Id))
    let seed = added.Items |> Seq.find (fun i -> i.Id = sprintf "seed-%s" crop.Id)
    Assert.Equal("Kale Seeds", seed.Name)
    Assert.Equal(crop.Id, orEmpty seed.CropType)
    Assert.True(added.Items |> Seq.exists (fun i -> i.Id = sprintf "crop-%s" crop.Id && i.Name = "Kale"))
    Assert.Same(added, added |> apply (UpsertCrop crop))
    let removed = added |> apply (RemoveCrop crop.Id)
    Assert.False(hasId crop.Id removed.CustomCrops.Value (fun c -> c.Id))
    Assert.False(hasId (sprintf "seed-%s" crop.Id) removed.Items (fun i -> i.Id))
    Assert.False(hasId (sprintf "crop-%s" crop.Id) removed.Items (fun i -> i.Id))
    Assert.Same(removed, removed |> apply (RemoveCrop crop.Id))

[<Fact>]
let ``removing a quest scrubs prerequisites, offers and outcomes`` () =
    let project = starter ()
    let chain = { Defaults.newQuest project with Prerequisites = Some [ "quest-first-harvest" ] }
    let startOutcome = { EventOutcome.Default with Type = "startQuest"; QuestId = Some("quest-first-harvest") }
    let starts = { Defaults.newEvent project with Outcomes = [ startOutcome ] }
    let project = project |> apply (Batch("setup", [ UpsertQuest chain; UpsertEvent starts ]))
    let removed = project |> apply (RemoveQuest "quest-first-harvest")
    Assert.Empty((removed.Quests |> Seq.find (fun q -> q.Id = chain.Id)).Prerequisites.Value)
    Assert.Empty((removed.Events |> Seq.find (fun e -> e.Id = starts.Id)).Outcomes)
    Assert.Empty(errors removed |> List.filter (fun p -> p.Code = "content.quests" || p.Code = "content.events"))

[<Fact>]
let ``removing an event clears its fired flag and scene lists`` () =
    let project = starter ()
    let event = Defaults.newEvent project
    let flags = project.EventFlags @ [ EventsSchema.EventFiredFlag event.Id, JBool true ]
    let project = project |> apply (UpsertEvent event)
    let project = { project with EventFlags = flags }
    let removed = project |> apply (RemoveEvent event.Id)
    Assert.False(removed.EventFlags |> List.exists (fun (key, _) -> key = EventsSchema.EventFiredFlag event.Id))
    Assert.False(hasId event.Id removed.Events (fun e -> e.Id))

[<Fact>]
let ``shops, recipes, node types and machine types`` () =
    let project = starter ()
    let shop = Defaults.newShop project
    let opens = project.Dialogues |> Seq.find (fun d -> d.Id = "dialogue-merchant-greeting")
    let option = { opens.Options.[0] with OpenShopId = Some shop.Id }
    let opens = { opens with Options = ([ option; opens.Options.[1] ]) }
    let withShop = project |> apply (Batch("shop", [ UpsertShop shop; UpsertDialogue opens ]))
    let firstOption (p: GameProject) = (npc p "npc-merchant").Dialogue.[0].Options.[0]
    Assert.Equal(shop.Id, orEmpty (firstOption withShop).OpenShopId)
    let noShop = withShop |> apply (RemoveShop shop.Id)
    Assert.True((firstOption noShop).OpenShopId.IsNone)
    let recipe = { (Defaults.newRecipe project) with MachineTypeId = Some "machine-kitchen" }
    let withRecipe = project |> apply (UpsertRecipe recipe)
    let noMachine = withRecipe |> apply (RemoveMachineType "machine-kitchen")
    Assert.True((noMachine.Recipes |> Seq.find (fun r -> r.Id = recipe.Id)).MachineTypeId.IsNone)
    Assert.False(hasId "machine-kitchen" noMachine.MachineTypes (fun m -> m.Id))
    let noRecipe = withRecipe |> apply (RemoveRecipe recipe.Id)
    Assert.False(hasId recipe.Id noRecipe.Recipes (fun r -> r.Id))
    let node = Defaults.newNodeType project
    let placed = project |> apply (Batch("node", [ UpsertNodeType node; PlaceNode("scene-farm", 5, 5, node.Id) ]))
    Assert.Equal(node.Id, (tile placed "scene-farm" 5 5).Node.Value.TypeId)
    let noNode = placed |> apply (RemoveNodeType node.Id)
    Assert.True((tile noNode "scene-farm" 5 5).Node.IsNone)
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
    let uses = { (Defaults.newItem project) with UseActionId = Some action.Id }
    let withAction = project |> apply (Batch("action", [ UpsertAction action; UpsertItem uses ]))
    let noAction = withAction |> apply (RemoveAction action.Id)
    Assert.True((noAction.Items |> Seq.find (fun i -> i.Id = uses.Id)).UseActionId.IsNone)
    let minigame = Defaults.newMinigame project
    let startOutcome = { EventOutcome.Default with Type = "startMinigame"; MinigameId = Some(minigame.Id) }
    let starts = { Defaults.newEvent project with Outcomes = [ startOutcome ] }
    let withMinigame = project |> apply (Batch("minigame", [ UpsertMinigame minigame; UpsertEvent starts ]))
    let noMinigame = withMinigame |> apply (RemoveMinigame minigame.Id)
    Assert.Empty((noMinigame.Events |> Seq.find (fun e -> e.Id = starts.Id)).Outcomes)

[<Fact>]
let ``weather weights, mine and panels`` () =
    let project = starter ()
    let stormy = project |> apply (SetWeatherWeight("spring", "storm", 3.0))
    let spring (p: GameProject) = p.Weather.Table |> List.find (fun (season, _) -> season = "spring") |> snd
    Assert.True(spring stormy |> Seq.exists (fun e -> e.WeatherId = "storm" && e.Weight = 3.0))
    let calm = stormy |> apply (SetWeatherWeight("spring", "storm", 0.0))
    Assert.False(spring calm |> Seq.exists (fun e -> e.WeatherId = "storm"))
    Assert.Same(calm, calm |> apply (SetWeatherWeight("spring", "storm", 0.0)))
    let mine = Defaults.mineEnabled project true
    let mined = project |> apply (SetMine mine)
    Assert.True mined.Mine.Enabled
    Assert.NotEmpty mined.Mine.Bands
    Assert.Same(mined, mined |> apply (SetMine mine))
    let panel = Defaults.newGamePanel project
    let paneled = project |> apply (SetGamePanels [ panel ])
    Assert.Equal(1, paneled.GamePanels.Value.Length)
    Assert.Same(paneled, paneled |> apply (SetGamePanels [ panel ]))

[<Fact>]
let ``add to inventory says which toast applies`` () =
    let project = starter ()
    let wood = project.Items |> List.find (fun i -> i.Id = "material-wood")
    Assert.Equal(sprintf "Added %s to inventory" wood.Name, ContentActions.AddToInventoryMessage(project, "material-wood"))
    Assert.Equal("Save nope before adding it to the inventory.", ContentActions.AddToInventoryMessage(project, "nope"))
    let wheatSeeds = project.Items |> List.find (fun i -> i.Id = "seed-wheat")
    let full = project.Player.Inventory |> List.map (fun s -> if s.Item.Id = "seed-wheat" then { s with Quantity = wheatSeeds.MaxStack } else s)
    let stackFull = { project with Player = { project.Player with Inventory = full } }
    Assert.Equal(sprintf "%s stack is full" wheatSeeds.Name, ContentActions.AddToInventoryMessage(stackFull, "seed-wheat"))
    Assert.Same(stackFull, stackFull |> apply (AddToInventory "seed-wheat"))
    let noRoom = { project with Player = { project.Player with MaxInventorySize = float project.Player.Inventory.Length } }
    Assert.Equal("Inventory is full!", ContentActions.AddToInventoryMessage(noRoom, "material-wood"))

[<Fact>]
let ``duplicated actions, minigames and crops get fresh ids and stay valid`` () =
    let project = starter ()
    let action = { (Defaults.newAction project) with Name = "Water Can" }
    let project = project |> apply (UpsertAction action)
    let copy = Defaults.duplicateAction project action
    Assert.Equal("water-can-copy", copy.Id)
    Assert.Equal("Water Can (copy)", copy.Name)
    Assert.Equal<EventOutcome list>(action.Outcomes, copy.Outcomes)
    let project = project |> apply (UpsertAction copy)
    Assert.Equal("water-can-copy-2", (Defaults.duplicateAction project action).Id)
    let minigame = Defaults.newMinigame project
    let project = project |> apply (UpsertMinigame minigame)
    let gameCopy = Defaults.duplicateMinigame project minigame
    Assert.NotEqual<string>(minigame.Id, gameCopy.Id)
    Assert.Equal(sprintf "%s (copy)" minigame.Name, gameCopy.Name)
    Assert.Equal(minigame.Kind, gameCopy.Kind)
    let project = project |> apply (UpsertMinigame gameCopy)
    let crop = Defaults.newCrop project
    let project = project |> apply (UpsertCrop crop)
    let cropCopy = Defaults.duplicateCrop project crop
    Assert.NotEqual<string>(crop.Id, cropCopy.Id)
    let project = project |> apply (UpsertCrop cropCopy)
    Assert.True(project.Items |> List.exists (fun i -> i.Id = sprintf "seed-%s" cropCopy.Id))
    Assert.True((errors project).IsEmpty, describe (errors project))

[<Fact>]
let ``reward options become once-only when their reward is set`` () =
    let project = starter ()
    let greeting = (npc project "npc-farmer").Dialogue.[0]
    let plain = { DialogueOption.Default with Text = "Hello" }
    let added = project |> apply (UpsertDialogue { greeting with Options = greeting.Options @ [ plain ] })
    let option (p: GameProject) = (p.Dialogues |> List.find (fun d -> d.Id = greeting.Id)).Options |> List.last
    Assert.Equal(None, (option added).Once)
    let rewarded = added |> apply (UpsertDialogue { greeting with Options = greeting.Options @ [ { plain with GiveMoney = Some 25.0 } ] })
    Assert.Equal(Some true, (option rewarded).Once)
    Assert.Equal(Some true, ((npc rewarded "npc-farmer").Dialogue.[0].Options |> List.last).Once)
    // An explicit choice stays.
    let repeatable = rewarded |> apply (UpsertDialogue { greeting with Options = greeting.Options @ [ { plain with GiveMoney = Some 25.0; Once = Some false } ] })
    Assert.Equal(Some false, (option repeatable).Once)
    // Through the NPC form too.
    let farmer = npc project "npc-farmer"
    let viaNpc = { farmer with Dialogue = [ { greeting with Options = greeting.Options @ [ { plain with GiveItem = Some "gift-flower" } ] } ] }
    Assert.Equal(Some true, ((npc (project |> apply (UpsertNpc viaNpc)) "npc-farmer").Dialogue.[0].Options |> List.last).Once)

[<Fact>]
let ``item edits keep a tool's durability in the starting inventory`` () =
    let project = starter ()
    let hoe = project.Items |> List.find (fun i -> i.Id = "tool-hoe")
    let worn = project.Player.Inventory |> List.map (fun s -> if s.Item.Id = "tool-hoe" then { s with Item = { s.Item with Durability = Some 40.0 } } else s)
    let project = { project with Player = { project.Player with Inventory = worn } }
    let renamed = project |> apply (UpsertItem { hoe with Name = "Old Hoe"; MaxDurability = Some 30.0 })
    let slot = renamed.Player.Inventory |> List.find (fun s -> s.Item.Id = "tool-hoe")
    Assert.Equal("Old Hoe", slot.Item.Name)
    Assert.Equal(Some 30.0, slot.Item.Durability)

[<Fact>]
let ``a max stack of 0 does not cap the starting inventory`` () =
    let project = starter ()
    let wood = { (project.Items |> List.find (fun i -> i.Id = "material-wood")) with MaxStack = 0.0 }
    let project = project |> apply (UpsertItem wood)
    let once = project |> apply (AddToInventory "material-wood")
    let twice = once |> apply (AddToInventory "material-wood")
    let woodSlots (p: GameProject) = p.Player.Inventory |> List.filter (fun s -> s.Item.Id = "material-wood")
    Assert.Equal(1, (woodSlots twice).Length)
    Assert.Equal(2.0, (woodSlots twice).Head.Quantity)

[<Fact>]
let ``pack starting items stack to their cap and stop at the slot limit`` () =
    let rules = InventoryRules.add
    let seed = { Item.Default with Id = "seed"; Stackable = true; MaxStack = 10.0 }
    let held = [ ({ Item = seed; Quantity = 4.0; Quality = None } : InventorySlot) ]
    let merged, rejected = rules seed 25.0 3.0 held
    Assert.Equal<float list>([ 10.0; 10.0; 9.0 ], merged |> List.map (fun s -> s.Quantity))
    Assert.Equal(0.0, rejected)
    let full, lost = rules seed 25.0 2.0 held
    Assert.Equal<float list>([ 10.0; 10.0 ], full |> List.map (fun s -> s.Quantity))
    Assert.Equal(9.0, lost)
