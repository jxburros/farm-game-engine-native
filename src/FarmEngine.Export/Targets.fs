namespace FarmEngine.Export

/// A desktop platform Export Game builds for (docs/EXPORT.md "The goal").
[<RequireQualifiedAccess>]
type ExportTarget =
    | WindowsX64
    | LinuxX64

    /// `windows-x64` / `linux-x64`: the id in project settings, template folders and the CLI.
    member this.Id =
        match this with
        | WindowsX64 -> "windows-x64"
        | LinuxX64 -> "linux-x64"

    /// "Windows x64" / "Linux x64".
    member this.DisplayName =
        match this with
        | WindowsX64 -> "Windows x64"
        | LinuxX64 -> "Linux x64"

    /// The player executable inside a template folder.
    member this.TemplateExecutable =
        match this with
        | WindowsX64 -> "farm-player.exe"
        | LinuxX64 -> "farm-player"

    /// The exported executable for a game called `name`.
    member this.ExecutableFile(name: string) =
        match this with
        | WindowsX64 -> name + ".exe"
        | LinuxX64 -> name

    /// `.zip` on Windows, `.tar.gz` on Linux.
    member this.ArchiveExtension =
        match this with
        | WindowsX64 -> ".zip"
        | LinuxX64 -> ".tar.gz"

module ExportTarget =
    /// Every target, in the order the editor lists them.
    let all = [ ExportTarget.WindowsX64; ExportTarget.LinuxX64 ]

    let tryParse (id: string) : ExportTarget option = all |> List.tryFind (fun t -> t.Id = id)

    /// The ids, for messages ("windows-x64 or linux-x64").
    let ids = all |> List.map (fun t -> t.Id)
