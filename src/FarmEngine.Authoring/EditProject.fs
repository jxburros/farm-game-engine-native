namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Core
open FarmEngine.Schemas

/// The project-level edits (ProjectSettingsEditor.tsx, AssetManager.tsx, ArtBindings.tsx,
/// ModsEditor.tsx, imports). Each function returns the same instance when nothing changed.
module internal EditProject =
    let private setField (record: 'T) (name: string) (value: objnull) : 'T = Records.withValue record name value

    let private visualValue (visual: VisualRef option) : objnull =
        match visual with
        | Some v -> box v
        | None -> null

    /// Project name and version (the header fields of the web project manager).
    let setProjectInfo (name: string) (version: string) (project: GameProject) =
        let name = if System.String.IsNullOrWhiteSpace name then project.Name else name.Trim()
        if project.Name = name && project.Version = version then project
        else Proj.setMany [ ("Name", box name); ("Version", box version) ] project

    /// ProjectSettingsEditor `update` / `updateTime` / `updateCalendar`: the whole settings record.
    let setSettings (settings: ProjectSettings) (project: GameProject) =
        if obj.Equals(project.Settings, settings) then project else Proj.set "Settings" (box settings) project

    /// ProjectSettingsEditor `removeSeason`: refused for the last season; festivals on it go too.
    let removeSeason (seasonId: string) (project: GameProject) =
        let calendar = project.Settings.Calendar
        if calendar.Seasons.Count <= 1 then project
        else
            match Lists.removeBy (fun (s: CalendarSeason) -> s.Id) seasonId calendar.Seasons with
            | None -> project
            | Some seasons ->
                let settings = setField project.Settings "Calendar" (box (setField calendar "Seasons" (box seasons)))
                Proj.set "Settings" (box settings) project |> Cleanup.dropSeason seasonId

    let setGraphics (graphics: GraphicsSettings) (project: GameProject) =
        if obj.Equals(project.Graphics, graphics) then project else Proj.set "Graphics" (box graphics) project

    let setPlayerVisual (visual: VisualRef option) (project: GameProject) =
        let value = visualValue visual
        if obj.Equals(project.PlayerVisual, value) then project else Proj.set "PlayerVisual" value project

    /// ArtBindings `apply`: artwork on the player or a definition. Items also refresh their dropped copies.
    let bindVisual (target: VisualTarget) (visual: VisualRef option) (project: GameProject) =
        let value = visualValue visual
        let bind (record: 'T) (current: VisualRef | null) : 'T =
            if obj.Equals(current, value) then record else setField record "Visual" value
        let inList (name: string) (idOf: 'T -> string) (currentOf: 'T -> VisualRef | null) (id: string) (list: List<'T>) =
            Proj.update name (Lists.mapChanged (fun item -> if idOf item = id then bind item (currentOf item) else item) list) project
        match target with
        | PlayerVisual -> setPlayerVisual visual project
        | NpcVisual id -> inList "Npcs" (fun (n: Npc) -> n.Id) (fun n -> n.Visual) id project.Npcs
        | ItemVisual id ->
            inList "Items" (fun (i: Item) -> i.Id) (fun i -> i.Visual) id project.Items
            |> Proj.mapScenes (Proj.mapTiles (fun tile ->
                match tile.Item with
                | null -> tile
                | item -> if item.Id = id && not (obj.Equals(item.Visual, value)) then setField tile "Item" (box (setField item "Visual" value)) else tile))
        | CropVisual id ->
            match project.CustomCrops with
            | null -> project
            | crops -> inList "CustomCrops" (fun (c: CustomCropDefinition) -> c.Id) (fun c -> c.Visual) id crops
        | NodeTypeVisual id -> inList "NodeTypes" (fun (n: NodeTypeDefinition) -> n.Id) (fun n -> n.Visual) id project.NodeTypes
        | AnimalSpeciesVisual id -> inList "AnimalSpecies" (fun (s: AnimalSpeciesDefinition) -> s.Id) (fun s -> s.Visual) id project.AnimalSpecies
        | MachineTypeVisual id -> inList "MachineTypes" (fun (m: MachineTypeDefinition) -> m.Id) (fun m -> m.Visual) id project.MachineTypes

    /// AssetManager `upload` / `update`: add or replace an asset by id.
    let upsertAsset (asset: CustomAsset) (project: GameProject) =
        Proj.update "CustomAssets" (Lists.upsertBy (fun (a: CustomAsset) -> a.Id) asset project.CustomAssets) project

    /// AssetManager "Remove unused art", made safe for art in use: bindings fall back to the default look.
    let removeAsset (assetId: string) (project: GameProject) =
        match Lists.removeBy (fun (a: CustomAsset) -> a.Id) assetId project.CustomAssets with
        | None -> project
        | Some kept -> Proj.set "CustomAssets" (box kept) project |> Cleanup.dropAsset assetId

    let private packId (install: PackInstallation) = install.Pack.Manifest.Id

    /// ModsEditor `confirmPackInstall`: appended enabled; a pack id already installed is refused.
    let installPack (pack: ContentPack) (project: GameProject) =
        if project.ContentPacks |> Seq.exists (fun i -> packId i = pack.Manifest.Id) then project
        else Proj.set "ContentPacks" (box (Lists.append (PackInstallation(Pack = pack, Enabled = true)) project.ContentPacks)) project

    let setPackEnabled (id: string) (enabled: bool) (project: GameProject) =
        let toggle (install: PackInstallation) =
            if packId install <> id || install.Enabled = enabled then install else setField install "Enabled" (box enabled)
        Proj.update "ContentPacks" (Lists.mapChanged toggle project.ContentPacks) project

    /// ModsEditor `move`: load order. Listed ids come first in that order; the rest keep theirs.
    let reorderPacks (ids: string list) (project: GameProject) =
        let installs = List.ofSeq project.ContentPacks
        let listed = ids |> List.choose (fun id -> installs |> List.tryFind (fun i -> packId i = id))
        let listedIds = HashSet<string>(listed |> List.map packId)
        let rest = installs |> List.filter (fun i -> not (listedIds.Contains(packId i)))
        let next = listed @ rest
        if List.forall2 (fun (a: PackInstallation) (b: PackInstallation) -> obj.ReferenceEquals(a, b)) next installs then project
        else Proj.set "ContentPacks" (box (Lists.ofSeq next)) project

    let removePack (id: string) (project: GameProject) =
        Proj.update "ContentPacks" (Lists.removeBy packId id project.ContentPacks) project

    /// ModsEditor "Import into project": `applyPackToProject`, then the layer is removed.
    let importPack (id: string) (project: GameProject) =
        match project.ContentPacks |> Seq.tryFind (fun i -> packId i = id) with
        | None -> project
        | Some install ->
            let imported, _ = PackMerge.applyToProject project install.Pack
            removePack id imported

    /// Imports and kept playtests: the whole project.
    let replaceProject (next: GameProject) (project: GameProject) =
        if obj.ReferenceEquals(next, project) then project else next
