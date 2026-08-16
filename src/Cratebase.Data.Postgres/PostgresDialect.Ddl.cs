using System.Globalization;
using System.Text;
using Cratebase.Core;
using Cratebase.Data;

namespace Cratebase.Data.Postgres;

public sealed partial class PostgresDialect
{
    /// <inheritdoc />
    // PostgreSQL alters the type in place and defers constraints: nothing to turn off.
    public IReadOnlyList<string> BeforeSchemaChange { get; } = [];

    /// <inheritdoc />
    public IReadOnlyList<string> AfterSchemaChange { get; } = [];

    /// <inheritdoc />
    public string? IntegrityCheckStatement => null;

    /// <inheritdoc />
    public IReadOnlyList<string> CreateTable(TableSpec table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var builder = new StringBuilder();
        builder.Append("CREATE TABLE ").Append(QuoteIdentifier(table.Name)).AppendLine(" (");

        var lines = new List<string>();

        foreach (var column in table.Columns)
        {
            var line = new StringBuilder("  ")
                .Append(QuoteIdentifier(column.Name))
                .Append(' ')
                .Append(ColumnType(column.Type, column.Multiple));

            if (string.Equals(column.Name, table.PrimaryKey, StringComparison.Ordinal))
            {
                line.Append(" NOT NULL PRIMARY KEY");
            }
            else
            {
                if (column.NotNull)
                {
                    line.Append(" NOT NULL");
                }

                if (column.DefaultValue is not null)
                {
                    line.Append(" DEFAULT ").Append(Literal(column.DefaultValue, column.Type, column.Multiple));
                }
            }

            lines.Add(line.ToString());
        }

        foreach (var key in table.ForeignKeys)
        {
            lines.Add(
                $"  FOREIGN KEY ({QuoteIdentifier(key.Column)}) " +
                $"REFERENCES {QuoteIdentifier(key.TargetTable)} (\"id\") " +
                $"ON DELETE {(key.CascadeDelete ? "CASCADE" : "SET DEFAULT")}");
        }

        builder.AppendJoin(",\n", lines).AppendLine().Append(')');

        return [builder.ToString()];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> DropTable(string table) =>
        [$"DROP TABLE IF EXISTS {QuoteIdentifier(table)}"];

    /// <inheritdoc />
    public IReadOnlyList<string> RenameTable(string oldName, string newName) =>
        [$"ALTER TABLE {QuoteIdentifier(oldName)} RENAME TO {QuoteIdentifier(newName)}"];

    /// <inheritdoc />
    public IReadOnlyList<string> AddColumn(string table, ColumnSpec column)
    {
        ArgumentNullException.ThrowIfNull(column);

        var builder = new StringBuilder("ALTER TABLE ")
            .Append(QuoteIdentifier(table))
            .Append(" ADD COLUMN ")
            .Append(QuoteIdentifier(column.Name))
            .Append(' ')
            .Append(ColumnType(column.Type, column.Multiple));

        var fallback = column.DefaultValue ?? ToStorage(
            column.Type, column.Multiple, column.Type.ZeroValue(column.Multiple));

        if (column.NotNull)
        {
            builder.Append(" NOT NULL");
        }

        if (fallback is not null)
        {
            builder.Append(" DEFAULT ").Append(Literal(fallback, column.Type, column.Multiple));
        }

        return [builder.ToString()];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> DropColumn(string table, string column) =>
        [$"ALTER TABLE {QuoteIdentifier(table)} DROP COLUMN {QuoteIdentifier(column)}"];

    /// <inheritdoc />
    public IReadOnlyList<string> RenameColumn(string table, string oldName, string newName) =>
    [
        $"ALTER TABLE {QuoteIdentifier(table)} " +
        $"RENAME COLUMN {QuoteIdentifier(oldName)} TO {QuoteIdentifier(newName)}"
    ];

    /// <inheritdoc />
    public IReadOnlyList<string> ChangeColumnType(
        TableSpec target,
        ColumnSpec column,
        IReadOnlyList<IndexSpec> indexes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(column);

        // PostgreSQL changes the type in place. Indexes survive — but USING is mandatory as soon
        // as the conversion isn't implicit, and it goes through text because that's the only
        // representation common to every Cratebase type.
        var type = ColumnType(column.Type, column.Multiple);
        var name = QuoteIdentifier(column.Name);

        return
        [
            $"ALTER TABLE {QuoteIdentifier(target.Name)} ALTER COLUMN {name} " +
            $"TYPE {type} USING {name}::text::{type}"
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> CreateIndex(IndexSpec index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var columns = string.Join(", ", index.Columns.Select(QuoteIdentifier));

        return
        [
            $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX IF NOT EXISTS " +
            $"{QuoteIdentifier(index.Name)} ON {QuoteIdentifier(index.Table)} ({columns})"
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> DropIndex(string table, string indexName) =>
        [$"DROP INDEX IF EXISTS {QuoteIdentifier(indexName)}"];

    private static string Literal(object? value, FieldType type, bool multiple)
    {
        if (value is null)
        {
            return "NULL";
        }

        var rendered = value switch
        {
            long number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            bool flag => flag ? "true" : "false",
            DateTimeOffset instant => "'" + Timestamp.Normalize(instant) + "'",
            _ => "'" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
                .Replace("'", "''", StringComparison.Ordinal) + "'",
        };

        // A text literal destined for a jsonb column must be cast explicitly, otherwise PostgreSQL
        // refuses the default value at CREATE TABLE time.
        return multiple || type is FieldType.Json or FieldType.GeoPoint
            ? rendered + "::jsonb"
            : rendered;
    }
}
