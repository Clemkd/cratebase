using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;

namespace Cratebase.Records;

/// <summary>
/// Parameters of a list query.
/// </summary>
public sealed record RecordQuery
{
    /// <summary>Default page size, as in PocketBase.</summary>
    public const int DefaultPerPage = 30;

    /// <summary>
    /// Maximum page size.
    /// </summary>
    /// <remarks>
    /// A mandatory bound: without it, <c>?perPage=1000000</c> is a denial of service in a URL. The
    /// cap is silent rather than an error — this is PocketBase's behavior, and it avoids breaking
    /// a client that asks for a bit too much.
    /// </remarks>
    public const int MaxPerPage = 500;

    /// <summary>Requested page, starting at 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Requested page size.</summary>
    public int PerPage { get; init; } = DefaultPerPage;

    /// <summary>Filter expression.</summary>
    public string? Filter { get; init; }

    /// <summary>Sort, fields comma-separated, prefixed with <c>-</c> for descending order.</summary>
    public string? Sort { get; init; }

    /// <summary>Projection of the returned fields.</summary>
    public string? Fields { get; init; }

    /// <summary>Skip the total count, which costs an extra query.</summary>
    public bool SkipTotal { get; init; }

    /// <summary>Effective, bounded page.</summary>
    public int EffectivePage => Math.Max(1, Page);

    /// <summary>Effective, bounded page size.</summary>
    public int EffectivePerPage => Math.Clamp(PerPage, 1, MaxPerPage);

    /// <summary>Corresponding offset.</summary>
    public int Offset => (EffectivePage - 1) * EffectivePerPage;
}

/// <summary>
/// Compilation of the sort clause.
/// </summary>
public static class SortCompiler
{
    /// <summary>Maximum number of sort fields.</summary>
    public const int MaxSortFields = 8;

    /// <summary>
    /// Compiles a sort expression into an <c>ORDER BY</c> clause.
    /// </summary>
    /// <remarks>
    /// ⚠️ Every field goes through the resolver, exactly as in a filter. An uncontrolled sort is a
    /// quiet leak: sorting by a field one has no right to read doesn't display it, but <b>the
    /// order of the results discloses its values</b>. A hidden field is therefore refused for
    /// sorting just as it is for filtering.
    /// </remarks>
    public static string Compile(
        string? sort,
        CollectionDefinition collection,
        ISqlDialect dialect,
        string alias)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(dialect);

        // Default order: most recent first. An ORDER BY is mandatory — without it, pagination is
        // meaningless, and the two engines don't return rows in the same order.
        if (string.IsNullOrWhiteSpace(sort))
        {
            return $"{dialect.QuoteIdentifier(alias)}.{dialect.QuoteIdentifier(SystemFields.Id)} DESC";
        }

        var terms = new List<string>();

        foreach (var raw in sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (terms.Count >= MaxSortFields)
            {
                throw new CratebaseBadRequestException(
                    $"Sorting cannot cover more than {MaxSortFields} fields.");
            }

            var descending = raw[0] == '-';
            var name = raw[0] is '-' or '+' ? raw[1..] : raw;

            var field = collection.Field(name);

            if (field is null || field.Hidden)
            {
                throw new CratebaseBadRequestException(
                    $"\"{name}\" is not a sortable field of collection \"{collection.Name}\".");
            }

            var expression = $"{dialect.QuoteIdentifier(alias)}.{dialect.QuoteIdentifier(field.ColumnName)}";

            terms.Add(dialect.OrderByNullsLast(expression, descending));
        }

        // Stable tie-break: without a deterministic last criterion, two rows with the same value
        // can swap order between two pages and one of them never appears.
        terms.Add($"{dialect.QuoteIdentifier(alias)}.{dialect.QuoteIdentifier(SystemFields.Id)} DESC");

        return string.Join(", ", terms);
    }
}
