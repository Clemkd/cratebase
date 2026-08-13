using Cratebase.Core;
using Cratebase.Data;

namespace Cratebase.UnitTests;

/// <summary>Horloge figée, pour rendre les macros de date déterministes.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;

    /// <summary>13 août 2026, 14 h 05 min 09,123 s UTC.</summary>
    public static FixedClock Default =>
        new(new DateTimeOffset(2026, 8, 13, 14, 5, 9, 123, TimeSpan.Zero));
}

/// <summary>Schéma de test : une collection « posts » aux champs représentatifs.</summary>
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

        // Le double de test ne traverse pas les relations : un chemin à plusieurs segments est
        // refusé, exactement comme le ferait le résolveur réel pour un champ inconnu.
        return segments.Count == 1 && _fields.TryGetValue(segments[0], out field!);
    }
}

/// <summary>Appelant authentifié, pour les règles portant sur <c>@request.auth.*</c>.</summary>
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
            ["email"] = "a@exemple.fr",
            ["verified"] = true,
            ["role"] = "editor",
        };
}
