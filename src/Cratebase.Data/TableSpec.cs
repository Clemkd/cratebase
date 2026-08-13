using Cratebase.Core;

namespace Cratebase.Data;

/// <summary>
/// Description physique d'une colonne, indépendante du dialecte.
/// </summary>
/// <param name="Name">Nom de la colonne.</param>
/// <param name="Type">Type logique du champ.</param>
/// <param name="Multiple">Le champ porte-t-il plusieurs valeurs ?</param>
/// <param name="NotNull">La colonne refuse-t-elle l'absence de valeur ?</param>
/// <param name="DefaultValue">Valeur par défaut, déjà convertie au stockage.</param>
public sealed record ColumnSpec(
    string Name,
    FieldType Type,
    bool Multiple = false,
    bool NotNull = false,
    object? DefaultValue = null);

/// <summary>
/// Clé étrangère d'un champ de relation.
/// </summary>
/// <param name="Column">Colonne portant la référence.</param>
/// <param name="TargetTable">Table visée.</param>
/// <param name="CascadeDelete">
/// La suppression du parent supprime-t-elle l'enfant ? Sinon la référence est mise à vide.
/// </param>
public sealed record ForeignKeySpec(string Column, string TargetTable, bool CascadeDelete);

/// <summary>
/// Index d'une table.
/// </summary>
/// <param name="Name">Nom de l'index. Unique dans la base.</param>
/// <param name="Table">Table portée.</param>
/// <param name="Columns">Colonnes, dans l'ordre.</param>
/// <param name="Unique">L'index impose-t-il l'unicité ?</param>
public sealed record IndexSpec(
    string Name,
    string Table,
    IReadOnlyList<string> Columns,
    bool Unique = false);

/// <summary>
/// Description physique d'une table.
/// </summary>
/// <param name="Name">Nom de la table.</param>
/// <param name="Columns">Colonnes, la clé primaire comprise.</param>
/// <param name="PrimaryKey">Nom de la colonne de clé primaire.</param>
/// <param name="ForeignKeys">Clés étrangères.</param>
public sealed record TableSpec(
    string Name,
    IReadOnlyList<ColumnSpec> Columns,
    string PrimaryKey,
    IReadOnlyList<ForeignKeySpec> ForeignKeys);

/// <summary>
/// Génération du DDL.
/// </summary>
/// <remarks>
/// Séparé de <see cref="ISqlDialect"/> parce que les deux surfaces n'évoluent pas au même rythme :
/// le DDL bouge quand on ajoute un type de champ, le langage de requête quand on ajoute un
/// opérateur. Les dialectes implémentent les deux.
/// </remarks>
public interface ISchemaDdl
{
    /// <summary>
    /// Instructions à exécuter <b>avant d'ouvrir la transaction</b> d'un changement de schéma.
    /// </summary>
    /// <remarks>
    /// ⚠️ Sur SQLite, la reconstruction de table détruit la table d'origine. Si les clés étrangères
    /// sont actives à ce moment-là, <c>ON DELETE CASCADE</c> se déclenche et <b>les lignes des
    /// tables référençantes sont effacées</b> — une perte de données totale et silencieuse,
    /// provoquée par une simple modification de type de champ. Le pragma doit donc être coupé, et
    /// il ne peut pas l'être à l'intérieur d'une transaction : d'où cette séparation.
    /// </remarks>
    IReadOnlyList<string> BeforeSchemaChange { get; }

    /// <summary>Instructions à exécuter après avoir validé la transaction.</summary>
    IReadOnlyList<string> AfterSchemaChange { get; }

    /// <summary>
    /// Vérification d'intégrité à exécuter dans la transaction, avant validation. Rend une ligne
    /// par violation détectée.
    /// </summary>
    string? IntegrityCheckStatement { get; }

    /// <summary>Crée une table.</summary>
    IReadOnlyList<string> CreateTable(TableSpec table);

    /// <summary>Supprime une table.</summary>
    IReadOnlyList<string> DropTable(string table);

    /// <summary>Renomme une table.</summary>
    IReadOnlyList<string> RenameTable(string oldName, string newName);

    /// <summary>Ajoute une colonne.</summary>
    IReadOnlyList<string> AddColumn(string table, ColumnSpec column);

    /// <summary>Supprime une colonne.</summary>
    IReadOnlyList<string> DropColumn(string table, string column);

    /// <summary>Renomme une colonne.</summary>
    IReadOnlyList<string> RenameColumn(string table, string oldName, string newName);

    /// <summary>
    /// Change le type d'une colonne.
    /// </summary>
    /// <remarks>
    /// ⚠️ SQLite ne sait pas le faire : il faut reconstruire la table. La reconstruction doit
    /// <b>recréer les index</b>, sinon une contrainte d'unicité disparaît sans le moindre message
    /// et les doublons s'installent. C'est pour cela que la méthode reçoit la table cible complète
    /// et non la seule colonne.
    /// </remarks>
    IReadOnlyList<string> ChangeColumnType(TableSpec target, ColumnSpec column, IReadOnlyList<IndexSpec> indexes);

    /// <summary>Crée un index.</summary>
    IReadOnlyList<string> CreateIndex(IndexSpec index);

    /// <summary>Supprime un index.</summary>
    IReadOnlyList<string> DropIndex(string table, string indexName);
}
