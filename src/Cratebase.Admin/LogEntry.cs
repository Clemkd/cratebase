using Cratebase.Core;

namespace Cratebase.Admin;

/// <summary>
/// Gravité d'une entrée de journal.
/// </summary>
/// <remarks>
/// Quatre niveaux, comme PocketBase, et pas davantage : au-delà, personne ne sait plus quel niveau
/// choisir à l'écriture, et le filtre de l'écran des journaux cesse de vouloir dire quelque chose.
/// L'ordre de déclaration <b>est</b> l'ordre de gravité — <see cref="LogSeverities.AtLeast"/> s'en
/// sert pour traduire « au moins Avertissement » en liste de valeurs.
/// </remarks>
public enum LogSeverity
{
    /// <summary>Détail de mise au point.</summary>
    Debug,

    /// <summary>Déroulement normal : une requête servie.</summary>
    Info,

    /// <summary>Anomalie imputable à l'appelant : 4xx, refus d'accès.</summary>
    Warning,

    /// <summary>Anomalie imputable au serveur : 5xx, exception non traduite.</summary>
    Error,
}

/// <summary>Opérations sur les niveaux de gravité.</summary>
public static class LogSeverities
{
    /// <summary>Tous les niveaux, du moins grave au plus grave.</summary>
    public static IReadOnlyList<LogSeverity> All { get; } =
        [LogSeverity.Debug, LogSeverity.Info, LogSeverity.Warning, LogSeverity.Error];

    /// <summary>Niveaux au moins aussi graves que celui demandé.</summary>
    public static IReadOnlyList<LogSeverity> AtLeast(LogSeverity minimum) =>
        [.. All.Where(level => level >= minimum)];

    /// <summary>Niveau correspondant à un statut HTTP.</summary>
    /// <remarks>
    /// Un 4xx est un avertissement et non une erreur : c'est le fonctionnement normal d'une API
    /// publique — un filtre mal écrit, un jeton périmé. Ranger les deux au même niveau noierait les
    /// vraies pannes sous le bruit des clients.
    /// </remarks>
    public static LogSeverity ForStatus(int status) => status switch
    {
        >= 500 => LogSeverity.Error,
        >= 400 => LogSeverity.Warning,
        _ => LogSeverity.Info,
    };
}

/// <summary>
/// Entrée du journal.
/// </summary>
/// <remarks>
/// <para>
/// Les attributs d'une requête ont leur propre colonne au lieu de vivre dans <see cref="Data"/> :
/// filtrer sur le statut ou la méthode est le geste courant de cet écran, et le faire à travers du
/// JSON exigerait une extraction propre à chaque moteur — exactement ce que la règle R1 interdit
/// hors des paquets <c>Cratebase.Data.*</c>.
/// </para>
/// <para>
/// Aucune propriété n'est nulle : une entrée d'application sans requête HTTP porte simplement des
/// chaînes vides et des zéros. Le <c>NULL</c> se propagerait dans les filtres — <c>status &lt;&gt;
/// 200</c> cesserait de ramener les lignes sans statut — pour ne rien exprimer de plus.
/// </para>
/// </remarks>
public sealed record LogEntry
{
    /// <summary>Identifiant. UUIDv7 : il trie déjà dans l'ordre de création.</summary>
    public RecordId Id { get; init; } = RecordId.New();

    /// <summary>Instant d'écriture.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Gravité.</summary>
    public LogSeverity Level { get; init; } = LogSeverity.Info;

    /// <summary>Message lisible.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Méthode HTTP, pour une entrée de requête.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>Chemin appelé, chaîne de requête comprise.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Statut renvoyé. Zéro hors requête.</summary>
    public int Status { get; init; }

    /// <summary>Durée de traitement, en millisecondes.</summary>
    public double Duration { get; init; }

    /// <summary>Collection d'authentification de l'appelant.</summary>
    public string AuthCollection { get; init; } = string.Empty;

    /// <summary>Identifiant de l'appelant.</summary>
    public string AuthId { get; init; } = string.Empty;

    /// <summary>Adresse d'origine, si sa collecte est activée.</summary>
    public string Ip { get; init; } = string.Empty;

    /// <summary>Agent utilisateur.</summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>Référent.</summary>
    public string Referer { get; init; } = string.Empty;

    /// <summary>Détails libres : message d'erreur, filtre soumis, en-têtes retenus.</summary>
    public IReadOnlyDictionary<string, object?> Data { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>Granularité de l'histogramme des journaux.</summary>
/// <remarks>
/// Trois paliers seulement, et chacun correspond à une longueur de préfixe de la forme canonique —
/// c'est ce qui rend le regroupement identique sur les deux moteurs, sans fonction de date.
/// </remarks>
public enum LogGranularity
{
    /// <summary>Un point par minute. Pour une fenêtre d'une heure.</summary>
    Minute,

    /// <summary>Un point par heure.</summary>
    Hour,

    /// <summary>Un point par jour.</summary>
    Day,
}

/// <summary>Un point de l'histogramme.</summary>
/// <param name="Bucket">Début de la tranche, en forme canonique.</param>
/// <param name="Level">Gravité comptée.</param>
/// <param name="Count">Nombre d'entrées.</param>
public sealed record LogBucket(string Bucket, LogSeverity Level, long Count);
