namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// Editor-level checks on content: duplicate ids, items, crops, quests, the event/action
/// vocabulary (event-forms.tsx), shops, recipes and machines, gathering nodes, wildlife,
/// artwork (validate-graphics.ts) and packs. Ports `validate-extensibility.ts` and
/// `validate-graphics.ts` and adds the checks the components make before saving.
module internal ChecksContent =
    let private hasValue = Context.hasValue

    let private missing (set: HashSet<string>) (id: string | null) =
        match id with
        | null -> false
        | value -> value.Length > 0 && not (set.Contains value)

    let private duplicates (context: Context) (sink: Sink) =
        let check (family: string) (path: string) (ids: seq<string>) (target: string -> NavigationTarget option) =
            let seen = HashSet<string>()
            ids
            |> Seq.iteri (fun i id ->
                if id.Length > 0 && not (seen.Add id) then
                    sink.Error(sprintf "duplicate.%s" family, sprintf "%s[%d].id" path i, sprintf "Duplicate %s id \"%s\"" family id, target id))
        let p = context.Project
        check "item" "items" (p.Items |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Item id))
        check "npc" "npcs" (p.Npcs |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Npc id))
        check "quest" "quests" (p.Quests |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Quest id))
        check "event" "events" (p.Events |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Event id))
        check "dialogue" "dialogues" (p.Dialogues |> Seq.map (fun x -> x.Id)) (fun _ -> None)
        check "shop" "shops" (p.Shops |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Shop id))
        check "recipe" "recipes" (p.Recipes |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Recipe id))
        check "nodeType" "nodeTypes" (p.NodeTypes |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.NodeType id))
        check "machineType" "machineTypes" (p.MachineTypes |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.MachineType id))
        check "animalSpecies" "animalSpecies" (p.AnimalSpecies |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.AnimalSpecies id))
        check "animal" "animals" (p.Animals |> Seq.map (fun x -> x.Id)) (fun _ -> None)
        check "fishTable" "fishTables" (p.FishTables |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.FishTable id))
        check "action" "actions" (p.Actions |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Action id))
        check "minigame" "minigames" (p.Minigames |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Minigame id))
        check "asset" "customAssets" (p.CustomAssets |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Asset id))
        match p.CustomCrops with
        | null -> ()
        | crops -> check "crop" "customCrops" (crops |> Seq.map (fun x -> x.Id)) (fun id -> Some(NavigationTarget.Crop id))

    let private items (context: Context) (sink: Sink) =
        context.Project.Items
        |> Seq.iteri (fun i item ->
            let path = sprintf "items[%d]" i
            let target = Some(NavigationTarget.Item item.Id)
            if System.String.IsNullOrWhiteSpace item.Name then
                sink.Warning("item.emptyName", path + ".name", sprintf "Item \"%s\" has no name" item.Id, target)
            if item.Type = ItemTypes.Seed && not (hasValue item.CropType) then
                sink.Warning("item.seedWithoutCrop", path + ".cropType", sprintf "Seed \"%s\" does not say which crop it grows" item.Name, target)
            if item.Type = ItemTypes.Tool && not (hasValue item.ToolType) then
                sink.Warning("item.toolWithoutType", path + ".toolType", sprintf "Tool \"%s\" has no tool type, so it cannot be used" item.Name, target)
            if item.Type <> ItemTypes.Seed && missing context.CropIds item.CropType then
                sink.Warning("item.unknownCrop", path + ".cropType", sprintf "Item \"%s\" belongs to missing crop \"%s\"" item.Name item.CropType, target)
            // validate-extensibility.ts: "on use" action must exist.
            if missing context.ActionIds item.UseActionId then
                sink.Error("item.useActionMissing", path + ".useActionId", sprintf "Item \"%s\" uses missing action \"%s\"" item.Name item.UseActionId, target))

    let private crops (context: Context) (sink: Sink) =
        match context.Project.CustomCrops with
        | null -> ()
        | crops ->
            let seedsFor = HashSet<string>(context.Project.Items |> Seq.filter (fun i -> i.Type = ItemTypes.Seed) |> Seq.choose (fun i -> Option.ofObj i.CropType))
            crops
            |> Seq.iteri (fun i crop ->
                let path = sprintf "customCrops[%d]" i
                let target = Some(NavigationTarget.Crop crop.Id)
                // CropEditor handleSaveCrop rules.
                if System.String.IsNullOrWhiteSpace crop.Name then
                    sink.Error("crop.emptyName", path + ".name", sprintf "Crop \"%s\" has no name" crop.Id, target)
                if not crop.GrowthDays.HasValue || crop.GrowthDays.Value <= 0.0 then
                    sink.Error("crop.growthDays", path + ".growthDays", sprintf "Crop \"%s\" needs at least 1 growth day" crop.Name, target)
                if crop.Stages < 2.0 then
                    sink.Error("crop.stagesTooFew", path + ".stages", sprintf "Crop \"%s\" needs at least 2 stages" crop.Name, target)
                if crop.Seasons.Count = 0 then
                    sink.Error("crop.noSeasons", path + ".seasons", sprintf "Crop \"%s\" grows in no season" crop.Name, target)
                crop.Seasons
                |> Seq.iteri (fun k season ->
                    if not (context.SeasonIds.Contains season) then
                        sink.Warning("crop.unknownSeason", sprintf "%s.seasons[%d]" path k, sprintf "Crop \"%s\" grows in \"%s\", which is not in the calendar" crop.Name season, target))
                if not (seedsFor.Contains crop.Id) then
                    sink.Warning("crop.noSeedItem", path, sprintf "Crop \"%s\" has no seed item, so nobody can plant it" crop.Name, target))

    let private quests (context: Context) (sink: Sink) =
        context.Project.Quests
        |> Seq.iteri (fun i quest ->
            let path = sprintf "quests[%d]" i
            let target = Some(NavigationTarget.Quest quest.Id)
            if quest.Objectives.Count = 0 then
                sink.Warning("quest.noObjectives", path + ".objectives", sprintf "Quest \"%s\" has no objectives, so it completes at once" quest.Name, target)
            if missing context.NpcIds quest.Giver then
                sink.Error("quest.giverMissing", path + ".giver", sprintf "Quest \"%s\" is given by missing NPC \"%s\"" quest.Name quest.Giver, target)
            match quest.Prerequisites with
            | null -> ()
            | prerequisites ->
                prerequisites
                |> Seq.iteri (fun k id ->
                    if id = quest.Id then
                        sink.Error("quest.prerequisiteSelf", sprintf "%s.prerequisites[%d]" path k, sprintf "Quest \"%s\" requires itself" quest.Name, target))
            if quest.AvailableFromDay.HasValue && quest.AvailableToDay.HasValue && quest.AvailableFromDay.Value > quest.AvailableToDay.Value then
                sink.Warning("quest.availableWindow", path + ".availableFromDay", sprintf "Quest \"%s\" is available from day %g until day %g, which never happens" quest.Name quest.AvailableFromDay.Value quest.AvailableToDay.Value, target)
            match quest.AvailableSeasons with
            | null -> ()
            | seasons ->
                seasons
                |> Seq.iteri (fun k season ->
                    if not (context.SeasonIds.Contains season) then
                        sink.Warning("quest.unknownSeason", sprintf "%s.availableSeasons[%d]" path k, sprintf "Quest \"%s\" is available in \"%s\", which is not in the calendar" quest.Name season, target))
            quest.Objectives
            |> Seq.iteri (fun k objective ->
                let opath = sprintf "%s.objectives[%d]" path k
                let noTarget (field: string) =
                    sink.Warning("quest.objectiveNoTarget", opath + "." + field, sprintf "Quest \"%s\" objective %d (%s) has no target yet" quest.Name (k + 1) objective.Type, target)
                match objective.Type with
                | QuestObjectiveTypes.Collect | QuestObjectiveTypes.Craft | QuestObjectiveTypes.Gift when not (hasValue objective.TargetItemId) -> noTarget "targetItemId"
                | QuestObjectiveTypes.Harvest when not (hasValue objective.TargetCropType) -> noTarget "targetCropType"
                | QuestObjectiveTypes.Talk when not (hasValue objective.TargetNpcId) -> noTarget "targetNPCId"
                | QuestObjectiveTypes.Visit when not (hasValue objective.TargetSceneId) -> noTarget "targetSceneId"
                | _ -> ()))

    /// The condition/outcome vocabulary shared by events, actions and minigame tiers (event-forms.tsx).
    /// `sceneId` is the scene tile conditions and tile outcomes are checked against (empty = any).
    let private conditions (context: Context) (sink: Sink) (family: string) (path: string) (label: string) (sceneId: string) (target: NavigationTarget option) (conditions: List<EventCondition>) =
        conditions
        |> Seq.iteri (fun c condition ->
            let cpath = sprintf "%s.conditions[%d]" path c
            let tile (x: float) (y: float) =
                if sceneId.Length > 0 && context.Scenes.ContainsKey sceneId && not (Context.tileInScene context sceneId x y) then
                    sink.Error(sprintf "%s.conditionTileOutOfBounds" family, cpath + ".x", sprintf "%s watches tile (%g,%g), outside its scene" label x y, target)
            match condition with
            | :? EnterTileCondition as t -> tile t.X t.Y
            | :? InteractTileCondition as t -> tile t.X t.Y
            | :? FriendshipCondition as f ->
                if missing context.NpcIds f.NpcId then
                    sink.Error(sprintf "%s.conditionUnknownNpc" family, cpath + ".npcId", sprintf "%s checks friendship with missing NPC \"%s\"" label f.NpcId, target)
            | :? FlagCondition as f ->
                if hasValue f.Flag && not (context.KnownFlags.Contains f.Flag) then
                    sink.Warning(sprintf "%s.conditionUnknownFlag" family, cpath + ".flag", sprintf "%s checks flag \"%s\", which nothing ever sets" label f.Flag, target)
            | :? WeatherCondition as w ->
                w.WeatherIds
                |> Seq.iteri (fun k id ->
                    if not (context.WeatherIds.Contains id) then
                        sink.Warning(sprintf "%s.conditionUnknownWeather" family, sprintf "%s.weatherIds[%d]" cpath k, sprintf "%s checks unknown weather \"%s\"" label id, target))
            | :? SeasonCondition as s ->
                s.Seasons
                |> Seq.iteri (fun k id ->
                    if not (context.SeasonIds.Contains id) then
                        sink.Warning(sprintf "%s.conditionUnknownSeason" family, sprintf "%s.seasons[%d]" cpath k, sprintf "%s checks season \"%s\", which is not in the calendar" label id, target))
            | :? FestivalIdCondition as f ->
                if missing context.FestivalIds f.FestivalId then
                    sink.Error(sprintf "%s.conditionUnknownFestival" family, cpath + ".festivalId", sprintf "%s checks missing festival \"%s\"" label f.FestivalId, target)
            | :? InventorySpaceCondition as i ->
                if missing context.ItemIds i.ItemId then
                    sink.Error(sprintf "%s.conditionUnknownItem" family, cpath + ".itemId", sprintf "%s checks room for missing item \"%s\"" label i.ItemId, target)
            // Event conditions on items and quests are content lints (validation.ts); actions get them here.
            | :? HasItemCondition as h when family <> "event" ->
                if missing context.ItemIds h.ItemId then
                    sink.Error(sprintf "%s.conditionUnknownItem" family, cpath + ".itemId", sprintf "%s checks missing item \"%s\"" label h.ItemId, target)
            | :? QuestStatusCondition as q when family <> "event" ->
                if missing context.QuestIds q.QuestId then
                    sink.Error(sprintf "%s.conditionUnknownQuest" family, cpath + ".questId", sprintf "%s checks missing quest \"%s\"" label q.QuestId, target)
            | _ -> ())

    let private outcomes (context: Context) (sink: Sink) (family: string) (path: string) (label: string) (sceneId: string) (target: NavigationTarget option) (outcomes: List<EventOutcome>) =
        outcomes
        |> Seq.iteri (fun o outcome ->
            let opath = sprintf "%s.outcomes[%d]" path o
            let needs (field: string) (present: bool) =
                if not present then
                    sink.Error(sprintf "%s.outcomeMissingField" family, opath + "." + field, sprintf "%s has a %s outcome with no %s" label outcome.Type field, target)
            let sceneFor (id: string | null) = if hasValue id then (match id with null -> sceneId | s -> s) else sceneId
            // Event outcomes on items, quests, NPCs and warps are content lints (validation.ts);
            // actions and minigame tiers get the same checks here.
            let unknown (code: string) (field: string) (known: string -> bool) (id: string | null) (what: string) =
                if family <> "event" && (match id with null -> false | id -> id.Length > 0 && not (known id)) then
                    sink.Error(sprintf "%s.%s" family code, opath + "." + field, sprintf "%s references missing %s \"%s\"" label what id, target)
            let unknownScene () =
                if (match outcome.SceneId with null -> false | id -> id.Length > 0 && not (context.Scenes.ContainsKey id)) then
                    sink.Error(sprintf "%s.outcomeUnknownScene" family, opath + ".sceneId", sprintf "%s %s in missing scene \"%s\"" label outcome.Type outcome.SceneId, target)
            match outcome.Type with
            | EventOutcomeTypes.Message -> needs "message" (hasValue outcome.Message)
            | EventOutcomeTypes.GiveItem | EventOutcomeTypes.TakeItem ->
                needs "itemId" (hasValue outcome.ItemId)
                unknown "outcomeUnknownItem" "itemId" context.ItemIds.Contains outcome.ItemId "item"
            | EventOutcomeTypes.GiveMoney | EventOutcomeTypes.TakeMoney | EventOutcomeTypes.ModifyEnergy -> needs "amount" outcome.Amount.HasValue
            | EventOutcomeTypes.ModifyFriendship ->
                needs "npcId" (hasValue outcome.NpcId)
                needs "amount" outcome.Amount.HasValue
                if missing context.NpcIds outcome.NpcId then
                    sink.Error(sprintf "%s.outcomeUnknownNpc" family, opath + ".npcId", sprintf "%s changes friendship with missing NPC \"%s\"" label outcome.NpcId, target)
            | EventOutcomeTypes.SetFlag | EventOutcomeTypes.ClearFlag -> needs "flagName" (hasValue outcome.FlagName)
            | EventOutcomeTypes.StartQuest | EventOutcomeTypes.CompleteQuest ->
                needs "questId" (hasValue outcome.QuestId)
                unknown "outcomeUnknownQuest" "questId" context.QuestIds.Contains outcome.QuestId "quest"
            | EventOutcomeTypes.SpawnNpc | EventOutcomeTypes.RemoveNpc ->
                needs "npcId" (hasValue outcome.NpcId)
                unknown "outcomeUnknownNpc" "npcId" context.NpcIds.Contains outcome.NpcId "NPC"
            | EventOutcomeTypes.StartDialogue ->
                needs "npcId" (hasValue outcome.NpcId)
                unknown "outcomeUnknownNpc" "npcId" context.NpcIds.Contains outcome.NpcId "NPC"
                match outcome.NpcId with
                | null -> ()
                | npcId ->
                    match outcome.DialogueId with
                    | null -> ()
                    | dialogueId when dialogueId.Length > 0 ->
                        match context.Project.Npcs |> Seq.tryFind (fun n -> n.Id = npcId) with
                        | Some npc when not (npc.Dialogue |> Seq.exists (fun d -> d.Id = dialogueId)) ->
                            sink.Error(sprintf "%s.outcomeUnknownDialogue" family, opath + ".dialogueId", sprintf "%s starts dialogue \"%s\", which %s does not have" label dialogueId npc.Name, target)
                        | _ -> ()
                    | _ -> ()
            | EventOutcomeTypes.ChangeTile ->
                needs "newTileType" (hasValue outcome.NewTileType)
                unknownScene ()
                let scene = sceneFor outcome.SceneId
                let x = if outcome.TileX.HasValue then outcome.TileX.Value else 0.0
                let y = if outcome.TileY.HasValue then outcome.TileY.Value else 0.0
                if scene.Length > 0 && context.Scenes.ContainsKey scene && not (Context.tileInScene context scene x y) then
                    sink.Error(sprintf "%s.outcomeTileOutOfBounds" family, opath + ".tileX", sprintf "%s changes tile (%g,%g), outside the scene" label x y, target)
            | EventOutcomeTypes.WarpPlayer ->
                needs "sceneId" (hasValue outcome.SceneId)
                unknown "outcomeUnknownScene" "sceneId" context.Scenes.ContainsKey outcome.SceneId "scene"
                let x = if outcome.X.HasValue then outcome.X.Value else 0.0
                let y = if outcome.Y.HasValue then outcome.Y.Value else 0.0
                match outcome.SceneId with
                | null -> ()
                | scene when context.Scenes.ContainsKey scene && not (Context.tileInScene context scene x y) ->
                    sink.Error(sprintf "%s.outcomeTileOutOfBounds" family, opath + ".x", sprintf "%s warps the player to (%g,%g), outside \"%s\"" label x y context.Scenes[scene].Name, target)
                | _ -> ()
            | EventOutcomeTypes.LockTransition | EventOutcomeTypes.UnlockTransition ->
                unknownScene ()
                let scene = sceneFor outcome.SceneId
                let x = if outcome.X.HasValue then outcome.X.Value else 0.0
                let y = if outcome.Y.HasValue then outcome.Y.Value else 0.0
                match context.Scenes.TryGetValue scene with
                | true, s when not (s.Transitions |> Seq.exists (fun t -> t.FromX = x && t.FromY = y)) ->
                    sink.Warning(sprintf "%s.outcomeUnknownTransition" family, opath + ".x", sprintf "%s %ss a transition at (%g,%g) in \"%s\", but there is none" label (if outcome.Type = EventOutcomeTypes.LockTransition then "lock" else "unlock") x y s.Name, target)
                | _ -> ()
            | EventOutcomeTypes.PlaySound -> needs "soundId" (hasValue outcome.SoundId)
            // validate-extensibility.ts: performAction / startMinigame targets must exist.
            | EventOutcomeTypes.PerformAction ->
                needs "actionId" (hasValue outcome.ActionId)
                if missing context.ActionIds outcome.ActionId then
                    sink.Error(sprintf "%s.outcomeUnknownAction" family, opath + ".actionId", sprintf "%s performs missing action \"%s\"" label outcome.ActionId, target)
            | EventOutcomeTypes.StartMinigame ->
                needs "minigameId" (hasValue outcome.MinigameId)
                if missing context.MinigameIds outcome.MinigameId then
                    sink.Error(sprintf "%s.outcomeUnknownMinigame" family, opath + ".minigameId", sprintf "%s starts missing minigame \"%s\"" label outcome.MinigameId, target)
            | _ -> ())

    let private events (context: Context) (sink: Sink) =
        context.Project.Events
        |> Seq.iteri (fun i event ->
            let path = sprintf "events[%d]" i
            let label = sprintf "Event \"%s\"" event.Name
            let target = Some(NavigationTarget.Event event.Id)
            if event.Outcomes.Count = 0 then
                sink.Warning("event.noOutcomes", path + ".outcomes", sprintf "%s does nothing (no outcomes)" label, target)
            let hasTile = event.Conditions |> Seq.exists (fun c -> c :? EnterTileCondition || c :? InteractTileCondition)
            if event.Trigger = EventTriggers.Enter && not hasTile then
                sink.Warning("event.enterWithoutTile", path + ".conditions", sprintf "%s fires on enter but has no tile or region condition, so it fires on every step" label, target)
            if event.Trigger = EventTriggers.Interact && not hasTile then
                sink.Warning("event.interactWithoutTile", path + ".conditions", sprintf "%s fires on interact but has no tile or region condition" label, target)
            conditions context sink "event" path label event.SceneId target event.Conditions
            outcomes context sink "event" path label event.SceneId target event.Outcomes)

    /// reserved-keys.ts: gameplay keys creator hotkeys may never claim.
    let reservedActionKeys =
        HashSet<string>([ "w"; "a"; "s"; "d"; "e"; "q"; "t"; "r"; "f"; "c"; "z"; "i"; "j"; "x"; " "; "enter"; "escape"; "arrowup"; "arrowdown"; "arrowleft"; "arrowright" ])

    let private actions (context: Context) (sink: Sink) =
        let hotkeys = Dictionary<string, string>()
        context.Project.Actions
        |> Seq.iteri (fun i action ->
            let path = sprintf "actions[%d]" i
            let label = sprintf "Action \"%s\"" action.Name
            let target = Some(NavigationTarget.Action action.Id)
            if action.Outcomes.Count = 0 then
                sink.Warning("action.noOutcomes", path + ".outcomes", sprintf "%s does nothing (no outcomes)" label, target)
            match action.Hotkey with
            | null -> ()
            | key when key.Length > 0 ->
                let lowered = key.ToLowerInvariant()
                if reservedActionKeys.Contains lowered then
                    sink.Warning("action.reservedHotkey", path + ".hotkey", sprintf "%s uses hotkey \"%s\", which is reserved for gameplay and ignored in play" label key, target)
                match hotkeys.TryGetValue lowered with
                | true, other -> sink.Warning("action.duplicateHotkey", path + ".hotkey", sprintf "%s shares hotkey \"%s\" with \"%s\"" label key other, target)
                | _ -> hotkeys[lowered] <- action.Name
            | _ -> ()
            conditions context sink "action" path label "" target action.Conditions
            outcomes context sink "action" path label "" target action.Outcomes)

    let private minigames (context: Context) (sink: Sink) =
        context.Project.Minigames
        |> Seq.iteri (fun i minigame ->
            let path = sprintf "minigames[%d]" i
            let target = Some(NavigationTarget.Minigame minigame.Id)
            if System.String.IsNullOrWhiteSpace minigame.Kind then
                sink.Error("minigame.emptyKind", path + ".kind", sprintf "Minigame \"%s\" has no kind" minigame.Name, target)
            if minigame.ResultTiers.Count = 0 then
                sink.Warning("minigame.noTiers", path + ".resultTiers", sprintf "Minigame \"%s\" has no result tiers, so its score changes nothing" minigame.Name, target)
            minigame.ResultTiers
            |> Seq.iteri (fun t tier ->
                outcomes context sink "minigame" (sprintf "%s.resultTiers[%d]" path t) (sprintf "Minigame \"%s\" tier %d" minigame.Name (t + 1)) "" target tier.Outcomes))

    let private shops (context: Context) (sink: Sink) =
        let opened = HashSet<string>(
            Seq.append
                (context.Project.Dialogues |> Seq.collect (fun d -> d.Options |> Seq.choose (fun o -> Option.ofObj o.OpenShopId)))
                (context.Project.Npcs |> Seq.collect (fun n -> n.Dialogue |> Seq.collect (fun d -> d.Options |> Seq.choose (fun o -> Option.ofObj o.OpenShopId)))))
        context.Project.Shops
        |> Seq.iteri (fun i shop ->
            let path = sprintf "shops[%d]" i
            let target = Some(NavigationTarget.Shop shop.Id)
            if shop.Stock.Count = 0 then
                sink.Info("shop.noStock", path + ".stock", sprintf "Shop \"%s\" sells nothing" shop.Name, target)
            if not (opened.Contains shop.Id) then
                sink.Info("shop.unreachable", path, sprintf "No dialogue option opens shop \"%s\"" shop.Name, target)
            shop.Stock
            |> Seq.iteri (fun k entry ->
                match entry.Seasons with
                | null -> ()
                | seasons ->
                    seasons
                    |> Seq.iteri (fun s season ->
                        if not (context.SeasonIds.Contains season) then
                            sink.Warning("shop.unknownSeason", sprintf "%s.stock[%d].seasons[%d]" path k s, sprintf "Shop \"%s\" sells \"%s\" in season \"%s\", which is not in the calendar" shop.Name entry.ItemId season, target))))

    let private recipes (context: Context) (sink: Sink) =
        let stations = HashSet<string>(context.Project.MachineTypes |> Seq.collect (fun m -> m.StationCategories))
        context.Project.Recipes
        |> Seq.iteri (fun i recipe ->
            let path = sprintf "recipes[%d]" i
            let target = Some(NavigationTarget.Recipe recipe.Id)
            let ingredients (kind: string) (list: List<RecipeIngredient>) =
                list
                |> Seq.iteri (fun k ingredient ->
                    if not (context.ItemIds.Contains ingredient.ItemId) then
                        sink.Error(sprintf "recipe.unknown%s" (if kind = "inputs" then "Input" else "Output"), sprintf "%s.%s[%d].itemId" path kind k,
                                   sprintf "Recipe \"%s\" %s missing item \"%s\"" recipe.Name (if kind = "inputs" then "needs" else "makes") ingredient.ItemId, target))
            ingredients "inputs" recipe.Inputs
            ingredients "outputs" recipe.Outputs
            if recipe.Outputs.Count = 0 then
                sink.Warning("recipe.noOutputs", path + ".outputs", sprintf "Recipe \"%s\" makes nothing" recipe.Name, target)
            if missing context.MachineTypeIds recipe.MachineTypeId then
                sink.Error("recipe.unknownMachine", path + ".machineTypeId", sprintf "Recipe \"%s\" needs missing machine \"%s\"" recipe.Name recipe.MachineTypeId, target)
            match recipe.RequiresStationCategory with
            | null -> ()
            | station when station.Length > 0 && not (stations.Contains station) ->
                sink.Warning("recipe.unknownStation", path + ".requiresStationCategory", sprintf "Recipe \"%s\" needs a \"%s\" station, which no machine provides" recipe.Name station, target)
            | _ -> ()
            match recipe.Unlock with
            | null -> ()
            | unlock ->
                match unlock.Skill with
                | null -> ()
                | skill when not (SaveSchema.SkillNames |> Seq.contains skill.Skill) ->
                    sink.Warning("recipe.unknownSkill", path + ".unlock.skill.skill", sprintf "Recipe \"%s\" unlocks with unknown skill \"%s\"" recipe.Name skill.Skill, target)
                | _ -> ()
                if missing context.QuestIds unlock.QuestId then
                    sink.Error("recipe.unlockQuestMissing", path + ".unlock.questId", sprintf "Recipe \"%s\" unlocks after missing quest \"%s\"" recipe.Name unlock.QuestId, target)
                match unlock.Seasons with
                | null -> ()
                | seasons ->
                    seasons
                    |> Seq.iteri (fun k season ->
                        if not (context.SeasonIds.Contains season) then
                            sink.Warning("recipe.unknownSeason", sprintf "%s.unlock.seasons[%d]" path k, sprintf "Recipe \"%s\" is craftable in \"%s\", which is not in the calendar" recipe.Name season, target)))

    let private machineTypes (context: Context) (sink: Sink) =
        context.Project.MachineTypes
        |> Seq.iteri (fun i machine ->
            let path = sprintf "machineTypes[%d]" i
            let target = Some(NavigationTarget.MachineType machine.Id)
            match machine.ItemId with
            | null -> sink.Warning("machineType.noItem", path + ".itemId", sprintf "Machine \"%s\" has no item, so players cannot place it" machine.Name, target)
            | id when id.Length = 0 -> sink.Warning("machineType.noItem", path + ".itemId", sprintf "Machine \"%s\" has no item, so players cannot place it" machine.Name, target)
            | id when not (context.ItemIds.Contains id) -> sink.Error("machineType.unknownItem", path + ".itemId", sprintf "Machine \"%s\" is placed with missing item \"%s\"" machine.Name id, target)
            | _ -> ())

    let private nodeTypes (context: Context) (sink: Sink) =
        context.Project.NodeTypes
        |> Seq.iteri (fun i node ->
            let path = sprintf "nodeTypes[%d]" i
            let target = Some(NavigationTarget.NodeType node.Id)
            if node.Drops.Count = 0 then
                sink.Warning("nodeType.noDrops", path + ".drops", sprintf "Node type \"%s\" drops nothing" node.Name, target)
            node.Drops
            |> Seq.iteri (fun k drop ->
                if drop.Min > drop.Max then
                    sink.Error("nodeType.dropRange", sprintf "%s.drops[%d].min" path k, sprintf "Node type \"%s\" drop %d has min %g above max %g" node.Name (k + 1) drop.Min drop.Max, target)))

    let private wildlife (context: Context) (sink: Sink) =
        context.Project.AnimalSpecies
        |> Seq.iteri (fun i species ->
            let path = sprintf "animalSpecies[%d]" i
            let target = Some(NavigationTarget.AnimalSpecies species.Id)
            if species.ProductItemId.Length = 0 then
                sink.Warning("species.noProduct", path + ".productItemId", sprintf "Species \"%s\" produces nothing" species.Name, target)
            elif not (context.ItemIds.Contains species.ProductItemId) then
                sink.Error("species.unknownProduct", path + ".productItemId", sprintf "Species \"%s\" produces missing item \"%s\"" species.Name species.ProductItemId, target)
            if missing context.ItemIds species.FeedItemId then
                sink.Error("species.unknownFeed", path + ".feedItemId", sprintf "Species \"%s\" eats missing item \"%s\"" species.Name species.FeedItemId, target))
        context.Project.Animals
        |> Seq.iteri (fun i animal ->
            let path = sprintf "animals[%d]" i
            let target = Some(NavigationTarget.AnimalSpecies animal.SpeciesId)
            if not (context.SpeciesIds.Contains animal.SpeciesId) then
                sink.Error("animal.unknownSpecies", path + ".speciesId", sprintf "Animal \"%s\" is of missing species \"%s\"" animal.Name animal.SpeciesId, target)
            if not (context.Scenes.ContainsKey animal.SceneId) then
                sink.Error("animal.unknownScene", path + ".sceneId", sprintf "Animal \"%s\" lives in missing scene \"%s\"" animal.Name animal.SceneId, target)
            elif not (Context.tileInScene context animal.SceneId animal.X animal.Y) then
                sink.Error("animal.outOfBounds", path + ".x", sprintf "Animal \"%s\" stands at (%g,%g), outside its scene" animal.Name animal.X animal.Y, Some(NavigationTarget.Scene(animal.SceneId, 0, 0))))
        context.Project.FishTables
        |> Seq.iteri (fun i table ->
            let path = sprintf "fishTables[%d]" i
            let target = Some(NavigationTarget.FishTable table.Id)
            if table.Entries.Count = 0 then
                sink.Warning("fishTable.noEntries", path + ".entries", sprintf "Fish table \"%s\" has no fish" table.Name, target)
            table.Entries
            |> Seq.iteri (fun k entry ->
                if not (context.ItemIds.Contains entry.ItemId) then
                    sink.Error("fishTable.unknownItem", sprintf "%s.entries[%d].itemId" path k, sprintf "Fish table \"%s\" catches missing item \"%s\"" table.Name entry.ItemId, target))
            if missing context.ItemIds table.JunkItemId then
                sink.Error("fishTable.unknownJunk", path + ".junkItemId", sprintf "Fish table \"%s\" catches missing junk item \"%s\"" table.Name table.JunkItemId, target)
            match table.SceneIds with
            | null -> ()
            | scenes ->
                scenes
                |> Seq.iteri (fun k id ->
                    if not (context.Scenes.ContainsKey id) then
                        sink.Error("fishTable.unknownScene", sprintf "%s.sceneIds[%d]" path k, sprintf "Fish table \"%s\" applies to missing scene \"%s\"" table.Name id, target))
            match table.Seasons with
            | null -> ()
            | seasons ->
                seasons
                |> Seq.iteri (fun k season ->
                    if not (context.SeasonIds.Contains season) then
                        sink.Warning("fishTable.unknownSeason", sprintf "%s.seasons[%d]" path k, sprintf "Fish table \"%s\" applies in \"%s\", which is not in the calendar" table.Name season, target)))

    /// validate-graphics.ts: bindings, frames, clips and sheets must fit their assets.
    let private graphics (context: Context) (sink: Sink) =
        let project = context.Project
        let assets = Dictionary<string, CustomAsset>()
        for a in project.CustomAssets do
            if not (assets.ContainsKey a.Id) then assets[a.Id] <- a
        let frame (path: string) (f: ArtFrame) (defaultId: string) (label: string) (target: NavigationTarget option) =
            let id = match f.AssetId with null -> defaultId | s -> s
            match assets.TryGetValue id with
            | false, _ -> sink.Error("graphics.missingAsset", path, sprintf "%s: missing frame image %s" label id, target)
            | true, a ->
                if a.Width.HasValue && a.Height.HasValue && (f.X + f.Width > a.Width.Value || f.Y + f.Height > a.Height.Value) then
                    sink.Error("graphics.frameOutside", path, sprintf "%s: frame is outside %s (%g×%g)" label a.Name a.Width.Value a.Height.Value, target)
        let visual (path: string) (v: VisualRef | null) (label: string) (target: NavigationTarget option) =
            match v with
            | null -> ()
            | v ->
                match assets.TryGetValue v.AssetId with
                | false, _ -> sink.Error("graphics.missingAsset", path + ".assetId", sprintf "%s: missing artwork %s" label v.AssetId, target)
                | true, a ->
                    match v.Animation with
                    | null -> ()
                    | animation when animation.Length > 0 && not (match a.Animations with null -> false | clips -> clips |> Seq.exists (fun c -> c.Name = animation)) ->
                        sink.Error("graphics.missingAnimation", path + ".animation", sprintf "%s: missing animation %s on %s" label animation a.Name, target)
                    | _ -> ()
                    match v.Frame with
                    | null -> ()
                    | f -> frame (path + ".frame") f a.Id label target
        let customImage (path: string) (id: string | null) (label: string) (target: NavigationTarget option) =
            match id with
            | null -> ()
            | id when id.Length > 0 && not (assets.ContainsKey id) ->
                sink.Warning("graphics.missingCustomImage", path, sprintf "%s: custom image %s is not an asset" label id, target)
            | _ -> ()
        project.CustomAssets
        |> Seq.iteri (fun i a ->
            let path = sprintf "customAssets[%d]" i
            let target = Some(NavigationTarget.Asset a.Id)
            match a.Animations with
            | null -> ()
            | clips ->
                let names = HashSet<string>()
                clips
                |> Seq.iteri (fun c clip ->
                    if not (names.Add clip.Name) then
                        sink.Error("graphics.duplicateClip", sprintf "%s.animations[%d].name" path c, sprintf "%s: duplicate clip %s" a.Name clip.Name, target)
                    clip.Frames
                    |> Seq.iteri (fun k f -> frame (sprintf "%s.animations[%d].frames[%d]" path c k) f a.Id (sprintf "%s / %s / frame %d" a.Name clip.Name (k + 1)) target))
            match a.Sheet with
            | null -> ()
            | sheet ->
                if a.Width.HasValue && a.Height.HasValue
                   && (sheet.FrameWidth * sheet.Frames > a.Width.Value || sheet.FrameHeight * (if sheet.Directional then 4.0 else 1.0) > a.Height.Value) then
                    sink.Error("graphics.sheetTooLarge", path + ".sheet", sprintf "%s: sprite sheet dimensions exceed the image" a.Name, target))
        visual "playerVisual" project.PlayerVisual "Player" (Some NavigationTarget.Settings)
        customImage "playerCustomImage" project.PlayerCustomImage "Player" (Some NavigationTarget.Settings)
        project.Npcs |> Seq.iteri (fun i n ->
            visual (sprintf "npcs[%d].visual" i) n.Visual n.Name (Some(NavigationTarget.Npc n.Id))
            customImage (sprintf "npcs[%d].customImage" i) n.CustomImage n.Name (Some(NavigationTarget.Npc n.Id)))
        project.Items |> Seq.iteri (fun i d ->
            visual (sprintf "items[%d].visual" i) d.Visual d.Name (Some(NavigationTarget.Item d.Id))
            customImage (sprintf "items[%d].customImage" i) d.CustomImage d.Name (Some(NavigationTarget.Item d.Id)))
        match project.CustomCrops with
        | null -> ()
        | crops -> crops |> Seq.iteri (fun i d -> visual (sprintf "customCrops[%d].visual" i) d.Visual d.Name (Some(NavigationTarget.Crop d.Id)))
        project.NodeTypes |> Seq.iteri (fun i d -> visual (sprintf "nodeTypes[%d].visual" i) d.Visual d.Name (Some(NavigationTarget.NodeType d.Id)))
        project.MachineTypes |> Seq.iteri (fun i d -> visual (sprintf "machineTypes[%d].visual" i) d.Visual d.Name (Some(NavigationTarget.MachineType d.Id)))
        project.AnimalSpecies |> Seq.iteri (fun i d -> visual (sprintf "animalSpecies[%d].visual" i) d.Visual d.Name (Some(NavigationTarget.AnimalSpecies d.Id)))
        project.Scenes
        |> Seq.iteri (fun s scene ->
            scene.Tiles
            |> Seq.iteri (fun y row ->
                row
                |> Seq.iteri (fun x tile ->
                    let path = sprintf "scenes[%d].tiles[%d][%d]" s y x
                    let target = Some(NavigationTarget.Scene(scene.Id, x, y))
                    let label = sprintf "%s (%d,%d)" scene.Name x y
                    match tile.Visuals with
                    | null -> ()
                    | visuals ->
                        visual (path + ".visuals.background") visuals.Background (label + " background") target
                        visual (path + ".visuals.overlay") visuals.Overlay (label + " overlay") target
                        visual (path + ".visuals.object") visuals.Object (label + " object") target
                    customImage (path + ".customImage") tile.CustomImage label target
                    match tile.Item with
                    | null -> ()
                    | item -> visual (path + ".item.visual") item.Visual (sprintf "%s dropped item" scene.Name) target)))
        match project.GamePanels with
        | null -> ()
        | panels ->
            panels
            |> Seq.iteri (fun i panel ->
                panel.Entries
                |> Seq.iteri (fun k entry ->
                    let path = sprintf "gamePanels[%d].entries[%d].value" i k
                    let shown = if entry.Value.Length = 0 then "(not selected)" else entry.Value
                    if entry.Kind = GamePanelEntryKinds.Action && not (context.ActionIds.Contains entry.Value) then
                        sink.Error("interface.missingAction", path, sprintf "%s: missing action %s" panel.Title shown, Some NavigationTarget.Interface)
                    if entry.Kind = GamePanelEntryKinds.Item && not (context.ItemIds.Contains entry.Value) then
                        sink.Error("interface.missingItem", path, sprintf "%s: missing item %s" panel.Title shown, Some NavigationTarget.Interface)))

    /// ModsEditor: the compatibility badge and the "already installed" rule.
    let private packs (context: Context) (sink: Sink) =
        let seen = HashSet<string>()
        context.Project.ContentPacks
        |> Seq.iteri (fun i install ->
            let manifest = install.Pack.Manifest
            let path = sprintf "contentPacks[%d]" i
            let target = Some(NavigationTarget.Pack manifest.Id)
            if not (seen.Add manifest.Id) then
                sink.Error("pack.duplicate", path + ".pack.manifest.id", sprintf "Pack \"%s\" is installed twice" manifest.Id, target)
            if not (PackRules.isEngineCompatible manifest.EngineCompatibility PackRules.EngineVersion) then
                sink.Warning("pack.incompatible", path + ".pack.manifest.engineCompatibility", sprintf "Pack \"%s\" wants engine %s, this is %s" manifest.Name manifest.EngineCompatibility PackRules.EngineVersion, target))

    let run (context: Context) (sink: Sink) =
        duplicates context sink
        items context sink
        crops context sink
        quests context sink
        events context sink
        actions context sink
        minigames context sink
        shops context sink
        recipes context sink
        machineTypes context sink
        nodeTypes context sink
        wildlife context sink
        graphics context sink
        packs context sink
