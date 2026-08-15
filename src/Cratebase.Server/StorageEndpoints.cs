using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Cratebase.Auth;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Records;
using Cratebase.Schema;
using Cratebase.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cratebase.Server;

/// <summary>
/// Un objet du magasin, replacé dans le modèle de collections.
/// </summary>
/// <remarks>
/// Le magasin ne connaît que des clés ; c'est la disposition <c>{collection}/{enregistrement}/…</c>
/// qui leur rend un sens. Le faire ici et non dans <c>Cratebase.Storage</c> est délibéré : le
/// magasin doit rester ignorant du modèle, sans quoi la promesse « le reste du moteur ne connaît que
/// des clés » se retournerait.
/// </remarks>
public sealed record StoredObject
{
    /// <summary>Clé complète.</summary>
    public required string Key { get; init; }

    /// <summary>Collection déduite de la clé, ou chaîne vide si la clé sort du modèle.</summary>
    public string Collection { get; init; } = string.Empty;

    /// <summary>Enregistrement déduit de la clé.</summary>
    public string RecordId { get; init; } = string.Empty;

    /// <summary>Nom du fichier, ou du fichier source pour une vignette.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Taille en octets.</summary>
    public long Size { get; init; }

    /// <summary>Type MIME déduit de l'extension.</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>Vignette dérivée d'une image, donc régénérable.</summary>
    public bool IsThumb { get; init; }

    /// <summary>Aucun enregistrement ne référence ce fichier.</summary>
    public bool Orphan { get; init; }
}

/// <summary>
/// Exploitation du magasin de fichiers.
/// </summary>
/// <remarks>
/// <para>
/// Ces routes décrivent et inventorient ; elles ne configurent rien. Le magasin — disque local ou
/// S3, seau, point de terminaison, identifiants — est décidé par la configuration de l'hôte, et le
/// reste : une console qui écrirait une clé secrète en base la ferait entrer dans toutes les
/// sauvegardes de cette base. La console peut donc <b>lire</b> la configuration en vigueur et
/// l'<b>éprouver</b>, ce qui est ce dont on a besoin quand les fichiers cessent de s'afficher.
/// </para>
/// <para>
/// Réservé au superadministrateur : l'inventaire nomme les fichiers de tous les enregistrements, y
/// compris ceux que les règles d'accès protégeraient un par un.
/// </para>
/// </remarks>
public static class StorageEndpoints
{
    /// <summary>Préfixe des objets de diagnostic, exclus de l'inventaire et des archives.</summary>
    public const string DiagnosticsPrefix = "_diagnostics";

    /// <summary>Segment qui marque un répertoire de vignettes.</summary>
    private const string ThumbMarker = "thumbs_";

    /// <summary>Taille de page maximale de l'inventaire.</summary>
    private const int MaxPerPage = 500;

    /// <summary>Durée de vie de l'URL signée éprouvée par le test de connexion.</summary>
    private static readonly TimeSpan ProbeLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Publie les routes du magasin de fichiers.</summary>
    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/storage");

        group.MapGet("/", async (
            CratebaseOptions options,
            IObjectStore store,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            var description = options.StorageDescription;

            long files = 0, thumbs = 0, fileBytes = 0, thumbBytes = 0;

            await foreach (var info in store.ListInfoAsync(string.Empty, cancellationToken).ConfigureAwait(false))
            {
                if (info.Key.StartsWith(DiagnosticsPrefix, StringComparison.Ordinal)) continue;

                if (IsThumbKey(info.Key))
                {
                    thumbs++;
                    thumbBytes += info.Length;
                }
                else
                {
                    files++;
                    fileBytes += info.Length;
                }
            }

            return Results.Ok(new
            {
                kind = description.Kind,
                name = store.Name,
                directory = description.Directory,
                bucket = description.Bucket,
                endpoint = description.Endpoint,
                publicEndpoint = description.PublicEndpoint,
                region = description.Region,
                forcePathStyle = description.ForcePathStyle,
                accessKeyHint = description.AccessKeyHint,
                hasSecretKey = description.HasSecretKey,
                // Le disque local n'a pas d'URL propre : l'API sert l'octet elle-même. C'est ce qui
                // décide si le test de connexion a une URL signée à éprouver.
                presignedUrls = description.Kind == "s3",
                objects = new { files, thumbs, fileBytes, thumbBytes },
            });
        });

        group.MapGet("/objects", async (
            HttpContext http,
            IObjectStore store,
            CollectionRegistry registry,
            RecordService records,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            var page = Math.Max(1, ReadInt(http, "page") ?? 1);
            var perPage = Math.Clamp(ReadInt(http, "perPage") ?? 50, 1, MaxPerPage);
            var collection = http.Request.Query["collection"].ToString();
            var search = http.Request.Query["q"].ToString();
            var kind = http.Request.Query["kind"].ToString();
            var orphansOnly = http.Request.Query["orphans"] == "1";

            // Le préfixe est passé au magasin quand il existe : sur S3, filtrer côté serveur au lieu
            // de rapatrier tout le seau change l'ordre de grandeur de l'inventaire.
            var prefix = string.IsNullOrWhiteSpace(collection) ? string.Empty : collection + "/";

            var all = new List<StoredObject>();

            await foreach (var info in store.ListInfoAsync(prefix, cancellationToken).ConfigureAwait(false))
            {
                if (info.Key.StartsWith(DiagnosticsPrefix, StringComparison.Ordinal)) continue;

                var described = Describe(info);

                if (kind == "files" && described.IsThumb) continue;
                if (kind == "thumbs" && !described.IsThumb) continue;

                if (search.Length > 0
                    && !described.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                all.Add(described);
            }

            // L'appartenance est résolue sur l'inventaire entier et non sur la page affichée : le
            // filtre « orphelins seulement » doit pouvoir écarter des lignes avant de paginer, sinon
            // la première page en montrerait trois et la deuxième aucune.
            var resolved = await MarkOrphansAsync(all, registry, records, user, cancellationToken)
                .ConfigureAwait(false);

            var visible = orphansOnly
                ? resolved.Where(entry => entry.Orphan).ToList()
                : resolved;

            var ordered = visible.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();
            var total = ordered.Count;

            return Results.Ok(new
            {
                page,
                perPage,
                totalItems = total,
                totalPages = total == 0 ? 0 : (total + perPage - 1) / perPage,
                items = ordered.Skip((page - 1) * perPage).Take(perPage),
                orphans = resolved.Count(entry => entry.Orphan),
            });
        });

        // Le corps est déclaré explicitement : une méthode DELETE n'admet pas de corps inféré, et
        // l'application refuse de démarrer si on le laisse deviner.
        group.MapDelete("/objects", async (
            [FromBody] DeleteObjectsRequest request,
            IObjectStore store,
            CollectionRegistry registry,
            RecordService records,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);
            ArgumentNullException.ThrowIfNull(request);

            var wanted = new List<StoredObject>();

            foreach (var key in request.Keys ?? [])
            {
                if (string.IsNullOrWhiteSpace(key)) continue;

                ObjectKey.Validate(key);

                var info = await store.StatAsync(key, cancellationToken).ConfigureAwait(false);

                if (info is not null) wanted.Add(Describe(info));
            }

            var resolved = await MarkOrphansAsync(wanted, registry, records, user, cancellationToken)
                .ConfigureAwait(false);

            // Un fichier encore référencé ne se supprime pas d'ici. L'enlever laisserait
            // l'enregistrement pointer vers rien : la console montrerait une image cassée, et rien
            // dans la base ne dirait qui l'a retirée ni quand. Le retrait passe par l'édition de
            // l'enregistrement, qui met la référence à jour en même temps.
            var referenced = resolved.Where(entry => !entry.Orphan && !entry.IsThumb).ToList();

            if (referenced.Count > 0)
            {
                throw new CratebaseConflictException(
                    $"{referenced.Count} objet(s) sont encore référencés par un enregistrement, "
                    + $"à commencer par « {referenced[0].Key} ». Retirez le fichier depuis "
                    + "l'enregistrement : la référence sera mise à jour en même temps.");
            }

            foreach (var entry in resolved)
            {
                await store.DeleteAsync(entry.Key, cancellationToken).ConfigureAwait(false);
            }

            return Results.Ok(new { deleted = resolved.Count });
        });

        group.MapPost("/check", async (
            IObjectStore store,
            IHttpClientFactory httpClients,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            CollectionEndpoints.RequireSuperuser(user);

            var steps = await ProbeAsync(store, httpClients, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new
            {
                ok = steps.TrueForAll(step => step.State != "failed"),
                steps,
            });
        });

        group.MapGet("/archive", async (
            HttpContext http,
            IObjectStore store,
            AuthTokenStore tokens,
            AuthService auth,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            // Un téléchargement est une navigation, qui ne porte pas d'en-tête `Authorization` :
            // le jeton passe donc par l'URL, comme pour les fichiers protégés, et vit deux minutes.
            // Le journal masque déjà le paramètre `token`, sinon la précaution serait annulée par
            // la ligne qui l'enregistre.
            CollectionEndpoints.RequireSuperuser(
                await ResolveCallerAsync(http, tokens, auth, user, cancellationToken)
                    .ConfigureAwait(false));

            var collection = http.Request.Query["collection"].ToString();
            var withThumbs = http.Request.Query["thumbs"] == "1";
            var prefix = string.IsNullOrWhiteSpace(collection) ? string.Empty : collection + "/";

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

            // `ZipArchive` écrit en synchrone — il n'existe pas d'API d'archive asynchrone dans la
            // bibliothèque standard — et Kestrel refuse par défaut les écritures synchrones sur le
            // corps de réponse. L'autorisation est posée ici, sur cette requête seule : la lever
            // globalement exposerait toutes les routes au blocage de fil qu'elle rend possible.
            // Le coût réel est un fil retenu pendant le téléchargement ; l'alternative — bâtir
            // l'archive en fichier temporaire d'abord — doublerait l'espace disque et retarderait
            // le premier octet de toute la durée de la copie.
            http.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;

            // En flux, jamais en mémoire : une archive se compte en gigaoctets, et la construire
            // d'abord ferait tomber le processus bien avant de servir le premier octet.
            return Results.Stream(
                stream => WriteArchiveAsync(stream, store, prefix, withThumbs, cancellationToken),
                "application/zip",
                fileDownloadName: $"fichiers-{stamp}.zip");
        });

        return endpoints;
    }

    /// <summary>Clés à supprimer.</summary>
    public sealed record DeleteObjectsRequest
    {
        /// <summary>Clés complètes.</summary>
        public IReadOnlyList<string>? Keys { get; init; }
    }

    /// <summary>Une étape du test de connexion.</summary>
    public sealed record ProbeStep
    {
        /// <summary>Ce que l'étape éprouve.</summary>
        public required string Name { get; init; }

        /// <summary><c>ok</c>, <c>skipped</c> ou <c>failed</c>.</summary>
        public required string State { get; init; }

        /// <summary>Précision lisible, ou message d'erreur.</summary>
        public string Detail { get; init; } = string.Empty;

        /// <summary>Durée de l'étape, en millisecondes.</summary>
        public double Milliseconds { get; init; }
    }

    /// <summary>Une clé désigne-t-elle une vignette ?</summary>
    private static bool IsThumbKey(string key)
    {
        var segments = key.Split('/');

        return segments.Length == 4
            && segments[2].StartsWith(ThumbMarker, StringComparison.Ordinal);
    }

    /// <summary>Replace une clé dans le modèle de collections.</summary>
    private static StoredObject Describe(ObjectInfo info)
    {
        var segments = info.Key.Split('/');

        // Vignette : {collection}/{enregistrement}/thumbs_{source}/{taille}.png
        if (segments.Length == 4 && segments[2].StartsWith(ThumbMarker, StringComparison.Ordinal))
        {
            return new StoredObject
            {
                Key = info.Key,
                Collection = segments[0],
                RecordId = segments[1],
                FileName = segments[2][ThumbMarker.Length..],
                Size = info.Length,
                ContentType = info.ContentType,
                IsThumb = true,
            };
        }

        // Fichier : {collection}/{enregistrement}/{fichier}
        if (segments.Length == 3)
        {
            return new StoredObject
            {
                Key = info.Key,
                Collection = segments[0],
                RecordId = segments[1],
                FileName = segments[2],
                Size = info.Length,
                ContentType = info.ContentType,
            };
        }

        // Hors modèle : déposé à la main, ou vestige d'une disposition antérieure. Il est montré
        // tel quel plutôt que masqué — c'est précisément ce qu'on cherche quand un seau grossit
        // sans raison.
        return new StoredObject
        {
            Key = info.Key,
            FileName = segments[^1],
            Size = info.Length,
            ContentType = info.ContentType,
            Orphan = true,
        };
    }

    /// <summary>
    /// Marque les objets que plus aucun enregistrement ne référence.
    /// </summary>
    /// <remarks>
    /// Une requête par collection, jamais une par objet : les identifiants concernés sont réunis en
    /// un seul filtre. Un objet dont la collection a disparu, dont l'enregistrement n'existe plus,
    /// ou dont le nom n'apparaît dans aucun champ fichier de cet enregistrement est orphelin. Les
    /// vignettes suivent le sort de leur source : elles se régénèrent, donc les garder après la
    /// disparition de l'image n'a aucun sens.
    /// </remarks>
    private static async Task<List<StoredObject>> MarkOrphansAsync(
        List<StoredObject> objects,
        CollectionRegistry registry,
        RecordService records,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var known = registry.All().ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entry in objects)
        {
            if (entry.Collection.Length == 0 || entry.RecordId.Length == 0) continue;
            if (!known.ContainsKey(entry.Collection)) continue;

            if (!wanted.TryGetValue(entry.Collection, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                wanted[entry.Collection] = ids;
            }

            ids.Add(entry.RecordId);
        }

        // Clé « collection/enregistrement » → noms de fichiers réellement référencés.
        var referenced = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (name, ids) in wanted)
        {
            var collection = known[name];
            var fileFields = collection.Fields
                .Where(field => field.Type is FieldType.File)
                .Select(field => field.Name)
                .ToList();

            if (fileFields.Count == 0) continue;

            foreach (var batch in ids.Chunk(100))
            {
                var filter = string.Join(" || ", batch.Select(id => $"id = '{Escape(id)}'"));

                var page = await records.ListAsync(
                        name,
                        new RecordQuery
                        {
                            Filter = filter,
                            PerPage = batch.Length,
                            SkipTotal = true,
                        },
                        ContextOf(user),
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var row in page.Items)
                {
                    var id = AsText(row.GetValueOrDefault(SystemFields.Id));

                    if (id.Length == 0) continue;

                    var names = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var field in fileFields)
                    {
                        foreach (var value in AsNames(row.GetValueOrDefault(field)))
                        {
                            names.Add(value);
                        }
                    }

                    referenced[$"{name}/{id}"] = names;
                }
            }
        }

        var result = new List<StoredObject>(objects.Count);

        foreach (var entry in objects)
        {
            if (entry.Orphan)
            {
                result.Add(entry);
                continue;
            }

            var orphan = !referenced.TryGetValue($"{entry.Collection}/{entry.RecordId}", out var names)
                || !names.Contains(entry.FileName);

            result.Add(entry with { Orphan = orphan });
        }

        return result;
    }

    /// <summary>Éprouve le magasin de bout en bout, écriture comprise.</summary>
    /// <remarks>
    /// Un test qui se contenterait de lister validerait un magasin en lecture seule ou un seau dont
    /// les droits d'écriture manquent. Chaque étape mesure une capacité distincte, et l'URL signée
    /// est éprouvée <b>en la suivant réellement</b> : c'est le seul moyen de détecter un
    /// <c>PublicEndpoint</c> erroné, qui laisse l'API parfaitement saine et rend tous les liens
    /// invalides côté navigateur.
    /// </remarks>
    private static async Task<List<ProbeStep>> ProbeAsync(
        IObjectStore store,
        IHttpClientFactory httpClients,
        CancellationToken cancellationToken)
    {
        var steps = new List<ProbeStep>();
        var key = $"{DiagnosticsPrefix}/{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes($"cratebase {DateTimeOffset.UtcNow:O}");
        var written = false;

        async Task<bool> RunAsync(string name, Func<Task<string>> action)
        {
            var watch = Stopwatch.StartNew();

            try
            {
                var detail = await action().ConfigureAwait(false);

                watch.Stop();
                steps.Add(new ProbeStep
                {
                    Name = name,
                    State = detail.Length == 0 ? "skipped" : "ok",
                    Detail = detail,
                    Milliseconds = watch.Elapsed.TotalMilliseconds,
                });

                return true;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                watch.Stop();
                steps.Add(new ProbeStep
                {
                    Name = name,
                    State = "failed",
                    Detail = failure.Message,
                    Milliseconds = watch.Elapsed.TotalMilliseconds,
                });

                return false;
            }
        }

        try
        {
            if (!await RunAsync("Préparation du magasin", async () =>
            {
                await store.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
                return store.Name;
            }).ConfigureAwait(false))
            {
                return steps;
            }

            if (!await RunAsync("Écriture d'un objet témoin", async () =>
            {
                await using var buffer = new MemoryStream(payload, writable: false);

                await store.PutAsync(key, buffer, "text/plain", cancellationToken).ConfigureAwait(false);
                written = true;

                return $"{payload.Length} octets";
            }).ConfigureAwait(false))
            {
                return steps;
            }

            await RunAsync("Relecture", async () =>
            {
                await using var stream = await store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("L'objet vient d'être écrit mais reste introuvable.");

                using var buffer = new MemoryStream();

                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (!buffer.ToArray().AsSpan().SequenceEqual(payload))
                {
                    throw new InvalidOperationException("Les octets relus diffèrent de ceux écrits.");
                }

                return "octets identiques";
            }).ConfigureAwait(false);

            await RunAsync("Description", async () =>
            {
                var info = await store.StatAsync(key, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("L'objet n'est pas décrit par le magasin.");

                return info.Length == payload.Length
                    ? $"{info.Length} octets"
                    : throw new InvalidOperationException(
                        $"Taille annoncée {info.Length}, attendue {payload.Length}.");
            }).ConfigureAwait(false);

            await RunAsync("URL signée suivie par le navigateur", async () =>
            {
                var url = await store.PresignedGetAsync(key, ProbeLifetime, cancellationToken)
                    .ConfigureAwait(false);

                // Le disque local n'en produit pas : l'API sert l'octet elle-même, et il n'y a rien
                // à éprouver ici. L'étape est déclarée sans objet plutôt que réussie.
                if (url is null) return string.Empty;

                using var client = httpClients.CreateClient();

                client.Timeout = TimeSpan.FromSeconds(15);

                var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"{(int)response.StatusCode} sur {url.GetLeftPart(UriPartial.Path)} — "
                        + "vérifiez Cratebase:S3:PublicEndpoint, l'hôte signé doit être celui que "
                        + "le navigateur appelle.");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);

                return bytes.AsSpan().SequenceEqual(payload)
                    ? url.Host
                    : throw new InvalidOperationException("L'URL signée sert des octets différents.");
            }).ConfigureAwait(false);
        }
        finally
        {
            // Le nettoyage est dans un `finally` pour que l'objet témoin ne survive pas à une
            // annulation. Son contrôle vit dans une fonction locale et non ici : une exception levée
            // depuis une clause `finally` masquerait celle qui a provoqué la sortie.
            if (written)
            {
                await RunAsync("Suppression de l'objet témoin", RemoveProbeAsync).ConfigureAwait(false);
            }
        }

        return steps;

        async Task<string> RemoveProbeAsync()
        {
            await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);

            var info = await store.StatAsync(key, cancellationToken).ConfigureAwait(false);

            return info is null
                ? "supprimé"
                : throw new InvalidOperationException("L'objet témoin subsiste après suppression.");
        }
    }

    /// <summary>Écrit l'archive des fichiers, en flux.</summary>
    private static async Task WriteArchiveAsync(
        Stream output,
        IObjectStore store,
        string prefix,
        bool withThumbs,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        await foreach (var info in store.ListInfoAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            if (info.Key.StartsWith(DiagnosticsPrefix, StringComparison.Ordinal)) continue;

            // Les vignettes sont exclues par défaut : elles se régénèrent à la demande, donc les
            // archiver revient à archiver un cache, et à en doubler le volume.
            if (!withThumbs && IsThumbKey(info.Key)) continue;

            var source = await store.OpenReadAsync(info.Key, cancellationToken).ConfigureAwait(false);

            // Disparu entre l'inventaire et la lecture : l'archive continue plutôt que d'échouer au
            // bout d'une heure de copie sur un fichier que quelqu'un vient de supprimer.
            if (source is null) continue;

            await using (source.ConfigureAwait(false))
            {
                var entry = archive.CreateEntry(info.Key, CompressionLevel.Fastest);

                await using var target = entry.Open();

                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Contexte des lectures d'appartenance.
    /// </summary>
    /// <remarks>
    /// L'appelant lui-même, et non un principal interne fabriqué pour l'occasion : ces routes ont
    /// déjà exigé le superadministrateur, qui contourne les règles d'accès. Un fichier ne doit pas
    /// devenir orphelin parce qu'une règle de consultation masque son enregistrement — et une
    /// identité privilégiée créée ici serait un second chemin d'élévation à surveiller.
    /// </remarks>
    private static FilterRequestContext ContextOf(ICurrentUser user) => new()
    {
        Auth = user,
        Context = "storage",
        Method = "GET",
    };

    /// <summary>
    /// Appelant d'un téléchargement : l'en-tête s'il y en a un, sinon le jeton de l'URL.
    /// </summary>
    private static async Task<ICurrentUser> ResolveCallerAsync(
        HttpContext http,
        AuthTokenStore tokens,
        AuthService auth,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (user.IsAuthenticated) return user;

        var token = http.Request.Query["token"].ToString();

        if (string.IsNullOrWhiteSpace(token)) return AnonymousUser.Instance;

        var resolved = await tokens.ResolveAsync(token, cancellationToken).ConfigureAwait(false);

        if (resolved is null) return AnonymousUser.Instance;

        var record = await auth.LoadAsync(resolved.Collection, resolved.RecordId, cancellationToken)
            .ConfigureAwait(false);

        return record is null ? AnonymousUser.Instance : new ArchiveCaller(record);
    }

    /// <summary>Appelant reconstitué depuis un jeton d'URL.</summary>
    private sealed class ArchiveCaller(AuthenticatedRecord record) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public bool IsSuperuser => record.IsSuperuser;

        public RecordId? Id => record.Id;

        public string? CollectionName => record.Collection;

        public IReadOnlyCollection<string> Permissions => record.Permissions;

        public IReadOnlyDictionary<string, object?> Fields => record.Fields;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static string AsText(object? value) => value?.ToString() ?? string.Empty;

    /// <summary>Noms de fichiers portés par un champ, qu'il soit simple ou multivalué.</summary>
    private static IEnumerable<string> AsNames(object? value)
    {
        switch (value)
        {
            case null:
                yield break;

            case string single when single.Length > 0:
                yield return single;
                yield break;

            case IEnumerable<object?> many:
                foreach (var item in many)
                {
                    var text = AsText(item);

                    if (text.Length > 0) yield return text;
                }

                yield break;
        }
    }

    private static int? ReadInt(HttpContext http, string name) =>
        int.TryParse(http.Request.Query[name], CultureInfo.InvariantCulture, out var value) ? value : null;
}
