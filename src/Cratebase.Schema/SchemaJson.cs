using System.Text.Json;
using System.Text.Json.Serialization;
using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>
/// Sérialisation des définitions de collections.
/// </summary>
/// <remarks>
/// Ces options gouvernent à la fois le stockage en base et les instantanés de migration, donc leur
/// stabilité est un contrat : changer la casse des noms ou le rendu des énumérations rendrait
/// illisibles les instantanés déjà écrits. Les énumérations sont donc en <b>chaînes</b>, jamais en
/// entiers — un numéro d'énumération se décale dès qu'on insère une valeur au milieu.
/// </remarks>
public static class SchemaJson
{
    /// <summary>Options partagées.</summary>
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

/// <summary>Sérialise un <see cref="RecordId"/> sous sa forme canonique.</summary>
public sealed class RecordIdJsonConverter : JsonConverter<RecordId>
{
    /// <inheritdoc />
    public override RecordId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();

        return RecordId.TryParse(raw, out var id)
            ? id
            : throw new JsonException($"« {raw} » n'est pas un identifiant valide.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, RecordId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.ToString());
    }
}
