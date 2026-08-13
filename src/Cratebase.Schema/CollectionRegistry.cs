using System.Collections.Concurrent;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Schema;

/// <summary>
/// Source d'autorité des collections : cache en mémoire, mutations transactionnelles.
/// </summary>
/// <remarks>
/// <para>
/// Le cache évite de relire la table système à chaque requête. Il est reconstruit après toute
/// mutation.
/// </para>
/// <para>
/// ⚠️ <b>Limite connue en multi-instance :</b> une modification de collection faite sur l'instance A
/// n'invalide pas le cache de l'instance B. Tant que le transport temps réel n'est pas branché
/// (jalon 8), la propagation passe par le redémarrage. C'est acceptable parce qu'une modification
/// de schéma est une opération d'administration, pas une opération de fonctionnement — mais ça doit
/// être écrit plutôt que découvert.
/// </para>
/// </remarks>
public sealed class CollectionRegistry(
    IDbConnectionFactory connections,
    SchemaStore store,
    CollectionValidator validator,
    IClock clock)
{
    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly SchemaStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly CollectionValidator _validator = validator
        ?? throw new ArgumentNullException(nameof(validator));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private ConcurrentDictionary<string, CollectionDefinition> _byName = new(StringComparer.Ordinal);

    /// <summary>Recharge le cache depuis la base.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var collections = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        _byName = new ConcurrentDictionary<string, CollectionDefinition>(
            collections.Select(c => KeyValuePair.Create(c.Name, c)),
            StringComparer.Ordinal);
    }

    /// <summary>Toutes les collections connues.</summary>
    public IReadOnlyList<CollectionDefinition> All() => [.. _byName.Values.OrderBy(c => c.Name, StringComparer.Ordinal)];

    /// <summary>Retrouve une collection par son nom ou son identifiant.</summary>
    public CollectionDefinition? Find(string nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
        {
            return null;
        }

        if (_byName.TryGetValue(nameOrId, out var byName))
        {
            return byName;
        }

        return RecordId.TryParse(nameOrId, out var id)
            ? _byName.Values.FirstOrDefault(c => c.Id == id)
            : null;
    }

    /// <summary>Retrouve une collection, ou lève un 404.</summary>
    public CollectionDefinition Require(string nameOrId) =>
        Find(nameOrId) ?? throw new CratebaseNotFoundException(
            $"La collection « {nameOrId} » n'existe pas.");

    /// <summary>Crée une collection et sa table.</summary>
    public async Task<CollectionDefinition> CreateAsync(
        CollectionDefinition draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (Find(draft.Name) is not null)
        {
            throw new CratebaseConflictException($"La collection « {draft.Name} » existe déjà.");
        }

        var now = _clock.UtcNow;

        var collection = Normalize(draft) with
        {
            Id = draft.Id.IsEmpty ? RecordId.New() : draft.Id,
            Created = now,
            Updated = now,
        };

        _validator.Validate(collection, [.. _byName.Keys]);

        var statements = SchemaPlanner.Plan(_connections.Ddl, _connections.Dialect, null, collection);

        await ApplyAsync(statements, collection, null, cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);

        return Require(collection.Name);
    }

    /// <summary>Modifie une collection et fait évoluer sa table.</summary>
    public async Task<CollectionDefinition> UpdateAsync(
        string nameOrId,
        CollectionDefinition draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var current = Require(nameOrId);

        if (current.IsSystem)
        {
            throw new CratebaseForbiddenException(
                $"La collection système « {current.Name} » ne peut pas être modifiée.");
        }

        var updated = Normalize(draft) with
        {
            Id = current.Id,
            Created = current.Created,
            Updated = _clock.UtcNow,
        };

        _validator.Validate(updated, [.. _byName.Keys.Where(n => !string.Equals(n, current.Name, StringComparison.Ordinal))]);

        var statements = SchemaPlanner.Plan(_connections.Ddl, _connections.Dialect, current, updated);

        await ApplyAsync(statements, updated, null, cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);

        return Require(updated.Name);
    }

    /// <summary>Supprime une collection et sa table.</summary>
    public async Task DeleteAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        var current = Require(nameOrId);

        if (current.IsSystem)
        {
            throw new CratebaseForbiddenException(
                $"La collection système « {current.Name} » ne peut pas être supprimée.");
        }

        var referencing = _byName.Values
            .Where(c => c.Id != current.Id)
            .Where(c => c.Fields.Any(f =>
                f.Type is FieldType.Relation
                && string.Equals(f.Options.TargetCollection, current.Name, StringComparison.Ordinal)))
            .Select(c => c.Name)
            .ToList();

        if (referencing.Count > 0)
        {
            throw new CratebaseConflictException(
                $"La collection « {current.Name} » est référencée par : {string.Join(", ", referencing)}.");
        }

        var statements = SchemaPlanner.Plan(_connections.Ddl, _connections.Dialect, current, null);

        await ApplyAsync(statements, null, current.Id, cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Complète une définition des champs et index que son type impose.
    /// </summary>
    private static CollectionDefinition Normalize(CollectionDefinition draft)
    {
        var system = draft.Kind is CollectionKind.Auth
            ? SystemFields.ForAuth()
            : SystemFields.ForBase();

        // Les champs système passent en tête, dans l'ordre canonique, et les champs déclarés par
        // l'utilisateur qui porteraient un nom système sont écartés : sinon un champ « id » défini
        // à la main écraserait la clé primaire.
        var custom = draft.Fields
            .Where(f => !SystemFields.IsSystemField(f.Name))
            .ToList();

        var indexes = draft.Kind is CollectionKind.Auth
            ? [.. SystemFields.AuthIndexes(draft.Name), .. draft.Indexes]
            : draft.Indexes;

        return draft with
        {
            Fields = [.. system, .. custom],
            Indexes = [.. indexes.DistinctBy(i => i.Name, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Exécute un lot de DDL et enregistre la définition, dans la même transaction.
    /// </summary>
    private async Task ApplyAsync(
        IReadOnlyList<string> statements,
        CollectionDefinition? collection,
        RecordId? deleted,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Hors transaction, obligatoirement : voir ISchemaDdl.BeforeSchemaChange.
        foreach (var pragma in _connections.Ddl.BeforeSchemaChange)
        {
            await connection.ExecuteAsync(new CommandDefinition(pragma, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var statement in statements)
            {
                try
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                            statement, transaction: transaction, cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                }
                catch (System.Data.Common.DbException error)
                    when (_connections.Dialect.TranslateException(error) is { } translated)
                {
                    // Un index unique posé sur une colonne qui contient déjà des doublons est un
                    // conflit de données, pas une panne : 409 et non 500.
                    throw translated;
                }
            }

            if (collection is not null)
            {
                await _store.SaveAsync(collection, connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (deleted is { } id)
            {
                await _store.DeleteAsync(id, connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
            }

            await EnsureIntegrityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var pragma in _connections.Ddl.AfterSchemaChange)
            {
                await connection.ExecuteAsync(new CommandDefinition(pragma, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureIntegrityAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (_connections.Ddl.IntegrityCheckStatement is not { } check)
        {
            return;
        }

        // Les clés étrangères ayant été coupées le temps du lot, on vérifie avant de valider que la
        // manœuvre n'a pas laissé de référence orpheline. Sans ce contrôle, couper le pragma
        // reviendrait à désactiver l'intégrité en échange de rien.
        var violations = await connection.QueryAsync(new CommandDefinition(
                check, transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (violations.Any())
        {
            throw new CratebaseConflictException(
                "Le changement de schéma laisserait des références orphelines : annulé.");
        }
    }
}
