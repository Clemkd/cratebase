using Cratebase.Core;
using Cratebase.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cratebase.Server;

/// <summary>Diagnostic endpoints.</summary>
public static class HealthEndpoints
{
    /// <summary>Publishes <c>/health</c>.</summary>
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
/// Description of a collection as submitted by the administration console.
/// </summary>
/// <remarks>
/// A separate input type from <see cref="CollectionDefinition"/>: the client supplies neither field
/// identifiers, nor dates, nor the system flag. Accepting the full definition as input would let a
/// caller declare itself a system collection, hence indestructible.
/// </remarks>
public sealed record CollectionRequest
{
    /// <summary>Collection name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Kind.</summary>
    public CollectionKind Type { get; init; } = CollectionKind.Base;

    /// <summary>Fields declared by the user.</summary>
    public IReadOnlyList<FieldRequest> Fields { get; init; } = [];

    /// <summary>Declared indexes.</summary>
    public IReadOnlyList<CollectionIndex> Indexes { get; init; } = [];

    /// <summary>Access rules.</summary>
    public AccessRules Rules { get; init; } = AccessRules.Locked;

    /// <summary>Converts to a definition, preserving existing field identifiers.</summary>
    public CollectionDefinition ToDefinition(CollectionDefinition? existing)
    {
        var fields = Fields.Select(field =>
        {
            // ⚠️ The identifier returned by the client is authoritative, and matching by name is
            // only a fallback.
            //
            // This is THE point that makes renaming possible. Matching by name would make "title"
            // → "titre" indistinguishable from a deletion followed by an addition: the planner
            // would emit DROP COLUMN then ADD COLUMN, and the column would come back empty. The
            // console therefore sends back the identifiers it received, and a field with no
            // identifier is a genuinely new field.
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

/// <summary>A field as submitted.</summary>
public sealed record FieldRequest
{
    /// <summary>
    /// Field identifier. Absent for a new field, sent back as-is for an existing one — that's what
    /// distinguishes a rename from a replacement.
    /// </summary>
    public RecordId? Id { get; init; }

    /// <summary>Name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Logical type.</summary>
    public FieldType Type { get; init; } = FieldType.Text;

    /// <summary>Is a value required?</summary>
    public bool Required { get; init; }

    /// <summary>Maximum number of values.</summary>
    public int MaxSelect { get; init; } = 1;

    /// <summary>Type-specific options.</summary>
    public FieldOptions Options { get; init; } = FieldOptions.None;

    /// <summary>Converts to a definition.</summary>
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
/// Collection administration endpoints.
/// </summary>
/// <remarks>
/// All reserved for the superuser: creating a collection means creating a table.
/// </remarks>
public static class CollectionEndpoints
{
    /// <summary>Publishes <c>/collections</c>.</summary>
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
            ? new CratebaseForbiddenException("Reserved for superusers.")
            : new CratebaseUnauthenticatedException();
    }
}
