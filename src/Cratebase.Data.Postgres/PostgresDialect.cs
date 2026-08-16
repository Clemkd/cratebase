using System.Data.Common;
using System.Globalization;
using Cratebase.Core;
using Cratebase.Data;
using Npgsql;

namespace Cratebase.Data.Postgres;

/// <summary>
/// PostgreSQL dialect. The scale-up engine.
/// </summary>
/// <remarks>
/// Written at the same time as the SQLite dialect, well before being used in production. This is
/// deliberate: a dialect added as an afterthought discovers too late that assumptions from the
/// first engine have spread everywhere. The design document's §10 accepts this as the price of the
/// portability promise.
/// </remarks>
public sealed partial class PostgresDialect : ISqlDialect, ISchemaDdl
{
    /// <summary>Shared instance — the dialect is stateless.</summary>
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
    /// <remarks>
    /// Size of the whole database, indexes included. It says nothing about remaining disk space:
    /// PostgreSQL exposes no volume capacity in a portable way, and a managed instance often
    /// exposes none at all. Declaring one is up to the operator.
    /// </remarks>
    public string DatabaseSizeQuery => "SELECT pg_database_size(current_database())";

    /// <inheritdoc />
    public string QuoteIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <inheritdoc />
    public string ColumnType(FieldType type, bool multiple)
    {
        // Multi-values stay JSON, not native arrays: see StorageJson.
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

            // COLLATE "C" aligns sort order with SQLite's binary order. Without it, "ORDER BY
            // title" doesn't return the same page depending on the engine, breaking pagination
            // across a migration.
            _ => "text COLLATE \"C\"",
        };
    }

    /// <inheritdoc />
    public object? ToStorage(FieldType type, bool multiple, object? value)
    {
        // The value stays a string; BindParameter is what makes a jsonb column accept it, by
        // casting inside the SQL.
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
        // ILIKE, not LIKE: this is what aligns PostgreSQL with SQLite's case insensitivity. With
        // LIKE, search works in development then stops working in production.
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
                    "The operation violates a uniqueness or integrity constraint.", postgres),
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

    // The column is a timestamptz: the parameter must be an instant, not a string. The canonical
    // form is still used as a pass-through, so precision truncation matches what SQLite applies.
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
