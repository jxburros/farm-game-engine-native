namespace FarmEngine.Authoring

open System
open FarmEngine.Json
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
    let private lint severity category message (subject: string | null) : ContentLint =
        { Severity = severity; Category = category; Message = message; Subject = Option.ofObj subject }

    let private error category message subject = lint Severity.Error category message subject
    let private warning category message subject = lint Severity.Warning category message subject

    /// `list ?? []`: the C# (like the TS) tolerates missing lists here.
    let private orEmpty (list: seq<'T> | null) : seq<'T> =
        match list with
        | null -> Seq.empty
        | list -> list

    /// `x is null` for a record or list the C# declares non-nullable (JSON `null` still gets there).
    let private missing (value: 'T) = obj.ReferenceEquals(value, null)

    /// `value && !known.has(value)`: a non-empty reference to something `known` does not contain.
    let private dangling (known: string -> bool) (value: string | null) =
        match value with
        | null -> false
        | value -> value.Length > 0 && not (known value)

    let private ids (idOf: 'T -> string) (list: seq<'T> | null) : Set<string> = orEmpty list |> Seq.map idOf |> Set.ofSeq

    /// Every lint for the project, in the order the web reports them.
    let validateProjectContent (project: GameProject) : ContentLint list =
        let scenes = orEmpty project.Scenes
        let sceneIds = ids (fun (s: Scene) -> s.Id) project.Scenes
        let itemIds = ids (fun (i: Item) -> i.Id) project.Items
        let npcIds = ids (fun (n: Npc) -> n.Id) project.Npcs
        let questIds = ids (fun (q: Quest) -> q.Id) project.Quests
        let shopIds = ids (fun (s: ShopDefinition) -> s.Id) project.Shops
        let crops = ContentCompiler.mergeCrops project.CustomCrops
        let nodeTypeIds =
            Seq.append (Builtin.nodeTypes () |> Seq.map (fun d -> d.Id)) (orEmpty project.NodeTypes |> Seq.map (fun d -> d.Id))
            |> Set.ofSeq
        let dialogueIds =
            Seq.append (orEmpty project.Dialogues) (orEmpty project.Npcs |> Seq.collect (fun npc -> orEmpty npc.Dialogue))
            |> Seq.map (fun d -> d.Id)
            |> Set.ofSeq
        let reachable =
            scenes
            |> Seq.collect (fun scene -> orEmpty scene.Transitions |> Seq.map (fun t -> t.ToSceneId))
            |> Set.ofSeq
            |> Set.add project.StartSceneId
        let isGenerated (scene: Scene) =
            match scene.Extra with
            | null -> false
            | extra ->
                match extra.TryGetValue "generated" with
                | true, generated -> Js.Truthy generated
                | _ -> false

        let at (tile: Tile) = $"({Js.Num tile.X},{Js.Num tile.Y})"

        let dialogueLinks (dialogue: Dialogue) =
            [ for option in orEmpty dialogue.Options do
                  if dangling dialogueIds.Contains option.NextDialogueId then
                      error "dialogue" $"Dialogue \"{dialogue.Id}\" links to missing dialogue \"{option.NextDialogueId}\"" dialogue.NpcId
                  if dangling itemIds.Contains option.GiveItem then
                      error "dialogue" $"Dialogue \"{dialogue.Id}\" gives missing item \"{option.GiveItem}\"" dialogue.NpcId
                  if dangling shopIds.Contains option.OpenShopId then
                      error "dialogue" $"Dialogue \"{dialogue.Id}\" opens missing shop \"{option.OpenShopId}\"" dialogue.NpcId
                  if dangling questIds.Contains option.OfferQuestId then
                      error "dialogue" $"Dialogue \"{dialogue.Id}\" offers missing quest \"{option.OfferQuestId}\"" dialogue.NpcId ]

        [ // Start scene
          if not (sceneIds.Contains project.StartSceneId) then
              error "scenes" $"Start scene \"{project.StartSceneId}\" does not exist" project.StartSceneId

          // Transitions
          for scene in scenes do
              for transition in orEmpty scene.Transitions do
                  if not (sceneIds.Contains transition.ToSceneId) then
                      error "transitions"
                          $"Transition in \"{scene.Name}\" at ({Js.Num transition.FromX},{Js.Num transition.FromY}) leads to missing scene \"{transition.ToSceneId}\""
                          scene.Id
                  else
                      let target = scenes |> Seq.find (fun s -> s.Id = transition.ToSceneId)
                      if transition.ToX < 0.0 || transition.ToX >= target.Width || transition.ToY < 0.0 || transition.ToY >= target.Height then
                          error "transitions"
                              $"Transition in \"{scene.Name}\" lands out of bounds at ({Js.Num transition.ToX},{Js.Num transition.ToY}) in \"{target.Name}\""
                              scene.Id

          // Unreachable scenes (no transition in, not the start scene); generated mine floors are transient.
          for scene in scenes do
              if not (isGenerated scene) && not (reachable.Contains scene.Id) then
                  warning "scenes" $"Scene \"{scene.Name}\" is unreachable (no transition leads to it)" scene.Id

          // Dialogue references, NPC placement and schedules
          for dialogue in orEmpty project.Dialogues do
              for problem in dialogueLinks dialogue do
                  problem
          for npc in orEmpty project.Npcs do
              for dialogue in orEmpty npc.Dialogue do
                  for problem in dialogueLinks dialogue do
                      problem
              if not (sceneIds.Contains npc.SceneId) then
                  error "npcs" $"NPC \"{npc.Name}\" is placed in missing scene \"{npc.SceneId}\"" npc.Id
              for entry in orEmpty npc.Schedule do
                  if not (sceneIds.Contains entry.SceneId) then
                      error "npcs" $"NPC \"{npc.Name}\" schedule targets missing scene \"{entry.SceneId}\"" npc.Id

          // Quests
          for quest in orEmpty project.Quests do
              for prereq in orEmpty quest.Prerequisites do
                  if not (questIds.Contains prereq) then
                      error "quests" $"Quest \"{quest.Name}\" requires missing quest \"{prereq}\"" quest.Id
              for objective in orEmpty quest.Objectives do
                  if dangling itemIds.Contains objective.TargetItemId then
                      error "quests" $"Quest \"{quest.Name}\" objective targets missing item \"{objective.TargetItemId}\"" quest.Id
                  if dangling npcIds.Contains objective.TargetNpcId then
                      error "quests" $"Quest \"{quest.Name}\" objective targets missing NPC \"{objective.TargetNpcId}\"" quest.Id
                  if dangling sceneIds.Contains objective.TargetSceneId then
                      error "quests" $"Quest \"{quest.Name}\" objective targets missing scene \"{objective.TargetSceneId}\"" quest.Id
                  if dangling crops.ContainsKey objective.TargetCropType then
                      error "quests" $"Quest \"{quest.Name}\" objective targets missing crop \"{objective.TargetCropType}\"" quest.Id
              let rewards = if missing quest.Rewards then null else quest.Rewards.Items
              for reward in orEmpty rewards do
                  if not (itemIds.Contains reward.ItemId) then
                      error "quests" $"Quest \"{quest.Name}\" rewards missing item \"{reward.ItemId}\"" quest.Id

          // Events
          for event in orEmpty project.Events do
              if dangling sceneIds.Contains event.SceneId then
                  error "events" $"Event \"{event.Name}\" belongs to missing scene \"{event.SceneId}\"" event.Id
              for condition in orEmpty event.Conditions do
                  match condition with
                  | :? HasItemCondition as hasItem when not (itemIds.Contains hasItem.ItemId) ->
                      error "events" $"Event \"{event.Name}\" checks missing item \"{hasItem.ItemId}\"" event.Id
                  | :? QuestStatusCondition as questStatus when not (questIds.Contains questStatus.QuestId) ->
                      error "events" $"Event \"{event.Name}\" checks missing quest \"{questStatus.QuestId}\"" event.Id
                  | _ -> ()
              for outcome in orEmpty event.Outcomes do
                  let missingRef (types: string list) (id: string | null) (known: Set<string>) =
                      List.contains outcome.Type types && dangling known.Contains id
                  if missingRef [ "giveItem"; "takeItem" ] outcome.ItemId itemIds then
                      error "events" $"Event \"{event.Name}\" references missing item \"{outcome.ItemId}\"" event.Id
                  if missingRef [ "startQuest"; "completeQuest" ] outcome.QuestId questIds then
                      error "events" $"Event \"{event.Name}\" references missing quest \"{outcome.QuestId}\"" event.Id
                  if missingRef [ "spawnNPC"; "removeNPC"; "startDialogue" ] outcome.NpcId npcIds then
                      error "events" $"Event \"{event.Name}\" references missing NPC \"{outcome.NpcId}\"" event.Id
                  if missingRef [ "warpPlayer" ] outcome.SceneId sceneIds then
                      error "events" $"Event \"{event.Name}\" warps to missing scene \"{outcome.SceneId}\"" event.Id

          // Shops
          for shop in orEmpty project.Shops do
              for entry in orEmpty shop.Stock do
                  if not (itemIds.Contains entry.ItemId) then
                      error "shops" $"Shop \"{shop.Name}\" stocks missing item \"{entry.ItemId}\"" shop.Id

          // Node types + placed nodes, planted crops
          for nodeType in orEmpty project.NodeTypes do
              for drop in orEmpty nodeType.Drops do
                  if not (itemIds.Contains drop.ItemId) then
                      error "nodes" $"Node type \"{nodeType.Name}\" drops missing item \"{drop.ItemId}\"" nodeType.Id
          for scene in scenes do
              for row in orEmpty scene.Tiles do
                  for tile in row do
                      match Option.ofObj tile.Node with
                      | Some node when not (nodeTypeIds.Contains node.TypeId) ->
                          error "nodes" $"Scene \"{scene.Name}\" has a placed node of missing type \"{node.TypeId}\" at {at tile}" scene.Id
                      | _ -> ()
                      match Option.ofObj tile.Crop with
                      | Some crop when not (crops.ContainsKey crop.Type) ->
                          warning "crops" $"Scene \"{scene.Name}\" has a planted crop of missing type \"{crop.Type}\" at {at tile}" scene.Id
                      | Some crop when not (crops[crop.Type].Seasons.Contains(project.CurrentSeason)) ->
                          warning "crops"
                              $"Scene \"{scene.Name}\": {crops[crop.Type].Name} at {at tile} cannot grow in the starting season ({project.CurrentSeason})"
                              scene.Id
                      | _ -> ()

          // Seeds referencing missing crops
          for item in orEmpty project.Items do
              if item.Type = "seed" && dangling crops.ContainsKey item.CropType then
                  error "items" $"Seed \"{item.Name}\" references missing crop \"{item.CropType}\"" item.Id

          // Content packs (M5): load-order errors, compatibility warnings and undeclared-override
          // conflicts from a dry-run merge.
          if not (missing project.ContentPacks) && project.ContentPacks.Count > 0 then
              let _, mergedProblems = PackMerge.mergeIntoContent (ContentCompiler.baseContent project) project.ContentPacks
              for problem in mergedProblems do
                  let severity = if problem.Severity = "error" then Severity.Error else Severity.Warning
                  lint severity "packs" problem.Message problem.PackId ]
