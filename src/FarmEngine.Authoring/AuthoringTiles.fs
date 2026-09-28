namespace FarmEngine.Authoring

open System.Collections.Generic
open FarmEngine.Schemas

/// Tile construction and layer edits from game-helpers.ts. These transform authored maps;
/// collision queries and movement during play remain in Rust.
[<AbstractClass; Sealed>]
type AuthoringTiles =
    static member ClassifyTileType(tileType: string) =
        match tileType with "path" -> "overlay" | "wall" | "door" -> "object" | _ -> "background"

    static member CreateEmptyTile(x: float, y: float, tileType: string) =
        let layer = AuthoringTiles.ClassifyTileType tileType
        Tile(X = x, Y = y, Type = tileType,
            Background = (if layer = "background" then tileType else "grass"),
            Overlay = (if layer = "overlay" then tileType else null),
            Object = (if layer = "object" then tileType else null),
            Collision = (tileType = "wall"), SoilMoisture = 0.0, SoilFertility = 0.0)

    static member SetTileLayer(tile: Tile, tileType: string, visual: VisualRef | null) =
        let layer = AuthoringTiles.ClassifyTileType tileType
        let property = match layer with "background" -> "Background" | "overlay" -> "Overlay" | _ -> "Object"
        let changes =
            [ "Type", box tileType
              property, box tileType
              if layer = "object" then "Collision", box (tileType = "wall")
              if not (isNull visual) then "CustomImage", null
              if not (isNull visual) || not (isNull tile.Visuals) then
                  let visuals = match tile.Visuals with null -> TileVisuals() | v -> v
                  "Visuals", box (Records.withValue visuals property visual) ]
        Records.withValues tile changes

    static member SetTileLayer(tile: Tile, tileType: string) = AuthoringTiles.SetTileLayer(tile, tileType, null)

    static member CreateEmptyScene(id: string, name: string, width: float, height: float) =
        let rows = List<List<Tile>>()
        let mutable y = 0.0
        while y < height do
            let row = List<Tile>()
            let mutable x = 0.0
            while x < width do
                row.Add(AuthoringTiles.CreateEmptyTile(x, y, "grass"))
                x <- x + 1.0
            rows.Add row
            y <- y + 1.0
        Scene(Id = id, Name = name, Width = width, Height = height, Tiles = rows,
            Transitions = List(), Npcs = List(), Events = List())
