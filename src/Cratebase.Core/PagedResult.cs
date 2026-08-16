namespace Cratebase.Core;

/// <summary>
/// List envelope. The shape matches PocketBase's exactly, so a client written against PocketBase
/// has nothing to relearn.
/// </summary>
/// <param name="Page">Requested page, starting at 1.</param>
/// <param name="PerPage">Effective page size, after server-side capping.</param>
/// <param name="TotalItems">Total row count, or -1 if <c>skipTotal</c> was requested.</param>
/// <param name="TotalPages">Total page count, or -1 if <c>skipTotal</c> was requested.</param>
/// <param name="Items">Rows of the page.</param>
public sealed record PagedResult<T>(
    int Page,
    int PerPage,
    long TotalItems,
    long TotalPages,
    IReadOnlyList<T> Items);

/// <summary>Factories for <see cref="PagedResult{T}"/>.</summary>
public static class PagedResult
{
    /// <summary>Builds a page whose total has been computed.</summary>
    public static PagedResult<T> Counted<T>(int page, int perPage, long totalItems, IReadOnlyList<T> items)
    {
        var totalPages = perPage <= 0 ? 0 : (totalItems + perPage - 1) / perPage;
        return new PagedResult<T>(page, perPage, totalItems, totalPages, items);
    }

    /// <summary>
    /// Builds a page whose total was not computed, at the client's request
    /// (<c>skipTotal=1</c>). Both totals are -1, as in PocketBase.
    /// </summary>
    public static PagedResult<T> Uncounted<T>(int page, int perPage, IReadOnlyList<T> items) =>
        new(page, perPage, -1, -1, items);
}
