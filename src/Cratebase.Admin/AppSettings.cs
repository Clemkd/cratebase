using Cratebase.Core;

namespace Cratebase.Admin;

/// <summary>
/// Réglages du journal.
/// </summary>
public sealed record LogSettings
{
    /// <summary>Les requêtes de l'API sont-elles journalisées ?</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Durée de conservation, en jours. Zéro conserve sans limite.
    /// </summary>
    /// <remarks>
    /// Sept jours par défaut : assez pour instruire un incident du week-end, assez peu pour qu'une
    /// instance oubliée ne remplisse pas son disque de traces que personne ne lira.
    /// </remarks>
    public int RetentionDays { get; init; } = 7;

    /// <summary>Gravité minimale effectivement écrite.</summary>
    public LogSeverity MinLevel { get; init; } = LogSeverity.Info;

    /// <summary>
    /// L'adresse d'origine est-elle conservée ?
    /// </summary>
    /// <remarks>
    /// Réglable, et pas seulement décoratif : une adresse IP est une donnée personnelle au sens du
    /// RGPD. Une instance qui n'en a pas l'usage doit pouvoir cesser d'en collecter sans renoncer
    /// au journal tout entier.
    /// </remarks>
    public bool LogIp { get; init; } = true;
}

/// <summary>
/// Réglages de l'instance, modifiables depuis la console.
/// </summary>
/// <remarks>
/// <para>
/// Ce qui atterrit ici doit être <b>honoré par le moteur</b>. Un écran de réglages qui affiche des
/// interrupteurs sans effet est pire qu'un écran absent : il fait croire à un contrôle qui n'existe
/// pas. Les secrets — identifiants OAuth2, chaîne de connexion — restent en configuration d'hôte
/// et n'apparaissent jamais ici : une table que la console sait lire finit dans une sauvegarde.
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Nom de l'instance, affiché par la console.</summary>
    public string AppName { get; init; } = "Cratebase";

    /// <summary>URL publique de l'instance. Sert à composer les adresses de retour OAuth2.</summary>
    public string AppUrl { get; init; } = string.Empty;

    /// <summary>Réglages du journal.</summary>
    public LogSettings Logs { get; init; } = new();

    /// <summary>
    /// Valide et normalise les réglages soumis.
    /// </summary>
    /// <exception cref="CratebaseValidationException">Un réglage est hors de ses bornes.</exception>
    public AppSettings Validated()
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = AppName.Trim();
        var url = AppUrl.Trim().TrimEnd('/');

        if (name.Length == 0)
        {
            errors["appName"] = ["Le nom de l'instance est obligatoire."];
        }
        else if (name.Length > 100)
        {
            errors["appName"] = ["Le nom ne peut pas dépasser 100 caractères."];
        }

        if (url.Length > 0 &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
             parsed.Scheme is not ("http" or "https")))
        {
            errors["appUrl"] = ["L'URL doit être absolue et en http ou https, par exemple https://exemple.org."];
        }

        if (Logs.RetentionDays is < 0 or > 365)
        {
            errors["logs.retentionDays"] = ["La rétention va de 0 (illimitée) à 365 jours."];
        }

        if (errors.Count > 0)
        {
            throw new CratebaseValidationException(errors);
        }

        return this with { AppName = name, AppUrl = url };
    }
}
