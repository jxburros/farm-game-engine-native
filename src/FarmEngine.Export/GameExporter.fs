namespace FarmEngine.Export

open System.Collections.Generic
open System.Runtime.InteropServices
open System.Threading
open FarmEngine.Authoring
open FarmEngine.Schemas

/// A target as the editor lists it.
type ExportTargetInfo = { Id: string; DisplayName: string }

/// What the Export Game dialog shows before exporting.
type ExportSummary =
    { Title: string
      ExecutableName: string
      Version: string
      GameId: string
      /// The targets exported last time (the project's `export.targets`).
      Targets: string list
      /// Problems errors: export refuses to run while this is above zero.
      ErrorCount: int
      WarningCount: int }

/// The C# entry points for Export Game (docs/EXPORT.md). The desktop app and `farmc export`
/// call these; every decision is made here, in F#.
[<AbstractClass; Sealed>]
type GameExporter =
    /// Every target, in menu order.
    static member Targets: IReadOnlyList<ExportTargetInfo> =
        [ for t in ExportTarget.all -> { Id = t.Id; DisplayName = t.DisplayName } ] :> _

    /// The version player templates must have: this editor's.
    static member EditorVersion: string = Templates.editorVersion

    /// `FARM_PLAYER_TEMPLATES`, or `players/` next to the running executable.
    static member DefaultTemplatesFolder: string = Templates.defaultFolder ()

    /// The effective title, executable name, version, id and last targets, and how many
    /// Problems stand in the way.
    static member Summarize(project: GameProject) : ExportSummary =
        let game = Exporter.identity project
        let errors, warnings = Exporter.check project
        { Title = game.Title
          ExecutableName = game.ExecutableName
          Version = game.Version
          GameId = game.GameId
          Targets = game.Targets
          ErrorCount = errors.Length
          WarningCount = warnings.Length }

    /// Exports `project` for `targets` into `outputFolder`. Pass null `templatesFolder` for the
    /// default. Never throws: project, template, file and unexpected failures are in the report.
    static member Export
        (
            project: GameProject,
            targets: seq<string>,
            outputFolder: string,
            createArchives: bool,
            [<Optional; DefaultParameterValue(null: string | null)>] templatesFolder: string | null
        ) : ExportReport =
        GameExporter.Export(project, targets, outputFolder, createArchives, templatesFolder, CancellationToken.None)

    /// `Export` that `cancel` can stop between files (`ExportReport.Cancelled`); targets not
    /// finished by then are left as they were. Never throws.
    static member Export
        (
            project: GameProject,
            targets: seq<string>,
            outputFolder: string,
            createArchives: bool,
            templatesFolder: string | null,
            cancel: CancellationToken
        ) : ExportReport =
        Exporter.runCancellable
            cancel
            { Targets = List.ofSeq targets
              OutputFolder = outputFolder
              TemplatesFolder = Option.ofObj templatesFolder
              CreateArchives = createArchives
              EditorVersion = None }
            project

    /// The edit that records `targets` as the ones exported last time (creating the export
    /// settings with their defaults if the project has none). False when nothing changes.
    static member TryRememberTargets(project: GameProject, targets: seq<string>, [<Out>] edit: byref<Edit>) : bool =
        let chosen = targets |> Seq.distinct |> Seq.filter (fun id -> (ExportTarget.tryParse id).IsSome) |> List.ofSeq
        let current = project.Export
        let settings = current |> Option.defaultWith (fun () -> Defaults.newExportSettings project)
        if chosen.IsEmpty || (current.IsSome && List.ofSeq settings.Targets = chosen) then false
        else
            edit <- SetExportSettings(Some { settings with Targets = chosen })
            true

    /// The report as text.
    static member Format(report: ExportReport) : string = Exporter.format report

    /// "3.4 KB".
    static member FormatSize(bytes: int64) : string = Exporter.formatSize bytes
