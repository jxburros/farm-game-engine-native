module FarmEngine.Export.Tests.IconTests

open Xunit
open FarmEngine.Export
open FarmEngine.Export.Tests.Support
open SkiaSharp

let private ok (result: Result<'T, string>) =
    match result with
    | Ok value -> value
    | Error e -> failwith e

[<Fact>]
let ``icons come out at 16, 32, 48 and 256 pixels as PNG`` () =
    let rendered = Icons.render (makePng 512 512 false) |> ok
    Assert.Equal<int list>([ 16; 32; 48; 256 ], rendered |> List.map fst)
    for size, png in rendered do
        Assert.Equal((size, size), pngSize png)

[<Fact>]
let ``the default icon renders at every size`` () =
    let rendered = Icons.render (Icons.defaultIcon ()) |> ok
    for size, png in rendered do
        Assert.Equal((size, size), pngSize png)

[<Fact>]
let ``a wide image is centred with transparent bands`` () =
    let rendered = Icons.render (makePng 512 256 false) |> ok
    let png = rendered |> List.find (fun (size, _) -> size = 256) |> snd
    use bitmap = SKBitmap.Decode png
    // Top and bottom quarter are padding; the centre has the drawn circle.
    Assert.Equal(0uy, bitmap.GetPixel(128, 10).Alpha)
    Assert.Equal(0uy, bitmap.GetPixel(128, 245).Alpha)
    Assert.Equal(255uy, bitmap.GetPixel(128, 128).Alpha)

[<Fact>]
let ``rendering is deterministic`` () =
    let source = makePng 300 300 false
    Assert.Equal<(int * byte[]) list>(Icons.render source |> ok, Icons.render source |> ok)

[<Fact>]
let ``data URLs must be base64 PNG`` () =
    let png = makePng 256 256 false
    Assert.Equal<byte[]>(png, Icons.decodeDataUrl (dataUrl png) |> ok)
    Assert.True(Result.isError (Icons.decodeDataUrl "data:image/jpeg;base64,AAAA"))
    Assert.True(Result.isError (Icons.decodeDataUrl "data:image/png,rawtext"))
    Assert.True(Result.isError (Icons.decodeDataUrl "data:image/png;base64,***"))
    Assert.True(Result.isError (Icons.render [| 1uy; 2uy; 3uy |]))
