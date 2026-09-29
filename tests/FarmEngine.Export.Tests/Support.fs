/// Fixtures, fake player templates and small helpers shared by the export tests.
module FarmEngine.Export.Tests.Support

open System
open System.IO
open System.Security.Cryptography
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Export
open FarmEngine.Schemas
open SkiaSharp
open Xunit

let fixture (name: string) = Path.Combine(AppContext.BaseDirectory, "Fixtures", name)

/// A temp folder deleted on dispose.
type TempDir() =
    let path = Path.Combine(Path.GetTempPath(), "farm-export-tests-" + Guid.NewGuid().ToString("N"))
    do Directory.CreateDirectory path |> ignore
    member _.Path = path
    member _.Sub(name: string) = Path.Combine(path, name)
    interface IDisposable with
        member _.Dispose() =
            try
                Directory.Delete(path, true)
            with :? IOException ->
                ()

/// The v8 fixture project (the one fixtures/golden/cartridges/project-v8.cart is compiled from).
let v8Project () : GameProject =
    let result = ProjectMigrations.migrateProjectText (File.ReadAllText(fixture "project-v8.json"))
    Assert.True(result.Ok, String.Join("\n", result.Errors))
    result.Data.Value

let starter () = ProjectCatalog.CreateInitialProject(0.0)

let sha256 (bytes: byte[]) = Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

/// Writes a template folder for `target` into `root`: the executable, template.json and licenses.
let writeTemplate (root: string) (target: ExportTarget) (version: string) (executable: byte[]) =
    let folder = Path.Combine(root, target.Id)
    Directory.CreateDirectory folder |> ignore
    File.WriteAllBytes(Path.Combine(folder, target.TemplateExecutable), executable)
    File.WriteAllText(Path.Combine(folder, "THIRD-PARTY.txt"), "Third-party software\n\nMIT License\n")
    File.WriteAllText(
        Path.Combine(folder, "template.json"),
        sprintf """{ "target": "%s", "version": "%s", "sha256": "%s" }""" target.Id version (sha256 executable)
    )
    folder

/// A stand-in Linux player: a shell script.
let fakeLinuxPlayer = Text.Encoding.UTF8.GetBytes "#!/bin/sh\necho fake player\n"

/// Templates for both targets at `version`: the PE fixture (real placeholder resources) for
/// Windows and a script for Linux.
let fakeTemplates (root: string) (version: string) =
    writeTemplate root ExportTarget.WindowsX64 version (File.ReadAllBytes(fixture "player-fixture.exe")) |> ignore
    writeTemplate root ExportTarget.LinuxX64 version fakeLinuxPlayer |> ignore
    root

let options (templates: string) (output: string) (targets: string list) : ExportOptions =
    { Targets = targets
      OutputFolder = output
      TemplatesFolder = Some templates
      CreateArchives = true
      EditorVersion = Some "1.0.0-test" }

/// Every file under `folder`, relative path (with `/`) → bytes.
let readFolder (folder: string) : Map<string, byte[]> =
    Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
    |> Seq.map (fun f -> Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllBytes f)
    |> Map.ofSeq

/// Width and height from a PNG's IHDR.
let pngSize (png: byte[]) =
    Assert.True(png.Length > 24 && png[0] = 0x89uy && png[1] = byte 'P', "not a PNG")
    let big (offset: int) = (int png[offset] <<< 24) ||| (int png[offset + 1] <<< 16) ||| (int png[offset + 2] <<< 8) ||| int png[offset + 3]
    big 16, big 20

/// A PNG drawn with Skia: a filled circle on transparent, or random noise (incompressible).
let makePng (width: int) (height: int) (noise: bool) : byte[] =
    use bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)
    if noise then
        let random = Random(7)
        for y in 0 .. height - 1 do
            for x in 0 .. width - 1 do
                bitmap.SetPixel(x, y, SKColor(byte (random.Next 256), byte (random.Next 256), byte (random.Next 256), 255uy))
    else
        use canvas = new SKCanvas(bitmap)
        canvas.Clear SKColors.Transparent
        use paint = new SKPaint(Color = SKColors.OrangeRed, IsAntialias = true)
        canvas.DrawCircle(float32 width / 2.0f, float32 height / 2.0f, float32 (min width height) / 2.5f, paint)
    use image = SKImage.FromBitmap bitmap
    use data = image.Encode(SKEncodedImageFormat.Png, 100)
    data.ToArray()

let dataUrl (png: byte[]) = "data:image/png;base64," + Convert.ToBase64String png

let asset (id: string) (png: byte[]) : CustomAsset =
    let width, height = pngSize png
    { CustomAsset.Default with Id = id; Name = id; Type = CustomAssetTypes.Art; Width = Some(float width); Height = Some(float height); DataUrl = dataUrl png }

let withAssets (assets: CustomAsset list) (project: GameProject) =
    { project with CustomAssets = project.CustomAssets @ assets }

let withExport (change: ExportSettings -> ExportSettings) (project: GameProject) =
    let settings = project.Export |> Option.defaultWith (fun () -> Defaults.newExportSettings project)
    { project with Export = Some(change settings) }
