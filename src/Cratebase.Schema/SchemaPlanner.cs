using Cratebase.Data;

namespace Cratebase.Schema;

/// <summary>
/// Calcule les instructions DDL faisant passer une collection d'un état à un autre.
/// </summary>
/// <remarks>
/// Le planificateur ne touche pas la base : il rend une liste d'instructions. C'est ce qui permet
/// de les inspecter, de les tester sans moteur, et de vérifier en conformité que les deux dialectes
/// produisent des schémas équivalents pour la même transition.
/// </remarks>
public static class SchemaPlanner
{
    /// <summary>
    /// Planifie une transition.
    /// </summary>
    /// <param name="ddl">Générateur de DDL du dialecte cible.</param>
    /// <param name="dialect">Dialecte, pour les types et les valeurs par défaut.</param>
    /// <param name="before">État antérieur, ou <see langword="null"/> pour une création.</param>
    /// <param name="after">État visé, ou <see langword="null"/> pour une suppression.</param>
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

        // Les index sont retirés d'abord et reposés à la fin : un index portant sur une colonne
        // renommée ou retypée doit disparaître avant qu'on y touche.
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

            // Renommage : détecté par l'identifiant, jamais par le nom. Sans identifiant stable,
            // cette transition passerait pour une suppression suivie d'un ajout, et la colonne
            // serait vidée.
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
