using System.Globalization;

namespace Cratebase.Core;

/// <summary>
/// Normalisation des instants. Point de portabilité critique entre SQLite et PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// SQLite n'a pas de type date : il compare les instants <b>en tant que chaînes</b>. PostgreSQL les
/// compare temporellement en <c>timestamptz</c>. Les deux ne donnent le même résultat que si la
/// représentation textuelle est strictement canonique : UTC, précision fixe, zéros de tête,
/// suffixe constant. Une seule date écrite en heure locale, ou avec une précision variable, suffit
/// à faire diverger un filtre de plage entre les deux moteurs — sans le moindre message.
/// </para>
/// <para>
/// La normalisation est donc appliquée par le mappeur de type à l'écriture, et jamais laissée à
/// l'appelant. Le format retenu trie lexicographiquement dans le même ordre que chronologiquement,
/// ce qui est la condition pour que <c>ORDER BY</c> soit identique des deux côtés.
/// </para>
/// </remarks>
public static class Timestamp
{
    /// <summary>
    /// Format canonique : <c>2026-08-13T14:05:09.123Z</c>. Longueur fixe de 24 caractères.
    /// </summary>
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Longueur d'une valeur canonique, en caractères.</summary>
    public const int Length = 24;

    /// <summary>
    /// Convertit un instant en sa forme canonique UTC.
    /// </summary>
    public static string Normalize(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>
    /// Convertit un instant en sa forme canonique UTC. Un <see cref="DateTimeKind.Unspecified"/>
    /// est interprété comme UTC — le moteur ne stocke jamais d'heure locale, donc une date sans
    /// fuseau ne peut venir que d'une valeur déjà normalisée.
    /// </summary>
    public static string Normalize(DateTime value) => Normalize(value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
        DateTimeKind.Local => new DateTimeOffset(value),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero),
    });

    /// <summary>
    /// Analyse une valeur fournie par un client. Accepte toute forme ISO-8601 reconnue par
    /// <see cref="DateTimeOffset"/>, et renvoie la forme canonique.
    /// </summary>
    /// <returns><see langword="true"/> si la valeur est un instant reconnaissable.</returns>
    public static bool TryNormalize(string? raw, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        canonical = Normalize(parsed);
        return true;
    }

    /// <summary>
    /// Relit une valeur canonique issue du stockage.
    /// </summary>
    public static DateTimeOffset Parse(string canonical) => DateTimeOffset.ParseExact(
        canonical,
        Format,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// Relit une valeur canonique issue du stockage, en tolérant la chaîne vide qui représente
    /// l'absence de date.
    /// </summary>
    public static DateTimeOffset? ParseOrNull(string? canonical) =>
        string.IsNullOrEmpty(canonical) ? null : Parse(canonical);
}
