namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// A tile's three art layers (web `classifyTileType`: path → overlay, wall/door → object, the
/// rest → background).
type TileLayer =
    | Background
    | Overlay
    | Object

/// What an art binding attaches to (web `ArtBindings.tsx` target select).
type VisualTarget =
    | PlayerVisual
    | NpcVisual of npcId: string
    | ItemVisual of itemId: string
    | CropVisual of cropId: string
    | NodeTypeVisual of nodeTypeId: string
    | AnimalSpeciesVisual of speciesId: string
    | MachineTypeVisual of machineTypeId: string

/// Every change a creator makes to a project (docs/LANGUAGES.md "F# conventions"). The editor
/// never touches a `GameProject` directly: it builds an `Edit` and hands it to `Document.apply`,
/// which gives undo/redo and autosave to every editor for free. `Batch` groups edits into one
/// undo step. Each case names what the creator does; the web component it ports is in the doc
/// comment. Removals also drop the references the removed thing left behind (see `Cleanup`), so
/// a project never gains dangling ids from an edit.
type Edit =
    // ---- Tiles and scenes (EditorPanel.tsx tile painter, App.tsx handleTileClick, SceneManager.tsx, TransitionEditor.tsx) ----
    /// Paint one layer of the given cells with a tile type (web single brush; clears crop and node).
    | PaintTiles of sceneId: string * layer: TileLayer * cells: (int * int) list * tileType: string
    /// Clear a layer on the given cells (background resets to grass, overlay/object to nothing).
    | EraseLayer of sceneId: string * layer: TileLayer * cells: (int * int) list
    /// Paint an inclusive rectangle, clamped to the scene (web `paintRect`).
    | FillRect of sceneId: string * layer: TileLayer * x0: int * y0: int * x1: int * y1: int * tileType: string
    /// Flood-fill the contiguous region sharing the clicked tile's type (web `floodFill`, 4-directional).
    | FloodFill of sceneId: string * layer: TileLayer * x: int * y: int * tileType: string
    /// Stamp a copied region with its top-left at (x, y), clamped (web `pasteTileRegion`).
    | PasteTiles of sceneId: string * x: int * y: int * tiles: Tile list list
    /// Set or clear collision on cells.
    | SetCollision of sceneId: string * cells: (int * int) list * blocked: bool
    /// Per-layer art override on cells (web brush with a `selectedTileVisual`); `None` clears it.
    | SetTileVisual of sceneId: string * layer: TileLayer * cells: (int * int) list * visual: VisualRef option
    /// Place a gathering node of a built-in or project node type (web node placement mode).
    | PlaceNode of sceneId: string * x: int * y: int * nodeTypeId: string
    | RemoveNode of sceneId: string * x: int * y: int
    /// Drop an item on a tile (web item placement mode).
    | PlaceItem of sceneId: string * x: int * y: int * item: Item
    | RemovePlacedItem of sceneId: string * x: int * y: int
    /// Place a machine of a machine type on a tile.
    | PlaceMachine of sceneId: string * x: int * y: int * machineTypeId: string
    | RemoveMachine of sceneId: string * x: int * y: int
    /// The map's Remove tool: the tile's crop, node, item and machine and every animal standing
    /// on it. NPCs stay: they are moved on the map, never deleted from it.
    | ClearTile of sceneId: string * x: int * y: int
    /// EditorPanel "Clear Items": drop every crop and item in the scene.
    | ClearCropsAndItems of sceneId: string
    /// EditorPanel "Reset Soil": every soil tile becomes dry grass without a crop.
    | ResetSoil of sceneId: string
    /// Fill a whole scene with one tile type, dropping crops and items (SceneManager `fillScene`).
    | FillScene of sceneId: string * tileType: string
    | AddScene of scene: Scene
    /// SceneManager `deleteScene`: refused (no-op) for the last scene and for the start scene.
    | RemoveScene of sceneId: string
    | RenameScene of sceneId: string * name: string
    /// Whether the scene keeps the weather out (rain, storms): greenhouses, interiors.
    | SetSceneIndoor of sceneId: string * indoor: bool
    /// SceneManager `applyResize`: the overlap keeps its tiles, new tiles are grass.
    | ResizeScene of sceneId: string * width: int * height: int
    /// SceneManager `duplicateScene`: a deep copy with a new id, "(Copy)" name and no NPC list.
    | DuplicateScene of sceneId: string * newSceneId: string
    /// TransitionEditor create/update: replaces the transition leaving the same tile, else appends.
    | SetTransition of sceneId: string * transition: SceneTransition
    | RemoveTransition of sceneId: string * fromX: int * fromY: int
    | ClearTransitions of sceneId: string
    | SetStartScene of sceneId: string
    /// Player start position (SceneManager "Switch To" and the NPC/player placement modes).
    | SetPlayerStart of sceneId: string * x: int * y: int
    /// The tile brush (EditorPanel palette, eyedropper, ArtBindings "tiles" target).
    | SelectBrush of tileType: string * visual: VisualRef option

    // ---- Characters (NPCEditor.tsx) ----
    /// Add or replace an NPC; `project.Dialogues` is kept in step with `npc.Dialogue`.
    | UpsertNpc of npc: Npc
    | RemoveNpc of npcId: string
    | MoveNpc of npcId: string * sceneId: string * x: int * y: int
    /// NPCDetailEditor create/save dialogue: on the NPC and in `project.Dialogues`.
    | UpsertDialogue of dialogue: Dialogue
    | RemoveDialogue of dialogueId: string

    // ---- Content (ItemEditor, CropEditor, QuestEditor, EventsEditor, ShopEditor, RecipeEditor, NodeTypeEditor, WildlifeEditor, ActionsEditor) ----
    | UpsertItem of item: Item
    | RemoveItem of itemId: string
    /// ItemEditor "Add to inventory": stacks when possible, else a new slot when there is room.
    | AddToInventory of itemId: string
    /// CropEditor save: also creates/updates the `seed-{id}` and `crop-{id}` items.
    | UpsertCrop of crop: CustomCropDefinition
    /// CropEditor delete: also removes the seed and crop items.
    | RemoveCrop of cropId: string
    | UpsertQuest of quest: Quest
    | RemoveQuest of questId: string
    | UpsertEvent of event: GameEvent
    | RemoveEvent of eventId: string
    | UpsertShop of shop: ShopDefinition
    | RemoveShop of shopId: string
    | UpsertRecipe of recipe: RecipeDefinition
    | RemoveRecipe of recipeId: string
    | UpsertNodeType of nodeType: NodeTypeDefinition
    | RemoveNodeType of nodeTypeId: string
    | UpsertMachineType of machineType: MachineTypeDefinition
    | RemoveMachineType of machineTypeId: string
    | UpsertAnimalSpecies of species: AnimalSpeciesDefinition
    /// WildlifeEditor delete: the species and every animal of it.
    | RemoveAnimalSpecies of speciesId: string
    | UpsertAnimal of animal: AnimalState
    | RemoveAnimal of animalId: string
    | UpsertFishTable of table: FishTable
    | RemoveFishTable of tableId: string
    | UpsertAction of action: ActionDef
    | RemoveAction of actionId: string
    | UpsertMinigame of minigame: MinigameDef
    | RemoveMinigame of minigameId: string
    | SetWeather of config: WeatherConfig
    /// ProjectSettingsEditor weather table: a weight of 0 removes the entry.
    | SetWeatherWeight of seasonId: string * weatherId: string * weight: float
    | SetMine of config: MineConfig
    /// InterfaceEditor: the creator panels shown in play.
    | SetGamePanels of panels: GamePanel list

    // ---- Project (ProjectSettingsEditor.tsx, AssetManager.tsx, ArtBindings.tsx, ModsEditor.tsx) ----
    | SetProjectInfo of name: string * version: string
    | SetSettings of settings: ProjectSettings
    /// Native export identity and target settings; None disables standalone export metadata.
    | SetExportSettings of settings: ExportSettings option
    /// ProjectSettingsEditor `removeSeason`: also drops festivals and weather rows of that season; refused for the last season.
    | RemoveSeason of seasonId: string
    /// ProjectSettingsEditor `moveSeason`: swap a season with the one `delta` places away (±1 for
    /// the arrows). Festivals, weather rows, crops and the current season refer to seasons by id,
    /// so they follow; only the order of the year changes. Out of range → no-op.
    | MoveSeason of seasonId: string * delta: int
    | SetGraphics of graphics: GraphicsSettings
    | SetPlayerVisual of visual: VisualRef option
    /// ArtBindings: attach artwork to the player or a content definition (items also update placed copies).
    | BindVisual of target: VisualTarget * visual: VisualRef option
    /// AssetManager import / clip edits: add or replace an asset by id.
    | UpsertAsset of asset: CustomAsset
    /// AssetManager "Remove unused art": here every binding that pointed at it is cleared instead.
    | RemoveAsset of assetId: string
    /// AssetManager frame timing: one frame's duration in ticks, or every frame's when `frame` is
    /// `None` (20 ticks = 1 second; at least 1).
    | SetFrameTicks of assetId: string * clip: string * frame: int option * ticks: int
    /// Art studio: copy a frame of a clip and insert the copy right after it (at most 1024 frames).
    | DuplicateFrame of assetId: string * clip: string * frame: int
    /// ModsEditor install (after the permission review); refused when the pack id is already installed.
    | InstallPack of pack: ContentPack
    | SetPackEnabled of packId: string * enabled: bool
    /// Load order; ids not listed keep their relative order after the listed ones.
    | ReorderPacks of packIds: string list
    | RemovePack of packId: string
    /// ModsEditor "Import into project": the pack's content becomes editable project content and the layer is removed.
    | ImportPack of packId: string
    /// Imports and playtest results: the whole project, still one undo step.
    | ReplaceProject of project: GameProject
    /// A group of edits applied as ONE undo step (drag strokes, workshop patterns).
    | Batch of label: string * edits: Edit list
