using System.Globalization;

namespace Cratebase.Core;

/// <summary>
/// Normalization of instants. A critical portability point between SQLite and PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// SQLite has no date type: it compares instants <b>as strings</b>. PostgreSQL compares them
/// temporally as <c>timestamptz</c>. The two only give the same result if the textual
/// representation is strictly canonical: UTC, fixed precision, zero-padded, constant suffix. A
/// single date written in local time, or with variable precision, is enough to make a range filter
/// diverge between the two engines — without the slightest error message.
/// </para>
/// <para>
/// Normalization is therefore applied by the type mapper on write, and never left to the caller.
/// The chosen format sorts lexicographically in the same order as chronologically, which is the
/// condition for <c>ORDER BY</c> to be identical on both sides.
/// </para>
/// </remarks>
public static class Timestamp
{
    /// <summary>
    /// Canonical format: <c>2026-08-13T14:05:09.123Z</c>. Fixed length of 24 characters.
    /// </summary>
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Length of a canonical value, in characters.</summary>
    public const int Length = 24;

    /// <summary>
    /// Converts an instant to its canonical UTC form.
    /// </summary>
    public static string Normalize(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>
    /// Converts an instant to its canonical UTC form. A <see cref="DateTimeKind.Unspecified"/> is
    /// interpreted as UTC — the engine never stores a local time, so a date with no timezone can
    /// only come from an already-normalized value.
    /// </summary>
    public static string Normalize(DateTime value) => Normalize(value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value, TimeSpan.Zero),
        DateTimeKind.Local => new DateTimeOffset(value),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero),
    });

    /// <summary>
    /// Parses a value supplied by a client. Accepts any ISO-8601 form recognized by
    /// <see cref="DateTimeOffset"/>, and returns the canonical form.
    /// </summary>
    /// <returns><see langword="true"/> if the value is a recognizable instant.</returns>
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
    /// Reads back a canonical value coming from storage.
    /// </summary>
    public static DateTimeOffset Parse(string canonical) => DateTimeOffset.ParseExact(
        canonical,
        Format,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>
    /// Reads back a canonical value coming from storage, tolerating the empty string that
    /// represents the absence of a date.
    /// </summary>
    public static DateTimeOffset? ParseOrNull(string? canonical) =>
        string.IsNullOrEmpty(canonical) ? null : Parse(canonical);
}
