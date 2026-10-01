namespace FarmEngine.Export

open System
open System.Globalization
open System.IO
open System.Net
open System.Text
open System.Threading
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
      /// Errors that stopped the whole export (Problems errors, bad options, compile failures,
      /// cancelling, an unexpected failure).
      Errors: string list
      /// Problems warnings and unused assets.
      Warnings: string list
      Targets: TargetReport list
      /// True when the export was cancelled; `Targets` has the ones finished before that.
      Cancelled: bool }

    /// True when something stopped the export as a whole (Problems, the options, cancelling).
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
          ExecutableName = (match settings.ExecutableName with None -> Defaults.defaultExecutableName title | Some value -> value)
          Version = version
          GameId = settings.GameId
          Company = orNull (Option.toObj settings.Company)
          Author = orNull (Option.toObj settings.Author)
          IconAssetId = orNull (Option.toObj settings.IconAssetId)
          Targets = List.ofSeq settings.Targets }

    /// Problems errors (block export) and warnings, plus unused assets, as report lines. The
    /// effective executable name (set, or made from the title) must be one export can write.
    let check (project: GameProject) : string list * string list =
        let problems = Problems.collect project
        let line (p: Problem) = if p.Path = "" then p.Message else p.Path + ": " + p.Message
        let errors = problems |> Problems.errors |> List.map line
        let name = (identity project).ExecutableName
        let nameError =
            if Defaults.executableNameAllowed name || errors |> List.exists (fun e -> e.StartsWith("export.executableName", StringComparison.Ordinal)) then []
            else
                [ sprintf
                      "export.executableName: The executable name \"%s\" can't be used: it needs 1 to %d letters, numbers, hyphens or underscores and cannot be a reserved name (a device name such as CON, or licenses)."
                      name
                      Defaults.MaxExecutableNameLength ]
        let unused = AssetUsage.unused project
        let assetWarnings =
            [ for asset in unused ->
                let index = project.CustomAssets |> List.findIndex (fun a -> obj.ReferenceEquals(a, asset) || a = asset)
                sprintf "customAssets[%d]: Asset \"%s\" (%s) is not used by the game and is left out of it." index asset.Name asset.Id ]
        errors @ nameError,
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

    /// A web page with `ExportTarget.webContentSecurityPolicy`: a template page that sets no
    /// policy (an older template) gets it as the first element of its `<head>`, or first of all.
    let withContentSecurityPolicy (page: string) : string =
        if page.Contains("Content-Security-Policy", StringComparison.OrdinalIgnoreCase) then
            page
        else
            let meta =
                sprintf "<meta http-equiv=\"Content-Security-Policy\" content=\"%s\">" ExportTarget.webContentSecurityPolicy
            let head = page.IndexOf("<head>", StringComparison.OrdinalIgnoreCase)
            if head >= 0 then page.Insert(head + "<head>".Length, "\n" + meta) else meta + "\n" + page

    /// The files of one target's game folder, in memory. `icons` has every `Icons.sizes` entry.
    /// Template files come from `template.Files`: the bytes `Templates.find` checked.
    let package
        (game: GameIdentity)
        (target: ExportTarget)
        (template: PlayerTemplate)
        (cart: byte[])
        (icons: (int * byte[]) list)
        (editorVersion: string)
        : Result<PackageFile list, string> =
        let player = template.Read target.TemplateExecutable
        let licenses = template.Read Templates.LicensesFile
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
            let page =
                Encoding.UTF8.GetString(template.Read "index.html").Replace("{{TITLE}}", WebUtility.HtmlEncode game.Title)
                |> withContentSecurityPolicy
            let icon = icons |> List.find (fun (size, _) -> size = 256) |> snd
            Ok(
                [ { Path = "index.html"; Data = Encoding.UTF8.GetBytes page; Executable = false }
                  { Path = "game.js"; Data = template.Read "game.js"; Executable = false }
                  { Path = "style.css"; Data = template.Read "style.css"; Executable = false }
                  { Path = "farm_wasm.js"; Data = template.Read "farm_wasm.js"; Executable = false }
                  { Path = target.TemplateExecutable; Data = player; Executable = false }
                  { Path = "icon.png"; Data = icon; Executable = false } ]
                @ common
            )
        |> Result.map (List.sortWith (fun a b -> String.CompareOrdinal(a.Path, b.Path)))

    /// Files a file manager or the OS leaves in folders it shows (`.DS_Store`, `Thumbs.db`,
    /// `desktop.ini`, KDE's `.directory`). They are not the creator's, so they don't block a
    /// re-export; they go with the replaced folder.
    let isOsMetadataFile (path: string) : bool =
        let path = path.Replace('\\', '/')
        match path.Substring(path.LastIndexOf '/' + 1).ToLowerInvariant() with
        | ".ds_store"
        | "thumbs.db"
        | "desktop.ini"
        | ".directory" -> true
        | _ -> false

    /// A symbolic link, junction or other reparse point: export never writes through one.
    let private isLink (info: FileSystemInfo) =
        // LinkTarget also sees a dangling link; Attributes is all ones for a missing path.
        not (isNull info.LinkTarget) || (info.Exists && info.Attributes.HasFlag FileAttributes.ReparsePoint)

    /// Everything under `folder` as (path relative to `root` with `/`, entry), without entering
    /// linked folders.
    let rec private entries (root: string) (folder: DirectoryInfo) : (string * FileSystemInfo) list =
        [ for info in folder.EnumerateFileSystemInfos() do
              yield Path.GetRelativePath(root, info.FullName).Replace('\\', '/'), info
              match info with
              | :? DirectoryInfo as sub when not (isLink sub) -> yield! entries root sub
              | _ -> () ]

    /// Why export must not replace `folder` (None when it may): a link where the folder or
    /// anything in it should be, or files export would not write (it may be the creator's
    /// folder, so a mistyped output folder loses nothing). OS metadata files are fine.
    let private replaceProblem (folder: string) (files: PackageFile list) : string option =
        let info = DirectoryInfo folder
        if isLink info then
            Some(sprintf "%s is a link. Export does not write through links; remove it or choose another output folder." folder)
        elif File.Exists folder then
            Some(sprintf "%s is a file, not a folder. Choose another output folder or remove it." folder)
        elif not info.Exists then None
        else
            let expected = files |> List.map (fun f -> f.Path) |> Set.ofList
            let found = entries folder info
            match found |> List.filter (fun (_, entry) -> isLink entry) with
            | (path, _) :: _ ->
                Some(sprintf "%s holds a link (%s). Export does not write through links; remove it or choose another output folder." folder path)
            | [] ->
                let foreign =
                    found
                    |> List.filter (fun (path, entry) ->
                        not (entry :? DirectoryInfo) && not (expected.Contains path) && not (isOsMetadataFile path))
                    |> List.map fst
                match foreign with
                | [] -> None
                | foreign ->
                    Some(
                        sprintf
                            "%s already has files that export did not write (%s). Choose another output folder or remove them."
                            folder
                            (String.Join(", ", foreign |> List.sort |> List.truncate 3))
                    )

    /// Writes `files` into the new folder `staging` (each file created, never opened through
    /// an existing link).
    let private writeStaging (cancel: CancellationToken) (staging: string) (files: PackageFile list) =
        Directory.CreateDirectory staging |> ignore
        for file in files do
            cancel.ThrowIfCancellationRequested()
            let path = Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar))
            match Path.GetDirectoryName path with
            | null -> ()
            | directory -> Directory.CreateDirectory directory |> ignore
            do
                use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                stream.Write(file.Data, 0, file.Data.Length)
            if file.Executable && not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(path, Archives.modeOf file)

    /// Deletes a staging folder or temp file export made, ignoring failures (it is hidden, and
    /// named so the next export never mistakes it for its own).
    let private tryDelete (path: string) =
        try
            if Directory.Exists path then Directory.Delete(path, true)
            elif File.Exists path then File.Delete path
        with
        | :? IOException
        | :? UnauthorizedAccessException -> ()

    /// Puts `staging` where `folder` is: the old folder is moved aside first (so a locked file,
    /// such as a running game, fails the export before anything changed), then removed.
    /// Returns a warning when the old folder could not be removed.
    let private swapIn (staging: string) (folder: string) (aside: string) : string list =
        if Directory.Exists folder then
            Directory.Move(folder, aside)
            try
                Directory.Move(staging, folder)
            with _ ->
                Directory.Move(aside, folder)
                reraise ()
            tryDelete aside
            if Directory.Exists aside then
                [ sprintf "The previous export was moved to %s but could not be deleted; remove it yourself." aside ]
            else []
        else
            Directory.Move(staging, folder)
            []

    let private failedTarget (target: ExportTarget) (errors: string list) =
        { Target = target.Id
          DisplayName = target.DisplayName
          Folder = null
          Archive = null
          ArchiveSize = 0L
          Files = []
          Warnings = []
          Errors = errors }

    /// One sentence for an exception export did not expect.
    let private describe (error: exn) =
        match error with
        | :? IOException
        | :? UnauthorizedAccessException -> error.Message
        | :? OutOfMemoryException -> "There is not enough memory to export the game."
        | _ -> sprintf "Export failed unexpectedly (%s): %s" (error.GetType().Name) error.Message

    /// One target: package it in memory, write the folder (and archive) under temporary names
    /// next to where they go, then rename them into place. A failure at any point leaves the
    /// previous export as it was, with no half-written files, and fails this target only.
    let private exportTarget
        (cancel: CancellationToken)
        (options: ExportOptions)
        (templatesRoot: string)
        (editorVersion: string)
        (game: GameIdentity)
        (cart: byte[])
        (icons: (int * byte[]) list)
        (target: ExportTarget)
        : TargetReport =
        let write (template: PlayerTemplate) (files: PackageFile list) =
            let parent = Path.Combine(options.OutputFolder, target.Id)
            let folder = Path.Combine(parent, game.ExecutableName)
            let archive =
                if options.CreateArchives then
                    Some(Path.Combine(options.OutputFolder, game.ExecutableName + "-" + target.Id + target.ArchiveExtension))
                else None
            let warnings =
                match target with
                | ExportTarget.WindowsX64 ->
                    match PeResources.read (template.Read target.TemplateExecutable) with
                    | Ok image when image.SignatureSize > 0 ->
                        [ "The Windows player template is signed. Changing the executable breaks a signature, so export removed it; sign the game again if you need one." ]
                    | _ -> []
                | ExportTarget.LinuxX64 -> []
                | ExportTarget.Web ->
                    [ "Browsers only run the web demo from a web server (an itch.io page, or `python3 -m http.server` in its folder), not from a file:// link." ]
            let archiveProblem =
                match archive with
                | Some path when Directory.Exists path || isLink (FileInfo path) ->
                    Some(sprintf "%s is a folder or a link. Export does not write through links; remove it or choose another output folder." path)
                | _ -> None
            let parentProblem =
                if isLink (DirectoryInfo parent) then
                    Some(sprintf "%s is a link. Export does not write through links; remove it or choose another output folder." parent)
                else None
            match parentProblem |> Option.orElse archiveProblem |> Option.orElse (replaceProblem folder files) with
            | Some problem -> failedTarget target [ problem ]
            | None ->
                let unique = Guid.NewGuid().ToString("N")
                let staging = Path.Combine(parent, sprintf ".%s.export-%s" game.ExecutableName unique)
                let aside = Path.Combine(parent, sprintf ".%s.previous-%s" game.ExecutableName unique)
                let archiveTemp = archive |> Option.map (fun path -> Path.Combine(options.OutputFolder, sprintf ".%s.%s.tmp" (Path.GetFileName path) unique))
                try
                    Directory.CreateDirectory parent |> ignore
                    writeStaging cancel staging files
                    let archiveSize =
                        match archiveTemp with
                        | None -> 0L
                        | Some temp ->
                            cancel.ThrowIfCancellationRequested()
                            let bytes =
                                match target with
                                | ExportTarget.WindowsX64 -> Archives.zip game.ExecutableName files
                                | ExportTarget.LinuxX64 -> Archives.tarGz game.ExecutableName files
                                // itch.io wants index.html at the root of the zip.
                                | ExportTarget.Web -> Archives.zipFlat files
                            do
                                use stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                                stream.Write(bytes, 0, bytes.Length)
                            int64 bytes.Length
                    cancel.ThrowIfCancellationRequested()
                    let swapWarnings = swapIn staging folder aside
                    match archive, archiveTemp with
                    | Some path, Some temp -> File.Move(temp, path, true)
                    | _ -> ()
                    { Target = target.Id
                      DisplayName = target.DisplayName
                      Folder = folder
                      Archive = Option.toObj archive
                      ArchiveSize = archiveSize
                      Files = [ for f in files -> { Path = f.Path; Size = int64 f.Data.Length } ]
                      Warnings = warnings @ swapWarnings
                      Errors = [] }
                finally
                    tryDelete staging
                    archiveTemp |> Option.iter tryDelete
        try
            match Templates.find templatesRoot target editorVersion with
            | Error e -> failedTarget target [ e ]
            | Ok template ->
                match package game target template cart icons editorVersion with
                | Error e -> failedTarget target [ e ]
                | Ok files -> write template files
        with
        | :? OperationCanceledException -> reraise ()
        | error -> failedTarget target [ describe error ]

    /// The icon PNGs for every size: the creator's icon asset, or the default icon.
    let icons (project: GameProject) (game: GameIdentity) : Result<(int * byte[]) list, string> =
        match game.IconAssetId with
        | null -> Icons.render (Icons.defaultIcon ())
        | id ->
            match project.CustomAssets |> Seq.tryFind (fun asset -> asset.Id = id) with
            | None -> Error(sprintf "The export icon asset %s was not found." id)
            | Some asset -> Icons.decodeDataUrl asset.DataUrl |> Result.bind Icons.render

    /// Runs an export that `cancel` can stop between files: targets not finished by then are
    /// left as they were (`Cancelled` in the report). Never throws: project, template, file and
    /// unexpected failures are all in the report.
    let runCancellable (cancel: CancellationToken) (options: ExportOptions) (project: GameProject) : ExportReport =
        let editorVersion = options.EditorVersion |> Option.defaultValue Templates.editorVersion
        let empty =
            { Title = project.Name
              ExecutableName = ""
              Version = project.Version
              GameId = ""
              EditorVersion = editorVersion
              OutputFolder = options.OutputFolder
              CartridgeSize = 0L
              CartridgeSha256 = ""
              Errors = []
              Warnings = []
              Targets = []
              Cancelled = false }
        let mutable report = empty
        try
            let game = identity project
            report <-
                { report with
                    Title = game.Title
                    ExecutableName = game.ExecutableName
                    Version = game.Version
                    GameId = game.GameId }
            let targets = options.Targets |> List.distinct
            let optionErrors =
                [ if targets.IsEmpty then "Choose at least one export target."
                  for id in targets do
                      if (ExportTarget.tryParse id).IsNone then
                          sprintf "Unknown export target %s (use %s)." id (String.Join(" or ", ExportTarget.ids))
                  if String.IsNullOrWhiteSpace options.OutputFolder then "Choose an output folder." ]
            let problemErrors, warnings = check project
            report <- { report with Warnings = warnings }
            if not optionErrors.IsEmpty || not problemErrors.IsEmpty then
                { report with Errors = optionErrors @ problemErrors }
            else
                let compiled =
                    try
                        Ok(CartridgeCompiler.Compile project)
                    with
                    | :? OperationCanceledException -> reraise ()
                    | error -> Error("The cartridge could not be compiled: " + error.Message)
                match compiled, icons project game with
                | Error e, _
                | _, Error e -> { report with Errors = [ e ] }
                | Ok cart, Ok iconImages ->
                    let options = { options with OutputFolder = Path.GetFullPath options.OutputFolder }
                    let templatesRoot = options.TemplatesFolder |> Option.defaultWith Templates.defaultFolder
                    report <-
                        { report with
                            OutputFolder = options.OutputFolder
                            CartridgeSize = int64 cart.Length
                            CartridgeSha256 = Binary.sha256 cart }
                    if File.Exists options.OutputFolder then
                        { report with Errors = [ sprintf "%s is a file, not a folder. Choose another output folder." options.OutputFolder ] }
                    else
                        Directory.CreateDirectory options.OutputFolder |> ignore
                        for id in targets do
                            cancel.ThrowIfCancellationRequested()
                            let result = exportTarget cancel options templatesRoot editorVersion game cart iconImages (ExportTarget.tryParse id).Value
                            report <- { report with Targets = report.Targets @ [ result ] }
                        report
        with
        | :? OperationCanceledException ->
            { report with
                Cancelled = true
                Errors = report.Errors @ [ "Export was cancelled. Targets it had not finished were left as they were." ] }
        | error -> { report with Errors = report.Errors @ [ describe error ] }

    /// Runs an export. Never throws: project, template, file and unexpected failures are all in
    /// the report.
    let run (options: ExportOptions) (project: GameProject) : ExportReport = runCancellable CancellationToken.None options project
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
            line (if report.Cancelled then "Export cancelled:" else "Export stopped:")
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
