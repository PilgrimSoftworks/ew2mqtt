using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pilgrim.EasyWorship.Wire;

internal sealed class FlexibleInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String => int.Parse(reader.GetString() ?? "0", CultureInfo.InvariantCulture),
            _ => throw new JsonException($"expected number or string, got {reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

internal sealed class FlexibleNullableInt32Converter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.String => string.IsNullOrEmpty(reader.GetString()) ? null : int.Parse(reader.GetString()!, CultureInfo.InvariantCulture),
            _ => throw new JsonException($"expected number, string, or null, got {reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

internal sealed class FlexibleInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt64(),
            JsonTokenType.String => long.Parse(reader.GetString() ?? "0", CultureInfo.InvariantCulture),
            _ => throw new JsonException($"expected number or string, got {reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

internal sealed class FlexibleNullableInt64Converter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetInt64(),
            JsonTokenType.String => string.IsNullOrEmpty(reader.GetString()) ? null : long.Parse(reader.GetString()!, CultureInfo.InvariantCulture),
            _ => throw new JsonException($"expected number, string, or null, got {reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

internal sealed class IntBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.GetInt32() != 0,
            JsonTokenType.String => reader.GetString() switch
            {
                "1" or "true" or "True" or "ON" or "on" => true,
                _ => false,
            },
            _ => throw new JsonException($"expected bool/number/string, got {reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value ? 1 : 0);
}
