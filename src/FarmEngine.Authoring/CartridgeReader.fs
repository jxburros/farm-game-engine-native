namespace FarmEngine.Authoring

/// `GameInfo` in schemas/cart.fbs.
type CartGameInfo =
    { Title: string
      Version: string
      GameId: string
      Author: string option
      Company: string option
      ExecutableName: string option
      WindowWidth: uint32
      WindowHeight: uint32
      Fullscreen: bool
      PixelScale: string option
      Credits: string option }

/// `Asset` in schemas/cart.fbs.
type CartAsset = { Id: string; Mime: string; Data: byte[] }

/// `Plugin` in schemas/cart.fbs.
type CartPlugin =
    { Id: string
      PackId: string
      Source: string
      GrantedHooks: string list
      /// The pack manifest's `permissions.mutations`; None when it declares none (the defaults).
      GrantedMutations: string list option }

/// A cartridge read back: the `Cartridge` table of schemas/cart.fbs with its JSON sections as text.
type CartContents =
    { CartFormat: uint32
      ProjectSchemaVersion: uint32
      Info: CartGameInfo
      ContentJson: string
      StartJson: string
      PresentationJson: string
      Assets: CartAsset list
      Plugins: CartPlugin list }

/// A plain-F# FlatBuffers reader for `.farmcart` files (the counterpart of `CartridgeCompiler`),
/// for tests, tools and hosts without the generated readers. Never throws: a truncated or
/// malformed buffer is an error.
module CartridgeReader =
    exception private Malformed of string

    let private u8 (b: byte[]) (i: int) =
        if i < 0 || i >= b.Length then raise (Malformed(sprintf "offset %d is outside the %d-byte buffer" i b.Length))
        int b.[i]

    let private u16 (b: byte[]) (i: int) = u8 b i ||| (u8 b (i + 1) <<< 8)
    let private i32 (b: byte[]) (i: int) = u8 b i ||| (u8 b (i + 1) <<< 8) ||| (u8 b (i + 2) <<< 16) ||| (u8 b (i + 3) <<< 24)
    let private u32 (b: byte[]) (i: int) = uint32 (i32 b i)

    /// The position of field `index`'s value in the table at `table`, if present.
    let private field (b: byte[]) (table: int) (index: int) : int option =
        let vtable = table - i32 b table
        let vtableSize = u16 b vtable
        let entry = 4 + 2 * index
        if entry >= vtableSize then None
        else
            match u16 b (vtable + entry) with
            | 0 -> None
            | offset -> Some(table + offset)

    let private deref (b: byte[]) (at: int) = at + i32 b at

    let private bytesAt (b: byte[]) (at: int) : int * int =
        let start = deref b at
        let length = i32 b start
        if length < 0 || start + 4 + length > b.Length then raise (Malformed(sprintf "vector at %d runs past the buffer" start))
        start + 4, length

    let private stringAt (b: byte[]) (at: int) =
        let start, length = bytesAt b at
        Bytes.utf8Text b start length

    let private optionalString (b: byte[]) (table: int) (index: int) = field b table index |> Option.map (stringAt b)

    let private requiredString (b: byte[]) (table: int) (index: int) (name: string) =
        match optionalString b table index with
        | Some value -> value
        | None -> raise (Malformed(name + " is missing"))

    let private uint (b: byte[]) (table: int) (index: int) (fallback: uint32) =
        field b table index |> Option.map (u32 b) |> Option.defaultValue fallback

    /// The tables of a vector of tables.
    let private tables (b: byte[]) (table: int) (index: int) : int list =
        match field b table index with
        | None -> []
        | Some at ->
            let start = deref b at
            [ for k in 0 .. i32 b start - 1 -> deref b (start + 4 + 4 * k) ]

    let private strings (b: byte[]) (table: int) (index: int) : string list =
        match field b table index with
        | None -> []
        | Some at ->
            let start = deref b at
            [ for k in 0 .. i32 b start - 1 -> stringAt b (start + 4 + 4 * k) ]

    let private text (b: byte[]) (table: int) (index: int) (name: string) =
        match field b table index with
        | Some at ->
            let start, length = bytesAt b at
            Bytes.utf8Text b start length
        | None -> raise (Malformed(name + " is missing"))

    /// Whether the buffer carries the `FGCT` file identifier.
    let hasIdentifier (bytes: byte[]) : bool =
        bytes.Length >= 8 && bytes.[4] = byte 'F' && bytes.[5] = byte 'G' && bytes.[6] = byte 'C' && bytes.[7] = byte 'T'

    let private readUnchecked (b: byte[]) : CartContents =
        let root = deref b 0
        let info =
            match field b root 2 with
            | Some at -> deref b at
            | None -> raise (Malformed "info is missing")
        { CartFormat = uint b root 0 0u
          ProjectSchemaVersion = uint b root 1 0u
          Info =
            { Title = requiredString b info 0 "info.title"
              Version = requiredString b info 1 "info.version"
              GameId = requiredString b info 2 "info.game_id"
              Author = optionalString b info 3
              Company = optionalString b info 4
              ExecutableName = optionalString b info 5
              WindowWidth = uint b info 6 1280u
              WindowHeight = uint b info 7 800u
              Fullscreen = field b info 8 |> Option.map (fun at -> u8 b at <> 0) |> Option.defaultValue false
              PixelScale = optionalString b info 9
              Credits = optionalString b info 10 }
          ContentJson = text b root 4 "content_json"
          StartJson = text b root 5 "start_json"
          PresentationJson = text b root 6 "presentation_json"
          Assets =
            [ for asset in tables b root 7 ->
                  let data =
                      match field b asset 2 with
                      | Some at ->
                          let start, length = bytesAt b at
                          Array.sub b start length
                      | None -> raise (Malformed "asset data is missing")
                  { Id = requiredString b asset 0 "asset.id"; Mime = requiredString b asset 1 "asset.mime"; Data = data } ]
          Plugins =
            [ for plugin in tables b root 8 ->
                  { Id = requiredString b plugin 0 "plugin.id"
                    PackId = requiredString b plugin 1 "plugin.pack_id"
                    Source = requiredString b plugin 2 "plugin.source"
                    GrantedHooks = strings b plugin 3
                    GrantedMutations = field b plugin 4 |> Option.map (fun _ -> strings b plugin 4) } ] }

    /// Read a cartridge: its tables, or why it could not be read.
    let read (bytes: byte[]) : Result<CartContents, string> =
        if not (hasIdentifier bytes) then Error "Not a cartridge (missing the FGCT identifier)"
        else
            try
                Ok(readUnchecked bytes)
            with
            | Malformed message -> Error("Malformed cartridge: " + message)
