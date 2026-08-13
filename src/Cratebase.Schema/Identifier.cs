using System.Text.RegularExpressions;
using Cratebase.Core;

namespace Cratebase.Schema;

/// <summary>
/// Validation des noms de collections et de champs.
/// </summary>
/// <remarks>
/// <para>
/// <b>C'est la liste blanche du DDL.</b> Un nom de collection devient un nom de table, un nom de
/// champ devient un nom de colonne, et le DDL n'admet pas de paramètres : ces noms sont donc
/// écrits dans du texte SQL. L'échappement du dialecte les protège, mais on n'accepte pas de
/// dépendre d'une seule barrière — un nom est validé <b>au moment où la collection est créée</b>,
/// pas au moment où une requête l'utilise.
/// </para>
/// <para>
/// Corollaire : la validation est stricte plutôt que permissive. Refuser un nom exotique coûte un
/// message d'erreur ; l'accepter coûte potentiellement la base.
/// </para>
/// </remarks>
public static partial class Identifier
{
    /// <summary>Longueur maximale d'un identifiant.</summary>
    /// <remarks>
    /// PostgreSQL tronque à 63 octets. Un nom plus long y serait silencieusement raccourci, donc
    /// deux collections distinctes pourraient viser la même table après migration.
    /// </remarks>
    public const int MaxLength = 63;

    // Le souligné initial est admis par la forme, mais réservé au moteur : c'est
    // CollectionValidator qui refuse une collection système déclarée par un utilisateur. Séparer
    // les deux contrôles permet aux collections système (« _superusers ») d'exister sans ouvrir
    // l'espace de noms à tout le monde.
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape { get; }

    // Mots que SQLite ou PostgreSQL réservent, et qui produiraient un DDL invalide ou, pire, une
    // requête valide au sens inattendu.
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

    /// <summary>Indique si un identifiant est acceptable.</summary>
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= MaxLength }
        && Shape.IsMatch(name)
        && !Reserved.Contains(name);

    /// <summary>
    /// Valide un identifiant, ou lève une erreur d'entrée décrivant précisément le refus.
    /// </summary>
    /// <param name="name">Nom à valider.</param>
    /// <param name="subject">Ce que le nom désigne, pour le message : « collection », « champ ».</param>
    public static void Validate(string? name, string subject)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CratebaseBadRequestException($"Le nom de {subject} est obligatoire.");
        }

        if (name.Length > MaxLength)
        {
            throw new CratebaseBadRequestException(
                $"Le nom de {subject} « {name} » dépasse {MaxLength} caractères.");
        }

        if (!Shape.IsMatch(name))
        {
            throw new CratebaseBadRequestException(
                $"Le nom de {subject} « {name} » est invalide : une lettre, puis des lettres, " +
                "chiffres ou soulignés.");
        }

        if (Reserved.Contains(name))
        {
            throw new CratebaseBadRequestException(
                $"« {name} » est un mot réservé SQL et ne peut pas nommer un(e) {subject}.");
        }
    }

    /// <summary>
    /// Indique si un nom appartient à l'espace réservé au moteur.
    /// </summary>
    /// <remarks>
    /// Les tables internes sont préfixées d'un souligné (<c>_collections</c>, <c>_migrations</c>).
    /// Comme <see cref="Shape"/> impose de commencer par une lettre, aucune collection créée par un
    /// utilisateur ne peut entrer en collision avec elles.
    /// </remarks>
    public static bool IsSystemName(string? name) => name is { Length: > 0 } && name[0] == '_';
}
