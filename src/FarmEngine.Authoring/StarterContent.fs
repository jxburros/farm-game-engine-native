namespace FarmEngine.Authoring

open System
open System.Collections.Generic
open System.Text.Json
open FarmEngine.Json
open FarmEngine.Schemas

/// The authored Farm Essentials pack (packages/content-default/src/index.ts).
/// Every call constructs fresh data. This module never reads a clock or runs simulation rules.
module StarterContent =
    let private list (xs: seq<'T>) = List<'T>(xs)

    let private farm () =
        let scene = AuthoringTiles.CreateEmptyScene("scene-farm", "Farm", 16.0, 12.0)
        let paint x y kind = scene.Tiles[y][x] <- AuthoringTiles.SetTileLayer(scene.Tiles[y][x], kind)
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
            scene.Tiles[y][x] <- Records.withValue scene.Tiles.[y].[x] "Node" (box (TileNode(TypeId = kind, RemainingHealth = health)))
        scene

    let private npcs () =
        let greeting = Dialogue(Id = "dialogue-farmer-greeting", NpcId = "npc-farmer",
                           Text = "Welcome to the farm! Plant crops in the soil and watch them grow. Come back when you need advice!",
                           Options = list [DialogueOption(Text = "Thanks for the help!")
                                           DialogueOption(Text = "What crops grow best here?", NextDialogueId = "dialogue-farmer-crops")])
        let crops = Dialogue(Id = "dialogue-farmer-crops", NpcId = "npc-farmer",
                        Text = "Wheat is the easiest crop to start with. Tomatoes take longer but sell for more!",
                        Options = list [DialogueOption(Text = "Got it, thanks!")])
        let shop = Dialogue(Id = "dialogue-merchant-greeting", NpcId = "npc-merchant",
                       Text = "Welcome! I buy crops and sell seeds, tools and fertilizer. I can also repair worn-out tools.",
                       Options = list [DialogueOption(Text = "Let's trade.", OpenShopId = "shop-general")
                                       DialogueOption(Text = "Just passing by.")])
        let people = list [Npc(Id = "npc-farmer", Name = "Old Farmer", X = 3.0, Y = 6.0,
                               SceneId = "scene-farm", Dialogue = list [greeting; crops],
                               CanMove = false, MovePattern = "stationary", Appearance = "farmer")
                           Npc(Id = "npc-merchant", Name = "Merchant Mia", X = 12.0, Y = 3.0,
                               SceneId = "scene-farm", Dialogue = list [shop],
                               CanMove = false, MovePattern = "stationary", Appearance = "merchant")]
        people, list [greeting; crops; shop]

    let private quests () =
        list [Quest(Id = "quest-first-harvest", Name = "First Harvest",
                    Description = "Plant and harvest your first crop to learn the basics of farming.",
                    Giver = "npc-farmer", Status = "not-started", AutoStart = true, Repeatable = false,
                    Objectives = list [QuestObjective(Id = "obj-harvest-wheat", Type = "harvest",
                        Description = "Harvest 3 wheat", TargetCropType = "wheat", TargetCropQuantity = Nullable 3.0,
                        Completed = false, Progress = 0.0)],
                    Rewards = QuestRewards(Money = Nullable 100.0, Items = list [QuestRewardItem(ItemId = "seed-tomato", Quantity = 5.0)]))
              Quest(Id = "quest-go-shopping", Name = "Supply Run",
                    Description = "Meet Merchant Mia — she buys your harvest and sells seeds, tools and fertilizer.",
                    Giver = "npc-farmer", Status = "not-started", AutoStart = true, Repeatable = false,
                    Prerequisites = list ["quest-first-harvest"],
                    Objectives = list [QuestObjective(Id = "obj-talk-merchant", Type = "talk",
                        Description = "Talk to Merchant Mia", TargetNpcId = "npc-merchant", Completed = false, Progress = 0.0)],
                    Rewards = QuestRewards(Money = Nullable 50.0))]

    let private showcaseItems () =
        let item id name description kind cap value =
            Item(Id = id, Name = name, Description = description, Type = kind, Stackable = true, MaxStack = cap, Value = value)
        list [item "machine-kitchen" "Kitchen" "A cooking station — place it, then cook nearby" "material" 9.0 180.0
              item "machine-workbench" "Workbench" "A carpentry station — place it, then build nearby" "material" 9.0 150.0
              item "food-veggie-soup" "Veggie Soup" "A hearty soup simmered from garden vegetables" "crop" 99.0 140.0
              item "food-fruit-tart" "Fruit Tart" "A sweet baked tart bursting with fruit" "crop" 99.0 220.0
              item "furniture-tool-rack" "Tool Rack" "A sturdy wooden rack for hanging tools" "material" 9.0 90.0
              item "furniture-planter-box" "Planter Box" "A handmade wooden planter box" "material" 9.0 140.0
              Item(Id = "snack-trail-mix", Name = "Trail Mix", Description = "A foraged snack. Use it for a quick pick-me-up.",
                   Type = "material", Stackable = true, MaxStack = 20.0, Value = 8.0, UseActionId = "action-forage-snack", ConsumeOnUse = true)
              item "machine-altar" "Enchanter's Altar" "A humming altar — place it, then enchant nearby" "material" 9.0 400.0
              Item(Id = "charm-growth", Name = "Growth Charm", Description = "A quartz charm warm to the touch. Use it to feel its blessing.",
                   Type = "material", Stackable = true, MaxStack = 10.0, Value = 320.0, UseActionId = "action-growth-blessing", ConsumeOnUse = true)]

    let private showcaseMachines () =
        let machine id name description color category =
            MachineTypeDefinition(Id = id, Name = name, Description = description, Color = color,
                                  ItemId = id, BlocksMovement = true, StationCategories = list [category])
        list [machine "machine-kitchen" "Kitchen" "Provides the cooking station needed for cooking recipes" "#c2703a" "cooking"
              machine "machine-workbench" "Workbench" "Provides the carpentry station needed for carpentry recipes" "#8a6a3f" "carpentry"
              machine "machine-altar" "Enchanter's Altar" "Provides the magic station needed for enchanting recipes" "#7a5bb5" "magic"]

    let private showcaseRecipes () =
        let recipe id name inputs output category station =
            RecipeDefinition(Id = id, Name = name,
                Inputs = list (inputs |> Seq.map (fun (id, quantity) -> RecipeIngredient(ItemId = id, Quantity = quantity))),
                Outputs = list [RecipeIngredient(ItemId = output, Quantity = 1.0)],
                ProcessingMinutes = 0.0, Category = category, RequiresStationCategory = station)
        list [recipe "recipe-craft-kitchen" "Kitchen" ["material-stone", 8.0; "material-wood", 6.0] "machine-kitchen" "crafting" null
              recipe "recipe-craft-workbench" "Workbench" ["material-wood", 10.0; "material-stone", 2.0] "machine-workbench" "crafting" null
              recipe "recipe-cook-veggie-soup" "Veggie Soup" ["crop-carrot", 2.0; "crop-potato", 2.0] "food-veggie-soup" "cooking" "cooking"
              recipe "recipe-cook-fruit-tart" "Fruit Tart" ["crop-wheat", 2.0; "crop-strawberry", 2.0] "food-fruit-tart" "cooking" "cooking"
              recipe "recipe-craft-tool-rack" "Tool Rack" ["material-wood", 8.0] "furniture-tool-rack" "carpentry" "carpentry"
              recipe "recipe-craft-planter-box" "Planter Box" ["material-wood", 6.0; "material-stone", 3.0] "furniture-planter-box" "carpentry" "carpentry"
              recipe "recipe-growth-charm" "Growth Charm" ["gem-quartz", 1.0; "material-fiber", 3.0] "charm-growth" "magic" "magic"]

    let private actions () =
        let action id name description message flag =
            ActionDef(Id = id, Name = name, Description = description, Conditions = list [], FailMessage = "", EnergyCost = 0.0,
                Outcomes = list [EventOutcome(Type = "message", Message = message); EventOutcome(Type = "setFlag", FlagName = flag)])
        list [action "action-growth-blessing" "Growth Blessing" "The charm blesses your fields for the day." "A warm green light washes over the farm…" "growth-blessing-today"
              action "action-forage-snack" "Trail Snack" "Munch a foraged snack to recover a little energy." "You feel refreshed!" "snacked-today"]

    let private minigames () =
        let config = OrderedDictionary<string, JsonElement>()
        config["speed"] <- Js.Value 0.9
        config["targetSize"] <- Js.Value 0.2
        config["prompt"] <- Js.Value "Hook the fish — stop the marker in the green zone!"
        list [MinigameDef(Id = "fishing", Name = "Fishing", Kind = "timing-bar", Config = config, ResultTiers = list [])]

    let pack () =
        let people, dialogues = npcs ()
        ContentPack(
            Manifest = PackManifest(Id = "content-default", Name = "Farm Essentials", Version = "1.0.0",
                Description = "The built-in farming game: crops, tools, recipes, animals, fishing, weather and the starter farm.",
                Author = "farm-game-engine", EngineCompatibility = "*", Base = true,
                Dependencies = list [], Overrides = list [],
                Permissions = PackPermissions(Hooks = list [], ContentInject = true, UiPanels = false)),
            Content = PackContent(Crops = list (Builtin.crops ()).Values,
                Items = list (Seq.append (Builtin.items ()) (showcaseItems ())),
                Recipes = list (Seq.append (Builtin.recipes ()) (showcaseRecipes ())),
                MachineTypes = list (Seq.append (Builtin.machineTypes ()) (showcaseMachines ())),
                NodeTypes = list (Seq.append (Builtin.nodeTypes ()) (Builtin.mineNodeTypes ())),
                AnimalSpecies = Builtin.animalSpecies (), FishTables = Builtin.fishTables (),
                WeatherTypes = MigrationsSchema.DefaultWeatherConfig().Types,
                Npcs = people, Dialogues = dialogues, Scenes = list [farm ()], Events = list [], Quests = quests (),
                Shops = list [Builtin.shop ()], Actions = actions (), Minigames = minigames (),
                PlayerStart = PackPlayerStart(SceneId = "scene-farm", X = Nullable 8.0, Y = Nullable 9.0, Money = Nullable 100.0,
                    Inventory = list [for id, quantity in ["seed-wheat", 10.0; "seed-tomato", 5.0; "tool-hoe", 1.0;
                                                          "tool-watering-can", 1.0; "fertilizer-basic", 10.0; "snack-trail-mix", 2.0] ->
                                          PackStartItem(ItemId = id, Quantity = quantity)])), Plugins = list [])

    let player sceneId =
        Player(X = 5.0, Y = 5.0, Direction = "down", SceneId = sceneId, Inventory = list [], MaxInventorySize = 20.0,
               Money = 100.0, ActiveQuests = list [], CompletedQuests = list [], EquippedTool = null,
               PixelX = 0.0, PixelY = 0.0, TargetX = 0.0, TargetY = 0.0)

    let private empty now =
        GameProject(SchemaVersion = ProjectSchema.CurrentProjectSchemaVersion, Id = "project-1", Name = "My Farming Game",
            Version = "2.0", Player = player "", StartSceneId = "", Mode = "play", SelectedTileType = "grass",
            CurrentTime = now, CurrentSeason = "spring", CurrentDay = 1.0,
            CurrentTimeMinutes = SettingsSchema.DefaultProjectSettings.Time.DayStartMinute, CurrentYear = 1.0,
            Settings = JsonDefaults.DeepClone SettingsSchema.DefaultProjectSettings,
            Weather = WeatherConfig(Types = list [], Table = OrderedDictionary()),
            Mine = MineConfig(Enabled = false), GameStartTime = now)

    let initial now =
        let project, problems = PackMerge.applyToProject (empty now) (pack ())
        if not problems.IsEmpty then invalidOp (problems |> List.map (fun p -> p.Message) |> String.concat "; ")
        let weather = Records.withValue (MigrationsSchema.DefaultWeatherConfig()) "Types" (box project.Weather.Types)
        Records.withValue project "Weather" (box weather)

    let blank now =
        Records.withValues (empty now)
            [ "Id", box "project-blank"; "Name", box "Untitled Game"; "Mode", box "tiles"
              "Scenes", box (list [AuthoringTiles.CreateEmptyScene("scene-main", "Main", 12.0, 12.0)])
              "Items", box (Builtin.items ()); "Player", box (player "scene-main"); "StartSceneId", box "scene-main"
              "Shops", box (list [Builtin.shop ()]); "Weather", box (MigrationsSchema.DefaultWeatherConfig()) ]
