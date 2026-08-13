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
            // ON DELETE SET DEFAULT plutôt que SET NULL : les champs de Cratebase ne sont pas
            // nullables (voir FieldTypeInfo.ZeroValue), donc la référence tombe à la chaîne vide.
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

        // SQLite exige une valeur par défaut non nulle pour ajouter une colonne NOT NULL à une
        // table qui contient déjà des lignes. Le type logique en fournit toujours une.
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

        // SQLite ne sait pas changer le type d'une colonne : il faut reconstruire la table.
        // Procédure officielle en 12 étapes, réduite à ce dont on a besoin ici.
        //
        // ⚠️ Le piège de cette manœuvre est la perte des index : ils appartiennent à la table
        // détruite. Une contrainte d'unicité qui disparaît ne produit aucune erreur — elle laisse
        // simplement les doublons s'installer, et on s'en aperçoit des mois plus tard. D'où la
        // recréation systématique ci-dessous, et le test de conformité qui la vérifie.
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
    /// Rend une valeur par défaut en littéral SQL.
    /// </summary>
    /// <remarks>
    /// Seul endroit du moteur où une valeur est écrite dans le texte SQL plutôt que passée en
    /// paramètre — le DDL n'admet pas les paramètres. Elle vient du schéma, défini par un
    /// superadmin, et l'échappement des quotes est donc la dernière ligne de défense.
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
