namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// The art studio's list actions (web AssetManager.tsx): importing several images at once and
/// removing the art nothing uses. Each is one undo step.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ArtLibrary =
    /// Imported assets added as ONE undo step, in order. An asset keeps its id unless another
    /// asset (already in the project, or earlier in the batch) has it; then it gets the next free
    /// `art-N`. The web imports every file of a multi-file pick the same way.
    let importAssets (project: GameProject) (assets: CustomAsset list) : Edit =
        let taken = HashSet<string>(project.CustomAssets |> List.map (fun a -> a.Id))
        let edits =
            assets
            |> List.map (fun asset ->
                let id = if asset.Id.Length > 0 && not (taken.Contains asset.Id) then asset.Id else Defaults.nextId "art" taken
                taken.Add id |> ignore
                UpsertAsset { asset with Id = id })
        Batch("Import art", edits)

    /// The assets "Remove unused art" would delete: those nothing in the game uses
    /// (`AssetUsage.unused`), in project order.
    let unused (project: GameProject) : CustomAsset list = AssetUsage.unused project

    /// AssetManager "Remove unused art" for the whole library: every unused asset, as ONE undo
    /// step. Nothing unused → an empty batch (a no-op).
    let removeUnused (project: GameProject) : Edit =
        Batch("Remove unused art", unused project |> List.map (fun asset -> RemoveAsset asset.Id))
