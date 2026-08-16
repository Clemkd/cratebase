using Cratebase.Data;

namespace Cratebase.Schema;

/// <summary>
/// Computes the DDL statements that move a collection from one state to another.
/// </summary>
/// <remarks>
/// The planner never touches the database: it returns a list of statements. This is what makes
/// them inspectable, testable without an engine, and lets the conformance suite verify that both
/// dialects produce equivalent schemas for the same transition.
/// </remarks>
public static class SchemaPlanner
{
    /// <summary>
    /// Plans a transition.
    /// </summary>
    /// <param name="ddl">DDL generator of the target dialect.</param>
    /// <param name="dialect">Dialect, for types and default values.</param>
    /// <param name="before">Prior state, or <see langword="null"/> for a creation.</param>
    /// <param name="after">Target state, or <see langword="null"/> for a deletion.</param>
    public static IReadOnlyList<string> Plan(
        ISchemaDdl ddl,
        ISqlDialect dialect,
        CollectionDefinition? before,
        CollectionDefinition? after)
    {
        ArgumentNullException.ThrowIfNull(ddl);
        ArgumentNullException.ThrowIfNull(dialect);

        if (before is null && after is null)
        {
            return [];
        }

        if (before is null)
        {
            return PlanCreate(ddl, dialect, after!);
        }

        if (after is null)
        {
            return ddl.DropTable(before.TableName);
        }

        return PlanAlter(ddl, dialect, before, after);
    }

    private static List<string> PlanCreate(
        ISchemaDdl ddl,
        ISqlDialect dialect,
        CollectionDefinition collection)
    {
        var statements = new List<string>(ddl.CreateTable(SchemaMapper.ToTableSpec(collection, dialect)));

        foreach (var index in SchemaMapper.ToIndexSpecs(collection))
        {
            statements.AddRange(ddl.CreateIndex(index));
        }

        return statements;
    }

    private static List<string> PlanAlter(
        ISchemaDdl ddl,
        ISqlDialect dialect,
        CollectionDefinition before,
        CollectionDefinition after)
    {
        var statements = new List<string>();

        if (!string.Equals(before.TableName, after.TableName, StringComparison.Ordinal))
        {
            statements.AddRange(ddl.RenameTable(before.TableName, after.TableName));
        }

        var table = after.TableName;
        var previous = before.Fields.ToDictionary(f => f.Id);
        var current = after.Fields.ToDictionary(f => f.Id);

        // Indexes are dropped first and recreated at the end: an index on a renamed or retyped
        // column must disappear before that column is touched.
        var beforeIndexes = SchemaMapper.ToIndexSpecs(before);
        var afterIndexes = SchemaMapper.ToIndexSpecs(after);

        foreach (var index in beforeIndexes)
        {
            statements.AddRange(ddl.DropIndex(table, index.Name));
        }

        foreach (var (id, field) in current)
        {
            if (!previous.TryGetValue(id, out var old))
            {
                statements.AddRange(ddl.AddColumn(table, SchemaMapper.ToColumnSpec(field, dialect)));
                continue;
            }

            // Rename: detected by identifier, never by name. Without a stable identifier, this
            // transition would look like a deletion followed by an addition, and the column would
            // come back empty.
            if (!string.Equals(old.ColumnName, field.ColumnName, StringComparison.Ordinal))
            {
                statements.AddRange(ddl.RenameColumn(table, old.ColumnName, field.ColumnName));
            }

            if (old.Type != field.Type || old.Multiple != field.Multiple)
            {
                statements.AddRange(ddl.ChangeColumnType(
                    SchemaMapper.ToTableSpec(after, dialect),
                    SchemaMapper.ToColumnSpec(field, dialect),
                    afterIndexes));
            }
        }

        foreach (var (id, field) in previous)
        {
            if (!current.ContainsKey(id))
            {
                statements.AddRange(ddl.DropColumn(table, field.ColumnName));
            }
        }

        foreach (var index in afterIndexes)
        {
            statements.AddRange(ddl.CreateIndex(index));
        }

        return statements;
    }
}
