namespace Cratebase.Core;

/// <summary>
/// A record's identifier: a UUID version 7.
/// </summary>
/// <remarks>
/// <para>
/// PocketBase uses 15-character random identifiers. Cratebase prefers UUIDv7 for two reasons: it
/// is <b>sortable in creation order</b>, which avoids a separate date index for pagination, and it
/// is standard, so it maps to a native <c>uuid</c> on PostgreSQL rather than opaque text.
/// </para>
/// <para>
/// The identifier is always produced by the application, never by the engine: this is rule R4 of
/// the design document. An identifier produced by the database would make batch insertion depend
/// on <c>last_insert_rowid</c>, which doesn't exist on both sides.
/// </para>
/// </remarks>
public readonly record struct RecordId(Guid Value) : IComparable<RecordId>
{
    /// <summary>Empty identifier, designating no record.</summary>
    public static readonly RecordId Empty = new(Guid.Empty);

    /// <summary>Produces a new identifier, increasing over time.</summary>
    public static RecordId New() => new(Guid.CreateVersion7());

    /// <summary>Indicates whether the identifier designates no record.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>
    /// Parses the canonical form. Rejects any form other than the 36-character lowercase form with
    /// dashes: accepting variants would make the textual comparison diverge in SQLite.
    /// </summary>
    public static bool TryParse(string? raw, out RecordId id)
    {
        id = Empty;

        if (raw is not { Length: 36 } || !Guid.TryParseExact(raw, "D", out var guid))
        {
            return false;
        }

        id = new RecordId(guid);
        return true;
    }

    /// <summary>Parses the canonical form, or throws.</summary>
    public static RecordId Parse(string raw) => TryParse(raw, out var id)
        ? id
        : throw new ArgumentException($"\"{raw}\" is not a valid identifier.", nameof(raw));

    /// <inheritdoc />
    public int CompareTo(RecordId other) => Value.CompareTo(other.Value);

    /// <summary>Canonical form: 36 lowercase characters with dashes.</summary>
    public override string ToString() => Value.ToString("D");

    public static bool operator <(RecordId left, RecordId right) => left.CompareTo(right) < 0;

    public static bool operator <=(RecordId left, RecordId right) => left.CompareTo(right) <= 0;

    public static bool operator >(RecordId left, RecordId right) => left.CompareTo(right) > 0;

    public static bool operator >=(RecordId left, RecordId right) => left.CompareTo(right) >= 0;
}
