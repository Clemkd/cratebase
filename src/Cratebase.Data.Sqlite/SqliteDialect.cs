using System.Data.Common;
using System.Globalization;
using Cratebase.Core;
using Cratebase.Data;
using Microsoft.Data.Sqlite;

namespace Cratebase.Data.Sqlite;

/// <summary>
/// SQLite dialect. Starting engine: a single file, no service to deploy.
/// </summary>
public sealed partial class SqliteDialect : ISqlDialect, ISchemaDdl
{
    /// <summary>Shared instance — the dialect is stateless.</summary>
    public static readonly SqliteDialect Instance = new();

    /// <inheritdoc />
    public string Name => "sqlite";

    /// <inheritdoc />
    public IReadOnlyList<string> ConnectionInitializationStatements { get; } =
    [
        // WAL: one writer and several concurrent readers. Without it, any read blocks the write in
        // progress, and the application collapses under a handful of parallel requests.
        "PRAGMA journal_mode = WAL;",

        // Foreign keys are disabled by default on SQLite. Without this pragma, the cascade declared
        // by relation fields is never applied — and nothing reports it.
        "PRAGMA foreign_keys = ON;",

        // SQLite only admits one writer: without a busy timeout, the second concurrent write fails
        // immediately with SQLITE_BUSY instead of waiting its turn.
        "PRAGMA busy_timeout = 5000;",

        "PRAGMA synchronous = NORMAL;",
    ];

    /// <inheritdoc />
    /// <remarks>
    /// The product of the pages rather than the file size: that's what the database actually
    /// occupies, without the write-ahead log or pages freed but not returned to the OS. Reading the
    /// file from the server would give a bigger, and above all non-portable, number.
    /// </remarks>
    public string DatabaseSizeQuery =>
        "SELECT (SELECT * FROM pragma_page_count()) * (SELECT * FROM pragma_page_size())";

    /// <inheritdoc />
    public string QuoteIdentifier(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <inheritdoc />
    public string ColumnType(FieldType type, bool multiple)
    {
        if (multiple)
        {
            return "TEXT";
        }

        return type switch
        {
            FieldType.Bool => "INTEGER",
            FieldType.Number => "REAL",
            _ => "TEXT",
        };
    }

    /// <inheritdoc />
    public object? ToStorage(FieldType type, bool multiple, object? value)
    {
        if (multiple)
        {
            return StorageJson.SerializeMultiple(value);
        }

        return type switch
        {
            // Always 0/1, never "true"/"false": SQLite has no boolean type, and a column holding
            // both forms silently makes "WHERE active = 1" false.
            FieldType.Bool => AsBoolean(value) ? 1L : 0L,

            FieldType.Number => AsNumber(value),

            FieldType.Date or FieldType.AutoDate => AsTimestamp(value),

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
            FieldType.Bool => value is not null && Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
            FieldType.Number => value is null ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture),
            FieldType.GeoPoint => StorageJson.DeserializeGeoPoint(value),
            _ => value,
        };
    }

    /// <inheritdoc />
    public string LikeExpression(string valueExpression, string patternPlaceholder, bool negated)
    {
        // LIKE is ASCII case-insensitive on SQLite, which is the semantics chosen for "~".
        // PostgreSQL must therefore use ILIKE to match.
        var comparison = $"{valueExpression} LIKE {patternPlaceholder} ESCAPE '{LikePattern.EscapeCharacter}'";

        return negated ? $"NOT ({comparison})" : comparison;
    }

    /// <inheritdoc />
    // SQLite types its columns loosely: no adaptation is needed.
    public string BindParameter(FieldType type, bool multiple, string placeholder) => placeholder;

    /// <inheritdoc />
    public string Lower(string expression) => $"lower({expression})";

    /// <inheritdoc />
    public string ArrayLength(string expression) =>
        $"coalesce(json_array_length({expression}), 0)";

    /// <inheritdoc />
    public string AnyElementMatches(string expression, string comparison) =>
        $"EXISTS (SELECT 1 FROM json_each({expression}) AS cb_e WHERE {Substitute(comparison)})";

    /// <inheritdoc />
    public string AllElementsMatch(string expression, string comparison) =>
        $"NOT EXISTS (SELECT 1 FROM json_each({expression}) AS cb_e WHERE NOT ({Substitute(comparison)}))";

    /// <inheritdoc />
    public string LimitOffset(int limit, int offset) => $"LIMIT {limit} OFFSET {offset}";

    /// <inheritdoc />
    public string OrderByNullsLast(string expression, bool descending) =>
        $"{expression} {(descending ? "DESC" : "ASC")} NULLS LAST";

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);

    /// <inheritdoc />
    public CratebaseException? TranslateException(DbException exception)
    {
        if (exception is not SqliteException sqlite)
        {
            return null;
        }

        return sqlite.SqliteErrorCode switch
        {
            SqliteConstraint => new CratebaseConflictException(
                "The operation violates a uniqueness or integrity constraint.", sqlite),
            _ => null,
        };
    }

    private const int SqliteConstraint = 19;

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

    private static object? AsTimestamp(object? value) => value switch
    {
        null => null,
        DateTimeOffset instant => Timestamp.Normalize(instant),
        DateTime instant => Timestamp.Normalize(instant),
        string text => Timestamp.TryNormalize(text, out var canonical) ? canonical : text,
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
