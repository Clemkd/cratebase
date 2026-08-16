using System.Globalization;
using System.Text;
using Cratebase.Core;
using Cratebase.Data;

namespace Cratebase.Data.Sqlite;

public sealed partial class SqliteDialect
{
    /// <inheritdoc />
    public IReadOnlyList<string> BeforeSchemaChange { get; } = ["PRAGMA foreign_keys = OFF;"];

    /// <inheritdoc />
    public IReadOnlyList<string> AfterSchemaChange { get; } = ["PRAGMA foreign_keys = ON;"];

    /// <inheritdoc />
    public string? IntegrityCheckStatement => "PRAGMA foreign_key_check;";

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
                    line.Append(" DEFAULT ").Append(Literal(column.DefaultValue));
                }
            }

            lines.Add(line.ToString());
        }

        foreach (var key in table.ForeignKeys)
        {
            // ON DELETE SET DEFAULT rather than SET NULL: Cratebase's fields are not nullable
            // (see FieldTypeInfo.ZeroValue), so the reference falls back to the empty string.
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

        // SQLite requires a non-null default to add a NOT NULL column to a table that already
        // holds rows. The logical type always provides one.
        var fallback = column.DefaultValue ?? ToStorage(
            column.Type, column.Multiple, column.Type.ZeroValue(column.Multiple));

        if (column.NotNull)
        {
            builder.Append(" NOT NULL");
        }

        if (fallback is not null)
        {
            builder.Append(" DEFAULT ").Append(Literal(fallback));
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
        ArgumentNullException.ThrowIfNull(indexes);

        // SQLite cannot change a column's type: the table must be rebuilt. This is the official
        // 12-step procedure, trimmed to what's needed here.
        //
        // ⚠️ The trap in this maneuver is losing the indexes: they belong to the destroyed table.
        // A uniqueness constraint that disappears produces no error — it simply lets duplicates
        // creep in, and they're noticed months later. Hence the systematic recreation below, and
        // the conformance test that verifies it.
        var temporary = target.Name + "_cb_rebuild";
        var statements = new List<string>();

        var renamed = target with { Name = temporary };
        statements.AddRange(CreateTable(renamed));

        var columns = string.Join(", ", target.Columns.Select(c => QuoteIdentifier(c.Name)));

        statements.Add(
            $"INSERT INTO {QuoteIdentifier(temporary)} ({columns}) " +
            $"SELECT {columns} FROM {QuoteIdentifier(target.Name)}");

        statements.Add($"DROP TABLE {QuoteIdentifier(target.Name)}");
        statements.Add($"ALTER TABLE {QuoteIdentifier(temporary)} RENAME TO {QuoteIdentifier(target.Name)}");

        foreach (var index in indexes)
        {
            statements.AddRange(CreateIndex(index));
        }

        return statements;
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

    /// <summary>
    /// Renders a default value as a SQL literal.
    /// </summary>
    /// <remarks>
    /// The only place in the engine where a value is written into SQL text rather than passed as a
    /// parameter — DDL doesn't admit parameters. It comes from the schema, defined by a superuser,
    /// so quote escaping is the last line of defense.
    /// </remarks>
    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        long number => number.ToString(CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "1" : "0",
        _ => "'" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
            .Replace("'", "''", StringComparison.Ordinal) + "'",
    };
}
