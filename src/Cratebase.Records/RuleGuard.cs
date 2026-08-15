using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;

namespace Cratebase.Records;

/// <summary>
/// Traduit une règle d'accès en prédicat SQL.
/// </summary>
/// <remarks>
/// Point unique de décision : aucune autre partie du moteur ne doit interpréter une règle. Les
/// trois états d'une règle et le contournement superadmin se lisent ici, en un seul endroit.
/// </remarks>
public static class RuleGuard
{
    /// <summary>
    /// Compile la règle d'une action en prédicat.
    /// </summary>
    /// <param name="collection">Collection visée.</param>
    /// <param name="action">Action demandée.</param>
    /// <param name="compiler">Compilateur configuré pour la collection et la requête courantes.</param>
    /// <param name="user">Appelant.</param>
    /// <param name="parameterPrefix">Préfixe des paramètres, distinct de celui du filtre client.</param>
    /// <exception cref="CratebaseForbiddenException">
    /// La règle est verrouillée et l'appelant n'est pas superadmin.
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

        // Un superadmin traverse les règles — mais jamais la validation ni les hooks. C'est la
        // sémantique de PocketBase, et elle est indispensable : sans elle, une règle mal écrite
        // rendrait sa propre collection inadministrable.
        if (user.IsSuperuser)
        {
            return SqlPredicate.Unconstrained;
        }

        var rule = collection.Rules.For(action);

        // Règle verrouillée (null) : réservée au superadmin. À ne surtout pas confondre avec la
        // chaîne vide, qui ouvre à tout le monde — c'est la distinction qui protège par défaut
        // toute collection nouvellement créée.
        if (rule is null)
        {
            throw new CratebaseForbiddenException(
                $"L'action « {action} » sur « {collection.Name} » est réservée aux super-admins.");
        }

        if (rule.Length == 0)
        {
            return SqlPredicate.Unconstrained;
        }

        return compiler.Compile(rule, "t", parameterPrefix);
    }
}
