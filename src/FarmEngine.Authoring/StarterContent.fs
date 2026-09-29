namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// The authored Farm Essentials pack (packages/content-default/src/index.ts).
/// Every call constructs fresh data. This module never reads a clock or runs simulation rules.
module StarterContent =
    let private farm () =
        let scene = AuthoringTiles.CreateEmptyScene("scene-farm", "Farm", 16.0, 12.0)
        let tiles = scene.Tiles |> List.map Array.ofList |> Array.ofList
        let paint x y kind = tiles.[y].[x] <- AuthoringTiles.SetTileLayer(tiles.[y].[x], kind)
        for y in 4 .. 8 do
            for x in 5 .. 11 do paint x y "soil"
        for y in 0 .. 11 do
            paint 0 y "wall"
            paint 15 y "wall"
        for x in 0 .. 15 do
            paint x 0 "wall"
            paint x 11 "wall"
        paint 8 11 "door"
        for x, y, kind, health in [2, 2, "node-tree", 4.0; 3, 9, "node-tree", 4.0;
                                   13, 2, "node-rock", 3.0; 13, 9, "node-rock", 3.0;
                                   2, 6, "node-weeds", 1.0; 12, 5, "node-weeds", 1.0] do
            tiles.[y].[x] <- { tiles.[y].[x] with Node = Some { TypeId = kind; RemainingHealth = health; DepletedOnDay = None; Extra = [] } }
        { scene with Tiles = tiles |> Array.map List.ofArray |> List.ofArray }

    let private option text : DialogueOption = { DialogueOption.Default with Text = text }

    let private npcs () =
        let greeting : Dialogue =
            { Id = "dialogue-farmer-greeting"; NpcId = "npc-farmer"
              Text = "Welcome to the farm! Plant crops in the soil and watch them grow. Come back when you need advice!"
              Options = [ option "Thanks for the help!"
                          { option "What crops grow best here?" with NextDialogueId = Some "dialogue-farmer-crops" } ]
              Extra = [] }
        let crops : Dialogue =
            { Id = "dialogue-farmer-crops"; NpcId = "npc-farmer"
              Text = "Wheat is the easiest crop to start with. Tomatoes take longer but sell for more!"
              Options = [ option "Got it, thanks!" ]
              Extra = [] }
        let shop : Dialogue =
            { Id = "dialogue-merchant-greeting"; NpcId = "npc-merchant"
              Text = "Welcome! I buy crops and sell seeds, tools and fertilizer. I can also repair worn-out tools."
              Options = [ { option "Let's trade." with OpenShopId = Some "shop-general" }
                          option "Just passing by." ]
              Extra = [] }
        let npc id name x y dialogue appearance : Npc =
            { Npc.Default with
                Id = id; Name = name; X = x; Y = y; SceneId = "scene-farm"; Dialogue = dialogue
                CanMove = false; MovePattern = Some "stationary"; Appearance = appearance }
        let people =
            [ npc "npc-farmer" "Old Farmer" 3.0 6.0 [ greeting; crops ] "farmer"
              npc "npc-merchant" "Merchant Mia" 12.0 3.0 [ shop ] "merchant" ]
        people, [ greeting; crops; shop ]

    let private quests () : Quest list =
        [ { Quest.Default with
              Id = "quest-first-harvest"; Name = "First Harvest"
              Description = "Plant and harvest your first crop to learn the basics of farming."
              Giver = Some "npc-farmer"; Status = "not-started"; AutoStart = Some true; Repeatable = Some false
              Objectives =
                [ { QuestObjective.Default with
                      Id = "obj-harvest-wheat"; Type = "harvest"; Description = "Harvest 3 wheat"
                      TargetCropType = Some "wheat"; TargetCropQuantity = Some 3.0; Completed = false; Progress = 0.0 } ]
              Rewards = { QuestRewards.Default with Money = Some 100.0; Items = Some [ { ItemId = "seed-tomato"; Quantity = 5.0 } ] } }
          { Quest.Default with
              Id = "quest-go-shopping"; Name = "Supply Run"
              Description = "Meet Merchant Mia — she buys your harvest and sells seeds, tools and fertilizer."
              Giver = Some "npc-farmer"; Status = "not-started"; AutoStart = Some true; Repeatable = Some false
              Prerequisites = Some [ "quest-first-harvest" ]
              Objectives =
                [ { QuestObjective.Default with
                      Id = "obj-talk-merchant"; Type = "talk"; Description = "Talk to Merchant Mia"
                      TargetNpcId = Some "npc-merchant"; Completed = false; Progress = 0.0 } ]
              Rewards = { QuestRewards.Default with Money = Some 50.0 } } ]

    let private showcaseItems () : Item list =
        let item id name description kind cap value : Item =
            { Item.Default with Id = id; Name = name; Description = description; Type = kind; Stackable = true; MaxStack = cap; Value = value }
        [ item "machine-kitchen" "Kitchen" "A cooking station — place it, then cook nearby" "material" 9.0 180.0
          item "machine-workbench" "Workbench" "A carpentry station — place it, then build nearby" "material" 9.0 150.0
          item "food-veggie-soup" "Veggie Soup" "A hearty soup simmered from garden vegetables" "crop" 99.0 140.0
          item "food-fruit-tart" "Fruit Tart" "A sweet baked tart bursting with fruit" "crop" 99.0 220.0
          item "furniture-tool-rack" "Tool Rack" "A sturdy wooden rack for hanging tools" "material" 9.0 90.0
          item "furniture-planter-box" "Planter Box" "A handmade wooden planter box" "material" 9.0 140.0
          { item "snack-trail-mix" "Trail Mix" "A foraged snack. Use it for a quick pick-me-up." "material" 20.0 8.0 with
              UseActionId = Some "action-forage-snack"; ConsumeOnUse = Some true }
          item "machine-altar" "Enchanter's Altar" "A humming altar — place it, then enchant nearby" "material" 9.0 400.0
          { item "charm-growth" "Growth Charm" "A quartz charm warm to the touch. Use it to feel its blessing." "material" 10.0 320.0 with
              UseActionId = Some "action-growth-blessing"; ConsumeOnUse = Some true } ]

    let private showcaseMachines () : MachineTypeDefinition list =
        let machine id name description color category : MachineTypeDefinition =
            { MachineTypeDefinition.Default with
                Id = id; Name = name; Description = description; Color = color
                ItemId = Some id; BlocksMovement = true; StationCategories = [ category ] }
        [ machine "machine-kitchen" "Kitchen" "Provides the cooking station needed for cooking recipes" "#c2703a" "cooking"
          machine "machine-workbench" "Workbench" "Provides the carpentry station needed for carpentry recipes" "#8a6a3f" "carpentry"
          machine "machine-altar" "Enchanter's Altar" "Provides the magic station needed for enchanting recipes" "#7a5bb5" "magic" ]

    let private showcaseRecipes () : RecipeDefinition list =
        let recipe id name inputs output category station : RecipeDefinition =
            { RecipeDefinition.Default with
                Id = id; Name = name
                Inputs = inputs |> List.map (fun (id, quantity) -> ({ ItemId = id; Quantity = quantity } : RecipeIngredient))
                Outputs = [ { ItemId = output; Quantity = 1.0 } ]
                ProcessingMinutes = 0.0; Category = category; RequiresStationCategory = station }
        [ recipe "recipe-craft-kitchen" "Kitchen" ["material-stone", 8.0; "material-wood", 6.0] "machine-kitchen" "crafting" None
          recipe "recipe-craft-workbench" "Workbench" ["material-wood", 10.0; "material-stone", 2.0] "machine-workbench" "crafting" None
          recipe "recipe-cook-veggie-soup" "Veggie Soup" ["crop-carrot", 2.0; "crop-potato", 2.0] "food-veggie-soup" "cooking" (Some "cooking")
          recipe "recipe-cook-fruit-tart" "Fruit Tart" ["crop-wheat", 2.0; "crop-strawberry", 2.0] "food-fruit-tart" "cooking" (Some "cooking")
          recipe "recipe-craft-tool-rack" "Tool Rack" ["material-wood", 8.0] "furniture-tool-rack" "carpentry" (Some "carpentry")
          recipe "recipe-craft-planter-box" "Planter Box" ["material-wood", 6.0; "material-stone", 3.0] "furniture-planter-box" "carpentry" (Some "carpentry")
          recipe "recipe-growth-charm" "Growth Charm" ["gem-quartz", 1.0; "material-fiber", 3.0] "charm-growth" "magic" (Some "magic") ]

    let private actions () : ActionDef list =
        let action id name description message flag : ActionDef =
            { ActionDef.Default with
                Id = id; Name = name; Description = description; FailMessage = ""; EnergyCost = 0.0
                Outcomes =
                    [ { EventOutcome.Default with Type = "message"; Message = Some message }
                      { EventOutcome.Default with Type = "setFlag"; FlagName = Some flag } ] }
        [ action "action-growth-blessing" "Growth Blessing" "The charm blesses your fields for the day." "A warm green light washes over the farm…" "growth-blessing-today"
          action "action-forage-snack" "Trail Snack" "Munch a foraged snack to recover a little energy." "You feel refreshed!" "snacked-today" ]

    let private minigames () : MinigameDef list =
        [ { MinigameDef.Default with
              Id = "fishing"; Name = "Fishing"; Kind = "timing-bar"
              Config =
                [ "speed", JNumber 0.9
                  "targetSize", JNumber 0.2
                  "prompt", JString "Hook the fish — stop the marker in the green zone!" ] } ]

    let pack () : ContentPack =
        let people, dialogues = npcs ()
        { Manifest =
            { PackManifest.Default with
                Id = "content-default"; Name = "Farm Essentials"; Version = "1.0.0"
                Description = Some "The built-in farming game: crops, tools, recipes, animals, fishing, weather and the starter farm."
                Author = Some "farm-game-engine"; EngineCompatibility = "*"; Base = true
                Permissions = { PackPermissions.Default with Hooks = []; ContentInject = true; UiPanels = false } }
          Content =
            { PackContent.Default with
                Crops = Builtin.crops () |> List.map snd
                Items = Builtin.items () @ showcaseItems ()
                Recipes = Builtin.recipes () @ showcaseRecipes ()
                MachineTypes = Builtin.machineTypes () @ showcaseMachines ()
                NodeTypes = Builtin.nodeTypes () @ Builtin.mineNodeTypes ()
                AnimalSpecies = Builtin.animalSpecies ()
                FishTables = Builtin.fishTables ()
                WeatherTypes = MigrationsSchema.DefaultWeatherConfig().Types
                Npcs = people
                Dialogues = dialogues
                Scenes = [ farm () ]
                Quests = quests ()
                Shops = [ Builtin.shop () ]
                Actions = actions ()
                Minigames = minigames ()
                PlayerStart =
                    Some
                        { PackPlayerStart.Default with
                            SceneId = Some "scene-farm"; X = Some 8.0; Y = Some 9.0; Money = Some 100.0
                            Inventory =
                                [ for id, quantity in [ "seed-wheat", 10.0; "seed-tomato", 5.0; "tool-hoe", 1.0
                                                        "tool-watering-can", 1.0; "fertilizer-basic", 10.0; "snack-trail-mix", 2.0 ] ->
                                      ({ ItemId = id; Quantity = quantity } : PackStartItem) ] } }
          Plugins = []
          Extra = [] }

    let player sceneId : Player =
        { Player.Default with
            X = 5.0; Y = 5.0; Direction = "down"; SceneId = sceneId; MaxInventorySize = 20.0; Money = 100.0 }

    let private empty now : GameProject =
        { GameProject.Default with
            SchemaVersion = ProjectSchema.CurrentProjectSchemaVersion; Id = "project-1"; Name = "My Farming Game"
            Version = "2.0"; Player = player ""; StartSceneId = ""; Mode = "play"; SelectedTileType = "grass"
            CurrentTime = now; CurrentSeason = "spring"; CurrentDay = 1.0
            CurrentTimeMinutes = SettingsSchema.DefaultProjectSettings.Time.DayStartMinute; CurrentYear = 1.0
            Settings = SettingsSchema.DefaultProjectSettings; GameStartTime = now }

    let initial now : GameProject =
        let project, problems = PackMerge.applyToProject (empty now) (pack ())
        if not problems.IsEmpty then invalidOp (problems |> List.map (fun p -> p.Message) |> String.concat "; ")
        { project with Weather = { MigrationsSchema.DefaultWeatherConfig() with Types = project.Weather.Types } }

    let blank now : GameProject =
        { empty now with
            Id = "project-blank"; Name = "Untitled Game"; Mode = "tiles"
            Scenes = [ AuthoringTiles.CreateEmptyScene("scene-main", "Main", 12.0, 12.0) ]
            Items = Builtin.items (); Player = player "scene-main"; StartSceneId = "scene-main"
            Shops = [ Builtin.shop () ]; Weather = MigrationsSchema.DefaultWeatherConfig() }
