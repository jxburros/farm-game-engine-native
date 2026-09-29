namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// A pack in the curated registry (web `src/lib/mod-registry.ts` `RegistryEntry`).
[<RequireQualifiedAccess>]
type RegistryEntry =
    { Id: string
      Name: string
      Author: string
      Description: string
      Pack: ContentPack }

/// Mod registry v1 (web `loadRegistry`): the curated packs that ship with the editor, the demo
/// mod and the starter template. Every entry is validated with `PackRules.validateContentPack`
/// when listed; one that does not validate is left out. The long-term shape is an index fetched
/// at runtime, which this entry format is meant to survive.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ModRegistry =
    /// docs/modding/examples/demo-mod.json of the web repo (fixtures/projects/demo-mod.json).
    let demoModJson : string =
        """{
  "manifest": {
    "id": "demo-glow-farm",
    "name": "Glow Farm Demo Mod",
    "version": "1.0.0",
    "description": "The M5 acceptance demo: a new crop, a new machine + recipe, and a plugin that reacts to onDayStart.",
    "author": "farm-game-engine",
    "engineCompatibility": ">=0.5.0",
    "base": false,
    "dependencies": [],
    "overrides": [],
    "permissions": {
      "hooks": ["onDayStart"],
      "contentInject": true,
      "uiPanels": false
    }
  },
  "content": {
    "crops": [
      {
        "id": "glowshroom",
        "name": "Glowshroom",
        "seedCost": 45,
        "baseHarvestValue": 110,
        "growthTime": 30000,
        "growthDays": 6,
        "stages": 5,
        "seasons": ["fall", "winter"],
        "canRegrow": false,
        "yieldMin": 1,
        "yieldMax": 2,
        "mutationChance": 0.03
      }
    ],
    "items": [
      {
        "id": "seed-glowshroom",
        "name": "Glowshroom Spores",
        "description": "Plant these to grow glowshrooms",
        "type": "seed",
        "stackable": true,
        "maxStack": 99,
        "value": 45,
        "cropType": "glowshroom"
      },
      {
        "id": "crop-glowshroom",
        "name": "Glowshroom",
        "description": "Fresh glowshroom",
        "type": "crop",
        "stackable": true,
        "maxStack": 99,
        "value": 110,
        "cropType": "glowshroom"
      },
      {
        "id": "glow-jelly",
        "name": "Glow Jelly",
        "description": "Luminous preserves that light up the pantry.",
        "type": "material",
        "stackable": true,
        "maxStack": 99,
        "value": 320
      }
    ],
    "machineTypes": [
      {
        "id": "machine-glow-vat",
        "name": "Glow Vat",
        "description": "Renders glowshrooms into luminous jelly.",
        "color": "#7de8a8",
        "blocksMovement": true
      }
    ],
    "recipes": [
      {
        "id": "recipe-glow-jelly",
        "name": "Glow Jelly",
        "inputs": [{ "itemId": "crop-glowshroom", "quantity": 2 }],
        "outputs": [{ "itemId": "glow-jelly", "quantity": 1 }],
        "machineTypeId": "machine-glow-vat",
        "processingMinutes": 180
      }
    ]
  },
  "plugins": [
    {
      "id": "morning-hum",
      "name": "Morning Hum",
      "hooks": ["onDayStart"],
      "source": "api.on('onDayStart', function (payload) { var out = [{ type: 'message', text: 'The glowshrooms hum softly on day ' + payload.day + '...' }]; if (payload.day % 7 === 0) { out.push({ type: 'giveItem', itemId: 'demo-glow-farm:seed-glowshroom', quantity: 1 }); } return out; })"
    }
  ]
}"""

    /// docs/modding/template/my-first-mod.json of the web repo (fixtures/projects/my-first-mod.json).
    let templateModJson : string =
        """{
  "manifest": {
    "id": "my-first-mod",
    "name": "My First Mod",
    "version": "0.1.0",
    "description": "Rename me, then add content below.",
    "author": "you",
    "engineCompatibility": "*",
    "base": false,
    "dependencies": [],
    "overrides": [],
    "permissions": {
      "hooks": [],
      "contentInject": true,
      "uiPanels": false
    }
  },
  "content": {
    "items": [
      {
        "id": "lucky-coin",
        "name": "Lucky Coin",
        "description": "It has to be lucky, right?",
        "type": "material",
        "stackable": true,
        "maxStack": 99,
        "value": 500
      }
    ]
  },
  "plugins": []
}"""

    /// A registry entry from pack JSON, or None when it is not a valid pack.
    let entry (text: string) : RegistryEntry option =
        match Json.parse text with
        | Error _ -> None
        | Ok raw ->
            match PackRules.validateContentPack raw with
            | Error _ -> None
            | Ok pack ->
                let manifest = pack.Manifest
                Some
                    { RegistryEntry.Id = manifest.Id
                      Name = manifest.Name
                      Author = defaultArg manifest.Author "unknown"
                      Description = defaultArg manifest.Description ""
                      Pack = pack }

    /// The curated index, in order.
    let entries () : RegistryEntry list = [ demoModJson; templateModJson ] |> List.choose entry

    /// Whether the project already has the pack (the web shows "Installed" and disables Install).
    let isInstalled (project: GameProject) (packId: string) : bool =
        project.ContentPacks |> List.exists (fun install -> install.Pack.Manifest.Id = packId)
