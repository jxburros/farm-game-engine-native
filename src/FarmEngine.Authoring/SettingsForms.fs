namespace FarmEngine.Authoring

open System
open FarmEngine.Schemas

/// What the Project Settings view decides beyond single fields (web ProjectSettingsEditor.tsx):
/// the weather odds table, the mine card and the season arrows. The view shows these values
/// and hands back what the creator typed; the clamping and the edits are made here.
module SettingsForms =
    let private finite (value: float) = not (Double.IsNaN value || Double.IsInfinity value)

    /// The weight the weather table shows for one season and weather type: 0 without an entry.
    let weatherWeight (project: GameProject) (seasonId: string) (weatherId: string) : float =
        project.Weather.Table
        |> List.tryFind (fun (key, _) -> key = seasonId)
        |> Option.bind (fun (_, entries) -> entries |> List.tryFind (fun e -> e.WeatherId = weatherId))
        |> Option.map (fun e -> e.Weight)
        |> Option.defaultValue 0.0

    /// The weather input's `Math.max(0, parseFloat(value) || 0)`, with non-finite values as 0.
    let cleanWeight (weight: float) : float = if finite weight && weight > 0.0 then weight else 0.0

    /// The weather table saved as ONE undo step: a `SetWeatherWeight` for each cell whose
    /// weight changed, for seasons and weather types the project has. A weight of 0 removes the
    /// entry. Nothing changed → an empty batch, which `Document.apply` treats as a no-op.
    let weatherOdds (project: GameProject) (weights: (string * string * float) list) : Edit =
        let hasSeason id = project.Settings.Calendar.Seasons |> List.exists (fun s -> s.Id = id)
        let hasType id = project.Weather.Types |> List.exists (fun t -> t.Id = id)
        let edits =
            weights
            |> List.choose (fun (seasonId, weatherId, weight) ->
                let weight = cleanWeight weight
                if hasSeason seasonId && hasType weatherId && weatherWeight project seasonId weatherId <> weight then
                    Some(SetWeatherWeight(seasonId, weatherId, weight))
                else None)
        Batch("Weather odds", edits)

    /// The mine card's fields as the web inputs clamp them: the entrance scene must exist (else
    /// the start scene), entrance coordinates are whole and at least 0, floors at least 1, and the
    /// ladder chance between 0.02 and 1 (0.18 when unreadable). The rest of the config is kept.
    let mine (project: GameProject) (enabled: bool) (entranceSceneId: string) (x: float) (y: float) (floors: float) (ladderChance: float) : MineConfig =
        let current = Defaults.mineEnabled project enabled
        let whole fallback minimum (value: float) = if finite value then max minimum (Math.Truncate value) else fallback
        let sceneId =
            if project.Scenes |> List.exists (fun s -> s.Id = entranceSceneId) then entranceSceneId
            else defaultArg current.EntranceSceneId project.StartSceneId
        { current with
            EntranceSceneId = Some sceneId
            EntranceX = Some(whole 0.0 0.0 x)
            EntranceY = Some(whole 0.0 0.0 y)
            Floors = whole 1.0 1.0 floors
            LadderChance = (if finite ladderChance && ladderChance > 0.0 then min 1.0 (max 0.02 ladderChance) else 0.18) }

    /// Whether the season arrows can move `seasonId` by `delta` (the web disables the up arrow
    /// of the first season and the down arrow of the last).
    let canMoveSeason (project: GameProject) (seasonId: string) (delta: int) : bool =
        let seasons = project.Settings.Calendar.Seasons
        match List.tryFindIndex (fun (s: CalendarSeason) -> s.Id = seasonId) seasons with
        | Some index -> delta <> 0 && index + delta >= 0 && index + delta < seasons.Length
        | None -> false
