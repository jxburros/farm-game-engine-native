module FarmEngine.Cli.Program

open System
open System.IO
open System.Reflection
open FarmEngine.Authoring
open FarmEngine.Authoring.Net
open FarmEngine.Export
open FarmEngine.Schemas

let private usageText =
    String.concat
        Environment.NewLine
        [ "Usage:"
          "  farmc compile <project.json> --out <game.cart>"
          "  farmc export <project.json> [--target windows-x64|linux-x64|web]... --out <dir> [--templates <dir>] [--no-archive]"
          "  farmc --help | --version"
          ""
          "Options may come in any order. export does what Export Game does in the editor"
          "(docs/EXPORT.md). Without --target it exports the project's saved targets. Templates"
          "come from --templates, then the FARM_PLAYER_TEMPLATES variable, then players/ next to"
          "farmc."
          ""
          "Exit codes: 0 done, 1 the command failed, 2 the command line is wrong." ]

/// The version farmc was built at (`-p:Version=…`, without a source revision suffix).
let version () : string =
    let assembly = Assembly.GetExecutingAssembly()
    match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
    | null -> string (assembly.GetName().Version)
    | attribute ->
        let text = attribute.InformationalVersion
        match text.IndexOf '+' with
        | -1 -> text
        | plus -> text.Substring(0, plus)

/// The command line after the command: positional arguments and options, in any order.
type Arguments =
    { Positional: string list
      Targets: string list
      Out: string option
      Templates: string option
      Archives: bool
      Help: bool }

/// No arguments yet (where `parseArguments` starts).
let emptyArguments = { Positional = []; Targets = []; Out = None; Templates = None; Archives = true; Help = false }

/// Options that take a value (the next argument).
let private valued = set [ "--target"; "--out"; "--templates" ]

let private isOption (arg: string) = arg.StartsWith("--", StringComparison.Ordinal) || arg = "-h"

/// One parser for every command. A value that looks like an option (`--out --no-archive`) is an
/// error, not a folder named "--no-archive".
let rec parseArguments (args: string list) (acc: Arguments) : Result<Arguments, string> =
    match args with
    | [] -> Ok { acc with Positional = List.rev acc.Positional }
    | flag :: rest when valued.Contains flag ->
        match rest with
        | value :: rest when not (isOption value) && value.Length > 0 ->
            let acc =
                match flag with
                | "--target" -> { acc with Targets = acc.Targets @ [ value ] }
                | "--out" -> { acc with Out = Some value }
                | _ -> { acc with Templates = Some value }
            parseArguments rest acc
        | _ -> Error(sprintf "%s needs a value." flag)
    | "--no-archive" :: rest -> parseArguments rest { acc with Archives = false }
    | ("--help" | "-h") :: rest -> parseArguments rest { acc with Help = true }
    | option :: _ when isOption option -> Error(sprintf "Unknown option %s." option)
    | positional :: rest -> parseArguments rest { acc with Positional = positional :: acc.Positional }

/// The migrated project, or the migration errors.
let private load (projectPath: string) : Result<GameProject, string list> =
    let migrated = ProjectMigrations.migrateProjectText (File.ReadAllText projectPath)
    if not migrated.Ok then Error(List.ofSeq migrated.Errors)
    else
        match migrated.Data with
        | None -> Error [ "Migration succeeded without project data." ]
        | Some project -> Ok project

let private export (args: Arguments) (out: TextWriter) (err: TextWriter) : int =
    match args.Positional, args.Out with
    | [ projectPath ], Some folder ->
        match load projectPath with
        | Error errors ->
            for error in errors do
                err.WriteLine error
            1
        | Ok project ->
            let targets = if args.Targets.IsEmpty then (Exporter.identity project).Targets else args.Targets
            let report =
                Exporter.run
                    { Targets = targets
                      OutputFolder = folder
                      TemplatesFolder = args.Templates
                      CreateArchives = args.Archives
                      EditorVersion = None }
                    project
            let text = Exporter.format report
            if report.Ok then out.Write text else err.Write text
            if report.Ok then 0 else 1
    | _ -> 2

let private compile (args: Arguments) (out: TextWriter) (err: TextWriter) : int =
    match args.Positional, args.Out with
    | [ projectPath ], Some outputPath when args.Targets.IsEmpty && args.Templates.IsNone && args.Archives ->
        match load projectPath with
        | Error errors ->
            for error in errors do
                err.WriteLine error
            1
        | Ok project ->
            let bytes = CartridgeCompiler.Compile project
            let absoluteOutput = Path.GetFullPath outputPath
            match Path.GetDirectoryName absoluteOutput with
            | null -> invalidOp "Output path has no directory."
            | directory -> Directory.CreateDirectory(directory) |> ignore
            File.WriteAllBytes(absoluteOutput, bytes)
            out.WriteLine(sprintf "Compiled %d bytes to %s" bytes.Length absoluteOutput)
            0
    | _ -> 2

/// What a usage error says beyond the usage text.
let private problem (command: string) (args: Arguments) : string =
    match args.Positional, args.Out with
    | [], _ -> sprintf "%s needs a project file." command
    | _ :: _ :: _, _ -> sprintf "%s takes one project file." command
    | _, None -> sprintf "%s needs --out." command
    | _ when command = "compile" -> "compile takes no --target, --templates or --no-archive."
    | _ -> sprintf "Wrong arguments for %s." command

/// farmc with its output streams: the exit code (0 done, 1 failed, 2 usage). Never throws.
let run (argv: string[]) (out: TextWriter) (err: TextWriter) : int =
    let usageError (message: string) =
        if message.Length > 0 then err.WriteLine message
        err.WriteLine usageText
        2
    try
        match List.ofArray argv with
        | [ "--version" ] ->
            out.WriteLine("farmc " + version ())
            0
        | [ "--help" ]
        | [ "-h" ]
        | [ "help" ] ->
            out.WriteLine usageText
            0
        | ("compile" | "export" as command) :: rest ->
            match parseArguments rest emptyArguments with
            | Error message -> usageError message
            | Ok args when args.Help ->
                out.WriteLine usageText
                0
            | Ok args ->
                let code = if command = "compile" then compile args out err else export args out err
                if code = 2 then usageError (problem command args) else code
        | [] -> usageError ""
        | command :: _ when isOption command -> usageError (sprintf "Unknown option %s." command)
        | command :: _ -> usageError (sprintf "Unknown command %s." command)
    with error ->
        // A bad path, an unreadable file, a project Problems refuses, a failed export: the
        // message, never a stack trace.
        err.WriteLine error.Message
        1

[<EntryPoint>]
let main argv = run argv Console.Out Console.Error
