using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmEngine.Json;

/// <summary>
/// A zod <c>z.number().nullable().optional()</c> field, where an absent key and an explicit
/// <c>null</c> are different values: <c>JSON.stringify</c> omits the one and writes the other,
/// so they hash differently. <c>default</c> is absent; pair the property with
/// <c>[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]</c> so absent stays
/// absent on the way out.
/// </summary>
/// <param name="IsPresent">The key was there (as a number or as <c>null</c>).</param>
/// <param name="Value">The number, or <c>null</c>.</param>
[JsonConverter(typeof(OptionalNullableNumberConverter))]
public readonly record struct OptionalNullableNumber(bool IsPresent, double? Value)
{
    /// <summary>A present field holding <paramref name="value"/> (which may be <c>null</c>).</summary>
    public static OptionalNullableNumber Of(double? value) => new(true, value);
}

/// <summary>JSON for <see cref="OptionalNullableNumber"/>. Only called when the key is present.</summary>
public sealed class OptionalNullableNumberConverter : JsonConverter<OptionalNullableNumber>
{
    public override bool HandleNull => true;

    public override OptionalNullableNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null
            ? OptionalNullableNumber.Of(null)
            : OptionalNullableNumber.Of(JsonSerializer.Deserialize<double>(ref reader, options));

    public override void Write(Utf8JsonWriter writer, OptionalNullableNumber value, JsonSerializerOptions options)
    {
        if (value.Value is { } number)
        {
            JsonSerializer.Serialize(writer, number, options);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
