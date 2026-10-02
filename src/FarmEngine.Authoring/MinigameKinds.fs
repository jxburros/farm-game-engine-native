namespace FarmEngine.Authoring

/// The value a minigame setting holds.
[<RequireQualifiedAccess>]
type MinigameSettingKind =
    | Number
    | Integer
    | Text

/// One config setting a built-in minigame kind reads. An absent setting (or one that is not the
/// right type) plays with `Default`; numbers are clamped to `Min`..`Max` by the player.
type MinigameSetting =
    { Key: string
      Label: string
      Kind: MinigameSettingKind
      /// What the player uses when the setting is absent (for text, the built-in wording, which
      /// the player shows in its own language).
      Default: string
      Min: float option
      Max: float option }

/// A minigame kind the player ships with.
type MinigameKind =
    { Id: string
      Name: string
      /// One line for the editor: what the player does and how the score comes about.
      Help: string
      Settings: MinigameSetting list }

/// The minigame kinds built into the player (Rust `farm_runtime::minigames::BUILT_IN_KINDS`; a
/// Rust test checks that both lists name the same kinds and settings). A def with any other kind
/// plays as a single "Go!" button that scores 0.5; Problems warns about it. New kinds need the
/// engine's Rust code, so creators pick from this list.
module MinigameKinds =
    let private number key label (fallback: float) min max =
        { Key = key; Label = label; Kind = MinigameSettingKind.Number; Default = string fallback; Min = Some min; Max = max }
    let private integer key label (fallback: int) min max =
        { Key = key; Label = label; Kind = MinigameSettingKind.Integer; Default = string fallback; Min = Some min; Max = Some max }
    let private text key label fallback =
        { Key = key; Label = label; Kind = MinigameSettingKind.Text; Default = fallback; Min = None; Max = None }

    let all: MinigameKind list =
        [ { Id = "timing-bar"
            Name = "Timing bar"
            Help = "A marker sweeps across a bar; stop it in the target zone. 1 inside the zone, less the farther away."
            Settings =
              [ number "speed" "Sweeps per second" 0.9 0.1 None
                number "targetSize" "Target zone (share of the bar)" 0.18 0.02 (Some 0.9)
                text "prompt" "Prompt" "Stop the marker in the zone!" ] }
          { Id = "hold-to-catch"
            Name = "Hold to catch"
            Help = "Hold the button, then let go after the target time. 1 on time, less the farther off."
            Settings = [ integer "holdMs" "Target hold (milliseconds)" 1200 1 10000 ] }
          { Id = "simple-battle"
            Name = "Simple battle"
            Help = "A turn-based fight with attack, guard and magic. Win scores 1, loss 0."
            Settings =
              [ integer "playerHealth" "Player health" 30 1 100
                integer "enemyHealth" "Enemy health" 24 1 100
                integer "attack" "Player attack" 7 1 100
                integer "enemyAttack" "Enemy attack" 5 1 100
                text "enemyName" "Enemy name" "Forest slime" ] }
          { Id = "rhythm-tap"
            Name = "Rhythm tap"
            Help = "Notes scroll to a line; press as each one reaches it. Scores the hits' accuracy over all notes."
            Settings =
              [ integer "beats" "Notes" 8 1 32
                number "bpm" "Beats per minute" 100.0 40.0 (Some 200.0)
                text "prompt" "Prompt" "Tap when a note reaches the line!" ] }
          { Id = "moving-target"
            Name = "Moving target"
            Help = "Keep a bar under a wandering target; hold to push the bar. Scores the time over the target (80% or more is 1)."
            Settings =
              [ number "seconds" "Length (seconds)" 8.0 2.0 (Some 60.0)
                number "targetSpeed" "Target speed" 0.5 0.1 (Some 3.0)
                number "barSize" "Bar size (share of the track)" 0.3 0.1 (Some 0.8)
                text "prompt" "Prompt" "Keep the bar under the target: hold to move right, let go to drift left." ] }
          { Id = "memory-sequence"
            Name = "Memory sequence"
            Help = "Watch a sequence of symbols, then repeat it. Scores the share repeated before the first mistake."
            Settings =
              [ integer "length" "Symbols" 4 2 10
                number "showSeconds" "Seconds each symbol shows" 0.8 0.2 (Some 3.0)
                text "prompt" "Prompt" "Watch the symbols, then repeat them in order." ] } ]

    let tryFind (kind: string) = all |> List.tryFind (fun k -> k.Id = kind)

    let isBuiltIn (kind: string) = (tryFind kind).IsSome

    /// (id, name) pairs for the editor's kind picker.
    let choices = all |> List.map (fun k -> k.Id, k.Name)

    /// The settings of `kind` (empty for a kind the player doesn't know).
    let settings (kind: string) =
        match tryFind kind with
        | Some k -> k.Settings
        | None -> []
