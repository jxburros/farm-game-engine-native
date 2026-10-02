using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Project identity, gameplay settings and calendar (saved as one F# undo step), weather odds,
/// the mine and export settings. Each section keeps what was typed into it until it is saved
/// or reverted (#70): saving another section, toggling the mine, undo or a tab switch reload
/// only the sections without unsaved fields. Season moves and removals change the draft rows
/// and are saved with the project settings. Errors name the field (#46).
/// </summary>
public sealed class SettingsEditorView : UserControl, IRetirable
{
    private sealed record SeasonRow(TextBox Id, TextBox Name, TextBox Days, Button Up, Button Down, Control Control);

    /// <summary>The parts of the form that are saved (and reloaded) on their own.</summary>
    private enum Section
    {
        Project,
        Weather,
        Mine,
        Export,
    }
    private sealed record FestivalRow(TextBox Id, TextBox Name, TextBox SeasonId, TextBox Day, Control Control);

    /// <summary>The fastest player speed the form accepts, in tiles a second.</summary>
    private const double MaxPlayerSpeed = 15;

    private readonly ProjectWorkspace _workspace;
    private readonly StackPanel _seasons = new() { Spacing = 5 };
    private readonly StackPanel _festivals = new() { Spacing = 5 };
    private readonly List<SeasonRow> _seasonRows = [];
    private readonly List<FestivalRow> _festivalRows = [];
    private readonly TextBlock _message = Ui.Wrapped("", "muted", "small");
    private readonly TextBox _name = new() { Name = "Setting_Name" };
    private readonly TextBox _version = new() { Name = "Setting_Version" };
    private readonly TextBox _locale = new() { Name = "Setting_Locale" };
    private readonly TextBox _speed = new() { Name = "Setting_PlayerSpeed" };
    private readonly TextBox _maxEnergy = new() { Name = "Setting_MaxEnergy" };
    private readonly TextBox _collapseFraction = new() { Name = "Setting_CollapseEnergyFraction" };
    private readonly TextBox _collapsePenalty = new() { Name = "Setting_CollapseMoneyPenalty" };
    private readonly TextBox _dayStart = new() { Name = "Setting_DayStartMinute" };
    private readonly TextBox _dayEnd = new() { Name = "Setting_DayEndMinute" };
    private readonly TextBox _minutesPerSecond = new() { Name = "Setting_MinutesPerRealSecond" };
    private readonly CheckBox _pauseInModals = new() { Name = "Setting_PauseInModals", Content = "Pause the clock in dialogue, shops, minigames and menus" };
    private readonly StackPanel _skillLevels = new() { Name = "SkillLevels", Spacing = 5 };
    private readonly List<TextBox> _skillLevelBoxes = [];
    private readonly CheckBox _energy = new() { Name = "Setting_EnergyEnabled", Content = "Energy enabled" };
    private readonly CheckBox _skills = new() { Name = "Setting_SkillsEnabled", Content = "Skills enabled" };
    private readonly CheckBox _credit = new() { Name = "Setting_ShowMadeWithCredit", Content = "Show creator credit" };
    private readonly TextBox _exportTitle = new() { Name = "Export_Title", Watermark = "Project name" };
    private readonly TextBox _exportExecutable = new() { Name = "Export_ExecutableName" };
    private readonly TextBox _exportVersion = new() { Name = "Export_Version", Watermark = "Project version" };
    private readonly TextBox _exportAuthor = new() { Name = "Export_Author" };
    private readonly TextBox _exportCompany = new() { Name = "Export_Company" };
    private readonly TextBox _exportGameId = new() { Name = "Export_GameId", IsReadOnly = true };
    private readonly ComboBox _exportIcon = new() { Name = "Export_Icon", MinWidth = 260 };
    private readonly TextBox _exportWidth = new() { Name = "Export_WindowWidth", Width = 90 };
    private readonly TextBox _exportHeight = new() { Name = "Export_WindowHeight", Width = 90 };
    private readonly CheckBox _exportFullscreen = new() { Name = "Export_Fullscreen", Content = "Start fullscreen" };
    private readonly ComboBox _exportPixelScale = new() { Name = "Export_PixelScale", MinWidth = 260 };
    private readonly TextBox _exportCredits = new() { Name = "Export_Credits", AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 60 };
    private readonly StackPanel _exportProblems = new() { Name = "ExportSettingsProblems", Spacing = 4 };
    private readonly TextBlock _exportMessage = Ui.Wrapped("", "muted", "small");
    private readonly StackPanel _weather = new() { Name = "WeatherOdds", Spacing = 6 };
    private readonly Dictionary<(string Season, string Weather), TextBox> _weatherCells = [];
    private readonly TextBlock _weatherMessage = Ui.Wrapped("", "muted", "small");
    private readonly CheckBox _mineEnabled = new() { Name = "Mine_Enabled", Content = "Mine enabled" };
    private readonly ComboBox _mineScene = new() { Name = "Mine_EntranceScene", MinWidth = 220 };
    private readonly TextBox _mineX = new() { Name = "Mine_EntranceX", Width = 70 };
    private readonly TextBox _mineY = new() { Name = "Mine_EntranceY", Width = 70 };
    private readonly TextBox _mineFloors = new() { Name = "Mine_Floors", Width = 70 };
    private readonly TextBox _mineLadder = new() { Name = "Mine_LadderChance", Width = 70 };
    private readonly TextBlock _mineNote = Ui.Wrapped("", "muted", "small");
    private readonly TextBlock _mineMessage = Ui.Wrapped("", "muted", "small");
    private readonly StackPanel _mineFields = new() { Name = "MineFields", Spacing = 6 };
    /// <summary>Each section's fields as they were last loaded from the project.</summary>
    private readonly Dictionary<Section, string> _loaded = [];
    /// <summary>The saved seasons when the project section was loaded (a row removed since is removed on save).</summary>
    private List<string> _loadedSeasons = [];
    private readonly Action<string, string, Action<int, int>>? _pickOnMap;

    /// <param name="workspace">The open project.</param>
    /// <param name="pickOnMap">Lets the creator click the mine entrance on the map (scene id, prompt, what to do with the tile); null hides the button.</param>
    public SettingsEditorView(ProjectWorkspace workspace, Action<string, string, Action<int, int>>? pickOnMap = null)
    {
        _workspace = workspace;
        _pickOnMap = pickOnMap;
        Name = "SettingsEditorView";
        _message.Name = "SettingsMessage";
        var form = new StackPanel { Spacing = 10, Margin = new Thickness(20), MaxWidth = 760 };
        form.Children.Add(Ui.Text("PROJECT", "section"));
        Field(form, "Name", _name);
        Field(form, "Version", _version);
        Field(form, "Locale", _locale);
        form.Children.Add(Ui.Text("GAMEPLAY", "section"));
        form.Children.Add(Ui.HStack(15, _energy, _skills, _credit));
        Field(form, "Player speed (tiles/sec)", _speed);
        Field(form, "Max energy", _maxEnergy);
        Field(form, "Collapse energy fraction", _collapseFraction);
        Field(form, "Collapse money penalty", _collapsePenalty);
        form.Children.Add(Ui.Text("Skill levels", "muted", "small"));
        form.Children.Add(Ui.Wrapped("The total XP a skill needs to reach each level. Players start at level 0; the game announces \"Farming level 1!\" once farming XP reaches level 1's amount.", "muted", "small"));
        form.Children.Add(_skillLevels);
        var addSkillLevel = Ui.Button("Add level", AddSkillLevel, "tool");
        addSkillLevel.Name = "AddSkillLevelButton";
        form.Children.Add(addSkillLevel);
        form.Children.Add(Ui.Text("TIME", "section"));
        Field(form, "Day start minute", _dayStart);
        Field(form, "Day end minute", _dayEnd);
        Field(form, "Game minutes per real second", _minutesPerSecond);
        form.Children.Add(_pauseInModals);
        form.Children.Add(Ui.Text("SEASONS", "section"));
        form.Children.Add(_seasons);
        var addSeason = Ui.Button("Add season", AddSeason, "tool");
        addSeason.Name = "AddSeasonButton";
        form.Children.Add(addSeason);
        form.Children.Add(Ui.Text("FESTIVALS", "section"));
        form.Children.Add(_festivals);
        var addFestival = Ui.Button("Add festival", AddFestival, "tool");
        addFestival.Name = "AddFestivalButton";
        form.Children.Add(addFestival);
        form.Children.Add(_message);
        var save = Ui.Button("Save project settings", Save, "accent");
        save.Name = "SaveSettingsButton";
        var revert = Ui.Button("Revert fields", () => Load(Section.Project), "tool");
        revert.Name = "RevertSettingsButton";
        form.Children.Add(Ui.HStack(8, save, revert));
        form.Children.Add(Ui.Text("WEATHER ODDS PER SEASON", "section"));
        form.Children.Add(Ui.Wrapped("Weights, rolled at each day start. Rain waters the soil, storms can damage crops. 0 means never.", "muted", "small"));
        form.Children.Add(_weather);
        _weatherMessage.Name = "WeatherMessage";
        form.Children.Add(_weatherMessage);
        var saveWeather = Ui.Button("Save weather odds", SaveWeather, "accent");
        saveWeather.Name = "SaveWeatherButton";
        form.Children.Add(saveWeather);
        form.Children.Add(Ui.Text("MINE", "section"));
        form.Children.Add(Ui.Wrapped("Procedural floors below an entrance tile; broken rocks reveal the ladder down.", "muted", "small"));
        _mineEnabled.Click += (_, _) => ToggleMine();
        form.Children.Add(_mineEnabled);
        Ui.Label((_mineScene, "Mine entrance scene"), (_mineX, "Mine entrance X"), (_mineY, "Mine entrance Y"), (_mineFloors, "Mine floors"),
            (_mineLadder, "Ladder chance (0.02–1)"), (_exportIcon, "Icon (PNG artwork, at least 256×256)"), (_exportWidth, "Window width"),
            (_exportHeight, "Window height"), (_exportPixelScale, "Pixel scale"));
        _mineFields.Children.Add(Ui.HStack(8, Ui.Text("Entrance scene", "muted", "small"), _mineScene));
        var mineRow = Ui.HStack(8, Ui.Text("Entrance X", "muted", "small"), _mineX, Ui.Text("Y", "muted", "small"), _mineY);
        if (_pickOnMap is not null)
        {
            var pick = Ui.Button("Pick on map", PickMineEntrance, "tool", "small");
            pick.Name = "Mine_PickOnMap";
            AutomationProperties.SetName(pick, "Pick the mine entrance on the map");
            mineRow.Children.Add(pick);
        }

        _mineFields.Children.Add(mineRow);
        _mineFields.Children.Add(Ui.HStack(8, Ui.Text("Floors", "muted", "small"), _mineFloors, Ui.Text("Ladder chance (0.02–1)", "muted", "small"), _mineLadder));
        _mineNote.Name = "MineNote";
        _mineFields.Children.Add(_mineNote);
        var saveMine = Ui.Button("Save mine", SaveMine, "accent");
        saveMine.Name = "SaveMineButton";
        _mineFields.Children.Add(saveMine);
        form.Children.Add(_mineFields);
        _mineMessage.Name = "MineMessage";
        form.Children.Add(_mineMessage);
        form.Children.Add(Ui.Text("EXPORT", "section"));
        form.Children.Add(Ui.Wrapped("How File → Export Game names and packages the standalone game. Empty title and version use the project's.", "muted", "small"));
        Field(form, "Game title", _exportTitle);
        Field(form, "Executable name", _exportExecutable);
        Field(form, "Game version", _exportVersion);
        Field(form, "Author", _exportAuthor);
        Field(form, "Company", _exportCompany);
        Field(form, "Game id (keeps save folders stable; set once)", _exportGameId);
        form.Children.Add(Ui.Text("Icon (PNG artwork, at least 256×256)", "muted", "small"));
        form.Children.Add(_exportIcon);
        form.Children.Add(Ui.HStack(8, Ui.Text("Window", "muted", "small"), _exportWidth, Ui.Text("×"), _exportHeight, _exportFullscreen));
        form.Children.Add(Ui.Text("Pixel scale", "muted", "small"));
        form.Children.Add(_exportPixelScale);
        Field(form, "Credits", _exportCredits);
        _exportMessage.Name = "ExportSettingsMessage";
        form.Children.Add(_exportProblems);
        form.Children.Add(_exportMessage);
        var saveExport = Ui.Button("Save export settings", SaveExport, "accent");
        saveExport.Name = "SaveExportSettingsButton";
        form.Children.Add(saveExport);
        Content = new ScrollViewer { Content = form };
        _workspace.ProjectChanged += OnProjectChanged;
        Refresh();
    }

    /// <summary>Stops following the project (the editor that built this view was replaced).</summary>
    public void Retire() => _workspace.ProjectChanged -= OnProjectChanged;

    private void OnProjectChanged(object? sender, ProjectChangedEventArgs e)
    {
        // Another project: nothing typed belongs to it.
        if (e.Kind == ProjectChangeKind.Opened)
        {
            _loaded.Clear();
        }

        if (IsEffectivelyVisible) Refresh();
    }

    /// <summary>True while a section holds fields that are not saved (#70, #87).</summary>
    public bool HasUnsavedChanges => Enum.GetValues<Section>().Any(IsDirty);

    /// <summary>
    /// Saves every section with unsaved fields (switching projects, closing the editor). False
    /// when one could not be saved; its message says why.
    /// </summary>
    public bool SaveDrafts()
    {
        if (IsDirty(Section.Project)) Save();
        if (IsDirty(Section.Weather)) SaveWeather();
        if (IsDirty(Section.Mine)) SaveMine();
        if (IsDirty(Section.Export)) SaveExport();
        return !HasUnsavedChanges;
    }

    private bool IsDirty(Section section) => _loaded.TryGetValue(section, out var loaded) && loaded != Snapshot(section);

    /// <summary>A section's fields as one string, to tell whether anything was typed since it was loaded.</summary>
    private string Snapshot(Section section)
    {
        static string Of(Control control) => control switch
        {
            TextBox box => box.Text ?? "",
            CheckBox check => check.IsChecked?.ToString() ?? "",
            ComboBox picker => (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
            _ => "",
        };
        IEnumerable<Control> controls = section switch
        {
            Section.Project =>
            [
                _name, _version, _locale, _speed, _maxEnergy, _collapseFraction, _collapsePenalty, _dayStart, _dayEnd, _minutesPerSecond,
                _pauseInModals, _energy, _skills, _credit, .. _skillLevelBoxes,
                .. _seasonRows.SelectMany(row => new Control[] { row.Id, row.Name, row.Days }),
                .. _festivalRows.SelectMany(row => new Control[] { row.Id, row.Name, row.SeasonId, row.Day }),
            ],
            Section.Weather => _weatherCells.OrderBy(cell => cell.Key).Select(cell => (Control)cell.Value),
            Section.Mine => [_mineScene, _mineX, _mineY, _mineFloors, _mineLadder],
            _ => [_exportTitle, _exportExecutable, _exportVersion, _exportAuthor, _exportCompany, _exportIcon, _exportWidth, _exportHeight, _exportFullscreen, _exportPixelScale, _exportCredits],
        };
        var keys = section switch
        {
            Section.Project => $"{_skillLevelBoxes.Count}/{_seasonRows.Count}/{_festivalRows.Count}",
            Section.Weather => string.Join(",", _weatherCells.Keys.OrderBy(key => key)),
            _ => "",
        };
        return keys + "\u001f" + string.Join("\u001f", controls.Select(Of));
    }

    /// <summary>Fills a section from the project and remembers it as loaded.</summary>
    private void Load(Section section)
    {
        if (_workspace.Current is not { } project) return;
        switch (section)
        {
            case Section.Project: LoadProject(project); break;
            case Section.Weather: RefreshWeather(project); break;
            case Section.Mine: RefreshMine(project); break;
            default: RefreshExport(project); break;
        }

        _loaded[section] = Snapshot(section);
    }

    /// <summary>The mine entrance clicked on the map, in the entrance scene picked here.</summary>
    private void PickMineEntrance()
    {
        if (_pickOnMap is null || (_mineScene.SelectedItem as ComboBoxItem)?.Tag is not string sceneId) return;
        _pickOnMap(sceneId, "Click the tile players use to go down into the mine.", (x, y) =>
        {
            _mineX.Text = x.ToString(CultureInfo.InvariantCulture);
            _mineY.Text = y.ToString(CultureInfo.InvariantCulture);
            _mineMessage.Text = $"Entrance set to ({x}, {y}). Save mine to keep it.";
        });
    }

    private static void Field(StackPanel form, string label, TextBox input)
    {
        Ui.Label((input, label));
        form.Children.Add(Ui.Text(label, "muted", "small"));
        form.Children.Add(input);
    }

    private static string Number(double value) => DisplayFormat.Number(value);

    /// <summary>The number in <paramref name="input"/>; the error names the field (its screen-reader name, #46).</summary>
    private static double Parse(TextBox input)
    {
        var label = AutomationProperties.GetName(input) is { Length: > 0 } name ? name : input.Name ?? "This field";
        var text = (input.Text ?? "").Trim();
        if (text.Length == 0) throw new FormatException($"{label} needs a value.");
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new FormatException($"{label} must be a number.");
        return value;
    }

    /// <summary>A whole number, named in the error like <see cref="Parse"/>.</summary>
    private static double Whole(TextBox input)
    {
        var value = Parse(input);
        if (value != Math.Floor(value)) throw new FormatException($"{AutomationProperties.GetName(input)} must be a whole number.");
        return value;
    }
    private static TextBox Box(string name, string value, double width) => new() { Name = name, Text = value, Width = width };

    /// <summary>
    /// Shows one row per skill level: "Level 0 [0] XP", "Level 1 [50] XP", … (the curve's index is
    /// the level the game reports). Drafts are saved with "Save project settings".
    /// </summary>
    private void ShowSkillLevels(IReadOnlyList<string> values)
    {
        _skillLevelBoxes.Clear();
        _skillLevels.Children.Clear();
        for (var index = 0; index < values.Count; index++)
        {
            var i = index;
            var box = Box($"Setting_SkillLevel_{i}", values[i], 90);
            AutomationProperties.SetName(box, $"Level {i} XP");
            var remove = Ui.Button("Remove", () => ShowSkillLevels(_skillLevelBoxes.Where((_, n) => n != i).Select(other => other.Text ?? "").ToList()), "tool", "small");
            remove.Name = $"Setting_SkillLevelRemove_{i}";
            AutomationProperties.SetName(remove, $"Remove level {i}");
            _skillLevelBoxes.Add(box);
            _skillLevels.Children.Add(Ui.HStack(8, new TextBlock { Text = $"Level {i}", Width = 70, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                box, Ui.Text("XP", "muted", "small"), remove));
        }
    }

    /// <summary>A new last level: as far above the last as the last is above the one before, else 100 XP more.</summary>
    private void AddSkillLevel()
    {
        static double Value(TextBox box) =>
            double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
        var values = _skillLevelBoxes.Select(box => box.Text ?? "").ToList();
        var count = _skillLevelBoxes.Count;
        var last = count > 0 ? Value(_skillLevelBoxes[^1]) : 0;
        var step = count > 1 ? last - Value(_skillLevelBoxes[^2]) : 0;
        values.Add(Number(count == 0 ? 0 : last + (step > 0 ? step : 100)));
        ShowSkillLevels(values);
    }

    /// <summary>The skill level rows as a curve: at least one level, each a finite XP amount of 0 or more and at least the one before.</summary>
    private List<double> SkillLevelCurve()
    {
        if (_skillLevelBoxes.Count == 0) throw new FormatException("Add at least one skill level.");
        var curve = new List<double>();
        for (var i = 0; i < _skillLevelBoxes.Count; i++)
        {
            if (!double.TryParse(_skillLevelBoxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var xp) || !double.IsFinite(xp) || xp < 0)
                throw new FormatException($"Level {i} needs an XP amount of 0 or more.");
            if (curve.Count > 0 && xp < curve[^1]) throw new FormatException("Each level needs at least as much XP as the one before.");
            curve.Add(xp);
        }

        return curve;
    }

    private void AddSeasonRow(CalendarSeason season, bool saved = true)
    {
        var id = Box("Season_Id", season.Id, 115);
        id.IsReadOnly = saved;
        var name = Box("Season_Name", season.Name, 145);
        var days = Box("Season_Days", Number(season.Days), 65);
        var what = string.IsNullOrWhiteSpace(season.Name) ? "new season" : season.Name;
        Ui.Label((id, $"{what} id"), (name, $"{what} name"), (days, $"{what} days"));
        SeasonRow? row = null;
        // Removing and moving change the rows; Save project settings applies them (#70).
        var remove = Ui.Button("Remove", () =>
        {
            if (row is null) return;
            if (_seasonRows.Count <= 1)
            {
                _message.Text = "The calendar needs at least one season.";
                return;
            }

            _seasonRows.Remove(row);
            _seasons.Children.Remove(row.Control);
            // Festivals on the season go with it, as they do once it is saved.
            foreach (var festival in _festivalRows.Where(f => (f.SeasonId.Text ?? "").Trim() == (id.Text ?? "").Trim()).ToList())
            {
                _festivalRows.Remove(festival);
                _festivals.Children.Remove(festival.Control);
            }

            UpdateSeasonArrows();
            _message.Text = $"Removed {what} from the calendar. Save project settings to apply it.";
        }, "tool", "small");
        remove.Name = $"Season_Remove_{season.Id}";
        Ui.Label((remove, $"Remove {what}"));
        var up = Ui.Button("↑", () => MoveSeasonRow(row, -1), "tool", "small");
        up.Name = $"Season_Up_{season.Id}";
        ToolTip.SetTip(up, "Earlier in the year");
        var down = Ui.Button("↓", () => MoveSeasonRow(row, 1), "tool", "small");
        down.Name = $"Season_Down_{season.Id}";
        ToolTip.SetTip(down, "Later in the year");
        Ui.Label((up, $"Move {what} earlier in the year"), (down, $"Move {what} later in the year"));
        var control = Ui.HStack(8, up, down, id, name, days, remove);
        row = new SeasonRow(id, name, days, up, down, control);
        _seasonRows.Add(row);
        _seasons.Children.Add(control);
        UpdateSeasonArrows();
    }

    private void MoveSeasonRow(SeasonRow? row, int delta)
    {
        if (row is null) return;
        var index = _seasonRows.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _seasonRows.Count) return;
        _seasonRows.RemoveAt(index);
        _seasonRows.Insert(target, row);
        _seasons.Children.Remove(row.Control);
        _seasons.Children.Insert(target, row.Control);
        UpdateSeasonArrows();
        _message.Text = "Season order changed. Save project settings to apply it.";
    }

    /// <summary>The first season can't move earlier, the last can't move later (web ProjectSettingsEditor).</summary>
    private void UpdateSeasonArrows()
    {
        for (var i = 0; i < _seasonRows.Count; i++)
        {
            _seasonRows[i].Up.IsEnabled = i > 0;
            _seasonRows[i].Down.IsEnabled = i < _seasonRows.Count - 1;
        }
    }

    private void AddFestivalRow(CalendarFestival festival)
    {
        var id = Box("Festival_Id", festival.Id, 110);
        var name = Box("Festival_Name", festival.Name, 145);
        var season = Box("Festival_SeasonId", festival.SeasonId, 110);
        var day = Box("Festival_Day", Number(festival.Day), 60);
        var festivalName = string.IsNullOrWhiteSpace(festival.Name) ? "new festival" : festival.Name;
        Ui.Label((id, $"{festivalName} id"), (name, $"{festivalName} name"), (season, $"{festivalName} season id"), (day, $"{festivalName} day"));
        var remove = Ui.Button("Remove", () =>
        {
            var row = _festivalRows.First(r => ReferenceEquals(r.Id, id));
            _festivalRows.Remove(row);
            _festivals.Children.Remove(row.Control);
        }, "tool", "small");
        Ui.Label((remove, $"Remove {festivalName}"));
        var control = Ui.HStack(8, id, name, season, day, remove);
        _festivalRows.Add(new FestivalRow(id, name, season, day, control));
        _festivals.Children.Add(control);
    }

    private void AddSeason()
    {
        if (_workspace.Current is null) return;
        var current = CalendarConfig.Default.WithSeasons(DraftSeasons());
        AddSeasonRow(Defaults.NewSeason(current), saved: false);
    }

    private void AddFestival()
    {
        if (_workspace.Current is not { } project) return;
        var calendar = CalendarConfig.Default
            .WithSeasons(DraftSeasons())
            .WithFestivals(_festivalRows.Select(row => CalendarFestival.Default.WithId(row.Id.Text ?? "")));
        var festival = Defaults.NewFestival(project, calendar);
        if (festival is not null) AddFestivalRow(festival);
    }

    /// <summary>The season rows as typed so far (unparseable day counts read as 28).</summary>
    private IEnumerable<CalendarSeason> DraftSeasons() =>
        _seasonRows.Select(row => CalendarSeason.Default
            .WithId(row.Id.Text ?? "")
            .WithName(row.Name.Text ?? "")
            .WithDays(double.TryParse(row.Days.Text, CultureInfo.InvariantCulture, out var days) ? days : 28)).ToList();

    /// <summary>
    /// Reloads the sections without unsaved fields from the project (the tab is shown, the
    /// project changed). A section with unsaved fields keeps them and says so.
    /// </summary>
    public void Refresh()
    {
        if (_workspace.Current is not { } project) return;
        var kept = new List<string>();
        foreach (var section in Enum.GetValues<Section>())
        {
            if (IsDirty(section))
            {
                kept.Add(section.ToString().ToLowerInvariant());
                continue;
            }

            Load(section);
        }

        // The mine toggle is applied at once, whatever its fields hold.
        _mineEnabled.IsChecked = project.Mine.Enabled;
        _mineFields.IsVisible = project.Mine.Enabled;
        if (kept.Count > 0 && IsEffectivelyVisible)
        {
            _message.Text = $"Unsaved changes kept ({string.Join(", ", kept)}): save or revert them.";
        }
    }

    private void LoadProject(GameProject project)
    {
        var settings = project.Settings;
        _name.Text = project.Name;
        _version.Text = project.Version;
        _locale.Text = settings.Locale;
        _speed.Text = Number(settings.Movement.PlayerSpeed);
        _maxEnergy.Text = Number(settings.MaxEnergy);
        _collapseFraction.Text = Number(settings.CollapseEnergyFraction);
        _collapsePenalty.Text = Number(settings.CollapseMoneyPenalty);
        _dayStart.Text = Number(settings.Time.DayStartMinute);
        _dayEnd.Text = Number(settings.Time.DayEndMinute);
        _minutesPerSecond.Text = Number(settings.Time.MinutesPerRealSecond);
        _pauseInModals.IsChecked = settings.Time.PauseInModals.OrNullable() != false;
        ShowSkillLevels(settings.SkillLevelCurve.Select(Number).ToList());
        _energy.IsChecked = settings.EnergyEnabled;
        _skills.IsChecked = settings.SkillsEnabled;
        _credit.IsChecked = settings.ShowMadeWithCredit;
        _seasonRows.Clear();
        _seasons.Children.Clear();
        foreach (var season in settings.Calendar.Seasons) AddSeasonRow(season);
        _loadedSeasons = settings.Calendar.Seasons.Select(season => season.Id).ToList();
        _festivalRows.Clear();
        _festivals.Children.Clear();
        foreach (var festival in settings.Calendar.Festivals) AddFestivalRow(festival);
        _message.Text = "";
    }

    private void RefreshWeather(GameProject project)
    {
        _weather.Children.Clear();
        _weatherCells.Clear();
        _weatherMessage.Text = project.Weather.Types.Length == 0 ? "This project has no weather types." : "";
        foreach (var season in project.Settings.Calendar.Seasons)
        {
            var row = Ui.HStack(6, new TextBlock { Text = season.Name, Width = 110, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            foreach (var type in project.Weather.Types)
            {
                var cell = Box($"Weather_{season.Id}_{type.Id}", Number(SettingsForm.WeatherWeight(project, season.Id, type.Id)), 55);
                ToolTip.SetTip(cell, $"{type.Name} in {season.Name}");
                Ui.Label((cell, $"{type.Name} weight in {season.Name}"));
                _weatherCells[(season.Id, type.Id)] = cell;
                row.Children.Add(Ui.Text(type.Name, "muted", "small"));
                row.Children.Add(cell);
            }

            _weather.Children.Add(row);
        }
    }

    private void SaveWeather()
    {
        if (_workspace.Current is not { } project) return;
        try
        {
            var weights = _weatherCells.Select(cell => (cell.Key.Season, cell.Key.Weather, string.IsNullOrWhiteSpace(cell.Value.Text) ? 0 : Parse(cell.Value))).ToList();
            var saved = _workspace.Apply(SettingsForm.WeatherOdds(project, weights));
            Load(Section.Weather);
            _weatherMessage.Text = saved ? _workspace.SavedText("Weather odds") : "No changes were made.";
        }
        catch (FormatException error)
        {
            _weatherMessage.Text = $"Could not save: {error.Message}";
        }
    }

    private void RefreshMine(GameProject project)
    {
        var mine = project.Mine;
        var sceneId = mine.EntranceSceneId.OrNull() ?? project.StartSceneId;
        _mineScene.Items.Clear();
        foreach (var scene in project.Scenes) _mineScene.Items.Add(new ComboBoxItem { Content = scene.Name, Tag = scene.Id });
        _mineScene.SelectedItem = _mineScene.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, sceneId));
        _mineX.Text = Number(mine.EntranceX.Or(1));
        _mineY.Text = Number(mine.EntranceY.Or(1));
        _mineFloors.Text = Number(mine.Floors);
        _mineLadder.Text = Number(mine.LadderChance);
        _mineNote.Text = $"Players use the entrance tile to go down; an elevator checkpoint every {Number(mine.ElevatorEvery)} floors.";
        _mineMessage.Text = "";
    }

    private void ToggleMine()
    {
        if (_workspace.Current is not { } project) return;
        var enabled = _mineEnabled.IsChecked == true;
        _workspace.Apply(Edits.SetMine(Defaults.MineEnabled(project, enabled)));
        _mineMessage.Text = enabled ? "Mine enabled." : "Mine disabled; its settings are kept.";
    }

    private void SaveMine()
    {
        if (_workspace.Current is not { } project) return;
        try
        {
            var sceneId = (_mineScene.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            var mine = SettingsForm.Mine(project, true, sceneId, Whole(_mineX), Whole(_mineY), Whole(_mineFloors), Parse(_mineLadder));
            _workspace.Apply(Edits.SetMine(mine));
            Load(Section.Mine);
            _mineMessage.Text = _workspace.SavedText("Mine");
        }
        catch (FormatException error)
        {
            _mineMessage.Text = $"Could not save: {error.Message}";
        }
    }

    private static void Fill(ComboBox box, IEnumerable<PickerOption> options, string? current)
    {
        box.Items.Clear();
        foreach (var option in options)
        {
            var item = new ComboBoxItem { Content = option.Label, Tag = option.Id };
            if (option.Missing) item.Classes.Add("missing");
            box.Items.Add(item);
        }
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag! == (current ?? ""));
    }

    private static string? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim();

    private void RefreshExport(GameProject project)
    {
        var export = ExportSettingsForm.Current(project);
        _exportTitle.Text = export.Title.OrNull() ?? "";
        _exportExecutable.Text = export.ExecutableName.OrNull() ?? "";
        _exportVersion.Text = export.Version.OrNull() ?? "";
        _exportAuthor.Text = export.Author.OrNull() ?? "";
        _exportCompany.Text = export.Company.OrNull() ?? "";
        _exportGameId.Text = export.GameId;
        Fill(_exportIcon, ExportSettingsForm.IconOptions(project, export.IconAssetId.OrNull()), export.IconAssetId.OrNull());
        _exportWidth.Text = export.Window.Width.ToString(CultureInfo.InvariantCulture);
        _exportHeight.Text = export.Window.Height.ToString(CultureInfo.InvariantCulture);
        _exportFullscreen.IsChecked = export.Window.Fullscreen;
        Fill(_exportPixelScale, ExportSettingsForm.PixelScales, export.PixelScale);
        _exportCredits.Text = export.Credits.OrNull() ?? "";
        _exportMessage.Text = !project.Export.HasValue() ? "Not saved yet: export uses these defaults." : "";
        ShowExportProblems(!project.Export.HasValue() ? [] : ExportSettingsForm.Check(project, export));
    }

    private void ShowExportProblems(IReadOnlyList<Problem> problems)
    {
        _exportProblems.Children.Clear();
        for (var i = 0; i < problems.Count; i++)
        {
            var line = Ui.Wrapped(problems[i].Message, problems[i].IsError ? "error" : "muted", "small");
            line.Name = $"ExportSettingsProblem_{i}";
            _exportProblems.Children.Add(line);
        }
    }

    private static int WholeNumber(TextBox box, string label) =>
        int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{label} must be a whole number.");

    private void SaveExport()
    {
        if (_workspace.Current is not { } project) return;
        try
        {
            var current = ExportSettingsForm.Current(project);
            var settings = current
                .WithTitle(Optional(_exportTitle))
                .WithExecutableName(Optional(_exportExecutable))
                .WithVersion(Optional(_exportVersion))
                .WithAuthor(Optional(_exportAuthor))
                .WithCompany(Optional(_exportCompany))
                .WithIconAssetId((_exportIcon.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } icon ? icon : null)
                .WithWindow(current.Window
                    .WithWidth(WholeNumber(_exportWidth, "Window width"))
                    .WithHeight(WholeNumber(_exportHeight, "Window height"))
                    .WithFullscreen(_exportFullscreen.IsChecked == true))
                .WithPixelScale((_exportPixelScale.SelectedItem as ComboBoxItem)?.Tag as string ?? current.PixelScale)
                .WithCredits(Optional(_exportCredits));
            var problems = ExportSettingsForm.Check(project, settings);
            _workspace.Apply(Edits.SetExportSettings(settings));
            Load(Section.Export);
            ShowExportProblems(problems);
            _exportMessage.Text = problems.Any(problem => problem.IsError)
                ? _workspace.SavedText("Export settings") + " Fix the errors above before exporting."
                : _workspace.SavedText("Export settings");
        }
        catch (FormatException error)
        {
            _exportMessage.Text = $"Could not save: {error.Message}";
        }
    }

    private static double Positive(TextBox box)
    {
        var value = Parse(box);
        return value > 0 ? value : throw new FormatException($"{AutomationProperties.GetName(box)} must be more than 0.");
    }

    private static double AtLeastZero(TextBox box)
    {
        var value = Parse(box);
        return value >= 0 ? value : throw new FormatException($"{AutomationProperties.GetName(box)} must be 0 or more.");
    }

    private static double Between(TextBox box, double low, double high)
    {
        var value = Parse(box);
        return value >= low && value <= high ? value : throw new FormatException($"{AutomationProperties.GetName(box)} must be from {Number(low)} to {Number(high)}.");
    }

    private void Save()
    {
        if (_workspace.Current is not { } project) return;
        try
        {
            var seasons = _seasonRows.Select(row => CalendarSeason.Default.WithId(row.Id.Text?.Trim() ?? "").WithName(row.Name.Text?.Trim() ?? "").WithDays(Parse(row.Days))).ToList();
            var festivals = _festivalRows.Select(row => CalendarFestival.Default.WithId(row.Id.Text?.Trim() ?? "").WithName(row.Name.Text?.Trim() ?? "").WithSeasonId(row.SeasonId.Text?.Trim() ?? "").WithDay(Parse(row.Day))).ToList();
            if (seasons.Count == 0) throw new FormatException("The calendar needs at least one season.");
            foreach (var season in seasons)
            {
                var label = season.Name.Length > 0 ? $"Season \"{season.Name}\"" : "A season";
                if (season.Id.Length == 0) throw new FormatException($"{label} needs an id.");
                if (season.Days <= 0 || season.Days != Math.Floor(season.Days)) throw new FormatException($"{label} days must be a whole number above 0.");
            }

            if (seasons.GroupBy(s => s.Id).FirstOrDefault(group => group.Count() > 1) is { } twice)
                throw new FormatException($"Two seasons have the id \"{twice.Key}\"; season ids must be different.");
            foreach (var festival in festivals)
            {
                var label = festival.Name.Length > 0 ? $"Festival \"{festival.Name}\"" : "A festival";
                if (festival.Id.Length == 0) throw new FormatException($"{label} needs an id.");
                if (seasons.FirstOrDefault(s => s.Id == festival.SeasonId) is not { } on) throw new FormatException($"{label} needs a season of the calendar.");
                if (festival.Day != Math.Floor(festival.Day) || festival.Day < 1 || festival.Day > on.Days)
                    throw new FormatException($"{label} day must be a whole number from 1 to {Number(on.Days)}.");
            }

            var curve = SkillLevelCurve();
            var settings = project.Settings
                .WithLocale(_locale.Text?.Trim() ?? "")
                .WithEnergyEnabled(_energy.IsChecked == true)
                .WithSkillsEnabled(_skills.IsChecked == true)
                .WithShowMadeWithCredit(_credit.IsChecked == true)
                .WithMaxEnergy(Positive(_maxEnergy))
                .WithCollapseEnergyFraction(Between(_collapseFraction, 0, 1))
                .WithCollapseMoneyPenalty(AtLeastZero(_collapsePenalty))
                .WithSkillLevelCurve(curve)
                .WithMovement(project.Settings.Movement.WithPlayerSpeed(Positive(_speed)))
                .WithTime(project.Settings.Time.WithDayStartMinute(Whole(_dayStart)).WithDayEndMinute(Whole(_dayEnd)).WithMinutesPerRealSecond(Positive(_minutesPerSecond))
                    // Absent means on: unchecking writes false, checking clears an explicit false.
                    .WithPauseInModals(_pauseInModals.IsChecked == true ? (project.Settings.Time.PauseInModals.OrNullable() is null ? null : true) : false))
                .WithCalendar(project.Settings.Calendar.WithSeasons(seasons).WithFestivals(festivals));
            var name = _name.Text?.Trim() ?? "";
            var version = _version.Text?.Trim() ?? "";
            if (name.Length == 0) throw new FormatException("Name needs a value.");
            if (version.Length == 0) throw new FormatException("Version needs a value.");
            // An end at or before the start collapsed the player on every tick.
            if (!SettingsSchema.validTime(settings.Time))
                throw new FormatException($"The day must end at least {SettingsSchema.MinDayWindowMinutes} minutes after it starts and by minute {SettingsSchema.MaxDayEndMinute}, and the clock can run at most {SettingsSchema.MaxMinutesPerRealSecond} minutes per second.");
            // Faster than this the player is hard to control (Problems warns about it too).
            if (settings.Movement.PlayerSpeed > MaxPlayerSpeed)
                throw new FormatException($"Player speed can be at most {MaxPlayerSpeed} tiles a second.");
            // Seasons removed from the rows go through RemoveSeason first, which also clears them
            // from weather, crops, shops, quests and fish (Cleanup.dropSeason).
            var removed = _loadedSeasons.Where(id => !seasons.Any(season => season.Id == id)).Select(Edits.RemoveSeason);
            _workspace.Apply(Edits.Batch("Project settings", [.. removed, Edits.SetProjectInfo(name, version), Edits.SetSettings(settings)]));
            Load(Section.Project);
            _message.Text = _workspace.SavedText("Settings");
        }
        catch (Exception error) when (error is FormatException or OverflowException)
        {
            _message.Text = $"Could not save: {error.Message}";
        }
    }
}
