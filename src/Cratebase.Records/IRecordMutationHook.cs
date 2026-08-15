using Cratebase.Schema;

namespace Cratebase.Records;

/// <summary>
/// Intervention sur un enregistrement avant son écriture.
/// </summary>
/// <remarks>
/// <para>
/// Premier maillon du système de crochets public (l'équivalent des <c>OnRecordCreate</c> de
/// PocketBase). Il existe d'abord pour un besoin concret : sur une collection d'authentification,
/// le mot de passe soumis doit être haché avant l'écriture — or <c>password</c> est un champ
/// système, donc retiré par la validation. Sans ce point d'extension, aucun compte ne pourrait être
/// créé par l'API.
/// </para>
/// <para>
/// L'ordre est imposé : <b>validation, puis crochets, puis règle de création</b>. Les crochets
/// voient donc des données déjà validées, et la règle d'accès voit l'enregistrement tel qu'il sera
/// écrit, y compris ce que les crochets ont posé.
/// </para>
/// </remarks>
public interface IRecordMutationHook
{
    /// <summary>
    /// Intervient avant une création.
    /// </summary>
    /// <param name="collection">Collection cible.</param>
    /// <param name="data">Valeurs validées, modifiables.</param>
    /// <param name="submitted">
    /// Corps brut tel que soumis, <b>avant</b> validation. C'est le seul endroit où retrouver un
    /// champ système écarté par la validation, <c>password</c> notamment.
    /// </param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    ValueTask BeforeCreateAsync(
        CollectionDefinition collection,
        RecordData data,
        IReadOnlyDictionary<string, object?> submitted,
        CancellationToken cancellationToken);

    /// <summary>Intervient avant une modification.</summary>
    /// <param name="collection">Collection cible.</param>
    /// <param name="data">Valeurs validées, modifiables.</param>
    /// <param name="submitted">Corps brut tel que soumis.</param>
    /// <param name="original">État antérieur de l'enregistrement.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    ValueTask BeforeUpdateAsync(
        CollectionDefinition collection,
        RecordData data,
        IReadOnlyDictionary<string, object?> submitted,
        IReadOnlyDictionary<string, object?> original,
        CancellationToken cancellationToken);

    /// <summary>
    /// Intervient avant une suppression. Lever ici annule la suppression.
    /// </summary>
    /// <remarks>
    /// <para>
    /// L'enregistrement n'est <b>pas</b> chargé pour l'occasion : la suppression tient en une seule
    /// instruction, règle d'accès comprise, et lire la ligne d'abord ajouterait un aller-retour à
    /// toutes les suppressions pour le seul bénéfice des crochets qui en veulent le contenu. Un
    /// crochet qui a besoin des valeurs les lit lui-même.
    /// </para>
    /// <para>
    /// Implémentation par défaut vide : un crochet écrit pour les seules créations n'a rien à
    /// ajouter pour continuer à compiler.
    /// </para>
    /// </remarks>
    /// <param name="collection">Collection cible.</param>
    /// <param name="recordId">Identifiant visé, tel que soumis.</param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
    ValueTask BeforeDeleteAsync(
        CollectionDefinition collection,
        string recordId,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
