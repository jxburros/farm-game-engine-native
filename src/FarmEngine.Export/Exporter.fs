namespace FarmEngine.Export

open System
open System.Globalization
open System.IO
open System.Net
open System.Text
open FarmEngine.Authoring
open FarmEngine.Schemas

/// What to export and where.
type ExportOptions =
    { /// Target ids (`windows-x64`, `linux-x64`).
      Targets: string list
      /// Receives `<target>/<Game>/` folders and the archives.
      OutputFolder: string
      /// Where `windows-x64/` and `linux-x64/` templates are; None for `Templates.defaultFolder`.
      TemplatesFolder: string option
      /// Also write `<Game>-windows-x64.zip` / `<Game>-linux-x64.tar.gz`.
      CreateArchives: bool
      /// The version templates must have; None for this editor's version.
      EditorVersion: string option }

/// One file of an exported folder, relative to the game folder.
type ExportedFile = { Path: string; Size: int64 }

/// The result for one target.
type TargetReport =
    { Target: string
      DisplayName: string
      /// `<output>/<target>/<Game>`, or null when nothing was written.
      Folder: string | null
      /// The archive, or null when none was written.
      Archive: string | null
      ArchiveSize: int64
      Files: ExportedFile list
      Warnings: string list
      Errors: string list }

    member this.Ok = this.Errors.IsEmpty

/// Everything an export did, for the dialog and `farmc export`.
type ExportReport =
    { Title: string
      ExecutableName: string
      Version: string
      GameId: string
      EditorVersion: string
      OutputFolder: string
      CartridgeSize: int64
      /// Lowercase hex SHA-256 of `game.cart` ("" when none was compiled).
      CartridgeSha256: string
      /// Errors that stopped the whole export (Problems errors, bad options, compile failures).
      Errors: string list
      /// Problems warnings and unused assets.
      Warnings: string list
      Targets: TargetReport list }

    /// True when Problems or the options stopped the export before any target.
    member this.Blocked = not this.Errors.IsEmpty

    /// True when every requested target was exported.
    member this.Ok = this.Errors.IsEmpty && not this.Targets.IsEmpty && this.Targets |> List.forall (fun t -> t.Ok)

/// The export identity, with the same fallbacks as the cartridge compiler (`GameInfo`).
type GameIdentity =
    { Title: string
      ExecutableName: string
      Version: string
      GameId: string
      Company: string | null
      Author: string | null
      IconAssetId: string | null
      Targets: string list }

/// Export Game (docs/EXPORT.md): Problems, the deterministic cartridge, then per target a
/// renamed player template, its resources or launcher files, license notices and an archive.
module Exporter =
    let private orNull (value: string | null) =
        match value with
        | null -> null
        | s when String.IsNullOrWhiteSpace s -> null
        | s -> s

    let identity (project: GameProject) : GameIdentity =
        let settings = project.Export |> Option.defaultWith (fun () -> Defaults.newExportSettings project)
        let title = defaultArg settings.Title project.Name
        let version = defaultArg settings.Version project.Version
        { Title = title
          ExecutableName = (match settings.ExecutableName with None -> Defaults.slugId title Seq.empty "game" | Some value -> value)
          Version = version
          GameId = settings.GameId
          Company = orNull (Option.toObj settings.Company)
          Author = orNull (Option.toObj settings.Author)
          IconAssetId = orNull (Option.toObj settings.IconAssetId)
          Targets = List.ofSeq settings.Targets }

    /// Problems errors (block export) and warnings, plus unused assets, as report lines.
    let check (project: GameProject) : string list * string list =
        let problems = Problems.collect project
        let line (p: Problem) = if p.Path = "" then p.Message else p.Path + ": " + p.Message
        let unused = AssetUsage.unused project
        let assetWarnings =
            [ for asset in unused ->
                let index = project.CustomAssets |> List.findIndex (fun a -> obj.ReferenceEquals(a, asset) || a = asset)
                sprintf "customAssets[%d]: Asset \"%s\" (%s) is not used by the game and is left out of it." index asset.Name asset.Id ]
        problems |> Problems.errors |> List.map line,
        (problems |> Problems.warnings |> List.map line) @ assetWarnings

    /// The version resource for a Windows game.
    let versionResource (game: GameIdentity) (editorVersion: string) : VersionResource =
        let owner = match game.Company with null -> game.Author | company -> company
        { Numbers = VersionInfo.parseNumbers game.Version
          Strings =
            [ "CompanyName", (match owner with null -> "" | o -> o)
              "FileDescription", game.Title
              "FileVersion", game.Version
              "InternalName", game.ExecutableName
              "LegalCopyright", (match owner with null -> "" | o -> "© " + o)
              "OriginalFilename", game.ExecutableName + ".exe"
              "ProductName", game.Title
              "ProductVersion", game.Version
              "Comments", "Made with Farming RPG Maker " + editorVersion ] }

    /// The files of one target's game folder, in memory. `icons` has every `Icons.sizes` entry.
    let package
        (game: GameIdentity)
        (target: ExportTarget)
        (template: PlayerTemplate)
        (cart: byte[])
        (icons: (int * byte[]) list)
        (editorVersion: string)
        : Result<PackageFile list, string> =
        let player = File.ReadAllBytes template.Executable
        let licenses = File.ReadAllBytes template.Licenses
        let common =
            [ { Path = "game.cart"; Data = cart; Executable = false }
              { Path = "licenses/" + Templates.LicensesFile; Data = licenses; Executable = false } ]
        match target with
        | ExportTarget.WindowsX64 ->
            let version = VersionInfo.build (versionResource game editorVersion)
            PeResources.patch icons version player
            |> Result.map (fun exe -> { Path = target.ExecutableFile game.ExecutableName; Data = exe; Executable = true } :: common)
        | ExportTarget.LinuxX64 ->
            let icon = icons |> List.find (fun (size, _) -> size = 256) |> snd
            let desktop = DesktopEntry.create game.Title game.ExecutableName game.Version
            Ok(
                [ { Path = target.ExecutableFile game.ExecutableName; Data = player; Executable = true }
                  { Path = game.ExecutableName + ".png"; Data = icon; Executable = false }
                  { Path = game.ExecutableName + ".desktop"; Data = Encoding.UTF8.GetBytes desktop; Executable = false } ]
                @ common
            )
        | ExportTarget.Web ->
            let missing = ExportTarget.webFiles |> List.filter (fun f -> not (File.Exists(Path.Combine(template.Folder, f))))
            if not missing.IsEmpty then
                Error(sprintf "The %s player template has no %s." target.DisplayName (String.Join(", ", missing)))
            else
                let read (name: string) = File.ReadAllBytes(Path.Combine(template.Folder, name))
                let page =
                    File.ReadAllText(Path.Combine(template.Folder, "index.html"))
                        .Replace("{{TITLE}}", WebUtility.HtmlEncode game.Title)
                let icon = icons |> List.find (fun (size, _) -> size = 256) |> snd
                Ok(
                    [ { Path = "index.html"; Data = Encoding.UTF8.GetBytes page; Executable = false }
                      { Path = "game.js"; Data = read "game.js"; Executable = false }
                      { Path = "farm_wasm.js"; Data = read "farm_wasm.js"; Executable = false }
                      { Path = target.TemplateExecutable; Data = player; Executable = false }
                      { Path = "icon.png"; Data = icon; Executable = false } ]
                    @ common
                )
        |> Result.map (List.sortWith (fun a b -> String.CompareOrdinal(a.Path, b.Path)))

    /// Writes `files` into `folder`. A folder that already holds files export would not write
    /// is left alone (it may be the creator's), so a mistyped output folder loses nothing.
    let private writeFolder (folder: string) (files: PackageFile list) : Result<unit, string> =
        let expected = files |> List.map (fun f -> f.Path) |> Set.ofList
        let existing =
            if Directory.Exists folder then
                Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                |> Seq.map (fun f -> Path.GetRelativePath(folder, f).Replace('\\', '/'))
                |> List.ofSeq
            else []
        match existing |> List.filter (fun p -> not (expected.Contains p)) with
        | [] ->
            for file in files do
                let path = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar))
                match Path.GetDirectoryName path with
                | null -> ()
                | directory -> Directory.CreateDirectory directory |> ignore
                File.WriteAllBytes(path, file.Data)
                if file.Executable && not (OperatingSystem.IsWindows()) then
                    File.SetUnixFileMode(path, Archives.modeOf file)
            Ok()
        | foreign ->
            Error(
                sprintf
                    "%s already has files that export did not write (%s). Choose another output folder or remove them."
                    folder
                    (String.Join(", ", foreign |> List.sort |> List.truncate 3))
            )

    let private failedTarget (target: ExportTarget) (errors: string list) =
        { Target = target.Id
          DisplayName = target.DisplayName
          Folder = null
          Archive = null
          ArchiveSize = 0L
          Files = []
          Warnings = []
          Errors = errors }

    let private exportTarget
        (options: ExportOptions)
        (templatesRoot: string)
        (editorVersion: string)
        (game: GameIdentity)
        (cart: byte[])
        (icons: (int * byte[]) list)
        (target: ExportTarget)
        : TargetReport =
        let write (template: PlayerTemplate) (files: PackageFile list) =
            let folder = Path.Combine(options.OutputFolder, target.Id, game.ExecutableName)
            let warnings =
                match target with
                | ExportTarget.WindowsX64 ->
                    match PeResources.read (File.ReadAllBytes template.Executable) with
                    | Ok image when image.SignatureSize > 0 ->
                        [ "The Windows player template is signed; export changes the executable, so sign the game again." ]
                    | _ -> []
                | ExportTarget.LinuxX64 -> []
                | ExportTarget.Web ->
                    [ "Browsers only run the web demo from a web server (an itch.io page, or `python3 -m http.server` in its folder), not from a file:// link." ]
            match writeFolder folder files with
            | Error e -> failedTarget target [ e ]
            | Ok() ->
                let (archive: string | null), archiveSize =
                    if options.CreateArchives then
                        let path = Path.Combine(options.OutputFolder, game.ExecutableName + "-" + target.Id + target.ArchiveExtension)
                        let bytes =
                            match target with
                            | ExportTarget.WindowsX64 -> Archives.zip game.ExecutableName files
                            | ExportTarget.LinuxX64 -> Archives.tarGz game.ExecutableName files
                            // itch.io wants index.html at the root of the zip.
                            | ExportTarget.Web -> Archives.zipFlat files
                        File.WriteAllBytes(path, bytes)
                        path, int64 bytes.Length
                    else null, 0L
                { Target = target.Id
                  DisplayName = target.DisplayName
                  Folder = folder
                  Archive = archive
                  ArchiveSize = archiveSize
                  Files = [ for f in files -> { Path = f.Path; Size = int64 f.Data.Length } ]
                  Warnings = warnings
                  Errors = [] }
        try
            match Templates.find templatesRoot target editorVersion with
            | Error e -> failedTarget target [ e ]
            | Ok template ->
                match package game target template cart icons editorVersion with
                | Error e -> failedTarget target [ e ]
                | Ok files -> write template files
        with
        | :? IOException as error -> failedTarget target [ error.Message ]
        | :? UnauthorizedAccessException as error -> failedTarget target [ error.Message ]

    /// The icon PNGs for every size: the creator's icon asset, or the default icon.
    let icons (project: GameProject) (game: GameIdentity) : Result<(int * byte[]) list, string> =
        match game.IconAssetId with
        | null -> Icons.render (Icons.defaultIcon ())
        | id ->
            match project.CustomAssets |> Seq.tryFind (fun asset -> asset.Id = id) with
            | None -> Error(sprintf "The export icon asset %s was not found." id)
            | Some asset -> Icons.decodeDataUrl asset.DataUrl |> Result.bind Icons.render

    /// Runs an export. Never throws for project, template or file problems; they are in the report.
    let run (options: ExportOptions) (project: GameProject) : ExportReport =
        let game = identity project
        let editorVersion = options.EditorVersion |> Option.defaultValue Templates.editorVersion
        let report =
            { Title = game.Title
              ExecutableName = game.ExecutableName
              Version = game.Version
              GameId = game.GameId
              EditorVersion = editorVersion
              OutputFolder = options.OutputFolder
              CartridgeSize = 0L
              CartridgeSha256 = ""
              Errors = []
              Warnings = []
              Targets = [] }
        let targets = options.Targets |> List.distinct
        let optionErrors =
            [ if targets.IsEmpty then "Choose at least one export target."
              for id in targets do
                  if (ExportTarget.tryParse id).IsNone then
                      sprintf "Unknown export target %s (use %s)." id (String.Join(" or ", ExportTarget.ids))
              if String.IsNullOrWhiteSpace options.OutputFolder then "Choose an output folder." ]
        let problemErrors, warnings = check project
        let report = { report with Warnings = warnings }
        if not optionErrors.IsEmpty || not problemErrors.IsEmpty then
            { report with Errors = optionErrors @ problemErrors }
        else
            let compiled =
                try
                    Ok(CartridgeCompiler.Compile project)
                with :? InvalidOperationException as error ->
                    Error("The cartridge could not be compiled: " + error.Message)
            match compiled, icons project game with
            | Error e, _
            | _, Error e -> { report with Errors = [ e ] }
            | Ok cart, Ok iconImages ->
                let options = { options with OutputFolder = Path.GetFullPath options.OutputFolder }
                let templatesRoot = options.TemplatesFolder |> Option.defaultWith Templates.defaultFolder
                let results =
                    try
                        Directory.CreateDirectory options.OutputFolder |> ignore
                        Ok(
                            [ for id in targets ->
                                exportTarget options templatesRoot editorVersion game cart iconImages (ExportTarget.tryParse id).Value ]
                        )
                    with
                    | :? IOException as error -> Error error.Message
                    | :? UnauthorizedAccessException as error -> Error error.Message
                let report =
                    { report with
                        OutputFolder = options.OutputFolder
                        CartridgeSize = int64 cart.Length
                        CartridgeSha256 = Binary.sha256 cart }
                match results with
                | Error e -> { report with Errors = [ e ] }
                | Ok targets -> { report with Targets = targets }

    /// "12 B", "3.4 KB", "5.6 MB".
    let formatSize (bytes: int64) =
        if bytes < 1024L then sprintf "%d B" bytes
        elif bytes < 1024L * 1024L then String.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", float bytes / 1024.0)
        else String.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", float bytes / 1024.0 / 1024.0)

    /// The report as text (farmc prints this).
    let format (report: ExportReport) : string =
        let text = StringBuilder()
        let line (s: string) = text.Append(s).Append('\n') |> ignore
        line (sprintf "Export Game: %s %s (%s, game id %s)" report.Title report.Version report.ExecutableName report.GameId)
        if report.Blocked then
            line "Export stopped:"
            for e in report.Errors do
                line ("  error: " + e)
        for w in report.Warnings do
            line ("  warning: " + w)
        if report.CartridgeSha256 <> "" then
            line (sprintf "game.cart: %s, sha256 %s" (formatSize report.CartridgeSize) report.CartridgeSha256)
        for target in report.Targets do
            if target.Ok then
                line (sprintf "%s: %s" target.DisplayName target.Folder)
                for file in target.Files do
                    line (sprintf "  %s (%s)" file.Path (formatSize file.Size))
                match target.Archive with
                | null -> ()
                | archive -> line (sprintf "  archive: %s (%s)" archive (formatSize target.ArchiveSize))
            else
                line (sprintf "%s: failed" target.DisplayName)
                for e in target.Errors do
                    line ("  error: " + e)
            for w in target.Warnings do
                line ("  warning: " + w)
        text.ToString()
