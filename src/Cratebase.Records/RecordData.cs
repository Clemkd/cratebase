using System.Globalization;
using System.Text.Json;
using Cratebase.Core;
using Cratebase.Schema;

namespace Cratebase.Records;

/// <summary>
/// Valeurs d'un enregistrement, indexées par nom de champ.
/// </summary>
public sealed class RecordData(IDictionary<string, object?>? values = null)
{
    private readonly Dictionary<string, object?> _values =
        values is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(values, StringComparer.Ordinal);

    /// <summary>Accès direct à une valeur.</summary>
    public object? this[string field]
    {
        get => _values.GetValueOrDefault(field);
        set => _values[field] = value;
    }

    /// <summary>Noms des champs présents.</summary>
    public IReadOnlyCollection<string> Keys => _values.Keys;

    /// <summary>Le champ est-il présent ?</summary>
    public bool Contains(string field) => _values.ContainsKey(field);

    /// <summary>Retire un champ.</summary>
    public void Remove(string field) => _values.Remove(field);

    /// <summary>Vue en lecture seule, pour la sérialisation et pour <c>@request.body</c>.</summary>
    public IReadOnlyDictionary<string, object?> AsDictionary() => _values;

    /// <summary>Identifiant de l'enregistrement.</summary>
    public RecordId Id => RecordId.TryParse(GetString(SystemFields.Id), out var id) ? id : RecordId.Empty;

    /// <summary>Lit une valeur textuelle.</summary>
    public string? GetString(string field) => this[field] switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(this[field], CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Convertit une valeur issue d'un corps JSON en valeur du modèle logique.
    /// </summary>
    public static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => element.EnumerateArray()
            .Select(item => item.ValueKind is JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : item.GetRawText())
            .ToArray(),
        _ => element.GetRawText(),
    };
}
