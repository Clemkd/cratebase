using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cratebase.Storage;

/// <summary>
/// Construction and validation of object keys.
/// </summary>
/// <remarks>
/// <b>Security boundary.</b> A key ends up as a file path on local storage: a <c>..</c> segment or
/// an unexpected separator would be enough to read or write outside the data directory. Validation
/// is therefore strict and applied at the store's entry point, not left to the caller.
/// </remarks>
public static class ObjectKey
{
    /// <summary>Length of the random suffix appended to every imported file.</summary>
    public const int SuffixLength = 10;

    /// <summary>Maximum length of a stored file name.</summary>
    public const int MaxFileNameLength = 100;

    private const string SuffixAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Builds the key of a record file.</summary>
    public static string For(string collection, string recordId, string fileName) =>
        $"{Segment(collection)}/{Segment(recordId)}/{Segment(fileName)}";

    /// <summary>Builds the prefix of a record's objects.</summary>
    public static string PrefixFor(string collection, string recordId) =>
        $"{Segment(collection)}/{Segment(recordId)}";

    /// <summary>Builds the key of a thumbnail.</summary>
    public static string ThumbFor(string collection, string recordId, string fileName, string size) =>
        $"{Segment(collection)}/{Segment(recordId)}/thumbs_{Segment(fileName)}/{Segment(size)}";

    /// <summary>
    /// Sanitizes a submitted file name and appends a random suffix.
    /// </summary>
    /// <remarks>
    /// The suffix isn't cosmetic: two files with the same name on the same record would overwrite
    /// each other, and a guessable name would make unprotected files enumerable.
    /// </remarks>
    public static string NewFileName(string? submitted)
    {
        var name = Path.GetFileNameWithoutExtension(submitted ?? string.Empty);
        var extension = Path.GetExtension(submitted ?? string.Empty);

        var slug = Slugify(name);
        var suffix = RandomSuffix();
        var safeExtension = Slugify(extension.TrimStart('.'));

        // We cap the stem, not the whole name: the suffix and extension must survive truncation,
        // otherwise uniqueness and type get lost on very long names.
        var budget = MaxFileNameLength - suffix.Length - safeExtension.Length - 2;

        if (budget > 0 && slug.Length > budget)
        {
            slug = slug[..budget];
        }

        if (slug.Length == 0)
        {
            slug = "file";
        }

        return safeExtension.Length > 0
            ? $"{slug}_{suffix}.{safeExtension}"
            : $"{slug}_{suffix}";
    }

    /// <summary>Validates a key segment.</summary>
    /// <exception cref="ArgumentException">The segment is empty or contains a forbidden character.</exception>
    public static string Segment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A key segment cannot be empty.", nameof(value));
        }

        if (value is ".." or "."
            || value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid key segment: \"{value}\".", nameof(value));
        }

        return value;
    }

    /// <summary>Validates a full key.</summary>
    public static string Validate(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A key cannot be empty.", nameof(key));
        }

        foreach (var segment in key.Split('/'))
        {
            Segment(segment);
        }

        return key;
    }

    /// <summary>Guesses the MIME type from the extension.</summary>
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
