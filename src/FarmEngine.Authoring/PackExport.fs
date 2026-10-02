namespace FarmEngine.Authoring

open System
open System.Collections.Generic
open FarmEngine.Schemas

/// One content type the Mods view can export, with the entries a creator can pick.
[<RequireQualifiedAccess>]
type PackExportCategory =
    { /// The pack content key ("items", "npcs", …).
      Key: string
      Label: string
      Entries: PickerOption list }

/// ModsEditor "Export selection as pack" (web `EXPORTABLE` and `exportSelectionAsPack`): the
/// editor is the mod IDE, so project content becomes a shareable content pack. The web exports
/// whole content types; here each type can also be narrowed to chosen entries.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PackExport =
    let private generated (scene: Scene) =
        scene.Extra |> List.exists (fun (key, value) -> key = "generated" && Json.truthy value)

    let private entry (id: string) (name: string) (json: Json) = id, name, json

    /// Every exportable type in pack order: key, label and (id, name, JSON) per entry. Actions
    /// and minigames go beyond the web list; packs carry them too.
    let private sources (project: GameProject) : (string * string * (string * string * Json) list) list =
        [ "crops", "Crops", defaultArg project.CustomCrops [] |> List.map (fun c -> entry c.Id c.Name (SchemaJson.encodeCustomCropDefinition c))
          "items", "Items", project.Items |> List.map (fun i -> entry i.Id i.Name (SchemaJson.encodeItem i))
          "recipes", "Recipes", project.Recipes |> List.map (fun r -> entry r.Id r.Name (SchemaJson.encodeRecipeDefinition r))
          "machineTypes", "Machines", project.MachineTypes |> List.map (fun m -> entry m.Id m.Name (SchemaJson.encodeMachineTypeDefinition m))
          "nodeTypes", "Node types", project.NodeTypes |> List.map (fun n -> entry n.Id n.Name (SchemaJson.encodeNodeTypeDefinition n))
          "animalSpecies", "Animals", project.AnimalSpecies |> List.map (fun s -> entry s.Id s.Name (SchemaJson.encodeAnimalSpeciesDefinition s))
          "fishTables", "Fish tables", project.FishTables |> List.map (fun f -> entry f.Id f.Name (SchemaJson.encodeFishTable f))
          "weatherTypes", "Weather types", project.Weather.Types |> List.map (fun w -> entry w.Id w.Name (SchemaJson.encodeWeatherTypeDefinition w))
          "npcs", "NPCs", project.Npcs |> List.map (fun n -> entry n.Id n.Name (SchemaJson.encodeNpc n))
          "dialogues", "Dialogues", project.Dialogues |> List.map (fun d -> entry d.Id d.NpcId (SchemaJson.encodeDialogue d))
          "scenes", "Scenes", project.Scenes |> List.filter (generated >> not) |> List.map (fun s -> entry s.Id s.Name (SchemaJson.encodeScene s))
          "events", "Events", project.Events |> List.map (fun e -> entry e.Id e.Name (SchemaJson.encodeGameEvent e))
          "quests", "Quests", project.Quests |> List.map (fun q -> entry q.Id q.Name (SchemaJson.encodeQuest q))
          "shops", "Shops", project.Shops |> List.map (fun s -> entry s.Id s.Name (SchemaJson.encodeShopDefinition s))
          "actions", "Actions", project.Actions |> List.map (fun a -> entry a.Id a.Name (SchemaJson.encodeActionDef a))
          "minigames", "Minigames", project.Minigames |> List.map (fun m -> entry m.Id m.Name (SchemaJson.encodeMinigameDef m)) ]

    /// What the export offers: every type with its entries (types without entries included, so
    /// the view can show them disabled with a count of 0).
    let categories (project: GameProject) : PackExportCategory list =
        sources project
        |> List.map (fun (key, label, entries) ->
            { PackExportCategory.Key = key
              Label = label
              Entries =
                entries
                |> List.map (fun (id, name, _) ->
                    { PickerOption.Id = id; Label = (if String.IsNullOrWhiteSpace name || name = id then id else sprintf "%s · %s" name id); Missing = false }) })

    /// The types the web export starts with ticked.
    let defaultKeys : string list = [ "items"; "recipes" ]

    /// The web's pack id rule: the name lowercased with runs of other characters as dashes,
    /// `my-pack` when nothing is left.
    let packId (name: string) : string = Defaults.slugId name Seq.empty "my-pack"

    /// The pack for the selection: (content key, chosen ids) pairs; entries keep project order.
    /// The art the chosen entries use (`AssetUsage.referencedBy`: visual bindings, crop art and
    /// the images their animation frames draw from) goes into the pack's `assets`, so the
    /// entries keep their look where the pack is installed. Validated with
    /// `PackRules.validateContentPack`, so the file installs anywhere the editor runs. Nothing
    /// selected → an error.
    let build (project: GameProject) (name: string) (selection: (string * string list) list) : Result<ContentPack, string list> =
        let chosen = Dictionary<string, HashSet<string>>()
        for key, ids in selection do
            if not (chosen.ContainsKey key) then chosen.[key] <- HashSet<string>()
            for id in ids do
                chosen.[key].Add id |> ignore
        let content =
            [ for key, _, entries in sources project do
                  match chosen.TryGetValue key with
                  | true, ids ->
                      let values = entries |> List.filter (fun (id, _, _) -> ids.Contains id) |> List.map (fun (_, _, json) -> json)
                      if not values.IsEmpty then yield key, JArray values
                  | _ -> () ]
        if content.IsEmpty then Error [ "Select at least one entry to export." ]
        else
            let assets =
                AssetUsage.referencedBy project (content |> List.map snd)
                |> List.distinctBy (fun asset -> asset.Id)
            let displayName = if String.IsNullOrWhiteSpace name then "My Pack" else name.Trim()
            JObject
                [ "manifest",
                  JObject
                      [ "id", JString(packId name)
                        "name", JString displayName
                        "version", JString "1.0.0"
                        "description", JString(sprintf "Exported from %s" project.Name)
                        "engineCompatibility", JString(">=" + PackRules.EngineVersion)
                        "permissions", JObject [ "hooks", JArray []; "contentInject", JBool true; "uiPanels", JBool false ] ]
                  "content", JObject content
                  "plugins", JArray []
                  if not assets.IsEmpty then PackRules.AssetsKey, JArray(assets |> List.map SchemaJson.encodeCustomAsset) ]
            |> PackRules.validateContentPack

    /// A pack as the indented JSON file the Mods view installs (and the web reads).
    let toText (pack: ContentPack) : string = Json.stringifyIndented (SchemaJson.encodeContentPack pack)

    /// The file name the export suggests (`{id}.json`).
    let fileName (pack: ContentPack) : string = pack.Manifest.Id + ".json"
