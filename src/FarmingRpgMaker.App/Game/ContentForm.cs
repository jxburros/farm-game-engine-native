using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Interop;
using FarmEngine.Schemas;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.FSharp.Reflection;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// A generated form over a JSON draft of one content entry. Property types pick the controls:
/// nested records become grouped sub-forms, lists get add/remove/move rows, string lists become
/// chips or rows. F# <see cref="ContentForms"/> says what the strings mean (references, choices),
/// which condition and outcome fields each type shows, and what new rows start as. Every nested
/// field also has a collapsed "Edit as JSON" box. Controls write into the draft; the owner turns
/// it back into the record with <see cref="Commit"/> and applies one F# upsert.
/// Where the web has its own layout the form follows it: tabs F# lists for the record (crops),
/// compact schedule and waypoint rows (NPCs), stock cards (shops), and art previews beside visual
/// bindings and legacy image fields.
/// </summary>
internal sealed class ContentForm
{
    private sealed record JsonEscape(string Label, TextBox Box, string Original, bool Nullable, Action<JsonNode?> Replace);

    private readonly GameProject _project;
    private readonly object _entity;
    private readonly Type _entityType;
    private readonly StackPanel _root;
    private readonly Action<string> _report;
    private readonly List<Action> _writers = [];
    private readonly List<JsonEscape> _json = [];
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, IReadOnlyList<PickerOption>> _options = [];
    private Dictionary<string, Item>? _items;
    private VisualPreview? _art;
    /// <summary>The bitmaps of the legacy image thumbnails on show, freed when the form rebuilds.</summary>
    private readonly List<Bitmap> _thumbnails = [];
    private int _tab;

    /// <summary>"Pick on map" for tile coordinates (scene id, prompt, what to do with the tile); null hides the buttons (#47).</summary>
    private readonly Action<string, string, Action<int, int>>? _pickOnMap;

    public ContentForm(GameProject project, object entity, Type entityType, StackPanel root, Action<string> report, Action<string, string, Action<int, int>>? pickOnMap = null)
    {
        _pickOnMap = pickOnMap;
        _project = project;
        _entity = entity;
        _entityType = entityType;
        _root = root;
        _report = report;
        Draft = RecordJson.ToNode(entity, entityType) as JsonObject
            ?? throw new InvalidOperationException("Content did not serialize to an object.");
        Rebuild();
        _initialJson = Draft.ToJsonString();
    }

    /// <summary>The draft as the form was built, to tell whether anything was edited since.</summary>
    private readonly string _initialJson;

    /// <summary>
    /// True when the fields no longer describe the entry the form was built from (#87): something
    /// typed, picked, added, removed or moved, a field holding something invalid, or an edited
    /// "Edit as JSON" box. Typing a value back to what it was is not a change.
    /// </summary>
    public bool IsDirty
    {
        get
        {
            _errors.Clear();
            foreach (var write in _writers) write();
            if (_errors.Count > 0) return true;
            if (_json.Any(escape => (escape.Box.Text ?? "") != escape.Original)) return true;
            return Draft.ToJsonString() != _initialJson;
        }
    }

    /// <summary>The entry as the form currently holds it.</summary>
    public JsonObject Draft { get; }

    /// <summary>Writes every field into the draft and returns the edited record.</summary>
    /// <exception cref="FormatException">A field holds something that is not a valid value.</exception>
    /// <exception cref="JsonException">The draft no longer fits the record (for example after a JSON edit).</exception>
    public object Commit()
    {
        var errors = Flush();
        if (errors.Count > 0) throw new FormatException(string.Join(" ", errors));
        try
        {
            return RecordJson.FromNode(Draft, _entityType);
        }
        catch (FormatException error)
        {
            throw new JsonException(error.Message, error);
        }
    }

    // ---- Draft bookkeeping ----

    private List<string> Flush()
    {
        _errors.Clear();
        foreach (var write in _writers) write();
        // Deeper JSON boxes first, so an edited outer box wins over the rows inside it.
        for (var i = _json.Count - 1; i >= 0; i--)
        {
            var escape = _json[i];
            var text = escape.Box.Text ?? "";
            if (text == escape.Original) continue;
            try
            {
                var node = text.Trim().Length == 0 ? null : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (node is null && !escape.Nullable) _errors.Add($"{escape.Label} needs a JSON value.");
                else escape.Replace(node);
            }
            catch (JsonException error)
            {
                _errors.Add($"{escape.Label} is not valid JSON: {error.Message}");
            }
        }

        return [.. _errors];
    }

    /// <summary>
    /// The entry as the fields describe it right now, for readouts that follow the typing: writes
    /// the fields into the draft (the "Edit as JSON" boxes wait for save) and returns the record,
    /// or null while a field holds something invalid or the draft does not fit the record.
    /// </summary>
    public object? Preview()
    {
        _errors.Clear();
        foreach (var write in _writers) write();
        if (_errors.Count > 0) return null;
        try
        {
            return RecordJson.FromNode(Draft, _entityType);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A structural change (add, remove, move, retype): write the fields, change, rebuild.</summary>
    private void Structural(Action change)
    {
        var errors = Flush();
        if (errors.Count > 0)
        {
            _report("Fix these first: " + string.Join(" ", errors));
            return;
        }

        change();
        Rebuild();
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        ReleaseImages();
        _writers.Clear();
        _json.Clear();
        _options.Clear();
        AddForm();
    }

    private object CurrentEntity()
    {
        try
        {
            return RecordJson.FromNode(Draft, _entityType);
        }
        catch (FormatException)
        {
            return _entity;
        }
    }

    private IReadOnlyList<PickerOption> Options(string kind)
    {
        if (!_options.TryGetValue(kind, out var options))
        {
            options = ContentForms.Options(kind, _project);
            _options[kind] = options;
        }

        return options;
    }

    // ---- Reflection over the schema records (display only) ----

    /// <summary>A record's fields in declaration order, without the undeclared-keys bag.</summary>
    private static IEnumerable<PropertyInfo> FormProperties(Type type) =>
        FSharpType.GetRecordFields(type, null).Where(property => property.Name != "Extra");

    private static string JsonKey(Type owner, PropertyInfo property) => RecordJson.JsonKey(owner, property.Name);

    private static bool IsOption(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FSharpOption<>);

    /// <summary><c>T option</c> → <c>T</c>; <c>float option option</c> (absent, null or a number) stays as it is.</summary>
    private static Type Unwrap(Type type) => IsOption(type) && !IsOption(type.GetGenericArguments()[0]) ? type.GetGenericArguments()[0] : type;

    private static bool IsNullable(PropertyInfo property) => IsOption(property.PropertyType);

    /// <summary><c>float option option</c>: absent, null or a number.</summary>
    private static bool IsOptionalNullableNumber(Type type) => IsOption(type) && IsOption(type.GetGenericArguments()[0]) && IsNumber(type.GetGenericArguments()[0].GetGenericArguments()[0]);

    private static bool IsNumber(Type type) => type == typeof(double) || type == typeof(int) || type == typeof(float) || type == typeof(long);

    private static Type? ListElement(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FSharpList<>) ? type.GetGenericArguments()[0] : null;

    /// <summary>An ordered map of JSON values: <c>(string * Json) list</c>.</summary>
    private static bool IsJsonDictionary(Type type) => type == typeof(FSharpList<Tuple<string, Json>>);

    private static bool IsRecord(Type type) =>
        type.Namespace == typeof(GameProject).Namespace && FSharpType.IsRecord(type, null);

    /// <summary>A record at its schema defaults (<c>Record.Default</c>).</summary>
    private static object DefaultOf(Type type) =>
        type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
        ?? throw new InvalidOperationException($"Cannot create a {type.Name}.");

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string NumberText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number.ToString("R", CultureInfo.InvariantCulture) : "";

    public static string Title(string name)
    {
        var text = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) text.Append(' ');
            text.Append(name[i]);
        }
        return text.ToString();
    }

    /// <summary>"Scene Id" → "Scene": a picker already says it holds an id.</summary>
    private static string TrimId(string label) =>
        label.EndsWith(" Ids", StringComparison.Ordinal) ? label[..^4] + "s"
        : label.EndsWith(" Id", StringComparison.Ordinal) ? label[..^3]
        : label;

    private static string Pascal(string key) => key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..];
    private static string Join(string path, string part) => path.Length == 0 ? part : $"{path}_{part}";

    // ---- Record forms ----

    /// <summary>
    /// The entry itself: one form, or a tab per F# <see cref="FormTab"/> (web CropEditor's Basic /
    /// Growth / Asset). Every tab's controls exist, so the writers and <see cref="Commit"/> do not
    /// depend on the tab showing. The art previews of one build share a renderer that is freed
    /// when the build is done; the images keep their own bitmaps.
    /// </summary>
    private void AddForm()
    {
        _art = new VisualPreview();
        try
        {
            var tabs = ContentReadouts.FormTabs(_entityType.Name);
            if (tabs.Count == 0) AddProperties(_root, Draft, _entityType, "");
            else _root.Children.Add(Tabs(tabs));
        }
        finally
        {
            _art.Dispose();
            _art = null;
        }
    }

    /// <summary>The tabs in F# order; properties no tab lists go on the last one. A rebuild keeps the selected tab.</summary>
    private TabControl Tabs(IReadOnlyList<FormTab> tabs)
    {
        var properties = VisibleProperties(Draft, _entityType).ToList();
        var listed = tabs.SelectMany(ContentReadouts.TabProperties).ToHashSet();
        var control = new TabControl { Name = "ContentTabs" };
        for (var i = 0; i < tabs.Count; i++)
        {
            var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
            var shown = ContentReadouts.TabProperties(tabs[i])
                .Select(name => properties.FirstOrDefault(property => property.Name == name))
                .OfType<PropertyInfo>();
            if (i == tabs.Count - 1) shown = shown.Concat(properties.Where(property => !listed.Contains(property.Name)));
            foreach (var property in shown) AddProperty(panel, Draft, _entityType, property, property.Name);
            var title = tabs[i].Title;
            control.Items.Add(Accessible(new TabItem { Name = "ContentTab_" + string.Concat(title.Where(char.IsLetterOrDigit)), Header = title, Content = panel }, title));
        }

        control.SelectedIndex = Math.Clamp(_tab, 0, tabs.Count - 1);
        control.SelectionChanged += (_, e) =>
        {
            // Pickers inside the tabs raise the same (bubbling) event.
            if (ReferenceEquals(e.Source, control) && control.SelectedIndex >= 0) _tab = control.SelectedIndex;
        };
        return control;
    }

    /// <summary>A record's fields as the form shows them: F# hides some while its <c>type</c> says so (quest objective targets).</summary>
    private static IEnumerable<PropertyInfo> VisibleProperties(JsonObject target, Type type)
    {
        var hidden = type.GetProperty("Type")?.PropertyType == typeof(string)
            ? ContentForms.HiddenProperties(type.Name, StringOf(target["type"]) ?? "")
            : [];
        return FormProperties(type).Where(property => !hidden.Contains(property.Name));
    }

    private void AddProperties(Panel panel, JsonObject target, Type type, string path)
    {
        foreach (var property in VisibleProperties(target, type))
        {
            AddProperty(panel, target, type, property, Join(path, property.Name));
        }
    }

    private void AddProperty(Panel panel, JsonObject target, Type owner, PropertyInfo property, string path)
    {
        var key = JsonKey(owner, property);
        var label = Title(property.Name);
        var name = "ContentField_" + path;
        var type = Unwrap(property.PropertyType);
        var nullable = IsNullable(property);
        var field = ContentForms.Field(owner.Name, property.Name, label);
        if (field is { Kind: "reference" or "referenceList" }) label = TrimId(label);

        if (property.Name == "Id" && type == typeof(string))
        {
            panel.Children.Add(Label(label));
            panel.Children.Add(Accessible(new TextBox { Name = name, Text = StringOf(target[key]) ?? "", IsReadOnly = true }, label));
            return;
        }

        if (type == typeof(string))
        {
            panel.Children.Add(Label(label));
            var control = StringControl(target, key, name, label, field, nullable,
                multiline: property.Name is "Description" or "Text" or "Message" or "FailMessage",
                rebuildOnChange: property.Name == "Type");
            panel.Children.Add(control is TextBox box && property.Name is "CustomImage" or "CustomAsset" ? WithThumbnail(box, path, label) : control);
            return;
        }

        if (type == typeof(bool))
        {
            panel.Children.Add(BoolControl(target, key, name, label, nullable ? null : false));
            return;
        }

        if (IsNumber(type) || IsOptionalNullableNumber(type))
        {
            panel.Children.Add(Label(label));
            panel.Children.Add(NumberControl(target, key, name, label, type == typeof(int) || type == typeof(long),
                optional: nullable || IsOptionalNullableNumber(type), min: null, max: null));
            return;
        }

        var body = new StackPanel { Spacing = 8 };
        if (IsJsonDictionary(type)) AddKeyValues(body, target, key, path);
        else if (ListElement(type) is { } element)
        {
            if (element == typeof(string))
            {
                if (field is { Kind: "referenceList" }) AddReferenceChips(body, target, key, path, label, field.Reference, nullable);
                else AddTextRows(body, target, key, path, label, nullable);
            }
            else if (IsNumber(element)) AddNumberRows(body, target, key, path, label, nullable);
            else if (element == typeof(EventCondition)) AddConditions(body, target, key, path);
            else if (element == typeof(EventOutcome)) AddOutcomes(body, target, key, path);
            else if (element == typeof(NpcScheduleEntry)) AddScheduleRows(body, target, key, path, nullable);
            else if (element == typeof(GridPoint)) AddWaypointRows(body, target, key, path, nullable);
            else if (element == typeof(ShopStockEntry)) AddStockCards(body, target, key, path, nullable);
            else if (IsRecord(element)) AddRecordList(body, target, key, path, element, nullable);
        }
        else if (IsRecord(type)) AddNested(body, target, key, path, type, nullable, label);

        AddJsonEscape(body, target, key, path, label, nullable);
        var group = new StackPanel { Spacing = 6 };
        group.Children.Add(Ui.Text(label.ToUpperInvariant(), "category"));
        group.Children.Add(body);
        panel.Children.Add(new Border { Name = "ContentGroup_" + path, Child = group }.WithClasses("form-group"));
    }

    private static TextBlock Label(string text) => Ui.Text(text, "muted", "small");

    /// <summary>Gives <paramref name="control"/> the name screen readers announce (its visible label).</summary>
    private static T Accessible<T>(T control, string name) where T : Control
    {
        AutomationProperties.SetName(control, name);
        return control;
    }

    /// <summary>Controls side by side in <paramref name="columns"/> (Grid column definitions).</summary>
    private static Grid Columns(string columns, params Control[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 6 };
        for (var i = 0; i < children.Length; i++)
        {
            Grid.SetColumn(children[i], i);
            grid.Children.Add(children[i]);
        }

        return grid;
    }

    private static StackPanel Labeled(string label, Control control, double width = 0)
    {
        var stack = Ui.VStack(3, Label(label), control);
        if (width > 0) stack.Width = width;
        return stack;
    }

    // ---- Scalars ----

    private Control StringControl(JsonObject target, string key, string name, string label, FormField? field, bool nullable, bool multiline, bool rebuildOnChange,
        Func<PickerOption, string>? optionLabel = null)
    {
        var current = StringOf(target[key]);
        if (field is { Kind: "reference" or "choice" })
        {
            var picker = Accessible(Picker(name, field, current, optionLabel), label);
            var initial = SelectedId(picker);
            Writer(target, key, () => SelectedId(picker) is not { } id || id == initial, () =>
            {
                var id = SelectedId(picker)!;
                target[key] = id.Length == 0 && nullable ? null : JsonValue.Create(id);
            });
            if (rebuildOnChange) picker.SelectionChanged += (_, _) => { if (SelectedId(picker) is { } id && id != initial) Structural(() => { }); };
            return picker;
        }

        var box = Accessible(new TextBox
        {
            Name = name,
            Text = current ?? "",
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 54 : 30,
        }, label);
        if (field is { Kind: "plain" } && field.Reason.Length > 0) ToolTip.SetTip(box, Ui.Capitalize(field.Reason) + ".");
        var original = box.Text;
        Writer(target, key, () => (box.Text ?? "") == original, () =>
        {
            var text = box.Text ?? "";
            target[key] = nullable && text.Length == 0 ? null : JsonValue.Create(text);
        });
        return box;
    }

    private CheckBox BoolControl(JsonObject target, string key, string name, string label, bool? whenAbsent)
    {
        var value = target[key] is JsonValue node && node.TryGetValue<bool>(out var flag) ? flag : whenAbsent;
        var check = Accessible(new CheckBox { Name = name, Content = label, IsChecked = value, IsThreeState = whenAbsent is null }, label);
        var original = check.IsChecked;
        Writer(target, key, () => check.IsChecked == original, () => target[key] = check.IsChecked is { } on ? JsonValue.Create(on) : null);
        return check;
    }

    /// <summary>
    /// A number box. Saving never changes what was typed behind the creator's back (#46): an
    /// emptied required field and a value outside <paramref name="min"/>–<paramref name="max"/>
    /// are reported by name instead of being reset or clamped.
    /// </summary>
    private TextBox NumberControl(JsonObject target, string key, string name, string label, bool integer, bool optional, double? min, double? max)
    {
        var box = Accessible(new TextBox { Name = name, Text = NumberText(target[key]), MinWidth = 90 }, label);
        var original = box.Text;
        Writer(target, key, () => (box.Text ?? "").Trim() == original, () =>
        {
            var raw = (box.Text ?? "").Trim();
            if (raw.Length == 0)
            {
                if (optional) target[key] = null;
                else _errors.Add($"{label} needs a value.");
                return;
            }

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)
                || (integer && value != Math.Floor(value)))
            {
                _errors.Add($"{label} must be {(integer ? "a whole number" : "a number")}.");
                return;
            }

            if ((min is { } low && value < low) || (max is { } high && value > high))
            {
                _errors.Add(RangeText(label, min, max));
                return;
            }

            target[key] = JsonValue.Create(value);
        });
        return box;
    }

    /// <summary>"Schedule entry 1 minute must be from 0 to 1560." (or "at least" / "at most").</summary>
    private static string RangeText(string label, double? min, double? max) => (min, max) switch
    {
        ({ } low, { } high) => $"{label} must be from {Ui.Num(low)} to {Ui.Num(high)}.",
        ({ } low, null) => $"{label} must be at least {Ui.Num(low)}.",
        (null, { } high) => $"{label} must be at most {Ui.Num(high)}.",
        _ => $"{label} is out of range.",
    };

    /// <summary>
    /// Registers the writer of the field <paramref name="key"/>: <paramref name="write"/> runs when
    /// the control changed. Unchanged, the value the field had when the form was built goes back
    /// into the draft, since an earlier <see cref="Preview"/> may have written a value the creator
    /// then typed back: the draft always follows the controls.
    /// </summary>
    private void Writer(JsonObject target, string key, Func<bool> unchanged, Action write)
    {
        var had = target.ContainsKey(key);
        var before = target[key]?.DeepClone();
        _writers.Add(() =>
        {
            if (!unchanged())
            {
                write();
                return;
            }

            if (!had)
            {
                target.Remove(key);
            }
            else if (!JsonNode.DeepEquals(target[key], before))
            {
                target[key] = before?.DeepClone();
            }
        });
    }

    /// <summary>
    /// A picker of <paramref name="field"/>'s choices or references, keeping an unknown current id
    /// as "(missing: id)"; <paramref name="optionLabel"/> relabels the known entries.
    /// </summary>
    private ComboBox Picker(string name, FormField field, string? current, Func<PickerOption, string>? optionLabel = null)
    {
        var available = field.Kind == "choice" ? field.Choices : Options(field.Reference);
        var picker = new ComboBox
        {
            Name = name,
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Pick…",
            IsEditable = field.Kind == "reference",
            MaxDropDownHeight = 320,
        };
        foreach (var option in ContentForms.Entries(field, available, current))
        {
            var label = optionLabel is not null && !option.Missing ? optionLabel(option) : option.Label;
            var item = new ComboBoxItem { Content = label, Tag = option.Id };
            TextSearch.SetText(item, label);
            if (option.Missing) item.Classes.Add("missing");
            picker.Items.Add(item);
        }

        var selected = current ?? "";
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag! == selected);
        return picker;
    }

    private static string? SelectedId(ComboBox picker) => (picker.SelectedItem as ComboBoxItem)?.Tag as string;

    // ---- Lists ----

    private JsonArray EnsureArray(JsonObject target, string key)
    {
        if (target[key] is JsonArray array) return array;
        var created = new JsonArray();
        target[key] = created;
        return created;
    }

    private Button SmallButton(string name, string text, string tip, Action action, bool enabled = true)
    {
        var button = Accessible(Ui.Button(text, action, "tool", "small"), tip);
        button.Name = name;
        button.IsEnabled = enabled;
        ToolTip.SetTip(button, tip);
        return button;
    }

    private Control RowButtons(string path, int index, int count, JsonArray array, JsonObject target, string key, bool nullable, string what) =>
        Ui.HStack(4,
            SmallButton($"ContentUp_{path}_{index}", "↑", $"Move {what} up", () => Structural(() => Move(array, index, -1)), index > 0),
            SmallButton($"ContentDown_{path}_{index}", "↓", $"Move {what} down", () => Structural(() => Move(array, index, 1)), index < count - 1),
            SmallButton($"ContentRemove_{path}_{index}", "×", $"Remove {what}", () => Structural(() =>
            {
                array.RemoveAt(index);
                if (array.Count == 0 && nullable) target[key] = null;
            })));

    private static void Move(JsonArray array, int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= array.Count) return;
        var node = array[index];
        array.RemoveAt(index);
        array.Insert(target, node);
    }

    private static Border ListRow(Control header, Control body)
    {
        var stack = Ui.VStack(6, header, body);
        return new Border { Child = stack }.WithClasses("row");
    }

    private static Grid RowHeader(Control title, Control buttons)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(title);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    private void AddRecordList(Panel panel, JsonObject target, string key, string path, Type element, bool nullable)
    {
        var array = target[key] as JsonArray;
        var what = Title(element.Name).ToLowerInvariant();
        for (var i = 0; i < (array?.Count ?? 0); i++)
        {
            var body = new StackPanel { Spacing = 6 };
            if (array![i] is JsonObject item) AddProperties(body, item, element, $"{path}_{i}");
            else body.Children.Add(Ui.Wrapped("This entry is not an object; use Edit as JSON.", "muted", "small"));
            var header = RowHeader(Ui.Text($"{i + 1}.", "category"), RowButtons(path, i, array.Count, array, target, key, nullable, what));
            panel.Children.Add(ListRow(header, body));
        }

        panel.Children.Add(AddElementButton(path, $"Add {what}", $"Add a {what}", element, target, key));
    }

    /// <summary>Appends what F# says a new <paramref name="element"/> of this entry starts as.</summary>
    private Button AddElementButton(string path, string text, string tip, Type element, JsonObject target, string key) =>
        SmallButton($"ContentAdd_{path}", text, tip, () => Structural(() =>
        {
            var list = EnsureArray(target, key);
            list.Add(NewElementNode(element, list));
        }));

    private JsonNode NewElementNode(Type element, JsonArray siblings)
    {
        var ids = siblings.OfType<JsonObject>().Select(sibling => StringOf(sibling["id"])).OfType<string>().ToList();
        var created = ContentForms.NewElement(element.Name, _project, CurrentEntity(), ids) ?? DefaultOf(element);
        return RecordJson.ToNode(created, element);
    }

    private void AddNested(Panel panel, JsonObject target, string key, string path, Type type, bool nullable, string label)
    {
        var node = target[key] as JsonObject;
        if (nullable)
        {
            var include = new CheckBox { Name = $"ContentInclude_{path}", Content = $"Use {label.ToLowerInvariant()}", IsChecked = node is not null };
            include.IsCheckedChanged += (_, _) => Structural(() =>
            {
                if (include.IsChecked == true)
                {
                    var created = ContentForms.NewElement(type.Name, _project, CurrentEntity(), []) ?? DefaultOf(type);
                    target[key] = RecordJson.ToNode(created, type);
                }
                else target[key] = null;
            });
            panel.Children.Add(include);
        }

        if (node is null) return;
        if (type == typeof(VisualRef)) AddVisual(panel, node, path);
        else AddProperties(panel, node, type, path);
    }

    private void AddTextRows(Panel panel, JsonObject target, string key, string path, string label, bool nullable)
    {
        var array = target[key] as JsonArray;
        for (var i = 0; i < (array?.Count ?? 0); i++)
        {
            var index = i;
            var box = Accessible(new TextBox { Name = $"ContentField_{path}_{i}", Text = StringOf(array![i]) ?? array[i]?.ToJsonString() ?? "", MinWidth = 180 }, $"{label} {i + 1}");
            var original = box.Text;
            var before = array[i]?.DeepClone();
            _writers.Add(() =>
            {
                if (box.Text != original) array[index] = JsonValue.Create(box.Text ?? "");
                else if (!JsonNode.DeepEquals(array[index], before)) array[index] = before?.DeepClone();
            });
            panel.Children.Add(Ui.Row(box, SmallButton($"ContentRemove_{path}_{i}", "×", "Remove", () => Structural(() =>
            {
                array.RemoveAt(index);
                if (array.Count == 0 && nullable) target[key] = null;
            }))));
        }

        panel.Children.Add(SmallButton($"ContentAdd_{path}", "Add", "Add a row", () => Structural(() => EnsureArray(target, key).Add(JsonValue.Create("")))));
    }

    private void AddNumberRows(Panel panel, JsonObject target, string key, string path, string label, bool nullable)
    {
        var array = target[key] as JsonArray;
        var rows = new WrapPanel();
        for (var i = 0; i < (array?.Count ?? 0); i++)
        {
            var index = i;
            var holder = new JsonObject { ["value"] = array![i]?.DeepClone() };
            var box = NumberControl(holder, "value", $"ContentField_{path}_{i}", $"{label} {i + 1}", integer: false, optional: false, min: null, max: null);
            _writers.Add(() =>
            {
                if (!JsonNode.DeepEquals(holder["value"], array[index])) array[index] = holder["value"]?.DeepClone();
            });
            box.Width = 90;
            rows.Children.Add(Ui.HStack(2, box, SmallButton($"ContentRemove_{path}_{i}", "×", "Remove", () => Structural(() =>
            {
                array.RemoveAt(index);
                if (array.Count == 0 && nullable) target[key] = null;
            }))));
        }

        panel.Children.Add(rows);
        panel.Children.Add(SmallButton($"ContentAdd_{path}", "Add", "Add a value", () => Structural(() => EnsureArray(target, key).Add(JsonValue.Create(0.0)))));
    }

    /// <summary>Reference lists as chips with a picker that adds the ids not chosen yet.</summary>
    private void AddReferenceChips(Panel panel, JsonObject target, string key, string path, string label, string kind, bool nullable)
    {
        var array = target[key] as JsonArray;
        var ids = array?.Select(node => StringOf(node) ?? "").ToList() ?? [];
        var available = Options(kind);
        var chips = new WrapPanel { Name = $"ContentChips_{path}" };
        var entries = ContentForms.ListEntries(available, ids);
        for (var i = 0; i < entries.Count; i++)
        {
            var index = i;
            var text = Ui.Text(entries[i].Label, entries[i].Missing ? "error" : "small");
            var remove = SmallButton($"ContentChipRemove_{path}_{i}", "×", $"Remove {entries[i].Label}", () => Structural(() =>
            {
                array!.RemoveAt(index);
                if (array.Count == 0 && nullable) target[key] = null;
            }));
            chips.Children.Add(new Border { Name = $"ContentChip_{path}_{i}", Child = Ui.HStack(4, text, remove), Margin = new Thickness(0, 0, 6, 6) }.WithClasses("chip"));
        }

        if (entries.Count == 0) chips.Children.Add(Ui.Text(nullable ? "None (no restriction)" : "None", "muted", "small"));
        panel.Children.Add(chips);
        var add = Accessible(new ComboBox { Name = $"ContentChipAdd_{path}", PlaceholderText = "Add…", MinWidth = 200 }, $"Add to {label}");
        foreach (var option in available.Where(option => !ids.Contains(option.Id)))
        {
            var item = new ComboBoxItem { Content = option.Label, Tag = option.Id };
            TextSearch.SetText(item, option.Label);
            add.Items.Add(item);
        }

        add.IsEnabled = add.Items.Count > 0;
        add.SelectionChanged += (_, _) =>
        {
            if (SelectedId(add) is { } id) Structural(() => EnsureArray(target, key).Add(JsonValue.Create(id)));
        };
        panel.Children.Add(add);
    }

    private void AddKeyValues(Panel panel, JsonObject target, string key, string path)
    {
        var entries = (target[key] as JsonObject)?.ToList() ?? [];
        var rows = new List<(TextBox Key, TextBox Value)>();
        var changed = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var index = i;
            var keyBox = Accessible(new TextBox { Name = $"ContentKey_{path}_{i}", Text = entries[i].Key, Width = 140 }, $"Setting {i + 1} name");
            var value = entries[i].Value;
            var valueBox = Accessible(new TextBox { Name = $"ContentValue_{path}_{i}", Text = StringOf(value) ?? value?.ToJsonString() ?? "null", MinWidth = 160 }, $"Setting {i + 1} value");
            var originalKey = keyBox.Text;
            var originalValue = valueBox.Text;
            _writers.Add(() => changed |= keyBox.Text != originalKey || valueBox.Text != originalValue);
            rows.Add((keyBox, valueBox));
            panel.Children.Add(Ui.Row(Ui.HStack(6, keyBox, valueBox), SmallButton($"ContentRemove_{path}_{i}", "×", "Remove setting", () => Structural(() =>
            {
                var next = new JsonObject();
                var current = (target[key] as JsonObject)?.ToList() ?? [];
                for (var n = 0; n < current.Count; n++)
                {
                    if (n != index) next[current[n].Key] = current[n].Value?.DeepClone();
                }
                target[key] = next;
            }))));
        }

        _writers.Add(() =>
        {
            if (!changed) return;
            var next = new JsonObject();
            foreach (var (keyBox, valueBox) in rows)
            {
                var name = keyBox.Text?.Trim() ?? "";
                if (name.Length > 0) next[name] = Literal(valueBox.Text ?? "");
            }
            target[key] = next;
            changed = false;
        });
        panel.Children.Add(SmallButton($"ContentAdd_{path}", "Add setting", "Add a setting", () => Structural(() =>
        {
            if (target[key] is not JsonObject values) target[key] = values = new JsonObject();
            var n = values.Count + 1;
            while (values.ContainsKey($"setting{n}")) n++;
            values[$"setting{n}"] = "";
        })));
    }

    /// <summary>A setting value: JSON when it parses (numbers, booleans, quoted text), else text.</summary>
    private static JsonNode? Literal(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    // ---- Compact rows and cards (web NPCEditor's schedule and waypoints, ShopEditor's stock) ----

    private static string Key<T>(string property) => RecordJson.JsonKey(typeof(T), property);

    private static TextBlock EmptyNote(string path, string text)
    {
        var note = Ui.Wrapped(text, "muted", "small");
        note.Name = $"ContentEmpty_{path}";
        return note;
    }

    private static Control NotAnObject(Control buttons) =>
        Columns("*,Auto", Ui.Wrapped("This entry is not an object; use Edit as JSON.", "muted", "small"), buttons);

    /// <summary>A whole-number tile coordinate box.</summary>
    private TextBox Coordinate(JsonObject target, string key, string name, string label, string watermark)
    {
        var box = NumberControl(target, key, name, label, integer: true, optional: false, min: null, max: null);
        box.MinWidth = 0;
        box.Width = 64;
        box.Watermark = watermark;
        return box;
    }

    /// <summary>
    /// Web NPCEditor's daily schedule: a row per entry with the minute of day and its clock time,
    /// the scene, x and y. New entries come from F# (8:00 AM at the NPC's own scene and tile).
    /// </summary>
    private void AddScheduleRows(Panel panel, JsonObject target, string key, string path, bool nullable)
    {
        var array = target[key] as JsonArray;
        var count = array?.Count ?? 0;
        var sceneField = ContentForms.Field(nameof(NpcScheduleEntry), nameof(NpcScheduleEntry.SceneId), "Scene");
        for (var i = 0; i < count; i++)
        {
            var title = $"Schedule entry {i + 1}";
            var buttons = RowButtons(path, i, count, array!, target, key, nullable, $"schedule entry {i + 1}");
            if (array![i] is not JsonObject entry)
            {
                panel.Children.Add(NotAnObject(buttons));
                continue;
            }

            var minute = NumberControl(entry, Key<NpcScheduleEntry>(nameof(NpcScheduleEntry.Minute)), $"ContentField_{path}_{i}_Minute", $"{title} minute",
                integer: true, optional: false, min: 0, max: 1560);
            minute.MinWidth = 0;
            ToolTip.SetTip(minute, "Minute of day (480 = 8:00 AM)");
            var clock = Ui.Text(ClockText(minute.Text), "muted", "small");
            clock.Name = $"ContentClock_{path}_{i}";
            minute.TextChanged += (_, _) => clock.Text = ClockText(minute.Text);
            var scene = StringControl(entry, Key<NpcScheduleEntry>(nameof(NpcScheduleEntry.SceneId)), $"ContentField_{path}_{i}_SceneId", $"{title} scene",
                sceneField, nullable: false, multiline: false, rebuildOnChange: false);
            scene.MinWidth = 120;
            var x = Coordinate(entry, Key<NpcScheduleEntry>(nameof(NpcScheduleEntry.X)), $"ContentField_{path}_{i}_X", $"{title} x", "x");
            var y = Coordinate(entry, Key<NpcScheduleEntry>(nameof(NpcScheduleEntry.Y)), $"ContentField_{path}_{i}_Y", $"{title} y", "y");
            string Scene() => scene switch
            {
                ComboBox picker => SelectedId(picker) ?? "",
                TextBox box => box.Text ?? "",
                _ => "",
            };
            var pick = PickButton($"ContentPick_{path}_{i}", $"where schedule entry {i + 1} goes", Scene, x, y);
            var row = pick is null
                ? Columns("72,118,*,Auto,Auto,Auto", minute, clock, scene, x, y, buttons)
                : Columns("72,118,*,Auto,Auto,Auto,Auto", minute, clock, scene, x, y, pick, buttons);
            row.Name = $"ContentRow_{path}_{i}";
            panel.Children.Add(row);
        }

        if (count == 0) panel.Children.Add(EmptyNote(path, "No schedule — the NPC stays put (or wanders/patrols)."));
        panel.Children.Add(AddElementButton(path, "Add schedule entry", "Add schedule entry", typeof(NpcScheduleEntry), target, key));
    }

    /// <summary>A typed minute of day as a clock time ("8:00 AM"), clamped like the saved value; empty while it is not a number.</summary>
    private static string ClockText(string? text) =>
        double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minute) && double.IsFinite(minute)
            ? ContentReadouts.Clock(Math.Clamp(minute, 0, 1560))
            : "";

    /// <summary>Web NPCEditor's patrol waypoints: "1. x y" rows; new points start at the NPC's tile (F#).</summary>
    private void AddWaypointRows(Panel panel, JsonObject target, string key, string path, bool nullable)
    {
        var array = target[key] as JsonArray;
        var count = array?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            var title = $"Waypoint {i + 1}";
            var buttons = RowButtons(path, i, count, array!, target, key, nullable, $"waypoint {i + 1}");
            if (array![i] is not JsonObject point)
            {
                panel.Children.Add(NotAnObject(buttons));
                continue;
            }

            var number = Ui.Text($"{i + 1}.", "muted", "small");
            number.MinWidth = 18;
            var x = Coordinate(point, Key<GridPoint>(nameof(GridPoint.X)), $"ContentField_{path}_{i}_X", $"{title} x", "x");
            var y = Coordinate(point, Key<GridPoint>(nameof(GridPoint.Y)), $"ContentField_{path}_{i}_Y", $"{title} y", "y");
            var row = Ui.HStack(6, number, x, y, buttons);
            if (PickButton($"ContentPick_{path}_{i}", $"waypoint {i + 1}", () => StringOf(Draft["sceneId"]) ?? "", x, y) is { } pick)
            {
                row.Children.Insert(3, pick);
            }
            row.Name = $"ContentRow_{path}_{i}";
            panel.Children.Add(row);
        }

        if (count == 0) panel.Children.Add(EmptyNote(path, "No waypoints yet."));
        panel.Children.Add(AddElementButton(path, "Add waypoint", "Add waypoint", typeof(GridPoint), target, key));
    }

    /// <summary>
    /// Web ShopEditor's stock: a card per entry with the item (its base value in the label), a price
    /// override over that base, a daily limit and the calendar's seasons as checkboxes. Keys the form
    /// does not know stay in the entry.
    /// </summary>
    private void AddStockCards(Panel panel, JsonObject target, string key, string path, bool nullable)
    {
        var array = target[key] as JsonArray;
        var count = array?.Count ?? 0;
        var itemField = ContentForms.Field(nameof(ShopStockEntry), nameof(ShopStockEntry.ItemId), "Item");
        var itemKey = Key<ShopStockEntry>(nameof(ShopStockEntry.ItemId));
        for (var i = 0; i < count; i++)
        {
            var title = $"Stock {i + 1}";
            var buttons = RowButtons(path, i, count, array!, target, key, nullable, $"stock entry {i + 1}");
            if (array![i] is not JsonObject entry)
            {
                panel.Children.Add(NotAnObject(buttons));
                continue;
            }

            var item = StringControl(entry, itemKey, $"ContentField_{path}_{i}_ItemId", $"{title} item", itemField, nullable: false, multiline: false, rebuildOnChange: false,
                optionLabel: StockLabel);
            var price = NumberControl(entry, Key<ShopStockEntry>(nameof(ShopStockEntry.Price)), $"ContentField_{path}_{i}_Price", $"{title} price override",
                integer: false, optional: true, min: null, max: null);
            price.Watermark = BaseValueText(StringOf(entry[itemKey]));
            if (item is ComboBox picker) picker.SelectionChanged += (_, _) => price.Watermark = BaseValueText(SelectedId(picker));
            var limit = LimitControl(entry, Key<ShopStockEntry>(nameof(ShopStockEntry.DailyLimit)), $"ContentField_{path}_{i}_DailyLimit", $"{title} daily limit");
            var seasons = SeasonChecks(entry, Key<ShopStockEntry>(nameof(ShopStockEntry.Seasons)), $"{path}_{i}", title);
            var card = Ui.VStack(8,
                Columns("*,Auto", item, buttons),
                Columns("*,*", Labeled("Price override", price), Labeled("Daily limit", limit)),
                Ui.VStack(4, Label("Seasons (none = all seasons)"), seasons));
            panel.Children.Add(new Border { Name = $"ContentCard_{path}_{i}", Child = card }.WithClasses("row"));
        }

        if (count == 0) panel.Children.Add(EmptyNote(path, "No stock entries. Add one to sell items."));
        panel.Children.Add(AddElementButton(path, "Add stock entry", "Add stock entry", typeof(ShopStockEntry), target, key));
    }

    /// <summary>The effective items (the project's, or the built-in catalog) by id.</summary>
    private Dictionary<string, Item> ItemsById() =>
        _items ??= ProjectContent.Compile(_project).Items.DistinctBy(item => item.Id).ToDictionary(item => item.Id);

    /// <summary>"Wheat ($25)": an item with its base value (web ShopEditor's picker).</summary>
    private string StockLabel(PickerOption option) =>
        ItemsById().TryGetValue(option.Id, out var item)
            ? $"{(string.IsNullOrWhiteSpace(item.Name) ? item.Id : item.Name)} ({ContentReadouts.Money(item.Value)})"
            : option.Label;

    /// <summary>The price placeholder: "25 (base)", the item's value when there is no override.</summary>
    private string BaseValueText(string? itemId) =>
        itemId is not null && ItemsById().TryGetValue(itemId, out var item) ? $"{Ui.Num(item.Value)} (base)" : "Base value";

    /// <summary>A whole-number limit; empty, zero or less removes it (web "Unlimited").</summary>
    private TextBox LimitControl(JsonObject target, string key, string name, string label)
    {
        var box = Accessible(new TextBox { Name = name, Text = NumberText(target[key]), MinWidth = 90, Watermark = "Unlimited" }, label);
        var original = box.Text;
        Writer(target, key, () => (box.Text ?? "").Trim() == original, () =>
        {
            var raw = (box.Text ?? "").Trim();
            if (raw.Length == 0)
            {
                target[key] = null;
                return;
            }

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value != Math.Floor(value))
            {
                _errors.Add($"{label} must be a whole number.");
                return;
            }

            target[key] = value > 0 ? JsonValue.Create(value) : null;
        });
        return box;
    }

    /// <summary>
    /// A season filter as a checkbox per calendar season: none checked means every season and is
    /// stored as absent. Ids the calendar no longer has stay listed as "(missing: id)" until unchecked.
    /// </summary>
    private WrapPanel SeasonChecks(JsonObject target, string key, string path, string title)
    {
        var ids = (target[key] as JsonArray)?.Select(node => StringOf(node) ?? "").ToList() ?? [];
        var calendar = Options("season");
        var seasons = calendar.Concat(ContentForms.ListEntries(calendar, ids).Where(entry => entry.Missing)).DistinctBy(entry => entry.Id).ToList();
        var wrap = new WrapPanel { Name = $"ContentSeasons_{path}" };
        var checks = new List<(string Id, CheckBox Check)>();
        for (var i = 0; i < seasons.Count; i++)
        {
            var check = new CheckBox
            {
                Name = $"ContentSeason_{path}_{i}",
                Content = seasons[i].Label,
                IsChecked = ids.Contains(seasons[i].Id),
                Margin = new Thickness(0, 0, 14, 0),
            };
            checks.Add((seasons[i].Id, Accessible(check, $"{title} season {seasons[i].Label}")));
            wrap.Children.Add(check);
        }

        var original = checks.Select(entry => entry.Check.IsChecked).ToList();
        Writer(target, key, () => checks.Select(entry => entry.Check.IsChecked).SequenceEqual(original), () =>
        {
            var chosen = checks.Where(entry => entry.Check.IsChecked == true).Select(entry => (JsonNode?)JsonValue.Create(entry.Id)).ToArray();
            target[key] = chosen.Length == 0 ? null : new JsonArray(chosen);
        });
        return wrap;
    }

    // ---- Art previews ----

    private const double ArtSize = 64;

    /// <summary>
    /// A visual binding's fields beside a small preview of its art, drawn by the Rust renderer as
    /// the game draws it; picking another asset redraws it.
    /// </summary>
    private void AddVisual(Panel panel, JsonObject node, string path)
    {
        var fields = new StackPanel { Spacing = 8 };
        AddProperties(fields, node, typeof(VisualRef), path);
        var preview = Accessible(new Border
        {
            Name = $"ContentArt_{path}",
            Width = ArtSize + 10,
            Height = ArtSize + 10,
            Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Top,
        }.WithClasses("row"), "Art preview");
        Show(preview, ArtImage(node));
        var assetKey = Key<VisualRef>(nameof(VisualRef.AssetId));
        var pickerName = $"ContentField_{Join(path, nameof(VisualRef.AssetId))}";
        if (fields.Children.OfType<ComboBox>().FirstOrDefault(picker => picker.Name == pickerName) is { } picker)
        {
            picker.SelectionChanged += (_, _) =>
            {
                if (SelectedId(picker) is not { } id) return;
                var chosen = (JsonObject)node.DeepClone();
                chosen[assetKey] = id;
                Show(preview, ArtImage(chosen));
            };
        }

        panel.Children.Add(Columns("*,Auto", fields, preview));
    }

    /// <summary>A preview frame holds an image, or hides while there is none.</summary>
    private static void Show(Border frame, Image? image)
    {
        frame.Child = image;
        frame.IsVisible = image is not null;
    }

    /// <summary>The art a visual binding draws, or null when it draws nothing.</summary>
    private Image? ArtImage(JsonNode node)
    {
        VisualRef visual;
        try
        {
            visual = RecordJson.FromNode<VisualRef>(node);
        }
        catch (FormatException)
        {
            return null;
        }

        if (visual.AssetId.Length == 0) return null;
        if (_art is not null) return _art.Render(_project, visual, 0, ArtSize);
        using var art = new VisualPreview();
        return art.Render(_project, visual, 0, ArtSize);
    }

    /// <summary>Frees the thumbnail bitmaps (the form rebuilds, or the editor moves to another entry).</summary>
    public void ReleaseImages()
    {
        foreach (var bitmap in _thumbnails) bitmap.Dispose();
        _thumbnails.Clear();
    }

    /// <summary>
    /// A legacy image field (an asset id or a data URL) with a thumbnail of what it holds. Typing
    /// decodes again only when the image the text names changes, and the replaced bitmap is freed.
    /// </summary>
    private Grid WithThumbnail(TextBox box, string path, string label)
    {
        var thumbnail = Accessible(new Border
        {
            Name = $"ContentThumb_{path}",
            Width = 40,
            Height = 40,
            VerticalAlignment = VerticalAlignment.Center,
        }, $"{label} preview");
        string? shownUrl = null;
        void Update()
        {
            var url = ThumbnailUrl(box.Text);
            if (url == shownUrl) return;
            shownUrl = url;
            var previous = (thumbnail.Child as Image)?.Source as Bitmap;
            Show(thumbnail, Thumbnail(url));
            if (previous is not null && _thumbnails.Remove(previous)) previous.Dispose();
        }

        Update();
        box.TextChanged += (_, _) => Update();
        return Columns("*,Auto", box, thumbnail);
    }

    /// <summary>The data URL a legacy image field names: the text itself, or the URL of the asset with that id ("" for none).</summary>
    private string ThumbnailUrl(string? text)
    {
        var url = (text ?? "").Trim();
        if (url.Length == 0 || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return url;
        return _project.CustomAssets.FirstOrDefault(asset => asset.Id == url)?.DataUrl ?? "";
    }

    /// <summary>
    /// The image of a <c>data:image/…;base64,</c> URL decoded at thumbnail size (2× the 40px frame),
    /// or null when there is none, it does not decode, or it is over the art import limits.
    /// </summary>
    private Image? Thumbnail(string url)
    {
        if (url.Length == 0 || ArtBitmaps.Thumbnail(url, 80) is not { } bitmap) return null;
        _thumbnails.Add(bitmap);
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        return image;
    }

    // ---- Condition and outcome vocabulary ----

    private ComboBox TypePicker(string name, IReadOnlyList<PickerOption> types, string current, string placeholder)
    {
        var picker = Accessible(new ComboBox { Name = name, MinWidth = 200, PlaceholderText = placeholder }, placeholder.TrimStart('+', ' '));
        foreach (var option in types)
        {
            picker.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Id });
        }

        if (current.Length > 0 && !types.Any(option => option.Id == current))
            picker.Items.Insert(0, new ComboBoxItem { Content = $"(legacy: {current})", Tag = current });
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag! == current);
        return picker;
    }

    private void AddConditions(Panel panel, JsonObject target, string key, string path)
    {
        var array = target[key] as JsonArray;
        for (var i = 0; i < (array?.Count ?? 0); i++)
        {
            var index = i;
            var condition = array![i] as JsonObject ?? new JsonObject();
            var type = StringOf(condition["type"]) ?? "";
            var picker = TypePicker($"ContentField_{path}_{i}_Type", ContentForms.ConditionTypes, type, "Condition type");
            picker.SelectionChanged += (_, _) =>
            {
                if (SelectedId(picker) is { } next && next != type)
                    Structural(() => array[index] = RecordJson.ToNode(ContentForms.DefaultCondition(next, _project), typeof(EventCondition)));
            };
            var header = RowHeader(picker, RowButtons(path, i, array.Count, array, target, key, nullable: false, "condition"));
            panel.Children.Add(ListRow(header, VocabularyFields(condition, ContentForms.ConditionFields(type), $"{path}_{i}")));
        }

        var add = TypePicker($"ContentAdd_{path}", ContentForms.ConditionTypes, "", "+ Add condition");
        add.SelectionChanged += (_, _) =>
        {
            if (SelectedId(add) is { } type)
                Structural(() => EnsureArray(target, key).Add(RecordJson.ToNode(ContentForms.DefaultCondition(type, _project), typeof(EventCondition))));
        };
        panel.Children.Add(add);
    }

    private void AddOutcomes(Panel panel, JsonObject target, string key, string path)
    {
        var array = target[key] as JsonArray;
        for (var i = 0; i < (array?.Count ?? 0); i++)
        {
            var index = i;
            var outcome = array![i] as JsonObject ?? new JsonObject();
            var type = StringOf(outcome["type"]) ?? "";
            var picker = TypePicker($"ContentField_{path}_{i}_Type", ContentForms.OutcomeTypes, type, "Outcome type");
            picker.SelectionChanged += (_, _) =>
            {
                if (SelectedId(picker) is { } next && next != type)
                    Structural(() => array[index] = RecordJson.ToNode(ContentForms.DefaultOutcome(next)));
            };
            var title = Ui.HStack(8, Ui.Text($"{i + 1}.", "category"), picker);
            var header = RowHeader(title, RowButtons(path, i, array.Count, array, target, key, nullable: false, "outcome"));
            panel.Children.Add(ListRow(header, VocabularyFields(outcome, ContentForms.OutcomeFields(type), $"{path}_{i}")));
        }

        var add = TypePicker($"ContentAdd_{path}", ContentForms.OutcomeTypes, "", "+ Add outcome");
        add.SelectionChanged += (_, _) =>
        {
            if (SelectedId(add) is { } type)
                Structural(() => EnsureArray(target, key).Add(RecordJson.ToNode(ContentForms.DefaultOutcome(type))));
        };
        panel.Children.Add(add);
    }

    /// <summary>The fields F# lists for one condition or outcome type, laid out in a wrap.</summary>
    private Control VocabularyFields(JsonObject target, IReadOnlyList<FormField> fields, string path)
    {
        var wrap = new WrapPanel();
        var stack = new StackPanel { Spacing = 6 };
        var numbers = new Dictionary<string, TextBox>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var name = $"ContentField_{path}_{Pascal(field.Key)}";
            switch (field.Kind)
            {
                case "integer" or "number":
                    var number = NumberControl(target, field.Key, name, field.Label, field.Kind == "integer", field.Optional,
                        field.HasMin ? field.Min : null, field.HasMax ? field.Max : null);
                    numbers[field.Key] = number;
                    number.Watermark = field.Optional ? "optional" : null;
                    wrap.Children.Add(Labeled(field.Label, number, 130));
                    break;
                case "bool":
                    wrap.Children.Add(BoolControl(target, field.Key, name, field.OnLabel, whenAbsent: true));
                    break;
                case "choice" or "reference":
                    wrap.Children.Add(Labeled(field.Label, StringControl(target, field.Key, name, field.Label, field, field.Optional, multiline: false, rebuildOnChange: false), 240));
                    break;
                case "referenceList":
                    var chips = new StackPanel { Spacing = 4 };
                    chips.Children.Add(Label(field.Label));
                    AddReferenceChips(chips, target, field.Key, $"{path}_{Pascal(field.Key)}", field.Label, field.Reference, nullable: false);
                    stack.Children.Add(chips);
                    break;
                default:
                    var text = (TextBox)StringControl(target, field.Key, name, field.Label, null, field.Optional, multiline: false, rebuildOnChange: false);
                    text.Watermark = field.Placeholder;
                    text.MinWidth = 240;
                    wrap.Children.Add(Labeled(field.Label, text));
                    break;
            }
        }

        // Tile coordinates can be clicked on the map: the outcome's own scene, else the event's.
        string Scene() => StringOf(target["sceneId"]) is { Length: > 0 } own ? own : StringOf(Draft["sceneId"]) ?? "";
        foreach (var (xKey, yKey, what) in new[] { ("x", "y", "the tile"), ("tileX", "tileY", "the tile"), ("x2", "y2", "the far corner") })
        {
            if (numbers.TryGetValue(xKey, out var x) && numbers.TryGetValue(yKey, out var y)
                && PickButton($"ContentPick_{path}_{Pascal(xKey)}", what, Scene, x, y) is { } pick)
            {
                wrap.Children.Add(pick);
            }
        }

        foreach (var child in wrap.Children.OfType<Control>()) child.Margin = new Thickness(0, 0, 10, 6);
        if (wrap.Children.Count > 0) stack.Children.Insert(0, wrap);
        return stack;
    }

    /// <summary>A "Pick on map" button that fills <paramref name="x"/> and <paramref name="y"/> with a clicked tile (#47).</summary>
    private Button? PickButton(string name, string what, Func<string> scene, TextBox x, TextBox y)
    {
        if (_pickOnMap is not { } pickOnMap) return null;
        var button = SmallButton(name, "Pick on map", $"Pick {what} on the map", () => pickOnMap(scene(), $"Click {what}.", (tileX, tileY) =>
        {
            x.Text = tileX.ToString(CultureInfo.InvariantCulture);
            y.Text = tileY.ToString(CultureInfo.InvariantCulture);
            _report($"Tile ({tileX}, {tileY}) chosen. Save changes to keep it.");
        }));
        button.VerticalAlignment = VerticalAlignment.Bottom;
        return button;
    }

    // ---- Escape hatch ----

    private void AddJsonEscape(Panel panel, JsonObject target, string key, string path, string label, bool nullable)
    {
        var text = target[key]?.ToJsonString(InteropJson.Indented) ?? "null";
        var box = Accessible(new TextBox
        {
            Name = $"ContentJson_{path}",
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"),
        }, $"{label} as JSON");
        var apply = Accessible(SmallButton($"ContentJsonApply_{path}", "Apply JSON", "Replace this field with the JSON above", () => Structural(() => { })), $"Apply {label} JSON");
        _json.Add(new JsonEscape(label, box, text, nullable, node => target[key] = node));
        panel.Children.Add(new Expander
        {
            Name = $"ContentJsonExpander_{path}",
            Header = "Edit as JSON",
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = Ui.VStack(6, box, apply),
        });
    }
}
