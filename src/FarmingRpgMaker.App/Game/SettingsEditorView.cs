using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using FarmEngine.Authoring;
using FarmEngine.Json;
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
        var current = new CalendarConfig
        {
            Seasons = _seasonRows.Select(row => new CalendarSeason { Id = row.Id.Text ?? "", Name = row.Name.Text ?? "", Days = double.TryParse(row.Days.Text, CultureInfo.InvariantCulture, out var days) ? days : 28 }).ToList(),
        };
        AddSeasonRow(Defaults.NewSeason(current), saved: false);
    }

    private void AddFestival()
    {
        if (_workspace.Current is not { } project) return;
        var calendar = new CalendarConfig { Seasons = _seasonRows.Select(row => new CalendarSeason { Id = row.Id.Text ?? "", Name = row.Name.Text ?? "", Days = double.TryParse(row.Days.Text, CultureInfo.InvariantCulture, out var days) ? days : 28 }).ToList(), Festivals = _festivalRows.Select(row => new CalendarFestival { Id = row.Id.Text ?? "" }).ToList() };
        var festival = Defaults.NewFestival(project, calendar);
        if (festival is not null) AddFestivalRow(festival);
    }

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
        _skillCurve.Text = JsonSerializer.Serialize(settings.SkillLevelCurve, JsonDefaults.Options);
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
    }

    private void Save()
    {
        if (_workspace.Current is not { } project) return;
        try
        {
            var seasons = _seasonRows.Select(row => new CalendarSeason { Id = row.Id.Text?.Trim() ?? "", Name = row.Name.Text?.Trim() ?? "", Days = Parse(row.Days) }).ToList();
            var festivals = _festivalRows.Select(row => new CalendarFestival { Id = row.Id.Text?.Trim() ?? "", Name = row.Name.Text?.Trim() ?? "", SeasonId = row.SeasonId.Text?.Trim() ?? "", Day = Parse(row.Day) }).ToList();
            if (seasons.Count == 0 || seasons.Any(s => s.Id.Length == 0 || s.Days <= 0 || s.Days != Math.Floor(s.Days)) || seasons.Select(s => s.Id).Distinct().Count() != seasons.Count)
                throw new FormatException("Seasons need distinct ids and positive day counts.");
            if (festivals.Any(f => f.Id.Length == 0 || f.Day != Math.Floor(f.Day) || !seasons.Any(s => s.Id == f.SeasonId && f.Day >= 1 && f.Day <= s.Days)))
                throw new FormatException("Every festival needs a season and a day within it.");
            var curve = JsonSerializer.Deserialize<List<double>>(_skillCurve.Text ?? "", JsonDefaults.Options) ?? throw new FormatException("The skill curve must be a JSON array.");
            if (curve.Count == 0 || curve.Any(value => !double.IsFinite(value) || value < 0))
                throw new FormatException("The skill curve needs finite nonnegative values.");
            var settings = project.Settings with
            {
                Locale = _locale.Text?.Trim() ?? "",
                EnergyEnabled = _energy.IsChecked == true,
                SkillsEnabled = _skills.IsChecked == true,
                ShowMadeWithCredit = _credit.IsChecked == true,
                MaxEnergy = Parse(_maxEnergy),
                CollapseEnergyFraction = Parse(_collapseFraction),
                CollapseMoneyPenalty = Parse(_collapsePenalty),
                SkillLevelCurve = curve,
                Movement = project.Settings.Movement with { PlayerSpeed = Parse(_speed) },
                Time = project.Settings.Time with { DayStartMinute = Parse(_dayStart), DayEndMinute = Parse(_dayEnd), MinutesPerRealSecond = Parse(_minutesPerSecond) },
                Calendar = new CalendarConfig { Seasons = seasons, Festivals = festivals },
            };
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
