namespace FarmEngine.Export

open System
open System.IO
open System.Reflection
open System.Text.Json

/// A prebuilt `farm-player` for one target (docs/EXPORT.md "Player templates"): a folder with
/// the executable, `template.json` (`{ "target", "version", "sha256", "files" }`) and the
/// player's third-party license notices (and for the web, the page files).
type PlayerTemplate =
    { Target: ExportTarget
      Folder: string
      Executable: string
      Licenses: string
      Version: string
      /// Every file export uses, by name (`farm-player`, `THIRD-PARTY.txt`, `index.html`, …),
      /// as read and checked against the manifest. Export uses these bytes, never the files
      /// again, so what was checked is what ships.
      Files: Map<string, byte[]> }

    /// A checked file of the template (`Templates.find` has read every file its target uses).
    member this.Read(name: string) : byte[] =
        match this.Files.TryFind name with
        | Some bytes -> bytes
        | None -> invalidOp (sprintf "The %s player template has no %s." this.Target.DisplayName name)

module Templates =
    /// Overrides where templates are looked up (a folder with `windows-x64/`, `linux-x64/`).
    let EnvironmentVariable = "FARM_PLAYER_TEMPLATES"

    let LicensesFile = "THIRD-PARTY.txt"

    let ManifestFile = "template.json"

    /// This editor's version without build metadata ("0.2.0+abc123" → "0.2.0"). All projects
    /// share one version (Directory.Build.props), so this is the app's and farmc's too.
    let editorVersion =
        let assembly = typeof<PlayerTemplate>.Assembly
        match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null ->
            match assembly.GetName().Version with
            | null -> "0.0.0"
            | version -> version.ToString(3)
        | attribute -> attribute.InformationalVersion.Split('+')[0]

    /// `FARM_PLAYER_TEMPLATES` when set, else `players/` next to the running executable
    /// (where the installer puts them).
    let defaultFolder () : string =
        match Environment.GetEnvironmentVariable EnvironmentVariable with
        | null
        | "" -> Path.Combine(AppContext.BaseDirectory, "players")
        | folder -> folder

    let private text (root: JsonElement) (name: string) =
        match root.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String ->
            match value.GetString() with
            | null -> None
            | s -> Some s
        | _ -> None

    /// The files of `target`'s template besides the executable, which `files` in the manifest
    /// must give a SHA-256 for.
    let checkedFiles (target: ExportTarget) : string list =
        match target with
        | ExportTarget.Web -> LicensesFile :: ExportTarget.webFiles
        | ExportTarget.WindowsX64
        | ExportTarget.LinuxX64 -> [ LicensesFile ]

    let private hashes (root: JsonElement) : Map<string, string> option =
        match root.TryGetProperty "files" with
        | true, files when files.ValueKind = JsonValueKind.Object ->
            Some(
                files.EnumerateObject()
                |> Seq.choose (fun p ->
                    if p.Value.ValueKind = JsonValueKind.String then
                        match p.Value.GetString() with
                        | null -> None
                        | hash -> Some(p.Name, hash)
                    else None)
                |> Map.ofSeq
            )
        | _ -> None

    /// The template for `target` in `root`, checked: the manifest names the same target, its
    /// version equals `version` (the editor refuses other versions), and every file export uses
    /// matches its SHA-256 (`sha256` for the executable, `files` for the license notices and the
    /// web page files). The files are read once, and those bytes are what export writes. Every
    /// failure is one sentence the export report can show.
    let find (root: string) (target: ExportTarget) (version: string) : Result<PlayerTemplate, string> =
        let folder = Path.Combine(root, target.Id)
        let name = target.DisplayName
        let manifest = Path.Combine(folder, ManifestFile)
        if not (File.Exists manifest) then
            Error(sprintf "No %s player template in %s. Install the templates that came with this editor, or point FARM_PLAYER_TEMPLATES at them." name folder)
        else
            let parsed =
                try
                    use document = JsonDocument.Parse(File.ReadAllText manifest)
                    let root = document.RootElement
                    if root.ValueKind <> JsonValueKind.Object then None
                    else Some(text root "target", text root "version", text root "sha256", hashes root)
                with
                | :? JsonException
                | :? IOException -> None
            match parsed with
            | None -> Error(sprintf "The %s player template's %s is not valid JSON." name ManifestFile)
            | Some(Some manifestTarget, _, _, _) when manifestTarget <> target.Id ->
                Error(sprintf "The %s player template is for %s." name manifestTarget)
            | Some(_, Some templateVersion, _, _) when templateVersion <> version ->
                Error(sprintf "The %s player template is version %s, but this editor is version %s. Use the templates that came with this editor." name templateVersion version)
            | Some(Some _, Some templateVersion, Some sha, files) ->
                let files = defaultArg files Map.empty
                let wanted = (target.TemplateExecutable, Some sha) :: [ for file in checkedFiles target -> file, files.TryFind file ]
                let rec load (acc: Map<string, byte[]>) (remaining: (string * string option) list) =
                    match remaining with
                    | [] -> Ok acc
                    | (file, expected) :: rest ->
                        let path = Path.Combine(folder, file)
                        if not (File.Exists path) then Error(sprintf "The %s player template has no %s." name file)
                        else
                            match expected with
                            | None -> Error(sprintf "The %s player template's %s has no checksum for %s. Use the templates that came with this editor." name ManifestFile file)
                            | Some hash ->
                                let bytes = File.ReadAllBytes path
                                if not (String.Equals(Binary.sha256 bytes, hash, StringComparison.OrdinalIgnoreCase)) then
                                    Error(sprintf "The %s player template is damaged: %s does not match its checksum." name file)
                                else load (acc.Add(file, bytes)) rest
                load Map.empty wanted
                |> Result.map (fun loaded ->
                    { Target = target
                      Folder = folder
                      Executable = Path.Combine(folder, target.TemplateExecutable)
                      Licenses = Path.Combine(folder, LicensesFile)
                      Version = templateVersion
                      Files = loaded })
            | Some _ -> Error(sprintf "The %s player template's %s needs target, version and sha256." name ManifestFile)
