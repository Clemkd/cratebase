using System.Collections.Concurrent;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Schema;

/// <summary>
/// Authoritative source for collections: in-memory cache, transactional mutations.
/// </summary>
/// <remarks>
/// <para>
/// The cache avoids re-reading the system table on every request. It is rebuilt after every
/// mutation.
/// </para>
/// <para>
/// ⚠️ <b>Known limitation in multi-instance deployments:</b> a collection change made on instance A
/// does not invalidate instance B's cache. Until the realtime transport is wired up (milestone 8),
/// propagation happens via restart. This is acceptable because a schema change is an
/// administration operation, not an operational one — but it should be written down rather than
/// discovered.
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

    /// <summary>Reloads the cache from the database.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var collections = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        _byName = new ConcurrentDictionary<string, CollectionDefinition>(
            collections.Select(c => KeyValuePair.Create(c.Name, c)),
            StringComparer.Ordinal);
    }

    /// <summary>Every known collection.</summary>
    public IReadOnlyList<CollectionDefinition> All() => [.. _byName.Values.OrderBy(c => c.Name, StringComparer.Ordinal)];

    /// <summary>Finds a collection by its name or identifier.</summary>
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

    /// <summary>Finds a collection, or throws a 404.</summary>
    public CollectionDefinition Require(string nameOrId) =>
        Find(nameOrId) ?? throw new CratebaseNotFoundException(
            $"Collection \"{nameOrId}\" does not exist.");

    /// <summary>Creates a collection and its table.</summary>
    public async Task<CollectionDefinition> CreateAsync(
        CollectionDefinition draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (Find(draft.Name) is not null)
        {
            throw new CratebaseConflictException($"Collection \"{draft.Name}\" already exists.");
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

    /// <summary>Modifies a collection and evolves its table.</summary>
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
                $"System collection \"{current.Name}\" cannot be modified.");
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

    /// <summary>Deletes a collection and its table.</summary>
    public async Task DeleteAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        var current = Require(nameOrId);

        if (current.IsSystem)
        {
            throw new CratebaseForbiddenException(
                $"System collection \"{current.Name}\" cannot be deleted.");
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
                $"Collection \"{current.Name}\" is referenced by: {string.Join(", ", referencing)}.");
        }

        var statements = SchemaPlanner.Plan(_connections.Ddl, _connections.Dialect, current, null);

        await ApplyAsync(statements, null, current.Id, cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Realigns existing collections with the current definition of system fields.
    /// </summary>
    /// <remarks>
    /// Called at startup. Renaming a system field in code alone would not be enough: the stored
    /// definition would still carry the old name, and the engine would look for a column that no
    /// longer exists. The planner recognizes the rename by the field's identifier, so the column
    /// is renamed and the data stays in place.
    ///
    /// With nothing to reconcile, the plan is empty and nothing executes: that's what lets it be
    /// called on every startup without a second thought.
    /// </remarks>
    public async Task ReconcileSystemFieldsAsync(CancellationToken cancellationToken = default)
    {
        var reconciled = false;

        foreach (var stored in All())
        {
            var aligned = WithoutStaleIndexes(Normalize(stored)) with { Updated = _clock.UtcNow };
            var statements = SchemaPlanner.Plan(_connections.Ddl, _connections.Dialect, stored, aligned);

            if (statements.Count == 0) continue;

            await ApplyAsync(statements, aligned, null, cancellationToken).ConfigureAwait(false);
            reconciled = true;
        }

        if (reconciled)
        {
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drops indexes that designate a column absent from the definition.
    /// </summary>
    /// <remarks>
    /// Reserved for realignment, and not applied to ordinary updates. Renaming a system field
    /// leaves behind the previous system index, which still carries the old column name: the
    /// planner would try to recreate it on a column that no longer exists, and startup would fail.
    /// Outside of migration, on the other hand, an index on a nonexistent field is an error better
    /// left to fail than silently dropped — the schema screen already flags it as blocking.
    /// </remarks>
    private static CollectionDefinition WithoutStaleIndexes(CollectionDefinition collection)
    {
        var columns = collection.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        return collection with
        {
            Indexes = [.. collection.Indexes.Where(index => index.Fields.All(columns.Contains))],
        };
    }

    /// <summary>
    /// Fills in a definition with the fields and indexes its type requires.
    /// </summary>
    private static CollectionDefinition Normalize(CollectionDefinition draft)
    {
        var system = draft.Kind is CollectionKind.Auth
            ? SystemFields.ForAuth()
            : SystemFields.ForBase();

        // System fields go first, in canonical order. Excluded from the rest are fields carrying a
        // system name — otherwise a hand-defined "id" field would overwrite the primary key — and
        // fields carrying a system identifier: this second filter is what lets a system field be
        // renamed without its old version surviving as a duplicate under the same identifier as
        // the new one.
        var systemIds = system.Select(f => f.Id).ToHashSet();

        var custom = draft.Fields
            .Where(f => !systemIds.Contains(f.Id) && !SystemFields.IsSystemField(f.Name))
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
    /// Runs a batch of DDL and saves the definition, in the same transaction.
    /// </summary>
    private async Task ApplyAsync(
        IReadOnlyList<string> statements,
        CollectionDefinition? collection,
        RecordId? deleted,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Outside the transaction, mandatorily: see ISchemaDdl.BeforeSchemaChange.
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
                    // A unique index placed on a column that already contains duplicates is a data
                    // conflict, not a failure: 409, not 500.
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

        // Since foreign keys were switched off for the duration of the batch, check before commit
        // that the maneuver left no orphaned reference. Without this check, turning off the pragma
        // would amount to disabling integrity for nothing in return.
        var violations = await connection.QueryAsync(new CommandDefinition(
                check, transaction: transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (violations.Any())
        {
            throw new CratebaseConflictException(
                "The schema change would leave orphaned references: rolled back.");
        }
    }
}
