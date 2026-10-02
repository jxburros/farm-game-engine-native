using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.FSharp.Reflection;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// What <see cref="ContentForm"/> reads off the F# schema records and the JSON draft: which
/// fields a record shows and how they are typed, field labels and control-name paths, and the
/// small edits rows make to the draft. No controls, so it is testable on its own.
/// </summary>
internal static class ContentFormSchema
{
    /// <summary>A record's fields in declaration order, without the undeclared-keys bag.</summary>
    internal static IEnumerable<PropertyInfo> FormProperties(Type type) =>
        FSharpType.GetRecordFields(type, null).Where(property => property.Name != "Extra");

    internal static string JsonKey(Type owner, PropertyInfo property) => RecordJson.JsonKey(owner, property.Name);

    internal static bool IsOption(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FSharpOption<>);

    /// <summary><c>T option</c> → <c>T</c>; <c>float option option</c> (absent, null or a number) stays as it is.</summary>
    internal static Type Unwrap(Type type) => IsOption(type) && !IsOption(type.GetGenericArguments()[0]) ? type.GetGenericArguments()[0] : type;

    internal static bool IsNullable(PropertyInfo property) => IsOption(property.PropertyType);

    /// <summary><c>float option option</c>: absent, null or a number.</summary>
    internal static bool IsOptionalNullableNumber(Type type) => IsOption(type) && IsOption(type.GetGenericArguments()[0]) && IsNumber(type.GetGenericArguments()[0].GetGenericArguments()[0]);

    internal static bool IsNumber(Type type) => type == typeof(double) || type == typeof(int) || type == typeof(float) || type == typeof(long);

    internal static Type? ListElement(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FSharpList<>) ? type.GetGenericArguments()[0] : null;

    /// <summary>An ordered map of JSON values: <c>(string * Json) list</c>.</summary>
    internal static bool IsJsonDictionary(Type type) => type == typeof(FSharpList<Tuple<string, Json>>);

    internal static bool IsRecord(Type type) =>
        type.Namespace == typeof(GameProject).Namespace && FSharpType.IsRecord(type, null);

    /// <summary>A record at its schema defaults (<c>Record.Default</c>).</summary>
    internal static object DefaultOf(Type type) =>
        type.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
        ?? throw new InvalidOperationException($"Cannot create a {type.Name}.");

    internal static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static string NumberText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number.ToString("R", CultureInfo.InvariantCulture) : "";

    internal static string Title(string name)
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
    internal static string TrimId(string label) =>
        label.EndsWith(" Ids", StringComparison.Ordinal) ? label[..^4] + "s"
        : label.EndsWith(" Id", StringComparison.Ordinal) ? label[..^3]
        : label;

    internal static string Pascal(string key) => key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..];

    internal static string Join(string path, string part) => path.Length == 0 ? part : $"{path}_{part}";

    /// <summary>A record's fields as the form shows them: F# hides some while its <c>type</c> says so (quest objective targets).</summary>
    internal static IEnumerable<PropertyInfo> VisibleProperties(JsonObject target, Type type)
    {
        var hidden = type.GetProperty("Type")?.PropertyType == typeof(string)
            ? ContentForms.HiddenProperties(type.Name, StringOf(target["type"]) ?? "")
            : [];
        return FormProperties(type).Where(property => !hidden.Contains(property.Name));
    }

    /// <summary>"Schedule entry 1 minute must be from 0 to 1560." (or "at least" / "at most").</summary>
    internal static string RangeText(string label, double? min, double? max) => (min, max) switch
    {
        ({ } low, { } high) => $"{label} must be from {Ui.Num(low)} to {Ui.Num(high)}.",
        ({ } low, null) => $"{label} must be at least {Ui.Num(low)}.",
        (null, { } high) => $"{label} must be at most {Ui.Num(high)}.",
        _ => $"{label} is out of range.",
    };

    internal static void Move(JsonArray array, int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= array.Count) return;
        var node = array[index];
        array.RemoveAt(index);
        array.Insert(target, node);
    }

    /// <summary>A setting value: JSON when it parses (numbers, booleans, quoted text), else text.</summary>
    internal static JsonNode? Literal(string text)
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

    internal static string Key<T>(string property) => RecordJson.JsonKey(typeof(T), property);

    /// <summary>A typed minute of day as a clock time ("8:00 AM"), clamped like the saved value; empty while it is not a number.</summary>
    internal static string ClockText(string? text) =>
        double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minute) && double.IsFinite(minute)
            ? ContentReadouts.Clock(Math.Clamp(minute, 0, 1560))
            : "";
}
