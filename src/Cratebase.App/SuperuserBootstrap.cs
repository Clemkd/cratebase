using Cratebase.Auth;

namespace Cratebase.App;

/// <summary>
/// Creates the first superuser at startup.
/// </summary>
/// <remarks>
/// <para>
/// A freshly created database has no accounts, and every system collection is locked: without
/// bootstrapping, the installation would be administrable by nobody.
/// </para>
/// <para>
/// Bootstrapping applies <b>only if no superuser exists</b>. Without this guard, a password left
/// in environment variables would reset the account on every restart — even after the administrator
/// has changed it.
/// </para>
/// </remarks>
public static partial class SuperuserBootstrapExtensions
{
    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "No superuser exists and none is configured. Set " +
                  "Cratebase:Superuser:Email and Cratebase:Superuser:Password to create one on " +
                  "the next startup.")]
    private static partial void LogNoSuperuser(ILogger logger);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Information,
        Message = "Initial superuser created: {Email}")]
    private static partial void LogSuperuserCreated(ILogger logger, string email);

    /// <summary>Configuration key for the address.</summary>
    public const string EmailKey = "Cratebase:Superuser:Email";

    /// <summary>Configuration key for the password.</summary>
    public const string PasswordKey = "Cratebase:Superuser:Password";

    /// <summary>Creates the first superuser if the database has none.</summary>
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
