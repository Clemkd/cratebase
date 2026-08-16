using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Physical description of a column, independent of the dialect.
/// </summary>
/// <param name="Name">Column name.</param>
/// <param name="Type">Logical type of the field.</param>
/// <param name="Multiple">Does the field carry several values?</param>
/// <param name="NotNull">Does the column reject the absence of a value?</param>
/// <param name="DefaultValue">Default value, already converted for storage.</param>
public sealed record ColumnSpec(
    string Name,
    FieldType Type,
    bool Multiple = false,
    bool NotNull = false,
    object? DefaultValue = null);

/// <summary>
/// Foreign key of a relation field.
/// </summary>
/// <param name="Column">Column carrying the reference.</param>
/// <param name="TargetTable">Target table.</param>
/// <param name="CascadeDelete">
/// Does deleting the parent delete the child? Otherwise the reference is cleared.
/// </param>
public sealed record ForeignKeySpec(string Column, string TargetTable, bool CascadeDelete);

/// <summary>
/// Index of a table.
/// </summary>
/// <param name="Name">Index name. Unique within the database.</param>
/// <param name="Table">Table it belongs to.</param>
/// <param name="Columns">Columns, in order.</param>
/// <param name="Unique">Does the index enforce uniqueness?</param>
public sealed record IndexSpec(
    string Name,
    string Table,
    IReadOnlyList<string> Columns,
    bool Unique = false);

/// <summary>
/// Physical description of a table.
/// </summary>
/// <param name="Name">Table name.</param>
/// <param name="Columns">Columns, including the primary key.</param>
/// <param name="PrimaryKey">Name of the primary key column.</param>
/// <param name="ForeignKeys">Foreign keys.</param>
public sealed record TableSpec(
    string Name,
    IReadOnlyList<ColumnSpec> Columns,
    string PrimaryKey,
    IReadOnlyList<ForeignKeySpec> ForeignKeys);

/// <summary>
/// DDL generation.
/// </summary>
/// <remarks>
/// Separated from <see cref="ISqlDialect"/> because the two surfaces don't evolve at the same
/// pace: DDL changes when a field type is added, the query language when an operator is added.
/// Dialects implement both.
/// </remarks>
public interface ISchemaDdl
{
    /// <summary>
    /// Statements to run <b>before opening the transaction</b> of a schema change.
    /// </summary>
    /// <remarks>
    /// ⚠️ On SQLite, rebuilding a table destroys the original table. If foreign keys are active at
    /// that moment, <c>ON DELETE CASCADE</c> fires and <b>rows in referencing tables are erased</b>
    /// — a total, silent data loss, triggered by a simple field type change. The pragma must
    /// therefore be turned off, and it cannot be turned off inside a transaction: hence this
    /// separation.
    /// </remarks>
    IReadOnlyList<string> BeforeSchemaChange { get; }

    /// <summary>Statements to run after the transaction has committed.</summary>
    IReadOnlyList<string> AfterSchemaChange { get; }

    /// <summary>
    /// Integrity check to run inside the transaction, before commit. Returns one row per violation
    /// detected.
    /// </summary>
    string? IntegrityCheckStatement { get; }

    /// <summary>Creates a table.</summary>
    IReadOnlyList<string> CreateTable(TableSpec table);

    /// <summary>Drops a table.</summary>
    IReadOnlyList<string> DropTable(string table);

    /// <summary>Renames a table.</summary>
    IReadOnlyList<string> RenameTable(string oldName, string newName);

    /// <summary>Adds a column.</summary>
    IReadOnlyList<string> AddColumn(string table, ColumnSpec column);

    /// <summary>Drops a column.</summary>
    IReadOnlyList<string> DropColumn(string table, string column);

    /// <summary>Renames a column.</summary>
    IReadOnlyList<string> RenameColumn(string table, string oldName, string newName);

    /// <summary>
    /// Changes a column's type.
    /// </summary>
    /// <remarks>
    /// ⚠️ SQLite cannot do this: the table must be rebuilt. The rebuild must <b>recreate the
    /// indexes</b>, otherwise a uniqueness constraint disappears without the slightest message and
    /// duplicates creep in. That's why the method receives the whole target table rather than just
    /// the column.
    /// </remarks>
    IReadOnlyList<string> ChangeColumnType(TableSpec target, ColumnSpec column, IReadOnlyList<IndexSpec> indexes);

    /// <summary>Creates an index.</summary>
    IReadOnlyList<string> CreateIndex(IndexSpec index);

    /// <summary>Drops an index.</summary>
    IReadOnlyList<string> DropIndex(string table, string indexName);
}
