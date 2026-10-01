namespace FarmEngine.Authoring

/// A zod-style issue path, innermost segment first (cheap to extend while decoding).
type Path = string list

/// The first value of the wrong kind a decoder met: the zod path (`scenes.0.width`) and message.
exception DecodeError of path: string * message: string

/// Readers for the schema decoders (SchemaJson.fs). Each takes the path of the value it reads and
/// raises `DecodeError` for a value of the wrong kind. Fable-safe.
module Decode =
    /// `scenes.0.width` from a reversed path.
    let pathText (path: Path) : string = path |> List.rev |> String.concat "."

    /// zod's name for the kind of a JSON value (`received …`).
    let kind (json: Json) : string =
        match json with
        | JNull -> "null"
        | JBool _ -> "boolean"
        | JNumber n when System.Double.IsNaN n -> "nan"
        | JNumber _ -> "number"
        | JString _ -> "string"
        | JArray _ -> "array"
        | JObject _ -> "object"

    let fail (path: Path) (message: string) : 'T = raise (DecodeError(pathText path, message))

    let expected (what: string) (path: Path) (json: Json) : 'T = fail path ("Expected " + what + ", received " + kind json)

    let object (path: Path) (json: Json) : (string * Json) list =
        match json with
        | JObject members -> members
        | _ -> expected "object" path json

    let string (path: Path) (json: Json) : string =
        match json with
        | JString s -> s
        | _ -> expected "string" path json

    /// A number JSON can write back: `JSON.stringify` writes ±Infinity as null, which would no
    /// longer load, so it is refused here (zod `.finite()`). `Json.parse` refuses `1e400` already;
    /// this covers `Json` built in code.
    let private finite (path: Path) (n: float) : float =
        if System.Double.IsInfinity n then fail path "Number must be finite" else n

    let number (path: Path) (json: Json) : float =
        match json with
        | JNumber n -> finite path n
        | _ -> expected "number" path json

    let boolean (path: Path) (json: Json) : bool =
        match json with
        | JBool b -> b
        | _ -> expected "boolean" path json

    /// A 32-bit integer (`int` fields).
    let int32 (path: Path) (json: Json) : int =
        match json with
        | JNumber n when System.Double.IsInfinity n -> finite path n |> int
        | JNumber n when n = System.Math.Floor n && n >= -2147483648.0 && n <= 2147483647.0 -> int n
        | JNumber _ -> fail path "Expected integer, received float"
        | _ -> expected "number" path json

    /// An unsigned 32-bit integer (xoshiro state words).
    let uint32 (path: Path) (json: Json) : uint32 =
        match json with
        | JNumber n when System.Double.IsInfinity n -> finite path n |> uint32
        | JNumber n when n = System.Math.Floor n && n >= 0.0 && n <= 4294967295.0 -> uint32 n
        | JNumber _ -> fail path "Expected a tuple of 4 integers"
        | _ -> expected "number" path json

    /// Any JSON value, kept as is.
    let json (_: Path) (json: Json) : Json = json

    /// A flag value: `boolean | number | string`.
    let flagValue (path: Path) (json: Json) : Json =
        match json with
        | JBool _
        | JNumber _
        | JString _ -> json
        | _ -> expected "boolean, number or string" path json

    /// An optional or nullable field: `null` is absent.
    let optional (decode: Path -> Json -> 'T) (path: Path) (json: Json) : 'T option =
        match json with
        | JNull -> None
        | _ -> Some(decode path json)

    /// A `.nullable().optional()` field whose `null` differs from absent (`Some None`).
    let optionalNullable (decode: Path -> Json -> 'T) (path: Path) (json: Json) : 'T option option =
        match json with
        | JNull -> Some None
        | _ -> Some(Some(decode path json))

    let list (decode: Path -> Json -> 'T) (path: Path) (json: Json) : 'T list =
        match json with
        | JArray items -> items |> List.mapi (fun i item -> decode (Operators.string i :: path) item)
        | _ -> expected "array" path json

    /// A `z.record(…)`: members in order.
    let dict (decode: Path -> Json -> 'T) (path: Path) (json: Json) : (string * 'T) list =
        match json with
        | JObject members -> members |> List.map (fun (key, value) -> key, decode (key :: path) value)
        | _ -> expected "object" path json

    /// The `type` of a discriminated union member.
    let discriminator (path: Path) (json: Json) : string =
        match Json.tryGet "type" (JObject(object path json)) with
        | Some(JString tag) -> tag
        | _ -> fail ("type" :: path) "Invalid discriminator value"

    let unknownDiscriminator (path: Path) (_tag: string) : 'T = fail ("type" :: path) "Invalid discriminator value"

    /// Runs a decoder on a whole document: the value, or `path: message` for the first issue.
    let run (decode: Path -> Json -> 'T) (json: Json) : Result<'T, string> =
        try
            Ok(decode [] json)
        with DecodeError(path, message) ->
            Error(path + ": " + message)

/// Writers for the schema encoders (SchemaJson.fs).
module Encode =
    let list (encode: 'T -> Json) (items: 'T list) : Json = JArray(items |> List.map encode)

    let dict (encode: 'T -> Json) (members: (string * 'T) list) : Json =
        JObject(members |> List.map (fun (key, value) -> key, encode value))
