namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// Every change a creator makes to a project (docs/LANGUAGES.md "F# conventions"). The editor
/// never touches a `GameProject` directly: it builds an `Edit` and hands it to `Document.apply`,
/// which gives undo/redo and autosave to every editor for free. `Batch` groups edits into one
/// undo step.
type Edit =
    /// Paint one tile layer (background/overlay/object) on `cells` of a scene with a tile type.
    | PaintTiles of sceneId: string * cells: (int * int) list * tileType: string
    /// A group of edits applied as ONE undo step (drag strokes, workshop patterns).
    | Batch of label: string * edits: Edit list
