using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Interop;
using FarmEngine.Schemas;
using FarmingRpgMaker.App.Projects;

namespace FarmingRpgMaker.App.Game;

/// <summary>Project identity, gameplay settings and calendar, saved as one F# undo step.</summary>
public sealed class SettingsEditorView : UserControl
{
    private sealed record SeasonRow(TextBox Id, TextBox Name, TextBox Days, Control Control);
    private sealed record FestivalRow(TextBox Id, TextBox Name, TextBox SeasonId, TextBox Day, Control Control);

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
    private readonly TextBox _skillCurve = new() { Name = "Setting_SkillLevelCurve" };
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

    public SettingsEditorView(ProjectWorkspace workspace)
    {
        _workspace = workspace;
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
        Field(form, "Skill level curve (JSON array)", _skillCurve);
        form.Children.Add(Ui.Text("TIME", "section"));
        Field(form, "Day start minute", _dayStart);
        Field(form, "Day end minute", _dayEnd);
        Field(form, "Game minutes per real second", _minutesPerSecond);
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
        var revert = Ui.Button("Revert fields", Refresh, "tool");
        form.Children.Add(Ui.HStack(8, save, revert));
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
        _workspace.ProjectChanged += (_, _) =>
        {
            if (IsEffectivelyVisible) Refresh();
        };
        Refresh();
    }

    private static void Field(StackPanel form, string label, TextBox input)
    {
        form.Children.Add(Ui.Text(label, "muted", "small"));
        form.Children.Add(input);
    }

    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    private static double Parse(TextBox input)
    {
        var value = double.Parse(input.Text ?? "", NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value)) throw new FormatException($"{input.Name} must be a finite number.");
        return value;
    }
    private static TextBox Box(string name, string value, double width) => new() { Name = name, Text = value, Width = width };

    private void AddSeasonRow(CalendarSeason season, bool saved = true)
    {
        var id = Box("Season_Id", season.Id, 115);
        id.IsReadOnly = saved;
        var name = Box("Season_Name", season.Name, 145);
        var days = Box("Season_Days", Number(season.Days), 65);
        var remove = Ui.Button("Remove", () =>
        {
            if (saved)
            {
                _workspace.Apply(Edits.RemoveSeason(id.Text ?? ""));
            }
            else
            {
                var row = _seasonRows.First(r => ReferenceEquals(r.Id, id));
                _seasonRows.Remove(row);
                _seasons.Children.Remove(row.Control);
            }
        }, "tool", "small");
        var control = Ui.HStack(8, id, name, days, remove);
        _seasonRows.Add(new SeasonRow(id, name, days, control));
        _seasons.Children.Add(control);
    }

    private void AddFestivalRow(CalendarFestival festival)
    {
        var id = Box("Festival_Id", festival.Id, 110);
        var name = Box("Festival_Name", festival.Name, 145);
        var season = Box("Festival_SeasonId", festival.SeasonId, 110);
        var day = Box("Festival_Day", Number(festival.Day), 60);
        var remove = Ui.Button("Remove", () =>
        {
            var row = _festivalRows.First(r => ReferenceEquals(r.Id, id));
            _festivalRows.Remove(row);
            _festivals.Children.Remove(row.Control);
        }, "tool", "small");
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

    public void Refresh()
    {
        if (_workspace.Current is not { } project) return;
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
        _skillCurve.Text = JsonSerializer.Serialize(settings.SkillLevelCurve, InteropJson.Options);
        _energy.IsChecked = settings.EnergyEnabled;
        _skills.IsChecked = settings.SkillsEnabled;
        _credit.IsChecked = settings.ShowMadeWithCredit;
        _seasonRows.Clear();
        _seasons.Children.Clear();
        foreach (var season in settings.Calendar.Seasons) AddSeasonRow(season);
        _festivalRows.Clear();
        _festivals.Children.Clear();
        foreach (var festival in settings.Calendar.Festivals) AddFestivalRow(festival);
        _message.Text = "";
        RefreshExport(project);
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
            ShowExportProblems(problems);
            _exportMessage.Text = problems.Any(problem => problem.IsError)
                ? "Export settings saved. Fix the errors above before exporting."
                : "Export settings saved.";
        }
        catch (FormatException error)
        {
            _exportMessage.Text = $"Could not save: {error.Message}";
        }
    }

    private void Save()
    {
        if (_workspace.Current is not { } project) return;
        try
        {
            var seasons = _seasonRows.Select(row => CalendarSeason.Default.WithId(row.Id.Text?.Trim() ?? "").WithName(row.Name.Text?.Trim() ?? "").WithDays(Parse(row.Days))).ToList();
            var festivals = _festivalRows.Select(row => CalendarFestival.Default.WithId(row.Id.Text?.Trim() ?? "").WithName(row.Name.Text?.Trim() ?? "").WithSeasonId(row.SeasonId.Text?.Trim() ?? "").WithDay(Parse(row.Day))).ToList();
            if (seasons.Count == 0 || seasons.Any(s => s.Id.Length == 0 || s.Days <= 0 || s.Days != Math.Floor(s.Days)) || seasons.Select(s => s.Id).Distinct().Count() != seasons.Count)
                throw new FormatException("Seasons need distinct ids and positive day counts.");
            if (festivals.Any(f => f.Id.Length == 0 || f.Day != Math.Floor(f.Day) || !seasons.Any(s => s.Id == f.SeasonId && f.Day >= 1 && f.Day <= s.Days)))
                throw new FormatException("Every festival needs a season and a day within it.");
            var curve = JsonSerializer.Deserialize<List<double>>(_skillCurve.Text ?? "", InteropJson.Options) ?? throw new FormatException("The skill curve must be a JSON array.");
            if (curve.Count == 0 || curve.Any(value => !double.IsFinite(value) || value < 0))
                throw new FormatException("The skill curve needs finite nonnegative values.");
            var settings = project.Settings
                .WithLocale(_locale.Text?.Trim() ?? "")
                .WithEnergyEnabled(_energy.IsChecked == true)
                .WithSkillsEnabled(_skills.IsChecked == true)
                .WithShowMadeWithCredit(_credit.IsChecked == true)
                .WithMaxEnergy(Parse(_maxEnergy))
                .WithCollapseEnergyFraction(Parse(_collapseFraction))
                .WithCollapseMoneyPenalty(Parse(_collapsePenalty))
                .WithSkillLevelCurve(curve)
                .WithMovement(project.Settings.Movement.WithPlayerSpeed(Parse(_speed)))
                .WithTime(project.Settings.Time.WithDayStartMinute(Parse(_dayStart)).WithDayEndMinute(Parse(_dayEnd)).WithMinutesPerRealSecond(Parse(_minutesPerSecond)))
                .WithCalendar(project.Settings.Calendar.WithSeasons(seasons).WithFestivals(festivals));
            var name = _name.Text?.Trim() ?? "";
            var version = _version.Text?.Trim() ?? "";
            if (name.Length == 0 || version.Length == 0 || settings.MaxEnergy <= 0 || settings.Movement.PlayerSpeed <= 0 || settings.Time.MinutesPerRealSecond <= 0
                || settings.CollapseEnergyFraction < 0 || settings.CollapseEnergyFraction > 1 || settings.CollapseMoneyPenalty < 0
                || settings.Time.DayStartMinute != Math.Floor(settings.Time.DayStartMinute) || settings.Time.DayEndMinute != Math.Floor(settings.Time.DayEndMinute))
                throw new FormatException("Name, version, energy, speed and time rate must have valid positive values.");
            _workspace.Apply(Edits.Batch("Project settings", [Edits.SetProjectInfo(name, version), Edits.SetSettings(settings)]));
            _message.Text = "Settings saved.";
        }
        catch (Exception error) when (error is FormatException or JsonException or OverflowException)
        {
            _message.Text = $"Could not save: {error.Message}";
        }
    }
}
