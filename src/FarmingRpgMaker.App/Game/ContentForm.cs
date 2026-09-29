using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Interop;
using FarmEngine.Schemas;
using Microsoft.FSharp.Core;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Reflection;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// A generated form over a JSON draft of one content entry. Property types pick the controls:
/// nested records become grouped sub-forms, lists get add/remove/move rows, string lists become
/// chips or rows. F# <see cref="ContentForms"/> says what the strings mean (references, choices),
/// which condition and outcome fields each type shows, and what new rows start as. Every nested
/// field also has a collapsed "Edit as JSON" box. Controls write into the draft; the owner turns
/// it back into the record with <see cref="Commit"/> and applies one F# upsert.
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

    public ContentForm(GameProject project, object entity, Type entityType, StackPanel root, Action<string> report)
    {
        _project = project;
        _entity = entity;
        _entityType = entityType;
        _root = root;
        _report = report;
        Draft = RecordJson.ToNode(entity, entityType) as JsonObject
            ?? throw new InvalidOperationException("Content did not serialize to an object.");
        Rebuild();
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
        _writers.Clear();
        _json.Clear();
        _options.Clear();
        AddProperties(_root, Draft, _entityType, "");
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

    private void AddProperties(Panel panel, JsonObject target, Type type, string path)
    {
        var hidden = type.GetProperty("Type")?.PropertyType == typeof(string)
            ? ContentForms.HiddenProperties(type.Name, StringOf(target["type"]) ?? "")
            : [];
        foreach (var property in FormProperties(type))
        {
            if (hidden.Contains(property.Name)) continue;
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
            panel.Children.Add(new TextBox { Name = name, Text = StringOf(target[key]) ?? "", IsReadOnly = true });
            return;
        }

        if (type == typeof(string))
        {
            panel.Children.Add(Label(label));
            panel.Children.Add(StringControl(target, key, name, field, nullable,
                multiline: property.Name is "Description" or "Text" or "Message" or "FailMessage",
                rebuildOnChange: property.Name == "Type"));
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
                optional: nullable || IsOptionalNullableNumber(type), whenEmpty: 0, min: null, max: null));
            return;
        }

        var body = new StackPanel { Spacing = 8 };
        if (IsJsonDictionary(type)) AddKeyValues(body, target, key, path);
        else if (ListElement(type) is { } element)
        {
            if (element == typeof(string))
            {
                if (field is { Kind: "referenceList" }) AddReferenceChips(body, target, key, path, field.Reference, nullable);
                else AddTextRows(body, target, key, path, nullable);
            }
            else if (IsNumber(element)) AddNumberRows(body, target, key, path, label, nullable);
            else if (element == typeof(EventCondition)) AddConditions(body, target, key, path);
            else if (element == typeof(EventOutcome)) AddOutcomes(body, target, key, path);
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

    private static StackPanel Labeled(string label, Control control, double width = 0)
    {
        var stack = Ui.VStack(3, Label(label), control);
        if (width > 0) stack.Width = width;
        return stack;
    }

    // ---- Scalars ----

    private Control StringControl(JsonObject target, string key, string name, FormField? field, bool nullable, bool multiline, bool rebuildOnChange)
    {
        var current = StringOf(target[key]);
        if (field is { Kind: "reference" or "choice" })
        {
            var picker = Picker(name, field, current);
            var initial = SelectedId(picker);
            _writers.Add(() =>
            {
                var id = SelectedId(picker);
                if (id is null || id == initial) return;
                target[key] = id.Length == 0 && nullable ? null : JsonValue.Create(id);
            });
            if (rebuildOnChange) picker.SelectionChanged += (_, _) => { if (SelectedId(picker) is { } id && id != initial) Structural(() => { }); };
            return picker;
        }

        var box = new TextBox
        {
            Name = name,
            Text = current ?? "",
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 54 : 30,
        };
        if (field is { Kind: "plain" } && field.Reason.Length > 0) ToolTip.SetTip(box, Ui.Capitalize(field.Reason) + ".");
        var original = box.Text;
        _writers.Add(() =>
        {
            var text = box.Text ?? "";
            if (text == original) return;
            target[key] = nullable && text.Length == 0 ? null : JsonValue.Create(text);
        });
        return box;
    }

    private CheckBox BoolControl(JsonObject target, string key, string name, string label, bool? whenAbsent)
    {
        var value = target[key] is JsonValue node && node.TryGetValue<bool>(out var flag) ? flag : whenAbsent;
        var check = new CheckBox { Name = name, Content = label, IsChecked = value, IsThreeState = whenAbsent is null };
        var original = check.IsChecked;
        _writers.Add(() =>
        {
            if (check.IsChecked == original) return;
            target[key] = check.IsChecked is { } on ? JsonValue.Create(on) : null;
        });
        return check;
    }

    private TextBox NumberControl(JsonObject target, string key, string name, string label, bool integer, bool optional, double whenEmpty, double? min, double? max)
    {
        var box = new TextBox { Name = name, Text = NumberText(target[key]), MinWidth = 90 };
        var original = box.Text;
        _writers.Add(() =>
        {
            var raw = (box.Text ?? "").Trim();
            if (raw == original) return;
            if (raw.Length == 0)
            {
                target[key] = optional ? null : JsonValue.Create(whenEmpty);
                return;
            }

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)
                || (integer && value != Math.Floor(value)))
            {
                _errors.Add($"{label} must be {(integer ? "a whole number" : "a number")}.");
                return;
            }

            if (min is { } low) value = Math.Max(low, value);
            if (max is { } high) value = Math.Min(high, value);
            target[key] = JsonValue.Create(value);
        });
        return box;
    }

    private ComboBox Picker(string name, FormField field, string? current)
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
            var item = new ComboBoxItem { Content = option.Label, Tag = option.Id };
            TextSearch.SetText(item, option.Label);
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
        var button = Ui.Button(text, action, "tool", "small");
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

        panel.Children.Add(SmallButton($"ContentAdd_{path}", $"Add {what}", $"Add a {what}", () => Structural(() =>
        {
            var list = EnsureArray(target, key);
            list.Add(NewElementNode(element, list));
        })));
    }

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

        if (node is not null) AddProperties(panel, node, type, path);
    }

    private void AddTextRows(Panel panel, JsonObject target, string key, string path, bool nullable)
    {
        var array = target[key] as JsonArray;
        for (var i = 0; i < (array?.Count ?? 0); i++)
        {
            var index = i;
            var box = new TextBox { Name = $"ContentField_{path}_{i}", Text = StringOf(array![i]) ?? array[i]?.ToJsonString() ?? "", MinWidth = 180 };
            var original = box.Text;
            _writers.Add(() =>
            {
                if (box.Text != original) array[index] = JsonValue.Create(box.Text ?? "");
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
            var box = NumberControl(holder, "value", $"ContentField_{path}_{i}", $"{label} {i + 1}", integer: false, optional: false, whenEmpty: 0, min: null, max: null);
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
    private void AddReferenceChips(Panel panel, JsonObject target, string key, string path, string kind, bool nullable)
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
            var remove = SmallButton($"ContentChipRemove_{path}_{i}", "×", "Remove", () => Structural(() =>
            {
                array!.RemoveAt(index);
                if (array.Count == 0 && nullable) target[key] = null;
            }));
            chips.Children.Add(new Border { Name = $"ContentChip_{path}_{i}", Child = Ui.HStack(4, text, remove), Margin = new Thickness(0, 0, 6, 6) }.WithClasses("chip"));
        }

        if (entries.Count == 0) chips.Children.Add(Ui.Text(nullable ? "None (no restriction)" : "None", "muted", "small"));
        panel.Children.Add(chips);
        var add = new ComboBox { Name = $"ContentChipAdd_{path}", PlaceholderText = "Add…", MinWidth = 200 };
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
            var keyBox = new TextBox { Name = $"ContentKey_{path}_{i}", Text = entries[i].Key, Width = 140 };
            var value = entries[i].Value;
            var valueBox = new TextBox { Name = $"ContentValue_{path}_{i}", Text = StringOf(value) ?? value?.ToJsonString() ?? "null", MinWidth = 160 };
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

    // ---- Condition and outcome vocabulary ----

    private ComboBox TypePicker(string name, IReadOnlyList<PickerOption> types, string current, string placeholder)
    {
        var picker = new ComboBox { Name = name, MinWidth = 200, PlaceholderText = placeholder };
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
        foreach (var field in fields)
        {
            var name = $"ContentField_{path}_{Pascal(field.Key)}";
            switch (field.Kind)
            {
                case "integer" or "number":
                    var number = NumberControl(target, field.Key, name, field.Label, field.Kind == "integer", field.Optional, field.WhenEmpty,
                        field.HasMin ? field.Min : null, field.HasMax ? field.Max : null);
                    number.Watermark = field.Optional ? "optional" : null;
                    wrap.Children.Add(Labeled(field.Label, number, 130));
                    break;
                case "bool":
                    wrap.Children.Add(BoolControl(target, field.Key, name, field.OnLabel, whenAbsent: true));
                    break;
                case "choice" or "reference":
                    wrap.Children.Add(Labeled(field.Label, StringControl(target, field.Key, name, field, field.Optional, multiline: false, rebuildOnChange: false), 240));
                    break;
                case "referenceList":
                    var chips = new StackPanel { Spacing = 4 };
                    chips.Children.Add(Label(field.Label));
                    AddReferenceChips(chips, target, field.Key, $"{path}_{Pascal(field.Key)}", field.Reference, nullable: false);
                    stack.Children.Add(chips);
                    break;
                default:
                    var text = (TextBox)StringControl(target, field.Key, name, null, field.Optional, multiline: false, rebuildOnChange: false);
                    text.Watermark = field.Placeholder;
                    text.MinWidth = 240;
                    wrap.Children.Add(Labeled(field.Label, text));
                    break;
            }
        }

        foreach (var child in wrap.Children.OfType<Control>()) child.Margin = new Thickness(0, 0, 10, 6);
        if (wrap.Children.Count > 0) stack.Children.Insert(0, wrap);
        return stack;
    }

    // ---- Escape hatch ----

    private void AddJsonEscape(Panel panel, JsonObject target, string key, string path, string label, bool nullable)
    {
        var text = target[key]?.ToJsonString(InteropJson.Indented) ?? "null";
        var box = new TextBox
        {
            Name = $"ContentJson_{path}",
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"),
        };
        var apply = SmallButton($"ContentJsonApply_{path}", "Apply JSON", "Replace this field with the JSON above", () => Structural(() => { }));
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
