using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InsightFlow.Domain.Viz;

/// <summary>Kind of a <see cref="ScalarValue"/>.</summary>
public enum ScalarKind
{
    Null,
    String,
    Number,
    Boolean,
}

/// <summary>
/// A filter literal as it appears in JSON (string, number, boolean or null). Values are always bound as
/// SQL parameters, never concatenated; the compiler converts them to the column's type (dates are ISO-8601
/// strings). A dedicated type keeps <see cref="VizSpec"/> value-comparable and source-generator friendly,
/// unlike <c>object</c> or <c>JsonElement</c>.
/// </summary>
[JsonConverter(typeof(ScalarValueJsonConverter))]
public readonly record struct ScalarValue
{
    private ScalarValue(ScalarKind kind, string? text, decimal number, bool boolean)
    {
        Kind = kind;
        Text = text;
        Number = number;
        Boolean = boolean;
    }

    public ScalarKind Kind { get; }

    /// <summary>The value when <see cref="Kind"/> is <see cref="ScalarKind.String"/>.</summary>
    public string? Text { get; }

    /// <summary>The value when <see cref="Kind"/> is <see cref="ScalarKind.Number"/>.</summary>
    public decimal Number { get; }

    /// <summary>The value when <see cref="Kind"/> is <see cref="ScalarKind.Boolean"/>.</summary>
    public bool Boolean { get; }

    public static ScalarValue Null { get; } = new(ScalarKind.Null, null, 0, false);

    public static ScalarValue Of(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(ScalarKind.String, value, 0, false);
    }

    public static ScalarValue Of(decimal value) => new(ScalarKind.Number, null, value, false);

    public static ScalarValue Of(bool value) => new(ScalarKind.Boolean, null, 0, value);

    public static implicit operator ScalarValue(string value) => Of(value);

    public static implicit operator ScalarValue(decimal value) => Of(value);

    public static implicit operator ScalarValue(int value) => Of(value);

    public static implicit operator ScalarValue(bool value) => Of(value);

    public static ScalarValue FromString(string value) => Of(value);

    public static ScalarValue FromDecimal(decimal value) => Of(value);

    public static ScalarValue FromInt32(int value) => Of(value);

    public static ScalarValue FromBoolean(bool value) => Of(value);

    public override string ToString() => Kind switch
    {
        ScalarKind.Null => "null",
        ScalarKind.String => Text!,
        ScalarKind.Number => Number.ToString(CultureInfo.InvariantCulture),
        ScalarKind.Boolean => Boolean ? "true" : "false",
        _ => string.Empty,
    };
}

/// <summary>Reads/writes <see cref="ScalarValue"/> as a native JSON primitive.</summary>
public sealed class ScalarValueJsonConverter : JsonConverter<ScalarValue>
{
    public override bool HandleNull => true;

    public override ScalarValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => ScalarValue.Null,
            JsonTokenType.String => ScalarValue.Of(reader.GetString()!),
            JsonTokenType.Number => ScalarValue.Of(reader.GetDecimal()),
            JsonTokenType.True => ScalarValue.Of(true),
            JsonTokenType.False => ScalarValue.Of(false),
            _ => throw new JsonException($"Filter values must be a string, number, boolean or null; got {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, ScalarValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (value.Kind)
        {
            case ScalarKind.String:
                writer.WriteStringValue(value.Text);
                break;
            case ScalarKind.Number:
                writer.WriteNumberValue(value.Number);
                break;
            case ScalarKind.Boolean:
                writer.WriteBooleanValue(value.Boolean);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
