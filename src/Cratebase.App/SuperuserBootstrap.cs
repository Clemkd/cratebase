using Cratebase.Auth;

namespace Cratebase.App;

/// <summary>
/// Création du premier superadministrateur au démarrage.
/// </summary>
/// <remarks>
/// <para>
/// Une base fraîchement créée n'a aucun compte, et toutes les collections système sont verrouillées :
/// sans amorçage, l'installation n'est administrable par personne.
/// </para>
/// <para>
/// L'amorçage ne s'applique <b>que si aucun superadministrateur n'existe</b>. Sans cette garde, un
/// mot de passe laissé dans les variables d'environnement réinitialiserait le compte à chaque
/// redémarrage — y compris après que l'administrateur l'a changé.
/// </para>
/// </remarks>
public static partial class SuperuserBootstrapExtensions
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Aucun superadministrateur n'existe et aucun n'est configuré. Poser " +
                  "Cratebase:Superuser:Email et Cratebase:Superuser:Password pour en créer un au " +
                  "prochain démarrage.")]
    private static partial void LogNoSuperuser(ILogger logger);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Information,
        Message = "Superadministrateur initial créé : {Email}")]
    private static partial void LogSuperuserCreated(ILogger logger, string email);

    /// <summary>Clé de configuration de l'adresse.</summary>
    public const string EmailKey = "Cratebase:Superuser:Email";

    /// <summary>Clé de configuration du mot de passe.</summary>
    public const string PasswordKey = "Cratebase:Superuser:Password";

    /// <summary>Crée le premier superadministrateur si la base n'en a aucun.</summary>
    public static async Task BootstrapSuperuserAsync(
        this IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var auth = services.GetRequiredService<AuthService>();

        if (await auth.HasSuperuserAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var email = configuration[EmailKey];
        var password = configuration[PasswordKey];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            LogNoSuperuser(logger);

            return;
        }

        await auth.UpsertSuperuserAsync(email, password, cancellationToken).ConfigureAwait(false);

        LogSuperuserCreated(logger, email);
    }
}
