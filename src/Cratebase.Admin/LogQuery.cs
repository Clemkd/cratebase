namespace Cratebase.Admin;

/// <summary>
/// Critères de consultation du journal.
/// </summary>
/// <remarks>
/// Un jeu de critères fermé, et non le langage de filtre des collections : le journal n'est pas une
/// collection — il n'a ni schéma modifiable, ni règles d'accès — et lui ouvrir le DSL reviendrait à
/// exposer un compilateur SQL sur une table que personne ne modélise. Les cinq critères ci-dessous
/// couvrent ce qu'on cherche réellement dans un journal : quoi, quand, par qui, avec quel résultat.
/// </remarks>
public sealed record LogQuery
{
    /// <summary>Plafond de taille de page, aligné sur celui des enregistrements.</summary>
    public const int MaxPerPage = 500;

    /// <summary>Page demandée, à partir de 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Taille de page.</summary>
    public int PerPage { get; init; } = 50;

    /// <summary>
    /// Niveaux retenus. Vide ou absent : tous.
    /// </summary>
    /// <remarks>
    /// Un ensemble et non une gravité minimale. « Au moins avertissement » se dit très bien avec un
    /// ensemble ; l'inverse est faux — isoler les seuls avertissements sans les erreurs est une
    /// demande courante en exploitation, et une borne basse ne sait pas l'exprimer.
    /// </remarks>
    public IReadOnlyList<LogSeverity> Levels { get; init; } = [];

    /// <summary>Fragment cherché dans le message ou l'URL.</summary>
    public string? Search { get; init; }

    /// <summary>Méthode HTTP exacte.</summary>
    public string? Method { get; init; }

    /// <summary>Statut HTTP exact.</summary>
    public int? Status { get; init; }

    /// <summary>Borne basse de la fenêtre, incluse.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Borne haute de la fenêtre, exclue.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Tri du plus ancien au plus récent. Le défaut est l'inverse.</summary>
    public bool Ascending { get; init; }

    /// <summary>Ramène la pagination dans ses bornes.</summary>
    public LogQuery Normalized() => this with
    {
        Page = Math.Max(1, Page),
        PerPage = Math.Clamp(PerPage, 1, MaxPerPage),
    };
}
