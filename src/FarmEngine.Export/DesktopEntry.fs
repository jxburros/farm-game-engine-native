namespace FarmEngine.Export

open System.Text

/// The `.desktop` launcher file next to a Linux game (freedesktop Desktop Entry 1.5).
module DesktopEntry =
    /// A `string`/`localestring` value: backslash, newline, tab and carriage return escaped.
    let escape (value: string) =
        value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r")

    /// The entry for `executable` (the file name in the game folder). `Exec` starts the game
    /// from the folder the entry sits in (`%k`), so the file works from the extracted folder in
    /// launchers that pass it; `Icon` names `<executable>.png` for when the game is installed.
    let create (title: string) (executable: string) (version: string) : string =
        // Exec argument quoting (spec "The Exec key"): inside double quotes, `"`, `$` and `\`
        // are backslash-escaped, then the value's own escaping doubles each backslash.
        let command = sprintf "exec \"$(dirname \"$0\")/%s\"" executable
        let quoted = "\"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$") + "\""
        let builder = StringBuilder()
        let line (key: string) (value: string) = builder.Append(key).Append('=').Append(value).Append('\n') |> ignore
        builder.Append("[Desktop Entry]\n") |> ignore
        line "Type" "Application"
        line "Version" "1.5"
        line "Name" (escape title)
        line "Comment" (escape (sprintf "%s %s" title version))
        line "Exec" ("sh -c " + escape quoted + " %k")
        line "Icon" executable
        line "Terminal" "false"
        line "Categories" "Game;"
        line "StartupWMClass" executable
        builder.ToString()
