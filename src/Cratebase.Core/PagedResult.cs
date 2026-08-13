namespace Cratebase.Core;

/// <summary>
/// Enveloppe de liste. La forme reprend celle de PocketBase à l'identique, pour qu'un client écrit
/// contre PocketBase n'ait rien à réapprendre.
/// </summary>
/// <param name="Page">Page demandée, à partir de 1.</param>
/// <param name="PerPage">Taille de page effective, après plafonnement par le serveur.</param>
/// <param name="TotalItems">Nombre total de lignes, ou -1 si <c>skipTotal</c> a été demandé.</param>
/// <param name="TotalPages">Nombre total de pages, ou -1 si <c>skipTotal</c> a été demandé.</param>
/// <param name="Items">Lignes de la page.</param>
public sealed record PagedResult<T>(
    int Page,
    int PerPage,
    long TotalItems,
    long TotalPages,
    IReadOnlyList<T> Items);

/// <summary>Fabriques de <see cref="PagedResult{T}"/>.</summary>
public static class PagedResult
{
    /// <summary>Construit une page dont le total a été calculé.</summary>
    public static PagedResult<T> Counted<T>(int page, int perPage, long totalItems, IReadOnlyList<T> items)
    {
        var totalPages = perPage <= 0 ? 0 : (totalItems + perPage - 1) / perPage;
        return new PagedResult<T>(page, perPage, totalItems, totalPages, items);
    }

    /// <summary>
    /// Construit une page dont le total n'a pas été calculé, à la demande du client
    /// (<c>skipTotal=1</c>). Les deux totaux valent -1, comme chez PocketBase.
    /// </summary>
    public static PagedResult<T> Uncounted<T>(int page, int perPage, IReadOnlyList<T> items) =>
        new(page, perPage, -1, -1, items);
}
