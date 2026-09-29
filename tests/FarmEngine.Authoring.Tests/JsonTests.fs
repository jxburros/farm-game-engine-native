/// The JS-semantics helpers of the Fable-safe `Json` DOM and its System.Text.Json conversion.
module FarmEngine.Authoring.Tests.JsonTests

open System.Text.Json.Nodes
open Xunit
open FarmEngine.Authoring
open FarmEngine.Authoring.Net

let private obj (members: (string * Json) list) = JObject members

[<Fact>]
let ``truthiness follows JS`` () =
    for value in [ JNull; JBool false; JNumber 0.0; JNumber nan; JString "" ] do
        Assert.False(Json.truthy value, sprintf "%A" value)
    for value in [ JBool true; JNumber -1.0; JString "0"; JArray []; obj [] ] do
        Assert.True(Json.truthy value, sprintf "%A" value)

[<Fact>]
let ``Number() follows JS for strings, arrays and objects`` () =
    let cases =
        [ JString "  12  ", 12.0
          JString "", 0.0
          JString " \n", 0.0
          JString "0x10", 16.0
          JString "0b101", 5.0
          JString "1e3", 1000.0
          JString "-.5", -0.5
          JString "5.", 5.0
          JString "-Infinity", -infinity
          JBool true, 1.0
          JNull, 0.0
          JArray [], 0.0
          JArray [ JNumber 5.0 ], 5.0
          JArray [ JNull ], 0.0 ]
    for value, expected in cases do
        Assert.Equal(expected, Json.toNumber value)
    for value in [ JString "abc"; JString "1,000"; JString "1e"; JString "0x"; JString "+-1"; JString "infinity"; obj []; JArray [ JNumber 1.0; JNumber 2.0 ] ] do
        Assert.True(System.Double.IsNaN(Json.toNumber value), sprintf "%A" value)

[<Fact>]
let ``number formatting matches JS Number.prototype.toString`` () =
    let cases =
        [ 0.0, "0"; -0.0, "0"; 1.0, "1"; -1.5, "-1.5"; 0.1, "0.1"; 0.3 - 0.1, "0.19999999999999998"
          1e21, "1e+21"; 1e20, "100000000000000000000"; 123456789012345680000.0, "123456789012345680000"
          1e-6, "0.000001"; 1e-7, "1e-7"; 1.5e-7, "1.5e-7"; 5e-324, "5e-324"
          1.7976931348623157e308, "1.7976931348623157e+308"; 4294967295.0, "4294967295"; 0.000123, "0.000123"
          12345.678, "12345.678"; nan, "NaN"; infinity, "Infinity"; -infinity, "-Infinity" ]
    for value, expected in cases do
        Assert.Equal(expected, JsNumber.format value)

[<Fact>]
let ``parse reads JSON like JSON.parse, with comments and trailing commas`` () =
    let text = """{ "a": [1, 2.5e1, -0.5, true, null, "x\"\u00e9\n"], /* note */ "b": {}, "a2": [], // end
      "c": { "d": "e", }, }"""
    let expected =
        JObject
            [ "a", JArray [ JNumber 1.0; JNumber 25.0; JNumber -0.5; JBool true; JNull; JString "x\"é\n" ]
              "b", JObject []
              "a2", JArray []
              "c", JObject [ "d", JString "e" ] ]
    Assert.Equal(Ok expected, Json.parse text)
    Assert.Equal(Ok(JObject [ "k", JNumber 2.0 ]), Json.parse """{"k":1,"k":2}""")
    for bad in [ ""; "{"; "[1,,2]"; "01"; "1.e5"; "{\"a\" 1}"; "tru"; "\"unterminated"; "[1] 2" ] do
        match Json.parse bad with
        | Ok value -> failwithf "%s parsed as %A" bad value
        | Error _ -> ()

[<Fact>]
let ``stringifyIndented matches JSON.stringify with two spaces`` () =
    let value = JObject [ "a", JArray [ JNumber 1.0; JObject [] ]; "b", JArray []; "c", JObject [ "d", JString "é" ] ]
    Assert.Equal("{\n  \"a\": [\n    1,\n    {}\n  ],\n  \"b\": [],\n  \"c\": {\n    \"d\": \"é\"\n  }\n}", Json.stringifyIndented value)

[<Fact>]
let ``x + n concatenates for strings, arrays and objects`` () =
    Assert.Equal(JNumber 60.0, Json.add JNull 60.0)
    Assert.Equal(JNumber 61.0, Json.add (JBool true) 60.0)
    Assert.Equal(JNumber 65.0, Json.add (JNumber 5.0) 60.0)
    Assert.Equal(JString "560", Json.add (JString "5") 60.0)
    Assert.Equal(JString "1,260", Json.add (JArray [ JNumber 1.0; JNumber 2.0 ]) 60.0)
    Assert.Equal(JString "[object Object]60", Json.add (obj []) 60.0)

[<Fact>]
let ``property keys coerce like a JS object lookup`` () =
    let value =
        obj
            [ "n", JNull
              "d", JNumber 1.5
              "a", JArray [ JNumber 1.0; JNull; JArray [ JNumber 2.0; JString "x" ] ]
              "o", obj [ "k", JNumber 1.0 ]
              "b", JBool false ]
    Assert.Equal("undefined", Json.propertyKey "missing" value)
    Assert.Equal("null", Json.propertyKey "n" value)
    Assert.Equal("1.5", Json.propertyKey "d" value)
    Assert.Equal("1,,2,x", Json.propertyKey "a" value)
    Assert.Equal("[object Object]", Json.propertyKey "o" value)
    Assert.Equal("false", Json.propertyKey "b" value)

[<Fact>]
let ``spread, set, withDefault and remove keep JS member order`` () =
    let value = obj [ "a", JNumber 1.0; "b", JNull; "c", JNumber 3.0 ]
    Assert.Equal(obj [ "a", JNumber 1.0; "b", JNumber 2.0; "c", JNumber 3.0 ], Json.set "b" (JNumber 2.0) value)
    Assert.Equal(obj [ "a", JNumber 1.0; "b", JNull; "c", JNumber 3.0; "d", JBool true ], Json.set "d" (JBool true) value)
    Assert.Equal(obj [ "a", JNumber 1.0; "b", JString "x"; "c", JNumber 3.0 ], Json.withDefault "b" (fun () -> JString "x") value)
    Assert.Equal(value, Json.withDefault "a" (fun () -> failwith "fallback must not run") value)
    Assert.Equal(obj [ "a", JNumber 1.0; "c", JNumber 3.0 ], Json.remove "b" value)
    Assert.Equal(obj [ "0", JString "x"; "1", JNumber 2.0 ], Json.set "1" (JNumber 2.0) (JArray [ JString "x"; JNull ]))
    Assert.Equal<(string * Json) list>([ "0", JString "h"; "1", JString "i" ], Json.spread (JString "hi"))
    Assert.Empty(Json.spread (JNumber 4.0))
    Assert.True(Json.has "b" value)
    Assert.False(Json.has "z" value)
    Assert.Equal(JNull, Json.get "b" value)
    Assert.Equal(JNull, Json.index 5 (JArray [ JNumber 1.0 ]))

[<Fact>]
let ``JS TypeErrors carry the C# messages`` () =
    let error (f: unit -> 'T) =
        try
            f () |> ignore
            failwith "expected a JsTypeError"
        with JsTypeError message ->
            message
    Assert.Equal("Cannot read properties of null", error (fun () -> Json.require JNull))
    Assert.Equal("Expected an array at '$.scenes'", error (fun () -> Json.mapArray "$.scenes" (fun _ v -> v) (JString "x")))
    Assert.Equal(JArray [], Json.mapArray "$.scenes" (fun _ v -> v) JNull)

[<Fact>]
let ``stringify orders index keys first and escapes like JSON.stringify`` () =
    // A lone surrogate built at run time (the compiler rewrites one in a literal).
    let lone = string (char 0xD800)
    let value = obj [ "b", JNumber 1.0; "10", JNull; "a", JString("q\"\n\u0001" + lone); "2", JArray [ JNumber nan; JBool true ] ]
    Assert.Equal("""{"2":[null,true],"10":null,"b":1,"a":"q\"\n\u0001\ud800"}""", Json.stringify value)
    Assert.Equal("""{"2":[null,true],"10":null,"a":"q\"\n\u0001\ud800","b":1}""", Json.stableStringify value)

[<Fact>]
let ``System.Text.Json conversion round-trips and keeps member order`` () =
    let text = """{"z":1,"a":[true,false,null,"s",{"y":2.5,"x":-0.125}],"m":{}}"""
    let node = JsonNode.Parse text
    let json = JsonInterop.ofNode node
    Assert.Equal(
        obj
            [ "z", JNumber 1.0
              "a", JArray [ JBool true; JBool false; JNull; JString "s"; obj [ "y", JNumber 2.5; "x", JNumber -0.125 ] ]
              "m", obj [] ],
        json
    )
    Assert.Equal(json, JsonInterop.ofNode (JsonInterop.toNode json))
    Assert.Equal(text, Json.stringify json)
    let created = JsonObject()
    created["i"] <- JsonValue.Create 42
    created["s"] <- JsonValue.Create "t"
    Assert.Equal(obj [ "i", JNumber 42.0; "s", JString "t" ], JsonInterop.ofNode created)
