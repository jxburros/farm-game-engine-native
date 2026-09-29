namespace FarmEngine.Authoring

/// An immutable JSON value: the raw, untyped data that migrations work on (the TS
/// `Record<string, any>`). Object members keep insertion order, like a JS object built from
/// spreads, so an F# migration and a TS migration that do the same thing produce the same member
/// order. A missing key and a JSON `null` are both "nullish" (`??`); `Json.has` is the
/// `!== undefined` test. There is no `undefined` value: code that would store `undefined`
/// removes the key instead, which is what `JSON.stringify` makes of it.
///
/// Fable-safe (docs/LANGUAGES.md "F# conventions"): no reflection, no System.Text.Json, no I/O.
/// Conversion from and to System.Text.Json lives in FarmEngine.Authoring.Net.
type Json =
    | JNull
    | JBool of bool
    | JNumber of float
    | JString of string
    | JArray of Json list
    | JObject of (string * Json) list

/// Thrown by the JS-semantics helpers where the JavaScript would throw a TypeError (property
/// access on `null`, `.map` on something that is not an array). Migrations turn it into a
/// "Migration failed: …" error, so callers never see it.
exception JsTypeError of string

/// JS number formatting and parsing (`String(n)`, `Number(s)`), shared by the JSON helpers.
module JsNumber =
    open System

    let private isJsWhiteSpace (c: char) =
        match c with
        | '\t' | '\n' | '\u000B' | '\u000C' | '\r' | ' ' | ' ' | ' ' | ' ' | ' ' | ' ' | ' '
        | '　' | '﻿' -> true
        | c -> c >= ' ' && c <= ' '

    /// JS `String.prototype.trim` (JS white space and line terminators, not .NET's set).
    let trim (s: string) : string =
        let mutable start = 0
        let mutable stop = s.Length - 1
        while start <= stop && isJsWhiteSpace s.[start] do
            start <- start + 1
        while stop >= start && isJsWhiteSpace s.[stop] do
            stop <- stop - 1
        if start > stop then "" else s.Substring(start, stop - start + 1)

    let private digitValue (c: char) : int =
        if c >= '0' && c <= '9' then int c - int '0'
        elif c >= 'a' && c <= 'z' then int c - int 'a' + 10
        elif c >= 'A' && c <= 'Z' then int c - int 'A' + 10
        else 99

    /// `0x…`/`0o…`/`0b…` bodies: NaN unless every digit is below the radix.
    let private parseRadix (digits: string) (radix: int) : float =
        if digits.Length = 0 then
            nan
        else
            let mutable value = 0.0
            let mutable ok = true
            for c in digits do
                let d = digitValue c
                if d >= radix then ok <- false else value <- value * float radix + float d
            if ok then value else nan

    /// The JS StrDecimalLiteral grammar without the sign: `digits[.digits][e±digits]`, `.digits…`
    /// or `Infinity`. Anything .NET would also accept (thousands separators, `∞`, hex) is refused.
    let private isDecimalLiteral (s: string) : bool =
        let n = s.Length
        let mutable i = 0
        let digits () =
            let start = i
            while i < n && s.[i] >= '0' && s.[i] <= '9' do
                i <- i + 1
            i - start
        let intDigits = digits ()
        let fracDigits =
            if i < n && s.[i] = '.' then
                i <- i + 1
                digits ()
            else
                0
        if intDigits + fracDigits = 0 then
            false
        else
            if i < n && (s.[i] = 'e' || s.[i] = 'E') then
                i <- i + 1
                if i < n && (s.[i] = '+' || s.[i] = '-') then i <- i + 1
                if digits () = 0 then i <- -1
            i = n

    /// JS `Number(s)` for a string: trims, `""` is 0, `0x`/`0o`/`0b` prefixes, `Infinity`, and
    /// NaN for anything that is not a complete numeric literal.
    let parse (s: string) : float =
        let t = trim s
        if t.Length = 0 then
            0.0
        elif t.Length > 2 && t.[0] = '0' && (t.[1] = 'x' || t.[1] = 'X') then
            parseRadix (t.Substring 2) 16
        elif t.Length > 2 && t.[0] = '0' && (t.[1] = 'o' || t.[1] = 'O') then
            parseRadix (t.Substring 2) 8
        elif t.Length > 2 && t.[0] = '0' && (t.[1] = 'b' || t.[1] = 'B') then
            parseRadix (t.Substring 2) 2
        else
            let negative = t.[0] = '-'
            let body = if t.[0] = '-' || t.[0] = '+' then t.Substring 1 else t
            if body = "Infinity" then
                (if negative then -infinity else infinity)
            elif isDecimalLiteral body then
                // The grammar check leaves only text that .NET and JS parse the same way.
                let value = float body
                if negative then -value else value
            else
                nan

#if FABLE_COMPILER
    /// JS `String(n)` / `` `${n}` `` (Number::toString).
    let format (value: float) : string = string value
#else
    /// JS `String(n)` / `` `${n}` `` (Number::toString): the shortest round-trip digits, laid out
    /// with the ECMAScript rules (plain up to 1e21, exponent below 1e-6). Mirrors C# `Js.Num`.
    let format (value: float) : string =
        if Double.IsNaN value then
            "NaN"
        elif Double.IsPositiveInfinity value then
            "Infinity"
        elif Double.IsNegativeInfinity value then
            "-Infinity"
        elif value = 0.0 then
            "0"
        else
            // .NET Core 3.0+ "R" yields the shortest round-trippable digits, the same digit
            // string ECMAScript's Number::toString picks.
            let r0 = value.ToString("R", Globalization.CultureInfo.InvariantCulture)
            let negative = r0.StartsWith "-"
            let r1 = if negative then r0.Substring 1 else r0
            let ePos = r1.IndexOfAny [| 'E'; 'e' |]
            let exponent, mantissa =
                if ePos >= 0 then
                    int (r1.Substring(ePos + 1)), r1.Substring(0, ePos)
                else
                    0, r1
            let dot = mantissa.IndexOf '.'
            let digits0, intLen0 =
                if dot >= 0 then mantissa.Substring(0, dot) + mantissa.Substring(dot + 1), dot
                else mantissa, mantissa.Length
            // Strip leading zeros (e.g. "0.001" → digits "0001").
            let mutable leading = 0
            while leading < digits0.Length - 1 && digits0.[leading] = '0' do
                leading <- leading + 1
            let digits = digits0.Substring(leading).TrimEnd('0')
            if digits.Length = 0 then
                "0"
            else
                // value = 0.d1d2…dk × 10^n
                let k = digits.Length
                let n = intLen0 - leading + exponent
                let sign = if negative then "-" else ""
                if k <= n && n <= 21 then
                    sign + digits + String('0', n - k)
                elif 0 < n && n <= 21 then
                    sign + digits.Substring(0, n) + "." + digits.Substring n
                elif -6 < n && n <= 0 then
                    sign + "0." + String('0', -n) + digits
                else
                    let e = n - 1
                    let fraction = if k > 1 then "." + digits.Substring 1 else ""
                    sign + digits.Substring(0, 1) + fraction + "e" + (if e >= 0 then "+" else "-") + string (abs e)
#endif

    /// JS `Math.round`: halves round toward +∞ (.NET rounds to even).
    let round (x: float) : float =
        if Double.IsNaN x || Double.IsInfinity x then
            x
        else
            let f = Math.Floor x
            if x - f >= 0.5 then f + 1.0 else f

/// JS-semantics helpers over `Json`, for code ported from TypeScript that works on raw
/// `Record<string, any>` data (migrations). Each helper names the JS expression it stands for.
[<RequireQualifiedAccess>]
module Json =
    // ── Reading ────────────────────────────────────────────────────────────

    /// `raw && typeof raw === 'object'` (arrays are objects in JS).
    let isObjectLike (value: Json) : bool =
        match value with
        | JObject _
        | JArray _ -> true
        | _ -> false

    /// `value ?? …` applies: JSON null (missing keys read as `JNull` too).
    let isNullish (value: Json) : bool =
        match value with
        | JNull -> true
        | _ -> false

    let tryGet (key: string) (value: Json) : Json option =
        match value with
        | JObject members -> members |> List.tryFind (fun (k, _) -> k = key) |> Option.map snd
        | _ -> None

    /// Optional-chaining read `obj?.key`: `JNull` for non-objects, missing keys and JSON null.
    let get (key: string) (value: Json) : Json = tryGet key value |> Option.defaultValue JNull

    /// `obj.key !== undefined` (a JSON null counts as defined).
    let has (key: string) (value: Json) : bool = (tryGet key value).IsSome

    /// `arr?.[i]`: `JNull` out of range and for non-arrays.
    let index (i: int) (value: Json) : Json =
        match value with
        | JArray items when i >= 0 -> items |> List.tryItem i |> Option.defaultValue JNull
        | _ -> JNull

    /// `a ?? b`.
    let orElse (fallback: Json) (value: Json) : Json = if isNullish value then fallback else value

    /// The string value, or `None` for anything that is not a JSON string.
    let asString (value: Json) : string option =
        match value with
        | JString s -> Some s
        | _ -> None

    /// JS truthiness: null, false, 0, NaN and "" are falsy; objects and arrays are truthy.
    let truthy (value: Json) : bool =
        match value with
        | JNull -> false
        | JBool b -> b
        | JNumber n -> n <> 0.0 && not (System.Double.IsNaN n)
        | JString s -> s.Length > 0
        | JArray _
        | JObject _ -> true

    /// JS `String(x)` (and the key `obj[x]` coerces `x` to): arrays join their elements with
    /// ",", null elements as "", objects are "[object Object]".
    let rec toJsString (value: Json) : string =
        match value with
        | JNull -> "null"
        | JBool true -> "true"
        | JBool false -> "false"
        | JNumber n -> JsNumber.format n
        | JString s -> s
        | JArray items ->
            items
            |> List.map (fun item -> if isNullish item then "" else toJsString item)
            |> String.concat ","
        | JObject _ -> "[object Object]"

    /// JS `Number(x)`. Objects and arrays convert through their string form, like JS.
    let toNumber (value: Json) : float =
        match value with
        | JNull -> 0.0
        | JBool b -> if b then 1.0 else 0.0
        | JNumber n -> n
        | JString s -> JsNumber.parse s
        | JArray _
        | JObject _ -> JsNumber.parse (toJsString value)

    /// JS `x + n`: string concatenation when `x` is a string, array or object (their string form),
    /// numeric addition otherwise (`null + 1` is 1, `true + 1` is 2).
    let add (value: Json) (n: float) : Json =
        match value with
        | JString _
        | JArray _
        | JObject _ -> JString(toJsString value + JsNumber.format n)
        | _ -> JNumber(toNumber value + n)

    /// The string a JS object lookup `lookup[obj.key]` coerces `obj.key` to ("undefined" when
    /// the key is missing).
    let propertyKey (key: string) (value: Json) : string =
        match tryGet key value with
        | None -> "undefined"
        | Some v -> toJsString v

    // ── Building ───────────────────────────────────────────────────────────

    /// `{ ...value }` as members: an object's own members; arrays and strings spread to index
    /// keys ("0", "1", …; strings per UTF-16 code unit); other values spread to nothing.
    let spread (value: Json) : (string * Json) list =
        match value with
        | JObject members -> members
        | JArray items -> items |> List.mapi (fun i item -> string i, item)
        | JString s -> [ for i in 0 .. s.Length - 1 -> string i, JString(string s.[i]) ]
        | _ -> []

    /// `{ ...obj, key: v }` on members: replaces the value in place when the key exists (it
    /// keeps its position, like a JS spread), appends it otherwise.
    let setMember (key: string) (v: Json) (members: (string * Json) list) : (string * Json) list =
        if members |> List.exists (fun (k, _) -> k = key) then
            members |> List.map (fun (k, old) -> if k = key then k, v else k, old)
        else
            members @ [ key, v ]

    /// `delete obj[key]` on members.
    let removeMember (key: string) (members: (string * Json) list) : (string * Json) list =
        members |> List.filter (fun (k, _) -> k <> key)

    /// `{ ...value, key: v }`.
    let set (key: string) (v: Json) (value: Json) : Json = JObject(setMember key v (spread value))

    /// `{ ...value, key: value.key ?? fallback() }`: the fallback only runs when the key is
    /// nullish.
    let withDefault (key: string) (fallback: unit -> Json) (value: Json) : Json =
        if isNullish (get key value) then set key (fallback ()) value else JObject(spread value)

    /// `delete copy[key]` on `{ ...value }`.
    let remove (key: string) (value: Json) : Json = JObject(removeMember key (spread value))

    /// The keys of `{ ...value }`, in order.
    let keys (value: Json) : string list = spread value |> List.map fst

    // ── Throwing reads (JS TypeErrors) ─────────────────────────────────────

    /// `value.anything` on null throws a TypeError in JS; everything else reads fine.
    let require (value: Json) : Json =
        match value with
        | JNull -> raise (JsTypeError "Cannot read properties of null")
        | v -> v

    /// `(value ?? []).map(f)` where `path` names the value (`$.scenes[0].tiles`) for the error
    /// when it is not an array. `f` gets each element with its own path.
    let mapArray (path: string) (f: string -> Json -> Json) (value: Json) : Json =
        match value with
        | JNull -> JArray []
        | JArray items -> JArray(items |> List.mapi (fun i item -> f (path + "[" + string i + "]") item))
        | _ -> raise (JsTypeError("Expected an array at '" + path + "'"))

    /// `for (const x of value ?? [])`, with the same error as `mapArray`.
    let elements (path: string) (value: Json) : Json list =
        match value with
        | JNull -> []
        | JArray items -> items
        | _ -> raise (JsTypeError("Expected an array at '" + path + "'"))

    // ── Writing ────────────────────────────────────────────────────────────

    let private hexDigit (d: int) : string = "0123456789abcdef".Substring(d, 1)

    let private unicodeEscape (c: char) : string =
        let code = int c
        "\\u" + hexDigit ((code >>> 12) &&& 0xF) + hexDigit ((code >>> 8) &&& 0xF) + hexDigit ((code >>> 4) &&& 0xF) + hexDigit (code &&& 0xF)

    let private isHighSurrogate (c: char) = int c >= 0xD800 && int c <= 0xDBFF
    let private isLowSurrogate (c: char) = int c >= 0xDC00 && int c <= 0xDFFF

    /// `JSON.stringify` of a string, quotes included (well-formed: lone surrogates escaped).
    let quote (s: string) : string =
        let sb = System.Text.StringBuilder(s.Length + 2)
        sb.Append '"' |> ignore
        let mutable i = 0
        while i < s.Length do
            let c = s.[i]
            match c with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\b' -> sb.Append "\\b" |> ignore
            | '\f' -> sb.Append "\\f" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.Append(unicodeEscape c) |> ignore
            | c when isHighSurrogate c ->
                if i + 1 < s.Length && isLowSurrogate s.[i + 1] then
                    sb.Append(c).Append(s.[i + 1]) |> ignore
                    i <- i + 1
                else
                    sb.Append(unicodeEscape c) |> ignore
            | c when isLowSurrogate c -> sb.Append(unicodeEscape c) |> ignore
            | c -> sb.Append c |> ignore
            i <- i + 1
        sb.Append '"' |> ignore
        sb.ToString()

    /// Canonical array-index keys ("0", no leading zero, ≤ 2^32 − 2), which JS objects enumerate
    /// first, in numeric order.
    let private arrayIndex (key: string) : float option =
        if key.Length = 0 || key.Length > 10 || (key.Length > 1 && key.[0] = '0') then
            None
        elif key |> Seq.forall (fun c -> c >= '0' && c <= '9') then
            let value = float key
            if value <= 4294967294.0 then Some value else None
        else
            None

    /// Members in the order a JS object would enumerate them: index keys numerically first,
    /// then the rest in the given order.
    let jsKeyOrder (members: (string * Json) list) : (string * Json) list =
        let indexed, rest = members |> List.partition (fun (k, _) -> (arrayIndex k).IsSome)
        if List.isEmpty indexed then members
        else (indexed |> List.sortBy (fun (k, _) -> (arrayIndex k).Value)) @ rest

    let rec private write (sorted: bool) (sb: System.Text.StringBuilder) (value: Json) : unit =
        match value with
        | JNull -> sb.Append "null" |> ignore
        | JBool b -> sb.Append(if b then "true" else "false") |> ignore
        | JNumber n ->
            // JSON.stringify writes non-finite numbers as null.
            let finite = not (System.Double.IsNaN n || System.Double.IsInfinity n)
            sb.Append(if finite then JsNumber.format n else "null") |> ignore
        | JString s -> sb.Append(quote s) |> ignore
        | JArray items ->
            sb.Append '[' |> ignore
            items
            |> List.iteri (fun i item ->
                if i > 0 then sb.Append ',' |> ignore
                write sorted sb item)
            sb.Append ']' |> ignore
        | JObject members ->
            let ordered =
                if sorted then members |> List.sortWith (fun (a, _) (b, _) -> compare a b) |> jsKeyOrder
                else jsKeyOrder members
            sb.Append '{' |> ignore
            ordered
            |> List.iteri (fun i (k, v) ->
                if i > 0 then sb.Append ',' |> ignore
                sb.Append(quote k).Append(':') |> ignore
                write sorted sb v)
            sb.Append '}' |> ignore

    /// `JSON.stringify(value)`: compact, members in JS enumeration order.
    let stringify (value: Json) : string =
        let sb = System.Text.StringBuilder()
        write false sb value
        sb.ToString()

    let rec private writeIndented (sb: System.Text.StringBuilder) (indent: string) (value: Json) : unit =
        match value with
        | JArray [] -> sb.Append "[]" |> ignore
        | JObject [] -> sb.Append "{}" |> ignore
        | JArray items ->
            let inner = indent + "  "
            sb.Append "[\n" |> ignore
            items
            |> List.iteri (fun i item ->
                if i > 0 then sb.Append ",\n" |> ignore
                sb.Append inner |> ignore
                writeIndented sb inner item)
            sb.Append('\n').Append(indent).Append(']') |> ignore
        | JObject members ->
            let inner = indent + "  "
            sb.Append "{\n" |> ignore
            jsKeyOrder members
            |> List.iteri (fun i (k, v) ->
                if i > 0 then sb.Append ",\n" |> ignore
                sb.Append(inner).Append(quote k).Append(": ") |> ignore
                writeIndented sb inner v)
            sb.Append('\n').Append(indent).Append('}') |> ignore
        | scalar -> write false sb scalar

    /// `JSON.stringify(value, null, 2)`: two-space indentation, members in JS enumeration order.
    let stringifyIndented (value: Json) : string =
        let sb = System.Text.StringBuilder()
        writeIndented sb "" value
        sb.ToString()

    // ── Reading text ───────────────────────────────────────────────────────

    exception private ParseFailure of string

    /// Parses JSON text (`JSON.parse`, plus line and block comments and trailing commas, which
    /// hand-edited project files sometimes carry). Duplicate keys keep the last value in the
    /// first key's position, like a JS object.
    let parse (text: string) : Result<Json, string> =
        let mutable i = 0
        let fail (message: string) : 'T = raise (ParseFailure(sprintf "%s at position %d" message i))
        let rec skip () =
            while i < text.Length && (text.[i] = ' ' || text.[i] = '\t' || text.[i] = '\n' || text.[i] = '\r') do
                i <- i + 1
            if i + 1 < text.Length && text.[i] = '/' && text.[i + 1] = '/' then
                while i < text.Length && text.[i] <> '\n' do
                    i <- i + 1
                skip ()
            elif i + 1 < text.Length && text.[i] = '/' && text.[i + 1] = '*' then
                let close = text.IndexOf("*/", i + 2)
                if close < 0 then fail "Unterminated comment"
                i <- close + 2
                skip ()
        let expect (c: char) =
            if i < text.Length && text.[i] = c then i <- i + 1 else fail (sprintf "Expected '%c'" c)
        let hex (c: char) =
            if c >= '0' && c <= '9' then int c - int '0'
            elif c >= 'a' && c <= 'f' then int c - int 'a' + 10
            elif c >= 'A' && c <= 'F' then int c - int 'A' + 10
            else fail "Invalid \\u escape"
        let readString () : string =
            expect '"'
            let sb = System.Text.StringBuilder()
            let mutable closed = false
            while not closed do
                if i >= text.Length then fail "Unterminated string"
                let c = text.[i]
                i <- i + 1
                if c = '"' then closed <- true
                elif c = '\\' then
                    if i >= text.Length then fail "Unterminated string"
                    let e = text.[i]
                    i <- i + 1
                    match e with
                    | '"' -> sb.Append '"' |> ignore
                    | '\\' -> sb.Append '\\' |> ignore
                    | '/' -> sb.Append '/' |> ignore
                    | 'b' -> sb.Append '\b' |> ignore
                    | 'f' -> sb.Append '\f' |> ignore
                    | 'n' -> sb.Append '\n' |> ignore
                    | 'r' -> sb.Append '\r' |> ignore
                    | 't' -> sb.Append '\t' |> ignore
                    | 'u' ->
                        if i + 4 > text.Length then fail "Invalid \\u escape"
                        let code = (hex text.[i] <<< 12) ||| (hex text.[i + 1] <<< 8) ||| (hex text.[i + 2] <<< 4) ||| hex text.[i + 3]
                        sb.Append(char code) |> ignore
                        i <- i + 4
                    | _ -> fail "Invalid escape"
                elif c < ' ' then fail "Control character in string"
                else sb.Append c |> ignore
            sb.ToString()
        let readNumber () : Json =
            let start = i
            if i < text.Length && text.[i] = '-' then i <- i + 1
            let digits () =
                let from = i
                while i < text.Length && text.[i] >= '0' && text.[i] <= '9' do
                    i <- i + 1
                i - from
            let intStart = i
            let intDigits = digits ()
            if intDigits = 0 then fail "Invalid number"
            // No leading zeros: `01` is not JSON.
            if intDigits > 1 && text.[intStart] = '0' then fail "Invalid number"
            if i < text.Length && text.[i] = '.' then
                i <- i + 1
                if digits () = 0 then fail "Invalid number"
            if i < text.Length && (text.[i] = 'e' || text.[i] = 'E') then
                i <- i + 1
                if i < text.Length && (text.[i] = '+' || text.[i] = '-') then i <- i + 1
                if digits () = 0 then fail "Invalid number"
            JNumber(JsNumber.parse (text.Substring(start, i - start)))
        let literal (word: string) (value: Json) =
            if i + word.Length <= text.Length && text.Substring(i, word.Length) = word then
                i <- i + word.Length
                value
            else fail "Unexpected token"
        let rec readValue () : Json =
            skip ()
            if i >= text.Length then fail "Unexpected end of JSON input"
            match text.[i] with
            | '{' ->
                i <- i + 1
                let members = ResizeArray<string * Json>()
                skip ()
                let mutable closed = false
                while not closed do
                    skip ()
                    if i < text.Length && text.[i] = '}' then
                        i <- i + 1
                        closed <- true
                    else
                        let key = readString ()
                        skip ()
                        expect ':'
                        let value = readValue ()
                        match Seq.tryFindIndex (fun (k, _) -> k = key) members with
                        | Some index -> members.[index] <- (key, value)
                        | None -> members.Add((key, value))
                        skip ()
                        if i < text.Length && text.[i] = ',' then i <- i + 1
                        elif i < text.Length && text.[i] = '}' then ()
                        else fail "Expected ',' or '}'"
                JObject(List.ofSeq members)
            | '[' ->
                i <- i + 1
                let items = ResizeArray<Json>()
                let mutable closed = false
                while not closed do
                    skip ()
                    if i < text.Length && text.[i] = ']' then
                        i <- i + 1
                        closed <- true
                    else
                        items.Add(readValue ())
                        skip ()
                        if i < text.Length && text.[i] = ',' then i <- i + 1
                        elif i < text.Length && text.[i] = ']' then ()
                        else fail "Expected ',' or ']'"
                JArray(List.ofSeq items)
            | '"' -> JString(readString ())
            | 't' -> literal "true" (JBool true)
            | 'f' -> literal "false" (JBool false)
            | 'n' -> literal "null" JNull
            | c when c = '-' || (c >= '0' && c <= '9') -> readNumber ()
            | _ -> fail "Unexpected token"
        try
            let value = readValue ()
            skip ()
            if i < text.Length then fail "Unexpected text after JSON"
            Ok value
        with ParseFailure message ->
            Error message

    /// `stableStringify` (engine-core hash.ts): keys sorted by UTF-16 code unit, then index keys
    /// first as a JS object enumerates them. The same text as C# `StableJStringify`.
    let stableStringify (value: Json) : string =
        let sb = System.Text.StringBuilder()
        write true sb value
        sb.ToString()
