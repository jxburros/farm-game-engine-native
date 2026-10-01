namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// The project-level edits (ProjectSettingsEditor.tsx, AssetManager.tsx, ArtBindings.tsx,
/// ModsEditor.tsx, imports). Each function returns the same instance when nothing changed.
module internal EditProject =
    /// Project name and version (the header fields of the web project manager).
    let setProjectInfo (name: string) (version: string) (project: GameProject) =
        let name = if System.String.IsNullOrWhiteSpace name then project.Name else name.Trim()
        if project.Name = name && project.Version = version then project
        else { project with Name = name; Version = version }

    /// ProjectSettingsEditor `update` / `updateTime` / `updateCalendar`: the whole settings record.
    let setSettings (settings: ProjectSettings) (project: GameProject) =
        if project.Settings = settings then project else { project with Settings = settings }

    let setExportSettings (settings: ExportSettings option) (project: GameProject) =
        if project.Export = settings then project else { project with Export = settings }

    /// ProjectSettingsEditor `removeSeason`: refused for the last season; festivals on it go too.
    let removeSeason (seasonId: string) (project: GameProject) =
        let calendar = project.Settings.Calendar
        if calendar.Seasons.Length <= 1 then project
        else
            match Lists.removeBy (fun (s: CalendarSeason) -> s.Id) seasonId calendar.Seasons with
            | None -> project
            | Some seasons ->
                { project with Settings = { project.Settings with Calendar = { calendar with Seasons = seasons } } }
                |> Cleanup.dropSeason seasonId

    /// ProjectSettingsEditor `moveSeason`: swaps with the season `delta` places away. Everything
    /// else names seasons by id, so nothing else changes.
    let moveSeason (seasonId: string) (delta: int) (project: GameProject) =
        let calendar = project.Settings.Calendar
        let seasons = Array.ofList calendar.Seasons
        match Array.tryFindIndex (fun (s: CalendarSeason) -> s.Id = seasonId) seasons with
        | Some index when delta <> 0 && index + delta >= 0 && index + delta < seasons.Length ->
            let target = index + delta
            let swapped = Array.copy seasons
            swapped.[index] <- seasons.[target]
            swapped.[target] <- seasons.[index]
            { project with Settings = { project.Settings with Calendar = { calendar with Seasons = List.ofArray swapped } } }
        | _ -> project

    let setGraphics (graphics: GraphicsSettings) (project: GameProject) =
        if project.Graphics = Some graphics then project else { project with Graphics = Some graphics }

    let setPlayerVisual (visual: VisualRef option) (project: GameProject) =
        if project.PlayerVisual = visual then project else { project with PlayerVisual = visual }

    /// ArtBindings `apply`: artwork on the player or a definition. Items also refresh their dropped copies.
    let bindVisual (target: VisualTarget) (visual: VisualRef option) (project: GameProject) =
        let bindIn (items: 'T list) (idOf: 'T -> string) (currentOf: 'T -> VisualRef option) (set: 'T -> 'T) (id: string) : 'T list option =
            Lists.mapChanged (fun item -> if idOf item = id && currentOf item <> visual then set item else item) items
        let apply (next: 'T list option) (write: 'T list -> GameProject) = match next with Some l -> write l | None -> project
        match target with
        | PlayerVisual -> setPlayerVisual visual project
        | NpcVisual id ->
            apply (bindIn project.Npcs (fun n -> n.Id) (fun n -> n.Visual) (fun n -> { n with Visual = visual }) id) (fun l -> { project with Npcs = l })
        | ItemVisual id ->
            apply (bindIn project.Items (fun i -> i.Id) (fun i -> i.Visual) (fun i -> { i with Visual = visual }) id) (fun l -> { project with Items = l })
            |> Proj.mapScenes (Proj.mapTiles (fun tile ->
                match tile.Item with
                | Some item when item.Id = id && item.Visual <> visual -> { tile with Item = Some { item with Visual = visual } }
                | _ -> tile))
        | CropVisual id ->
            match project.CustomCrops with
            | None -> project
            | Some crops ->
                apply (bindIn crops (fun c -> c.Id) (fun c -> c.Visual) (fun c -> { c with Visual = visual }) id) (fun l -> { project with CustomCrops = Some l })
        | NodeTypeVisual id ->
            apply (bindIn project.NodeTypes (fun n -> n.Id) (fun n -> n.Visual) (fun n -> { n with Visual = visual }) id) (fun l -> { project with NodeTypes = l })
        | AnimalSpeciesVisual id ->
            apply (bindIn project.AnimalSpecies (fun s -> s.Id) (fun s -> s.Visual) (fun s -> { s with Visual = visual }) id) (fun l -> { project with AnimalSpecies = l })
        | MachineTypeVisual id ->
            apply (bindIn project.MachineTypes (fun m -> m.Id) (fun m -> m.Visual) (fun m -> { m with Visual = visual }) id) (fun l -> { project with MachineTypes = l })

    /// AssetManager `upload` / `update`: add or replace an asset by id.
    let upsertAsset (asset: CustomAsset) (project: GameProject) =
        match Lists.upsertBy (fun (a: CustomAsset) -> a.Id) asset project.CustomAssets with
        | Some assets -> { project with CustomAssets = assets }
        | None -> project

    /// Replace the frames of one clip of one asset; the same project when nothing changed.
    let private mapClipFrames (assetId: string) (clipName: string) (f: ArtFrame list -> ArtFrame list option) (project: GameProject) =
        let mapClip (clip: AnimationClip) =
            if clip.Name <> clipName then clip
            else
                match f clip.Frames with
                | Some frames -> { clip with Frames = frames }
                | None -> clip
        let mapAsset (asset: CustomAsset) =
            match asset.Animations with
            | Some clips when asset.Id = assetId ->
                match Lists.mapChanged mapClip clips with
                | Some next -> { asset with Animations = Some next }
                | None -> asset
            | _ -> asset
        match Lists.mapChanged mapAsset project.CustomAssets with
        | Some assets -> { project with CustomAssets = assets }
        | None -> project

    /// AssetManager frame duration input (`Math.max(1, …)`): one frame, or all frames of the clip.
    let setFrameTicks (assetId: string) (clipName: string) (frame: int option) (ticks: int) (project: GameProject) =
        let ticks = float (max 1 ticks)
        mapClipFrames assetId clipName (fun frames ->
            let retime index (f: ArtFrame) =
                if (match frame with Some i -> i = index | None -> true) && f.Ticks <> ticks then { f with Ticks = ticks } else f
            let next = List.mapi retime frames
            if next = frames then None else Some next) project

    /// Duplicate a frame in place: the copy follows the original. Refused at the 1024-frame limit.
    let duplicateFrame (assetId: string) (clipName: string) (frame: int) (project: GameProject) =
        mapClipFrames assetId clipName (fun frames ->
            if frame < 0 || frame >= frames.Length || frames.Length >= 1024 then None
            else Some(List.insertAt (frame + 1) frames.[frame] frames)) project

    /// AssetManager "Remove unused art", made safe for art in use: bindings fall back to the default look.
    let removeAsset (assetId: string) (project: GameProject) =
        match Lists.removeBy (fun (a: CustomAsset) -> a.Id) assetId project.CustomAssets with
        | None -> project
        | Some kept -> { project with CustomAssets = kept } |> Cleanup.dropAsset assetId

    let private packId (install: PackInstallation) = install.Pack.Manifest.Id

    /// ModsEditor `confirmPackInstall`: appended enabled; a pack id already installed is refused.
    /// Installs a pack (one undo step). Its art joins the project's custom assets
    /// (`PackMerge.mergeAssets`), where the game, the previews and the Art tab find it, and the
    /// installed copy keeps no second copy of the images.
    let installPack (pack: ContentPack) (project: GameProject) =
        if project.ContentPacks |> List.exists (fun i -> packId i = pack.Manifest.Id) then project
        else
            // A pack without contentInject brings no content, its art included.
            let project = if pack.Manifest.Permissions.ContentInject then fst (PackMerge.mergeAssets project pack) else project
            { project with ContentPacks = Lists.append { Pack = PackRules.withoutAssets pack; Enabled = true } project.ContentPacks }

    let setPackEnabled (id: string) (enabled: bool) (project: GameProject) =
        let toggle (install: PackInstallation) =
            if packId install <> id || install.Enabled = enabled then install else { install with Enabled = enabled }
        match Lists.mapChanged toggle project.ContentPacks with
        | Some packs -> { project with ContentPacks = packs }
        | None -> project

    /// ModsEditor `move`: load order. Listed ids come first in that order; the rest keep theirs.
    /// Each listed id takes the next install with that id not taken yet, so two installs sharing
    /// an id (which Problems reports) are both kept: listing the id twice moves both, once moves
    /// the first. Unknown ids are ignored.
    let reorderPacks (ids: string list) (project: GameProject) =
        let installs = Array.ofList project.ContentPacks
        let taken = Array.create installs.Length false
        let listed =
            ids
            |> List.choose (fun id ->
                match Seq.tryFind (fun k -> not taken.[k] && packId installs.[k] = id) (seq { 0 .. installs.Length - 1 }) with
                | Some k ->
                    taken.[k] <- true
                    Some installs.[k]
                | None -> None)
        let rest = installs |> Array.indexed |> Array.filter (fun (k, _) -> not taken.[k]) |> Array.map snd |> List.ofArray
        let next = listed @ rest
        let unchanged =
            next.Length = installs.Length
            && List.forall2 (fun (a: PackInstallation) (b: PackInstallation) -> LanguagePrimitives.PhysicalEquality a b) next (List.ofArray installs)
        if unchanged then project else { project with ContentPacks = next }

    let removePack (id: string) (project: GameProject) =
        match Lists.removeBy packId id project.ContentPacks with
        | Some packs -> { project with ContentPacks = packs }
        | None -> project

    /// ModsEditor "Import into project": `applyPackToProject`, then the layer is removed.
    let importPack (id: string) (project: GameProject) =
        match project.ContentPacks |> List.tryFind (fun i -> packId i = id) with
        | None -> project
        | Some install ->
            let imported, _ = PackMerge.applyToProject project install.Pack
            removePack id imported

    /// Imports and kept playtests: the whole project.
    let replaceProject (next: GameProject) (project: GameProject) =
        if LanguagePrimitives.PhysicalEquality next project then project else next
