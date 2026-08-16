using System.Runtime.CompilerServices;

namespace Cratebase.Storage;

/// <summary>
/// Storage on local disk. Starting implementation, for the single-container deployment.
/// </summary>
public sealed class LocalObjectStore : IObjectStore
{
    private readonly string _root;

    /// <summary>Builds a store rooted at a directory.</summary>
    public LocalObjectStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc />
    public string Name => "local";

    /// <inheritdoc />
    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task PutAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = Resolve(key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temporary file then swap atomically: an interrupted write would otherwise
        // leave a truncated file indistinguishable from a valid one, and the record would point to
        // it.
        var temporary = path + ".part";

        await using (var destination = new FileStream(
                         temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);

        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        return Task.FromResult<Stream?>(stream);
    }

    /// <inheritdoc />
    public Task<ObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);
        var info = new FileInfo(path);

        return Task.FromResult(info.Exists
            ? new ObjectInfo(key, info.Length, ObjectKey.ContentTypeOf(path))
            : null);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var path = Resolve(prefix);

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = ResolvePrefix(prefix);

        if (!Directory.Exists(path))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The file system returns the size along with the name: describing it during enumeration
    /// avoids a <c>stat</c> per object, and above all the window during which a listed file could
    /// have disappeared before being described.
    /// </remarks>
    public async IAsyncEnumerable<ObjectInfo> ListInfoAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path = ResolvePrefix(prefix);

        if (!Directory.Exists(path))
        {
            yield break;
        }

        var directory = new DirectoryInfo(path);

        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = Path.GetRelativePath(_root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');

            yield return new ObjectInfo(key, file.Length, ObjectKey.ContentTypeOf(file.Name));
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Local disk has no URL of its own: the API serves the bytes itself, after applying the view
    /// rule. Returning <see langword="null"/> here isn't a gap, it's the contract — and the caller
    /// must handle this case from the start, otherwise the switch to S3 would uncover a path that
    /// was never exercised.
    /// </remarks>
    public Task<Uri?> PresignedGetAsync(
        string key,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default) => Task.FromResult<Uri?>(null);

    /// <summary>
    /// Resolves an enumeration prefix, with the empty string meaning the whole store.
    /// </summary>
    /// <remarks>
    /// <see cref="Resolve"/> rejects an empty key, and it must keep doing so: a write or read
    /// without a key is a caller error. Enumerating without a prefix, on the other hand, is the
    /// normal way to build an inventory — these are two distinct contracts, hence two resolutions.
    /// </remarks>
    private string ResolvePrefix(string prefix) =>
        string.IsNullOrEmpty(prefix) ? _root : Resolve(prefix);

    private string Resolve(string key)
    {
        ObjectKey.Validate(key);

        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));

        // Second barrier, after segment validation: we check that the resolved path stays under the
        // root. One barrier would suffice in theory; directory escape is costly enough to deserve
        // two.
        if (!path.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Key \"{key}\" escapes the storage directory.", nameof(key));
        }

        return path;
    }
}
