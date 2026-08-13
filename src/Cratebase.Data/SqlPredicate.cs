namespace Cratebase.Data;

/// <summary>
/// Prédicat compilé, composable <b>uniquement par conjonction</b>.
/// </summary>
/// <remarks>
/// <para>
/// C'est le type qui rend structurelle la règle du §7 de la conception : <i>une règle d'accès ne
/// peut qu'ajouter une contrainte, jamais en retirer</i>. Il n'existe pas de <c>Or</c> public sur
/// ce type, donc composer une règle et un filtre utilisateur en alternative est <b>inexprimable</b>
/// — pas seulement déconseillé.
/// </para>
/// <para>
/// La disjonction reste évidemment disponible <i>à l'intérieur</i> d'une règle, via l'opérateur
/// <c>||</c> du langage : c'est le compilateur qui l'écrit, sur un arbre déjà validé. Ce qui est
/// interdit ici, c'est de la faire apparaître entre deux prédicats d'origines différentes.
/// </para>
/// </remarks>
public readonly record struct SqlPredicate
{
    /// <summary>Prédicat qui ne contraint rien.</summary>
    public static readonly SqlPredicate Unconstrained;

    /// <summary>Prédicat qui n'admet aucune ligne.</summary>
    /// <remarks>
    /// Valeur d'une règle verrouillée pour un appelant non superadmin : la requête part quand
    /// même, et ne ramène rien. Renvoyer une liste vide plutôt qu'une erreur est la sémantique de
    /// <c>listRule</c> chez PocketBase, reprise telle quelle.
    /// </remarks>
    public static readonly SqlPredicate Denied = new(new SqlFragment("1 = 0", new Dictionary<string, object?>()));

    /// <summary>Construit un prédicat à partir d'un fragment compilé.</summary>
    public SqlPredicate(SqlFragment fragment) => Fragment = fragment;

    /// <summary>Fragment SQL et ses paramètres.</summary>
    public SqlFragment? Fragment { get; }

    /// <summary>Le prédicat laisse-t-il tout passer ?</summary>
    public bool IsUnconstrained => Fragment is null || Fragment.IsEmpty;

    /// <summary>Texte SQL, ou <c>1 = 1</c> si le prédicat ne contraint rien.</summary>
    public string Sql => IsUnconstrained ? "1 = 1" : Fragment!.Sql;

    /// <summary>Paramètres du prédicat.</summary>
    public IReadOnlyDictionary<string, object?> Parameters =>
        Fragment?.Parameters ?? new Dictionary<string, object?>();

    /// <summary>
    /// Conjonction avec un autre prédicat. <b>Seule composition offerte.</b>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Les deux prédicats partagent un nom de paramètre. C'est une erreur de programmation : chaque
    /// compilation doit recevoir son propre préfixe de paramètres.
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
                    $"Le paramètre « {name} » est déclaré par les deux prédicats composés. " +
                    "Chaque compilation doit recevoir un préfixe de paramètres distinct.");
            }
        }

        return new SqlPredicate(new SqlFragment($"({Sql}) AND ({other.Sql})", parameters));
    }
}
