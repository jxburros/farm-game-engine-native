namespace FarmEngine.Authoring.Net

open System.Text.Json
open System.Text.Json.Nodes
open FarmEngine.Authoring

/// Conversion between System.Text.Json and the Fable-safe `Json` DOM of FarmEngine.Authoring.
/// Object members keep their order both ways.
module JsonInterop =

    // A parsed node wraps a JsonElement and converts directly; a value C# created from another
    // CLR type (an int, say) goes through its JSON form.
    let private number (value: JsonValue) : float =
        try
            value.GetValue<float>()
        with :? System.InvalidOperationException ->
            JsonSerializer.SerializeToElement(value).GetDouble()

    let private text (value: JsonValue) : string =
        let s: string | null =
            try
                value.GetValue<string>()
            with :? System.InvalidOperationException ->
                JsonSerializer.SerializeToElement(value).GetString()
        match s with
        | null -> ""
        | s -> s

    /// A `JsonNode` (null for JSON null) as `Json`.
    let rec ofNode (node: JsonNode | null) : Json =
        match node with
        | null -> JNull
        | :? JsonObject as o -> JObject [ for KeyValue(key, value) in o -> key, ofNode value ]
        | :? JsonArray as a -> JArray [ for item in a -> ofNode item ]
        | :? JsonValue as v ->
            match v.GetValueKind() with
            | JsonValueKind.String -> JString(text v)
            | JsonValueKind.Number -> JNumber(number v)
            | JsonValueKind.True -> JBool true
            | JsonValueKind.False -> JBool false
            | _ -> JNull
        | _ -> JNull

    /// A `JsonElement` as `Json`.
    let rec ofElement (element: JsonElement) : Json =
        match element.ValueKind with
        | JsonValueKind.Object -> JObject [ for p in element.EnumerateObject() -> p.Name, ofElement p.Value ]
        | JsonValueKind.Array -> JArray [ for item in element.EnumerateArray() -> ofElement item ]
        | JsonValueKind.String ->
            match element.GetString() with
            | null -> JNull
            | s -> JString s
        | JsonValueKind.Number -> JNumber(element.GetDouble())
        | JsonValueKind.True -> JBool true
        | JsonValueKind.False -> JBool false
        | _ -> JNull

    /// `Json` as a fresh, detached `JsonNode` (null for JSON null).
    let rec toNode (value: Json) : JsonNode | null =
        match value with
        | JNull -> null
        | JBool b -> JsonValue.Create b
        | JNumber n -> JsonValue.Create n
        | JString s -> JsonValue.Create s
        | JArray items ->
            let array = JsonArray()
            for item in items do
                array.Add(toNode item)
            array
        | JObject members ->
            let obj = JsonObject()
            for key, item in members do
                obj[key] <- toNode item
            obj
