using System.Text.Json;
using System.Text.Json.Serialization;
using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>
/// Serialization of collection definitions.
/// </summary>
/// <remarks>
/// These options govern both storage in the database and migration snapshots, so their stability
/// is a contract: changing name casing or enum rendering would make already-written snapshots
/// unreadable. Enums are therefore <b>strings</b>, never integers — an enum number shifts as soon
/// as a value is inserted in the middle.
/// </remarks>
public static class SchemaJson
{
    /// <summary>Shared options.</summary>
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new RecordIdJsonConverter());

        return options;
    }
}

/// <summary>Serializes a <see cref="RecordId"/> in its canonical form.</summary>
public sealed class RecordIdJsonConverter : JsonConverter<RecordId>
{
    /// <inheritdoc />
    public override RecordId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();

        return RecordId.TryParse(raw, out var id)
            ? id
            : throw new JsonException($"\"{raw}\" is not a valid identifier.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RecordId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToString());
    }
}
