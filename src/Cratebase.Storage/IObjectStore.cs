using System.Runtime.CompilerServices;

namespace Cratebase.Storage;

/// <summary>Metadata of a stored object.</summary>
/// <param name="Key">Full key.</param>
/// <param name="Length">Size in bytes.</param>
/// <param name="ContentType">MIME type.</param>
public sealed record ObjectInfo(string Key, long Length, string ContentType);

/// <summary>
/// Object storage.
/// </summary>
/// <remarks>
/// <para>
/// A single abstraction for local disk and for S3. This is what keeps the second scalability
/// promise: moving from disk to MinIO or S3 is a configuration change, not a rewrite. The rest of
/// the engine only ever knows about <b>keys</b>, never paths.
/// </para>
/// <para>
/// Key layout: <c>{collection}/{recordId}/{file}</c>. It carries ownership, so authorization can
/// be decided on the prefix alone, without reading the database.
/// </para>
/// </remarks>
public interface IObjectStore
{
    /// <summary>Name of the implementation, for diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Prepares the store: directory created on local disk, bucket created on S3.
    /// </summary>
    /// <remarks>
    /// Polymorphic rather than left to the host: otherwise preparation is done for whichever store
    /// was in mind at the time, and forgotten for the other. A missing bucket only shows up on the
    /// first file import, long after startup.
    /// </remarks>
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes an object, overwriting it if it already exists.</summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Opens an object for reading, or returns <see langword="null"/> if it doesn't exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Describes an object, or returns <see langword="null"/> if it doesn't exist.</summary>
    Task<ObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes an object. Does not throw if it's already absent.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes every object under a prefix.</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Enumerates the keys under a prefix. An empty string means the whole store.</summary>
    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Enumerates the objects under a prefix, including metadata.</summary>
    /// <remarks>
    /// The default implementation describes each key one at a time: correct everywhere, but costly
    /// on remote storage, where it means one network request per object. Stores whose listing
    /// already returns the size — S3 and local disk both do — override it, and an inventory of ten
    /// thousand files becomes a scan instead of ten thousand calls.
    /// </remarks>
    async IAsyncEnumerable<ObjectInfo> ListInfoAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var key in ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            var info = await StatAsync(key, cancellationToken).ConfigureAwait(false);

            if (info is not null)
            {
                yield return info;
            }
        }
    }

    /// <summary>
    /// Produces a signed direct-read URL, when the storage backend offers one.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> for local storage, which has no URL of its own: the API then
    /// serves the bytes itself. Both paths are therefore exercised from the start, and the switch to
    /// S3 doesn't uncover an untested case.
    /// </remarks>
    Task<Uri?> PresignedGetAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default);
}
