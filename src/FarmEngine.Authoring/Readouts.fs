namespace FarmEngine.Authoring

open System
open FarmEngine.Schemas

/// One line of a readout card: a label and its value, already formatted for display.
type ReadoutLine = { Label: string; Value: string }

/// A tab of a content form: its title and the record properties it shows, in order.
type FormTab = { Title: string; Properties: string list }

/// What the content editors show beside a form (web RecipeEditor's profit calculator,
/// CropEditor's profit line, summary card and tabs, NodeTypeEditor's built-in list): computed
/// from the effective content, never stored in the project.
module Readouts =
    /// Rounds to a whole number like the web's `toFixed(0)`: half away from zero (so a negative
    /// profit rounds like its positive twin). Same as `Math.round` for the non-negative day counts.
    let private round (value: float) = JsNumber.roundHalfAway value

    /// Money like the web (`$12`, `-$5`).
    let money (value: float) = (if value < 0.0 then "-$" else "$") + JsNumber.format (abs value)

    /// A profit with its sign like the web list (`+12`, `-5`, `+0`).
    let signed (value: float) = (if value >= 0.0 then "+" else "") + JsNumber.format value

    // ---- Recipes ----

    /// The value of one `itemId` in the effective items (the project's, or the built-in catalog);
    /// 0 when unknown.
    let itemValue (project: GameProject) (itemId: string) : float =
        ContentCompiler.items project
        |> List.tryFind (fun item -> item.Id = itemId)
        |> Option.map (fun item -> item.Value)
        |> Option.defaultValue 0.0

    /// Output value minus input value of one craft (web `profit(recipe)`).
    let recipeProfit (project: GameProject) (recipe: RecipeDefinition) : float =
        let total (ingredients: RecipeIngredient list) =
            ingredients |> List.sumBy (fun ingredient -> itemValue project ingredient.ItemId * ingredient.Quantity)
        total recipe.Outputs - total recipe.Inputs

    /// Profit per in-game hour of processing, rounded like the web's `toFixed(0)`; None for an
    /// instant craft.
    let recipeProfitPerHour (project: GameProject) (recipe: RecipeDefinition) : float option =
        if recipe.ProcessingMinutes > 0.0 then Some(round (recipeProfit project recipe / (recipe.ProcessingMinutes / 60.0)))
        else None

    /// "Profit per craft: 12 · time-adjusted: 24/hr" (or "instant").
    let recipeSummary (project: GameProject) (recipe: RecipeDefinition) : string =
        let perHour =
            match recipeProfitPerHour project recipe with
            | Some value -> JsNumber.format value + "/hr"
            | None -> "instant"
        sprintf "Profit per craft: %s · time-adjusted: %s" (JsNumber.format (recipeProfit project recipe)) perHour

    /// The list line under a recipe (web RecipeEditor): where it is made and its profit.
    let recipeNote (project: GameProject) (recipe: RecipeDefinition) : string =
        let where =
            match recipe.MachineTypeId with
            | Some machineId ->
                let name =
                    project.MachineTypes
                    |> List.tryFind (fun machine -> machine.Id = machineId)
                    |> Option.map (fun machine -> machine.Name)
                    |> Option.defaultValue "machine"
                sprintf "%s · %smin" name (JsNumber.format recipe.ProcessingMinutes)
            | None -> "hand craft"
        sprintf "%s · profit %s" where (signed (recipeProfit project recipe))

    // ---- Crops ----

    /// A custom crop as the crop definition the engine gets (its extra fields ride along).
    let cropOfCustom (crop: CustomCropDefinition) : CropDefinition = ContentCompiler.cropOfCustom crop

    /// A crop definition as a custom crop with the same id and values (a built-in crop's
    /// replacement, so the creator can change it).
    let customOfCrop (crop: CropDefinition) : CustomCropDefinition =
        match Decode.run SchemaJson.decodeCustomCropDefinition (SchemaJson.encodeCropDefinition crop) with
        | Ok definition -> definition
        | Error message -> invalidOp message

    /// Harvest value minus seed cost (web "Profit per harvest").
    let cropProfit (crop: CropDefinition) : float = crop.BaseHarvestValue - crop.SeedCost

    /// "Profit per harvest: $15" (the line under the crop form).
    let cropProfitText (crop: CropDefinition) : string = "Profit per harvest: " + money (cropProfit crop)

    /// Days from planting to harvest: `growthDays`, else the legacy wall-clock time in days.
    let growthDays (crop: CropDefinition) : float =
        match crop.GrowthDays with
        | Some days -> days
        | None -> min 28.0 (max 1.0 (round (crop.GrowthTime / 5000.0)))

    let private seasonName (project: GameProject) (id: string) =
        project.Settings.Calendar.Seasons
        |> List.tryFind (fun season -> season.Id = id)
        |> Option.map (fun season -> season.Name)
        |> Option.defaultValue (if id.Length = 0 then id else id.Substring(0, 1).ToUpperInvariant() + id.Substring 1)

    /// The crop summary card (web CropEditor's read-only view).
    let cropSummary (project: GameProject) (crop: CropDefinition) : ReadoutLine list =
        let line label value = { Label = label; Value = value }
        let mutation = round ((defaultArg crop.MutationChance 0.0) * 1000.0) / 10.0
        let mutationText =
            let text = JsNumber.format mutation
            if text.Contains "." then text else text + ".0"
        // The web shows the regrowth row when either field is set and not 0 (JS truthiness).
        let set (value: float option) = value |> Option.exists (fun v -> v <> 0.0)
        [ line "Seed cost" (money crop.SeedCost)
          line "Harvest value" (money crop.BaseHarvestValue)
          line "Profit per harvest" (money (cropProfit crop))
          line "Growth days" (JsNumber.format (growthDays crop))
          line "Stages" (JsNumber.format crop.Stages)
          line "Seasons" (match crop.Seasons with [] -> "None" | seasons -> seasons |> List.map (seasonName project) |> String.concat ", ")
          line "Yield range" (sprintf "%s – %s" (JsNumber.format crop.YieldMin) (JsNumber.format crop.YieldMax))
          line "Mutation chance" (mutationText + "%")
          line "Can regrow" (if crop.CanRegrow then "Yes" else "No")
          if crop.CanRegrow && (set crop.RegrowthDays || set crop.RegrowthTime) then
              let days =
                  match crop.RegrowthDays with
                  | Some days -> days
                  | None -> round ((defaultArg crop.RegrowthTime 10000.0) / 5000.0)
              line "Regrowth time" (sprintf "%s day(s)" (JsNumber.format days))
          match crop.MultiTile with
          | Some size -> line "Multi-tile size" (sprintf "%s × %s tiles" (JsNumber.format size.Width) (JsNumber.format size.Height))
          | None -> () ]

    /// Built-in crop ids, in pack order.
    let builtinCropIds () : string list = Builtin.crops () |> List.map fst

    /// The built-in crops no custom crop replaces (listed read-only by the crop editor).
    let builtinCrops (project: GameProject) : CropDefinition list =
        let custom = project.CustomCrops |> Option.defaultValue [] |> List.map (fun crop -> crop.Id) |> Set.ofList
        Builtin.crops () |> List.filter (fun (id, _) -> not (custom.Contains id)) |> List.map snd

    /// Where a listed entry comes from: a built-in shown read-only, a project entry with a
    /// built-in's id (it replaces the built-in), or the project's own.
    let private origin (builtinIds: string list) (listedBuiltin: bool) (id: string) =
        if listedBuiltin then "Built-in"
        elif List.contains id builtinIds then "Replaces built-in"
        else "Custom"

    /// The list line under a custom crop (web CropEditor): "Custom · 5 stages", or
    /// "Replaces built-in · 4 stages" for one with a built-in crop's id.
    let cropNote (crop: CustomCropDefinition) : string =
        sprintf "%s · %s stages" (origin (builtinCropIds ()) false crop.Id) (JsNumber.format crop.Stages)

    /// The list line under a built-in crop: "Built-in · 4 stages".
    let builtinCropNote (crop: CropDefinition) : string =
        sprintf "%s · %s stages" (origin [] true crop.Id) (JsNumber.format crop.Stages)

    // ---- Node types ----

    /// Built-in and mine node ids.
    let builtinNodeTypeIds () : string list = Builtin.nodeTypes () @ Builtin.mineNodeTypes () |> List.map (fun node -> node.Id)

    /// The built-in node types (mine rocks included) the project does not replace.
    let builtinNodeTypes (project: GameProject) : NodeTypeDefinition list =
        let own = project.NodeTypes |> List.map (fun node -> node.Id) |> Set.ofList
        Builtin.nodeTypes () @ Builtin.mineNodeTypes () |> List.filter (fun node -> not (own.Contains node.Id))

    /// "4 hp · axe · no respawn" (web NodeTypeEditor's list line).
    let nodeTypeNote (node: NodeTypeDefinition) : string =
        let tier = if node.RequiredToolTier > 1.0 then sprintf " t%s" (JsNumber.format node.RequiredToolTier) else ""
        let respawn =
            match node.RespawnDays with
            | Some(Some days) -> sprintf "respawns %sd" (JsNumber.format days)
            | _ -> "no respawn"
        sprintf "%s hp · %s%s · %s" (JsNumber.format node.Health) node.RequiredTool tier respawn

    /// The list line under a node type: "Built-in · 4 hp · axe · no respawn" (`builtin` when it
    /// is listed read-only), "Replaces built-in · …" or "Custom · …".
    let nodeTypeListNote (node: NodeTypeDefinition) (builtin: bool) : string =
        sprintf "%s · %s" (origin (builtinNodeTypeIds ()) builtin node.Id) (nodeTypeNote node)

    /// The node type summary card: health, tool, drops, respawn and blocking.
    let nodeTypeSummary (project: GameProject) (node: NodeTypeDefinition) : ReadoutLine list =
        let itemName id =
            ContentCompiler.items project |> List.tryFind (fun item -> item.Id = id) |> Option.map (fun item -> item.Name) |> Option.defaultValue id
        let drops =
            match node.Drops with
            | [] -> "Nothing"
            | drops ->
                drops
                |> List.map (fun drop ->
                    let range = if drop.Min = drop.Max then JsNumber.format drop.Min else sprintf "%s–%s" (JsNumber.format drop.Min) (JsNumber.format drop.Max)
                    sprintf "%s ×%s" (itemName drop.ItemId) range)
                |> String.concat ", "
        [ { Label = "Health"; Value = sprintf "%s hits" (JsNumber.format node.Health) }
          { Label = "Tool"; Value = if node.RequiredToolTier > 1.0 then sprintf "%s (tier %s)" node.RequiredTool (JsNumber.format node.RequiredToolTier) else node.RequiredTool }
          { Label = "Drops"; Value = drops }
          { Label = "Respawn"; Value = match node.RespawnDays with Some(Some days) -> sprintf "%s day(s)" (JsNumber.format days) | _ -> "Never" }
          { Label = "Blocks movement"; Value = if node.BlocksMovement then "Yes" else "No" } ]

    // ---- Schedules ----

    /// A schedule minute as a clock time (engine-core `formatTimeOfDay`): 480 → "8:00 AM",
    /// 1500 → "1:00 AM (next day)". Negative and non-numbers read as midnight.
    let clock (minute: float) : string =
        let total = if minute > 0.0 then int (Math.Floor(min minute 1e9)) else 0
        let inDay = total % 1440
        let hour = inDay / 60
        let hour12 = if hour % 12 = 0 then 12 else hour % 12
        let minutes = inDay % 60
        let text = sprintf "%d:%s%d %s" hour12 (if minutes < 10 then "0" else "") minutes (if hour < 12 then "AM" else "PM")
        if total >= 1440 then text + " (next day)" else text

    // ---- Form layout ----

    /// Tabs a content form groups its fields into (web CropEditor's Basic / Growth / Asset);
    /// empty for a record shown as one form. Properties not listed go on the last tab.
    let formTabs (recordType: string) : FormTab list =
        match recordType with
        | "CustomCropDefinition" ->
            [ { Title = "Basic"; Properties = [ "Id"; "Name"; "SeedCost"; "BaseHarvestValue"; "Seasons"; "YieldMin"; "YieldMax" ] }
              { Title = "Growth"; Properties = [ "GrowthDays"; "Stages"; "CanRegrow"; "RegrowthDays"; "MutationChance"; "MultiTile"; "GrowthTime"; "RegrowthTime" ] }
              { Title = "Asset"; Properties = [ "Visual"; "CustomAsset" ] } ]
        | _ -> []
