namespace FarmEngine.Authoring

open FarmEngine.Schemas

/// Tile construction and layer edits from game-helpers.ts. These transform authored maps;
/// collision queries and movement during play remain in Rust.
[<AbstractClass; Sealed>]
type AuthoringTiles =
    static member ClassifyTileType(tileType: string) =
        match tileType with "path" -> "overlay" | "wall" | "door" -> "object" | _ -> "background"

    static member CreateEmptyTile(x: float, y: float, tileType: string) : Tile =
        let layer = AuthoringTiles.ClassifyTileType tileType
        { Tile.Default with
            X = x; Y = y; Type = tileType
            Background = (if layer = "background" then tileType else "grass")
            Overlay = (if layer = "overlay" then Some tileType else None)
            Object = (if layer = "object" then Some tileType else None)
            Collision = (tileType = "wall"); SoilMoisture = 0.0; SoilFertility = 0.0 }

    static member SetTileLayer(tile: Tile, tileType: string, visual: VisualRef option) : Tile =
        let layer = AuthoringTiles.ClassifyTileType tileType
        let tile =
            match layer with
            | "background" -> { tile with Background = tileType }
            | "overlay" -> { tile with Overlay = Some tileType }
            | _ -> { tile with Object = Some tileType; Collision = (tileType = "wall") }
        let tile = { tile with Type = tileType; CustomImage = (if visual.IsSome then None else tile.CustomImage) }
        if visual.IsSome || tile.Visuals.IsSome then
            let visuals = defaultArg tile.Visuals TileVisuals.Default
            let visuals =
                match layer with
                | "background" -> { visuals with Background = visual }
                | "overlay" -> { visuals with Overlay = visual }
                | _ -> { visuals with Object = visual }
            { tile with Visuals = Some visuals }
        else
            tile

    static member SetTileLayer(tile: Tile, tileType: string) = AuthoringTiles.SetTileLayer(tile, tileType, None)

    static member CreateEmptyScene(id: string, name: string, width: float, height: float) : Scene =
        let rows =
            [ let mutable y = 0.0
              while y < height do
                  yield
                      [ let mutable x = 0.0
                        while x < width do
                            yield AuthoringTiles.CreateEmptyTile(x, y, "grass")
                            x <- x + 1.0 ]
                  y <- y + 1.0 ]
        { Scene.Default with Id = id; Name = name; Width = width; Height = height; Tiles = rows }
