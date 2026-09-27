module FarmEngine.Cli.Program

open System
open System.IO
open FarmEngine.Authoring
open FarmEngine.Authoring.Net

let private usage () =
    eprintfn "Usage: farmc compile <project.json> --out <game.cart>"
    2

[<EntryPoint>]
let main argv =
    match argv with
    | [| "compile"; projectPath; "--out"; outputPath |] ->
        try
            let migrated = ProjectMigrations.migrateProjectText(File.ReadAllText projectPath)
            if not migrated.Ok then
                for error in migrated.Errors do eprintfn "%s" error
                1
            else
                let project =
                    match migrated.Data with
                    | null -> invalidOp "Migration succeeded without project data."
                    | project -> project
                let bytes = CartridgeCompiler.Compile project
                let absoluteOutput = Path.GetFullPath outputPath
                match Path.GetDirectoryName absoluteOutput with
                | null -> invalidOp "Output path has no directory."
                | directory -> Directory.CreateDirectory(directory) |> ignore
                File.WriteAllBytes(absoluteOutput, bytes)
                printfn "Compiled %d bytes to %s" bytes.Length absoluteOutput
                0
        with
        | :? IOException as error -> eprintfn "%s" error.Message; 1
        | :? InvalidOperationException as error -> eprintfn "%s" error.Message; 1
        | :? UnauthorizedAccessException as error -> eprintfn "%s" error.Message; 1
    | _ -> usage ()
