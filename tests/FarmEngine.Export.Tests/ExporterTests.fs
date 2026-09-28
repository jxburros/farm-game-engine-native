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
        Records.withValues
            s
            [ "Title", box "Willow Creek Farm"
              "ExecutableName", box "WillowCreek"
              "Version", box "1.2.0"
              "Company", box "Willow Games" ])

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
        willowCreek () |> withAssets [ asset "art-icon" icon ] |> withExport (fun s -> Records.withValue s "IconAssetId" (box "art-icon"))
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
    let broken = Records.withValue (willowCreek ()) "SelectedTileType" (box "lava")
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
    Assert.Equal<string list>(both, [ for t in GameExporter.Targets -> t.Id ])
    let mutable edit = Unchecked.defaultof<Edit>
    Assert.False(GameExporter.TryRememberTargets(project, both, &edit))
    Assert.True(GameExporter.TryRememberTargets(project, [ "linux-x64" ], &edit))
    let updated = Document.run project edit
    Assert.Equal<string seq>([ "linux-x64" ], updated.Export.Targets)
    Assert.Equal(project.Export.GameId, updated.Export.GameId)
    // A project without export settings gets them, with the stable generated game id.
    let plain = starter ()
    Assert.Null(plain.Export)
    Assert.True(GameExporter.TryRememberTargets(plain, both, &edit))
    Assert.Equal((Defaults.newExportSettings plain).GameId, (Document.run plain edit).Export.GameId)
    let templates = fakeTemplates (dir.Sub "templates") GameExporter.EditorVersion
    let report = GameExporter.Export(project, [ "linux-x64" ], dir.Sub "out", false, templates)
    Assert.True(report.Ok, GameExporter.Format report)
    Assert.Equal("12 B", GameExporter.FormatSize 12L)
    Assert.Equal("1.5 KB", GameExporter.FormatSize 1536L)
