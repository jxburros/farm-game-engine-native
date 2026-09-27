namespace FarmEngine.Authoring

open System
open FarmEngine.Schemas

/// The Problems pipeline (ProblemsPanel.tsx): the schema checks (`SchemaChecks`) and the content
/// lints (`ContentLints`) mapped to `Problem`s with JSON paths and navigation targets, plus the editor-level checks in
/// `ChecksWorld` and `ChecksContent`.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Problems =
    let private isIndex (segment: string) = segment.Length > 0 && segment |> Seq.forall Char.IsDigit

    /// zod's dotted issue path (`scenes.0.tiles.1.2.background`) as a JSON path (`scenes[0].tiles[1][2].background`).
    let jsonPath (zodPath: string) : string =
        let builder = Text.StringBuilder()
        for segment in zodPath.Split('.') do
            if isIndex segment then builder.Append('[').Append(segment).Append(']') |> ignore
            elif builder.Length = 0 then builder.Append segment |> ignore
            else builder.Append('.').Append(segment) |> ignore
        builder.ToString()

    let private idAt (list: Collections.Generic.List<'T>) (idOf: 'T -> string) (index: int) : string option =
        if index >= 0 && index < list.Count then Some(idOf list[index]) else None

    let private tryIndex (segments: string list) (position: int) : int option =
        match List.tryItem position segments with
        | Some s when isIndex s -> Some(int s)
        | _ -> None

    /// Where a schema error points, from its path segments.
    let private schemaTarget (project: GameProject) (segments: string list) : NavigationTarget option =
        let item (list: Collections.Generic.List<'T>) (idOf: 'T -> string) (make: string -> NavigationTarget) =
            tryIndex segments 1 |> Option.bind (idAt list idOf) |> Option.map make
        match segments with
        | "scenes" :: _ ->
            match tryIndex segments 1 with
            | Some s when s < project.Scenes.Count ->
                let scene = project.Scenes[s]
                match List.tryItem 2 segments, tryIndex segments 3, tryIndex segments 4 with
                | Some "tiles", Some y, Some x -> Some(NavigationTarget.Scene(scene.Id, x, y))
                | Some "transitions", Some t, _ when t < scene.Transitions.Count ->
                    Some(NavigationTarget.Scene(scene.Id, int scene.Transitions[t].FromX, int scene.Transitions[t].FromY))
                | _ -> Some(NavigationTarget.Scene(scene.Id, 0, 0))
            | _ -> None
        | "npcs" :: _ -> item project.Npcs (fun n -> n.Id) NavigationTarget.Npc
        | "items" :: _ -> item project.Items (fun i -> i.Id) NavigationTarget.Item
        | "quests" :: _ -> item project.Quests (fun q -> q.Id) NavigationTarget.Quest
        | "events" :: _ -> item project.Events (fun e -> e.Id) NavigationTarget.Event
        | "shops" :: _ -> item project.Shops (fun s -> s.Id) NavigationTarget.Shop
        | "recipes" :: _ -> item project.Recipes (fun r -> r.Id) NavigationTarget.Recipe
        | "nodeTypes" :: _ -> item project.NodeTypes (fun n -> n.Id) NavigationTarget.NodeType
        | "machineTypes" :: _ -> item project.MachineTypes (fun m -> m.Id) NavigationTarget.MachineType
        | "animalSpecies" :: _ -> item project.AnimalSpecies (fun s -> s.Id) NavigationTarget.AnimalSpecies
        | "animals" :: _ -> item project.Animals (fun a -> a.SpeciesId) NavigationTarget.AnimalSpecies
        | "fishTables" :: _ -> item project.FishTables (fun f -> f.Id) NavigationTarget.FishTable
        | "actions" :: _ -> item project.Actions (fun a -> a.Id) NavigationTarget.Action
        | "minigames" :: _ -> item project.Minigames (fun m -> m.Id) NavigationTarget.Minigame
        | "contentPacks" :: _ -> item project.ContentPacks (fun p -> p.Pack.Manifest.Id) NavigationTarget.Pack
        | "customAssets" :: _ -> item project.CustomAssets (fun a -> a.Id) NavigationTarget.Asset
        | "customCrops" :: _ ->
            match project.CustomCrops with
            | null -> None
            | crops -> item crops (fun c -> c.Id) NavigationTarget.Crop
        | "player" :: _ -> if project.Scenes |> Seq.exists (fun s -> s.Id = project.Player.SceneId) then Some(NavigationTarget.Scene(project.Player.SceneId, int project.Player.X, int project.Player.Y)) else None
        | "settings" :: _ | "mine" :: _ | "weather" :: _ | "startSceneId" :: _ | "currentYear" :: _ | "mode" :: _ | "selectedTileType" :: _
        | "schemaVersion" :: _ | "id" :: _ | "rngState" :: _ | "mineDeepestFloor" :: _ -> Some NavigationTarget.Settings
        | _ -> None

    /// `SchemaChecks.projectIssues`: zod refinement failures, all errors.
    let private fromSchema (project: GameProject) (sink: Sink) =
        for issue in SchemaChecks.projectIssues project do
            let segments = issue.Path.Split('.') |> List.ofArray
            sink.Error("schema." + (List.head segments), jsonPath issue.Path, issue.Message, schemaTarget project segments)

    let private indexOf (list: Collections.Generic.List<'T>) (idOf: 'T -> string) (id: string | null) : int option =
        match id with
        | null -> None
        | id ->
            match Seq.tryFindIndex (fun x -> idOf x = id) list with
            | Some i -> Some i
            | None -> None

    /// `ContentLints.validateProjectContent`: the web `validateProjectContent` lints, whose
    /// `subject` is an id. The path is the list entry the subject names.
    let private fromContent (project: GameProject) (sink: Sink) =
        for problem in ContentLints.validateProjectContent project do
            let severity = problem.Severity
            let subject = Option.toObj problem.Subject
            let entry (name: string) (list: Collections.Generic.List<'T>) (idOf: 'T -> string) (make: string -> NavigationTarget) =
                match indexOf list idOf subject with
                | Some i -> sprintf "%s[%d]" name i, Some(make (idOf list[i]))
                | None -> name, None
            let sceneEntry (suffix: string) =
                match indexOf project.Scenes (fun s -> s.Id) subject with
                | Some i -> sprintf "scenes[%d]%s" i suffix, Some(NavigationTarget.Scene(project.Scenes[i].Id, 0, 0))
                | None -> "scenes", None
            let path, target =
                match problem.Category with
                | "scenes" -> if problem.Message.StartsWith("Start scene", StringComparison.Ordinal) then "startSceneId", Some NavigationTarget.Settings else sceneEntry ""
                | "transitions" -> sceneEntry ".transitions"
                | "crops" -> sceneEntry ".tiles"
                | "nodes" ->
                    match indexOf project.NodeTypes (fun n -> n.Id) subject with
                    | Some i -> sprintf "nodeTypes[%d].drops" i, Some(NavigationTarget.NodeType project.NodeTypes[i].Id)
                    | None -> sceneEntry ".tiles"
                | "dialogue" -> entry "npcs" project.Npcs (fun n -> n.Id) NavigationTarget.Npc |> fun (p, t) -> p + ".dialogue", t
                | "npcs" -> entry "npcs" project.Npcs (fun n -> n.Id) NavigationTarget.Npc
                | "quests" -> entry "quests" project.Quests (fun q -> q.Id) NavigationTarget.Quest
                | "events" -> entry "events" project.Events (fun e -> e.Id) NavigationTarget.Event
                | "shops" -> entry "shops" project.Shops (fun s -> s.Id) NavigationTarget.Shop
                | "items" -> entry "items" project.Items (fun i -> i.Id) NavigationTarget.Item
                | "packs" -> entry "contentPacks" project.ContentPacks (fun p -> p.Pack.Manifest.Id) NavigationTarget.Pack
                | other -> other, None
            sink.Add(severity, "content." + problem.Category, path, problem.Message, target)

    /// Everything the Problems panel shows for a project, in a stable order: schema errors,
    /// content lints, then the editor checks.
    let collect (project: GameProject) : Problem list =
        let sink = Sink()
        fromSchema project sink
        fromContent project sink
        let context = Context.create project
        ChecksWorld.run context sink
        ChecksContent.run context sink
        sink.ToList()

    let errors (problems: Problem list) = problems |> List.filter (fun p -> p.Severity = Severity.Error)
    let warnings (problems: Problem list) = problems |> List.filter (fun p -> p.Severity = Severity.Warning)

    /// Export is refused while any error remains (warnings and infos are advice).
    let blocksExport (problems: Problem list) = problems |> List.exists (fun p -> p.Severity = Severity.Error)
