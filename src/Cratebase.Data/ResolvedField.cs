using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Field resolved against a collection's schema.
/// </summary>
/// <param name="ColumnName">Physical column name.</param>
/// <param name="Type">Logical type.</param>
/// <param name="Multiple">Does the field carry several values?</param>
/// <param name="TargetCollection">Target collection, for a relation-type field.</param>
public sealed record ResolvedField(
    string ColumnName,
    FieldType Type,
    bool Multiple,
    string? TargetCollection = null);

/// <summary>
/// Resolution of an identifier path against the schema.
/// </summary>
/// <remarks>
/// <b>This is the allow-list.</b> A path the resolver refuses must never reach SQL: the compiler
/// raises a 400 error. This is what makes injection impossible by construction rather than by
/// vigilance — a field invented by a client has no way to become SQL text.
/// </remarks>
public interface IQueryFieldResolver
{
    /// <summary>Name of the root collection.</summary>
    string RootCollection { get; }

    /// <summary>
    /// Resolves a path relative to the root collection.
    /// </summary>
    /// <param name="segments">Path segments, e.g. <c>["author", "name"]</c>.</param>
    /// <param name="field">Resolved field, if the path is valid.</param>
    /// <returns><see langword="false"/> if the path designates no known field.</returns>
    bool TryResolve(IReadOnlyList<string> segments, out ResolvedField field);
}
