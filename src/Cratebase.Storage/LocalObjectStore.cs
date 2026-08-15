using System.Runtime.CompilerServices;

namespace Cratebase.Storage;

/// <summary>
/// Stockage sur le disque local. Implémentation de départ, dans le conteneur unique.
/// </summary>
public sealed class LocalObjectStore : IObjectStore
{
    private readonly string _root;

    /// <summary>Construit un magasin enraciné dans un répertoire.</summary>
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

        // Écriture dans un fichier temporaire puis remplacement atomique : une écriture
        // interrompue laisserait sinon un fichier tronqué que rien ne distingue d'un fichier
        // valide, et l'enregistrement pointerait dessus.
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
    /// Le système de fichiers rend la taille en même temps que le nom : la décrire depuis
    /// l'énumération évite un <c>stat</c> par objet, et surtout la fenêtre pendant laquelle un
    /// fichier listé aurait disparu avant d'être décrit.
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
    /// Le disque local n'a pas d'URL propre : c'est l'API qui sert l'octet, après avoir appliqué la
    /// règle de consultation. Rendre <see langword="null"/> ici n'est pas une lacune, c'est le
    /// contrat — et l'appelant doit gérer ce cas dès le départ, sinon la bascule vers S3
    /// découvrirait un chemin jamais exercé.
    /// </remarks>
    public Task<Uri?> PresignedGetAsync(
        string key,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default) => Task.FromResult<Uri?>(null);

    /// <summary>
    /// Résout un préfixe d'énumération, la chaîne vide désignant le magasin entier.
    /// </summary>
    /// <remarks>
    /// <see cref="Resolve"/> refuse la clé vide, et il doit continuer de le faire : une écriture ou
    /// une lecture sans clé est une erreur d'appel. Énumérer sans préfixe, en revanche, est la façon
    /// normale de dresser un inventaire — ce sont deux contrats distincts, d'où deux résolutions.
    /// </remarks>
    private string ResolvePrefix(string prefix) =>
        string.IsNullOrEmpty(prefix) ? _root : Resolve(prefix);

    private string Resolve(string key)
    {
        ObjectKey.Validate(key);

        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));

        // Deuxième barrière, après la validation des segments : on vérifie que le chemin résolu
        // reste sous la racine. Une seule barrière suffirait en théorie ; l'évasion de répertoire
        // est assez coûteuse pour en mériter deux.
        if (!path.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new ArgumentException($"La clé « {key} » sort du répertoire de stockage.", nameof(key));
        }

        return path;
    }
}
