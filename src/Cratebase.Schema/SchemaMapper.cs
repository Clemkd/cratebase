using Cratebase.Core;
using Cratebase.Data;

namespace Cratebase.Schema;

/// <summary>
/// Traduit une définition de collection en description physique de table.
/// </summary>
public static class SchemaMapper
{
    /// <summary>Construit la description de table d'une collection.</summary>
    public static TableSpec ToTableSpec(CollectionDefinition collection, ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(dialect);

        var columns = collection.Fields.Select(field => ToColumnSpec(field, dialect)).ToList();
        var foreignKeys = new List<ForeignKeySpec>();

        foreach (var field in collection.Fields)
        {
            // Une relation multi-valuée est stockée en tableau JSON : aucune clé étrangère n'est
            // possible dessus. L'intégrité et la cascade sont alors appliquées par le moteur, dans
            // la transaction de suppression — moins fort que la base, mais c'est le prix du
            // multi-valué, et c'est le choix de PocketBase aussi.
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

    /// <summary>Construit la description d'une colonne.</summary>
    public static ColumnSpec ToColumnSpec(FieldDefinition field, ISqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(dialect);

        // Aucun champ n'est nullable sauf le JSON : c'est la règle de PocketBase, reprise parce
        // qu'elle supprime la distinction « absent » / « vide », qui rend les filtres ambigus et
        // les résultats dépendants du moteur.
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

    /// <summary>Construit les descriptions d'index d'une collection.</summary>
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
