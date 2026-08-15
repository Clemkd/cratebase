using Cratebase.Core;
using Cratebase.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cratebase.Server;

/// <summary>Endpoints de diagnostic.</summary>
public static class HealthEndpoints
{
    /// <summary>Publie <c>/health</c>.</summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", (CollectionRegistry registry, CratebaseOptions options) => Results.Ok(new
        {
            status = "ok",
            engine = options.Dialect.Name,
            collections = registry.All().Count,
        }));

        return endpoints;
    }
}

/// <summary>
/// Description d'une collection telle que soumise par la console d'administration.
/// </summary>
/// <remarks>
/// Un type d'entrée distinct de <see cref="CollectionDefinition"/> : le client ne fournit ni les
/// identifiants de champ, ni les dates, ni le drapeau système. Accepter la définition complète en
/// entrée permettrait à un appelant de se déclarer collection système, donc indestructible.
/// </remarks>
public sealed record CollectionRequest
{
    /// <summary>Nom de la collection.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Nature.</summary>
    public CollectionKind Type { get; init; } = CollectionKind.Base;

    /// <summary>Champs déclarés par l'utilisateur.</summary>
    public IReadOnlyList<FieldRequest> Fields { get; init; } = [];

    /// <summary>Index déclarés.</summary>
    public IReadOnlyList<CollectionIndex> Indexes { get; init; } = [];

    /// <summary>Règles d'accès.</summary>
    public AccessRules Rules { get; init; } = AccessRules.Locked;

    /// <summary>Convertit en définition, en conservant les identifiants de champ existants.</summary>
    public CollectionDefinition ToDefinition(CollectionDefinition? existing)
    {
        var fields = Fields.Select(field =>
        {
            // ⚠️ L'identifiant renvoyé par le client fait autorité, et l'appariement par nom n'est
            // qu'un repli.
            //
            // C'est LE point qui rend le renommage possible. Apparier par nom rendrait « title »
            // → « titre » indiscernable d'une suppression suivie d'un ajout : le planificateur
            // émettrait DROP COLUMN puis ADD COLUMN, et la colonne repartirait vide. La console
            // renvoie donc les identifiants qu'elle a reçus, et un champ sans identifiant est un
            // champ réellement nouveau.
            var id = field.Id ?? existing?.Field(field.Name)?.Id ?? RecordId.New();

            return field.ToDefinition(id);
        }).ToList();

        return new CollectionDefinition
        {
            Id = existing?.Id ?? RecordId.New(),
            Name = Name,
            Kind = Type,
            Fields = fields,
            Indexes = Indexes,
            Rules = Rules,
            IsSystem = false,
        };
    }
}

/// <summary>Champ tel que soumis.</summary>
public sealed record FieldRequest
{
    /// <summary>
    /// Identifiant du champ. Absent pour un champ nouveau, renvoyé tel quel pour un champ existant
    /// — c'est ce qui permet de distinguer un renommage d'un remplacement.
    /// </summary>
    public RecordId? Id { get; init; }

    /// <summary>Nom.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Type logique.</summary>
    public FieldType Type { get; init; } = FieldType.Text;

    /// <summary>Valeur obligatoire ?</summary>
    public bool Required { get; init; }

    /// <summary>Nombre maximal de valeurs.</summary>
    public int MaxSelect { get; init; } = 1;

    /// <summary>Options propres au type.</summary>
    public FieldOptions Options { get; init; } = FieldOptions.None;

    /// <summary>Convertit en définition.</summary>
    public FieldDefinition ToDefinition(RecordId id) => new()
    {
        Id = id,
        Name = Name,
        Type = Type,
        Required = Required,
        MaxSelect = MaxSelect,
        Options = Options,
    };
}

/// <summary>
/// Endpoints d'administration des collections.
/// </summary>
/// <remarks>
/// Tous réservés au super-admin : créer une collection, c'est créer une table.
/// </remarks>
public static class CollectionEndpoints
{
    /// <summary>Publie <c>/collections</c>.</summary>
    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/collections");

        group.MapGet("/", (CollectionRegistry registry, ICurrentUser user) =>
        {
            RequireSuperuser(user);

            return Results.Ok(new { items = registry.All() });
        });

        group.MapGet("/{nameOrId}", (string nameOrId, CollectionRegistry registry, ICurrentUser user) =>
        {
            RequireSuperuser(user);

            return Results.Ok(registry.Require(nameOrId));
        });

        group.MapPost("/", async (
            CollectionRequest request,
            CollectionRegistry registry,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            RequireSuperuser(user);
            ArgumentNullException.ThrowIfNull(request);

            var created = await registry.CreateAsync(request.ToDefinition(null), cancellationToken)
                .ConfigureAwait(false);

            return Results.Created($"/api/collections/{created.Name}", created);
        });

        group.MapPatch("/{nameOrId}", async (
            string nameOrId,
            CollectionRequest request,
            CollectionRegistry registry,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            RequireSuperuser(user);
            ArgumentNullException.ThrowIfNull(request);

            var existing = registry.Require(nameOrId);

            var updated = await registry
                .UpdateAsync(nameOrId, request.ToDefinition(existing), cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(updated);
        });

        group.MapDelete("/{nameOrId}", async (
            string nameOrId,
            CollectionRegistry registry,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            RequireSuperuser(user);

            await registry.DeleteAsync(nameOrId, cancellationToken).ConfigureAwait(false);

            return Results.NoContent();
        });

        return endpoints;
    }

    internal static void RequireSuperuser(ICurrentUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.IsSuperuser)
        {
            return;
        }

        throw user.IsAuthenticated
            ? new CratebaseForbiddenException("Réservé aux super-admins.")
            : new CratebaseUnauthenticatedException();
    }
}
