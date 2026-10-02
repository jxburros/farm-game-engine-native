/// FsCheck properties at the F# untrusted-input boundaries (#116): the JSON parser and the
/// cartridge reader take files from anywhere, so they must answer every input with a value or an
/// error, never an exception. A failure prints FsCheck's `Replay` seed; pass it as
/// `[<Property(Replay = "…")>]` to rerun that case.
module FarmEngine.Authoring.Tests.FuzzPropertyTests

open System
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open FarmEngine.Authoring

// ── JSON parser ────────────────────────────────────────────────────────────

/// Pieces of JSON and of near-JSON: brackets, separators, keys, numbers at and past the double
/// range, escapes, comments, literals and their typos.
let private tokens =
    [ "{"; "}"; "["; "]"; ","; ":"; " "; "\n"; "\"k\""; "\"a\""; "\"\\u00e9\\n\""; "\"\\ud83d\""; "\"\\x\""; "\""
      "1"; "-0.5"; "01"; "1."; ".5"; "1e308"; "1e400"; "-1e999"; "1e-400"; "0x10"; "NaN"; "Infinity"
      "true"; "false"; "null"; "nul"; "//c\n"; "/*c*/"; "/*"; "\\"; "\u0001"; "é" ]

let private nearJson = Gen.elements tokens |> Gen.listOf |> Gen.map (List.truncate 80 >> String.concat "")

[<Property(MaxTest = 500)>]
let ``parsing any near-JSON text gives a value or an error, never an exception`` () =
    Prop.forAll (Arb.fromGen nearJson) (fun text ->
        match Json.parse text with
        | Ok _
        | Error _ -> true)

[<Property(MaxTest = 300)>]
let ``parsing arbitrary text gives a value or an error, never an exception`` (text: NonNull<string>) =
    match Json.parse text.Get with
    | Ok _
    | Error _ -> true

let private jsonString =
    Gen.elements [ 'a'; 'Z'; '0'; ' '; '"'; '\\'; '/'; '\n'; '\t'; '\u0001'; 'é'; '€'; '\ud83d'; '\ude00' ]
    |> Gen.listOf
    |> Gen.map (List.truncate 12 >> Array.ofList >> String)

/// Finite numbers JSON writes exactly: integers, halves and eighths, and doubles near the ends of
/// the range.
let private finiteNumber =
    Gen.oneof
        [ Gen.choose (-1_000_000, 1_000_000) |> Gen.map float
          Gen.choose (-4000, 4000) |> Gen.map (fun n -> float n / 8.0)
          Gen.elements [ 0.1; 1e-7; 1e21; 1.7976931348623157e308; -5e-324; 9007199254740993.0 ] ]

let rec private jsonValue (depth: int) : Gen<Json> =
    let scalars =
        [ Gen.constant JNull
          Gen.elements [ true; false ] |> Gen.map JBool
          finiteNumber |> Gen.map JNumber
          jsonString |> Gen.map JString ]
    if depth = 0 then
        Gen.oneof scalars
    else
        let child = jsonValue (depth - 1)
        // Keys that are not array indexes: JS (and `stringify`) enumerates those first.
        let members =
            Gen.zip (jsonString |> Gen.map (fun key -> "k" + key)) child
            |> Gen.listOf
            |> Gen.map (List.truncate 6 >> List.distinctBy fst)
        Gen.oneof (scalars @ [ Gen.listOf child |> Gen.map (List.truncate 6 >> JArray); members |> Gen.map JObject ])

[<Property(MaxTest = 300)>]
let ``stringify then parse gives the same value`` () =
    Prop.forAll (Arb.fromGen (jsonValue 4)) (fun value ->
        Json.parse (Json.stringify value) = Ok value && Json.parse (Json.stringifyIndented value) = Ok value)

[<Property(MaxTest = 300)>]
let ``a duplicate key keeps its last value in its first position`` () =
    let entries = Gen.zip (Gen.elements [ "a"; "b"; "c"; "d" ]) (Gen.choose (0, 99)) |> Gen.listOf
    Prop.forAll (Arb.fromGen entries) (fun entries ->
        let text =
            entries |> List.map (fun (key, value) -> sprintf "\"%s\":%d" key value) |> String.concat "," |> sprintf "{%s}"
        let expected =
            entries
            |> List.map fst
            |> List.distinct
            |> List.map (fun key -> key, JNumber(float (entries |> List.findBack (fun (k, _) -> k = key) |> snd)))
        Json.parse text = Ok(JObject expected))

[<Property(MaxTest = 50)>]
let ``non-finite numbers are written as null`` () =
    Prop.forAll (Arb.fromGen (Gen.elements [ nan; infinity; -infinity ])) (fun number ->
        Json.stringify (JArray [ JNumber number ]) = "[null]")

// ── Cartridge reader ───────────────────────────────────────────────────────

let private cartridges =
    [ for name in [ "starter-farm"; "project-v8" ] ->
          IO.File.ReadAllBytes(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "fixtures", "golden", "cartridges", name + ".cart")) ]

/// A sample cartridge with a few bytes changed, inserted or cut.
let private editedCartridge =
    gen {
        let! source = Gen.elements cartridges
        let bytes = ResizeArray source
        let! edits = Gen.listOfLength 4 (Gen.zip3 (Gen.choose (0, 2)) (Gen.choose (0, Int32.MaxValue)) (Gen.choose (0, 255)))
        for (kind, at, value) in edits do
            if bytes.Count > 0 then
                let at = at % bytes.Count
                match kind with
                | 0 -> bytes.[at] <- byte value
                | 1 -> bytes.Insert(at, byte value)
                | _ -> bytes.RemoveRange(at, min (value + 1) (bytes.Count - at))
        return bytes.ToArray()
    }

[<Property(MaxTest = 300)>]
let ``reading an edited cartridge gives contents or an error, never an exception`` () =
    Prop.forAll (Arb.fromGen editedCartridge) (fun bytes ->
        match CartridgeReader.read bytes with
        | Ok _
        | Error _ -> true)

[<Property(MaxTest = 300)>]
let ``reading arbitrary bytes behind the identifier gives contents or an error`` (data: byte[]) =
    let bytes = Array.append (Array.sub cartridges.[1] 0 8) (if isNull data then [||] else data)
    match CartridgeReader.read bytes with
    | Ok _
    | Error _ -> true
