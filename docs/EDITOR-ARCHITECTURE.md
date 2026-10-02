# Editor architecture (C# app)

This note describes how the Avalonia editor (`src/FarmingRpgMaker.App`) is put
together today, the structure it is moving towards, and the steps that are
left. [LANGUAGES.md](LANGUAGES.md) has the rule that decides which language
owns what: **F# understands the project, Rust runs the game, C# is the
desktop app**. Within the C# app, the rule this note adds is: **a view shows
and collects; the decisions and the text it shows come from code that can be
tested without building the view.**

## Where things are today

| Layer | Files | Built how |
|---|---|---|
| Shell | `Views/MainWindow.axaml`, `AboutWindow.axaml`, `UpdateCenterWindow.axaml` with `ViewModels/MainWindowViewModel`, `AboutViewModel`, `UpdateCenterViewModel`; `ExportGameViewModel` behind `Projects/ExportGameWindow` | MVVM: XAML bound to view models (`Mvvm/ObservableObject`, `RelayCommand`) |
| Game surface | `Game/GameWorkspaceView` (switches Edit and Play Mode), `Game/PlayModeView`, `Game/DebugDrawer` | Code-built views over the Rust player |
| Edit Mode | `Game/EditModeView` (+ `EditModeTools`, `EditModeFind`), `ContentEditorView` + `ContentForm`, `SettingsEditorView`, `ArtEditorView`, `ModsEditorView`, `WorkshopView`, `InterfaceEditorView`, `ProblemsView` | Code-built `UserControl`s (`Ui` builders) that hold their state and call `ProjectWorkspace` |
| Project | `Projects/ProjectWorkspace` (the open F# `Document`, undo/redo, autosave), `ProjectStore`, `ProjectDialogs`, `ProjectCommandHandler` | Plain classes; dialogs code-built |
| Engine | F# `FarmEngine.Authoring` (edits, validation, form metadata, readouts), Rust through `FarmEngine.Interop` | Called by the views and view models |

Edit Mode is where most of the code is, and it is not MVVM: the views build
their controls in code, keep form state in the controls, and apply F# edits
through `ProjectWorkspace` directly. That works and is well covered by the
headless UI tests, but it means a rule hidden in a view can only be tested by
building the whole editor, and restyling means editing C#.

## Shared pieces

Use these instead of writing another copy:

| Need | Use |
|---|---|
| Open an Edit Mode tab | `EditModeView.SelectedTab` with the `EditorTab` enum (the enum's order is the tab order; `AddTab` asserts it) |
| Where a Workshop link or a Problems row leads | `EditorNavigation.WorkshopTarget`, `EditorNavigation.ContentCategory` |
| File sizes, download sizes, "5 minutes ago", numbers in text fields | `DisplayFormat` (`FileSize` is the F# `GameExporter.FormatSize` that Export Game and `farmc` print) |
| Write a file without leaving half of it behind | `FarmingRpgMaker.Updates.AtomicFile` |
| Read or change one section of `settings.json` | `SettingsFile` through `AppSettingsStore` / `JsonSettingsStore`; `AppSettingsStore.TryUpdate` for preferences that must never stop the creator |
| Read or write a file the creator picked | `Projects/PickedFiles` (size caps, atomic local saves) |
| Ask about unsaved form fields | `Game/DraftBar` |
| Common controls (buttons, rows, icons, modal cards, accessible names) | `Game/Ui` |
| Reflection over the schema records for generated forms | `Game/ContentFormSchema` (no controls); layout pieces in `Game/ContentFormLayout` |

Error messages name what the creator sees (the field's label, which is also
its accessible name), never a control's `Name`.

Test doubles stay in the test project: the shell's placeholder game surface
and project commands live in `tests/FarmingRpgMaker.App.Tests/Ui/Placeholders.cs`.
`FakeUpdateService` still ships in `FarmingRpgMaker.Updates` because the
`FARMING_RPG_MAKER_FAKE_UPDATES` demo mode ([RELEASING.md](RELEASING.md))
uses it; see the remaining steps.

## Target structure

Each Edit Mode tab gets a view model, introduced one tab at a time:

- **The view model owns the state** a tab has today in its controls and
  fields: the selected entry, the draft, the dirty flag, validation messages,
  the status line. It calls F# for every decision (edits, validation,
  readouts, options) and `ProjectWorkspace` to apply edits. It has no
  Avalonia types, so tests drive it directly.
- **The view binds to it.** Small, static layouts move to XAML; generated
  layouts (the schema-driven `ContentForm`, the map canvas) stay in code but
  read and write the view model rather than holding state.
- **Navigation goes through one place.** `EditorTab` and `EditorNavigation`
  are the start: a shell-level navigator (a method on the Edit Mode view
  model) replaces the callbacks the tabs pass around today (`PickOnMap`, the
  Workshop's tab keys, the Problems row handler).
- **Strings go through `EditorStrings`** so every tab can follow the
  editor's language ([#59](https://github.com/jxburros/farm-game-engine-native/issues/59)).

## Remaining steps

In order; each step is a separate change that keeps the UI tests passing.

1. **Draft forms first.** Give the Content, Interface and Settings tabs view
   models that own their drafts, dirty flags and validation (`ContentForm`'s
   draft bookkeeping, `HasUnsavedChanges`, `SaveDraft`), so the unsaved-fields
   rules (#87, #70) are tested without the UI. `DraftBar` binds to them.
2. **Split `ContentForm` further**: the list rows (schedules, waypoints, stock
   cards) and the condition and outcome editors become their own controls over
   the draft, next to the reflection helpers already in `ContentFormSchema`.
3. **Map editor state.** Move the map tool state (`Tool`, the brush, the
   selection, the door form's fields, Pick on map) out of `EditModeView` and
   `EditModeTools` into a view model; the canvas and toolbar bind to it.
4. **Art studio.** Split `ArtEditorView` into the asset list, the animation
   clip editor and the bindings panel, with the frame and clip arithmetic in a
   view model or F#.
5. **Localization.** Route the remaining views' text through `EditorStrings`
   and refresh Play Mode's toolbar when the language changes (#59).
6. **Demo-only code.** Move `FakeUpdateService` into a test-support assembly
   and keep the `FARMING_RPG_MAKER_FAKE_UPDATES` switch only in debug builds,
   once the release docs no longer point at it for UI demos.
7. **Tests use the names.** The UI tests still select tabs by index
   (`SelectedIndex = 1`); move them to `EditModeView.SelectedTab` when the test
   helpers are next reworked. `EditorStructureTests` pins the enum to the tab
   order meanwhile.
