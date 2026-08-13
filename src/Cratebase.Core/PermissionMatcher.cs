namespace Cratebase.Core;

/// <summary>
/// Comparaison d'une permission détenue à une permission exigée, avec jokers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Le joker ne couvre qu'un seul niveau.</b> <c>posts.*</c> couvre <c>posts.write</c> mais pas
/// <c>posts.comments.moderate</c>. C'est le choix de <c>instacontent</c>, repris tel quel : un
/// joker récursif fait qu'accorder <c>posts.*</c> aujourd'hui accorde silencieusement, demain, une
/// permission plus sensible ajoutée sous cette racine. Personne ne revoit les rôles à ce
/// moment-là.
/// </para>
/// <para>
/// ⚠️ Ce matcher a un jumeau TypeScript dans <c>@cratebase/client</c>, qui sert à masquer
/// l'interface. Les deux partagent un jeu de cas de test commun (<c>permission-cases.json</c>) :
/// une divergence entre les deux fait qu'un bouton s'affiche alors que l'API refusera, ou
/// l'inverse. Toute modification ici se répercute là-bas, et le jeu de cas le prouve.
/// </para>
/// </remarks>
public static class PermissionMatcher
{
    private const char Separator = '.';
    private const string Wildcard = "*";

    /// <summary>
    /// Indique si l'ensemble des permissions détenues satisfait la permission exigée.
    /// </summary>
    public static bool IsSatisfied(IReadOnlyCollection<string> held, string required)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentException.ThrowIfNullOrWhiteSpace(required);

        foreach (var candidate in held)
        {
            if (Covers(candidate, required))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Indique si <paramref name="held"/> couvre <paramref name="required"/>.
    /// </summary>
    public static bool Covers(string? held, string required)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(required);

        if (string.IsNullOrWhiteSpace(held))
        {
            return false;
        }

        if (string.Equals(held, required, StringComparison.Ordinal))
        {
            return true;
        }

        // Le joker terminal couvre exactement un segment de plus, jamais davantage.
        if (!held.EndsWith(Separator + Wildcard, StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = held.AsSpan(0, held.Length - 1);            // « posts.* » → « posts. »
        var target = required.AsSpan();

        if (!target.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = target[prefix.Length..];

        return remainder.Length > 0 && !remainder.Contains(Separator);
    }
}
