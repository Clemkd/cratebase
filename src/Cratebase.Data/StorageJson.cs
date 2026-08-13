using System.Text.Json;
using System.Text.Json.Serialization;
using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Sérialisation des valeurs composites vers le stockage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Décision de portabilité :</b> les champs multi-valués sont des tableaux JSON dans les deux
/// moteurs — <c>TEXT</c> sur SQLite, <c>jsonb</c> sur PostgreSQL — et non des <c>text[]</c> natifs
/// côté PostgreSQL. Le tableau natif serait plus rapide, mais il donnerait deux représentations
/// différentes à la même donnée logique, donc deux sémantiques à faire coïncider sur le tri, la
/// casse et les valeurs vides. Une seule forme, un seul comportement à prouver.
/// </para>
/// <para>
/// L'ordre est stable et les valeurs ne sont jamais réordonnées : un champ multi-valué garde
/// l'ordre soumis, parce que c'est ce que l'utilisateur voit dans l'interface.
/// </para>
/// </remarks>
public static class StorageJson
{
    /// <summary>Options de sérialisation, identiques pour les deux dialectes.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>Sérialise une valeur multi-valuée en tableau JSON.</summary>
    public static string SerializeMultiple(object? value)
    {
        var items = value switch
        {
            null => [],
            string single => new[] { single },
            IEnumerable<string> many => [.. many],
            System.Collections.IEnumerable many => many.Cast<object?>()
                .Select(item => item?.ToString() ?? string.Empty)
                .ToArray(),
            _ => new[] { value.ToString() ?? string.Empty },
        };

        return JsonSerializer.Serialize(items, Options);
    }

    /// <summary>Relit un tableau JSON.</summary>
    public static IReadOnlyList<string> DeserializeMultiple(object? stored)
    {
        if (stored is not string text || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(text, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Sérialise un point géographique.</summary>
    public static string SerializeGeoPoint(GeoPoint point) =>
        JsonSerializer.Serialize(point, Options);

    /// <summary>Relit un point géographique.</summary>
    public static GeoPoint DeserializeGeoPoint(object? stored)
    {
        if (stored is not string text || string.IsNullOrWhiteSpace(text))
        {
            return new GeoPoint(0, 0);
        }

        try
        {
            return JsonSerializer.Deserialize<GeoPoint>(text, Options);
        }
        catch (JsonException)
        {
            return new GeoPoint(0, 0);
        }
    }
}
