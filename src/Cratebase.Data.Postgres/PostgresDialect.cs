using System.Data.Common;
using System.Globalization;
using Cratebase.Core;
using Cratebase.Data;
using Npgsql;

namespace Cratebase.Data.Postgres;

/// <summary>
/// Dialecte PostgreSQL. Moteur de montée en charge.
/// </summary>
/// <remarks>
/// Écrit en même temps que le dialecte SQLite, bien avant d'être utilisé en production. C'est
/// délibéré : un dialecte ajouté après coup découvre trop tard que des hypothèses du premier moteur
/// ont essaimé partout. Le §10 du document de conception l'assume comme le prix de la promesse
/// d'évolutivité.
/// </remarks>
public sealed partial class PostgresDialect : ISqlDialect, ISchemaDdl
{
    /// <summary>Instance partagée — le dialecte est sans état.</summary>
    public static readonly PostgresDialect Instance = new();

    /// <inheritdoc />
    public string BindParameter(FieldType type, bool multiple, string placeholder) =>
        IsJsonColumn(type, multiple) ? placeholder + "::jsonb" : placeholder;

    private static bool IsJsonColumn(FieldType type, bool multiple) =>
        multiple || type is FieldType.Json or FieldType.GeoPoint;

    /// <inheritdoc />
    public string Name => "postgres";

    /// <inheritdoc />
    public IReadOnlyList<string> ConnectionInitializationStatements { get; } = [];

    /// <inheritdoc />
    public string QuoteIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <inheritdoc />
    public string ColumnType(FieldType type, bool multiple)
    {
        // Les multi-valeurs restent du JSON, et non des tableaux natifs : voir StorageJson.
        if (multiple)
        {
            return "jsonb";
        }

        return type switch
        {
            FieldType.Bool => "boolean",
            FieldType.Number => "double precision",
            FieldType.Date or FieldType.AutoDate => "timestamptz",
            FieldType.Json or FieldType.GeoPoint => "jsonb",

            // COLLATE "C" aligne le tri sur l'ordre binaire de SQLite. Sans lui, « ORDER BY title »
            // ne rend pas la même page selon le moteur, ce qui casse la pagination à la migration.
            _ => "text COLLATE \"C\"",
        };
    }

    /// <inheritdoc />
    public object? ToStorage(FieldType type, bool multiple, object? value)
    {
        // La valeur reste une chaîne ; c'est BindParameter qui la fait accepter par une colonne
        // jsonb, en transtypant dans le SQL.
        if (multiple)
        {
            return StorageJson.SerializeMultiple(value);
        }

        return type switch
        {
            FieldType.Bool => AsBoolean(value),
            FieldType.Number => AsNumber(value),
            FieldType.Date or FieldType.AutoDate => AsInstant(value),
            FieldType.GeoPoint when value is GeoPoint point => StorageJson.SerializeGeoPoint(point),
            FieldType.Json => value is null ? null : AsText(value),
            _ => value is null ? null : AsText(value),
        };
    }

    /// <inheritdoc />
    public object? FromStorage(FieldType type, bool multiple, object? value)
    {
        if (multiple)
        {
            return StorageJson.DeserializeMultiple(value);
        }

        return type switch
        {
            FieldType.Bool => value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            FieldType.Number => value is null ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture),
            FieldType.Date or FieldType.AutoDate => value switch
            {
                DateTime instant => Timestamp.Normalize(instant),
                DateTimeOffset instant => Timestamp.Normalize(instant),
                _ => value,
            },
            FieldType.GeoPoint => StorageJson.DeserializeGeoPoint(value),
            _ => value,
        };
    }

    /// <inheritdoc />
    public string LikeExpression(string valueExpression, string patternPlaceholder, bool negated)
    {
        // ILIKE, et non LIKE : c'est ce qui aligne PostgreSQL sur l'insensibilité à la casse de
        // SQLite. Avec LIKE, la recherche marche en développement puis cesse en production.
        var comparison = $"{valueExpression} ILIKE {patternPlaceholder} ESCAPE '{LikePattern.EscapeCharacter}'";

        return negated ? $"NOT ({comparison})" : comparison;
    }

    /// <inheritdoc />
    public string Lower(string expression) => $"lower({expression})";

    /// <inheritdoc />
    public string ArrayLength(string expression) =>
        $"coalesce(jsonb_array_length({expression}), 0)";

    /// <inheritdoc />
    public string AnyElementMatches(string expression, string comparison) =>
        $"EXISTS (SELECT 1 FROM jsonb_array_elements_text({expression}) AS cb_e(value) WHERE {Substitute(comparison)})";

    /// <inheritdoc />
    public string AllElementsMatch(string expression, string comparison) =>
        $"NOT EXISTS (SELECT 1 FROM jsonb_array_elements_text({expression}) AS cb_e(value) WHERE NOT ({Substitute(comparison)}))";

    /// <inheritdoc />
    public string LimitOffset(int limit, int offset) => $"LIMIT {limit} OFFSET {offset}";

    /// <inheritdoc />
    public string OrderByNullsLast(string expression, bool descending) =>
        $"{expression} {(descending ? "DESC" : "ASC")} NULLS LAST";

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public CratebaseException? TranslateException(DbException exception)
    {
        if (exception is not PostgresException postgres)
        {
            return null;
        }

        return postgres.SqlState switch
        {
            UniqueViolation or ForeignKeyViolation or CheckViolation or NotNullViolation =>
                new CratebaseConflictException(
                    "L'opération viole une contrainte d'unicité ou d'intégrité.", postgres),
            _ => null,
        };
    }

    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";
    private const string CheckViolation = "23514";
    private const string NotNullViolation = "23502";

    private static string Substitute(string comparison) =>
        comparison.Replace(FilterCompiler.ElementPlaceholder, "cb_e.value", StringComparison.Ordinal);

    private static bool AsBoolean(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        double number => number != 0,
        long number => number != 0,
        string text => text is not ("" or "0" or "false"),
        _ => true,
    };

    private static double AsNumber(object? value) => value switch
    {
        null => 0d,
        double number => number,
        string text when double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    // La colonne est un timestamptz : le paramètre doit être un instant, pas une chaîne. On passe
    // malgré tout par la forme canonique, pour que la troncature de précision soit la même que
    // celle appliquée côté SQLite.
    private static object? AsInstant(object? value) => value switch
    {
        null => null,
        DateTimeOffset instant => Timestamp.Parse(Timestamp.Normalize(instant)),
        DateTime instant => Timestamp.Parse(Timestamp.Normalize(instant)),
        string text => Timestamp.TryNormalize(text, out var canonical) ? Timestamp.Parse(canonical) : null,
        _ => value,
    };

    private static string AsText(object value) => value switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        RecordId id => id.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
