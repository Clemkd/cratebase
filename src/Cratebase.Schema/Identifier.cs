using System.Text.RegularExpressions;
using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>
/// Validation of collection and field names.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is DDL's allow-list.</b> A collection name becomes a table name, a field name becomes a
/// column name, and DDL doesn't admit parameters: these names are therefore written into SQL text.
/// The dialect's escaping protects them, but depending on a single barrier is not acceptable — a
/// name is validated <b>when the collection is created</b>, not when a query uses it.
/// </para>
/// <para>
/// Corollary: validation is strict rather than permissive. Rejecting an exotic name costs an error
/// message; accepting it can cost the database.
/// </para>
/// </remarks>
public static partial class Identifier
{
    /// <summary>Maximum length of an identifier.</summary>
    /// <remarks>
    /// PostgreSQL truncates at 63 bytes. A longer name would be silently shortened there, so two
    /// distinct collections could end up targeting the same table after migration.
    /// </remarks>
    public const int MaxLength = 63;

    // The leading underscore is admitted by the shape, but reserved for the engine:
    // CollectionValidator is what rejects a system collection declared by a user. Separating the
    // two checks lets system collections ("_superusers") exist without opening that namespace to
    // everyone.
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape { get; }

    // Words reserved by SQLite or PostgreSQL, which would produce invalid DDL or, worse, a query
    // that is valid but means something unexpected.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "abort", "action", "add", "all", "alter", "analyze", "and", "as", "asc", "authorization",
        "between", "both", "by", "case", "cast", "check", "collate", "column", "commit",
        "constraint", "create", "cross", "current", "current_date", "current_time",
        "current_timestamp", "current_user", "default", "deferrable", "delete", "desc", "distinct",
        "do", "drop", "else", "end", "except", "exists", "false", "fetch", "for", "foreign",
        "from", "full", "grant", "group", "having", "in", "index", "initially", "inner", "insert",
        "intersect", "into", "is", "join", "lateral", "leading", "left", "like", "limit",
        "localtime", "localtimestamp", "natural", "not", "null", "offset", "on", "only", "or",
        "order", "outer", "overlaps", "placing", "primary", "references", "returning", "right",
        "select", "session_user", "similar", "some", "symmetric", "table", "then", "to",
        "trailing", "true", "union", "unique", "update", "user", "using", "values", "verbose",
        "when", "where", "window", "with",
    };

    /// <summary>Indicates whether an identifier is acceptable.</summary>
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= MaxLength }
        && Shape.IsMatch(name)
        && !Reserved.Contains(name);

    /// <summary>
    /// Validates an identifier, or throws an input error describing precisely why it was refused.
    /// </summary>
    /// <param name="name">Name to validate.</param>
    /// <param name="subject">What the name designates, for the message: "collection", "field".</param>
    public static void Validate(string? name, string subject)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CratebaseBadRequestException($"The {subject} name is required.");
        }

        if (name.Length > MaxLength)
        {
            throw new CratebaseBadRequestException(
                $"The {subject} name \"{name}\" exceeds {MaxLength} characters.");
        }

        if (!Shape.IsMatch(name))
        {
            throw new CratebaseBadRequestException(
                $"The {subject} name \"{name}\" is invalid: a letter, then letters, " +
                "digits, or underscores.");
        }

        if (Reserved.Contains(name))
        {
            throw new CratebaseBadRequestException(
                $"\"{name}\" is a reserved SQL word and cannot name a {subject}.");
        }
    }

    /// <summary>
    /// Indicates whether a name belongs to the namespace reserved for the engine.
    /// </summary>
    /// <remarks>
    /// Internal tables are prefixed with an underscore (<c>_collections</c>, <c>_migrations</c>).
    /// Since <see cref="Shape"/> requires starting with a letter, no user-created collection can
    /// collide with them.
    /// </remarks>
    public static bool IsSystemName(string? name) => name is { Length: > 0 } && name[0] == '_';
}
