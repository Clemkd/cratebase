using System.Data.Common;
using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Traduction du modèle logique de Cratebase vers un moteur SQL donné.
/// </summary>
/// <remarks>
/// <para>
/// <b>C'est le seul endroit du code où du SQL propre à un moteur a le droit d'exister.</b> Règle R1
/// du document de conception : un <c>grep</c> de <c>SELECT</c>, <c>json_extract</c> ou
/// <c>strftime</c> hors des paquets <c>Cratebase.Data.*</c> doit revenir vide.
/// </para>
/// <para>
/// Toute méthode ajoutée ici doit être couverte par la suite de conformité, qui exécute les mêmes
/// assertions sur chaque implémentation. Une méthode dont les deux dialectes ne rendent pas le
/// même résultat est un bogue de portabilité, pas une différence acceptable.
/// </para>
/// </remarks>
public interface ISqlDialect
{
    /// <summary>Nom du dialecte, pour les journaux et les messages d'erreur.</summary>
    string Name { get; }

    /// <summary>Échappe un identifiant (table, colonne, alias).</summary>
    string QuoteIdentifier(string name);

    /// <summary>Type de colonne correspondant à un champ logique.</summary>
    string ColumnType(FieldType type, bool multiple);

    /// <summary>
    /// Convertit une valeur du modèle logique vers sa représentation de stockage.
    /// </summary>
    /// <remarks>
    /// Point de normalisation obligatoire : booléens en 0/1 sur SQLite, dates en forme canonique,
    /// multi-valeurs en JSON à ordre stable. Sans lui, les mêmes données produisent des résultats
    /// de filtre différents selon le moteur.
    /// </remarks>
    object? ToStorage(FieldType type, bool multiple, object? value);

    /// <summary>Convertit une valeur de stockage vers le modèle logique.</summary>
    object? FromStorage(FieldType type, bool multiple, object? value);

    /// <summary>
    /// Adapte l'emplacement réservé d'un paramètre au type de la colonne visée.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rend l'emplacement tel quel dans la plupart des cas. PostgreSQL, lui, refuse d'écrire une
    /// chaîne dans une colonne <c>jsonb</c> — <i>column is of type jsonb but expression is of type
    /// text</i> — et exige un transtypage explicite.
    /// </para>
    /// <para>
    /// Le transtypage passe par le SQL et non par le pilote, délibérément : les paramètres sont
    /// portés par des objets anonymes, dont les propriétés sont typées <c>object</c>, et les
    /// mécanismes de typage des pilotes se résolvent sur le type <i>déclaré</i>. Ils ne voient donc
    /// jamais la valeur réelle. Le SQL, lui, ne se trompe pas.
    /// </para>
    /// </remarks>
    string BindParameter(FieldType type, bool multiple, string placeholder);

    /// <summary>
    /// Rend une comparaison « contient », <b>toujours insensible à la casse</b>.
    /// </summary>
    /// <remarks>
    /// SQLite est insensible à la casse en ASCII, PostgreSQL est sensible. On aligne sur
    /// l'insensible parce que c'est le comportement observé en développement : le contraire ferait
    /// cesser la recherche de fonctionner au passage en production, sans message.
    /// </remarks>
    string LikeExpression(string valueExpression, string patternPlaceholder, bool negated);

    /// <summary>Rend une mise en minuscules.</summary>
    string Lower(string expression);

    /// <summary>Rend le nombre d'éléments d'une valeur multi-valuée.</summary>
    string ArrayLength(string expression);

    /// <summary>
    /// Rend un test « au moins un élément de la valeur multi-valuée satisfait la comparaison ».
    /// </summary>
    /// <param name="expression">Expression de la colonne multi-valuée.</param>
    /// <param name="comparison">
    /// Modèle de comparaison où <c>{0}</c> désigne l'élément courant. Exemple : <c>{0} = @p3</c>.
    /// </param>
    string AnyElementMatches(string expression, string comparison);

    /// <summary>
    /// Rend un test « tous les éléments de la valeur multi-valuée satisfont la comparaison ».
    /// </summary>
    string AllElementsMatch(string expression, string comparison);

    /// <summary>Rend une clause de pagination.</summary>
    string LimitOffset(int limit, int offset);

    /// <summary>Rend la position des <c>NULL</c> dans un tri, pour l'aligner entre moteurs.</summary>
    string OrderByNullsLast(string expression, bool descending);

    /// <summary>Ouvre une connexion.</summary>
    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Traduit une exception du fournisseur en exception métier, quand elle correspond à une
    /// violation de contrainte connue.
    /// </summary>
    /// <returns>
    /// L'exception métier correspondante, ou <see langword="null"/> si l'exception n'est pas
    /// reconnue et doit remonter telle quelle.
    /// </returns>
    CratebaseException? TranslateException(DbException exception);

    /// <summary>
    /// Instructions à exécuter à l'ouverture de chaque connexion (mode WAL, clés étrangères…).
    /// </summary>
    IReadOnlyList<string> ConnectionInitializationStatements { get; }

    /// <summary>
    /// Requête scalaire rendant la taille de la base, en octets.
    /// </summary>
    /// <remarks>
    /// Portée par le dialecte parce qu'elle n'existe pas ailleurs : SQLite la déduit de ses pages,
    /// PostgreSQL a une fonction dédiée, et les deux formes relèvent du moteur. La mesurer depuis
    /// le serveur en lisant un fichier marcherait pour SQLite et pour lui seul — l'écran
    /// d'exploitation deviendrait alors muet dès la bascule, c'est-à-dire au moment précis où la
    /// volumétrie commence à compter.
    /// </remarks>
    string DatabaseSizeQuery { get; }
}
