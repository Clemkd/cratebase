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
/// Entry points of Cratebase as a library.
/// </summary>
/// <remarks>
/// <para>
/// This is the .NET equivalent of PocketBase's "framework" mode: an existing ASP.NET Core
/// application adds Cratebase, keeps its own endpoints and its own pipeline, and shares the same
/// caller and the same database.
/// </para>
/// <code>
/// builder.AddCratebase(o => o.UseSqlite("Data Source=./data/cratebase.db"));
/// var app = builder.Build();
/// app.MapCratebase();
/// app.MapGet("/api/report", ...);   // your endpoints, same ICurrentUser
/// </code>
/// </remarks>
public static class CratebaseExtensions
{
    /// <summary>Registers Cratebase's services.</summary>
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

        // Realtime registers like any other after-write hook: proof that the extension point is
        // sufficient, not a favor granted to an internal module.
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

        // Enums travel as strings, never as integers: an enum number shifts as soon as a value is
        // inserted in the middle, and the API contract then breaks silently. Same policy as for
        // schema snapshots (SchemaJson).
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.SerializerOptions.Converters.Add(new RecordIdJsonConverter());
        });

        services.AddExceptionHandler<CratebaseExceptionHandler>();
        services.AddProblemDetails();

        return services;
    }

    /// <summary>Registers Cratebase's services on a web application builder.</summary>
    public static WebApplicationBuilder AddCratebase(
        this WebApplicationBuilder builder,
        Action<CratebaseOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddCratebase(configure);

        return builder;
    }

    /// <summary>
    /// Prepares the database: system tables created, collections cached.
    /// </summary>
    /// <remarks>
    /// Called automatically by <see cref="MapCratebase"/>, but exposed for hosts that prefer to do
    /// it themselves — a background worker, for instance, has no endpoints.
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

        // Resolved here, not on the administration screen's first call: the displayed uptime must
        // be counted from the actual startup.
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

        // After system collections are created, never before: they must exist to be realigned like
        // the others.
        await registry.ReconcileSystemFieldsAsync(cancellationToken).ConfigureAwait(false);

        await tokens.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wires up authentication token resolution.
    /// </summary>
    /// <remarks>
    /// Call <b>before</b> <see cref="MapCratebase"/> in the pipeline: without it, every request is
    /// anonymous and locked collections become unreachable, even to the superuser.
    /// </remarks>
    public static IApplicationBuilder UseCratebaseAuthentication(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<CratebaseAuthMiddleware>();
    }

    /// <summary>
    /// Wires up logging of the API's requests.
    /// </summary>
    /// <remarks>
    /// Place <b>after</b> <see cref="UseCratebaseAuthentication"/>: that's what stores the caller in
    /// the context, and a log entry with no author doesn't say much. Has no effect if
    /// <see cref="CratebaseOptions.EnableRequestLog"/> is off, so a host that already logs by its
    /// own means has nothing to remove from its pipeline.
    /// </remarks>
    public static IApplicationBuilder UseCratebaseRequestLog(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetRequiredService<CratebaseOptions>();

        return options.EnableRequestLog ? app.UseMiddleware<CratebaseRequestLogMiddleware>() : app;
    }

    /// <summary>Publishes Cratebase's endpoints.</summary>
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
