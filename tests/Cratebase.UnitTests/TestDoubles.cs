using Cratebase.Core;
using Cratebase.Data;

namespace Cratebase.UnitTests;

/// <summary>Frozen clock, to make date macros deterministic.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;

    /// <summary>August 13, 2026, 14:05:09.123 UTC.</summary>
    public static FixedClock Default =>
        new(new DateTimeOffset(2026, 8, 13, 14, 5, 9, 123, TimeSpan.Zero));
}

/// <summary>Test schema: a "posts" collection with representative fields.</summary>
public sealed class FakeResolver : IQueryFieldResolver
{
    private readonly Dictionary<string, ResolvedField> _fields = new(StringComparer.Ordinal)
    {
        ["id"] = new ResolvedField("id", FieldType.Text, Multiple: false),
        ["title"] = new ResolvedField("title", FieldType.Text, Multiple: false),
        ["views"] = new ResolvedField("views", FieldType.Number, Multiple: false),
        ["published"] = new ResolvedField("published", FieldType.Bool, Multiple: false),
        ["releasedAt"] = new ResolvedField("released_at", FieldType.Date, Multiple: false),
        ["owner"] = new ResolvedField("owner", FieldType.Relation, Multiple: false, "users"),
        ["tags"] = new ResolvedField("tags", FieldType.Select, Multiple: true),
    };

    public string RootCollection => "posts";

    public bool TryResolve(IReadOnlyList<string> segments, out ResolvedField field)
    {
        field = null!;

        // The test double doesn't traverse relations: a multi-segment path is refused, exactly as
        // the real resolver would for an unknown field.
        return segments.Count == 1 && _fields.TryGetValue(segments[0], out field!);
    }
}

/// <summary>Authenticated caller, for rules involving <c>@request.auth.*</c>.</summary>
public sealed class FakeUser(RecordId id, params string[] permissions) : ICurrentUser
{
    public bool IsAuthenticated => true;

    public bool IsSuperuser { get; init; }

    public RecordId? Id => id;

    public string? CollectionName => "users";

    public IReadOnlyCollection<string> Permissions => permissions;

    public IReadOnlyDictionary<string, object?> Fields { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["email"] = "a@example.com",
            ["verified"] = true,
            ["role"] = "editor",
        };
}
