using Cratebase.Auth;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Records;
using Cratebase.Schema;
using Cratebase.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cratebase.Server;

/// <summary>
/// File serving.
/// </summary>
/// <remarks>
/// Routes compatible with PocketBase: <c>/api/files/{collection}/{recordId}/{file}</c>, with
/// <c>?thumb=</c>, <c>?download=1</c>, and <c>?token=</c>.
/// </remarks>
public static class FileEndpoints
{
    /// <summary>Publishes the file routes.</summary>
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/files/token", async (
            AuthTokenStore tokens,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (!user.IsAuthenticated || user.Id is not { } id || user.CollectionName is not { } collection)
            {
                throw new CratebaseUnauthenticatedException();
            }

            var token = await tokens
                .IssueAsync(collection, id, AuthTokenStore.FileTokenLifetime, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new { token });
        });

        endpoints.MapGet("/files/{collection}/{recordId}/{fileName}", async (
            string collection,
            string recordId,
            string fileName,
            HttpContext http,
            CollectionRegistry registry,
            RecordService records,
            AuthTokenStore tokens,
            AuthService auth,
            IObjectStore store,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var definition = registry.Require(collection);
            var field = FieldOwning(definition, fileName, recordId, store, cancellationToken);

            // A protected file is only served to callers who satisfy its record's view rule.
            // Without this check, publishing the URL would be enough to bypass the rule — and
            // thumbnails are a full access path of their own, so they go through the same check.
            if (await field.ConfigureAwait(false) is { Options.Protected: true })
            {
                var caller = await ResolveFileCallerAsync(http, tokens, auth, user, cancellationToken)
                    .ConfigureAwait(false);

                var context = RequestContextFactory.Create(http, caller);
                context = new FilterRequestContext
                {
                    Auth = caller,
                    Context = "protectedFile",
                    Method = context.Method,
                    Headers = context.Headers,
                    Query = context.Query,
                };

                // Throws 404 if the rule refuses: same semantics as a direct view.
                await records.ViewAsync(collection, recordId, context, cancellationToken)
                    .ConfigureAwait(false);
            }

            var key = ObjectKey.For(collection, recordId, fileName);
            var thumb = http.Request.Query["thumb"].ToString();

            if (!string.IsNullOrWhiteSpace(thumb))
            {
                return await ServeThumbAsync(store, collection, recordId, fileName, thumb, cancellationToken)
                    .ConfigureAwait(false);
            }

            var stream = await store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new CratebaseNotFoundException();

            var download = http.Request.Query["download"] == "1";

            return Results.Stream(
                stream,
                ObjectKey.ContentTypeOf(fileName),
                fileDownloadName: download ? fileName : null,
                enableRangeProcessing: true);
        });

        return endpoints;
    }

    private static async Task<FieldDefinition?> FieldOwning(
        CollectionDefinition collection,
        string fileName,
        string recordId,
        IObjectStore store,
        CancellationToken cancellationToken)
    {
        // There's no way to know which field the file belongs to without reading the record. If
        // exactly one file-type field is protected, the check applies; otherwise the file is served
        // directly. This is deliberately conservative: better to check a public file than serve a
        // protected one unchecked.
        await Task.CompletedTask.ConfigureAwait(false);

        var fileFields = collection.Fields.Where(f => f.Type is FieldType.File).ToList();

        return fileFields.FirstOrDefault(f => f.Options.Protected) ?? fileFields.FirstOrDefault();
    }

    private static async Task<ICurrentUser> ResolveFileCallerAsync(
        HttpContext http,
        AuthTokenStore tokens,
        AuthService auth,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (user.IsAuthenticated)
        {
            return user;
        }

        // A file is loaded by an <img> tag, which carries no Authorization header: hence the token
        // as a URL parameter, with a very short lifetime.
        var token = http.Request.Query["token"].ToString();

        if (string.IsNullOrWhiteSpace(token))
        {
            return AnonymousUser.Instance;
        }

        var resolved = await tokens.ResolveAsync(token, cancellationToken).ConfigureAwait(false);

        if (resolved is null)
        {
            return AnonymousUser.Instance;
        }

        var record = await auth.LoadAsync(resolved.Collection, resolved.RecordId, cancellationToken)
            .ConfigureAwait(false);

        return record is null ? AnonymousUser.Instance : new TokenUser(record);
    }

    private static async Task<IResult> ServeThumbAsync(
        IObjectStore store,
        string collection,
        string recordId,
        string fileName,
        string thumb,
        CancellationToken cancellationToken)
    {
        if (!ThumbnailGenerator.TryParse(thumb, out var size))
        {
            throw new CratebaseBadRequestException($"Invalid thumbnail size: \"{thumb}\".");
        }

        if (!ThumbnailGenerator.IsSupported(fileName))
        {
            throw new CratebaseBadRequestException(
                "Thumbnails are only produced for png, jpeg, gif, and webp images.");
        }

        var thumbKey = ObjectKey.ThumbFor(collection, recordId, fileName, size + ".png");

        // Cache: the thumbnail is only produced once. Without it, a page listing a hundred images
        // would trigger a hundred resizes on every render.
        var cached = await store.OpenReadAsync(thumbKey, cancellationToken).ConfigureAwait(false);

        if (cached is not null)
        {
            return Results.Stream(cached, "image/png");
        }

        var sourceKey = ObjectKey.For(collection, recordId, fileName);

        await using var source = await store.OpenReadAsync(sourceKey, cancellationToken).ConfigureAwait(false)
            ?? throw new CratebaseNotFoundException();

        var bytes = ThumbnailGenerator.Generate(source, size)
            ?? throw new CratebaseBadRequestException("This file is not a readable image.");

        await using (var buffer = new MemoryStream(bytes, writable: false))
        {
            await store.PutAsync(thumbKey, buffer, "image/png", cancellationToken).ConfigureAwait(false);
        }

        return Results.Bytes(bytes, "image/png");
    }

    private sealed class TokenUser(AuthenticatedRecord record) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public bool IsSuperuser => record.IsSuperuser;

        public RecordId? Id => record.Id;

        public string? CollectionName => record.Collection;

        public IReadOnlyCollection<string> Permissions => record.Permissions;

        public IReadOnlyDictionary<string, object?> Fields => record.Fields;
    }
}
