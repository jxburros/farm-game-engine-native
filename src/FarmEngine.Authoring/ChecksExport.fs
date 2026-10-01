namespace FarmEngine.Authoring

open System
open FarmEngine.Schemas

/// Native export settings are additive to project schema v8, so the Problems pipeline validates
/// them separately from the web-compatible Zod checks. No export binary is produced yet.
module internal ChecksExport =
    let private gameIdAllowed (id: string) =
        let validPart (part: string) =
            part.Length > 0 && (part.[0] >= 'a' && part.[0] <= 'z' || part.[0] >= '0' && part.[0] <= '9')
            && (part |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-'))
        not (String.IsNullOrWhiteSpace id) && id.Split('.').Length >= 2 && (id.Split('.') |> Array.forall validPart)

    /// Artwork that can be the exported game's icon: a PNG of at least 256×256 pixels.
    let suitableIcon (asset: CustomAsset) =
        asset.DataUrl.ToLowerInvariant().StartsWith("data:image/png;", StringComparison.Ordinal)
        && (match asset.Width, asset.Height with
            | Some width, Some height -> width >= 256.0 && height >= 256.0
            | _ -> false)

    let run (project: GameProject) (sink: Sink) =
        match project.Export with
        | None -> ()
        | Some settings ->
            let error code path message = sink.Error("export." + code, "export." + path, message, Some NavigationTarget.Settings)
            if not (gameIdAllowed settings.GameId) then
                error "gameId" "gameId" "Game id needs at least two lowercase dot-separated parts (for example com.example.farm)."
            if settings.Title = Some "" then error "title" "title" "Export title must not be empty."
            if settings.Version = Some "" then error "version" "version" "Export version must not be empty."
            match settings.ExecutableName with
            | Some name when not (Defaults.executableNameAllowed name) ->
                error
                    "executableName"
                    "executableName"
                    (sprintf
                        "Executable name needs 1 to %d letters, numbers, hyphens or underscores and cannot be a reserved name (a device name such as CON, or licenses)."
                        Defaults.MaxExecutableNameLength)
            | _ -> ()
            if settings.Window.Width < 320 || settings.Window.Width > 8192 || settings.Window.Height < 240 || settings.Window.Height > 8192 then
                error "window" "window" "Window size must be between 320×240 and 8192×8192."
            if settings.PixelScale <> "integer" && settings.PixelScale <> "fit" then
                error "pixelScale" "pixelScale" "Pixel scale must be integer or fit."
            if settings.Targets.IsEmpty then error "targets" "targets" "Select at least one export target."
            else
                settings.Targets
                |> List.iteri (fun i target ->
                    if target <> "windows-x64" && target <> "linux-x64" && target <> "web" then
                        error "target" (sprintf "targets[%d]" i) "Export target must be windows-x64, linux-x64 or web.")
                if (List.distinct settings.Targets).Length <> settings.Targets.Length then
                    error "targets" "targets" "Export targets must not repeat."
            match settings.IconAssetId with
            | None -> ()
            | Some id ->
                match project.CustomAssets |> Seq.tryFind (fun asset -> asset.Id = id) with
                | None -> error "icon" "icon" "Export icon asset was not found."
                | Some asset when not (suitableIcon asset) ->
                    error "icon" "icon" "Export icon must be PNG artwork at least 256×256 pixels."
                | Some _ -> ()
