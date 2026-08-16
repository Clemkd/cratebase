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
/// A store object, placed back into the collections model.
/// </summary>
/// <remarks>
/// The store only knows keys; it's the <c>{collection}/{record}/…</c> layout that gives them
/// meaning. Doing this here rather than in <c>Cratebase.Storage</c> is deliberate: the store must
/// stay ignorant of the model, otherwise the promise "the rest of the engine only knows keys" would
/// turn against itself.
/// </remarks>
public sealed record StoredObject
{
    /// <summary>Full key.</summary>
    public required string Key { get; init; }

    /// <summary>Collection inferred from the key, or an empty string if the key falls outside the model.</summary>
    public string Collection { get; init; } = string.Empty;

    /// <summary>Record inferred from the key.</summary>
    public string RecordId { get; init; } = string.Empty;

    /// <summary>File name, or the source file's name for a thumbnail.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>MIME type inferred from the extension.</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>Thumbnail derived from an image, hence regenerable.</summary>
    public bool IsThumb { get; init; }

    /// <summary>No record references this file.</summary>
    public bool Orphan { get; init; }
}

/// <summary>
/// Operating the file store.
/// </summary>
/// <remarks>
/// <para>
/// These routes describe and inventory; they configure nothing. The store — local disk or S3,
/// bucket, endpoint, credentials — is decided by host configuration, and stays there: a console
/// that wrote a secret key to the database would let it into every backup of that database. The
/// console can therefore <b>read</b> the configuration in effect and <b>test</b> it, which is what's
/// needed when files stop showing up.
/// </para>
/// <para>
/// Reserved for the superuser: the inventory names the files of every record, including ones access
/// rules would protect individually.
/// </para>
/// </remarks>
public static class StorageEndpoints
{
    /// <summary>Prefix of diagnostic objects, excluded from the inventory and from archives.</summary>
    public const string DiagnosticsPrefix = "_diagnostics";

    /// <summary>Segment that marks a thumbnail directory.</summary>
    private const string ThumbMarker = "thumbs_";

    /// <summary>Maximum inventory page size.</summary>
    private const int MaxPerPage = 500;

    /// <summary>Lifetime of the presigned URL used by the connection test.</summary>
    private static readonly TimeSpan ProbeLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Publishes the file store routes.</summary>
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
                // Local disk has no URL of its own: the API serves the bytes itself. That's what
                // decides whether the connection test has a presigned URL to exercise.
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

            // The prefix is passed to the store when it exists: on S3, filtering server-side
            // instead of pulling the whole bucket changes the order of magnitude of the inventory.
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

            // Ownership is resolved over the whole inventory, not over the displayed page: the
            // "orphans only" filter must be able to discard rows before pagination, otherwise the
            // first page could show three and the second none.
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

        // The body is declared explicitly: a DELETE method doesn't get an inferred body, and the
        // application refuses to start if left to guess it.
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

            // A file still referenced isn't deleted from here. Removing it would leave the record
            // pointing at nothing: the console would show a broken image, and nothing in the
            // database would say who removed it or when. Removal goes through editing the record,
            // which updates the reference at the same time.
            var referenced = resolved.Where(entry => !entry.Orphan && !entry.IsThumb).ToList();

            if (referenced.Count > 0)
            {
                throw new CratebaseConflictException(
                    $"{referenced.Count} object(s) are still referenced by a record, "
                    + $"starting with \"{referenced[0].Key}\". Remove the file from "
                    + "the record: the reference will be updated at the same time.");
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
            // A download is a navigation, which carries no `Authorization` header: the token
            // therefore travels through the URL, as for protected files, and lives two minutes.
            // The log already masks the `token` parameter, otherwise the precaution would be undone
            // by the very line that records it.
            CollectionEndpoints.RequireSuperuser(
                await ResolveCallerAsync(http, tokens, auth, user, cancellationToken)
                    .ConfigureAwait(false));

            var collection = http.Request.Query["collection"].ToString();
            var withThumbs = http.Request.Query["thumbs"] == "1";
            var prefix = string.IsNullOrWhiteSpace(collection) ? string.Empty : collection + "/";

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

            // `ZipArchive` writes synchronously — there's no asynchronous archive API in the
            // standard library — and Kestrel refuses synchronous writes on the response body by
            // default. The permission is set here, on this one request: lifting it globally would
            // expose every route to the thread blocking it enables. The real cost is one thread
            // held for the duration of the download; the alternative — building the archive to a
            // temporary file first — would double disk usage and delay the first byte by the whole
            // copy's duration.
            http.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;

            // Streamed, never buffered in memory: an archive is measured in gigabytes, and building
            // it first would crash the process long before serving the first byte.
            return Results.Stream(
                stream => WriteArchiveAsync(stream, store, prefix, withThumbs, cancellationToken),
                "application/zip",
                fileDownloadName: $"files-{stamp}.zip");
        });

        return endpoints;
    }

    /// <summary>Keys to delete.</summary>
    public sealed record DeleteObjectsRequest
    {
        /// <summary>Full keys.</summary>
        public IReadOnlyList<string>? Keys { get; init; }
    }

    /// <summary>One step of the connection test.</summary>
    public sealed record ProbeStep
    {
        /// <summary>What the step tests.</summary>
        public required string Name { get; init; }

        /// <summary><c>ok</c>, <c>skipped</c>, or <c>failed</c>.</summary>
        public required string State { get; init; }

        /// <summary>Human-readable detail, or error message.</summary>
        public string Detail { get; init; } = string.Empty;

        /// <summary>Step duration, in milliseconds.</summary>
        public double Milliseconds { get; init; }
    }

    /// <summary>Does a key designate a thumbnail?</summary>
    private static bool IsThumbKey(string key)
    {
        var segments = key.Split('/');

        return segments.Length == 4
            && segments[2].StartsWith(ThumbMarker, StringComparison.Ordinal);
    }

    /// <summary>Places a key back into the collections model.</summary>
    private static StoredObject Describe(ObjectInfo info)
    {
        var segments = info.Key.Split('/');

        // Thumbnail: {collection}/{record}/thumbs_{source}/{size}.png
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

        // File: {collection}/{record}/{file}
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

        // Outside the model: dropped in by hand, or a leftover from an earlier layout. It's shown
        // as-is rather than hidden — that's exactly what one looks for when a bucket grows for no
        // reason.
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
    /// Marks the objects no record references anymore.
    /// </summary>
    /// <remarks>
    /// One query per collection, never one per object: the identifiers involved are gathered into a
    /// single filter. An object whose collection has disappeared, whose record no longer exists, or
    /// whose name doesn't appear in any file field of that record is orphaned. Thumbnails follow
    /// their source's fate: they regenerate, so keeping them after the image disappears makes no
    /// sense.
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

        // "collection/record" key → file names actually referenced.
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

    /// <summary>Exercises the store end to end, including a write.</summary>
    /// <remarks>
    /// A test that only listed would validate a read-only store, or a bucket missing write
    /// permissions. Each step measures a distinct capability, and the presigned URL is tested
    /// <b>by actually following it</b>: that's the only way to detect a wrong <c>PublicEndpoint</c>,
    /// which leaves the API perfectly healthy while making every link invalid on the browser side.
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
            if (!await RunAsync("Preparing the store", async () =>
            {
                await store.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
                return store.Name;
            }).ConfigureAwait(false))
            {
                return steps;
            }

            if (!await RunAsync("Writing a probe object", async () =>
            {
                await using var buffer = new MemoryStream(payload, writable: false);

                await store.PutAsync(key, buffer, "text/plain", cancellationToken).ConfigureAwait(false);
                written = true;

                return $"{payload.Length} bytes";
            }).ConfigureAwait(false))
            {
                return steps;
            }

            await RunAsync("Reading it back", async () =>
            {
                await using var stream = await store.OpenReadAsync(key, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The object was just written but is still not found.");

                using var buffer = new MemoryStream();

                await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (!buffer.ToArray().AsSpan().SequenceEqual(payload))
                {
                    throw new InvalidOperationException("The bytes read back differ from the ones written.");
                }

                return "identical bytes";
            }).ConfigureAwait(false);

            await RunAsync("Describing it", async () =>
            {
                var info = await store.StatAsync(key, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The object is not described by the store.");

                return info.Length == payload.Length
                    ? $"{info.Length} bytes"
                    : throw new InvalidOperationException(
                        $"Reported size {info.Length}, expected {payload.Length}.");
            }).ConfigureAwait(false);

            await RunAsync("Presigned URL followed by a browser", async () =>
            {
                var url = await store.PresignedGetAsync(key, ProbeLifetime, cancellationToken)
                    .ConfigureAwait(false);

                // Local disk doesn't produce one: the API serves the bytes itself, and there's
                // nothing to test here. The step is reported as not applicable rather than passed.
                if (url is null) return string.Empty;

                using var client = httpClients.CreateClient();

                client.Timeout = TimeSpan.FromSeconds(15);

                var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"{(int)response.StatusCode} on {url.GetLeftPart(UriPartial.Path)} — "
                        + "check Cratebase:S3:PublicEndpoint, the signed host must be the one "
                        + "the browser calls.");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);

                return bytes.AsSpan().SequenceEqual(payload)
                    ? url.Host
                    : throw new InvalidOperationException("The presigned URL serves different bytes.");
            }).ConfigureAwait(false);
        }
        finally
        {
            // Cleanup lives in a `finally` so the probe object doesn't outlive a cancellation. Its
            // logic sits in a local function rather than here: an exception raised from a `finally`
            // clause would mask the one that caused the exit.
            if (written)
            {
                await RunAsync("Deleting the probe object", RemoveProbeAsync).ConfigureAwait(false);
            }
        }

        return steps;

        async Task<string> RemoveProbeAsync()
        {
            await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);

            var info = await store.StatAsync(key, cancellationToken).ConfigureAwait(false);

            return info is null
                ? "deleted"
                : throw new InvalidOperationException("The probe object still exists after deletion.");
        }
    }

    /// <summary>Writes the files archive, streamed.</summary>
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

            // Thumbnails are excluded by default: they regenerate on demand, so archiving them
            // amounts to archiving a cache, and doubling the archive's size.
            if (!withThumbs && IsThumbKey(info.Key)) continue;

            var source = await store.OpenReadAsync(info.Key, cancellationToken).ConfigureAwait(false);

            // Disappeared between the inventory and the read: the archive carries on rather than
            // failing after an hour of copying, over a file someone just deleted.
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
    /// Context for ownership reads.
    /// </summary>
    /// <remarks>
    /// The caller itself, not an internal principal fabricated for the occasion: these routes have
    /// already required the superuser, who bypasses access rules. A file must not become orphaned
    /// because a view rule hides its record — and a privileged identity created here would be a
    /// second elevation path to watch.
    /// </remarks>
    private static FilterRequestContext ContextOf(ICurrentUser user) => new()
    {
        Auth = user,
        Context = "storage",
        Method = "GET",
    };

    /// <summary>
    /// Caller of a download: the header if there is one, otherwise the URL token.
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

    /// <summary>Caller reconstructed from a URL token.</summary>
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

    /// <summary>File names carried by a field, whether single or multi-valued.</summary>
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
