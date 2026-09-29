namespace FarmEngine.Export

/// A platform Export Game builds for (docs/EXPORT.md "The goal"): the two desktop targets, and
/// the web demo (farm-wasm in a page, for itch.io and the like).
[<RequireQualifiedAccess>]
type ExportTarget =
    | WindowsX64
    | LinuxX64
    | Web

    /// `windows-x64` / `linux-x64` / `web`: the id in project settings, template folders and the CLI.
    member this.Id =
        match this with
        | WindowsX64 -> "windows-x64"
        | LinuxX64 -> "linux-x64"
        | Web -> "web"

    /// "Windows x64" / "Linux x64" / "Web (browser demo)".
    member this.DisplayName =
        match this with
        | WindowsX64 -> "Windows x64"
        | LinuxX64 -> "Linux x64"
        | Web -> "Web (browser demo)"

    /// The player inside a template folder (for the web, the WebAssembly module), the file the
    /// template's checksum covers.
    member this.TemplateExecutable =
        match this with
        | WindowsX64 -> "farm-player.exe"
        | LinuxX64 -> "farm-player"
        | Web -> "farm_wasm_bg.wasm"

    /// The file that starts a game called `name`.
    member this.ExecutableFile(name: string) =
        match this with
        | WindowsX64 -> name + ".exe"
        | LinuxX64 -> name
        | Web -> "index.html"

    /// `.zip` on Windows and for the web (itch.io takes a zip with `index.html` at its root),
    /// `.tar.gz` on Linux.
    member this.ArchiveExtension =
        match this with
        | WindowsX64
        | Web -> ".zip"
        | LinuxX64 -> ".tar.gz"

module ExportTarget =
    /// Every target, in the order the editor lists them.
    let all = [ ExportTarget.WindowsX64; ExportTarget.LinuxX64; ExportTarget.Web ]

    /// The files a web template holds besides the module and its manifest.
    let webFiles = [ "farm_wasm.js"; "game.js"; "index.html" ]

    let tryParse (id: string) : ExportTarget option = all |> List.tryFind (fun t -> t.Id = id)

    /// The ids, for messages ("windows-x64 or linux-x64").
    let ids = all |> List.map (fun t -> t.Id)
