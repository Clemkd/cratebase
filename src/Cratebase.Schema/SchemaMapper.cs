using Cratebase.Core;
using Cratebase.Data;

namespace Cratebase.Schema;

/// <summary>
/// Translates a collection definition into a physical table description.
/// </summary>
public static class SchemaMapper
{
    /// <summary>Builds a collection's table description.</summary>
    public static TableSpec ToTableSpec(CollectionDefinition collection, ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(dialect);

        var columns = collection.Fields.Select(field => ToColumnSpec(field, dialect)).ToList();
        var foreignKeys = new List<ForeignKeySpec>();

        foreach (var field in collection.Fields)
        {
            // A multi-valued relation is stored as a JSON array: no foreign key is possible on it.
            // Integrity and cascading are then applied by the engine, inside the delete
            // transaction — weaker than the database, but that's the price of being multi-valued,
            // and it's PocketBase's choice too.
            if (field.Type is not FieldType.Relation
                || field.Multiple
                || field.Options.TargetCollection is not { Length: > 0 } target)
            {
                continue;
            }

            foreignKeys.Add(new ForeignKeySpec(field.ColumnName, target, field.Options.CascadeDelete));
        }

        return new TableSpec(collection.TableName, columns, SystemFields.Id, foreignKeys);
    }

    /// <summary>Builds a column's description.</summary>
    public static ColumnSpec ToColumnSpec(FieldDefinition field, ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(dialect);

        // No field is nullable except JSON: this is PocketBase's rule, kept because it removes the
        // "absent" vs. "empty" distinction, which makes filters ambiguous and results dependent on
        // the engine.
        var nullable = field.Type is FieldType.Json;

        var zero = field.Type.ZeroValue(field.Multiple);
        var storageDefault = nullable ? null : dialect.ToStorage(field.Type, field.Multiple, zero);

        return new ColumnSpec(
            field.ColumnName,
            field.Type,
            field.Multiple,
            NotNull: !nullable,
            storageDefault);
    }

    /// <summary>Builds a collection's index descriptions.</summary>
    public static IReadOnlyList<IndexSpec> ToIndexSpecs(CollectionDefinition collection)
    {
        ArgumentNullException.ThrowIfNull(collection);

        return
        [
            .. collection.Indexes.Select(index => new IndexSpec(
                index.Name,
                collection.TableName,
                [.. index.Fields.Select(name => collection.Field(name)?.ColumnName ?? name)],
                index.Unique)),
        ];
    }
}
