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
/// Service des fichiers.
/// </summary>
/// <remarks>
/// Routes compatibles avec PocketBase : <c>/api/files/{collection}/{recordId}/{fichier}</c>, avec
/// <c>?thumb=</c>, <c>?download=1</c> et <c>?token=</c>.
/// </remarks>
public static class FileEndpoints
{
    /// <summary>Publie les routes de fichiers.</summary>
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

            // Un fichier protégé n'est servi qu'aux appelants qui satisfont la règle de
            // consultation de son enregistrement. Sans ce contrôle, publier l'URL suffirait à
            // contourner la règle — et les vignettes sont un chemin d'accès à part entière, donc
            // elles passent par le même contrôle.
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

                // Lève 404 si la règle refuse : même sémantique que la consultation directe.
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
        // On ne sait pas de quel champ vient le fichier sans lire l'enregistrement. Si un seul
        // champ de type fichier est protégé, on applique le contrôle ; sinon on sert directement.
        // C'est volontairement conservateur : mieux vaut contrôler un fichier public que servir un
        // fichier protégé.
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

        // Un fichier est chargé par une balise <img>, qui ne porte pas d'en-tête Authorization :
        // d'où le jeton en paramètre d'URL, de très courte durée de vie.
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
            throw new CratebaseBadRequestException($"Taille de vignette invalide : « {thumb} ».");
        }

        if (!ThumbnailGenerator.IsSupported(fileName))
        {
            throw new CratebaseBadRequestException(
                "Les vignettes ne sont produites que pour les images png, jpeg, gif et webp.");
        }

        var thumbKey = ObjectKey.ThumbFor(collection, recordId, fileName, size + ".png");

        // Cache : la vignette n'est produite qu'une fois. Sans lui, une page listant cent images
        // relance cent redimensionnements à chaque affichage.
        var cached = await store.OpenReadAsync(thumbKey, cancellationToken).ConfigureAwait(false);

        if (cached is not null)
        {
            return Results.Stream(cached, "image/png");
        }

        var sourceKey = ObjectKey.For(collection, recordId, fileName);

        await using var source = await store.OpenReadAsync(sourceKey, cancellationToken).ConfigureAwait(false)
            ?? throw new CratebaseNotFoundException();

        var bytes = ThumbnailGenerator.Generate(source, size)
            ?? throw new CratebaseBadRequestException("Ce fichier n'est pas une image lisible.");

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
