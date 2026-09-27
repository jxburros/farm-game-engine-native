namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// Editor-level checks on the world: player start, scenes and doors, NPC placement and
/// schedules, the dialogue graph, the mine and the calendar. These are the checks the web
/// components make inline (TransitionEditor bounds, ProjectSettingsEditor season rules) or that
/// the LANGUAGES.md editor list asks for (unreachable dialogue nodes, dead ends).
module internal ChecksWorld =
    let private sceneTarget (context: Context) (sceneId: string) (x: float) (y: float) =
        if context.Scenes.ContainsKey sceneId then Some(NavigationTarget.Scene(sceneId, int x, int y)) else None

    let private player (context: Context) (sink: Sink) =
        let p = context.Project.Player
        if not (context.Scenes.ContainsKey p.SceneId) then
            sink.Error("player.sceneMissing", "player.sceneId", sprintf "The player starts in missing scene \"%s\"" p.SceneId, None)
        elif not (Context.tileInScene context p.SceneId p.X p.Y) then
            let scene = context.Scenes[p.SceneId]
            sink.Error("player.startOutOfBounds", "player.x",
                       sprintf "The player starts at (%g,%g), outside \"%s\" (%g×%g)" p.X p.Y scene.Name scene.Width scene.Height,
                       sceneTarget context p.SceneId 0.0 0.0)

    let private transitions (context: Context) (sink: Sink) =
        context.Project.Scenes
        |> Seq.iteri (fun s scene ->
            let seen = HashSet<float * float>()
            scene.Transitions
            |> Seq.iteri (fun t transition ->
                let path = sprintf "scenes[%d].transitions[%d]" s t
                if not (Context.tileInScene context scene.Id transition.FromX transition.FromY) then
                    sink.Error("transition.fromOutOfBounds", path + ".fromX",
                               sprintf "Transition in \"%s\" starts at (%g,%g), outside the scene" scene.Name transition.FromX transition.FromY,
                               sceneTarget context scene.Id 0.0 0.0)
                if not (seen.Add((transition.FromX, transition.FromY))) then
                    sink.Warning("transition.duplicateFrom", path,
                                 sprintf "\"%s\" has two transitions leaving (%g,%g); only the first one works" scene.Name transition.FromX transition.FromY,
                                 sceneTarget context scene.Id transition.FromX transition.FromY)
                if transition.ToSceneId = scene.Id && transition.ToX = transition.FromX && transition.ToY = transition.FromY then
                    sink.Warning("transition.leadsToItself", path,
                                 sprintf "Transition in \"%s\" at (%g,%g) leads back to the same tile" scene.Name transition.FromX transition.FromY,
                                 sceneTarget context scene.Id transition.FromX transition.FromY)))

    let private npcs (context: Context) (sink: Sink) =
        context.Project.Npcs
        |> Seq.iteri (fun i npc ->
            let path = sprintf "npcs[%d]" i
            let target = Some(NavigationTarget.Npc npc.Id)
            if context.Scenes.ContainsKey npc.SceneId && not (Context.tileInScene context npc.SceneId npc.X npc.Y) then
                sink.Error("npc.outOfBounds", path + ".x", sprintf "NPC \"%s\" stands at (%g,%g), outside its scene" npc.Name npc.X npc.Y, target)
            match npc.Schedule with
            | null -> ()
            | schedule ->
                schedule
                |> Seq.iteri (fun k entry ->
                    if context.Scenes.ContainsKey entry.SceneId && not (Context.tileInScene context entry.SceneId entry.X entry.Y) then
                        sink.Error("npc.scheduleOutOfBounds", sprintf "%s.schedule[%d].x" path k,
                                   sprintf "NPC \"%s\" schedule stop %d is at (%g,%g), outside its scene" npc.Name (k + 1) entry.X entry.Y, target))
            match npc.PatrolPoints with
            | null -> ()
            | points ->
                points
                |> Seq.iteri (fun k point ->
                    if context.Scenes.ContainsKey npc.SceneId && not (Context.tileInScene context npc.SceneId point.X point.Y) then
                        sink.Error("npc.patrolOutOfBounds", sprintf "%s.patrolPoints[%d].x" path k,
                                   sprintf "NPC \"%s\" waypoint %d is at (%g,%g), outside its scene" npc.Name (k + 1) point.X point.Y, target))
            let noWaypoints = (match npc.PatrolPoints with null -> true | points -> points.Count = 0)
            if npc.CanMove && npc.MovePattern = NpcMovePatterns.Patrol && noWaypoints then
                sink.Warning("npc.patrolWithoutWaypoints", path + ".patrolPoints", sprintf "NPC \"%s\" patrols but has no waypoints" npc.Name, target))

    /// The dialogue graph per NPC: conversations start at the first dialogue (or one an event
    /// starts by id), then follow `nextDialogueId`. Dialogues nobody can reach, dialogues with no
    /// options (the player cannot answer) and options with no text are reported.
    let private dialogues (context: Context) (sink: Sink) =
        let project = context.Project
        let startedByOutcome =
            let fromOutcomes (outcomes: seq<EventOutcome>) =
                outcomes |> Seq.filter (fun o -> o.Type = EventOutcomeTypes.StartDialogue) |> Seq.choose (fun o -> Option.ofObj o.DialogueId)
            HashSet<string>(
                Seq.concat
                    [ project.Events |> Seq.collect (fun e -> fromOutcomes e.Outcomes)
                      project.Actions |> Seq.collect (fun a -> fromOutcomes a.Outcomes)
                      project.Minigames |> Seq.collect (fun m -> m.ResultTiers |> Seq.collect (fun t -> fromOutcomes t.Outcomes)) ])
        let checkText (path: string) (owner: string) (dialogue: Dialogue) (target: NavigationTarget option) =
            if System.String.IsNullOrWhiteSpace dialogue.Text then
                sink.Warning("dialogue.emptyText", path + ".text", sprintf "Dialogue \"%s\" of %s says nothing" dialogue.Id owner, target)
            if dialogue.Options.Count = 0 then
                sink.Warning("dialogue.noOptions", path + ".options", sprintf "Dialogue \"%s\" of %s has no options, so the player cannot answer" dialogue.Id owner, target)
            dialogue.Options
            |> Seq.iteri (fun k option ->
                if System.String.IsNullOrWhiteSpace option.Text then
                    sink.Warning("dialogue.optionEmptyText", sprintf "%s.options[%d].text" path k, sprintf "Dialogue \"%s\" of %s has an option with no text" dialogue.Id owner, target))
        project.Npcs
        |> Seq.iteri (fun i npc ->
            let target = Some(NavigationTarget.Npc npc.Id)
            let byId = Dictionary<string, Dialogue>()
            for d in npc.Dialogue do
                if not (byId.ContainsKey d.Id) then byId[d.Id] <- d
            let reachable = HashSet<string>()
            let queue = Queue<string>()
            let start (id: string) =
                if byId.ContainsKey id && reachable.Add id then queue.Enqueue id
            match Seq.tryHead npc.Dialogue with
            | Some first -> start first.Id
            | None -> ()
            for id in startedByOutcome do start id
            while queue.Count > 0 do
                let current = byId[queue.Dequeue()]
                for option in current.Options do
                    match option.NextDialogueId with
                    | null -> ()
                    | next -> start next
            npc.Dialogue
            |> Seq.iteri (fun d dialogue ->
                let path = sprintf "npcs[%d].dialogue[%d]" i d
                if not (reachable.Contains dialogue.Id) then
                    sink.Warning("dialogue.unreachable", path, sprintf "Dialogue \"%s\" of %s can never be reached (no option or event leads to it)" dialogue.Id npc.Name, target)
                checkText path npc.Name dialogue target))
        // The flat list must mirror the NPC lists (NPCEditor keeps both).
        let onNpcs = HashSet<string>(project.Npcs |> Seq.collect (fun n -> n.Dialogue |> Seq.map (fun d -> d.Id)))
        project.Dialogues
        |> Seq.iteri (fun d dialogue ->
            let path = sprintf "dialogues[%d]" d
            if not (context.NpcIds.Contains dialogue.NpcId) then
                sink.Error("dialogue.npcMissing", path + ".npcId", sprintf "Dialogue \"%s\" belongs to missing NPC \"%s\"" dialogue.Id dialogue.NpcId, None)
            elif not (onNpcs.Contains dialogue.Id) then
                sink.Warning("dialogue.notOnNpc", path, sprintf "Dialogue \"%s\" is in the project list but not on NPC \"%s\"" dialogue.Id dialogue.NpcId, Some(NavigationTarget.Npc dialogue.NpcId)))
        let inProject = HashSet<string>(project.Dialogues |> Seq.map (fun d -> d.Id))
        project.Npcs
        |> Seq.iteri (fun i npc ->
            npc.Dialogue
            |> Seq.iteri (fun d dialogue ->
                if not (inProject.Contains dialogue.Id) then
                    sink.Warning("dialogue.notInProject", sprintf "npcs[%d].dialogue[%d]" i d, sprintf "Dialogue \"%s\" of %s is missing from the project dialogue list" dialogue.Id npc.Name, Some(NavigationTarget.Npc npc.Id))))

    let private mine (context: Context) (sink: Sink) =
        let mine = context.Project.Mine
        let settings = Some NavigationTarget.Settings
        if mine.Enabled then
            match mine.EntranceSceneId with
            | null -> sink.Warning("mine.noEntrance", "mine.entranceSceneId", "The mine is enabled but has no entrance scene", settings)
            | sceneId when not (context.Scenes.ContainsKey sceneId) ->
                sink.Error("mine.entranceSceneMissing", "mine.entranceSceneId", sprintf "The mine entrance is in missing scene \"%s\"" sceneId, settings)
            | sceneId ->
                let x = if mine.EntranceX.HasValue then mine.EntranceX.Value else 0.0
                let y = if mine.EntranceY.HasValue then mine.EntranceY.Value else 0.0
                if not (Context.tileInScene context sceneId x y) then
                    sink.Error("mine.entranceOutOfBounds", "mine.entranceX", sprintf "The mine entrance (%g,%g) is outside its scene" x y, sceneTarget context sceneId 0.0 0.0)
            if mine.Bands.Count = 0 then
                sink.Warning("mine.noBands", "mine.bands", "The mine is enabled but has no depth bands, so floors have no rocks", settings)
        mine.Bands
        |> Seq.iteri (fun b band ->
            let path = sprintf "mine.bands[%d]" b
            if band.FromFloor > band.ToFloor then
                sink.Error("mine.bandRange", path + ".fromFloor", sprintf "Mine band %d starts below its end (%g > %g)" (b + 1) band.FromFloor band.ToFloor, settings)
            band.Rocks
            |> Seq.iteri (fun r rock ->
                if not (context.NodeTypeIds.Contains rock.NodeTypeId) then
                    sink.Error("mine.bandUnknownNodeType", sprintf "%s.rocks[%d].nodeTypeId" path r, sprintf "Mine band %d spawns missing node type \"%s\"" (b + 1) rock.NodeTypeId, settings)))

    let private calendar (context: Context) (sink: Sink) =
        let project = context.Project
        let calendar = project.Settings.Calendar
        let settings = Some NavigationTarget.Settings
        let seen = HashSet<string>()
        calendar.Seasons
        |> Seq.iteri (fun i season ->
            if not (seen.Add season.Id) then
                sink.Error("calendar.duplicateSeason", sprintf "settings.calendar.seasons[%d].id" i, sprintf "Duplicate season id \"%s\"" season.Id, settings))
        let festivalIds = HashSet<string>()
        calendar.Festivals
        |> Seq.iteri (fun i festival ->
            let path = sprintf "settings.calendar.festivals[%d]" i
            if not (festivalIds.Add festival.Id) then
                sink.Error("calendar.duplicateFestival", path + ".id", sprintf "Duplicate festival id \"%s\"" festival.Id, settings)
            match calendar.Seasons |> Seq.tryFind (fun s -> s.Id = festival.SeasonId) with
            | None -> sink.Error("calendar.festivalSeasonUnknown", path + ".seasonId", sprintf "Festival \"%s\" is in missing season \"%s\"" festival.Name festival.SeasonId, settings)
            | Some season ->
                if festival.Day < 1.0 || festival.Day > season.Days then
                    sink.Error("calendar.festivalDayOutOfRange", path + ".day", sprintf "Festival \"%s\" is on day %g but %s has %g days" festival.Name festival.Day season.Name season.Days, settings))
        if context.SeasonIds.Count > 0 && not (context.SeasonIds.Contains project.CurrentSeason) then
            sink.Warning("project.currentSeasonUnknown", "currentSeason", sprintf "The starting season \"%s\" is not in the calendar" project.CurrentSeason, settings)
        let weatherSeen = HashSet<string>()
        project.Weather.Types
        |> Seq.iteri (fun i weather ->
            if not (weatherSeen.Add weather.Id) then
                sink.Error("weather.duplicateType", sprintf "weather.types[%d].id" i, sprintf "Duplicate weather id \"%s\"" weather.Id, settings))
        for KeyValue(season, entries) in project.Weather.Table do
            if not (context.SeasonIds.Contains season) then
                sink.Warning("weather.tableUnknownSeason", sprintf "weather.table.%s" season, sprintf "The weather table has a row for unknown season \"%s\"" season, settings)
            entries
            |> Seq.iteri (fun k entry ->
                if not (context.WeatherIds.Contains entry.WeatherId) then
                    sink.Error("weather.tableUnknownType", sprintf "weather.table.%s[%d].weatherId" season k, sprintf "The %s weather table rolls missing weather \"%s\"" season entry.WeatherId, settings))

    let run (context: Context) (sink: Sink) =
        player context sink
        transitions context sink
        npcs context sink
        dialogues context sink
        mine context sink
        calendar context sink
