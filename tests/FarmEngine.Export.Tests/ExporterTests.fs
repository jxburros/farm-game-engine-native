module FarmEngine.Export.Tests.ExporterTests

open System
open System.Formats.Tar
open System.IO
open System.IO.Compression
open Xunit
open FarmEngine.Authoring
open FarmEngine.Export
open FarmEngine.Export.Tests.Support
open FarmEngine.Schemas

let private both = [ "windows-x64"; "linux-x64" ]

/// A project with an explicit identity, so file names are predictable.
let private willowCreek () =
    starter ()
    |> withExport (fun s ->
        { s with
            Title = Some "Willow Creek Farm"
            ExecutableName = Some "WillowCreek"
            Version = Some "1.2.0"
            Company = Some "Willow Games" })

let private export (project: GameProject) (templates: string) (output: string) (targets: string list) =
    Exporter.run (options templates output targets) project

let private target (report: ExportReport) (id: string) = report.Targets |> List.find (fun t -> t.Target = id)

[<Fact>]
let ``each target gets the folder layout EXPORT.md describes`` () =
    use dir = new TempDir()
    let report = export (willowCreek ()) (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") both
    Assert.True(report.Ok, Exporter.format report)
    let windows = target report "windows-x64"
    Assert.Equal(Path.Combine(dir.Sub "out", "windows-x64", "WillowCreek"), windows.Folder)
    Assert.Equal<string list>([ "WillowCreek.exe"; "game.cart"; "licenses/THIRD-PARTY.txt" ], windows.Files |> List.map (fun f -> f.Path))
    Assert.Equal<string list>([ "WillowCreek.exe"; "game.cart"; "licenses/THIRD-PARTY.txt" ], readFolder windows.Folder |> Map.keys |> List.ofSeq)
    Assert.Equal(Path.Combine(dir.Sub "out", "WillowCreek-windows-x64.zip"), windows.Archive)
    let linux = target report "linux-x64"
    Assert.Equal<string list>(
        [ "WillowCreek"; "WillowCreek.desktop"; "WillowCreek.png"; "game.cart"; "licenses/THIRD-PARTY.txt" ],
        readFolder linux.Folder |> Map.keys |> List.ofSeq
    )
    Assert.Equal(Path.Combine(dir.Sub "out", "WillowCreek-linux-x64.tar.gz"), linux.Archive)
    // Report sizes are the file sizes; the cartridge is the compiler's.
    for file in windows.Files @ linux.Files do
        Assert.True(file.Size > 0L)
    let cart = CartridgeCompiler.Compile(willowCreek ())
    Assert.Equal<byte[]>(cart, File.ReadAllBytes(Path.Combine(linux.Folder, "game.cart")))
    Assert.Equal(sha256 cart, report.CartridgeSha256)
    Assert.Equal("Third-party software\n\nMIT License\n", File.ReadAllText(Path.Combine(linux.Folder, "licenses", "THIRD-PARTY.txt")))
    Assert.Equal<byte[]>(fakeLinuxPlayer, File.ReadAllBytes(Path.Combine(linux.Folder, "WillowCreek")))
    if not (OperatingSystem.IsWindows()) then
        Assert.True(File.GetUnixFileMode(Path.Combine(linux.Folder, "WillowCreek")).HasFlag UnixFileMode.UserExecute)

[<Fact>]
let ``the Windows exe carries the game's icon and version info`` () =
    use dir = new TempDir()
    let icon = makePng 256 256 false
    let project =
        willowCreek () |> withAssets [ asset "art-icon" icon ] |> withExport (fun s -> { s with IconAssetId = Some "art-icon" })
    let report = export project (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") both
    Assert.True(report.Ok, Exporter.format report)
    let exe = File.ReadAllBytes(Path.Combine((target report "windows-x64").Folder, "WillowCreek.exe"))
    match PeResources.readVersion exe |> Result.bind VersionInfo.parse with
    | Error e -> failwith e
    | Ok version ->
        Assert.Equal((1us, 2us, 0us, 0us), version.Numbers)
        Assert.Contains(("ProductName", "Willow Creek Farm"), version.Strings)
        Assert.Contains(("FileDescription", "Willow Creek Farm"), version.Strings)
        Assert.Contains(("CompanyName", "Willow Games"), version.Strings)
        Assert.Contains(("LegalCopyright", "© Willow Games"), version.Strings)
        Assert.Contains(("OriginalFilename", "WillowCreek.exe"), version.Strings)
        Assert.Contains(("ProductVersion", "1.2.0"), version.Strings)
    let expected = match Icons.render icon with Ok r -> r | Error e -> failwith e
    match PeResources.readIcons exe with
    | Error e -> failwith e
    | Ok icons ->
        for size, png in expected do
            Assert.Equal<byte[]>(png, (icons |> List.find (fun i -> i.Width = size)).Data)
    // Linux gets the same 256×256 image.
    Assert.Equal<byte[]>(expected |> List.find (fun (s, _) -> s = 256) |> snd, File.ReadAllBytes(Path.Combine((target report "linux-x64").Folder, "WillowCreek.png")))

[<Fact>]
let ``the .desktop file names the game, its binary and its icon`` () =
    use dir = new TempDir()
    let report = export (willowCreek ()) (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") [ "linux-x64" ]
    let text = File.ReadAllText(Path.Combine((target report "linux-x64").Folder, "WillowCreek.desktop"))
    let expected =
        [ "[Desktop Entry]"
          "Type=Application"
          "Version=1.5"
          "Name=Willow Creek Farm"
          "Comment=Willow Creek Farm 1.2.0"
          """Exec=sh -c "exec \\"\\$(dirname \\"\\$0\\")/WillowCreek\\"" %k"""
          "Icon=WillowCreek"
          "Terminal=false"
          "Categories=Game;"
          "StartupWMClass=WillowCreek"
          "" ]
    Assert.Equal(String.Join("\n", expected), text)
    Assert.Equal("Line\\nbreak \\\\ tab\\t", DesktopEntry.escape "Line\nbreak \\ tab\t")

[<Fact>]
let ``two exports of the same project are byte-identical`` () =
    use dir = new TempDir()
    let templates = fakeTemplates (dir.Sub "templates") "1.0.0-test"
    let first = export (willowCreek ()) templates (dir.Sub "a") both
    let second = export (willowCreek ()) templates (dir.Sub "b") both
    Assert.True(first.Ok && second.Ok)
    for id in both do
        let a, b = target first id, target second id
        Assert.Equal<byte[]>(File.ReadAllBytes a.Archive, File.ReadAllBytes b.Archive)
        Assert.Equal<Map<string, byte[]>>(readFolder a.Folder, readFolder b.Folder)
    // Exporting again into the same folder replaces the files and gives the same bytes.
    let zip = File.ReadAllBytes (target first "windows-x64").Archive
    let again = export (willowCreek ()) templates (dir.Sub "a") both
    Assert.True(again.Ok, Exporter.format again)
    Assert.Equal<byte[]>(zip, File.ReadAllBytes (target again "windows-x64").Archive)

[<Fact>]
let ``archives hold the folder with the execute bit on the Linux binary`` () =
    use dir = new TempDir()
    let report = export (willowCreek ()) (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") both
    use tar = new MemoryStream()
    (use gz = new GZipStream(File.OpenRead (target report "linux-x64").Archive, CompressionMode.Decompress)
     gz.CopyTo tar)
    tar.Position <- 0L
    use reader = new TarReader(tar)
    let modes =
        [ let mutable entry = reader.GetNextEntry()
          while not (isNull entry) do
              yield entry.Name, entry.Mode
              entry <- reader.GetNextEntry() ]
        |> Map.ofList
    Assert.True(modes["WillowCreek/WillowCreek"].HasFlag UnixFileMode.OtherExecute)
    Assert.False(modes["WillowCreek/game.cart"].HasFlag UnixFileMode.UserExecute)
    Assert.True(modes.ContainsKey "WillowCreek/licenses/THIRD-PARTY.txt")
    use zip = ZipFile.OpenRead (target report "windows-x64").Archive
    Assert.Equal<string list>(
        [ "WillowCreek/WillowCreek.exe"; "WillowCreek/game.cart"; "WillowCreek/licenses/THIRD-PARTY.txt" ],
        [ for e in zip.Entries -> e.FullName ]
    )

[<Fact>]
let ``Problems errors block export and nothing is written`` () =
    use dir = new TempDir()
    let broken = { willowCreek () with SelectedTileType = "lava" }
    let report = export broken (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") both
    Assert.True(report.Blocked)
    Assert.False(report.Ok)
    Assert.NotEmpty(report.Errors)
    Assert.Empty(report.Targets)
    Assert.False(Directory.Exists(dir.Sub "out"))
    Assert.Contains("Export stopped", Exporter.format report)

[<Fact>]
let ``warnings are reported and do not block`` () =
    use dir = new TempDir()
    let project = willowCreek () |> withAssets [ asset "art-scratch" (makePng 16 16 false) ]
    let report = export project (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") [ "linux-x64" ]
    Assert.True(report.Ok, Exporter.format report)
    Assert.Contains(report.Warnings, fun w -> w.Contains "art-scratch")
    Assert.Contains("warning: customAssets[0]", Exporter.format report)

[<Fact>]
let ``a missing template fails only its own target`` () =
    use dir = new TempDir()
    let templates = dir.Sub "templates"
    writeTemplate templates ExportTarget.LinuxX64 "1.0.0-test" fakeLinuxPlayer |> ignore
    let report = export (willowCreek ()) templates (dir.Sub "out") both
    Assert.False(report.Ok)
    Assert.False(report.Blocked)
    let windows = target report "windows-x64"
    Assert.Contains("No Windows x64 player template", String.Join(" ", windows.Errors))
    Assert.Null(windows.Folder)
    Assert.True((target report "linux-x64").Ok)
    Assert.False(Directory.Exists(Path.Combine(dir.Sub "out", "windows-x64")))

[<Fact>]
let ``a template from another editor version is refused`` () =
    use dir = new TempDir()
    let report = export (willowCreek ()) (fakeTemplates (dir.Sub "templates") "0.9.9") (dir.Sub "out") both
    for t in report.Targets do
        Assert.Contains("this editor is version 1.0.0-test", String.Join(" ", t.Errors))

[<Fact>]
let ``bad options are errors, and foreign files in the game folder are kept`` () =
    use dir = new TempDir()
    let templates = fakeTemplates (dir.Sub "templates") "1.0.0-test"
    let bad = export (willowCreek ()) templates (dir.Sub "out") [ "macos-arm64" ]
    Assert.Contains("Unknown export target macos-arm64", String.Join(" ", bad.Errors))
    Assert.Contains("Choose at least one", String.Join(" ", (export (willowCreek ()) templates (dir.Sub "out") []).Errors))
    let folder = Path.Combine(dir.Sub "out", "linux-x64", "WillowCreek")
    Directory.CreateDirectory folder |> ignore
    File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine")
    let report = export (willowCreek ()) templates (dir.Sub "out") [ "linux-x64" ]
    Assert.Contains("did not write (notes.txt)", String.Join(" ", (target report "linux-x64").Errors))
    Assert.Equal("mine", File.ReadAllText(Path.Combine(folder, "notes.txt")))

[<Fact>]
let ``no archives when they are not wanted`` () =
    use dir = new TempDir()
    let report =
        Exporter.run { options (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") both with CreateArchives = false } (willowCreek ())
    Assert.True(report.Ok)
    for t in report.Targets do
        Assert.Null(t.Archive)
    Assert.Empty(Directory.GetFiles(dir.Sub "out"))

[<Fact>]
let ``the C# facade summarizes, exports and remembers targets`` () =
    use dir = new TempDir()
    let project = willowCreek ()
    let summary = GameExporter.Summarize project
    Assert.Equal("Willow Creek Farm", summary.Title)
    Assert.Equal("WillowCreek", summary.ExecutableName)
    Assert.Equal(0, summary.ErrorCount)
    Assert.Equal<string list>(both, summary.Targets)
    Assert.Equal<string list>(both @ [ "web" ], [ for t in GameExporter.Targets -> t.Id ])
    let mutable edit = Unchecked.defaultof<Edit>
    Assert.False(GameExporter.TryRememberTargets(project, both, &edit))
    Assert.True(GameExporter.TryRememberTargets(project, [ "linux-x64" ], &edit))
    let updated = Document.run project edit
    Assert.Equal<string list>([ "linux-x64" ], updated.Export.Value.Targets)
    Assert.Equal(project.Export.Value.GameId, updated.Export.Value.GameId)
    // A project without export settings gets them, with the stable generated game id.
    let plain = starter ()
    Assert.True(plain.Export.IsNone)
    Assert.True(GameExporter.TryRememberTargets(plain, both, &edit))
    Assert.Equal((Defaults.newExportSettings plain).GameId, (Document.run plain edit).Export.Value.GameId)
    let templates = fakeTemplates (dir.Sub "templates") GameExporter.EditorVersion
    let report = GameExporter.Export(project, [ "linux-x64" ], dir.Sub "out", false, templates)
    Assert.True(report.Ok, GameExporter.Format report)
    Assert.Equal("12 B", GameExporter.FormatSize 12L)
    Assert.Equal("1.5 KB", GameExporter.FormatSize 1536L)

[<Fact>]
let ``the web demo is a page with the module, the cartridge and an icon, zipped flat`` () =
    use dir = new TempDir()
    let project = { willowCreek () with Name = "Willow & <Creek>" } |> withExport (fun s -> { s with Title = Some "Willow & <Creek>" })
    let report = export project (fakeTemplates (dir.Sub "templates") "1.0.0-test") (dir.Sub "out") [ "web" ]
    Assert.True(report.Ok, Exporter.format report)
    let web = target report "web"
    let files = readFolder web.Folder
    Assert.Equal<string list>(
        [ "farm_wasm.js"; "farm_wasm_bg.wasm"; "game.cart"; "game.js"; "icon.png"; "index.html"; "licenses/THIRD-PARTY.txt"; "style.css" ],
        files |> Map.toList |> List.map fst)
    // The page carries the game's title, escaped, and a Content-Security-Policy (added here: the
    // fake template's page has none).
    let page = Text.Encoding.UTF8.GetString files.["index.html"]
    Assert.Contains("<title>Willow &amp; &lt;Creek&gt;</title>", page)
    Assert.StartsWith(
        sprintf "<meta http-equiv=\"Content-Security-Policy\" content=\"%s\">" ExportTarget.webContentSecurityPolicy,
        page
    )
    Assert.Equal<byte>(CartridgeCompiler.Compile project, files.["game.cart"])
    Assert.Equal((256, 256), pngSize files.["icon.png"])
    // Browsers need a web server for modules and wasm: the report says so.
    Assert.Contains(web.Warnings, fun w -> w.Contains "web server")
    // itch.io serves index.html from the root of the upload.
    let archive = File.ReadAllBytes web.Archive
    Assert.EndsWith(".zip", web.Archive)
    Assert.Contains("index.html", Text.Encoding.ASCII.GetString archive)
    Assert.DoesNotContain("WillowCreek/index.html", Text.Encoding.ASCII.GetString archive)

[<Fact>]
let ``the web demo page sets its Content-Security-Policy once`` () =
    let policy = ExportTarget.webContentSecurityPolicy
    // Nothing inline may run or style the page, and only its own files load.
    Assert.DoesNotContain("unsafe-inline", policy)
    Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", policy)
    Assert.Contains("default-src 'none'", policy)
    // A page with a policy keeps it; one without gets it at the top of its <head>.
    let own = "<head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'self'\"></head>"
    Assert.Equal(own, Exporter.withContentSecurityPolicy own)
    let added = Exporter.withContentSecurityPolicy "<html><head><title>x</title></head></html>"
    Assert.StartsWith("<html><head>\n<meta http-equiv=\"Content-Security-Policy\"", added)
    Assert.EndsWith("<title>x</title></head></html>", added)
    // The real template carries the same policy, so export never adds a second one.
    let rec repoRoot (folder: string) =
        if File.Exists(Path.Combine(folder, "tools", "wasm", "web-template", "index.html")) then folder
        else
            match Path.GetDirectoryName folder with
            | null -> failwith "tools/wasm/web-template not found above the test folder"
            | parent -> repoRoot parent
    let template = File.ReadAllText(Path.Combine(repoRoot AppContext.BaseDirectory, "tools", "wasm", "web-template", "index.html"))
    Assert.Contains(sprintf "<meta http-equiv=\"Content-Security-Policy\" content=\"%s\">" policy, template)
    Assert.Equal(template, Exporter.withContentSecurityPolicy template)
    Assert.DoesNotContain("<style", template)
    Assert.DoesNotContain("style=\"", template)

[<Fact>]
let ``a web template without its page files is refused`` () =
    use dir = new TempDir()
    let templates = fakeTemplates (dir.Sub "templates") "1.0.0-test"
    File.Delete(Path.Combine(templates, "web", "game.js"))
    let report = export (willowCreek ()) templates (dir.Sub "out") [ "web" ]
    Assert.False report.Ok
    Assert.Contains("has no game.js", String.concat " " (target report "web").Errors)
