using System.Text.Json;
using System.Text.Json.Serialization;
using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Serialization of composite values for storage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Portability decision:</b> multi-valued fields are JSON arrays on both engines — <c>TEXT</c>
/// on SQLite, <c>jsonb</c> on PostgreSQL — rather than native <c>text[]</c> on the PostgreSQL side.
/// A native array would be faster, but it would give two different representations to the same
/// logical data, hence two semantics to reconcile on sorting, case, and empty values. One form
/// only, one behavior to prove.
/// </para>
/// <para>
/// Order is stable and values are never reordered: a multi-valued field keeps the order it was
/// submitted in, because that's what the user sees in the interface.
/// </para>
/// </remarks>
public static class StorageJson
{
    /// <summary>Serialization options, identical for both dialects.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>Serializes a multi-valued value to a JSON array.</summary>
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

    /// <summary>Reads back a JSON array.</summary>
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

    /// <summary>Serializes a geographic point.</summary>
    public static string SerializeGeoPoint(GeoPoint point) =>
        JsonSerializer.Serialize(point, Options);

    /// <summary>Reads back a geographic point.</summary>
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
