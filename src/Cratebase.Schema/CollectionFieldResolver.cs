using Cratebase.Data;

namespace Cratebase.Schema;

/// <summary>
/// Resolves a filter's paths against a collection's schema.
/// </summary>
/// <remarks>
/// This is the concrete implementation of the compiler's allow-list. It knows only the declared
/// fields: everything else is refused, and the refusal produces a 400 error before any SQL is
/// written.
/// </remarks>
public sealed class CollectionFieldResolver(CollectionDefinition collection) : IQueryFieldResolver
{
    private readonly CollectionDefinition _collection = collection
        ?? throw new ArgumentNullException(nameof(collection));

    /// <inheritdoc />
    public string RootCollection => _collection.Name;

    /// <inheritdoc />
    public bool TryResolve(IReadOnlyList<string> segments, out ResolvedField field)
    {
        ArgumentNullException.ThrowIfNull(segments);

        field = null!;

        // Relation traversal ("author.name") requires a join, hence a query rewrite and an extra
        // access check on the traversed collection. Until that's written, refuse explicitly rather
        // than resolving the first segment and producing a query that means something different
        // from what was asked.
        if (segments.Count != 1)
        {
            return false;
        }

        var definition = _collection.Field(segments[0]);

        if (definition is null || definition.Hidden)
        {
            // A hidden field (password, token key) is treated as nonexistent: making it filterable
            // would let a secret be guessed one character at a time with "~".
            return false;
        }

        field = new ResolvedField(
            definition.ColumnName,
            definition.Type,
            definition.Multiple,
            definition.Options.TargetCollection);

        return true;
    }
}
