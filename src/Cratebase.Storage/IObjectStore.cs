namespace Cratebase.Storage;

/// <summary>Métadonnées d'un objet stocké.</summary>
/// <param name="Key">Clé complète.</param>
/// <param name="Length">Taille en octets.</param>
/// <param name="ContentType">Type MIME.</param>
public sealed record ObjectInfo(string Key, long Length, string ContentType);

/// <summary>
/// Stockage d'objets.
/// </summary>
/// <remarks>
/// <para>
/// Une seule abstraction pour le disque local et pour S3. C'est elle qui tient la deuxième promesse
/// d'évolutivité : passer du disque à MinIO ou S3 est un changement de configuration, pas une
/// réécriture. Le reste du moteur ne connaît que des <b>clés</b>, jamais des chemins.
/// </para>
/// <para>
/// Disposition des clés : <c>{collection}/{recordId}/{fichier}</c>. Elle porte l'appartenance, donc
/// une autorisation peut se décider sur le préfixe seul, sans lire la base.
/// </para>
/// </remarks>
public interface IObjectStore
{
    /// <summary>Nom de l'implémentation, pour les diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Prépare le magasin : répertoire créé sur le disque local, seau créé côté S3.
    /// </summary>
    /// <remarks>
    /// Polymorphe plutôt que laissée à l'hôte : sinon la préparation est faite pour le magasin
    /// qu'on utilisait au moment où on y a pensé, et oubliée pour l'autre. Un seau absent ne se
    /// manifeste qu'au premier import de fichier, longtemps après le démarrage.
    /// </remarks>
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>Écrit un objet, en écrasant s'il existe.</summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Ouvre un objet en lecture, ou rend <see langword="null"/> s'il n'existe pas.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Décrit un objet, ou rend <see langword="null"/> s'il n'existe pas.</summary>
    Task<ObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Supprime un objet. Ne lève pas s'il est déjà absent.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Supprime tous les objets sous un préfixe.</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Énumère les clés sous un préfixe.</summary>
    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Produit une URL signée de lecture directe, quand le stockage en propose.
    /// </summary>
    /// <remarks>
    /// Rend <see langword="null"/> pour le stockage local, qui n'a pas d'URL propre : l'API sert
    /// alors l'octet elle-même. Les deux chemins sont donc exercés dès le départ, et la bascule
    /// vers S3 ne découvre pas un cas non couvert.
    /// </remarks>
    Task<Uri?> PresignedGetAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default);
}
