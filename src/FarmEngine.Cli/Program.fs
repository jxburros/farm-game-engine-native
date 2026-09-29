module FarmEngine.Cli.Program

open System
open System.IO
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Export
open FarmEngine.Schemas

let private usage () =
    eprintfn "Usage:"
    eprintfn "  farmc compile <project.json> --out <game.cart>"
    eprintfn "  farmc export <project.json> [--target windows-x64|linux-x64|web]... --out <dir> [--templates <dir>] [--no-archive]"
    eprintfn ""
    eprintfn "export does what Export Game does in the editor (docs/EXPORT.md). Without --target it"
    eprintfn "exports the project's saved targets. Templates come from --templates, then the"
    eprintfn "FARM_PLAYER_TEMPLATES variable, then players/ next to farmc."
    2

/// The migrated project, or the migration errors.
let private load (projectPath: string) : Result<GameProject, string list> =
    let migrated = ProjectMigrations.migrateProjectText (File.ReadAllText projectPath)
    if not migrated.Ok then Error(List.ofSeq migrated.Errors)
    else
        match migrated.Data with
        | None -> Error [ "Migration succeeded without project data." ]
        | Some project -> Ok project

type private ExportArgs =
    { Project: string option
      Targets: string list
      Out: string option
      Templates: string option
      Archives: bool }

let rec private parseExport (args: string list) (acc: ExportArgs) : Result<ExportArgs, string> =
    match args with
    | [] -> Ok acc
    | "--target" :: target :: rest -> parseExport rest { acc with Targets = acc.Targets @ [ target ] }
    | "--out" :: folder :: rest -> parseExport rest { acc with Out = Some folder }
    | "--templates" :: folder :: rest -> parseExport rest { acc with Templates = Some folder }
    | "--no-archive" :: rest -> parseExport rest { acc with Archives = false }
    | option :: _ when option.StartsWith("--", StringComparison.Ordinal) -> Error(sprintf "Unknown or incomplete option %s." option)
    | project :: rest when acc.Project.IsNone -> parseExport rest { acc with Project = Some project }
    | extra :: _ -> Error(sprintf "Unexpected argument %s." extra)

let private export (args: string list) : int =
    match parseExport args { Project = None; Targets = []; Out = None; Templates = None; Archives = true } with
    | Error message ->
        eprintfn "%s" message
        usage ()
    | Ok { Project = None }
    | Ok { Out = None } -> usage ()
    | Ok({ Project = Some projectPath; Out = Some out } as parsed) ->
        match load projectPath with
        | Error errors ->
            for error in errors do
                eprintfn "%s" error
            1
        | Ok project ->
            let targets = if parsed.Targets.IsEmpty then (Exporter.identity project).Targets else parsed.Targets
            let report =
                Exporter.run
                    { Targets = targets
                      OutputFolder = out
                      TemplatesFolder = parsed.Templates
                      CreateArchives = parsed.Archives
                      EditorVersion = None }
                    project
            let text = Exporter.format report
            if report.Ok then printf "%s" text else eprintf "%s" text
            if report.Ok then 0 else 1

let private compile (projectPath: string) (outputPath: string) : int =
    match load projectPath with
    | Error errors ->
        for error in errors do
            eprintfn "%s" error
        1
    | Ok project ->
        let bytes = CartridgeCompiler.Compile project
        let absoluteOutput = Path.GetFullPath outputPath
        match Path.GetDirectoryName absoluteOutput with
        | null -> invalidOp "Output path has no directory."
        | directory -> Directory.CreateDirectory(directory) |> ignore
        File.WriteAllBytes(absoluteOutput, bytes)
        printfn "Compiled %d bytes to %s" bytes.Length absoluteOutput
        0

[<EntryPoint>]
let main argv =
    try
        match List.ofArray argv with
        | [ "compile"; projectPath; "--out"; outputPath ] -> compile projectPath outputPath
        | "export" :: rest -> export rest
        | _ -> usage ()
    with
    | :? IOException as error -> eprintfn "%s" error.Message; 1
    | :? InvalidOperationException as error -> eprintfn "%s" error.Message; 1
    | :? UnauthorizedAccessException as error -> eprintfn "%s" error.Message; 1
