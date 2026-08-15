using System.Text.Json.Serialization;
using Cratebase.Admin;
using Cratebase.Auth;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Realtime;
using Cratebase.Records;
using Cratebase.Schema;
using Cratebase.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cratebase.Server;

/// <summary>
/// Points d'entrée de Cratebase en tant que librairie.
/// </summary>
/// <remarks>
/// <para>
/// C'est l'équivalent .NET du mode « framework » de PocketBase : une application ASP.NET Core
/// existante ajoute Cratebase, garde ses propres endpoints, son propre pipeline, et partage le
/// même appelant et la même base.
/// </para>
/// <code>
/// builder.AddCratebase(o => o.UseSqlite("Data Source=./data/cratebase.db"));
/// var app = builder.Build();
/// app.MapCratebase();
/// app.MapGet("/api/rapport", ...);   // vos endpoints, même ICurrentUser
/// </code>
/// </remarks>
public static class CratebaseExtensions
{
    /// <summary>Enregistre les services de Cratebase.</summary>
    public static IServiceCollection AddCratebase(
        this IServiceCollection services,
        Action<CratebaseOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CratebaseOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.AddHttpContextAccessor();

        services.AddSingleton<IDbConnectionFactory>(_ =>
            new DbConnectionFactory(options.Dialect, options.Ddl, options.ConnectionString));

        services.AddSingleton<SchemaStore>();
        services.AddSingleton<CollectionValidator>(provider =>
            new CollectionValidator(options.Dialect, provider.GetRequiredService<IClock>()));
        services.AddSingleton<CollectionRegistry>();
        services.AddSingleton<RecordService>();

        services.AddSingleton<AuthTokenStore>();
        services.AddSingleton<MfaService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<GrantService>();
        services.AddSingleton<OAuth2Service>();
        services.AddHttpClient("cratebase-oauth2", client =>
            client.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IRecordMutationHook, AuthRecordHook>();

        // Le temps réel s'enregistre comme n'importe quel crochet d'après-écriture : c'est la
        // preuve que le point d'extension suffit, et non une faveur faite à un module interne.
        services.TryAddSingleton<IRealtimeTransport>(_ => new InMemoryRealtimeTransport());
        services.AddSingleton<RealtimeHub>();
        services.AddSingleton<IRecordMutationHook, RealtimeRecordHook>();
        services.AddHostedService<RealtimeDispatcher>();
        services.AddSingleton<IObjectStore>(_ => options.ObjectStoreFactory());

        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        services.AddSingleton<LogStore>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<InstanceDescriptor>();

        if (options.EnableRequestLog)
        {
            services.AddHostedService<LogMaintenanceService>();
        }

        // Les énumérations circulent en chaînes, jamais en entiers : un numéro d'énumération se
        // décale dès qu'on insère une valeur au milieu, et le contrat d'API bascule alors en
        // silence. Même politique que pour les instantanés de schéma (SchemaJson).
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.SerializerOptions.Converters.Add(new RecordIdJsonConverter());
        });

        services.AddExceptionHandler<CratebaseExceptionHandler>();
        services.AddProblemDetails();

        return services;
    }

    /// <summary>Enregistre les services de Cratebase sur un constructeur d'application web.</summary>
    public static WebApplicationBuilder AddCratebase(
        this WebApplicationBuilder builder,
        Action<CratebaseOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddCratebase(configure);

        return builder;
    }

    /// <summary>
    /// Prépare la base : tables système créées, collections chargées en cache.
    /// </summary>
    /// <remarks>
    /// Appelé automatiquement par <see cref="MapCratebase"/>, mais exposé pour les hôtes qui
    /// préfèrent le faire eux-mêmes — un travailleur de fond, par exemple, n'a pas d'endpoints.
    /// </remarks>
    public static async Task InitializeCratebaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetRequiredService<CratebaseOptions>();

        Directory.CreateDirectory(options.DataDirectory);

        await services.GetRequiredService<IObjectStore>()
            .EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var store = services.GetRequiredService<SchemaStore>();
        await store.EnsureSystemTablesAsync(cancellationToken).ConfigureAwait(false);

        // Résolu ici, et pas au premier appel de l'écran d'administration : la durée de
        // fonctionnement affichée doit se compter depuis le démarrage réel.
        services.GetRequiredService<InstanceDescriptor>();

        await services.GetRequiredService<LogStore>()
            .EnsureTableAsync(cancellationToken).ConfigureAwait(false);

        var settings = services.GetRequiredService<SettingsStore>();
        await settings.EnsureTableAsync(cancellationToken).ConfigureAwait(false);
        await settings.LoadAsync(cancellationToken).ConfigureAwait(false);

        var tokens = services.GetRequiredService<AuthTokenStore>();
        await tokens.EnsureTableAsync(cancellationToken).ConfigureAwait(false);

        await services.GetRequiredService<MfaService>()
            .EnsureTablesAsync(cancellationToken).ConfigureAwait(false);

        await services.GetRequiredService<OAuth2Service>()
            .EnsureTableAsync(cancellationToken).ConfigureAwait(false);

        var registry = services.GetRequiredService<CollectionRegistry>();
        await registry.ReloadAsync(cancellationToken).ConfigureAwait(false);

        await SystemCollections.EnsureAsync(registry, cancellationToken).ConfigureAwait(false);

        // Après la création des collections système, jamais avant : elles doivent exister pour être
        // réalignées comme les autres.
        await registry.ReconcileSystemFieldsAsync(cancellationToken).ConfigureAwait(false);

        await tokens.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Branche la résolution du jeton d'authentification.
    /// </summary>
    /// <remarks>
    /// À appeler <b>avant</b> <see cref="MapCratebase"/> dans le pipeline : sans elle, toute
    /// requête est anonyme et les collections verrouillées deviennent inaccessibles, y compris au
    /// super-admin.
    /// </remarks>
    public static IApplicationBuilder UseCratebaseAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<CratebaseAuthMiddleware>();
    }

    /// <summary>
    /// Branche la journalisation des requêtes de l'API.
    /// </summary>
    /// <remarks>
    /// À placer <b>après</b> <see cref="UseCratebaseAuthentication"/> : c'est elle qui dépose
    /// l'appelant dans le contexte, et une entrée de journal sans auteur ne dit pas grand-chose.
    /// Reste sans effet si <see cref="CratebaseOptions.EnableRequestLog"/> est fermé, pour qu'un
    /// hôte qui journalise déjà par ses propres moyens n'ait rien à retirer de son pipeline.
    /// </remarks>
    public static IApplicationBuilder UseCratebaseRequestLog(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetRequiredService<CratebaseOptions>();

        return options.EnableRequestLog ? app.UseMiddleware<CratebaseRequestLogMiddleware>() : app;
    }

    /// <summary>Publie les endpoints de Cratebase.</summary>
    public static IEndpointRouteBuilder MapCratebase(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<CratebaseOptions>();
        var api = endpoints.MapGroup(options.ApiPrefix);

        api.MapHealthEndpoints();
        api.MapAuthEndpoints();
        api.MapCollectionEndpoints();
        api.MapRecordEndpoints();
        api.MapFileEndpoints();
        api.MapLogEndpoints();
        api.MapAdminEndpoints();
        api.MapStorageEndpoints();
        api.MapRealtimeEndpoints();

        return endpoints;
    }
}
