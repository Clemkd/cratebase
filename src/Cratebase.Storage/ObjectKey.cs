using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cratebase.Storage;

/// <summary>
/// Construction et validation des clés d'objets.
/// </summary>
/// <remarks>
/// <b>Frontière de sécurité.</b> Une clé finit en chemin de fichier sur le stockage local : un
/// segment <c>..</c> ou un séparateur inattendu suffirait à lire ou écrire hors du répertoire de
/// données. La validation est donc stricte et appliquée à l'entrée du magasin, pas à l'appelant.
/// </remarks>
public static class ObjectKey
{
    /// <summary>Longueur du suffixe aléatoire ajouté à chaque fichier importé.</summary>
    public const int SuffixLength = 10;

    /// <summary>Longueur maximale d'un nom de fichier stocké.</summary>
    public const int MaxFileNameLength = 100;

    private const string SuffixAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Construit la clé d'un fichier d'enregistrement.</summary>
    public static string For(string collection, string recordId, string fileName) =>
        $"{Segment(collection)}/{Segment(recordId)}/{Segment(fileName)}";

    /// <summary>Construit le préfixe des objets d'un enregistrement.</summary>
    public static string PrefixFor(string collection, string recordId) =>
        $"{Segment(collection)}/{Segment(recordId)}";

    /// <summary>Construit la clé d'une vignette.</summary>
    public static string ThumbFor(string collection, string recordId, string fileName, string size) =>
        $"{Segment(collection)}/{Segment(recordId)}/thumbs_{Segment(fileName)}/{Segment(size)}";

    /// <summary>
    /// Assainit un nom de fichier soumis et lui ajoute un suffixe aléatoire.
    /// </summary>
    /// <remarks>
    /// Le suffixe n'est pas cosmétique : deux fichiers de même nom sur le même enregistrement
    /// s'écraseraient, et un nom devinable rendrait les fichiers non protégés énumérables.
    /// </remarks>
    public static string NewFileName(string? submitted)
    {
        var name = Path.GetFileNameWithoutExtension(submitted ?? string.Empty);
        var extension = Path.GetExtension(submitted ?? string.Empty);

        var slug = Slugify(name);
        var suffix = RandomSuffix();
        var safeExtension = Slugify(extension.TrimStart('.'));

        // On borne le radical, pas l'ensemble : le suffixe et l'extension doivent survivre à la
        // troncature, sinon l'unicité et le type se perdent sur les noms très longs.
        var budget = MaxFileNameLength - suffix.Length - safeExtension.Length - 2;

        if (budget > 0 && slug.Length > budget)
        {
            slug = slug[..budget];
        }

        if (slug.Length == 0)
        {
            slug = "fichier";
        }

        return safeExtension.Length > 0
            ? $"{slug}_{suffix}.{safeExtension}"
            : $"{slug}_{suffix}";
    }

    /// <summary>Valide un segment de clé.</summary>
    /// <exception cref="ArgumentException">Le segment est vide ou contient un caractère interdit.</exception>
    public static string Segment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Un segment de clé ne peut pas être vide.", nameof(value));
        }

        if (value is ".." or "."
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Segment de clé invalide : « {value} ».", nameof(value));
        }

        return value;
    }

    /// <summary>Valide une clé complète.</summary>
    public static string Validate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Une clé ne peut pas être vide.", nameof(key));
        }

        foreach (var segment in key.Split('/'))
        {
            Segment(segment);
        }

        return key;
    }

    /// <summary>Devine le type MIME depuis l'extension.</summary>
    public static string ContentTypeOf(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".json" => "application/json",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream",
        };

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var current in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(current) is UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(current))
            {
                builder.Append(char.ToLowerInvariant(current));
            }
            else if (current is '-' or '_' or ' ' or '.')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().Trim('_');
    }

    private static string RandomSuffix()
    {
        Span<char> buffer = stackalloc char[SuffixLength];

        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = SuffixAlphabet[RandomNumberGenerator.GetInt32(SuffixAlphabet.Length)];
        }

        return new string(buffer);
    }
}
