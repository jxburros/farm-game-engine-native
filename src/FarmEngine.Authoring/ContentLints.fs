namespace FarmEngine.Authoring

open System
open FarmEngine.Schemas

/// One content lint (TS `Problem` of validation.ts). `Category` is the web category
/// (`scenes`, `transitions`, `dialogue`, `npcs`, `quests`, `events`, `shops`, `nodes`, `crops`,
/// `items`, `packs`) and `Subject` the id to deep-link to (scene, NPC, quest, …) when there is one.
type ContentLint =
    { Severity: Severity
      Category: string
      Message: string
      Subject: string option }

/// Port of `Validation.cs` (validation.ts `validateProjectContent`): project linting for the
/// Problems panel. Pure content checks, the same validations that run on import. Every check
/// targets a dangling-reference or unreachable-content class of bug.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ContentLints =
    let private lint severity category message (subject: string) : ContentLint =
        { Severity = severity; Category = category; Message = message; Subject = Some subject }

    let private error category message subject = lint Severity.Error category message subject
    let private warning category message subject = lint Severity.Warning category message subject

    /// `list ?? []`: the TS tolerates missing optional lists here.
    let private orEmpty (list: 'T list option) : 'T list = defaultArg list []

    /// `value && !known.has(value)`: a non-empty reference to something `known` does not contain.
    let private dangling (known: string -> bool) (value: string option) =
        match value with
        | None -> false
        | Some value -> value.Length > 0 && not (known value)

    /// An optional reference as the TS template literal prints it (`undefined` never reaches one).
    let private text (value: string option) = defaultArg value ""

    let private num (value: float) = JsNumber.format value

    let private ids (idOf: 'T -> string) (list: 'T list) : Set<string> = list |> List.map idOf |> Set.ofList

    /// Every lint for the project, in the order the web reports them.
    let validateProjectContent (project: GameProject) : ContentLint list =
        let scenes = project.Scenes
        let sceneIds = ids (fun (s: Scene) -> s.Id) project.Scenes
        let itemIds = ids (fun (i: Item) -> i.Id) project.Items
        let npcIds = ids (fun (n: Npc) -> n.Id) project.Npcs
        let questIds = ids (fun (q: Quest) -> q.Id) project.Quests
        let shopIds = ids (fun (s: ShopDefinition) -> s.Id) project.Shops
        let crops = ContentCompiler.mergeCrops project.CustomCrops
        let cropIds = crops |> List.map fst |> Set.ofList
        let crop (id: string) = crops |> List.find (fun (key, _) -> key = id) |> snd
        let nodeTypeIds =
            (Builtin.nodeTypes () |> List.map (fun d -> d.Id)) @ (project.NodeTypes |> List.map (fun d -> d.Id))
            |> Set.ofList
        let dialogueIds =
            project.Dialogues @ (project.Npcs |> List.collect (fun npc -> npc.Dialogue))
            |> List.map (fun d -> d.Id)
            |> Set.ofList
        let reachable =
            scenes
            |> List.collect (fun scene -> scene.Transitions |> List.map (fun t -> t.ToSceneId))
            |> Set.ofList
            |> Set.add project.StartSceneId
        let isGenerated (scene: Scene) =
            match scene.Extra |> List.tryFind (fun (key, _) -> key = "generated") with
            | Some(_, generated) -> Json.truthy generated
            | None -> false

        let at (tile: Tile) = $"({num tile.X},{num tile.Y})"

        let dialogueLinks (dialogue: Dialogue) =
            [ for option in dialogue.Options do
                  if dangling dialogueIds.Contains option.NextDialogueId then
                      error "dialogue" $"Dialogue \"{dialogue.Id}\" links to missing dialogue \"{text option.NextDialogueId}\"" dialogue.NpcId
                  if dangling itemIds.Contains option.GiveItem then
                      error "dialogue" $"Dialogue \"{dialogue.Id}\" gives missing item \"{text option.GiveItem}\"" dialogue.NpcId
                  if dangling shopIds.Contains option.OpenShopId then
                      error "dialogue" $"Dialogue \"{dialogue.Id}\" opens missing shop \"{text option.OpenShopId}\"" dialogue.NpcId
                  if dangling questIds.Contains option.OfferQuestId then
                      error "dialogue" $"Dialogue \"{dialogue.Id}\" offers missing quest \"{text option.OfferQuestId}\"" dialogue.NpcId ]

        [ // Start scene
          if not (sceneIds.Contains project.StartSceneId) then
              error "scenes" $"Start scene \"{project.StartSceneId}\" does not exist" project.StartSceneId

          // Transitions
          for scene in scenes do
              for transition in scene.Transitions do
                  if not (sceneIds.Contains transition.ToSceneId) then
                      error "transitions"
                          $"Transition in \"{scene.Name}\" at ({num transition.FromX},{num transition.FromY}) leads to missing scene \"{transition.ToSceneId}\""
                          scene.Id
                  else
                      let target = scenes |> List.find (fun s -> s.Id = transition.ToSceneId)
                      if transition.ToX < 0.0 || transition.ToX >= target.Width || transition.ToY < 0.0 || transition.ToY >= target.Height then
                          error "transitions"
                              $"Transition in \"{scene.Name}\" lands out of bounds at ({num transition.ToX},{num transition.ToY}) in \"{target.Name}\""
                              scene.Id

          // Unreachable scenes (no transition in, not the start scene); generated mine floors are transient.
          for scene in scenes do
              if not (isGenerated scene) && not (reachable.Contains scene.Id) then
                  warning "scenes" $"Scene \"{scene.Name}\" is unreachable (no transition leads to it)" scene.Id

          // Dialogue references, NPC placement and schedules. The NPC copies mirror the project
          // list, so an NPC copy is only linted when it differs from the listed one (or is
          // missing from the list): otherwise every dangling link would be reported twice.
          for dialogue in project.Dialogues do
              for problem in dialogueLinks dialogue do
                  problem
          let listed = project.Dialogues |> List.map (fun d -> d.Id, d) |> List.distinctBy fst |> Map.ofList
          for npc in project.Npcs do
              for dialogue in npc.Dialogue do
                  if Map.tryFind dialogue.Id listed <> Some dialogue then
                      for problem in dialogueLinks dialogue do
                          problem
              if not (sceneIds.Contains npc.SceneId) then
                  error "npcs" $"NPC \"{npc.Name}\" is placed in missing scene \"{npc.SceneId}\"" npc.Id
              for entry in orEmpty npc.Schedule do
                  if not (sceneIds.Contains entry.SceneId) then
                      error "npcs" $"NPC \"{npc.Name}\" schedule targets missing scene \"{entry.SceneId}\"" npc.Id

          // Quests
          for quest in project.Quests do
              for prereq in orEmpty quest.Prerequisites do
                  if not (questIds.Contains prereq) then
                      error "quests" $"Quest \"{quest.Name}\" requires missing quest \"{prereq}\"" quest.Id
              for objective in quest.Objectives do
                  if dangling itemIds.Contains objective.TargetItemId then
                      error "quests" $"Quest \"{quest.Name}\" objective targets missing item \"{text objective.TargetItemId}\"" quest.Id
                  if dangling npcIds.Contains objective.TargetNpcId then
                      error "quests" $"Quest \"{quest.Name}\" objective targets missing NPC \"{text objective.TargetNpcId}\"" quest.Id
                  if dangling sceneIds.Contains objective.TargetSceneId then
                      error "quests" $"Quest \"{quest.Name}\" objective targets missing scene \"{text objective.TargetSceneId}\"" quest.Id
                  if dangling cropIds.Contains objective.TargetCropType then
                      error "quests" $"Quest \"{quest.Name}\" objective targets missing crop \"{text objective.TargetCropType}\"" quest.Id
              for reward in orEmpty quest.Rewards.Items do
                  if not (itemIds.Contains reward.ItemId) then
                      error "quests" $"Quest \"{quest.Name}\" rewards missing item \"{reward.ItemId}\"" quest.Id

          // Events
          for event in project.Events do
              if dangling sceneIds.Contains (Some event.SceneId) then
                  error "events" $"Event \"{event.Name}\" belongs to missing scene \"{event.SceneId}\"" event.Id
              for condition in event.Conditions do
                  match condition with
                  | EventCondition.HasItem hasItem when not (itemIds.Contains hasItem.ItemId) ->
                      error "events" $"Event \"{event.Name}\" checks missing item \"{hasItem.ItemId}\"" event.Id
                  | EventCondition.QuestStatus questStatus when not (questIds.Contains questStatus.QuestId) ->
                      error "events" $"Event \"{event.Name}\" checks missing quest \"{questStatus.QuestId}\"" event.Id
                  | _ -> ()
              for outcome in event.Outcomes do
                  let missingRef (types: string list) (id: string option) (known: Set<string>) =
                      List.contains outcome.Type types && dangling known.Contains id
                  if missingRef [ "giveItem"; "takeItem" ] outcome.ItemId itemIds then
                      error "events" $"Event \"{event.Name}\" references missing item \"{text outcome.ItemId}\"" event.Id
                  if missingRef [ "startQuest"; "completeQuest" ] outcome.QuestId questIds then
                      error "events" $"Event \"{event.Name}\" references missing quest \"{text outcome.QuestId}\"" event.Id
                  if missingRef [ "spawnNPC"; "removeNPC"; "startDialogue" ] outcome.NpcId npcIds then
                      error "events" $"Event \"{event.Name}\" references missing NPC \"{text outcome.NpcId}\"" event.Id
                  if missingRef [ "warpPlayer" ] outcome.SceneId sceneIds then
                      error "events" $"Event \"{event.Name}\" warps to missing scene \"{text outcome.SceneId}\"" event.Id

          // Shops
          for shop in project.Shops do
              for entry in shop.Stock do
                  if not (itemIds.Contains entry.ItemId) then
                      error "shops" $"Shop \"{shop.Name}\" stocks missing item \"{entry.ItemId}\"" shop.Id

          // Node types + placed nodes, planted crops
          for nodeType in project.NodeTypes do
              for drop in nodeType.Drops do
                  if not (itemIds.Contains drop.ItemId) then
                      error "nodes" $"Node type \"{nodeType.Name}\" drops missing item \"{drop.ItemId}\"" nodeType.Id
          for scene in scenes do
              for row in scene.Tiles do
                  for tile in row do
                      match tile.Node with
                      | Some node when not (nodeTypeIds.Contains node.TypeId) ->
                          error "nodes" $"Scene \"{scene.Name}\" has a placed node of missing type \"{node.TypeId}\" at {at tile}" scene.Id
                      | _ -> ()
                      match tile.Crop with
                      | Some planted when not (cropIds.Contains planted.Type) ->
                          warning "crops" $"Scene \"{scene.Name}\" has a planted crop of missing type \"{planted.Type}\" at {at tile}" scene.Id
                      | Some planted when not (List.contains project.CurrentSeason (crop planted.Type).Seasons) ->
                          warning "crops"
                              $"Scene \"{scene.Name}\": {(crop planted.Type).Name} at {at tile} cannot grow in the starting season ({project.CurrentSeason})"
                              scene.Id
                      | _ -> ()

          // Seeds referencing missing crops
          for item in project.Items do
              if item.Type = "seed" && dangling cropIds.Contains item.CropType then
                  error "items" $"Seed \"{item.Name}\" references missing crop \"{text item.CropType}\"" item.Id

          // Content packs (M5): load-order errors, compatibility warnings and undeclared-override
          // conflicts from a dry-run merge.
          if not project.ContentPacks.IsEmpty then
              let _, mergedProblems = PackMerge.mergeIntoContent (ContentCompiler.baseContent project) project.ContentPacks
              for problem in mergedProblems do
                  let severity = if problem.Severity = "error" then Severity.Error else Severity.Warning
                  lint severity "packs" problem.Message problem.PackId ]
