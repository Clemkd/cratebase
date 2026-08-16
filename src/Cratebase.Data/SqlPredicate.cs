namespace Cratebase.Data;

/// <summary>
/// Compiled predicate, composable <b>only by conjunction</b>.
/// </summary>
/// <remarks>
/// <para>
/// This is the type that makes the design document's §7 rule structural: <i>an access rule can
/// only add a constraint, never remove one</i>. There is no public <c>Or</c> on this type, so
/// composing a rule and a user filter as alternatives is <b>unexpressible</b> — not merely
/// discouraged.
/// </para>
/// <para>
/// Disjunction obviously remains available <i>inside</i> a rule, via the language's <c>||</c>
/// operator: the compiler writes it, on an already-validated tree. What is forbidden here is
/// making it appear between two predicates from different origins.
/// </para>
/// </remarks>
public readonly record struct SqlPredicate
{
    /// <summary>Predicate that constrains nothing.</summary>
    public static readonly SqlPredicate Unconstrained;

    /// <summary>Predicate that admits no row.</summary>
    /// <remarks>
    /// Value of a locked rule for a non-superuser caller: the query still runs, and returns
    /// nothing. Returning an empty list rather than an error is PocketBase's <c>listRule</c>
    /// semantics, kept as-is.
    /// </remarks>
    public static readonly SqlPredicate Denied = new(new SqlFragment("1 = 0", new Dictionary<string, object?>()));

    /// <summary>Builds a predicate from a compiled fragment.</summary>
    public SqlPredicate(SqlFragment fragment) => Fragment = fragment;

    /// <summary>SQL fragment and its parameters.</summary>
    public SqlFragment? Fragment { get; }

    /// <summary>Does the predicate let everything through?</summary>
    public bool IsUnconstrained => Fragment is null || Fragment.IsEmpty;

    /// <summary>SQL text, or <c>1 = 1</c> if the predicate constrains nothing.</summary>
    public string Sql => IsUnconstrained ? "1 = 1" : Fragment!.Sql;

    /// <summary>Predicate parameters.</summary>
    public IReadOnlyDictionary<string, object?> Parameters =>
        Fragment?.Parameters ?? new Dictionary<string, object?>();

    /// <summary>
    /// Conjunction with another predicate. <b>The only composition offered.</b>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Both predicates share a parameter name. This is a programming error: each compilation must
    /// receive its own parameter prefix.
    /// </exception>
    public SqlPredicate And(SqlPredicate other)
    {
        if (IsUnconstrained)
        {
            return other;
        }

        if (other.IsUnconstrained)
        {
            return this;
        }

        var parameters = new Dictionary<string, object?>(Parameters);

        foreach (var (name, value) in other.Parameters)
        {
            if (!parameters.TryAdd(name, value))
            {
                throw new InvalidOperationException(
                    $"Parameter \"{name}\" is declared by both composed predicates. " +
                    "Each compilation must receive a distinct parameter prefix.");
            }
        }

        return new SqlPredicate(new SqlFragment($"({Sql}) AND ({other.Sql})", parameters));
    }
}
