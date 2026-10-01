namespace FarmEngine.Schemas

open FarmEngine.Authoring

/// JSON for the schema records (Schema.fs), Fable-safe: `decode…` reads a `Json` value the way the
/// retired System.Text.Json contract did (camelCase names, absent keys take the field's default,
/// undeclared keys go to `Extra` or are dropped) and fails on the first value of the wrong kind with
/// a zod-style issue (`scenes.0.width: Expected number, received string`). `encode…` writes declared
/// fields in order, then `Extra`; absent options are left out, except the fields zod declares
/// `.nullable()`, which are written as `null`.
module SchemaJson =
    let rec decodeActionDef (path: Path) (json: Json) : ActionDef =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vDescription = ""
        let mutable vConditions = []
        let mutable vFailMessage = ""
        let mutable vOutcomes = []
        let mutable vEnergyCost = 0.0
        let mutable vHotkey = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "description" -> vDescription <- Decode.string (key :: path) value
            | "conditions" -> vConditions <- (Decode.list decodeEventCondition) (key :: path) value
            | "failMessage" -> vFailMessage <- Decode.string (key :: path) value
            | "outcomes" -> vOutcomes <- (Decode.list decodeEventOutcome) (key :: path) value
            | "energyCost" -> vEnergyCost <- Decode.number (key :: path) value
            | "hotkey" -> vHotkey <- Decode.optional Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Description = vDescription; Conditions = vConditions; FailMessage = vFailMessage; Outcomes = vOutcomes; EnergyCost = vEnergyCost; Hotkey = vHotkey; Extra = List.ofSeq extra }

    and encodeActionDef (value: ActionDef) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "description", JString value.Description
                yield "conditions", (Encode.list encodeEventCondition) value.Conditions
                yield "failMessage", JString value.FailMessage
                yield "outcomes", (Encode.list encodeEventOutcome) value.Outcomes
                yield "energyCost", JNumber value.EnergyCost
                match value.Hotkey with
                | Some v -> yield "hotkey", JString v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeAnimalSpeciesDefinition (path: Path) (json: Json) : AnimalSpeciesDefinition =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vVisual = None
        let mutable vPurchaseCost = 0.0
        let mutable vFeedItemId = None
        let mutable vProductItemId = ""
        let mutable vProductIntervalDays = 1.0
        let mutable vDaysToAdult = 3.0
        let mutable vColor = "#e8d8c3"
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "visual" -> vVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "purchaseCost" -> vPurchaseCost <- Decode.number (key :: path) value
            | "feedItemId" -> vFeedItemId <- Decode.optional Decode.string (key :: path) value
            | "productItemId" -> vProductItemId <- Decode.string (key :: path) value
            | "productIntervalDays" -> vProductIntervalDays <- Decode.number (key :: path) value
            | "daysToAdult" -> vDaysToAdult <- Decode.number (key :: path) value
            | "color" -> vColor <- Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Visual = vVisual; PurchaseCost = vPurchaseCost; FeedItemId = vFeedItemId; ProductItemId = vProductItemId; ProductIntervalDays = vProductIntervalDays; DaysToAdult = vDaysToAdult; Color = vColor; Extra = List.ofSeq extra }

    and encodeAnimalSpeciesDefinition (value: AnimalSpeciesDefinition) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                match value.Visual with
                | Some v -> yield "visual", encodeVisualRef v
                | None -> ()
                yield "purchaseCost", JNumber value.PurchaseCost
                match value.FeedItemId with
                | Some v -> yield "feedItemId", JString v
                | None -> ()
                yield "productItemId", JString value.ProductItemId
                yield "productIntervalDays", JNumber value.ProductIntervalDays
                yield "daysToAdult", JNumber value.DaysToAdult
                yield "color", JString value.Color
                yield! value.Extra
            ]
        )

    and decodeAnimalState (path: Path) (json: Json) : AnimalState =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vSpeciesId = ""
        let mutable vName = ""
        let mutable vSceneId = ""
        let mutable vX = 0.0
        let mutable vY = 0.0
        let mutable vMood = 70.0
        let mutable vFedToday = false
        let mutable vPettedToday = false
        let mutable vAgeDays = 0.0
        let mutable vDaysSinceProduct = 0.0
        let mutable vProductReady = false
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "speciesId" -> vSpeciesId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "sceneId" -> vSceneId <- Decode.string (key :: path) value
            | "x" -> vX <- Decode.number (key :: path) value
            | "y" -> vY <- Decode.number (key :: path) value
            | "mood" -> vMood <- Decode.number (key :: path) value
            | "fedToday" -> vFedToday <- Decode.boolean (key :: path) value
            | "pettedToday" -> vPettedToday <- Decode.boolean (key :: path) value
            | "ageDays" -> vAgeDays <- Decode.number (key :: path) value
            | "daysSinceProduct" -> vDaysSinceProduct <- Decode.number (key :: path) value
            | "productReady" -> vProductReady <- Decode.boolean (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; SpeciesId = vSpeciesId; Name = vName; SceneId = vSceneId; X = vX; Y = vY; Mood = vMood; FedToday = vFedToday; PettedToday = vPettedToday; AgeDays = vAgeDays; DaysSinceProduct = vDaysSinceProduct; ProductReady = vProductReady; Extra = List.ofSeq extra }

    and encodeAnimalState (value: AnimalState) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "speciesId", JString value.SpeciesId
                yield "name", JString value.Name
                yield "sceneId", JString value.SceneId
                yield "x", JNumber value.X
                yield "y", JNumber value.Y
                yield "mood", JNumber value.Mood
                yield "fedToday", JBool value.FedToday
                yield "pettedToday", JBool value.PettedToday
                yield "ageDays", JNumber value.AgeDays
                yield "daysSinceProduct", JNumber value.DaysSinceProduct
                yield "productReady", JBool value.ProductReady
                yield! value.Extra
            ]
        )

    and decodeAnimationClip (path: Path) (json: Json) : AnimationClip =
        let members = Decode.object path json
        let mutable vName = ""
        let mutable vLoop = true
        let mutable vFrames = []
        for (key, value) in members do
            match key with
            | "name" -> vName <- Decode.string (key :: path) value
            | "loop" -> vLoop <- Decode.boolean (key :: path) value
            | "frames" -> vFrames <- (Decode.list decodeArtFrame) (key :: path) value
            | _ -> ()
        { Name = vName; Loop = vLoop; Frames = vFrames }

    and encodeAnimationClip (value: AnimationClip) : Json =
        JObject(
            [
                yield "name", JString value.Name
                yield "loop", JBool value.Loop
                yield "frames", (Encode.list encodeArtFrame) value.Frames
            ]
        )

    and decodeArtFrame (path: Path) (json: Json) : ArtFrame =
        let members = Decode.object path json
        let mutable vAssetId = None
        let mutable vX = 0.0
        let mutable vY = 0.0
        let mutable vWidth = 0.0
        let mutable vHeight = 0.0
        let mutable vTicks = 6.0
        for (key, value) in members do
            match key with
            | "assetId" -> vAssetId <- Decode.optional Decode.string (key :: path) value
            | "x" -> vX <- Decode.number (key :: path) value
            | "y" -> vY <- Decode.number (key :: path) value
            | "width" -> vWidth <- Decode.number (key :: path) value
            | "height" -> vHeight <- Decode.number (key :: path) value
            | "ticks" -> vTicks <- Decode.number (key :: path) value
            | _ -> ()
        { AssetId = vAssetId; X = vX; Y = vY; Width = vWidth; Height = vHeight; Ticks = vTicks }

    and encodeArtFrame (value: ArtFrame) : Json =
        JObject(
            [
                match value.AssetId with
                | Some v -> yield "assetId", JString v
                | None -> ()
                yield "x", JNumber value.X
                yield "y", JNumber value.Y
                yield "width", JNumber value.Width
                yield "height", JNumber value.Height
                yield "ticks", JNumber value.Ticks
            ]
        )

    and decodeCalendarConfig (path: Path) (json: Json) : CalendarConfig =
        let members = Decode.object path json
        let mutable vSeasons = [ ({ Id = "spring"; Name = "Spring"; Days = 28.0 } : CalendarSeason); ({ Id = "summer"; Name = "Summer"; Days = 28.0 } : CalendarSeason); ({ Id = "fall"; Name = "Fall"; Days = 28.0 } : CalendarSeason); ({ Id = "winter"; Name = "Winter"; Days = 28.0 } : CalendarSeason) ]
        let mutable vFestivals = []
        for (key, value) in members do
            match key with
            | "seasons" -> vSeasons <- (Decode.list decodeCalendarSeason) (key :: path) value
            | "festivals" -> vFestivals <- (Decode.list decodeCalendarFestival) (key :: path) value
            | _ -> ()
        { Seasons = vSeasons; Festivals = vFestivals }

    and encodeCalendarConfig (value: CalendarConfig) : Json =
        JObject(
            [
                yield "seasons", (Encode.list encodeCalendarSeason) value.Seasons
                yield "festivals", (Encode.list encodeCalendarFestival) value.Festivals
            ]
        )

    and decodeCalendarFestival (path: Path) (json: Json) : CalendarFestival =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vSeasonId = ""
        let mutable vDay = 0.0
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "seasonId" -> vSeasonId <- Decode.string (key :: path) value
            | "day" -> vDay <- Decode.number (key :: path) value
            | _ -> ()
        { Id = vId; Name = vName; SeasonId = vSeasonId; Day = vDay }

    and encodeCalendarFestival (value: CalendarFestival) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "seasonId", JString value.SeasonId
                yield "day", JNumber value.Day
            ]
        )

    and decodeCalendarSeason (path: Path) (json: Json) : CalendarSeason =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vDays = 0.0
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "days" -> vDays <- Decode.number (key :: path) value
            | _ -> ()
        { Id = vId; Name = vName; Days = vDays }

    and encodeCalendarSeason (value: CalendarSeason) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "days", JNumber value.Days
            ]
        )

    and decodeContentPack (path: Path) (json: Json) : ContentPack =
        let members = Decode.object path json
        let mutable vManifest = PackManifest.Default
        let mutable vContent = PackContent.Default
        let mutable vPlugins = []
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "manifest" -> vManifest <- decodePackManifest (key :: path) value
            | "content" -> vContent <- decodePackContent (key :: path) value
            | "plugins" -> vPlugins <- (Decode.list decodePackPlugin) (key :: path) value
            | _ -> extra.Add((key, value))
        { Manifest = vManifest; Content = vContent; Plugins = vPlugins; Extra = List.ofSeq extra }

    and encodeContentPack (value: ContentPack) : Json =
        JObject(
            [
                yield "manifest", encodePackManifest value.Manifest
                yield "content", encodePackContent value.Content
                yield "plugins", (Encode.list encodePackPlugin) value.Plugins
                yield! value.Extra
            ]
        )

    and decodeCrop (path: Path) (json: Json) : Crop =
        let members = Decode.object path json
        let mutable vType = ""
        let mutable vPlantedAt = 0.0
        let mutable vPlantedOnDay = None
        let mutable vDaysGrown = None
        let mutable vWithered = None
        let mutable vStage = 0.0
        let mutable vWatered = false
        let mutable vLastWateredDay = None
        let mutable vQuality = ""
        let mutable vMutation = None
        let mutable vIsMultiTileRoot = None
        let mutable vMultiTileId = None
        let mutable vHarvestCount = 0.0
        let mutable vDaysWithoutWater = 0.0
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "type" -> vType <- Decode.string (key :: path) value
            | "plantedAt" -> vPlantedAt <- Decode.number (key :: path) value
            | "plantedOnDay" -> vPlantedOnDay <- Decode.optional Decode.number (key :: path) value
            | "daysGrown" -> vDaysGrown <- Decode.optional Decode.number (key :: path) value
            | "withered" -> vWithered <- Decode.optional Decode.boolean (key :: path) value
            | "stage" -> vStage <- Decode.number (key :: path) value
            | "watered" -> vWatered <- Decode.boolean (key :: path) value
            | "lastWateredDay" -> vLastWateredDay <- Decode.optional Decode.number (key :: path) value
            | "quality" -> vQuality <- Decode.string (key :: path) value
            | "mutation" -> vMutation <- Decode.optional Decode.string (key :: path) value
            | "isMultiTileRoot" -> vIsMultiTileRoot <- Decode.optional Decode.boolean (key :: path) value
            | "multiTileId" -> vMultiTileId <- Decode.optional Decode.string (key :: path) value
            | "harvestCount" -> vHarvestCount <- Decode.number (key :: path) value
            | "daysWithoutWater" -> vDaysWithoutWater <- Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { Type = vType; PlantedAt = vPlantedAt; PlantedOnDay = vPlantedOnDay; DaysGrown = vDaysGrown; Withered = vWithered; Stage = vStage; Watered = vWatered; LastWateredDay = vLastWateredDay; Quality = vQuality; Mutation = vMutation; IsMultiTileRoot = vIsMultiTileRoot; MultiTileId = vMultiTileId; HarvestCount = vHarvestCount; DaysWithoutWater = vDaysWithoutWater; Extra = List.ofSeq extra }

    and encodeCrop (value: Crop) : Json =
        JObject(
            [
                yield "type", JString value.Type
                yield "plantedAt", JNumber value.PlantedAt
                match value.PlantedOnDay with
                | Some v -> yield "plantedOnDay", JNumber v
                | None -> ()
                match value.DaysGrown with
                | Some v -> yield "daysGrown", JNumber v
                | None -> ()
                match value.Withered with
                | Some v -> yield "withered", JBool v
                | None -> ()
                yield "stage", JNumber value.Stage
                yield "watered", JBool value.Watered
                match value.LastWateredDay with
                | Some v -> yield "lastWateredDay", JNumber v
                | None -> ()
                yield "quality", JString value.Quality
                match value.Mutation with
                | Some v -> yield "mutation", JString v
                | None -> yield "mutation", JNull
                match value.IsMultiTileRoot with
                | Some v -> yield "isMultiTileRoot", JBool v
                | None -> ()
                match value.MultiTileId with
                | Some v -> yield "multiTileId", JString v
                | None -> ()
                yield "harvestCount", JNumber value.HarvestCount
                yield "daysWithoutWater", JNumber value.DaysWithoutWater
                yield! value.Extra
            ]
        )

    and decodeCropDefinition (path: Path) (json: Json) : CropDefinition =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vVisual = None
        let mutable vSeedCost = 0.0
        let mutable vBaseHarvestValue = 0.0
        let mutable vGrowthTime = 0.0
        let mutable vGrowthDays = None
        let mutable vStages = 0.0
        let mutable vSeasons = []
        let mutable vRegrowthTime = None
        let mutable vRegrowthDays = None
        let mutable vCanRegrow = false
        let mutable vMultiTile = None
        let mutable vMutationChance = None
        let mutable vYieldMin = 0.0
        let mutable vYieldMax = 0.0
        let mutable vHarvestItemId = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "visual" -> vVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "seedCost" -> vSeedCost <- Decode.number (key :: path) value
            | "baseHarvestValue" -> vBaseHarvestValue <- Decode.number (key :: path) value
            | "growthTime" -> vGrowthTime <- Decode.number (key :: path) value
            | "growthDays" -> vGrowthDays <- Decode.optional Decode.number (key :: path) value
            | "stages" -> vStages <- Decode.number (key :: path) value
            | "seasons" -> vSeasons <- (Decode.list Decode.string) (key :: path) value
            | "regrowthTime" -> vRegrowthTime <- Decode.optional Decode.number (key :: path) value
            | "regrowthDays" -> vRegrowthDays <- Decode.optional Decode.number (key :: path) value
            | "canRegrow" -> vCanRegrow <- Decode.boolean (key :: path) value
            | "multiTile" -> vMultiTile <- Decode.optional decodeCropMultiTile (key :: path) value
            | "mutationChance" -> vMutationChance <- Decode.optional Decode.number (key :: path) value
            | "yieldMin" -> vYieldMin <- Decode.number (key :: path) value
            | "yieldMax" -> vYieldMax <- Decode.number (key :: path) value
            | "harvestItemId" -> vHarvestItemId <- Decode.optional Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Visual = vVisual; SeedCost = vSeedCost; BaseHarvestValue = vBaseHarvestValue; GrowthTime = vGrowthTime; GrowthDays = vGrowthDays; Stages = vStages; Seasons = vSeasons; RegrowthTime = vRegrowthTime; RegrowthDays = vRegrowthDays; CanRegrow = vCanRegrow; MultiTile = vMultiTile; MutationChance = vMutationChance; YieldMin = vYieldMin; YieldMax = vYieldMax; HarvestItemId = vHarvestItemId; Extra = List.ofSeq extra }

    and encodeCropDefinition (value: CropDefinition) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                match value.Visual with
                | Some v -> yield "visual", encodeVisualRef v
                | None -> ()
                yield "seedCost", JNumber value.SeedCost
                yield "baseHarvestValue", JNumber value.BaseHarvestValue
                yield "growthTime", JNumber value.GrowthTime
                match value.GrowthDays with
                | Some v -> yield "growthDays", JNumber v
                | None -> ()
                yield "stages", JNumber value.Stages
                yield "seasons", (Encode.list JString) value.Seasons
                match value.RegrowthTime with
                | Some v -> yield "regrowthTime", JNumber v
                | None -> ()
                match value.RegrowthDays with
                | Some v -> yield "regrowthDays", JNumber v
                | None -> ()
                yield "canRegrow", JBool value.CanRegrow
                match value.MultiTile with
                | Some v -> yield "multiTile", encodeCropMultiTile v
                | None -> ()
                match value.MutationChance with
                | Some v -> yield "mutationChance", JNumber v
                | None -> ()
                yield "yieldMin", JNumber value.YieldMin
                yield "yieldMax", JNumber value.YieldMax
                match value.HarvestItemId with
                | Some v -> yield "harvestItemId", JString v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeCropMultiTile (path: Path) (json: Json) : CropMultiTile =
        let members = Decode.object path json
        let mutable vWidth = 0.0
        let mutable vHeight = 0.0
        for (key, value) in members do
            match key with
            | "width" -> vWidth <- Decode.number (key :: path) value
            | "height" -> vHeight <- Decode.number (key :: path) value
            | _ -> ()
        { Width = vWidth; Height = vHeight }

    and encodeCropMultiTile (value: CropMultiTile) : Json =
        JObject(
            [
                yield "width", JNumber value.Width
                yield "height", JNumber value.Height
            ]
        )

    and decodeCustomAsset (path: Path) (json: Json) : CustomAsset =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vType = ""
        let mutable vWidth = None
        let mutable vHeight = None
        let mutable vAnimations = None
        let mutable vDataUrl = ""
        let mutable vTileType = None
        let mutable vSheet = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "type" -> vType <- Decode.string (key :: path) value
            | "width" -> vWidth <- Decode.optional Decode.number (key :: path) value
            | "height" -> vHeight <- Decode.optional Decode.number (key :: path) value
            | "animations" -> vAnimations <- Decode.optional (Decode.list decodeAnimationClip) (key :: path) value
            | "dataUrl" -> vDataUrl <- Decode.string (key :: path) value
            | "tileType" -> vTileType <- Decode.optional Decode.string (key :: path) value
            | "sheet" -> vSheet <- Decode.optional decodeSpriteSheet (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Type = vType; Width = vWidth; Height = vHeight; Animations = vAnimations; DataUrl = vDataUrl; TileType = vTileType; Sheet = vSheet; Extra = List.ofSeq extra }

    and encodeCustomAsset (value: CustomAsset) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "type", JString value.Type
                match value.Width with
                | Some v -> yield "width", JNumber v
                | None -> ()
                match value.Height with
                | Some v -> yield "height", JNumber v
                | None -> ()
                match value.Animations with
                | Some v -> yield "animations", (Encode.list encodeAnimationClip) v
                | None -> ()
                yield "dataUrl", JString value.DataUrl
                match value.TileType with
                | Some v -> yield "tileType", JString v
                | None -> ()
                match value.Sheet with
                | Some v -> yield "sheet", encodeSpriteSheet v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeCustomCropDefinition (path: Path) (json: Json) : CustomCropDefinition =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vVisual = None
        let mutable vSeedCost = 0.0
        let mutable vBaseHarvestValue = 0.0
        let mutable vGrowthTime = 0.0
        let mutable vGrowthDays = None
        let mutable vStages = 0.0
        let mutable vSeasons = []
        let mutable vRegrowthTime = None
        let mutable vRegrowthDays = None
        let mutable vCanRegrow = false
        let mutable vMultiTile = None
        let mutable vMutationChance = None
        let mutable vYieldMin = 0.0
        let mutable vYieldMax = 0.0
        let mutable vHarvestItemId = None
        let mutable vCustomAsset = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "visual" -> vVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "seedCost" -> vSeedCost <- Decode.number (key :: path) value
            | "baseHarvestValue" -> vBaseHarvestValue <- Decode.number (key :: path) value
            | "growthTime" -> vGrowthTime <- Decode.number (key :: path) value
            | "growthDays" -> vGrowthDays <- Decode.optional Decode.number (key :: path) value
            | "stages" -> vStages <- Decode.number (key :: path) value
            | "seasons" -> vSeasons <- (Decode.list Decode.string) (key :: path) value
            | "regrowthTime" -> vRegrowthTime <- Decode.optional Decode.number (key :: path) value
            | "regrowthDays" -> vRegrowthDays <- Decode.optional Decode.number (key :: path) value
            | "canRegrow" -> vCanRegrow <- Decode.boolean (key :: path) value
            | "multiTile" -> vMultiTile <- Decode.optional decodeCropMultiTile (key :: path) value
            | "mutationChance" -> vMutationChance <- Decode.optional Decode.number (key :: path) value
            | "yieldMin" -> vYieldMin <- Decode.number (key :: path) value
            | "yieldMax" -> vYieldMax <- Decode.number (key :: path) value
            | "harvestItemId" -> vHarvestItemId <- Decode.optional Decode.string (key :: path) value
            | "customAsset" -> vCustomAsset <- Decode.optional Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Visual = vVisual; SeedCost = vSeedCost; BaseHarvestValue = vBaseHarvestValue; GrowthTime = vGrowthTime; GrowthDays = vGrowthDays; Stages = vStages; Seasons = vSeasons; RegrowthTime = vRegrowthTime; RegrowthDays = vRegrowthDays; CanRegrow = vCanRegrow; MultiTile = vMultiTile; MutationChance = vMutationChance; YieldMin = vYieldMin; YieldMax = vYieldMax; HarvestItemId = vHarvestItemId; CustomAsset = vCustomAsset; Extra = List.ofSeq extra }

    and encodeCustomCropDefinition (value: CustomCropDefinition) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                match value.Visual with
                | Some v -> yield "visual", encodeVisualRef v
                | None -> ()
                yield "seedCost", JNumber value.SeedCost
                yield "baseHarvestValue", JNumber value.BaseHarvestValue
                yield "growthTime", JNumber value.GrowthTime
                match value.GrowthDays with
                | Some v -> yield "growthDays", JNumber v
                | None -> ()
                yield "stages", JNumber value.Stages
                yield "seasons", (Encode.list JString) value.Seasons
                match value.RegrowthTime with
                | Some v -> yield "regrowthTime", JNumber v
                | None -> ()
                match value.RegrowthDays with
                | Some v -> yield "regrowthDays", JNumber v
                | None -> ()
                yield "canRegrow", JBool value.CanRegrow
                match value.MultiTile with
                | Some v -> yield "multiTile", encodeCropMultiTile v
                | None -> ()
                match value.MutationChance with
                | Some v -> yield "mutationChance", JNumber v
                | None -> ()
                yield "yieldMin", JNumber value.YieldMin
                yield "yieldMax", JNumber value.YieldMax
                match value.HarvestItemId with
                | Some v -> yield "harvestItemId", JString v
                | None -> ()
                match value.CustomAsset with
                | Some v -> yield "customAsset", JString v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeDialogue (path: Path) (json: Json) : Dialogue =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vNpcId = ""
        let mutable vText = ""
        let mutable vOptions = []
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "npcId" -> vNpcId <- Decode.string (key :: path) value
            | "text" -> vText <- Decode.string (key :: path) value
            | "options" -> vOptions <- (Decode.list decodeDialogueOption) (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; NpcId = vNpcId; Text = vText; Options = vOptions; Extra = List.ofSeq extra }

    and encodeDialogue (value: Dialogue) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "npcId", JString value.NpcId
                yield "text", JString value.Text
                yield "options", (Encode.list encodeDialogueOption) value.Options
                yield! value.Extra
            ]
        )

    and decodeDialogueOption (path: Path) (json: Json) : DialogueOption =
        let members = Decode.object path json
        let mutable vText = ""
        let mutable vNextDialogueId = None
        let mutable vGiveItem = None
        let mutable vGiveItemQuantity = None
        let mutable vTakeMoney = None
        let mutable vGiveMoney = None
        let mutable vEventFlag = None
        let mutable vOnce = None
        let mutable vHiddenIfFlag = None
        let mutable vRequiresItem = None
        let mutable vRequiresFlag = None
        let mutable vOpenShopId = None
        let mutable vOfferQuestId = None
        let mutable vRequiresFriendship = None
        let mutable vActionId = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "text" -> vText <- Decode.string (key :: path) value
            | "nextDialogueId" -> vNextDialogueId <- Decode.optional Decode.string (key :: path) value
            | "giveItem" -> vGiveItem <- Decode.optional Decode.string (key :: path) value
            | "giveItemQuantity" -> vGiveItemQuantity <- Decode.optional Decode.number (key :: path) value
            | "takeMoney" -> vTakeMoney <- Decode.optional Decode.number (key :: path) value
            | "giveMoney" -> vGiveMoney <- Decode.optional Decode.number (key :: path) value
            | "eventFlag" -> vEventFlag <- Decode.optional Decode.string (key :: path) value
            | "once" -> vOnce <- Decode.optional Decode.boolean (key :: path) value
            | "hiddenIfFlag" -> vHiddenIfFlag <- Decode.optional Decode.string (key :: path) value
            | "requiresItem" -> vRequiresItem <- Decode.optional Decode.string (key :: path) value
            | "requiresFlag" -> vRequiresFlag <- Decode.optional Decode.string (key :: path) value
            | "openShopId" -> vOpenShopId <- Decode.optional Decode.string (key :: path) value
            | "offerQuestId" -> vOfferQuestId <- Decode.optional Decode.string (key :: path) value
            | "requiresFriendship" -> vRequiresFriendship <- Decode.optional Decode.number (key :: path) value
            | "actionId" -> vActionId <- Decode.optional Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Text = vText; NextDialogueId = vNextDialogueId; GiveItem = vGiveItem; GiveItemQuantity = vGiveItemQuantity; TakeMoney = vTakeMoney; GiveMoney = vGiveMoney; EventFlag = vEventFlag; Once = vOnce; HiddenIfFlag = vHiddenIfFlag; RequiresItem = vRequiresItem; RequiresFlag = vRequiresFlag; OpenShopId = vOpenShopId; OfferQuestId = vOfferQuestId; RequiresFriendship = vRequiresFriendship; ActionId = vActionId; Extra = List.ofSeq extra }

    and encodeDialogueOption (value: DialogueOption) : Json =
        JObject(
            [
                yield "text", JString value.Text
                match value.NextDialogueId with
                | Some v -> yield "nextDialogueId", JString v
                | None -> ()
                match value.GiveItem with
                | Some v -> yield "giveItem", JString v
                | None -> ()
                match value.GiveItemQuantity with
                | Some v -> yield "giveItemQuantity", JNumber v
                | None -> ()
                match value.TakeMoney with
                | Some v -> yield "takeMoney", JNumber v
                | None -> ()
                match value.GiveMoney with
                | Some v -> yield "giveMoney", JNumber v
                | None -> ()
                match value.EventFlag with
                | Some v -> yield "eventFlag", JString v
                | None -> ()
                match value.Once with
                | Some v -> yield "once", JBool v
                | None -> ()
                match value.HiddenIfFlag with
                | Some v -> yield "hiddenIfFlag", JString v
                | None -> ()
                match value.RequiresItem with
                | Some v -> yield "requiresItem", JString v
                | None -> ()
                match value.RequiresFlag with
                | Some v -> yield "requiresFlag", JString v
                | None -> ()
                match value.OpenShopId with
                | Some v -> yield "openShopId", JString v
                | None -> ()
                match value.OfferQuestId with
                | Some v -> yield "offerQuestId", JString v
                | None -> ()
                match value.RequiresFriendship with
                | Some v -> yield "requiresFriendship", JNumber v
                | None -> ()
                match value.ActionId with
                | Some v -> yield "actionId", JString v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeEventOutcome (path: Path) (json: Json) : EventOutcome =
        let members = Decode.object path json
        let mutable vType = ""
        let mutable vMessage = None
        let mutable vItemId = None
        let mutable vItemQuantity = None
        let mutable vAmount = None
        let mutable vRadius = None
        let mutable vFlagName = None
        let mutable vQuestId = None
        let mutable vNpcId = None
        let mutable vDialogueId = None
        let mutable vTileX = None
        let mutable vTileY = None
        let mutable vNewTileType = None
        let mutable vSceneId = None
        let mutable vX = None
        let mutable vY = None
        let mutable vSoundId = None
        let mutable vActionId = None
        let mutable vMinigameId = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "type" -> vType <- Decode.string (key :: path) value
            | "message" -> vMessage <- Decode.optional Decode.string (key :: path) value
            | "itemId" -> vItemId <- Decode.optional Decode.string (key :: path) value
            | "itemQuantity" -> vItemQuantity <- Decode.optional Decode.number (key :: path) value
            | "amount" -> vAmount <- Decode.optional Decode.number (key :: path) value
            | "radius" -> vRadius <- Decode.optional Decode.number (key :: path) value
            | "flagName" -> vFlagName <- Decode.optional Decode.string (key :: path) value
            | "questId" -> vQuestId <- Decode.optional Decode.string (key :: path) value
            | "npcId" -> vNpcId <- Decode.optional Decode.string (key :: path) value
            | "dialogueId" -> vDialogueId <- Decode.optional Decode.string (key :: path) value
            | "tileX" -> vTileX <- Decode.optional Decode.number (key :: path) value
            | "tileY" -> vTileY <- Decode.optional Decode.number (key :: path) value
            | "newTileType" -> vNewTileType <- Decode.optional Decode.string (key :: path) value
            | "sceneId" -> vSceneId <- Decode.optional Decode.string (key :: path) value
            | "x" -> vX <- Decode.optional Decode.number (key :: path) value
            | "y" -> vY <- Decode.optional Decode.number (key :: path) value
            | "soundId" -> vSoundId <- Decode.optional Decode.string (key :: path) value
            | "actionId" -> vActionId <- Decode.optional Decode.string (key :: path) value
            | "minigameId" -> vMinigameId <- Decode.optional Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Type = vType; Message = vMessage; ItemId = vItemId; ItemQuantity = vItemQuantity; Amount = vAmount; Radius = vRadius; FlagName = vFlagName; QuestId = vQuestId; NpcId = vNpcId; DialogueId = vDialogueId; TileX = vTileX; TileY = vTileY; NewTileType = vNewTileType; SceneId = vSceneId; X = vX; Y = vY; SoundId = vSoundId; ActionId = vActionId; MinigameId = vMinigameId; Extra = List.ofSeq extra }

    and encodeEventOutcome (value: EventOutcome) : Json =
        JObject(
            [
                yield "type", JString value.Type
                match value.Message with
                | Some v -> yield "message", JString v
                | None -> ()
                match value.ItemId with
                | Some v -> yield "itemId", JString v
                | None -> ()
                match value.ItemQuantity with
                | Some v -> yield "itemQuantity", JNumber v
                | None -> ()
                match value.Amount with
                | Some v -> yield "amount", JNumber v
                | None -> ()
                match value.Radius with
                | Some v -> yield "radius", JNumber v
                | None -> ()
                match value.FlagName with
                | Some v -> yield "flagName", JString v
                | None -> ()
                match value.QuestId with
                | Some v -> yield "questId", JString v
                | None -> ()
                match value.NpcId with
                | Some v -> yield "npcId", JString v
                | None -> ()
                match value.DialogueId with
                | Some v -> yield "dialogueId", JString v
                | None -> ()
                match value.TileX with
                | Some v -> yield "tileX", JNumber v
                | None -> ()
                match value.TileY with
                | Some v -> yield "tileY", JNumber v
                | None -> ()
                match value.NewTileType with
                | Some v -> yield "newTileType", JString v
                | None -> ()
                match value.SceneId with
                | Some v -> yield "sceneId", JString v
                | None -> ()
                match value.X with
                | Some v -> yield "x", JNumber v
                | None -> ()
                match value.Y with
                | Some v -> yield "y", JNumber v
                | None -> ()
                match value.SoundId with
                | Some v -> yield "soundId", JString v
                | None -> ()
                match value.ActionId with
                | Some v -> yield "actionId", JString v
                | None -> ()
                match value.MinigameId with
                | Some v -> yield "minigameId", JString v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeExportSettings (path: Path) (json: Json) : ExportSettings =
        let members = Decode.object path json
        let mutable vTitle = None
        let mutable vExecutableName = None
        let mutable vVersion = None
        let mutable vGameId = ""
        let mutable vAuthor = None
        let mutable vCompany = None
        let mutable vIconAssetId = None
        let mutable vWindow = ExportWindow.Default
        let mutable vPixelScale = "integer"
        let mutable vCredits = None
        let mutable vTargets = [ "windows-x64"; "linux-x64" ]
        for (key, value) in members do
            match key with
            | "title" -> vTitle <- Decode.optional Decode.string (key :: path) value
            | "executableName" -> vExecutableName <- Decode.optional Decode.string (key :: path) value
            | "version" -> vVersion <- Decode.optional Decode.string (key :: path) value
            | "gameId" -> vGameId <- Decode.string (key :: path) value
            | "author" -> vAuthor <- Decode.optional Decode.string (key :: path) value
            | "company" -> vCompany <- Decode.optional Decode.string (key :: path) value
            | "icon" -> vIconAssetId <- Decode.optional Decode.string (key :: path) value
            | "window" -> vWindow <- decodeExportWindow (key :: path) value
            | "pixelScale" -> vPixelScale <- Decode.string (key :: path) value
            | "credits" -> vCredits <- Decode.optional Decode.string (key :: path) value
            | "targets" -> vTargets <- (Decode.list Decode.string) (key :: path) value
            | _ -> ()
        { Title = vTitle; ExecutableName = vExecutableName; Version = vVersion; GameId = vGameId; Author = vAuthor; Company = vCompany; IconAssetId = vIconAssetId; Window = vWindow; PixelScale = vPixelScale; Credits = vCredits; Targets = vTargets }

    and encodeExportSettings (value: ExportSettings) : Json =
        JObject(
            [
                match value.Title with
                | Some v -> yield "title", JString v
                | None -> ()
                match value.ExecutableName with
                | Some v -> yield "executableName", JString v
                | None -> ()
                match value.Version with
                | Some v -> yield "version", JString v
                | None -> ()
                yield "gameId", JString value.GameId
                match value.Author with
                | Some v -> yield "author", JString v
                | None -> ()
                match value.Company with
                | Some v -> yield "company", JString v
                | None -> ()
                match value.IconAssetId with
                | Some v -> yield "icon", JString v
                | None -> ()
                yield "window", encodeExportWindow value.Window
                yield "pixelScale", JString value.PixelScale
                match value.Credits with
                | Some v -> yield "credits", JString v
                | None -> ()
                yield "targets", (Encode.list JString) value.Targets
            ]
        )

    and decodeExportWindow (path: Path) (json: Json) : ExportWindow =
        let members = Decode.object path json
        let mutable vWidth = 1280
        let mutable vHeight = 800
        let mutable vFullscreen = false
        for (key, value) in members do
            match key with
            | "width" -> vWidth <- Decode.int32 (key :: path) value
            | "height" -> vHeight <- Decode.int32 (key :: path) value
            | "fullscreen" -> vFullscreen <- Decode.boolean (key :: path) value
            | _ -> ()
        { Width = vWidth; Height = vHeight; Fullscreen = vFullscreen }

    and encodeExportWindow (value: ExportWindow) : Json =
        JObject(
            [
                yield "width", (float >> JNumber) value.Width
                yield "height", (float >> JNumber) value.Height
                yield "fullscreen", JBool value.Fullscreen
            ]
        )

    and decodeExportedGame (path: Path) (json: Json) : ExportedGame =
        let members = Decode.object path json
        let mutable vSchemaVersion = None
        let mutable vVersion = ""
        let mutable vName = ""
        let mutable vScenes = []
        let mutable vNpcs = []
        let mutable vItems = []
        let mutable vEvents = []
        let mutable vDialogues = []
        let mutable vQuests = []
        let mutable vStartSceneId = ""
        let mutable vCustomAssets = []
        let mutable vCustomCrops = None
        let mutable vPlayerCustomImage = None
        let mutable vPlayerVisual = None
        let mutable vGraphics = None
        let mutable vGamePanels = None
        let mutable vCurrentSeason = ""
        let mutable vCurrentDay = 0.0
        let mutable vCurrentTimeMinutes = 0.0
        let mutable vCurrentYear = 0.0
        let mutable vGameStartTime = 0.0
        let mutable vShops = []
        let mutable vNodeTypes = []
        let mutable vSettings = ProjectSettings.Default
        let mutable vRecipes = []
        let mutable vMachineTypes = []
        let mutable vWeather = WeatherConfig.Default
        let mutable vAnimalSpecies = []
        let mutable vAnimals = []
        let mutable vFishTables = []
        let mutable vMine = MineConfig.Default
        let mutable vActions = []
        let mutable vMinigames = []
        let mutable vContentPacks = []
        let mutable vPlayer = None
        let mutable vCurrentWeatherId = None
        let mutable vSocialState = None
        let mutable vMineDeepestFloor = None
        let mutable vQuarantinedItems = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "schemaVersion" -> vSchemaVersion <- Decode.optional Decode.number (key :: path) value
            | "version" -> vVersion <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "scenes" -> vScenes <- (Decode.list decodeScene) (key :: path) value
            | "npcs" -> vNpcs <- (Decode.list decodeNpc) (key :: path) value
            | "items" -> vItems <- (Decode.list decodeItem) (key :: path) value
            | "events" -> vEvents <- (Decode.list decodeGameEvent) (key :: path) value
            | "dialogues" -> vDialogues <- (Decode.list decodeDialogue) (key :: path) value
            | "quests" -> vQuests <- (Decode.list decodeQuest) (key :: path) value
            | "startSceneId" -> vStartSceneId <- Decode.string (key :: path) value
            | "customAssets" -> vCustomAssets <- (Decode.list decodeCustomAsset) (key :: path) value
            | "customCrops" -> vCustomCrops <- Decode.optional (Decode.list decodeCustomCropDefinition) (key :: path) value
            | "playerCustomImage" -> vPlayerCustomImage <- Decode.optional Decode.string (key :: path) value
            | "playerVisual" -> vPlayerVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "graphics" -> vGraphics <- Decode.optional decodeGraphicsSettings (key :: path) value
            | "gamePanels" -> vGamePanels <- Decode.optional (Decode.list decodeGamePanel) (key :: path) value
            | "currentSeason" -> vCurrentSeason <- Decode.string (key :: path) value
            | "currentDay" -> vCurrentDay <- Decode.number (key :: path) value
            | "currentTimeMinutes" -> vCurrentTimeMinutes <- Decode.number (key :: path) value
            | "currentYear" -> vCurrentYear <- Decode.number (key :: path) value
            | "gameStartTime" -> vGameStartTime <- Decode.number (key :: path) value
            | "shops" -> vShops <- (Decode.list decodeShopDefinition) (key :: path) value
            | "nodeTypes" -> vNodeTypes <- (Decode.list decodeNodeTypeDefinition) (key :: path) value
            | "settings" -> vSettings <- decodeProjectSettings (key :: path) value
            | "recipes" -> vRecipes <- (Decode.list decodeRecipeDefinition) (key :: path) value
            | "machineTypes" -> vMachineTypes <- (Decode.list decodeMachineTypeDefinition) (key :: path) value
            | "weather" -> vWeather <- decodeWeatherConfig (key :: path) value
            | "animalSpecies" -> vAnimalSpecies <- (Decode.list decodeAnimalSpeciesDefinition) (key :: path) value
            | "animals" -> vAnimals <- (Decode.list decodeAnimalState) (key :: path) value
            | "fishTables" -> vFishTables <- (Decode.list decodeFishTable) (key :: path) value
            | "mine" -> vMine <- decodeMineConfig (key :: path) value
            | "actions" -> vActions <- (Decode.list decodeActionDef) (key :: path) value
            | "minigames" -> vMinigames <- (Decode.list decodeMinigameDef) (key :: path) value
            | "contentPacks" -> vContentPacks <- (Decode.list decodePackInstallation) (key :: path) value
            | "player" -> vPlayer <- Decode.optional decodePlayer (key :: path) value
            | "currentWeatherId" -> vCurrentWeatherId <- Decode.optional Decode.string (key :: path) value
            | "socialState" -> vSocialState <- Decode.optional (Decode.dict decodeNpcSocialState) (key :: path) value
            | "mineDeepestFloor" -> vMineDeepestFloor <- Decode.optional Decode.number (key :: path) value
            | "quarantinedItems" -> vQuarantinedItems <- Decode.optional (Decode.list decodeInventorySlot) (key :: path) value
            | _ -> extra.Add((key, value))
        { SchemaVersion = vSchemaVersion; Version = vVersion; Name = vName; Scenes = vScenes; Npcs = vNpcs; Items = vItems; Events = vEvents; Dialogues = vDialogues; Quests = vQuests; StartSceneId = vStartSceneId; CustomAssets = vCustomAssets; CustomCrops = vCustomCrops; PlayerCustomImage = vPlayerCustomImage; PlayerVisual = vPlayerVisual; Graphics = vGraphics; GamePanels = vGamePanels; CurrentSeason = vCurrentSeason; CurrentDay = vCurrentDay; CurrentTimeMinutes = vCurrentTimeMinutes; CurrentYear = vCurrentYear; GameStartTime = vGameStartTime; Shops = vShops; NodeTypes = vNodeTypes; Settings = vSettings; Recipes = vRecipes; MachineTypes = vMachineTypes; Weather = vWeather; AnimalSpecies = vAnimalSpecies; Animals = vAnimals; FishTables = vFishTables; Mine = vMine; Actions = vActions; Minigames = vMinigames; ContentPacks = vContentPacks; Player = vPlayer; CurrentWeatherId = vCurrentWeatherId; SocialState = vSocialState; MineDeepestFloor = vMineDeepestFloor; QuarantinedItems = vQuarantinedItems; Extra = List.ofSeq extra }

    and encodeExportedGame (value: ExportedGame) : Json =
        JObject(
            [
                match value.SchemaVersion with
                | Some v -> yield "schemaVersion", JNumber v
                | None -> ()
                yield "version", JString value.Version
                yield "name", JString value.Name
                yield "scenes", (Encode.list encodeScene) value.Scenes
                yield "npcs", (Encode.list encodeNpc) value.Npcs
                yield "items", (Encode.list encodeItem) value.Items
                yield "events", (Encode.list encodeGameEvent) value.Events
                yield "dialogues", (Encode.list encodeDialogue) value.Dialogues
                yield "quests", (Encode.list encodeQuest) value.Quests
                yield "startSceneId", JString value.StartSceneId
                yield "customAssets", (Encode.list encodeCustomAsset) value.CustomAssets
                match value.CustomCrops with
                | Some v -> yield "customCrops", (Encode.list encodeCustomCropDefinition) v
                | None -> ()
                match value.PlayerCustomImage with
                | Some v -> yield "playerCustomImage", JString v
                | None -> ()
                match value.PlayerVisual with
                | Some v -> yield "playerVisual", encodeVisualRef v
                | None -> ()
                match value.Graphics with
                | Some v -> yield "graphics", encodeGraphicsSettings v
                | None -> ()
                match value.GamePanels with
                | Some v -> yield "gamePanels", (Encode.list encodeGamePanel) v
                | None -> ()
                yield "currentSeason", JString value.CurrentSeason
                yield "currentDay", JNumber value.CurrentDay
                yield "currentTimeMinutes", JNumber value.CurrentTimeMinutes
                yield "currentYear", JNumber value.CurrentYear
                yield "gameStartTime", JNumber value.GameStartTime
                yield "shops", (Encode.list encodeShopDefinition) value.Shops
                yield "nodeTypes", (Encode.list encodeNodeTypeDefinition) value.NodeTypes
                yield "settings", encodeProjectSettings value.Settings
                yield "recipes", (Encode.list encodeRecipeDefinition) value.Recipes
                yield "machineTypes", (Encode.list encodeMachineTypeDefinition) value.MachineTypes
                yield "weather", encodeWeatherConfig value.Weather
                yield "animalSpecies", (Encode.list encodeAnimalSpeciesDefinition) value.AnimalSpecies
                yield "animals", (Encode.list encodeAnimalState) value.Animals
                yield "fishTables", (Encode.list encodeFishTable) value.FishTables
                yield "mine", encodeMineConfig value.Mine
                yield "actions", (Encode.list encodeActionDef) value.Actions
                yield "minigames", (Encode.list encodeMinigameDef) value.Minigames
                yield "contentPacks", (Encode.list encodePackInstallation) value.ContentPacks
                match value.Player with
                | Some v -> yield "player", encodePlayer v
                | None -> ()
                match value.CurrentWeatherId with
                | Some v -> yield "currentWeatherId", JString v
                | None -> ()
                match value.SocialState with
                | Some v -> yield "socialState", (Encode.dict encodeNpcSocialState) v
                | None -> ()
                match value.MineDeepestFloor with
                | Some v -> yield "mineDeepestFloor", JNumber v
                | None -> ()
                match value.QuarantinedItems with
                | Some v -> yield "quarantinedItems", (Encode.list encodeInventorySlot) v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeFishTable (path: Path) (json: Json) : FishTable =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vSeasons = None
        let mutable vSceneIds = None
        let mutable vEntries = []
        let mutable vJunkChance = 0.15
        let mutable vJunkItemId = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "seasons" -> vSeasons <- Decode.optional (Decode.list Decode.string) (key :: path) value
            | "sceneIds" -> vSceneIds <- Decode.optional (Decode.list Decode.string) (key :: path) value
            | "entries" -> vEntries <- (Decode.list decodeFishTableEntry) (key :: path) value
            | "junkChance" -> vJunkChance <- Decode.number (key :: path) value
            | "junkItemId" -> vJunkItemId <- Decode.optional Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Seasons = vSeasons; SceneIds = vSceneIds; Entries = vEntries; JunkChance = vJunkChance; JunkItemId = vJunkItemId; Extra = List.ofSeq extra }

    and encodeFishTable (value: FishTable) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                match value.Seasons with
                | Some v -> yield "seasons", (Encode.list JString) v
                | None -> ()
                match value.SceneIds with
                | Some v -> yield "sceneIds", (Encode.list JString) v
                | None -> ()
                yield "entries", (Encode.list encodeFishTableEntry) value.Entries
                yield "junkChance", JNumber value.JunkChance
                match value.JunkItemId with
                | Some v -> yield "junkItemId", JString v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeFishTableEntry (path: Path) (json: Json) : FishTableEntry =
        let members = Decode.object path json
        let mutable vItemId = ""
        let mutable vWeight = 0.0
        let mutable vDifficulty = 0.3
        for (key, value) in members do
            match key with
            | "itemId" -> vItemId <- Decode.string (key :: path) value
            | "weight" -> vWeight <- Decode.number (key :: path) value
            | "difficulty" -> vDifficulty <- Decode.number (key :: path) value
            | _ -> ()
        { ItemId = vItemId; Weight = vWeight; Difficulty = vDifficulty }

    and encodeFishTableEntry (value: FishTableEntry) : Json =
        JObject(
            [
                yield "itemId", JString value.ItemId
                yield "weight", JNumber value.Weight
                yield "difficulty", JNumber value.Difficulty
            ]
        )

    and decodeGameContent (path: Path) (json: Json) : GameContent =
        let members = Decode.object path json
        let mutable vContentVersion = 0.0
        let mutable vCrops = []
        let mutable vItems = []
        let mutable vNpcs = []
        let mutable vDialogues = []
        let mutable vQuests = []
        let mutable vEvents = []
        let mutable vShops = []
        let mutable vNodeTypes = []
        let mutable vSettings = ProjectSettings.Default
        let mutable vRecipes = []
        let mutable vMachineTypes = []
        let mutable vWeather = WeatherConfig.Default
        let mutable vAnimalSpecies = []
        let mutable vFishTables = []
        let mutable vMine = MineConfig.Default
        let mutable vActions = []
        let mutable vMinigames = []
        let mutable vScenes = []
        let mutable vStartSceneId = ""
        for (key, value) in members do
            match key with
            | "contentVersion" -> vContentVersion <- Decode.number (key :: path) value
            | "crops" -> vCrops <- (Decode.dict decodeCropDefinition) (key :: path) value
            | "items" -> vItems <- (Decode.list decodeItem) (key :: path) value
            | "npcs" -> vNpcs <- (Decode.list decodeNpc) (key :: path) value
            | "dialogues" -> vDialogues <- (Decode.list decodeDialogue) (key :: path) value
            | "quests" -> vQuests <- (Decode.list decodeQuest) (key :: path) value
            | "events" -> vEvents <- (Decode.list decodeGameEvent) (key :: path) value
            | "shops" -> vShops <- (Decode.list decodeShopDefinition) (key :: path) value
            | "nodeTypes" -> vNodeTypes <- (Decode.list decodeNodeTypeDefinition) (key :: path) value
            | "settings" -> vSettings <- decodeProjectSettings (key :: path) value
            | "recipes" -> vRecipes <- (Decode.list decodeRecipeDefinition) (key :: path) value
            | "machineTypes" -> vMachineTypes <- (Decode.list decodeMachineTypeDefinition) (key :: path) value
            | "weather" -> vWeather <- decodeWeatherConfig (key :: path) value
            | "animalSpecies" -> vAnimalSpecies <- (Decode.list decodeAnimalSpeciesDefinition) (key :: path) value
            | "fishTables" -> vFishTables <- (Decode.list decodeFishTable) (key :: path) value
            | "mine" -> vMine <- decodeMineConfig (key :: path) value
            | "actions" -> vActions <- (Decode.list decodeActionDef) (key :: path) value
            | "minigames" -> vMinigames <- (Decode.list decodeMinigameDef) (key :: path) value
            | "scenes" -> vScenes <- (Decode.list decodeScene) (key :: path) value
            | "startSceneId" -> vStartSceneId <- Decode.string (key :: path) value
            | _ -> ()
        { ContentVersion = vContentVersion; Crops = vCrops; Items = vItems; Npcs = vNpcs; Dialogues = vDialogues; Quests = vQuests; Events = vEvents; Shops = vShops; NodeTypes = vNodeTypes; Settings = vSettings; Recipes = vRecipes; MachineTypes = vMachineTypes; Weather = vWeather; AnimalSpecies = vAnimalSpecies; FishTables = vFishTables; Mine = vMine; Actions = vActions; Minigames = vMinigames; Scenes = vScenes; StartSceneId = vStartSceneId }

    and encodeGameContent (value: GameContent) : Json =
        JObject(
            [
                yield "contentVersion", JNumber value.ContentVersion
                yield "crops", (Encode.dict encodeCropDefinition) value.Crops
                yield "items", (Encode.list encodeItem) value.Items
                yield "npcs", (Encode.list encodeNpc) value.Npcs
                yield "dialogues", (Encode.list encodeDialogue) value.Dialogues
                yield "quests", (Encode.list encodeQuest) value.Quests
                yield "events", (Encode.list encodeGameEvent) value.Events
                yield "shops", (Encode.list encodeShopDefinition) value.Shops
                yield "nodeTypes", (Encode.list encodeNodeTypeDefinition) value.NodeTypes
                yield "settings", encodeProjectSettings value.Settings
                yield "recipes", (Encode.list encodeRecipeDefinition) value.Recipes
                yield "machineTypes", (Encode.list encodeMachineTypeDefinition) value.MachineTypes
                yield "weather", encodeWeatherConfig value.Weather
                yield "animalSpecies", (Encode.list encodeAnimalSpeciesDefinition) value.AnimalSpecies
                yield "fishTables", (Encode.list encodeFishTable) value.FishTables
                yield "mine", encodeMineConfig value.Mine
                yield "actions", (Encode.list encodeActionDef) value.Actions
                yield "minigames", (Encode.list encodeMinigameDef) value.Minigames
                yield "scenes", (Encode.list encodeScene) value.Scenes
                yield "startSceneId", JString value.StartSceneId
            ]
        )

    and decodeGameEvent (path: Path) (json: Json) : GameEvent =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vSceneId = ""
        let mutable vTrigger = ""
        let mutable vConditions = []
        let mutable vOutcomes = []
        let mutable vActive = false
        let mutable vRepeatable = false
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "sceneId" -> vSceneId <- Decode.string (key :: path) value
            | "trigger" -> vTrigger <- Decode.string (key :: path) value
            | "conditions" -> vConditions <- (Decode.list decodeEventCondition) (key :: path) value
            | "outcomes" -> vOutcomes <- (Decode.list decodeEventOutcome) (key :: path) value
            | "active" -> vActive <- Decode.boolean (key :: path) value
            | "repeatable" -> vRepeatable <- Decode.boolean (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; SceneId = vSceneId; Trigger = vTrigger; Conditions = vConditions; Outcomes = vOutcomes; Active = vActive; Repeatable = vRepeatable; Extra = List.ofSeq extra }

    and encodeGameEvent (value: GameEvent) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "sceneId", JString value.SceneId
                yield "trigger", JString value.Trigger
                yield "conditions", (Encode.list encodeEventCondition) value.Conditions
                yield "outcomes", (Encode.list encodeEventOutcome) value.Outcomes
                yield "active", JBool value.Active
                yield "repeatable", JBool value.Repeatable
                yield! value.Extra
            ]
        )

    and decodeGamePanel (path: Path) (json: Json) : GamePanel =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vTitle = ""
        let mutable vVisibleFlag = None
        let mutable vEntries = []
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "title" -> vTitle <- Decode.string (key :: path) value
            | "visibleFlag" -> vVisibleFlag <- Decode.optional Decode.string (key :: path) value
            | "entries" -> vEntries <- (Decode.list decodeGamePanelEntry) (key :: path) value
            | _ -> ()
        { Id = vId; Title = vTitle; VisibleFlag = vVisibleFlag; Entries = vEntries }

    and encodeGamePanel (value: GamePanel) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "title", JString value.Title
                match value.VisibleFlag with
                | Some v -> yield "visibleFlag", JString v
                | None -> ()
                yield "entries", (Encode.list encodeGamePanelEntry) value.Entries
            ]
        )

    and decodeGamePanelEntry (path: Path) (json: Json) : GamePanelEntry =
        let members = Decode.object path json
        let mutable vLabel = ""
        let mutable vKind = ""
        let mutable vValue = ""
        for (key, value) in members do
            match key with
            | "label" -> vLabel <- Decode.string (key :: path) value
            | "kind" -> vKind <- Decode.string (key :: path) value
            | "value" -> vValue <- Decode.string (key :: path) value
            | _ -> ()
        { Label = vLabel; Kind = vKind; Value = vValue }

    and encodeGamePanelEntry (value: GamePanelEntry) : Json =
        JObject(
            [
                yield "label", JString value.Label
                yield "kind", JString value.Kind
                yield "value", JString value.Value
            ]
        )

    and decodeGameProject (path: Path) (json: Json) : GameProject =
        let members = Decode.object path json
        let mutable vSchemaVersion = 0.0
        let mutable vId = ""
        let mutable vName = ""
        let mutable vVersion = ""
        let mutable vScenes = []
        let mutable vNpcs = []
        let mutable vItems = []
        let mutable vEvents = []
        let mutable vDialogues = []
        let mutable vQuests = []
        let mutable vPlayer = Player.Default
        let mutable vEventFlags = []
        let mutable vStartSceneId = ""
        let mutable vMode = ""
        let mutable vSelectedTileType = ""
        let mutable vSelectedTileVisual = None
        let mutable vSelectedNpcId = None
        let mutable vSelectedItemId = None
        let mutable vCurrentTime = 0.0
        let mutable vCustomAssets = []
        let mutable vCustomCrops = None
        let mutable vPlayerCustomImage = None
        let mutable vPlayerVisual = None
        let mutable vGraphics = None
        let mutable vGamePanels = None
        let mutable vCurrentSeason = ""
        let mutable vCurrentDay = 0.0
        let mutable vCurrentDayOfSeason = None
        let mutable vCurrentTimeMinutes = 0.0
        let mutable vCurrentYear = 0.0
        let mutable vGameStartTime = 0.0
        let mutable vShops = []
        let mutable vNodeTypes = []
        let mutable vSettings = ProjectSettings.Default
        let mutable vRecipes = []
        let mutable vMachineTypes = []
        let mutable vWeather = WeatherConfig.Default
        let mutable vAnimalSpecies = []
        let mutable vAnimals = []
        let mutable vFishTables = []
        let mutable vMine = MineConfig.Default
        let mutable vActions = []
        let mutable vMinigames = []
        let mutable vContentPacks = []
        let mutable vExport = None
        let mutable vRngState = None
        let mutable vCurrentWeatherId = None
        let mutable vSocialState = None
        let mutable vMineDeepestFloor = None
        let mutable vQuarantinedItems = None
        let mutable vKeptState = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "schemaVersion" -> vSchemaVersion <- Decode.number (key :: path) value
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "version" -> vVersion <- Decode.string (key :: path) value
            | "scenes" -> vScenes <- (Decode.list decodeScene) (key :: path) value
            | "npcs" -> vNpcs <- (Decode.list decodeNpc) (key :: path) value
            | "items" -> vItems <- (Decode.list decodeItem) (key :: path) value
            | "events" -> vEvents <- (Decode.list decodeGameEvent) (key :: path) value
            | "dialogues" -> vDialogues <- (Decode.list decodeDialogue) (key :: path) value
            | "quests" -> vQuests <- (Decode.list decodeQuest) (key :: path) value
            | "player" -> vPlayer <- decodePlayer (key :: path) value
            | "eventFlags" -> vEventFlags <- (Decode.dict Decode.flagValue) (key :: path) value
            | "startSceneId" -> vStartSceneId <- Decode.string (key :: path) value
            | "mode" -> vMode <- Decode.string (key :: path) value
            | "selectedTileType" -> vSelectedTileType <- Decode.string (key :: path) value
            | "selectedTileVisual" -> vSelectedTileVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "selectedNPCId" -> vSelectedNpcId <- Decode.optional Decode.string (key :: path) value
            | "selectedItemId" -> vSelectedItemId <- Decode.optional Decode.string (key :: path) value
            | "currentTime" -> vCurrentTime <- Decode.number (key :: path) value
            | "customAssets" -> vCustomAssets <- (Decode.list decodeCustomAsset) (key :: path) value
            | "customCrops" -> vCustomCrops <- Decode.optional (Decode.list decodeCustomCropDefinition) (key :: path) value
            | "playerCustomImage" -> vPlayerCustomImage <- Decode.optional Decode.string (key :: path) value
            | "playerVisual" -> vPlayerVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "graphics" -> vGraphics <- Decode.optional decodeGraphicsSettings (key :: path) value
            | "gamePanels" -> vGamePanels <- Decode.optional (Decode.list decodeGamePanel) (key :: path) value
            | "currentSeason" -> vCurrentSeason <- Decode.string (key :: path) value
            | "currentDay" -> vCurrentDay <- Decode.number (key :: path) value
            | "currentDayOfSeason" -> vCurrentDayOfSeason <- Decode.optional Decode.number (key :: path) value
            | "currentTimeMinutes" -> vCurrentTimeMinutes <- Decode.number (key :: path) value
            | "currentYear" -> vCurrentYear <- Decode.number (key :: path) value
            | "gameStartTime" -> vGameStartTime <- Decode.number (key :: path) value
            | "shops" -> vShops <- (Decode.list decodeShopDefinition) (key :: path) value
            | "nodeTypes" -> vNodeTypes <- (Decode.list decodeNodeTypeDefinition) (key :: path) value
            | "settings" -> vSettings <- decodeProjectSettings (key :: path) value
            | "recipes" -> vRecipes <- (Decode.list decodeRecipeDefinition) (key :: path) value
            | "machineTypes" -> vMachineTypes <- (Decode.list decodeMachineTypeDefinition) (key :: path) value
            | "weather" -> vWeather <- decodeWeatherConfig (key :: path) value
            | "animalSpecies" -> vAnimalSpecies <- (Decode.list decodeAnimalSpeciesDefinition) (key :: path) value
            | "animals" -> vAnimals <- (Decode.list decodeAnimalState) (key :: path) value
            | "fishTables" -> vFishTables <- (Decode.list decodeFishTable) (key :: path) value
            | "mine" -> vMine <- decodeMineConfig (key :: path) value
            | "actions" -> vActions <- (Decode.list decodeActionDef) (key :: path) value
            | "minigames" -> vMinigames <- (Decode.list decodeMinigameDef) (key :: path) value
            | "contentPacks" -> vContentPacks <- (Decode.list decodePackInstallation) (key :: path) value
            | "export" -> vExport <- Decode.optional decodeExportSettings (key :: path) value
            | "rngState" -> vRngState <- Decode.optional decodeRngState (key :: path) value
            | "currentWeatherId" -> vCurrentWeatherId <- Decode.optional Decode.string (key :: path) value
            | "socialState" -> vSocialState <- Decode.optional (Decode.dict decodeNpcSocialState) (key :: path) value
            | "mineDeepestFloor" -> vMineDeepestFloor <- Decode.optional Decode.number (key :: path) value
            | "quarantinedItems" -> vQuarantinedItems <- Decode.optional (Decode.list decodeInventorySlot) (key :: path) value
            | "keptState" -> vKeptState <- Decode.optional Decode.json (key :: path) value
            | _ -> extra.Add((key, value))
        { SchemaVersion = vSchemaVersion; Id = vId; Name = vName; Version = vVersion; Scenes = vScenes; Npcs = vNpcs; Items = vItems; Events = vEvents; Dialogues = vDialogues; Quests = vQuests; Player = vPlayer; EventFlags = vEventFlags; StartSceneId = vStartSceneId; Mode = vMode; SelectedTileType = vSelectedTileType; SelectedTileVisual = vSelectedTileVisual; SelectedNpcId = vSelectedNpcId; SelectedItemId = vSelectedItemId; CurrentTime = vCurrentTime; CustomAssets = vCustomAssets; CustomCrops = vCustomCrops; PlayerCustomImage = vPlayerCustomImage; PlayerVisual = vPlayerVisual; Graphics = vGraphics; GamePanels = vGamePanels; CurrentSeason = vCurrentSeason; CurrentDay = vCurrentDay; CurrentDayOfSeason = vCurrentDayOfSeason; CurrentTimeMinutes = vCurrentTimeMinutes; CurrentYear = vCurrentYear; GameStartTime = vGameStartTime; Shops = vShops; NodeTypes = vNodeTypes; Settings = vSettings; Recipes = vRecipes; MachineTypes = vMachineTypes; Weather = vWeather; AnimalSpecies = vAnimalSpecies; Animals = vAnimals; FishTables = vFishTables; Mine = vMine; Actions = vActions; Minigames = vMinigames; ContentPacks = vContentPacks; Export = vExport; RngState = vRngState; CurrentWeatherId = vCurrentWeatherId; SocialState = vSocialState; MineDeepestFloor = vMineDeepestFloor; QuarantinedItems = vQuarantinedItems; KeptState = vKeptState; Extra = List.ofSeq extra }

    and encodeGameProject (value: GameProject) : Json =
        JObject(
            [
                yield "schemaVersion", JNumber value.SchemaVersion
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "version", JString value.Version
                yield "scenes", (Encode.list encodeScene) value.Scenes
                yield "npcs", (Encode.list encodeNpc) value.Npcs
                yield "items", (Encode.list encodeItem) value.Items
                yield "events", (Encode.list encodeGameEvent) value.Events
                yield "dialogues", (Encode.list encodeDialogue) value.Dialogues
                yield "quests", (Encode.list encodeQuest) value.Quests
                yield "player", encodePlayer value.Player
                yield "eventFlags", (Encode.dict id) value.EventFlags
                yield "startSceneId", JString value.StartSceneId
                yield "mode", JString value.Mode
                yield "selectedTileType", JString value.SelectedTileType
                match value.SelectedTileVisual with
                | Some v -> yield "selectedTileVisual", encodeVisualRef v
                | None -> ()
                match value.SelectedNpcId with
                | Some v -> yield "selectedNPCId", JString v
                | None -> yield "selectedNPCId", JNull
                match value.SelectedItemId with
                | Some v -> yield "selectedItemId", JString v
                | None -> yield "selectedItemId", JNull
                yield "currentTime", JNumber value.CurrentTime
                yield "customAssets", (Encode.list encodeCustomAsset) value.CustomAssets
                match value.CustomCrops with
                | Some v -> yield "customCrops", (Encode.list encodeCustomCropDefinition) v
                | None -> ()
                match value.PlayerCustomImage with
                | Some v -> yield "playerCustomImage", JString v
                | None -> ()
                match value.PlayerVisual with
                | Some v -> yield "playerVisual", encodeVisualRef v
                | None -> ()
                match value.Graphics with
                | Some v -> yield "graphics", encodeGraphicsSettings v
                | None -> ()
                match value.GamePanels with
                | Some v -> yield "gamePanels", (Encode.list encodeGamePanel) v
                | None -> ()
                yield "currentSeason", JString value.CurrentSeason
                yield "currentDay", JNumber value.CurrentDay
                match value.CurrentDayOfSeason with
                | Some v -> yield "currentDayOfSeason", JNumber v
                | None -> ()
                yield "currentTimeMinutes", JNumber value.CurrentTimeMinutes
                yield "currentYear", JNumber value.CurrentYear
                yield "gameStartTime", JNumber value.GameStartTime
                yield "shops", (Encode.list encodeShopDefinition) value.Shops
                yield "nodeTypes", (Encode.list encodeNodeTypeDefinition) value.NodeTypes
                yield "settings", encodeProjectSettings value.Settings
                yield "recipes", (Encode.list encodeRecipeDefinition) value.Recipes
                yield "machineTypes", (Encode.list encodeMachineTypeDefinition) value.MachineTypes
                yield "weather", encodeWeatherConfig value.Weather
                yield "animalSpecies", (Encode.list encodeAnimalSpeciesDefinition) value.AnimalSpecies
                yield "animals", (Encode.list encodeAnimalState) value.Animals
                yield "fishTables", (Encode.list encodeFishTable) value.FishTables
                yield "mine", encodeMineConfig value.Mine
                yield "actions", (Encode.list encodeActionDef) value.Actions
                yield "minigames", (Encode.list encodeMinigameDef) value.Minigames
                yield "contentPacks", (Encode.list encodePackInstallation) value.ContentPacks
                match value.Export with
                | Some v -> yield "export", encodeExportSettings v
                | None -> ()
                match value.RngState with
                | Some v -> yield "rngState", encodeRngState v
                | None -> ()
                match value.CurrentWeatherId with
                | Some v -> yield "currentWeatherId", JString v
                | None -> ()
                match value.SocialState with
                | Some v -> yield "socialState", (Encode.dict encodeNpcSocialState) v
                | None -> ()
                match value.MineDeepestFloor with
                | Some v -> yield "mineDeepestFloor", JNumber v
                | None -> ()
                match value.QuarantinedItems with
                | Some v -> yield "quarantinedItems", (Encode.list encodeInventorySlot) v
                | None -> ()
                match value.KeptState with
                | Some v -> yield "keptState", v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeGiftTastes (path: Path) (json: Json) : GiftTastes =
        let members = Decode.object path json
        let mutable vLoved = []
        let mutable vLiked = []
        let mutable vDisliked = []
        let mutable vHated = []
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "loved" -> vLoved <- (Decode.list Decode.string) (key :: path) value
            | "liked" -> vLiked <- (Decode.list Decode.string) (key :: path) value
            | "disliked" -> vDisliked <- (Decode.list Decode.string) (key :: path) value
            | "hated" -> vHated <- (Decode.list Decode.string) (key :: path) value
            | _ -> extra.Add((key, value))
        { Loved = vLoved; Liked = vLiked; Disliked = vDisliked; Hated = vHated; Extra = List.ofSeq extra }

    and encodeGiftTastes (value: GiftTastes) : Json =
        JObject(
            [
                yield "loved", (Encode.list JString) value.Loved
                yield "liked", (Encode.list JString) value.Liked
                yield "disliked", (Encode.list JString) value.Disliked
                yield "hated", (Encode.list JString) value.Hated
                yield! value.Extra
            ]
        )

    and decodeGraphicsSettings (path: Path) (json: Json) : GraphicsSettings =
        let members = Decode.object path json
        let mutable vPixelArt = true
        for (key, value) in members do
            match key with
            | "pixelArt" -> vPixelArt <- Decode.boolean (key :: path) value
            | _ -> ()
        { PixelArt = vPixelArt }

    and encodeGraphicsSettings (value: GraphicsSettings) : Json =
        JObject(
            [
                yield "pixelArt", JBool value.PixelArt
            ]
        )

    and decodeGridPoint (path: Path) (json: Json) : GridPoint =
        let members = Decode.object path json
        let mutable vX = 0.0
        let mutable vY = 0.0
        for (key, value) in members do
            match key with
            | "x" -> vX <- Decode.number (key :: path) value
            | "y" -> vY <- Decode.number (key :: path) value
            | _ -> ()
        { X = vX; Y = vY }

    and encodeGridPoint (value: GridPoint) : Json =
        JObject(
            [
                yield "x", JNumber value.X
                yield "y", JNumber value.Y
            ]
        )

    and decodeInventorySlot (path: Path) (json: Json) : InventorySlot =
        let members = Decode.object path json
        let mutable vItem = Item.Default
        let mutable vQuantity = 0.0
        let mutable vQuality = None
        for (key, value) in members do
            match key with
            | "item" -> vItem <- decodeItem (key :: path) value
            | "quantity" -> vQuantity <- Decode.number (key :: path) value
            | "quality" -> vQuality <- Decode.optional Decode.string (key :: path) value
            | _ -> ()
        { Item = vItem; Quantity = vQuantity; Quality = vQuality }

    and encodeInventorySlot (value: InventorySlot) : Json =
        JObject(
            [
                yield "item", encodeItem value.Item
                yield "quantity", JNumber value.Quantity
                match value.Quality with
                | Some v -> yield "quality", JString v
                | None -> ()
            ]
        )

    and decodeItem (path: Path) (json: Json) : Item =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vVisual = None
        let mutable vDescription = ""
        let mutable vType = ""
        let mutable vStackable = false
        let mutable vMaxStack = 0.0
        let mutable vValue = 0.0
        let mutable vCropType = None
        let mutable vCustomImage = None
        let mutable vToolType = None
        let mutable vToolPower = None
        let mutable vToolTier = None
        let mutable vDurability = None
        let mutable vMaxDurability = None
        let mutable vUseActionId = None
        let mutable vConsumeOnUse = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "visual" -> vVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "description" -> vDescription <- Decode.string (key :: path) value
            | "type" -> vType <- Decode.string (key :: path) value
            | "stackable" -> vStackable <- Decode.boolean (key :: path) value
            | "maxStack" -> vMaxStack <- Decode.number (key :: path) value
            | "value" -> vValue <- Decode.number (key :: path) value
            | "cropType" -> vCropType <- Decode.optional Decode.string (key :: path) value
            | "customImage" -> vCustomImage <- Decode.optional Decode.string (key :: path) value
            | "toolType" -> vToolType <- Decode.optional Decode.string (key :: path) value
            | "toolPower" -> vToolPower <- Decode.optional Decode.number (key :: path) value
            | "toolTier" -> vToolTier <- Decode.optional Decode.number (key :: path) value
            | "durability" -> vDurability <- Decode.optional Decode.number (key :: path) value
            | "maxDurability" -> vMaxDurability <- Decode.optional Decode.number (key :: path) value
            | "useActionId" -> vUseActionId <- Decode.optional Decode.string (key :: path) value
            | "consumeOnUse" -> vConsumeOnUse <- Decode.optional Decode.boolean (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Visual = vVisual; Description = vDescription; Type = vType; Stackable = vStackable; MaxStack = vMaxStack; Value = vValue; CropType = vCropType; CustomImage = vCustomImage; ToolType = vToolType; ToolPower = vToolPower; ToolTier = vToolTier; Durability = vDurability; MaxDurability = vMaxDurability; UseActionId = vUseActionId; ConsumeOnUse = vConsumeOnUse; Extra = List.ofSeq extra }

    and encodeItem (value: Item) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                match value.Visual with
                | Some v -> yield "visual", encodeVisualRef v
                | None -> ()
                yield "description", JString value.Description
                yield "type", JString value.Type
                yield "stackable", JBool value.Stackable
                yield "maxStack", JNumber value.MaxStack
                yield "value", JNumber value.Value
                match value.CropType with
                | Some v -> yield "cropType", JString v
                | None -> ()
                match value.CustomImage with
                | Some v -> yield "customImage", JString v
                | None -> ()
                match value.ToolType with
                | Some v -> yield "toolType", JString v
                | None -> ()
                match value.ToolPower with
                | Some v -> yield "toolPower", JNumber v
                | None -> ()
                match value.ToolTier with
                | Some v -> yield "toolTier", JNumber v
                | None -> ()
                match value.Durability with
                | Some v -> yield "durability", JNumber v
                | None -> ()
                match value.MaxDurability with
                | Some v -> yield "maxDurability", JNumber v
                | None -> ()
                match value.UseActionId with
                | Some v -> yield "useActionId", JString v
                | None -> ()
                match value.ConsumeOnUse with
                | Some v -> yield "consumeOnUse", JBool v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeMachineProcessing (path: Path) (json: Json) : MachineProcessing =
        let members = Decode.object path json
        let mutable vRecipeId = ""
        let mutable vCompletesAtMinute = 0.0
        for (key, value) in members do
            match key with
            | "recipeId" -> vRecipeId <- Decode.string (key :: path) value
            | "completesAtMinute" -> vCompletesAtMinute <- Decode.number (key :: path) value
            | _ -> ()
        { RecipeId = vRecipeId; CompletesAtMinute = vCompletesAtMinute }

    and encodeMachineProcessing (value: MachineProcessing) : Json =
        JObject(
            [
                yield "recipeId", JString value.RecipeId
                yield "completesAtMinute", JNumber value.CompletesAtMinute
            ]
        )

    and decodeMachineTypeDefinition (path: Path) (json: Json) : MachineTypeDefinition =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vVisual = None
        let mutable vDescription = ""
        let mutable vColor = "#9a7b4f"
        let mutable vItemId = None
        let mutable vBlocksMovement = true
        let mutable vStationCategories = []
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "visual" -> vVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "description" -> vDescription <- Decode.string (key :: path) value
            | "color" -> vColor <- Decode.string (key :: path) value
            | "itemId" -> vItemId <- Decode.optional Decode.string (key :: path) value
            | "blocksMovement" -> vBlocksMovement <- Decode.boolean (key :: path) value
            | "stationCategories" -> vStationCategories <- (Decode.list Decode.string) (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Visual = vVisual; Description = vDescription; Color = vColor; ItemId = vItemId; BlocksMovement = vBlocksMovement; StationCategories = vStationCategories; Extra = List.ofSeq extra }

    and encodeMachineTypeDefinition (value: MachineTypeDefinition) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                match value.Visual with
                | Some v -> yield "visual", encodeVisualRef v
                | None -> ()
                yield "description", JString value.Description
                yield "color", JString value.Color
                match value.ItemId with
                | Some v -> yield "itemId", JString v
                | None -> ()
                yield "blocksMovement", JBool value.BlocksMovement
                yield "stationCategories", (Encode.list JString) value.StationCategories
                yield! value.Extra
            ]
        )

    and decodeMineBand (path: Path) (json: Json) : MineBand =
        let members = Decode.object path json
        let mutable vFromFloor = 0.0
        let mutable vToFloor = 0.0
        let mutable vRocks = []
        let mutable vDensity = 0.35
        for (key, value) in members do
            match key with
            | "fromFloor" -> vFromFloor <- Decode.number (key :: path) value
            | "toFloor" -> vToFloor <- Decode.number (key :: path) value
            | "rocks" -> vRocks <- (Decode.list decodeMineRockWeight) (key :: path) value
            | "density" -> vDensity <- Decode.number (key :: path) value
            | _ -> ()
        { FromFloor = vFromFloor; ToFloor = vToFloor; Rocks = vRocks; Density = vDensity }

    and encodeMineBand (value: MineBand) : Json =
        JObject(
            [
                yield "fromFloor", JNumber value.FromFloor
                yield "toFloor", JNumber value.ToFloor
                yield "rocks", (Encode.list encodeMineRockWeight) value.Rocks
                yield "density", JNumber value.Density
            ]
        )

    and decodeMineConfig (path: Path) (json: Json) : MineConfig =
        let members = Decode.object path json
        let mutable vEnabled = false
        let mutable vEntranceSceneId = None
        let mutable vEntranceX = None
        let mutable vEntranceY = None
        let mutable vFloors = 20.0
        let mutable vFloorWidth = 14.0
        let mutable vFloorHeight = 12.0
        let mutable vBands = []
        let mutable vLadderChance = 0.18
        let mutable vElevatorEvery = 5.0
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "enabled" -> vEnabled <- Decode.boolean (key :: path) value
            | "entranceSceneId" -> vEntranceSceneId <- Decode.optional Decode.string (key :: path) value
            | "entranceX" -> vEntranceX <- Decode.optional Decode.number (key :: path) value
            | "entranceY" -> vEntranceY <- Decode.optional Decode.number (key :: path) value
            | "floors" -> vFloors <- Decode.number (key :: path) value
            | "floorWidth" -> vFloorWidth <- Decode.number (key :: path) value
            | "floorHeight" -> vFloorHeight <- Decode.number (key :: path) value
            | "bands" -> vBands <- (Decode.list decodeMineBand) (key :: path) value
            | "ladderChance" -> vLadderChance <- Decode.number (key :: path) value
            | "elevatorEvery" -> vElevatorEvery <- Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { Enabled = vEnabled; EntranceSceneId = vEntranceSceneId; EntranceX = vEntranceX; EntranceY = vEntranceY; Floors = vFloors; FloorWidth = vFloorWidth; FloorHeight = vFloorHeight; Bands = vBands; LadderChance = vLadderChance; ElevatorEvery = vElevatorEvery; Extra = List.ofSeq extra }

    and encodeMineConfig (value: MineConfig) : Json =
        JObject(
            [
                yield "enabled", JBool value.Enabled
                match value.EntranceSceneId with
                | Some v -> yield "entranceSceneId", JString v
                | None -> ()
                match value.EntranceX with
                | Some v -> yield "entranceX", JNumber v
                | None -> ()
                match value.EntranceY with
                | Some v -> yield "entranceY", JNumber v
                | None -> ()
                yield "floors", JNumber value.Floors
                yield "floorWidth", JNumber value.FloorWidth
                yield "floorHeight", JNumber value.FloorHeight
                yield "bands", (Encode.list encodeMineBand) value.Bands
                yield "ladderChance", JNumber value.LadderChance
                yield "elevatorEvery", JNumber value.ElevatorEvery
                yield! value.Extra
            ]
        )

    and decodeMineRockWeight (path: Path) (json: Json) : MineRockWeight =
        let members = Decode.object path json
        let mutable vNodeTypeId = ""
        let mutable vWeight = 0.0
        for (key, value) in members do
            match key with
            | "nodeTypeId" -> vNodeTypeId <- Decode.string (key :: path) value
            | "weight" -> vWeight <- Decode.number (key :: path) value
            | _ -> ()
        { NodeTypeId = vNodeTypeId; Weight = vWeight }

    and encodeMineRockWeight (value: MineRockWeight) : Json =
        JObject(
            [
                yield "nodeTypeId", JString value.NodeTypeId
                yield "weight", JNumber value.Weight
            ]
        )

    and decodeMinigameDef (path: Path) (json: Json) : MinigameDef =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vKind = "timing-bar"
        let mutable vConfig = []
        let mutable vResultTiers = []
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "kind" -> vKind <- Decode.string (key :: path) value
            | "config" -> vConfig <- (Decode.dict Decode.json) (key :: path) value
            | "resultTiers" -> vResultTiers <- (Decode.list decodeMinigameResultTier) (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Kind = vKind; Config = vConfig; ResultTiers = vResultTiers; Extra = List.ofSeq extra }

    and encodeMinigameDef (value: MinigameDef) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "kind", JString value.Kind
                yield "config", (Encode.dict id) value.Config
                yield "resultTiers", (Encode.list encodeMinigameResultTier) value.ResultTiers
                yield! value.Extra
            ]
        )

    and decodeMinigameResultTier (path: Path) (json: Json) : MinigameResultTier =
        let members = Decode.object path json
        let mutable vMinScore = 0.0
        let mutable vOutcomes = []
        for (key, value) in members do
            match key with
            | "minScore" -> vMinScore <- Decode.number (key :: path) value
            | "outcomes" -> vOutcomes <- (Decode.list decodeEventOutcome) (key :: path) value
            | _ -> ()
        { MinScore = vMinScore; Outcomes = vOutcomes }

    and encodeMinigameResultTier (value: MinigameResultTier) : Json =
        JObject(
            [
                yield "minScore", JNumber value.MinScore
                yield "outcomes", (Encode.list encodeEventOutcome) value.Outcomes
            ]
        )

    and decodeMovementConfig (path: Path) (json: Json) : MovementConfig =
        let members = Decode.object path json
        let mutable vPlayerSpeed = 4.5
        for (key, value) in members do
            match key with
            | "playerSpeed" -> vPlayerSpeed <- Decode.number (key :: path) value
            | _ -> ()
        { PlayerSpeed = vPlayerSpeed }

    and encodeMovementConfig (value: MovementConfig) : Json =
        JObject(
            [
                yield "playerSpeed", JNumber value.PlayerSpeed
            ]
        )

    and decodeNodeDrop (path: Path) (json: Json) : NodeDrop =
        let members = Decode.object path json
        let mutable vItemId = ""
        let mutable vMin = 0.0
        let mutable vMax = 0.0
        let mutable vWeight = 0.0
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "itemId" -> vItemId <- Decode.string (key :: path) value
            | "min" -> vMin <- Decode.number (key :: path) value
            | "max" -> vMax <- Decode.number (key :: path) value
            | "weight" -> vWeight <- Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { ItemId = vItemId; Min = vMin; Max = vMax; Weight = vWeight; Extra = List.ofSeq extra }

    and encodeNodeDrop (value: NodeDrop) : Json =
        JObject(
            [
                yield "itemId", JString value.ItemId
                yield "min", JNumber value.Min
                yield "max", JNumber value.Max
                yield "weight", JNumber value.Weight
                yield! value.Extra
            ]
        )

    and decodeNodeTypeDefinition (path: Path) (json: Json) : NodeTypeDefinition =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vVisual = None
        let mutable vHealth = 0.0
        let mutable vRequiredTool = ""
        let mutable vRequiredToolTier = 1.0
        let mutable vDrops = []
        let mutable vRespawnDays = None
        let mutable vColor = "#7a5a3a"
        let mutable vBlocksMovement = true
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "visual" -> vVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "health" -> vHealth <- Decode.number (key :: path) value
            | "requiredTool" -> vRequiredTool <- Decode.string (key :: path) value
            | "requiredToolTier" -> vRequiredToolTier <- Decode.number (key :: path) value
            | "drops" -> vDrops <- (Decode.list decodeNodeDrop) (key :: path) value
            | "respawnDays" -> vRespawnDays <- Decode.optionalNullable Decode.number (key :: path) value
            | "color" -> vColor <- Decode.string (key :: path) value
            | "blocksMovement" -> vBlocksMovement <- Decode.boolean (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Visual = vVisual; Health = vHealth; RequiredTool = vRequiredTool; RequiredToolTier = vRequiredToolTier; Drops = vDrops; RespawnDays = vRespawnDays; Color = vColor; BlocksMovement = vBlocksMovement; Extra = List.ofSeq extra }

    and encodeNodeTypeDefinition (value: NodeTypeDefinition) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                match value.Visual with
                | Some v -> yield "visual", encodeVisualRef v
                | None -> ()
                yield "health", JNumber value.Health
                yield "requiredTool", JString value.RequiredTool
                yield "requiredToolTier", JNumber value.RequiredToolTier
                yield "drops", (Encode.list encodeNodeDrop) value.Drops
                match value.RespawnDays with
                | Some(Some n) -> yield "respawnDays", JNumber n
                | Some None -> yield "respawnDays", JNull
                | None -> ()
                yield "color", JString value.Color
                yield "blocksMovement", JBool value.BlocksMovement
                yield! value.Extra
            ]
        )

    and decodeNpc (path: Path) (json: Json) : Npc =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vVisual = None
        let mutable vX = 0.0
        let mutable vY = 0.0
        let mutable vSceneId = ""
        let mutable vDialogue = []
        let mutable vCanMove = false
        let mutable vMovePattern = None
        let mutable vWanderRadius = None
        let mutable vPatrolPoints = None
        let mutable vSchedule = None
        let mutable vGiftTastes = None
        let mutable vBirthday = None
        let mutable vAppearance = ""
        let mutable vCustomImage = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "visual" -> vVisual <- Decode.optional decodeVisualRef (key :: path) value
            | "x" -> vX <- Decode.number (key :: path) value
            | "y" -> vY <- Decode.number (key :: path) value
            | "sceneId" -> vSceneId <- Decode.string (key :: path) value
            | "dialogue" -> vDialogue <- (Decode.list decodeDialogue) (key :: path) value
            | "canMove" -> vCanMove <- Decode.boolean (key :: path) value
            | "movePattern" -> vMovePattern <- Decode.optional Decode.string (key :: path) value
            | "wanderRadius" -> vWanderRadius <- Decode.optional Decode.number (key :: path) value
            | "patrolPoints" -> vPatrolPoints <- Decode.optional (Decode.list decodeGridPoint) (key :: path) value
            | "schedule" -> vSchedule <- Decode.optional (Decode.list decodeNpcScheduleEntry) (key :: path) value
            | "giftTastes" -> vGiftTastes <- Decode.optional decodeGiftTastes (key :: path) value
            | "birthday" -> vBirthday <- Decode.optional decodeNpcBirthday (key :: path) value
            | "appearance" -> vAppearance <- Decode.string (key :: path) value
            | "customImage" -> vCustomImage <- Decode.optional Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Visual = vVisual; X = vX; Y = vY; SceneId = vSceneId; Dialogue = vDialogue; CanMove = vCanMove; MovePattern = vMovePattern; WanderRadius = vWanderRadius; PatrolPoints = vPatrolPoints; Schedule = vSchedule; GiftTastes = vGiftTastes; Birthday = vBirthday; Appearance = vAppearance; CustomImage = vCustomImage; Extra = List.ofSeq extra }

    and encodeNpc (value: Npc) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                match value.Visual with
                | Some v -> yield "visual", encodeVisualRef v
                | None -> ()
                yield "x", JNumber value.X
                yield "y", JNumber value.Y
                yield "sceneId", JString value.SceneId
                yield "dialogue", (Encode.list encodeDialogue) value.Dialogue
                yield "canMove", JBool value.CanMove
                match value.MovePattern with
                | Some v -> yield "movePattern", JString v
                | None -> ()
                match value.WanderRadius with
                | Some v -> yield "wanderRadius", JNumber v
                | None -> ()
                match value.PatrolPoints with
                | Some v -> yield "patrolPoints", (Encode.list encodeGridPoint) v
                | None -> ()
                match value.Schedule with
                | Some v -> yield "schedule", (Encode.list encodeNpcScheduleEntry) v
                | None -> ()
                match value.GiftTastes with
                | Some v -> yield "giftTastes", encodeGiftTastes v
                | None -> ()
                match value.Birthday with
                | Some v -> yield "birthday", encodeNpcBirthday v
                | None -> ()
                yield "appearance", JString value.Appearance
                match value.CustomImage with
                | Some v -> yield "customImage", JString v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeNpcBirthday (path: Path) (json: Json) : NpcBirthday =
        let members = Decode.object path json
        let mutable vSeason = ""
        let mutable vDay = 0.0
        for (key, value) in members do
            match key with
            | "season" -> vSeason <- Decode.string (key :: path) value
            | "day" -> vDay <- Decode.number (key :: path) value
            | _ -> ()
        { Season = vSeason; Day = vDay }

    and encodeNpcBirthday (value: NpcBirthday) : Json =
        JObject(
            [
                yield "season", JString value.Season
                yield "day", JNumber value.Day
            ]
        )

    and decodeNpcScheduleEntry (path: Path) (json: Json) : NpcScheduleEntry =
        let members = Decode.object path json
        let mutable vMinute = 0.0
        let mutable vSceneId = ""
        let mutable vX = 0.0
        let mutable vY = 0.0
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "minute" -> vMinute <- Decode.number (key :: path) value
            | "sceneId" -> vSceneId <- Decode.string (key :: path) value
            | "x" -> vX <- Decode.number (key :: path) value
            | "y" -> vY <- Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { Minute = vMinute; SceneId = vSceneId; X = vX; Y = vY; Extra = List.ofSeq extra }

    and encodeNpcScheduleEntry (value: NpcScheduleEntry) : Json =
        JObject(
            [
                yield "minute", JNumber value.Minute
                yield "sceneId", JString value.SceneId
                yield "x", JNumber value.X
                yield "y", JNumber value.Y
                yield! value.Extra
            ]
        )

    and decodeNpcSocialState (path: Path) (json: Json) : NpcSocialState =
        let members = Decode.object path json
        let mutable vFriendship = 0.0
        let mutable vGiftsToday = 0.0
        let mutable vLastGiftDay = None
        for (key, value) in members do
            match key with
            | "friendship" -> vFriendship <- Decode.number (key :: path) value
            | "giftsToday" -> vGiftsToday <- Decode.number (key :: path) value
            | "lastGiftDay" -> vLastGiftDay <- Decode.optional Decode.number (key :: path) value
            | _ -> ()
        { Friendship = vFriendship; GiftsToday = vGiftsToday; LastGiftDay = vLastGiftDay }

    and encodeNpcSocialState (value: NpcSocialState) : Json =
        JObject(
            [
                yield "friendship", JNumber value.Friendship
                yield "giftsToday", JNumber value.GiftsToday
                match value.LastGiftDay with
                | Some v -> yield "lastGiftDay", JNumber v
                | None -> ()
            ]
        )

    and decodePackContent (path: Path) (json: Json) : PackContent =
        let members = Decode.object path json
        let mutable vCrops = []
        let mutable vItems = []
        let mutable vRecipes = []
        let mutable vMachineTypes = []
        let mutable vNodeTypes = []
        let mutable vAnimalSpecies = []
        let mutable vFishTables = []
        let mutable vWeatherTypes = []
        let mutable vNpcs = []
        let mutable vDialogues = []
        let mutable vScenes = []
        let mutable vEvents = []
        let mutable vQuests = []
        let mutable vShops = []
        let mutable vActions = []
        let mutable vMinigames = []
        let mutable vPlayerStart = None
        let mutable vStrings = []
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "crops" -> vCrops <- (Decode.list decodeCropDefinition) (key :: path) value
            | "items" -> vItems <- (Decode.list decodeItem) (key :: path) value
            | "recipes" -> vRecipes <- (Decode.list decodeRecipeDefinition) (key :: path) value
            | "machineTypes" -> vMachineTypes <- (Decode.list decodeMachineTypeDefinition) (key :: path) value
            | "nodeTypes" -> vNodeTypes <- (Decode.list decodeNodeTypeDefinition) (key :: path) value
            | "animalSpecies" -> vAnimalSpecies <- (Decode.list decodeAnimalSpeciesDefinition) (key :: path) value
            | "fishTables" -> vFishTables <- (Decode.list decodeFishTable) (key :: path) value
            | "weatherTypes" -> vWeatherTypes <- (Decode.list decodeWeatherTypeDefinition) (key :: path) value
            | "npcs" -> vNpcs <- (Decode.list decodeNpc) (key :: path) value
            | "dialogues" -> vDialogues <- (Decode.list decodeDialogue) (key :: path) value
            | "scenes" -> vScenes <- (Decode.list decodeScene) (key :: path) value
            | "events" -> vEvents <- (Decode.list decodeGameEvent) (key :: path) value
            | "quests" -> vQuests <- (Decode.list decodeQuest) (key :: path) value
            | "shops" -> vShops <- (Decode.list decodeShopDefinition) (key :: path) value
            | "actions" -> vActions <- (Decode.list decodeActionDef) (key :: path) value
            | "minigames" -> vMinigames <- (Decode.list decodeMinigameDef) (key :: path) value
            | "playerStart" -> vPlayerStart <- Decode.optional decodePackPlayerStart (key :: path) value
            | "strings" -> vStrings <- (Decode.dict (Decode.dict Decode.string)) (key :: path) value
            | _ -> extra.Add((key, value))
        { Crops = vCrops; Items = vItems; Recipes = vRecipes; MachineTypes = vMachineTypes; NodeTypes = vNodeTypes; AnimalSpecies = vAnimalSpecies; FishTables = vFishTables; WeatherTypes = vWeatherTypes; Npcs = vNpcs; Dialogues = vDialogues; Scenes = vScenes; Events = vEvents; Quests = vQuests; Shops = vShops; Actions = vActions; Minigames = vMinigames; PlayerStart = vPlayerStart; Strings = vStrings; Extra = List.ofSeq extra }

    and encodePackContent (value: PackContent) : Json =
        JObject(
            [
                yield "crops", (Encode.list encodeCropDefinition) value.Crops
                yield "items", (Encode.list encodeItem) value.Items
                yield "recipes", (Encode.list encodeRecipeDefinition) value.Recipes
                yield "machineTypes", (Encode.list encodeMachineTypeDefinition) value.MachineTypes
                yield "nodeTypes", (Encode.list encodeNodeTypeDefinition) value.NodeTypes
                yield "animalSpecies", (Encode.list encodeAnimalSpeciesDefinition) value.AnimalSpecies
                yield "fishTables", (Encode.list encodeFishTable) value.FishTables
                yield "weatherTypes", (Encode.list encodeWeatherTypeDefinition) value.WeatherTypes
                yield "npcs", (Encode.list encodeNpc) value.Npcs
                yield "dialogues", (Encode.list encodeDialogue) value.Dialogues
                yield "scenes", (Encode.list encodeScene) value.Scenes
                yield "events", (Encode.list encodeGameEvent) value.Events
                yield "quests", (Encode.list encodeQuest) value.Quests
                yield "shops", (Encode.list encodeShopDefinition) value.Shops
                yield "actions", (Encode.list encodeActionDef) value.Actions
                yield "minigames", (Encode.list encodeMinigameDef) value.Minigames
                match value.PlayerStart with
                | Some v -> yield "playerStart", encodePackPlayerStart v
                | None -> ()
                yield "strings", (Encode.dict (Encode.dict JString)) value.Strings
                yield! value.Extra
            ]
        )

    and decodePackDependency (path: Path) (json: Json) : PackDependency =
        let members = Decode.object path json
        let mutable vPackId = ""
        let mutable vVersion = None
        for (key, value) in members do
            match key with
            | "packId" -> vPackId <- Decode.string (key :: path) value
            | "version" -> vVersion <- Decode.optional Decode.string (key :: path) value
            | _ -> ()
        { PackId = vPackId; Version = vVersion }

    and encodePackDependency (value: PackDependency) : Json =
        JObject(
            [
                yield "packId", JString value.PackId
                match value.Version with
                | Some v -> yield "version", JString v
                | None -> ()
            ]
        )

    and decodePackInstallation (path: Path) (json: Json) : PackInstallation =
        let members = Decode.object path json
        let mutable vPack = ContentPack.Default
        let mutable vEnabled = true
        for (key, value) in members do
            match key with
            | "pack" -> vPack <- decodeContentPack (key :: path) value
            | "enabled" -> vEnabled <- Decode.boolean (key :: path) value
            | _ -> ()
        { Pack = vPack; Enabled = vEnabled }

    and encodePackInstallation (value: PackInstallation) : Json =
        JObject(
            [
                yield "pack", encodeContentPack value.Pack
                yield "enabled", JBool value.Enabled
            ]
        )

    and decodePackManifest (path: Path) (json: Json) : PackManifest =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vVersion = ""
        let mutable vDescription = None
        let mutable vAuthor = None
        let mutable vEngineCompatibility = "*"
        let mutable vBase = false
        let mutable vDependencies = []
        let mutable vOverrides = []
        let mutable vPermissions = PackPermissions.Default
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "version" -> vVersion <- Decode.string (key :: path) value
            | "description" -> vDescription <- Decode.optional Decode.string (key :: path) value
            | "author" -> vAuthor <- Decode.optional Decode.string (key :: path) value
            | "engineCompatibility" -> vEngineCompatibility <- Decode.string (key :: path) value
            | "base" -> vBase <- Decode.boolean (key :: path) value
            | "dependencies" -> vDependencies <- (Decode.list decodePackDependency) (key :: path) value
            | "overrides" -> vOverrides <- (Decode.list Decode.string) (key :: path) value
            | "permissions" -> vPermissions <- decodePackPermissions (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Version = vVersion; Description = vDescription; Author = vAuthor; EngineCompatibility = vEngineCompatibility; Base = vBase; Dependencies = vDependencies; Overrides = vOverrides; Permissions = vPermissions; Extra = List.ofSeq extra }

    and encodePackManifest (value: PackManifest) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "version", JString value.Version
                match value.Description with
                | Some v -> yield "description", JString v
                | None -> ()
                match value.Author with
                | Some v -> yield "author", JString v
                | None -> ()
                yield "engineCompatibility", JString value.EngineCompatibility
                yield "base", JBool value.Base
                yield "dependencies", (Encode.list encodePackDependency) value.Dependencies
                yield "overrides", (Encode.list JString) value.Overrides
                yield "permissions", encodePackPermissions value.Permissions
                yield! value.Extra
            ]
        )

    and decodePackPermissions (path: Path) (json: Json) : PackPermissions =
        let members = Decode.object path json
        let mutable vHooks = []
        let mutable vContentInject = true
        let mutable vUiPanels = false
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "hooks" -> vHooks <- (Decode.list Decode.string) (key :: path) value
            | "contentInject" -> vContentInject <- Decode.boolean (key :: path) value
            | "uiPanels" -> vUiPanels <- Decode.boolean (key :: path) value
            | _ -> extra.Add((key, value))
        { Hooks = vHooks; ContentInject = vContentInject; UiPanels = vUiPanels; Extra = List.ofSeq extra }

    and encodePackPermissions (value: PackPermissions) : Json =
        JObject(
            [
                yield "hooks", (Encode.list JString) value.Hooks
                yield "contentInject", JBool value.ContentInject
                yield "uiPanels", JBool value.UiPanels
                yield! value.Extra
            ]
        )

    and decodePackPlayerStart (path: Path) (json: Json) : PackPlayerStart =
        let members = Decode.object path json
        let mutable vSceneId = None
        let mutable vX = None
        let mutable vY = None
        let mutable vMoney = None
        let mutable vInventory = []
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "sceneId" -> vSceneId <- Decode.optional Decode.string (key :: path) value
            | "x" -> vX <- Decode.optional Decode.number (key :: path) value
            | "y" -> vY <- Decode.optional Decode.number (key :: path) value
            | "money" -> vMoney <- Decode.optional Decode.number (key :: path) value
            | "inventory" -> vInventory <- (Decode.list decodePackStartItem) (key :: path) value
            | _ -> extra.Add((key, value))
        { SceneId = vSceneId; X = vX; Y = vY; Money = vMoney; Inventory = vInventory; Extra = List.ofSeq extra }

    and encodePackPlayerStart (value: PackPlayerStart) : Json =
        JObject(
            [
                match value.SceneId with
                | Some v -> yield "sceneId", JString v
                | None -> ()
                match value.X with
                | Some v -> yield "x", JNumber v
                | None -> ()
                match value.Y with
                | Some v -> yield "y", JNumber v
                | None -> ()
                match value.Money with
                | Some v -> yield "money", JNumber v
                | None -> ()
                yield "inventory", (Encode.list encodePackStartItem) value.Inventory
                yield! value.Extra
            ]
        )

    and decodePackPlugin (path: Path) (json: Json) : PackPlugin =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = None
        let mutable vHooks = []
        let mutable vSource = ""
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.optional Decode.string (key :: path) value
            | "hooks" -> vHooks <- (Decode.list Decode.string) (key :: path) value
            | "source" -> vSource <- Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Hooks = vHooks; Source = vSource; Extra = List.ofSeq extra }

    and encodePackPlugin (value: PackPlugin) : Json =
        JObject(
            [
                yield "id", JString value.Id
                match value.Name with
                | Some v -> yield "name", JString v
                | None -> ()
                yield "hooks", (Encode.list JString) value.Hooks
                yield "source", JString value.Source
                yield! value.Extra
            ]
        )

    and decodePackStartItem (path: Path) (json: Json) : PackStartItem =
        let members = Decode.object path json
        let mutable vItemId = ""
        let mutable vQuantity = 0.0
        for (key, value) in members do
            match key with
            | "itemId" -> vItemId <- Decode.string (key :: path) value
            | "quantity" -> vQuantity <- Decode.number (key :: path) value
            | _ -> ()
        { ItemId = vItemId; Quantity = vQuantity }

    and encodePackStartItem (value: PackStartItem) : Json =
        JObject(
            [
                yield "itemId", JString value.ItemId
                yield "quantity", JNumber value.Quantity
            ]
        )

    and decodePlayer (path: Path) (json: Json) : Player =
        let members = Decode.object path json
        let mutable vX = 0.0
        let mutable vY = 0.0
        let mutable vDirection = ""
        let mutable vSceneId = ""
        let mutable vInventory = []
        let mutable vMaxInventorySize = 0.0
        let mutable vMoney = 0.0
        let mutable vEnergy = None
        let mutable vMaxEnergy = None
        let mutable vSkills = None
        let mutable vActiveQuests = []
        let mutable vCompletedQuests = []
        let mutable vEquippedTool = None
        let mutable vPixelX = 0.0
        let mutable vPixelY = 0.0
        let mutable vTargetX = 0.0
        let mutable vTargetY = 0.0
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "x" -> vX <- Decode.number (key :: path) value
            | "y" -> vY <- Decode.number (key :: path) value
            | "direction" -> vDirection <- Decode.string (key :: path) value
            | "sceneId" -> vSceneId <- Decode.string (key :: path) value
            | "inventory" -> vInventory <- (Decode.list decodeInventorySlot) (key :: path) value
            | "maxInventorySize" -> vMaxInventorySize <- Decode.number (key :: path) value
            | "money" -> vMoney <- Decode.number (key :: path) value
            | "energy" -> vEnergy <- Decode.optional Decode.number (key :: path) value
            | "maxEnergy" -> vMaxEnergy <- Decode.optional Decode.number (key :: path) value
            | "skills" -> vSkills <- Decode.optional (Decode.dict decodeSkillState) (key :: path) value
            | "activeQuests" -> vActiveQuests <- (Decode.list Decode.string) (key :: path) value
            | "completedQuests" -> vCompletedQuests <- (Decode.list Decode.string) (key :: path) value
            | "equippedTool" -> vEquippedTool <- Decode.optional Decode.string (key :: path) value
            | "pixelX" -> vPixelX <- Decode.number (key :: path) value
            | "pixelY" -> vPixelY <- Decode.number (key :: path) value
            | "targetX" -> vTargetX <- Decode.number (key :: path) value
            | "targetY" -> vTargetY <- Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { X = vX; Y = vY; Direction = vDirection; SceneId = vSceneId; Inventory = vInventory; MaxInventorySize = vMaxInventorySize; Money = vMoney; Energy = vEnergy; MaxEnergy = vMaxEnergy; Skills = vSkills; ActiveQuests = vActiveQuests; CompletedQuests = vCompletedQuests; EquippedTool = vEquippedTool; PixelX = vPixelX; PixelY = vPixelY; TargetX = vTargetX; TargetY = vTargetY; Extra = List.ofSeq extra }

    and encodePlayer (value: Player) : Json =
        JObject(
            [
                yield "x", JNumber value.X
                yield "y", JNumber value.Y
                yield "direction", JString value.Direction
                yield "sceneId", JString value.SceneId
                yield "inventory", (Encode.list encodeInventorySlot) value.Inventory
                yield "maxInventorySize", JNumber value.MaxInventorySize
                yield "money", JNumber value.Money
                match value.Energy with
                | Some v -> yield "energy", JNumber v
                | None -> ()
                match value.MaxEnergy with
                | Some v -> yield "maxEnergy", JNumber v
                | None -> ()
                match value.Skills with
                | Some v -> yield "skills", (Encode.dict encodeSkillState) v
                | None -> ()
                yield "activeQuests", (Encode.list JString) value.ActiveQuests
                yield "completedQuests", (Encode.list JString) value.CompletedQuests
                match value.EquippedTool with
                | Some v -> yield "equippedTool", JString v
                | None -> ()
                yield "pixelX", JNumber value.PixelX
                yield "pixelY", JNumber value.PixelY
                yield "targetX", JNumber value.TargetX
                yield "targetY", JNumber value.TargetY
                yield! value.Extra
            ]
        )

    and decodeProjectSettings (path: Path) (json: Json) : ProjectSettings =
        let members = Decode.object path json
        let mutable vMovement = MovementConfig.Default
        let mutable vEnergyEnabled = true
        let mutable vMaxEnergy = 100.0
        let mutable vCollapseEnergyFraction = 0.5
        let mutable vCollapseMoneyPenalty = 50.0
        let mutable vTime = TimeConfig.Default
        let mutable vCalendar = CalendarConfig.Default
        let mutable vSkillsEnabled = true
        let mutable vSkillLevelCurve = [ 0.0; 50.0; 150.0; 300.0; 500.0; 750.0; 1050.0; 1400.0; 1800.0; 2250.0 ]
        let mutable vLocale = "en"
        let mutable vShowMadeWithCredit = false
        for (key, value) in members do
            match key with
            | "movement" -> vMovement <- decodeMovementConfig (key :: path) value
            | "energyEnabled" -> vEnergyEnabled <- Decode.boolean (key :: path) value
            | "maxEnergy" -> vMaxEnergy <- Decode.number (key :: path) value
            | "collapseEnergyFraction" -> vCollapseEnergyFraction <- Decode.number (key :: path) value
            | "collapseMoneyPenalty" -> vCollapseMoneyPenalty <- Decode.number (key :: path) value
            | "time" -> vTime <- decodeTimeConfig (key :: path) value
            | "calendar" -> vCalendar <- decodeCalendarConfig (key :: path) value
            | "skillsEnabled" -> vSkillsEnabled <- Decode.boolean (key :: path) value
            | "skillLevelCurve" -> vSkillLevelCurve <- (Decode.list Decode.number) (key :: path) value
            | "locale" -> vLocale <- Decode.string (key :: path) value
            | "showMadeWithCredit" -> vShowMadeWithCredit <- Decode.boolean (key :: path) value
            | _ -> ()
        { Movement = vMovement; EnergyEnabled = vEnergyEnabled; MaxEnergy = vMaxEnergy; CollapseEnergyFraction = vCollapseEnergyFraction; CollapseMoneyPenalty = vCollapseMoneyPenalty; Time = vTime; Calendar = vCalendar; SkillsEnabled = vSkillsEnabled; SkillLevelCurve = vSkillLevelCurve; Locale = vLocale; ShowMadeWithCredit = vShowMadeWithCredit }

    and encodeProjectSettings (value: ProjectSettings) : Json =
        JObject(
            [
                yield "movement", encodeMovementConfig value.Movement
                yield "energyEnabled", JBool value.EnergyEnabled
                yield "maxEnergy", JNumber value.MaxEnergy
                yield "collapseEnergyFraction", JNumber value.CollapseEnergyFraction
                yield "collapseMoneyPenalty", JNumber value.CollapseMoneyPenalty
                yield "time", encodeTimeConfig value.Time
                yield "calendar", encodeCalendarConfig value.Calendar
                yield "skillsEnabled", JBool value.SkillsEnabled
                yield "skillLevelCurve", (Encode.list JNumber) value.SkillLevelCurve
                yield "locale", JString value.Locale
                yield "showMadeWithCredit", JBool value.ShowMadeWithCredit
            ]
        )

    and decodeQuest (path: Path) (json: Json) : Quest =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vDescription = ""
        let mutable vGiver = None
        let mutable vStatus = ""
        let mutable vObjectives = []
        let mutable vRewards = QuestRewards.Default
        let mutable vPrerequisites = None
        let mutable vAutoStart = None
        let mutable vRepeatable = None
        let mutable vAvailableSeasons = None
        let mutable vAvailableFromDay = None
        let mutable vAvailableToDay = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "description" -> vDescription <- Decode.string (key :: path) value
            | "giver" -> vGiver <- Decode.optional Decode.string (key :: path) value
            | "status" -> vStatus <- Decode.string (key :: path) value
            | "objectives" -> vObjectives <- (Decode.list decodeQuestObjective) (key :: path) value
            | "rewards" -> vRewards <- decodeQuestRewards (key :: path) value
            | "prerequisites" -> vPrerequisites <- Decode.optional (Decode.list Decode.string) (key :: path) value
            | "autoStart" -> vAutoStart <- Decode.optional Decode.boolean (key :: path) value
            | "repeatable" -> vRepeatable <- Decode.optional Decode.boolean (key :: path) value
            | "availableSeasons" -> vAvailableSeasons <- Decode.optional (Decode.list Decode.string) (key :: path) value
            | "availableFromDay" -> vAvailableFromDay <- Decode.optional Decode.number (key :: path) value
            | "availableToDay" -> vAvailableToDay <- Decode.optional Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Description = vDescription; Giver = vGiver; Status = vStatus; Objectives = vObjectives; Rewards = vRewards; Prerequisites = vPrerequisites; AutoStart = vAutoStart; Repeatable = vRepeatable; AvailableSeasons = vAvailableSeasons; AvailableFromDay = vAvailableFromDay; AvailableToDay = vAvailableToDay; Extra = List.ofSeq extra }

    and encodeQuest (value: Quest) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "description", JString value.Description
                match value.Giver with
                | Some v -> yield "giver", JString v
                | None -> ()
                yield "status", JString value.Status
                yield "objectives", (Encode.list encodeQuestObjective) value.Objectives
                yield "rewards", encodeQuestRewards value.Rewards
                match value.Prerequisites with
                | Some v -> yield "prerequisites", (Encode.list JString) v
                | None -> ()
                match value.AutoStart with
                | Some v -> yield "autoStart", JBool v
                | None -> ()
                match value.Repeatable with
                | Some v -> yield "repeatable", JBool v
                | None -> ()
                match value.AvailableSeasons with
                | Some v -> yield "availableSeasons", (Encode.list JString) v
                | None -> ()
                match value.AvailableFromDay with
                | Some v -> yield "availableFromDay", JNumber v
                | None -> ()
                match value.AvailableToDay with
                | Some v -> yield "availableToDay", JNumber v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeQuestObjective (path: Path) (json: Json) : QuestObjective =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vType = ""
        let mutable vDescription = ""
        let mutable vTargetItemId = None
        let mutable vTargetItemQuantity = None
        let mutable vTargetCropType = None
        let mutable vTargetCropQuantity = None
        let mutable vTargetNpcId = None
        let mutable vTargetSceneId = None
        let mutable vCompleted = false
        let mutable vProgress = 0.0
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "type" -> vType <- Decode.string (key :: path) value
            | "description" -> vDescription <- Decode.string (key :: path) value
            | "targetItemId" -> vTargetItemId <- Decode.optional Decode.string (key :: path) value
            | "targetItemQuantity" -> vTargetItemQuantity <- Decode.optional Decode.number (key :: path) value
            | "targetCropType" -> vTargetCropType <- Decode.optional Decode.string (key :: path) value
            | "targetCropQuantity" -> vTargetCropQuantity <- Decode.optional Decode.number (key :: path) value
            | "targetNPCId" -> vTargetNpcId <- Decode.optional Decode.string (key :: path) value
            | "targetSceneId" -> vTargetSceneId <- Decode.optional Decode.string (key :: path) value
            | "completed" -> vCompleted <- Decode.boolean (key :: path) value
            | "progress" -> vProgress <- Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Type = vType; Description = vDescription; TargetItemId = vTargetItemId; TargetItemQuantity = vTargetItemQuantity; TargetCropType = vTargetCropType; TargetCropQuantity = vTargetCropQuantity; TargetNpcId = vTargetNpcId; TargetSceneId = vTargetSceneId; Completed = vCompleted; Progress = vProgress; Extra = List.ofSeq extra }

    and encodeQuestObjective (value: QuestObjective) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "type", JString value.Type
                yield "description", JString value.Description
                match value.TargetItemId with
                | Some v -> yield "targetItemId", JString v
                | None -> ()
                match value.TargetItemQuantity with
                | Some v -> yield "targetItemQuantity", JNumber v
                | None -> ()
                match value.TargetCropType with
                | Some v -> yield "targetCropType", JString v
                | None -> ()
                match value.TargetCropQuantity with
                | Some v -> yield "targetCropQuantity", JNumber v
                | None -> ()
                match value.TargetNpcId with
                | Some v -> yield "targetNPCId", JString v
                | None -> ()
                match value.TargetSceneId with
                | Some v -> yield "targetSceneId", JString v
                | None -> ()
                yield "completed", JBool value.Completed
                yield "progress", JNumber value.Progress
                yield! value.Extra
            ]
        )

    and decodeQuestRewardItem (path: Path) (json: Json) : QuestRewardItem =
        let members = Decode.object path json
        let mutable vItemId = ""
        let mutable vQuantity = 0.0
        for (key, value) in members do
            match key with
            | "itemId" -> vItemId <- Decode.string (key :: path) value
            | "quantity" -> vQuantity <- Decode.number (key :: path) value
            | _ -> ()
        { ItemId = vItemId; Quantity = vQuantity }

    and encodeQuestRewardItem (value: QuestRewardItem) : Json =
        JObject(
            [
                yield "itemId", JString value.ItemId
                yield "quantity", JNumber value.Quantity
            ]
        )

    and decodeQuestRewards (path: Path) (json: Json) : QuestRewards =
        let members = Decode.object path json
        let mutable vMoney = None
        let mutable vItems = None
        let mutable vExperience = None
        let mutable vSkill = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "money" -> vMoney <- Decode.optional Decode.number (key :: path) value
            | "items" -> vItems <- Decode.optional (Decode.list decodeQuestRewardItem) (key :: path) value
            | "experience" -> vExperience <- Decode.optional Decode.number (key :: path) value
            | "skill" -> vSkill <- Decode.optional Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Money = vMoney; Items = vItems; Experience = vExperience; Skill = vSkill; Extra = List.ofSeq extra }

    and encodeQuestRewards (value: QuestRewards) : Json =
        JObject(
            [
                match value.Money with
                | Some v -> yield "money", JNumber v
                | None -> ()
                match value.Items with
                | Some v -> yield "items", (Encode.list encodeQuestRewardItem) v
                | None -> ()
                match value.Experience with
                | Some v -> yield "experience", JNumber v
                | None -> ()
                match value.Skill with
                | Some v -> yield "skill", JString v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeRecipeDefinition (path: Path) (json: Json) : RecipeDefinition =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vInputs = []
        let mutable vOutputs = []
        let mutable vProcessingMinutes = 0.0
        let mutable vMachineTypeId = None
        let mutable vCategory = "crafting"
        let mutable vRequiresStationCategory = None
        let mutable vUnlock = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "inputs" -> vInputs <- (Decode.list decodeRecipeIngredient) (key :: path) value
            | "outputs" -> vOutputs <- (Decode.list decodeRecipeIngredient) (key :: path) value
            | "processingMinutes" -> vProcessingMinutes <- Decode.number (key :: path) value
            | "machineTypeId" -> vMachineTypeId <- Decode.optional Decode.string (key :: path) value
            | "category" -> vCategory <- Decode.string (key :: path) value
            | "requiresStationCategory" -> vRequiresStationCategory <- Decode.optional Decode.string (key :: path) value
            | "unlock" -> vUnlock <- Decode.optional decodeRecipeUnlock (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Inputs = vInputs; Outputs = vOutputs; ProcessingMinutes = vProcessingMinutes; MachineTypeId = vMachineTypeId; Category = vCategory; RequiresStationCategory = vRequiresStationCategory; Unlock = vUnlock; Extra = List.ofSeq extra }

    and encodeRecipeDefinition (value: RecipeDefinition) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "inputs", (Encode.list encodeRecipeIngredient) value.Inputs
                yield "outputs", (Encode.list encodeRecipeIngredient) value.Outputs
                yield "processingMinutes", JNumber value.ProcessingMinutes
                match value.MachineTypeId with
                | Some v -> yield "machineTypeId", JString v
                | None -> ()
                yield "category", JString value.Category
                match value.RequiresStationCategory with
                | Some v -> yield "requiresStationCategory", JString v
                | None -> ()
                match value.Unlock with
                | Some v -> yield "unlock", encodeRecipeUnlock v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeRecipeIngredient (path: Path) (json: Json) : RecipeIngredient =
        let members = Decode.object path json
        let mutable vItemId = ""
        let mutable vQuantity = 0.0
        for (key, value) in members do
            match key with
            | "itemId" -> vItemId <- Decode.string (key :: path) value
            | "quantity" -> vQuantity <- Decode.number (key :: path) value
            | _ -> ()
        { ItemId = vItemId; Quantity = vQuantity }

    and encodeRecipeIngredient (value: RecipeIngredient) : Json =
        JObject(
            [
                yield "itemId", JString value.ItemId
                yield "quantity", JNumber value.Quantity
            ]
        )

    and decodeRecipeSkillRequirement (path: Path) (json: Json) : RecipeSkillRequirement =
        let members = Decode.object path json
        let mutable vSkill = ""
        let mutable vLevel = 0.0
        for (key, value) in members do
            match key with
            | "skill" -> vSkill <- Decode.string (key :: path) value
            | "level" -> vLevel <- Decode.number (key :: path) value
            | _ -> ()
        { Skill = vSkill; Level = vLevel }

    and encodeRecipeSkillRequirement (value: RecipeSkillRequirement) : Json =
        JObject(
            [
                yield "skill", JString value.Skill
                yield "level", JNumber value.Level
            ]
        )

    and decodeRecipeUnlock (path: Path) (json: Json) : RecipeUnlock =
        let members = Decode.object path json
        let mutable vSkill = None
        let mutable vQuestId = None
        let mutable vSeasons = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "skill" -> vSkill <- Decode.optional decodeRecipeSkillRequirement (key :: path) value
            | "questId" -> vQuestId <- Decode.optional Decode.string (key :: path) value
            | "seasons" -> vSeasons <- Decode.optional (Decode.list Decode.string) (key :: path) value
            | _ -> extra.Add((key, value))
        { Skill = vSkill; QuestId = vQuestId; Seasons = vSeasons; Extra = List.ofSeq extra }

    and encodeRecipeUnlock (value: RecipeUnlock) : Json =
        JObject(
            [
                match value.Skill with
                | Some v -> yield "skill", encodeRecipeSkillRequirement v
                | None -> ()
                match value.QuestId with
                | Some v -> yield "questId", JString v
                | None -> ()
                match value.Seasons with
                | Some v -> yield "seasons", (Encode.list JString) v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeRngState (path: Path) (json: Json) : RngState =
        let members = Decode.object path json
        let mutable vAlgorithm = "xoshiro128ss"
        let mutable vS = [ 0u; 0u; 0u; 0u ]
        for (key, value) in members do
            match key with
            // zod checks the literal itself, so any value reaches the "Invalid literal" check.
            | "algorithm" -> vAlgorithm <- (match value with JString s -> s | _ -> "")
            | "s" -> vS <- (Decode.list Decode.uint32) (key :: path) value
            | _ -> ()
        { Algorithm = vAlgorithm; S = vS }

    and encodeRngState (value: RngState) : Json =
        JObject(
            [
                yield "algorithm", JString value.Algorithm
                yield "s", (Encode.list (float >> JNumber)) value.S
            ]
        )

    and decodeScene (path: Path) (json: Json) : Scene =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vWidth = 0.0
        let mutable vHeight = 0.0
        let mutable vTiles = []
        let mutable vTransitions = []
        let mutable vNpcs = []
        let mutable vEvents = []
        let mutable vIndoor = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "width" -> vWidth <- Decode.number (key :: path) value
            | "height" -> vHeight <- Decode.number (key :: path) value
            | "tiles" -> vTiles <- (Decode.list (Decode.list decodeTile)) (key :: path) value
            | "transitions" -> vTransitions <- (Decode.list decodeSceneTransition) (key :: path) value
            | "npcs" -> vNpcs <- (Decode.list Decode.string) (key :: path) value
            | "events" -> vEvents <- (Decode.list Decode.string) (key :: path) value
            | "indoor" -> vIndoor <- Decode.optional Decode.boolean (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Width = vWidth; Height = vHeight; Tiles = vTiles; Transitions = vTransitions; Npcs = vNpcs; Events = vEvents; Indoor = vIndoor; Extra = List.ofSeq extra }

    and encodeScene (value: Scene) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "width", JNumber value.Width
                yield "height", JNumber value.Height
                yield "tiles", (Encode.list (Encode.list encodeTile)) value.Tiles
                yield "transitions", (Encode.list encodeSceneTransition) value.Transitions
                yield "npcs", (Encode.list JString) value.Npcs
                yield "events", (Encode.list JString) value.Events
                match value.Indoor with
                | Some v -> yield "indoor", JBool v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeSceneTransition (path: Path) (json: Json) : SceneTransition =
        let members = Decode.object path json
        let mutable vFromX = 0.0
        let mutable vFromY = 0.0
        let mutable vToSceneId = ""
        let mutable vToX = 0.0
        let mutable vToY = 0.0
        let mutable vLocked = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "fromX" -> vFromX <- Decode.number (key :: path) value
            | "fromY" -> vFromY <- Decode.number (key :: path) value
            | "toSceneId" -> vToSceneId <- Decode.string (key :: path) value
            | "toX" -> vToX <- Decode.number (key :: path) value
            | "toY" -> vToY <- Decode.number (key :: path) value
            | "locked" -> vLocked <- Decode.optional Decode.boolean (key :: path) value
            | _ -> extra.Add((key, value))
        { FromX = vFromX; FromY = vFromY; ToSceneId = vToSceneId; ToX = vToX; ToY = vToY; Locked = vLocked; Extra = List.ofSeq extra }

    and encodeSceneTransition (value: SceneTransition) : Json =
        JObject(
            [
                yield "fromX", JNumber value.FromX
                yield "fromY", JNumber value.FromY
                yield "toSceneId", JString value.ToSceneId
                yield "toX", JNumber value.ToX
                yield "toY", JNumber value.ToY
                match value.Locked with
                | Some v -> yield "locked", JBool v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeShopDefinition (path: Path) (json: Json) : ShopDefinition =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vStock = []
        let mutable vSellPriceMultiplier = 1.0
        let mutable vBuysItems = true
        let mutable vRepairsTools = false
        let mutable vRepairCostPerPoint = 0.5
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "stock" -> vStock <- (Decode.list decodeShopStockEntry) (key :: path) value
            | "sellPriceMultiplier" -> vSellPriceMultiplier <- Decode.number (key :: path) value
            | "buysItems" -> vBuysItems <- Decode.boolean (key :: path) value
            | "repairsTools" -> vRepairsTools <- Decode.boolean (key :: path) value
            | "repairCostPerPoint" -> vRepairCostPerPoint <- Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; Stock = vStock; SellPriceMultiplier = vSellPriceMultiplier; BuysItems = vBuysItems; RepairsTools = vRepairsTools; RepairCostPerPoint = vRepairCostPerPoint; Extra = List.ofSeq extra }

    and encodeShopDefinition (value: ShopDefinition) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "stock", (Encode.list encodeShopStockEntry) value.Stock
                yield "sellPriceMultiplier", JNumber value.SellPriceMultiplier
                yield "buysItems", JBool value.BuysItems
                yield "repairsTools", JBool value.RepairsTools
                yield "repairCostPerPoint", JNumber value.RepairCostPerPoint
                yield! value.Extra
            ]
        )

    and decodeShopStockEntry (path: Path) (json: Json) : ShopStockEntry =
        let members = Decode.object path json
        let mutable vItemId = ""
        let mutable vPrice = None
        let mutable vSeasons = None
        let mutable vDailyLimit = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "itemId" -> vItemId <- Decode.string (key :: path) value
            | "price" -> vPrice <- Decode.optional Decode.number (key :: path) value
            | "seasons" -> vSeasons <- Decode.optional (Decode.list Decode.string) (key :: path) value
            | "dailyLimit" -> vDailyLimit <- Decode.optional Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { ItemId = vItemId; Price = vPrice; Seasons = vSeasons; DailyLimit = vDailyLimit; Extra = List.ofSeq extra }

    and encodeShopStockEntry (value: ShopStockEntry) : Json =
        JObject(
            [
                yield "itemId", JString value.ItemId
                match value.Price with
                | Some v -> yield "price", JNumber v
                | None -> ()
                match value.Seasons with
                | Some v -> yield "seasons", (Encode.list JString) v
                | None -> ()
                match value.DailyLimit with
                | Some v -> yield "dailyLimit", JNumber v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeSkillState (path: Path) (json: Json) : SkillState =
        let members = Decode.object path json
        let mutable vXp = 0.0
        let mutable vLevel = 0.0
        for (key, value) in members do
            match key with
            | "xp" -> vXp <- Decode.number (key :: path) value
            | "level" -> vLevel <- Decode.number (key :: path) value
            | _ -> ()
        { Xp = vXp; Level = vLevel }

    and encodeSkillState (value: SkillState) : Json =
        JObject(
            [
                yield "xp", JNumber value.Xp
                yield "level", JNumber value.Level
            ]
        )

    and decodeSpriteSheet (path: Path) (json: Json) : SpriteSheet =
        let members = Decode.object path json
        let mutable vFrameWidth = 0.0
        let mutable vFrameHeight = 0.0
        let mutable vFrames = 0.0
        let mutable vTicksPerFrame = 6.0
        let mutable vDirectional = true
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "frameWidth" -> vFrameWidth <- Decode.number (key :: path) value
            | "frameHeight" -> vFrameHeight <- Decode.number (key :: path) value
            | "frames" -> vFrames <- Decode.number (key :: path) value
            | "ticksPerFrame" -> vTicksPerFrame <- Decode.number (key :: path) value
            | "directional" -> vDirectional <- Decode.boolean (key :: path) value
            | _ -> extra.Add((key, value))
        { FrameWidth = vFrameWidth; FrameHeight = vFrameHeight; Frames = vFrames; TicksPerFrame = vTicksPerFrame; Directional = vDirectional; Extra = List.ofSeq extra }

    and encodeSpriteSheet (value: SpriteSheet) : Json =
        JObject(
            [
                yield "frameWidth", JNumber value.FrameWidth
                yield "frameHeight", JNumber value.FrameHeight
                yield "frames", JNumber value.Frames
                yield "ticksPerFrame", JNumber value.TicksPerFrame
                yield "directional", JBool value.Directional
                yield! value.Extra
            ]
        )

    and decodeTile (path: Path) (json: Json) : Tile =
        let members = Decode.object path json
        let mutable vX = 0.0
        let mutable vY = 0.0
        let mutable vType = ""
        let mutable vBackground = ""
        let mutable vOverlay = None
        let mutable vObject = None
        let mutable vCrop = None
        let mutable vItem = None
        let mutable vNode = None
        let mutable vMachine = None
        let mutable vLadderDown = None
        let mutable vCollision = false
        let mutable vCustomImage = None
        let mutable vVisuals = None
        let mutable vSoilState = None
        let mutable vSoilMoisture = 0.0
        let mutable vSoilFertility = 0.0
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "x" -> vX <- Decode.number (key :: path) value
            | "y" -> vY <- Decode.number (key :: path) value
            | "type" -> vType <- Decode.string (key :: path) value
            | "background" -> vBackground <- Decode.string (key :: path) value
            | "overlay" -> vOverlay <- Decode.optional Decode.string (key :: path) value
            | "object" -> vObject <- Decode.optional Decode.string (key :: path) value
            | "crop" -> vCrop <- Decode.optional decodeCrop (key :: path) value
            | "item" -> vItem <- Decode.optional decodeItem (key :: path) value
            | "node" -> vNode <- Decode.optional decodeTileNode (key :: path) value
            | "machine" -> vMachine <- Decode.optional decodeTileMachine (key :: path) value
            | "ladderDown" -> vLadderDown <- Decode.optional Decode.boolean (key :: path) value
            | "collision" -> vCollision <- Decode.boolean (key :: path) value
            | "customImage" -> vCustomImage <- Decode.optional Decode.string (key :: path) value
            | "visuals" -> vVisuals <- Decode.optional decodeTileVisuals (key :: path) value
            | "soilState" -> vSoilState <- Decode.optional Decode.string (key :: path) value
            | "soilMoisture" -> vSoilMoisture <- Decode.number (key :: path) value
            | "soilFertility" -> vSoilFertility <- Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { X = vX; Y = vY; Type = vType; Background = vBackground; Overlay = vOverlay; Object = vObject; Crop = vCrop; Item = vItem; Node = vNode; Machine = vMachine; LadderDown = vLadderDown; Collision = vCollision; CustomImage = vCustomImage; Visuals = vVisuals; SoilState = vSoilState; SoilMoisture = vSoilMoisture; SoilFertility = vSoilFertility; Extra = List.ofSeq extra }

    and encodeTile (value: Tile) : Json =
        JObject(
            [
                yield "x", JNumber value.X
                yield "y", JNumber value.Y
                yield "type", JString value.Type
                yield "background", JString value.Background
                match value.Overlay with
                | Some v -> yield "overlay", JString v
                | None -> yield "overlay", JNull
                match value.Object with
                | Some v -> yield "object", JString v
                | None -> yield "object", JNull
                match value.Crop with
                | Some v -> yield "crop", encodeCrop v
                | None -> ()
                match value.Item with
                | Some v -> yield "item", encodeItem v
                | None -> ()
                match value.Node with
                | Some v -> yield "node", encodeTileNode v
                | None -> ()
                match value.Machine with
                | Some v -> yield "machine", encodeTileMachine v
                | None -> ()
                match value.LadderDown with
                | Some v -> yield "ladderDown", JBool v
                | None -> ()
                yield "collision", JBool value.Collision
                match value.CustomImage with
                | Some v -> yield "customImage", JString v
                | None -> ()
                match value.Visuals with
                | Some v -> yield "visuals", encodeTileVisuals v
                | None -> ()
                match value.SoilState with
                | Some v -> yield "soilState", JString v
                | None -> ()
                yield "soilMoisture", JNumber value.SoilMoisture
                yield "soilFertility", JNumber value.SoilFertility
                yield! value.Extra
            ]
        )

    and decodeTileMachine (path: Path) (json: Json) : TileMachine =
        let members = Decode.object path json
        let mutable vTypeId = ""
        let mutable vProcessing = None
        let mutable vOutput = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "typeId" -> vTypeId <- Decode.string (key :: path) value
            | "processing" -> vProcessing <- Decode.optional decodeMachineProcessing (key :: path) value
            | "output" -> vOutput <- Decode.optional (Decode.list decodeRecipeIngredient) (key :: path) value
            | _ -> extra.Add((key, value))
        { TypeId = vTypeId; Processing = vProcessing; Output = vOutput; Extra = List.ofSeq extra }

    and encodeTileMachine (value: TileMachine) : Json =
        JObject(
            [
                yield "typeId", JString value.TypeId
                match value.Processing with
                | Some v -> yield "processing", encodeMachineProcessing v
                | None -> ()
                match value.Output with
                | Some v -> yield "output", (Encode.list encodeRecipeIngredient) v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeTileNode (path: Path) (json: Json) : TileNode =
        let members = Decode.object path json
        let mutable vTypeId = ""
        let mutable vRemainingHealth = 0.0
        let mutable vDepletedOnDay = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "typeId" -> vTypeId <- Decode.string (key :: path) value
            | "remainingHealth" -> vRemainingHealth <- Decode.number (key :: path) value
            | "depletedOnDay" -> vDepletedOnDay <- Decode.optional Decode.number (key :: path) value
            | _ -> extra.Add((key, value))
        { TypeId = vTypeId; RemainingHealth = vRemainingHealth; DepletedOnDay = vDepletedOnDay; Extra = List.ofSeq extra }

    and encodeTileNode (value: TileNode) : Json =
        JObject(
            [
                yield "typeId", JString value.TypeId
                yield "remainingHealth", JNumber value.RemainingHealth
                match value.DepletedOnDay with
                | Some v -> yield "depletedOnDay", JNumber v
                | None -> ()
                yield! value.Extra
            ]
        )

    and decodeTileVisuals (path: Path) (json: Json) : TileVisuals =
        let members = Decode.object path json
        let mutable vBackground = None
        let mutable vOverlay = None
        let mutable vObject = None
        for (key, value) in members do
            match key with
            | "background" -> vBackground <- Decode.optional decodeVisualRef (key :: path) value
            | "overlay" -> vOverlay <- Decode.optional decodeVisualRef (key :: path) value
            | "object" -> vObject <- Decode.optional decodeVisualRef (key :: path) value
            | _ -> ()
        { Background = vBackground; Overlay = vOverlay; Object = vObject }

    and encodeTileVisuals (value: TileVisuals) : Json =
        JObject(
            [
                match value.Background with
                | Some v -> yield "background", encodeVisualRef v
                | None -> ()
                match value.Overlay with
                | Some v -> yield "overlay", encodeVisualRef v
                | None -> ()
                match value.Object with
                | Some v -> yield "object", encodeVisualRef v
                | None -> ()
            ]
        )

    and decodeTimeConfig (path: Path) (json: Json) : TimeConfig =
        let members = Decode.object path json
        let mutable vDayStartMinute = 360.0
        let mutable vDayEndMinute = 1560.0
        let mutable vMinutesPerRealSecond = 1.0
        let mutable vPauseInModals = None
        for (key, value) in members do
            match key with
            | "dayStartMinute" -> vDayStartMinute <- Decode.number (key :: path) value
            | "dayEndMinute" -> vDayEndMinute <- Decode.number (key :: path) value
            | "minutesPerRealSecond" -> vMinutesPerRealSecond <- Decode.number (key :: path) value
            | "pauseInModals" -> vPauseInModals <- Decode.optional Decode.boolean (key :: path) value
            | _ -> ()
        { DayStartMinute = vDayStartMinute; DayEndMinute = vDayEndMinute; MinutesPerRealSecond = vMinutesPerRealSecond; PauseInModals = vPauseInModals }

    and encodeTimeConfig (value: TimeConfig) : Json =
        JObject(
            [
                yield "dayStartMinute", JNumber value.DayStartMinute
                yield "dayEndMinute", JNumber value.DayEndMinute
                yield "minutesPerRealSecond", JNumber value.MinutesPerRealSecond
                match value.PauseInModals with
                | Some v -> yield "pauseInModals", JBool v
                | None -> ()
            ]
        )

    and decodeVisualRef (path: Path) (json: Json) : VisualRef =
        let members = Decode.object path json
        let mutable vAssetId = ""
        let mutable vAnimation = None
        let mutable vFrame = None
        for (key, value) in members do
            match key with
            | "assetId" -> vAssetId <- Decode.string (key :: path) value
            | "animation" -> vAnimation <- Decode.optional Decode.string (key :: path) value
            | "frame" -> vFrame <- Decode.optional decodeArtFrame (key :: path) value
            | _ -> ()
        { AssetId = vAssetId; Animation = vAnimation; Frame = vFrame }

    and encodeVisualRef (value: VisualRef) : Json =
        JObject(
            [
                yield "assetId", JString value.AssetId
                match value.Animation with
                | Some v -> yield "animation", JString v
                | None -> ()
                match value.Frame with
                | Some v -> yield "frame", encodeArtFrame v
                | None -> ()
            ]
        )

    and decodeWeatherConfig (path: Path) (json: Json) : WeatherConfig =
        let members = Decode.object path json
        let mutable vTypes = []
        let mutable vTable = []
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "types" -> vTypes <- (Decode.list decodeWeatherTypeDefinition) (key :: path) value
            | "table" -> vTable <- (Decode.dict (Decode.list decodeWeatherTableEntry)) (key :: path) value
            | _ -> extra.Add((key, value))
        { Types = vTypes; Table = vTable; Extra = List.ofSeq extra }

    and encodeWeatherConfig (value: WeatherConfig) : Json =
        JObject(
            [
                yield "types", (Encode.list encodeWeatherTypeDefinition) value.Types
                yield "table", (Encode.dict (Encode.list encodeWeatherTableEntry)) value.Table
                yield! value.Extra
            ]
        )

    and decodeWeatherTableEntry (path: Path) (json: Json) : WeatherTableEntry =
        let members = Decode.object path json
        let mutable vWeatherId = ""
        let mutable vWeight = 0.0
        for (key, value) in members do
            match key with
            | "weatherId" -> vWeatherId <- Decode.string (key :: path) value
            | "weight" -> vWeight <- Decode.number (key :: path) value
            | _ -> ()
        { WeatherId = vWeatherId; Weight = vWeight }

    and encodeWeatherTableEntry (value: WeatherTableEntry) : Json =
        JObject(
            [
                yield "weatherId", JString value.WeatherId
                yield "weight", JNumber value.Weight
            ]
        )

    and decodeWeatherTypeDefinition (path: Path) (json: Json) : WeatherTypeDefinition =
        let members = Decode.object path json
        let mutable vId = ""
        let mutable vName = ""
        let mutable vWatersOutdoorSoil = false
        let mutable vCropDamageChance = 0.0
        let mutable vNpcsStayInside = false
        let mutable vOverlay = None
        let extra = ResizeArray<string * Json>()
        for (key, value) in members do
            match key with
            | "id" -> vId <- Decode.string (key :: path) value
            | "name" -> vName <- Decode.string (key :: path) value
            | "watersOutdoorSoil" -> vWatersOutdoorSoil <- Decode.boolean (key :: path) value
            | "cropDamageChance" -> vCropDamageChance <- Decode.number (key :: path) value
            | "npcsStayInside" -> vNpcsStayInside <- Decode.boolean (key :: path) value
            | "overlay" -> vOverlay <- Decode.optional Decode.string (key :: path) value
            | _ -> extra.Add((key, value))
        { Id = vId; Name = vName; WatersOutdoorSoil = vWatersOutdoorSoil; CropDamageChance = vCropDamageChance; NpcsStayInside = vNpcsStayInside; Overlay = vOverlay; Extra = List.ofSeq extra }

    and encodeWeatherTypeDefinition (value: WeatherTypeDefinition) : Json =
        JObject(
            [
                yield "id", JString value.Id
                yield "name", JString value.Name
                yield "watersOutdoorSoil", JBool value.WatersOutdoorSoil
                yield "cropDamageChance", JNumber value.CropDamageChance
                yield "npcsStayInside", JBool value.NpcsStayInside
                match value.Overlay with
                | Some v -> yield "overlay", JString v
                | None -> yield "overlay", JNull
                yield! value.Extra
            ]
        )

    and decodeEnterTileCondition (path: Path) (json: Json) : EnterTileCondition =
        let members = Decode.object path json
        let mutable vX = 0.0
        let mutable vY = 0.0
        let mutable vX2 = None
        let mutable vY2 = None
        for (key, value) in members do
            match key with
            | "x" -> vX <- Decode.number (key :: path) value
            | "y" -> vY <- Decode.number (key :: path) value
            | "x2" -> vX2 <- Decode.optional Decode.number (key :: path) value
            | "y2" -> vY2 <- Decode.optional Decode.number (key :: path) value
            | "type" -> ()
            | _ -> ()
        { X = vX; Y = vY; X2 = vX2; Y2 = vY2 }

    and encodeEnterTileCondition (value: EnterTileCondition) : Json =
        JObject(
            [
                yield "type", JString "enterTile"
                yield "x", JNumber value.X
                yield "y", JNumber value.Y
                match value.X2 with
                | Some v -> yield "x2", JNumber v
                | None -> ()
                match value.Y2 with
                | Some v -> yield "y2", JNumber v
                | None -> ()
            ]
        )

    and decodeInteractTileCondition (path: Path) (json: Json) : InteractTileCondition =
        let members = Decode.object path json
        let mutable vX = 0.0
        let mutable vY = 0.0
        let mutable vX2 = None
        let mutable vY2 = None
        for (key, value) in members do
            match key with
            | "x" -> vX <- Decode.number (key :: path) value
            | "y" -> vY <- Decode.number (key :: path) value
            | "x2" -> vX2 <- Decode.optional Decode.number (key :: path) value
            | "y2" -> vY2 <- Decode.optional Decode.number (key :: path) value
            | "type" -> ()
            | _ -> ()
        { X = vX; Y = vY; X2 = vX2; Y2 = vY2 }

    and encodeInteractTileCondition (value: InteractTileCondition) : Json =
        JObject(
            [
                yield "type", JString "interactTile"
                yield "x", JNumber value.X
                yield "y", JNumber value.Y
                match value.X2 with
                | Some v -> yield "x2", JNumber v
                | None -> ()
                match value.Y2 with
                | Some v -> yield "y2", JNumber v
                | None -> ()
            ]
        )

    and decodeHasItemCondition (path: Path) (json: Json) : HasItemCondition =
        let members = Decode.object path json
        let mutable vItemId = ""
        let mutable vQuantity = 1.0
        for (key, value) in members do
            match key with
            | "itemId" -> vItemId <- Decode.string (key :: path) value
            | "quantity" -> vQuantity <- Decode.number (key :: path) value
            | "type" -> ()
            | _ -> ()
        { ItemId = vItemId; Quantity = vQuantity }

    and encodeHasItemCondition (value: HasItemCondition) : Json =
        JObject(
            [
                yield "type", JString "hasItem"
                yield "itemId", JString value.ItemId
                yield "quantity", JNumber value.Quantity
            ]
        )

    and decodeInventorySpaceCondition (path: Path) (json: Json) : InventorySpaceCondition =
        let members = Decode.object path json
        let mutable vItemId = ""
        let mutable vQuantity = 1.0
        for (key, value) in members do
            match key with
            | "itemId" -> vItemId <- Decode.string (key :: path) value
            | "quantity" -> vQuantity <- Decode.number (key :: path) value
            | "type" -> ()
            | _ -> ()
        { ItemId = vItemId; Quantity = vQuantity }

    and encodeInventorySpaceCondition (value: InventorySpaceCondition) : Json =
        JObject(
            [
                yield "type", JString "inventorySpace"
                yield "itemId", JString value.ItemId
                yield "quantity", JNumber value.Quantity
            ]
        )

    and decodeFlagCondition (path: Path) (json: Json) : FlagCondition =
        let members = Decode.object path json
        let mutable vFlag = ""
        let mutable vValue = true
        for (key, value) in members do
            match key with
            | "flag" -> vFlag <- Decode.string (key :: path) value
            | "value" -> vValue <- Decode.boolean (key :: path) value
            | "type" -> ()
            | _ -> ()
        { Flag = vFlag; Value = vValue }

    and encodeFlagCondition (value: FlagCondition) : Json =
        JObject(
            [
                yield "type", JString "flag"
                yield "flag", JString value.Flag
                yield "value", JBool value.Value
            ]
        )

    and decodeDayRangeCondition (path: Path) (json: Json) : DayRangeCondition =
        let members = Decode.object path json
        let mutable vMinDay = None
        let mutable vMaxDay = None
        for (key, value) in members do
            match key with
            | "minDay" -> vMinDay <- Decode.optional Decode.number (key :: path) value
            | "maxDay" -> vMaxDay <- Decode.optional Decode.number (key :: path) value
            | "type" -> ()
            | _ -> ()
        { MinDay = vMinDay; MaxDay = vMaxDay }

    and encodeDayRangeCondition (value: DayRangeCondition) : Json =
        JObject(
            [
                yield "type", JString "dayRange"
                match value.MinDay with
                | Some v -> yield "minDay", JNumber v
                | None -> ()
                match value.MaxDay with
                | Some v -> yield "maxDay", JNumber v
                | None -> ()
            ]
        )

    and decodeSeasonCondition (path: Path) (json: Json) : SeasonCondition =
        let members = Decode.object path json
        let mutable vSeasons = []
        for (key, value) in members do
            match key with
            | "seasons" -> vSeasons <- (Decode.list Decode.string) (key :: path) value
            | "type" -> ()
            | _ -> ()
        { Seasons = vSeasons }

    and encodeSeasonCondition (value: SeasonCondition) : Json =
        JObject(
            [
                yield "type", JString "season"
                yield "seasons", (Encode.list JString) value.Seasons
            ]
        )

    and decodeYearRangeCondition (path: Path) (json: Json) : YearRangeCondition =
        let members = Decode.object path json
        let mutable vMinYear = None
        let mutable vMaxYear = None
        for (key, value) in members do
            match key with
            | "minYear" -> vMinYear <- Decode.optional Decode.number (key :: path) value
            | "maxYear" -> vMaxYear <- Decode.optional Decode.number (key :: path) value
            | "type" -> ()
            | _ -> ()
        { MinYear = vMinYear; MaxYear = vMaxYear }

    and encodeYearRangeCondition (value: YearRangeCondition) : Json =
        JObject(
            [
                yield "type", JString "yearRange"
                match value.MinYear with
                | Some v -> yield "minYear", JNumber v
                | None -> ()
                match value.MaxYear with
                | Some v -> yield "maxYear", JNumber v
                | None -> ()
            ]
        )

    and decodeTimeOfDayCondition (path: Path) (json: Json) : TimeOfDayCondition =
        let members = Decode.object path json
        let mutable vMinMinute = 0.0
        let mutable vMaxMinute = 0.0
        for (key, value) in members do
            match key with
            | "minMinute" -> vMinMinute <- Decode.number (key :: path) value
            | "maxMinute" -> vMaxMinute <- Decode.number (key :: path) value
            | "type" -> ()
            | _ -> ()
        { MinMinute = vMinMinute; MaxMinute = vMaxMinute }

    and encodeTimeOfDayCondition (value: TimeOfDayCondition) : Json =
        JObject(
            [
                yield "type", JString "timeOfDay"
                yield "minMinute", JNumber value.MinMinute
                yield "maxMinute", JNumber value.MaxMinute
            ]
        )

    and decodeQuestStatusCondition (path: Path) (json: Json) : QuestStatusCondition =
        let members = Decode.object path json
        let mutable vQuestId = ""
        let mutable vStatus = ""
        for (key, value) in members do
            match key with
            | "questId" -> vQuestId <- Decode.string (key :: path) value
            | "status" -> vStatus <- Decode.string (key :: path) value
            | "type" -> ()
            | _ -> ()
        { QuestId = vQuestId; Status = vStatus }

    and encodeQuestStatusCondition (value: QuestStatusCondition) : Json =
        JObject(
            [
                yield "type", JString "questStatus"
                yield "questId", JString value.QuestId
                yield "status", JString value.Status
            ]
        )

    and decodeFriendshipCondition (path: Path) (json: Json) : FriendshipCondition =
        let members = Decode.object path json
        let mutable vNpcId = ""
        let mutable vMin = 0.0
        for (key, value) in members do
            match key with
            | "npcId" -> vNpcId <- Decode.string (key :: path) value
            | "min" -> vMin <- Decode.number (key :: path) value
            | "type" -> ()
            | _ -> ()
        { NpcId = vNpcId; Min = vMin }

    and encodeFriendshipCondition (value: FriendshipCondition) : Json =
        JObject(
            [
                yield "type", JString "friendship"
                yield "npcId", JString value.NpcId
                yield "min", JNumber value.Min
            ]
        )

    and decodeWeatherCondition (path: Path) (json: Json) : WeatherCondition =
        let members = Decode.object path json
        let mutable vWeatherIds = []
        for (key, value) in members do
            match key with
            | "weatherIds" -> vWeatherIds <- (Decode.list Decode.string) (key :: path) value
            | "type" -> ()
            | _ -> ()
        { WeatherIds = vWeatherIds }

    and encodeWeatherCondition (value: WeatherCondition) : Json =
        JObject(
            [
                yield "type", JString "weather"
                yield "weatherIds", (Encode.list JString) value.WeatherIds
            ]
        )

    and decodeFestivalIdCondition (path: Path) (json: Json) : FestivalIdCondition =
        let members = Decode.object path json
        let mutable vFestivalId = ""
        for (key, value) in members do
            match key with
            | "festivalId" -> vFestivalId <- Decode.string (key :: path) value
            | "type" -> ()
            | _ -> ()
        { FestivalId = vFestivalId }

    and encodeFestivalIdCondition (value: FestivalIdCondition) : Json =
        JObject(
            [
                yield "type", JString "festivalId"
                yield "festivalId", JString value.FestivalId
            ]
        )

    and decodeEventCondition (path: Path) (json: Json) : EventCondition =
        match Decode.discriminator path json with
        | "enterTile" -> EventCondition.EnterTile(decodeEnterTileCondition path json)
        | "interactTile" -> EventCondition.InteractTile(decodeInteractTileCondition path json)
        | "hasItem" -> EventCondition.HasItem(decodeHasItemCondition path json)
        | "inventorySpace" -> EventCondition.InventorySpace(decodeInventorySpaceCondition path json)
        | "flag" -> EventCondition.Flag(decodeFlagCondition path json)
        | "dayRange" -> EventCondition.DayRange(decodeDayRangeCondition path json)
        | "season" -> EventCondition.Season(decodeSeasonCondition path json)
        | "yearRange" -> EventCondition.YearRange(decodeYearRangeCondition path json)
        | "timeOfDay" -> EventCondition.TimeOfDay(decodeTimeOfDayCondition path json)
        | "questStatus" -> EventCondition.QuestStatus(decodeQuestStatusCondition path json)
        | "friendship" -> EventCondition.Friendship(decodeFriendshipCondition path json)
        | "weather" -> EventCondition.Weather(decodeWeatherCondition path json)
        | "festivalId" -> EventCondition.FestivalId(decodeFestivalIdCondition path json)
        | other -> Decode.unknownDiscriminator path other

    and encodeEventCondition (value: EventCondition) : Json =
        match value with
        | EventCondition.EnterTile c -> encodeEnterTileCondition c
        | EventCondition.InteractTile c -> encodeInteractTileCondition c
        | EventCondition.HasItem c -> encodeHasItemCondition c
        | EventCondition.InventorySpace c -> encodeInventorySpaceCondition c
        | EventCondition.Flag c -> encodeFlagCondition c
        | EventCondition.DayRange c -> encodeDayRangeCondition c
        | EventCondition.Season c -> encodeSeasonCondition c
        | EventCondition.YearRange c -> encodeYearRangeCondition c
        | EventCondition.TimeOfDay c -> encodeTimeOfDayCondition c
        | EventCondition.QuestStatus c -> encodeQuestStatusCondition c
        | EventCondition.Friendship c -> encodeFriendshipCondition c
        | EventCondition.Weather c -> encodeWeatherCondition c
        | EventCondition.FestivalId c -> encodeFestivalIdCondition c
