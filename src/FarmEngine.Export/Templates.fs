namespace FarmEngine.Export

open System
open System.IO
open System.Reflection
open System.Text.Json

/// A prebuilt `farm-player` for one target (docs/EXPORT.md "Player templates"): a folder with
/// the executable, `template.json` (`{ "target", "version", "sha256" }`) and the player's
/// third-party license notices.
type PlayerTemplate =
    { Target: ExportTarget
      Folder: string
      Executable: string
      Licenses: string
      Version: string }

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

    /// The template for `target` in `root`, checked: the manifest names the same target, its
    /// version equals `version` (the editor refuses other versions), and the executable's
    /// SHA-256 matches. Every failure is one sentence the export report can show.
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
                    else Some(text root "target", text root "version", text root "sha256")
                with
                | :? JsonException
                | :? IOException -> None
            match parsed with
            | None -> Error(sprintf "The %s player template's %s is not valid JSON." name ManifestFile)
            | Some(Some manifestTarget, _, _) when manifestTarget <> target.Id ->
                Error(sprintf "The %s player template is for %s." name manifestTarget)
            | Some(_, Some templateVersion, _) when templateVersion <> version ->
                Error(sprintf "The %s player template is version %s, but this editor is version %s. Use the templates that came with this editor." name templateVersion version)
            | Some(Some _, Some templateVersion, Some sha) ->
                let executable = Path.Combine(folder, target.TemplateExecutable)
                let licenses = Path.Combine(folder, LicensesFile)
                if not (File.Exists executable) then Error(sprintf "The %s player template has no %s." name target.TemplateExecutable)
                elif not (File.Exists licenses) then Error(sprintf "The %s player template has no %s." name LicensesFile)
                elif not (String.Equals(Binary.sha256File executable, sha, StringComparison.OrdinalIgnoreCase)) then
                    Error(sprintf "The %s player template is damaged: %s does not match its checksum." name target.TemplateExecutable)
                else
                    Ok
                        { Target = target
                          Folder = folder
                          Executable = executable
                          Licenses = licenses
                          Version = templateVersion }
            | Some _ -> Error(sprintf "The %s player template's %s needs target, version and sha256." name ManifestFile)
