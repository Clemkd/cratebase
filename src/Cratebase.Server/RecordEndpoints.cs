using System.Text.Json;
using Cratebase.Core;
using Cratebase.Records;
using Cratebase.Schema;
using Cratebase.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cratebase.Server;

/// <summary>
/// Record CRUD endpoints. Compatible with PocketBase's API.
/// </summary>
public static class RecordEndpoints
{
    /// <summary>Publishes <c>/collections/{collection}/records</c>.</summary>
    public static IEndpointRouteBuilder MapRecordEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/collections/{collection}/records");

        group.MapGet("/", async (
            string collection,
            HttpContext http,
            RecordService records,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var query = new RecordQuery
            {
                Page = ReadInt(http, "page", 1),
                PerPage = ReadInt(http, "perPage", RecordQuery.DefaultPerPage),
                Filter = http.Request.Query["filter"],
                Sort = http.Request.Query["sort"],
                Fields = http.Request.Query["fields"],
                SkipTotal = http.Request.Query["skipTotal"] == "1",
            };

            var context = RequestContextFactory.Create(http, user);

            var page = await records.ListAsync(collection, query, context, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(Project(page, query.Fields));
        });

        group.MapGet("/{id}", async (
            string collection,
            string id,
            HttpContext http,
            RecordService records,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var context = RequestContextFactory.Create(http, user);

            var record = await records.ViewAsync(collection, id, context, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(ProjectOne(record, http.Request.Query["fields"]));
        });

        group.MapPost("/", async (
            string collection,
            HttpContext http,
            CollectionRegistry registry,
            RecordService records,
            IObjectStore store,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var definition = registry.Require(collection);

            // The identifier is produced here, before the write: objects are filed under
            // {collection}/{id}/…, so it must be known in order to import files.
            var recordId = RecordId.New();
            var (data, uploaded) = await ReadBodyAsync(
                    http, definition, recordId, store, null, cancellationToken)
                .ConfigureAwait(false);

            var context = RequestContextFactory.Create(http, user, data.AsDictionary());

            try
            {
                var created = await records
                    .CreateAsync(collection, data, context, recordId, cancellationToken)
                    .ConfigureAwait(false);

                return Results.Created(
                    $"/api/collections/{collection}/records/{created["id"]}", created);
            }
            catch
            {
                // The write failed after the import: without this rollback, the bytes would stay on
                // storage with no row to name them.
                await uploaded.RollbackAsync(store, cancellationToken).ConfigureAwait(false);
                throw;
            }
        });

        group.MapPatch("/{id}", async (
            string collection,
            string id,
            HttpContext http,
            CollectionRegistry registry,
            RecordService records,
            IObjectStore store,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var definition = registry.Require(collection);

            if (!RecordId.TryParse(id, out var recordId))
            {
                throw new CratebaseNotFoundException();
            }

            // The prior state is used by the "+" and "-" modifiers on file fields. It's read with
            // the caller's own rights: if they can't view the row, the modifiers don't apply and the
            // write becomes a full replacement.
            IReadOnlyDictionary<string, object?>? original = null;

            try
            {
                original = await records
                    .ViewAsync(collection, id, RequestContextFactory.Create(http, user), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CratebaseNotFoundException)
            {
                original = null;
            }

            var (data, uploaded) = await ReadBodyAsync(
                    http, definition, recordId, store, original, cancellationToken)
                .ConfigureAwait(false);

            var context = RequestContextFactory.Create(http, user, data.AsDictionary());

            try
            {
                var updated = await records
                    .UpdateAsync(collection, id, data, context, cancellationToken)
                    .ConfigureAwait(false);

                return Results.Ok(updated);
            }
            catch
            {
                await uploaded.RollbackAsync(store, cancellationToken).ConfigureAwait(false);
                throw;
            }
        });

        group.MapDelete("/{id}", async (
            string collection,
            string id,
            HttpContext http,
            RecordService records,
            IObjectStore store,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var context = RequestContextFactory.Create(http, user);

            await records.DeleteAsync(collection, id, context, cancellationToken).ConfigureAwait(false);

            // Row first, then bytes. The order matters: deleting the files first would leave a row
            // pointing at nothing if the database deletion then failed.
            await store.DeletePrefixAsync(ObjectKey.PrefixFor(collection, id), cancellationToken)
                .ConfigureAwait(false);

            return Results.NoContent();
        });

        return endpoints;
    }

    private static async Task<(RecordData Data, UploadedFiles Files)> ReadBodyAsync(
        HttpContext http,
        CollectionDefinition collection,
        RecordId recordId,
        IObjectStore store,
        IReadOnlyDictionary<string, object?>? original,
        CancellationToken cancellationToken)
    {
        if (FileUploads.IsMultipart(http.Request))
        {
            return await FileUploads
                .ReadMultipartAsync(http.Request, collection, recordId, store, original, cancellationToken)
                .ConfigureAwait(false);
        }

        var body = await http.Request.ReadFromJsonAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);

        return (FileUploads.ReadJson(body), new UploadedFiles());
    }

    private static int ReadInt(HttpContext http, string name, int fallback) =>
        int.TryParse(http.Request.Query[name], out var value) ? value : fallback;

    private static PagedResult<IReadOnlyDictionary<string, object?>> Project(
        PagedResult<IReadOnlyDictionary<string, object?>> page,
        string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
        {
            return page;
        }

        return new PagedResult<IReadOnlyDictionary<string, object?>>(
            page.Page,
            page.PerPage,
            page.TotalItems,
            page.TotalPages,
            [.. page.Items.Select(item => ProjectOne(item, fields))]);
    }

    /// <summary>
    /// Restricts a record to the requested fields.
    /// </summary>
    /// <remarks>
    /// The projection applies <b>after</b> the query and can only remove fields. Letting it
    /// participate in building the <c>SELECT</c> would let a client request a hidden column by
    /// naming it.
    /// </remarks>
    private static IReadOnlyDictionary<string, object?> ProjectOne(
        IReadOnlyDictionary<string, object?> record,
        string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields) || fields.Contains('*', StringComparison.Ordinal))
        {
            return record;
        }

        var wanted = fields
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        var projected = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in record)
        {
            if (wanted.Contains(key))
            {
                projected[key] = value;
            }
        }

        return projected;
    }
}
