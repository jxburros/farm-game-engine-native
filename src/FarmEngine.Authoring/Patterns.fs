namespace FarmEngine.Authoring

open System.Collections.Generic
open System.Text.Json
open FarmEngine.Json
open FarmEngine.Schemas

/// The Creator Workshop patterns (web `creator-patterns.ts` `CREATOR_PATTERNS`).
type PatternKind =
    | Story
    | Romance
    | Mail
    | Building
    | Magic
    | Combat
    | Fishing
    | Insect
    | Forage
    | Tree
    | Rock
    | Weed
    | Craft

/// One workshop card: id, display name, the editor tab that shows the result, and the help line.
type PatternInfo =
    { Id: string
      Kind: PatternKind
      Name: string
      Tab: string
      Help: string }

/// The workshop form (web `PatternOptions`).
type PatternOptions =
    { Name: string
      Text: string
      X: int
      Y: int
      Day: int
      NpcId: string
      Friendship: int
      Consequences: bool }

/// Port of `creator-patterns.ts` `addCreatorPattern`: each pattern is a pure composition of
/// ordinary content, expressed as edits so the result stays editable and undoes as one step.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Patterns =
    let all : PatternInfo list =
        [ { Id = "story"; Kind = Story; Name = "Branching story"; Tab = "npcs"; Help = "A character with a choice, two branches, and an optional lasting story flag." }
          { Id = "romance"; Kind = Romance; Name = "Romance milestone"; Tab = "actions"; Help = "An invitation gated by friendship; a repeat-safe relationship flag and friendship reward." }
          { Id = "mail"; Kind = Mail; Name = "Letter & mailbox"; Tab = "events"; Help = "Interact at the chosen tile after the delivery day to receive a readable letter." }
          { Id = "building"; Kind = Building; Name = "Building & interior"; Tab = "events"; Help = "Donate five wood at the doorway to unlock a furnished-size interior and return door." }
          { Id = "magic"; Kind = Magic; Name = "Watering spell"; Tab = "actions"; Help = "A reusable inventory spell that spends energy and waters nearby soil." }
          { Id = "combat"; Kind = Combat; Name = "Combat encounter"; Tab = "actions"; Help = "A turn-based encounter with attack, guard and magic; winning grants a reward, losing costs energy." }
          { Id = "fishing"; Kind = Fishing; Name = "Custom fishing challenge"; Tab = "actions"; Help = "Connect the hold-to-catch minigame to fishing casts. Edit its timing or replace its code." }
          { Id = "insect"; Kind = Insect; Name = "Catchable insect"; Tab = "events"; Help = "An interactable insect location with a scored catching challenge and inventory specimen reward." }
          { Id = "forage"; Kind = Forage; Name = "Forageable"; Tab = "nodes"; Help = "A harvestable resource with a daily respawn and editable drops." }
          { Id = "tree"; Kind = Tree; Name = "Tree"; Tab = "nodes"; Help = "An axe-harvested tree with several hits, wood drops, collision and regrowth." }
          { Id = "rock"; Kind = Rock; Name = "Mineable rock"; Tab = "nodes"; Help = "A pickaxe-harvested rock with editable drop tables and respawn." }
          { Id = "weed"; Kind = Weed; Name = "Weeds"; Tab = "nodes"; Help = "Scythe-cut weeds that produce a resource and grow back." }
          { Id = "craft"; Kind = Craft; Name = "Crafting discipline"; Tab = "craft"; Help = "A placeable station, construction recipe, and station-specific recipe. Use for cooking, alchemy, smithing or other crafts." } ]

    let info (kind: PatternKind) : PatternInfo = all |> List.find (fun p -> p.Kind = kind)

    /// The pattern with this web id ("story", "tree", …).
    let tryFind (id: string) : PatternInfo option = all |> List.tryFind (fun p -> p.Id = id)

    let private config (pairs: (string * JsonElement) list) : OrderedDictionary<string, JsonElement> =
        let d = OrderedDictionary<string, JsonElement>()
        for (k, v) in pairs do d[k] <- v
        d

    let private outcome (kind: string) (changes: (string * objnull) list) : EventOutcome =
        Records.withValues (EventOutcome(Type = kind)) changes

    let private msg (text: string) = outcome EventOutcomeTypes.Message [ ("Message", box text) ]

    /// The id the pattern's content will use: the slugged name (or the kind), bumped until none
    /// of `id`, `id-item`, `id-action`, `id-inside`, `id-recipe` collide with existing content.
    let private chooseId (project: GameProject) (kind: string) (name: string) =
        let ids = HashSet<string>()
        let add (xs: seq<string>) = for x in xs do ids.Add x |> ignore
        add (project.Items |> Seq.map (fun i -> i.Id))
        add (project.Events |> Seq.map (fun e -> e.Id))
        add (project.Npcs |> Seq.map (fun n -> n.Id))
        add (project.Actions |> Seq.map (fun a -> a.Id))
        add (project.Minigames |> Seq.map (fun m -> m.Id))
        add (project.NodeTypes |> Seq.map (fun n -> n.Id))
        add (project.MachineTypes |> Seq.map (fun m -> m.Id))
        add (project.Scenes |> Seq.map (fun s -> s.Id))
        let slug = Defaults.slugId name Seq.empty kind
        let base' = if slug.Length = 0 then kind else slug
        let taken (id: string) = [ id; id + "-item"; id + "-action"; id + "-inside"; id + "-recipe" ] |> List.exists ids.Contains
        let rec go n =
            let candidate = if n = 1 then base' else sprintf "%s-%d" base' n
            if taken candidate then go (n + 1) else candidate
        go 1

    /// The edits for a pattern, or the message the web shows when the form is not ready
    /// ("Select a scene first.", "Choose a tile inside the current scene.", …).
    let edits (kind: PatternKind) (options: PatternOptions) (project: GameProject) : Result<Edit list, string> =
        match Proj.currentScene project with
        | None -> Error "Select a scene first."
        | Some scene ->
            let x, y = options.X, options.Y
            if x < 0 || y < 0 || float x >= scene.Width || float y >= scene.Height then Error "Choose a tile inside the current scene."
            else
                let pattern = info kind
                let id = chooseId project pattern.Id options.Name
                let name = if System.String.IsNullOrWhiteSpace options.Name then pattern.Name else options.Name.Trim()
                let text = options.Text
                let tile = scene.Tiles[y][x]
                let item (itemId: string) (title: string) : Item =
                    Item(Id = itemId, Name = title, Description = text, Type = ItemTypes.Material, Stackable = true, MaxStack = 99.0, Value = 20.0)
                let action (changes: (string * objnull) list) : ActionDef =
                    Records.withValues
                        (ActionDef(Id = id + "-action", Name = name, Description = text, EnergyCost = 0.0, FailMessage = "The requirements are not met yet."))
                        changes
                let event (conditions: EventCondition list) (outcomes: EventOutcome list) (repeatable: bool) : GameEvent =
                    GameEvent(Id = id, Name = name, SceneId = scene.Id, Trigger = EventTriggers.Interact, Active = true, Repeatable = repeatable,
                              Conditions = List<EventCondition>(conditions), Outcomes = List<EventOutcome>(outcomes))
                let interact = InteractTileCondition(X = float x, Y = float y) :> EventCondition
                let placeItem (i: Item) : Result<Edit list, string> =
                    if not (isNull tile.Item) then Error "That tile already has an item. Choose an empty tile."
                    else Ok [ UpsertItem i; PlaceItem(scene.Id, x, y, i) ]
                let ensureWood =
                    if project.Items |> Seq.exists (fun i -> i.Id = "material-wood") then [] else [ UpsertItem(item "material-wood" "Wood") ]
                let itemQty (kind: string) (itemId: string) (quantity: float) = outcome kind [ ("ItemId", box itemId); ("ItemQuantity", box quantity) ]
                match kind with
                | Story ->
                    let hello =
                        Dialogue(Id = id + "-hello", NpcId = id, Text = (if text.Length = 0 then "Will you help care for this place?" else text),
                                 Options = List<DialogueOption>(
                                    [ Records.withValues (DialogueOption(Text = "Yes, count me in.", NextDialogueId = id + "-yes"))
                                          (if options.Consequences then [ ("EventFlag", box (id + "-promised")) ] else [])
                                      DialogueOption(Text = "Tell me more first.", NextDialogueId = id + "-more") ]))
                    let yes = Dialogue(Id = id + "-yes", NpcId = id, Text = "Then we have a new beginning.", Options = List<DialogueOption>([ DialogueOption(Text = "See you soon.") ]))
                    let more =
                        Dialogue(Id = id + "-more", NpcId = id, Text = "There is no hurry. Make this place your own.",
                                 Options = List<DialogueOption>([ DialogueOption(Text = "I understand.", NextDialogueId = id + "-hello"); DialogueOption(Text = "Goodbye.") ]))
                    let npc = Npc(Id = id, Name = name, SceneId = scene.Id, X = float x, Y = float y, CanMove = false, Appearance = "villager", Dialogue = List<Dialogue>([ hello; yes; more ]))
                    Ok [ UpsertNpc npc ]
                | Romance ->
                    match project.Npcs |> Seq.tryFind (fun n -> n.Id = options.NpcId) with
                    | Some npc when npc.Dialogue.Count > 0 ->
                        let flag = id + "-relationship"
                        let a =
                            action
                                [ ("Conditions", box (List<EventCondition>([ FriendshipCondition(NpcId = npc.Id, Min = float options.Friendship) :> EventCondition; FlagCondition(Flag = flag, Value = false) :> EventCondition ])))
                                  ("Outcomes", box (List<EventOutcome>(
                                      [ outcome EventOutcomeTypes.SetFlag [ ("FlagName", box flag) ]
                                        outcome EventOutcomeTypes.ModifyFriendship [ ("NpcId", box npc.Id); ("Amount", box 125.0) ]
                                        msg (if text.Length = 0 then "I would love to spend more time with you." else text) ]))) ]
                        let first = npc.Dialogue[0]
                        let invitation = DialogueOption(Text = name, RequiresFriendship = System.Nullable(float options.Friendship), ActionId = id + "-action")
                        let updated = Records.withValue first "Options" (box (Lists.append invitation first.Options))
                        Ok [ UpsertAction a; UpsertDialogue updated ]
                    | _ -> Error "Choose a character with a starting dialogue."
                | Mail ->
                    let letter = Records.withValues (item (id + "-item") name) [ ("Type", box ItemTypes.Quest); ("UseActionId", box (id + "-action")) ]
                    let a = action [ ("Outcomes", box (List<EventOutcome>([ msg (if text.Length = 0 then "Welcome to your new farm!" else text) ]))) ]
                    let e =
                        event [ interact; DayRangeCondition(MinDay = System.Nullable(float options.Day)) :> EventCondition; InventorySpaceCondition(ItemId = letter.Id, Quantity = 1.0) :> EventCondition ]
                              [ itemQty EventOutcomeTypes.GiveItem letter.Id 1.0 ] false
                    Ok [ UpsertItem letter; UpsertAction a; UpsertEvent e ]
                | Building ->
                    if scene.Transitions |> Seq.exists (fun t -> int t.FromX = x && int t.FromY = y) then Error "That tile already has a doorway."
                    else
                        let interior = AuthoringTiles.CreateEmptyScene(id + "-inside", name + " interior", 10.0, 8.0)
                        let interior = Proj.mapTiles (fun t -> AuthoringTiles.SetTileLayer(t, TileTypes.Floor)) interior
                        let interior = Records.withValue interior "Transitions" (box (List<SceneTransition>([ SceneTransition(FromX = 4.0, FromY = 7.0, ToSceneId = scene.Id, ToX = float x, ToY = float y) ])))
                        let door = SceneTransition(FromX = float x, FromY = float y, ToSceneId = interior.Id, ToX = 4.0, ToY = 6.0, Locked = System.Nullable true)
                        let e =
                            event [ interact; HasItemCondition(ItemId = "material-wood", Quantity = 5.0) :> EventCondition ]
                                  [ itemQty EventOutcomeTypes.TakeItem "material-wood" 5.0
                                    outcome EventOutcomeTypes.UnlockTransition [ ("SceneId", box scene.Id); ("X", box (float x)); ("Y", box (float y)) ]
                                    msg (sprintf "%s is ready. Step through the doorway." name) ] false
                        Ok(ensureWood @ [ AddScene interior; PaintTiles(scene.Id, Object, [ (x, y) ], TileTypes.Door); SetTransition(scene.Id, door); UpsertEvent e ])
                | Magic ->
                    let a = action [ ("EnergyCost", box 8.0); ("Outcomes", box (List<EventOutcome>([ outcome EventOutcomeTypes.WaterArea [ ("Radius", box 2.0) ] ]))) ]
                    let spell = Records.withValues (item (id + "-item") name) [ ("UseActionId", box (id + "-action")); ("Stackable", box false); ("MaxStack", box 1.0) ]
                    placeItem spell |> Result.map (fun placed -> UpsertAction a :: placed)
                | Fishing ->
                    match project.Minigames |> Seq.tryFind (fun m -> m.Id = ExtensibilitySchema.FishingMinigameId) with
                    | Some existing ->
                        let cfg = OrderedDictionary<string, JsonElement>(existing.Config)
                        cfg["holdMs"] <- Js.Value 1200.0
                        Ok [ UpsertMinigame(Records.withValues existing [ ("Kind", box "hold-to-catch"); ("Config", box cfg); ("Name", box name) ]) ]
                    | None ->
                        Ok [ UpsertMinigame(MinigameDef(Id = ExtensibilitySchema.FishingMinigameId, Name = name, Kind = "hold-to-catch", Config = config [ ("holdMs", Js.Value 1200.0) ])) ]
                | Combat | Insect ->
                    let combat = (kind = Combat)
                    let reward = item (id + "-item") (if combat then name + " trophy" else name)
                    let cfg =
                        if combat then config [ ("enemyName", Js.Value name); ("enemyHealth", Js.Value 24.0); ("playerHealth", Js.Value 30.0); ("enemyAttack", Js.Value 5.0); ("attack", Js.Value 7.0) ]
                        else config [ ("prompt", Js.Value(sprintf "Catch %s!" name)); ("speed", Js.Value 1.2); ("targetSize", Js.Value 0.2) ]
                    let lose =
                        [ yield msg (if combat then "You retreat to safety." else "It got away!")
                          if combat then yield outcome EventOutcomeTypes.ModifyEnergy [ ("Amount", box -10.0) ] ]
                    let win = [ itemQty EventOutcomeTypes.GiveItem reward.Id 1.0; outcome EventOutcomeTypes.SetFlag [ ("FlagName", box (id + "-complete")) ] ]
                    let minigame =
                        MinigameDef(Id = id, Name = name, Kind = (if combat then "simple-battle" else "timing-bar"), Config = cfg,
                                    ResultTiers = List<MinigameResultTier>([ MinigameResultTier(MinScore = 0.0, Outcomes = List<EventOutcome>(lose)); MinigameResultTier(MinScore = 0.7, Outcomes = List<EventOutcome>(win)) ]))
                    let e =
                        event [ interact; FlagCondition(Flag = id + "-complete", Value = false) :> EventCondition; InventorySpaceCondition(ItemId = reward.Id, Quantity = 1.0) :> EventCondition ]
                              [ outcome EventOutcomeTypes.StartMinigame [ ("MinigameId", box id) ] ] true
                    Ok [ UpsertItem reward; UpsertMinigame minigame; UpsertEvent e ]
                | Craft ->
                    let station = item (id + "-item") (name + " station")
                    let product = item (id + "-product") (name + " creation")
                    let machine = MachineTypeDefinition(Id = id, Name = name, Description = text, Color = "#987852", ItemId = station.Id, BlocksMovement = true, StationCategories = List<string>([ id ]))
                    let ingredient (itemId: string) (quantity: float) = RecipeIngredient(ItemId = itemId, Quantity = quantity)
                    let build =
                        RecipeDefinition(Id = id + "-recipe", Name = sprintf "Build %s station" name, Inputs = List<RecipeIngredient>([ ingredient "material-wood" 5.0 ]),
                                         Outputs = List<RecipeIngredient>([ ingredient station.Id 1.0 ]), Category = "construction", ProcessingMinutes = 0.0)
                    let make =
                        RecipeDefinition(Id = id + "-product-recipe", Name = product.Name, Inputs = List<RecipeIngredient>([ ingredient "material-wood" 1.0 ]),
                                         Outputs = List<RecipeIngredient>([ ingredient product.Id 1.0 ]), Category = name, RequiresStationCategory = id, ProcessingMinutes = 0.0)
                    Ok(ensureWood @ [ UpsertItem station; UpsertItem product; UpsertMachineType machine; UpsertRecipe build; UpsertRecipe make ])
                | Forage | Tree | Rock | Weed ->
                    if not (isNull tile.Node) then Error "That tile already has a gathering node."
                    else
                        let resource = item (id + "-item") (name + " resource")
                        let health = match kind with Tree -> 3.0 | Rock -> 2.0 | _ -> 1.0
                        let tool = match kind with Tree -> ToolTypes.Axe | Rock -> ToolTypes.Pickaxe | _ -> ToolTypes.Scythe
                        let nodeType =
                            NodeTypeDefinition(Id = id, Name = name, Health = health, RequiredTool = tool, RequiredToolTier = 1.0,
                                               Drops = List<NodeDrop>([ NodeDrop(ItemId = resource.Id, Min = 1.0, Max = 3.0, Weight = 1.0) ]),
                                               RespawnDays = System.Nullable(if kind = Tree then 5.0 else 1.0),
                                               BlocksMovement = (kind = Tree || kind = Rock), Color = (if kind = Rock then "#898795" else "#60944b"))
                        Ok [ UpsertItem resource; UpsertNodeType nodeType; PlaceNode(scene.Id, x, y, id) ]

    /// The pattern as ONE undo step (a `Batch` labelled with the pattern name).
    let build (kind: PatternKind) (options: PatternOptions) (project: GameProject) : Result<Edit, string> =
        edits kind options project |> Result.map (fun list -> Batch((info kind).Name, list))

    /// The message the workshop shows once a pattern is created (CreatorWorkshop.tsx).
    let successMessage (kind: PatternKind) (name: string) : string =
        let shown = if System.String.IsNullOrWhiteSpace name then (info kind).Name else name.Trim()
        let hint =
            match kind with
            | Magic -> "Pick up the spell at the chosen tile and use it from inventory."
            | Fishing -> "Cast a fishing rod at water to try it."
            | _ -> "Open its editor below to customize the generated content."
        sprintf "%s created. %s" shown hint
