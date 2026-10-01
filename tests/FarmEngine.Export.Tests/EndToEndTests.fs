/// Export with the real player template the build put next to this assembly, then run the
/// exported game headless (docs/EXPORT.md "Tests").
module FarmEngine.Export.Tests.EndToEndTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open FarmEngine.Export
open FarmEngine.Export.Tests.Support

/// FarmEngine.Export's build writes farm_player.status: "built" means a host template must be
/// here (a broken cargo step must not turn these tests green); "skipped" means no Rust toolchain,
/// which fails these tests unless FARM_ALLOW_MISSING_NATIVE=1 allows it.
let private hostTemplate () : (ExportTarget * string) option =
    let status = Path.Combine(AppContext.BaseDirectory, "farm_player.status")
    Assert.True(File.Exists status, "FarmEngine.Export did not write farm_player.status")
    match File.ReadAllText(status).Trim() with
    | "skipped" ->
        Assert.True(
            (Environment.GetEnvironmentVariable "FARM_ALLOW_MISSING_NATIVE" = "1"),
            "The player template was not built (farm_player.status is 'skipped'); set FARM_ALLOW_MISSING_NATIVE=1 to allow that."
        )
        None
    | "built"
    | "prebuilt" ->
        let target = if OperatingSystem.IsWindows() then ExportTarget.WindowsX64 else ExportTarget.LinuxX64
        Some(target, Path.Combine(AppContext.BaseDirectory, "players"))
    | other -> failwithf "Unexpected farm_player.status %s" other

let private run (executable: string) (args: string list) =
    let start = ProcessStartInfo(executable, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    for arg in args do
        start.ArgumentList.Add arg
    use proc = Process.Start start
    let output = proc.StandardOutput.ReadToEnd()
    let error = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    Assert.True((proc.ExitCode = 0), sprintf "%s exited with %d: %s" executable proc.ExitCode error)
    JsonDocument.Parse(output).RootElement.Clone()

[<Fact>]
let ``an exported host game runs headless and reports the game's identity and hash`` () =
    match hostTemplate () with
    | None -> ()
    | Some(target, templates) ->
        use dir = new TempDir()
        let report =
            Exporter.run
                { Targets = [ target.Id ]
                  OutputFolder = dir.Sub "out"
                  TemplatesFolder = Some templates
                  CreateArchives = true
                  EditorVersion = None }
                (v8Project ())
        Assert.True(report.Ok, Exporter.format report)
        let result = report.Targets.Head
        // The exported cartridge is the checked-in golden one: same project, same compiler.
        let golden = fixture (Path.Combine("cartridges", "project-v8.cart"))
        Assert.Equal<byte[]>(File.ReadAllBytes golden, File.ReadAllBytes(Path.Combine(result.Folder, "game.cart")))
        let replay = fixture (Path.Combine("cartridges", "twenty-ticks.json"))
        // The exported game finds game.cart beside itself.
        let exported = run (Path.Combine(result.Folder, target.ExecutableFile report.ExecutableName)) [ "--headless"; "--replay"; replay ]
        // The template itself, told where the golden cartridge is.
        let reference = run (Path.Combine(templates, target.Id, target.TemplateExecutable)) [ "--headless"; "--cart"; golden; "--replay"; replay ]
        Assert.Equal("local.project-1", exported.GetProperty("gameId").GetString())
        Assert.Equal("2.0", exported.GetProperty("gameVersion").GetString())
        Assert.Equal(report.GameId, exported.GetProperty("gameId").GetString())
        Assert.Equal(report.Version, exported.GetProperty("gameVersion").GetString())
        let hash = exported.GetProperty("stateHash").GetString()
        Assert.Equal(16, hash.Length)
        Assert.Equal(reference.GetProperty("stateHash").GetString(), hash)
        Assert.Equal(reference.GetProperty("contentHash").GetString(), exported.GetProperty("contentHash").GetString())
