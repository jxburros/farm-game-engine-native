namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// Authored starter definitions from engine-core/content-builtin.ts. Factories return fresh
/// collections so editing one project never changes another project's defaults.
module Builtin =
    /// Built-in crops by id, in pack order.
    let crops () : (string * CropDefinition) list =
        let add id name seed value time days stages seasons regrow regrowthTime regrowthDays size low high chance =
            id,
            { CropDefinition.Default with
                Id = id; Name = name; SeedCost = seed; BaseHarvestValue = value
                GrowthTime = time; GrowthDays = Some days; Stages = stages; Seasons = seasons
                CanRegrow = regrow; RegrowthTime = regrowthTime; RegrowthDays = regrowthDays
                MultiTile = (size |> Option.map (fun (w, h) -> { Width = w; Height = h }))
                YieldMin = low; YieldMax = high; MutationChance = Some chance }
        [ add "wheat" "Wheat" 10.0 25.0 15000.0 3.0 4.0 ["spring"; "fall"] false None None None 1.0 2.0 0.01
          add "corn" "Corn" 15.0 40.0 25000.0 5.0 5.0 ["summer"; "fall"] false None None None 1.0 3.0 0.015
          add "tomato" "Tomato" 20.0 50.0 20000.0 4.0 5.0 ["summer"] true (Some 8000.0) (Some 2.0) None 1.0 3.0 0.02
          add "carrot" "Carrot" 8.0 20.0 12000.0 2.0 4.0 ["spring"; "fall"; "winter"] false None None None 1.0 2.0 0.008
          add "potato" "Potato" 12.0 30.0 18000.0 4.0 4.0 ["spring"; "fall"] false None None None 2.0 4.0 0.012
          add "strawberry" "Strawberry" 30.0 60.0 22000.0 4.0 5.0 ["spring"] true (Some 10000.0) (Some 2.0) None 1.0 2.0 0.025
          add "pumpkin" "Pumpkin" 50.0 150.0 35000.0 7.0 6.0 ["fall"] false None None (Some(2.0, 2.0)) 1.0 1.0 0.05
          add "cauliflower" "Cauliflower" 40.0 120.0 28000.0 6.0 5.0 ["spring"] false None None (Some(2.0, 2.0)) 1.0 1.0 0.04
          add "blueberry" "Blueberry" 35.0 70.0 24000.0 5.0 5.0 ["summer"] true (Some 9000.0) (Some 2.0) None 2.0 5.0 0.03 ]

    let items () : Item list =
        let result = ResizeArray<Item>()
        for _, crop in crops () do
            result.Add
                { Item.Default with
                    Id = "seed-" + crop.Id; Name = crop.Name + " Seeds"
                    Description = "Plant these to grow " + crop.Name.ToLowerInvariant(); Type = "seed"
                    Stackable = true; MaxStack = 99.0; Value = crop.SeedCost; CropType = Some crop.Id }
            result.Add
                { Item.Default with
                    Id = "crop-" + crop.Id; Name = crop.Name
                    Description = "Fresh " + crop.Name.ToLowerInvariant(); Type = "crop"
                    Stackable = true; MaxStack = 99.0; Value = crop.BaseHarvestValue; CropType = Some crop.Id }
        let tool id name description value kind tier =
            { Item.Default with
                Id = id; Name = name; Description = description; Type = "tool"; Stackable = false
                MaxStack = 1.0; Value = value; ToolType = Some kind; ToolPower = Some tier
                ToolTier = (if tier = 2.0 then Some 2.0 else None)
                Durability = Some(100.0 * tier); MaxDurability = Some(100.0 * tier) }
        result.Add(tool "tool-hoe" "Hoe" "Till grass into soil for planting" 50.0 "hoe" 1.0)
        result.Add(tool "tool-watering-can" "Watering Can" "Water your crops to help them grow" 50.0 "watering-can" 1.0)
        result.Add(tool "tool-axe" "Axe" "Chop down trees and wooden objects" 100.0 "axe" 1.0)
        result.Add(tool "tool-pickaxe" "Pickaxe" "Break rocks and mine ore" 150.0 "pickaxe" 1.0)
        result.Add(tool "tool-scythe" "Scythe" "Harvest crops quickly in an area" 200.0 "scythe" 1.0)
        result.Add(tool "tool-hoe-2" "Copper Hoe" "A sturdier hoe — tills with less effort" 250.0 "hoe" 2.0)
        result.Add(tool "tool-watering-can-2" "Copper Watering Can" "Waters a wider area with less effort" 250.0 "watering-can" 2.0)
        result.Add(tool "tool-axe-2" "Copper Axe" "Fells trees in fewer swings" 400.0 "axe" 2.0)
        result.Add(tool "tool-pickaxe-2" "Copper Pickaxe" "Cracks boulders lesser picks cannot" 500.0 "pickaxe" 2.0)
        let plain id name description kind maxStack value =
            { Item.Default with
                Id = id; Name = name; Description = description; Type = kind
                Stackable = true; MaxStack = maxStack; Value = value }
        result.Add(plain "material-wood" "Wood" "Sturdy timber from trees and stumps" "material" 999.0 5.0)
        result.Add(plain "material-stone" "Stone" "Rough stone chipped from rocks" "material" 999.0 4.0)
        result.Add(plain "material-fiber" "Fiber" "Plant fiber cut from weeds" "material" 999.0 2.0)
        result.Add(plain "fertilizer-basic" "Basic Fertilizer" "Improves soil quality and crop growth" "fertilizer" 99.0 10.0)
        result.Add(plain "fertilizer-quality" "Quality Fertilizer" "Increases chance of higher quality crops" "fertilizer" 99.0 25.0)
        result.Add(plain "gift-flower" "Flower" "A beautiful flower that makes a nice gift" "gift" 99.0 20.0)
        result.Add(tool "tool-fishing-rod" "Fishing Rod" "Catch fish from water tiles" 120.0 "fishing-rod" 1.0)
        result.Add(plain "fish-carp" "Carp" "A common pond fish" "fish" 99.0 18.0)
        result.Add(plain "fish-perch" "Perch" "A quick freshwater fish" "fish" 99.0 30.0)
        result.Add(plain "fish-catfish" "Catfish" "A prized whiskered catch" "fish" 99.0 75.0)
        result.Add(plain "junk-boot" "Old Boot" "Someone lost this a long time ago" "material" 99.0 1.0)
        result.Add(plain "feed-hay" "Hay" "Animal feed — one serving a day keeps them happy" "material" 999.0 3.0)
        result.Add(plain "product-egg" "Egg" "A fresh egg" "crop" 99.0 22.0)
        result.Add(plain "product-milk" "Milk" "A pail of fresh milk" "crop" 99.0 48.0)
        result.Add(plain "ore-copper" "Copper Ore" "Raw copper, ready for smelting" "material" 999.0 12.0)
        result.Add(plain "ore-iron" "Iron Ore" "Raw iron, ready for smelting" "material" 999.0 20.0)
        result.Add(plain "gem-quartz" "Quartz" "A translucent crystal" "material" 999.0 40.0)
        result.Add(plain "bar-copper" "Copper Bar" "A smelted copper ingot" "material" 999.0 45.0)
        result.Add(plain "bar-iron" "Iron Bar" "A smelted iron ingot" "material" 999.0 80.0)
        result.Add(plain "machine-furnace" "Furnace" "Smelts ore into bars (place it, then load a recipe)" "material" 9.0 150.0)
        result.Add(plain "machine-preserves" "Preserves Jar" "Turns crops into preserves worth more" "material" 9.0 200.0)
        result.Add(plain "food-preserves" "Preserves" "Sweet preserved produce" "crop" 99.0 120.0)
        List.ofSeq result

    let private node id name health tool tier item low high respawn color blocking : NodeTypeDefinition =
        { NodeTypeDefinition.Default with
            Id = id; Name = name; Health = health; RequiredTool = tool; RequiredToolTier = tier
            Drops = [ { ItemId = item; Min = low; Max = high; Weight = 1.0; Extra = [] } ]
            RespawnDays = Some respawn; Color = color; BlocksMovement = blocking }

    let nodeTypes () =
        [ node "node-tree" "Tree" 4.0 "axe" 1.0 "material-wood" 2.0 4.0 None "#3f6d33" true
          node "node-stump" "Stump" 2.0 "axe" 1.0 "material-wood" 1.0 2.0 (Some 3.0) "#6d5233" true
          node "node-rock" "Rock" 3.0 "pickaxe" 1.0 "material-stone" 1.0 3.0 (Some 3.0) "#8a8a95" true
          node "node-boulder" "Boulder" 6.0 "pickaxe" 2.0 "material-stone" 4.0 8.0 None "#5e5e6b" true
          node "node-weeds" "Weeds" 1.0 "scythe" 1.0 "material-fiber" 1.0 2.0 (Some 2.0) "#7d9a3f" false ]

    let mineNodeTypes () =
        [ node "node-mine-rock" "Mine Rock" 2.0 "pickaxe" 1.0 "material-stone" 1.0 2.0 None "#6e6e78" true
          node "node-copper-ore" "Copper Node" 3.0 "pickaxe" 1.0 "ore-copper" 1.0 3.0 None "#b87333" true
          node "node-iron-ore" "Iron Node" 4.0 "pickaxe" 2.0 "ore-iron" 1.0 3.0 None "#a19d94" true
          node "node-quartz" "Quartz Crystal" 2.0 "pickaxe" 1.0 "gem-quartz" 1.0 1.0 None "#cfe3ee" true ]

    let mineBands () =
        let rock id weight : MineRockWeight = { NodeTypeId = id; Weight = weight }
        [ { FromFloor = 1.0; ToFloor = 7.0; Density = 0.3
            Rocks = [ rock "node-mine-rock" 6.0; rock "node-copper-ore" 3.0; rock "node-quartz" 1.0 ] }
          { FromFloor = 8.0; ToFloor = 20.0; Density = 0.35
            Rocks = [ rock "node-mine-rock" 4.0; rock "node-copper-ore" 3.0; rock "node-iron-ore" 3.0; rock "node-quartz" 1.0 ] } ]

    let machineTypes () =
        let machine id name description color : MachineTypeDefinition =
            { MachineTypeDefinition.Default with
                Id = id; Name = name; Description = description; Color = color; ItemId = Some id
                BlocksMovement = true; StationCategories = [] }
        [ machine "machine-furnace" "Furnace" "Smelts ore into metal bars" "#8a4a3a"
          machine "machine-preserves" "Preserves Jar" "Preserves crops into higher-value goods" "#7a4a8a" ]

    let recipes () =
        let ingredients xs = xs |> List.map (fun (id, quantity) -> ({ ItemId = id; Quantity = quantity } : RecipeIngredient))
        let recipe id name inputs output minutes machine category skill : RecipeDefinition =
            { RecipeDefinition.Default with
                Id = id; Name = name; Inputs = ingredients inputs; Outputs = ingredients [ output ]
                ProcessingMinutes = minutes; MachineTypeId = machine; Category = category
                Unlock =
                    skill
                    |> Option.map (fun (kind, level) ->
                        { RecipeUnlock.Default with Skill = Some { Skill = kind; Level = level } }) }
        [ recipe "recipe-craft-furnace" "Furnace" ["material-stone", 10.0; "ore-copper", 2.0] ("machine-furnace", 1.0) 0.0 None "crafting" None
          recipe "recipe-craft-preserves-jar" "Preserves Jar" ["material-wood", 12.0; "material-stone", 4.0] ("machine-preserves", 1.0) 0.0 None "crafting" (Some("farming", 1.0))
          recipe "recipe-craft-hay" "Hay Bundle" ["material-fiber", 3.0] ("feed-hay", 2.0) 0.0 None "farming" None
          recipe "recipe-smelt-copper" "Copper Bar" ["ore-copper", 3.0; "material-wood", 1.0] ("bar-copper", 1.0) 120.0 (Some "machine-furnace") "smithing" None
          recipe "recipe-smelt-iron" "Iron Bar" ["ore-iron", 3.0; "material-wood", 1.0] ("bar-iron", 1.0) 180.0 (Some "machine-furnace") "smithing" (Some("mining", 2.0))
          recipe "recipe-preserve-wheat" "Wheat Preserves" ["crop-wheat", 3.0] ("food-preserves", 1.0) 360.0 (Some "machine-preserves") "cooking" None ]

    let animalSpecies () =
        let species id name cost product interval adult color : AnimalSpeciesDefinition =
            { AnimalSpeciesDefinition.Default with
                Id = id; Name = name; PurchaseCost = cost; FeedItemId = Some "feed-hay"; ProductItemId = product
                ProductIntervalDays = interval; DaysToAdult = adult; Color = color }
        [ species "animal-chicken" "Chicken" 400.0 "product-egg" 1.0 3.0 "#e8e0c3"
          species "animal-cow" "Cow" 1500.0 "product-milk" 2.0 5.0 "#d3b28a" ]

    let fishTables () =
        let entry id weight difficulty : FishTableEntry = { ItemId = id; Weight = weight; Difficulty = difficulty }
        [ { FishTable.Default with
              Id = "fish-table-default"; Name = "Pond Fish"; JunkChance = 0.15; JunkItemId = Some "junk-boot"
              Entries = [ entry "fish-carp" 6.0 0.15; entry "fish-perch" 3.0 0.35; entry "fish-catfish" 1.0 0.6 ] } ]

    let shop () =
        let entry id : ShopStockEntry = { ShopStockEntry.Default with ItemId = id }
        let stock =
            [ for _, crop in crops () do
                  yield { entry ("seed-" + crop.Id) with Seasons = Some crop.Seasons }
              yield entry "fertilizer-basic"
              yield { entry "fertilizer-quality" with DailyLimit = Some 5.0 }
              for id in [ "tool-hoe"; "tool-watering-can"; "tool-axe"; "tool-pickaxe"; "tool-scythe"
                          "tool-hoe-2"; "tool-watering-can-2"; "tool-axe-2"; "tool-pickaxe-2"
                          "tool-fishing-rod"; "feed-hay"; "machine-furnace"; "machine-preserves" ] do
                  yield entry id ]
        { ShopDefinition.Default with
            Id = "shop-general"; Name = "General Store"; Stock = stock
            SellPriceMultiplier = 1.0; BuysItems = true; RepairsTools = true; RepairCostPerPoint = 0.5 }
