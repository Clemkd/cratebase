using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;

namespace Cratebase.Records;

/// <summary>
/// Translates an access rule into a SQL predicate.
/// </summary>
/// <remarks>
/// The single decision point: no other part of the engine should interpret a rule. A rule's three
/// states and the superuser bypass are read here, in one place.
/// </remarks>
public static class RuleGuard
{
    /// <summary>
    /// Compiles an action's rule into a predicate.
    /// </summary>
    /// <param name="collection">Target collection.</param>
    /// <param name="action">Requested action.</param>
    /// <param name="compiler">Compiler configured for the current collection and request.</param>
    /// <param name="user">Caller.</param>
    /// <param name="parameterPrefix">Parameter prefix, distinct from the client filter's.</param>
    /// <exception cref="CratebaseForbiddenException">
    /// The rule is locked and the caller is not a superuser.
    /// </exception>
    public static SqlPredicate Compile(
        CollectionDefinition collection,
        CollectionAction action,
        FilterCompiler compiler,
        ICurrentUser user,
        string parameterPrefix = "r")
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(user);

        // A superuser bypasses rules — but never validation or hooks. This is PocketBase's
        // semantics, and it's essential: without it, a badly written rule would make its own
        // collection unadministrable.
        if (user.IsSuperuser)
        {
            return SqlPredicate.Unconstrained;
        }

        var rule = collection.Rules.For(action);

        // Locked rule (null): reserved for superusers. Not to be confused with the empty string,
        // which opens to everyone — this distinction is what protects any newly created collection
        // by default.
        if (rule is null)
        {
            throw new CratebaseForbiddenException(
                $"Action \"{action}\" on \"{collection.Name}\" is reserved for superusers.");
        }

        if (rule.Length == 0)
        {
            return SqlPredicate.Unconstrained;
        }

        return compiler.Compile(rule, "t", parameterPrefix);
    }
}
