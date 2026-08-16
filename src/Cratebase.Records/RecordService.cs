using System.Data.Common;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;
using Dapper;

namespace Cratebase.Records;

/// <summary>
/// Records CRUD engine.
/// </summary>
/// <remarks>
/// <para>
/// The order of application is the design document's §7, and it is not negotiable: access rule
/// compiled first, client filter next, composed by conjunction, bounds applied, then a single
/// parameterized query.
/// </para>
/// <para>
/// Writes place the rule in the statement's own <c>WHERE</c> clause and <b>check the number of
/// affected rows</b>. This is the countermeasure to this kind of layer's most costly defect: a
/// deletion that crosses the scope, returns 204, and destroys someone else's row.
/// </para>
/// </remarks>
public sealed class RecordService(
    IDbConnectionFactory connections,
    CollectionRegistry registry,
    IClock clock,
    IEnumerable<IRecordMutationHook>? hooks = null)
{
    private const string Alias = "t";
    private const string RulePrefix = "r";
    private const string FilterPrefix = "f";
    private const string ValuePrefix = "v";

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly CollectionRegistry _registry = registry
        ?? throw new ArgumentNullException(nameof(registry));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly IReadOnlyList<IRecordMutationHook> _hooks = [.. hooks ?? []];

    private ISqlDialect Dialect => _connections.Dialect;

    /// <summary>Lists a collection's records.</summary>
    public async Task<PagedResult<IReadOnlyDictionary<string, object?>>> ListAsync(
        string collectionName,
        RecordQuery query,
        FilterRequestContext request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(request);

        var collection = _registry.Require(collectionName);
        var compiler = CompilerFor(collection, request);

        // 1. The rule first. 2. Then the client's filter. 3. Conjunction — and nothing else:
        // SqlPredicate exposes no disjunction, so the filter can never widen the rule.
        var rule = RuleGuard.Compile(collection, CollectionAction.List, compiler, request.Auth, RulePrefix);
        var filter = compiler.Compile(query.Filter, Alias, FilterPrefix);
        var predicate = rule.And(filter);

        var order = SortCompiler.Compile(query.Sort, collection, Dialect, Alias);
        var parameters = ToParameters(predicate.Parameters);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var sql = $"""
                   SELECT {Projection(collection)}
                   FROM {Dialect.QuoteIdentifier(collection.TableName)} AS {Dialect.QuoteIdentifier(Alias)}
                   WHERE {predicate.Sql}
                   ORDER BY {order}
                   {Dialect.LimitOffset(query.EffectivePerPage, query.Offset)}
                   """;

        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var items = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var row in rows)
        {
            items.Add(Materialize(collection, row));
        }

        if (query.SkipTotal)
        {
            return PagedResult.Uncounted<IReadOnlyDictionary<string, object?>>(
                query.EffectivePage, query.EffectivePerPage, items);
        }

        var countSql = $"""
                        SELECT COUNT(*)
                        FROM {Dialect.QuoteIdentifier(collection.TableName)} AS {Dialect.QuoteIdentifier(Alias)}
                        WHERE {predicate.Sql}
                        """;

        var total = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return PagedResult.Counted<IReadOnlyDictionary<string, object?>>(
            query.EffectivePage, query.EffectivePerPage, total, items);
    }

    /// <summary>
    /// Sets the auto-date fields declared by the collection.
    /// </summary>
    /// <remarks>
    /// <c>created</c> and <c>updated</c> are set just before this, hardcoded: they are system
    /// fields, and the engine cannot depend on options someone could uncheck. These are
    /// user-declared — "viewed at", "archived at" — and follow exactly the same rule, otherwise
    /// the type would be nothing but a label: the console would let it be chosen and nothing would
    /// ever fill it in.
    ///
    /// The submitted value is overwritten, never honored: a field claimed to be automatic that a
    /// client can set no longer proves anything about when the write actually happened.
    /// </remarks>
    private static void StampAutoDates(
        CollectionDefinition collection,
        RecordData data,
        DateTimeOffset instant,
        bool creating)
    {
        foreach (var field in collection.Fields)
        {
            if (field.Type is not FieldType.AutoDate) continue;
            if (SystemFields.IsSystemField(field.Name)) continue;

            var applies = creating ? field.Options.OnCreate : field.Options.OnUpdate;

            if (applies)
            {
                data[field.Name] = Timestamp.Normalize(instant);
            }
        }
    }

    /// <summary>Views a record.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> ViewAsync(
        string collectionName,
        string recordId,
        FilterRequestContext request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var collection = _registry.Require(collectionName);
        var compiler = CompilerFor(collection, request);
        var rule = RuleGuard.Compile(collection, CollectionAction.View, compiler, request.Auth, RulePrefix);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await FetchAsync(connection, null, collection, recordId, rule, cancellationToken)
            .ConfigureAwait(false);

        // 404, not 403: a 403 would confirm the row exists, which is enough to map a database by
        // trying identifiers.
        return row ?? throw new CratebaseNotFoundException();
    }

    /// <summary>Creates a record.</summary>
    /// <param name="collectionName">Target collection.</param>
    /// <param name="data">Submitted values.</param>
    /// <param name="request">Rule evaluation context.</param>
    /// <param name="id">
    /// Forced identifier. Used for file import: objects are stored under
    /// <c>{collection}/{id}/…</c>, so the identifier must be known <b>before</b> insertion. Leave
    /// empty in every other case.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyDictionary<string, object?>> CreateAsync(
        string collectionName,
        RecordData data,
        FilterRequestContext request,
        RecordId? id = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(request);

        var collection = _registry.Require(collectionName);
        var compiler = CompilerFor(collection, request);
        var rule = RuleGuard.Compile(collection, CollectionAction.Create, compiler, request.Auth, RulePrefix);

        // The raw body is captured before validation: validation strips system fields, and that's
        // exactly where hooks go looking for the submitted password.
        var submitted = new Dictionary<string, object?>(data.AsDictionary(), StringComparer.Ordinal);

        RecordValidator.Validate(collection, data, isCreate: true);

        foreach (var hook in _hooks)
        {
            await hook.BeforeCreateAsync(collection, data, submitted, cancellationToken)
                .ConfigureAwait(false);
        }

        var now = _clock.UtcNow;
        var recordId = id ?? RecordId.New();

        data[SystemFields.Id] = recordId.ToString();
        data[SystemFields.Created] = Timestamp.Normalize(now);
        data[SystemFields.Updated] = Timestamp.Normalize(now);

        StampAutoDates(collection, data, now, creating: true);

        var writable = collection.Fields.Where(f => data.Contains(f.Name)).ToList();
        var columns = string.Join(", ", writable.Select(f => Dialect.QuoteIdentifier(f.ColumnName)));

        // BindParameter, not the bare placeholder: a native JSON column requires an explicit cast
        // on PostgreSQL.
        var placeholders = string.Join(", ", writable.Select(f =>
            Dialect.BindParameter(f.Type, f.Multiple, $"@{ValuePrefix}_{f.ColumnName}")));

        var parameters = new DynamicParameters();

        foreach (var field in writable)
        {
            parameters.Add(
                $"{ValuePrefix}_{field.ColumnName}",
                Dialect.ToStorage(field.Type, field.Multiple, data[field.Name]));
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    $"INSERT INTO {Dialect.QuoteIdentifier(collection.TableName)} ({columns}) VALUES ({placeholders})",
                    parameters,
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (DbException error) when (Dialect.TranslateException(error) is { } translated)
        {
            throw translated;
        }

        // The create rule evaluates against the row exactly as it was just written, defaults
        // included — not against the raw body. An "owner = @request.auth.id" rule must therefore
        // see the owner set by a hook, not only the one submitted by the client.
        var created = await FetchAsync(
                connection, transaction, collection, recordId.ToString(), rule, cancellationToken)
            .ConfigureAwait(false);

        if (created is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw new CratebaseBadRequestException(
                "This collection's create rule forbids this record.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        await AnnounceAsync(collection, RecordAction.Create, recordId.ToString(), created, cancellationToken)
            .ConfigureAwait(false);

        return created;
    }

    /// <summary>Updates a record.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> UpdateAsync(
        string collectionName,
        string recordId,
        RecordData data,
        FilterRequestContext request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(request);

        var collection = _registry.Require(collectionName);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The prior state feeds the ":changed" modifier. It's read without applying a rule — safe,
        // since it's never returned and the write itself carries the rule in its WHERE.
        var original = await FetchAsync(
                connection, null, collection, recordId, SqlPredicate.Unconstrained, cancellationToken)
            .ConfigureAwait(false);

        if (original is null)
        {
            throw new CratebaseNotFoundException();
        }

        var enriched = new FilterRequestContext
        {
            Auth = request.Auth,
            Context = request.Context,
            Method = request.Method,
            Headers = request.Headers,
            Query = request.Query,
            Body = data.AsDictionary(),
            Original = original,
        };

        var compiler = CompilerFor(collection, enriched);
        var rule = RuleGuard.Compile(collection, CollectionAction.Update, compiler, request.Auth, RulePrefix);

        var submitted = new Dictionary<string, object?>(data.AsDictionary(), StringComparer.Ordinal);

        RecordValidator.Validate(collection, data, isCreate: false);

        foreach (var hook in _hooks)
        {
            await hook.BeforeUpdateAsync(collection, data, submitted, original, cancellationToken)
                .ConfigureAwait(false);
        }

        var stamped = _clock.UtcNow;

        data[SystemFields.Updated] = Timestamp.Normalize(stamped);

        StampAutoDates(collection, data, stamped, creating: false);

        var writable = collection.Fields
            .Where(f => data.Contains(f.Name) && f.Name != SystemFields.Id && f.Name != SystemFields.Created)
            .ToList();

        if (writable.Count == 0)
        {
            return original;
        }

        var assignments = string.Join(", ", writable.Select(f =>
            $"{Dialect.QuoteIdentifier(f.ColumnName)} = " +
            Dialect.BindParameter(f.Type, f.Multiple, $"@{ValuePrefix}_{f.ColumnName}")));

        var parameters = ToParameters(rule.Parameters);
        parameters.Add("cb_id", recordId);

        foreach (var field in writable)
        {
            parameters.Add(
                $"{ValuePrefix}_{field.ColumnName}",
                Dialect.ToStorage(field.Type, field.Multiple, data[field.Name]));
        }

        // ⚠️ The rule sits in the UPDATE's WHERE, and the number of affected rows is checked.
        // Checking access with a prior read would open a window between the check and the write;
        // here, both are the same statement.
        var sql = $"""
                   UPDATE {Dialect.QuoteIdentifier(collection.TableName)} AS {Dialect.QuoteIdentifier(Alias)}
                   SET {assignments}
                   WHERE {Dialect.QuoteIdentifier(Alias)}.{Dialect.QuoteIdentifier(SystemFields.Id)} = @cb_id
                     AND ({rule.Sql})
                   """;

        int affected;

        try
        {
            affected = await connection.ExecuteAsync(
                    new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (DbException error) when (Dialect.TranslateException(error) is { } translated)
        {
            throw translated;
        }

        if (affected == 0)
        {
            throw new CratebaseNotFoundException();
        }

        var updated = await FetchAsync(
                connection, null, collection, recordId, SqlPredicate.Unconstrained, cancellationToken)
            .ConfigureAwait(false);

        if (updated is null) throw new CratebaseNotFoundException();

        await AnnounceAsync(collection, RecordAction.Update, recordId, updated, cancellationToken)
            .ConfigureAwait(false);

        return updated;
    }

    /// <summary>Deletes a record.</summary>
    public async Task DeleteAsync(
        string collectionName,
        string recordId,
        FilterRequestContext request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var collection = _registry.Require(collectionName);
        var compiler = CompilerFor(collection, request);
        var rule = RuleGuard.Compile(collection, CollectionAction.Delete, compiler, request.Auth, RulePrefix);

        // Hooks run before the access rule, as on create: they protect an engine invariant — the
        // last superuser — that no rule could express, since that's precisely the account that has
        // the right to do everything.
        foreach (var hook in _hooks)
        {
            await hook.BeforeDeleteAsync(collection, recordId, cancellationToken).ConfigureAwait(false);
        }

        var parameters = ToParameters(rule.Parameters);
        parameters.Add("cb_id", recordId);

        var sql = $"""
                   DELETE FROM {Dialect.QuoteIdentifier(collection.TableName)}
                   WHERE {Dialect.QuoteIdentifier(SystemFields.Id)} = @cb_id
                     AND {Dialect.QuoteIdentifier(SystemFields.Id)} IN (
                       SELECT {Dialect.QuoteIdentifier(Alias)}.{Dialect.QuoteIdentifier(SystemFields.Id)}
                       FROM {Dialect.QuoteIdentifier(collection.TableName)} AS {Dialect.QuoteIdentifier(Alias)}
                       WHERE {rule.Sql}
                     )
                   """;

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        int affected;

        try
        {
            affected = await connection.ExecuteAsync(
                    new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (DbException error) when (Dialect.TranslateException(error) is { } translated)
        {
            throw translated;
        }

        // Zero rows affected: either the record doesn't exist, or it's out of scope. Both cases
        // return 404, and that's deliberate — telling them apart would disclose that the row
        // exists.
        if (affected == 0)
        {
            throw new CratebaseNotFoundException();
        }

        await AnnounceAsync(collection, RecordAction.Delete, recordId, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Notifies the hooks that a write happened.
    /// </summary>
    /// <remarks>
    /// Exceptions are absorbed, one hook at a time: an unreachable subscriber or a failed email
    /// send must not turn a successful creation into an error for the caller. The write already
    /// happened; the only thing throwing here would achieve is a client that believes it was lost
    /// and replays it.
    /// </remarks>
    private async Task AnnounceAsync(
        CollectionDefinition collection,
        RecordAction action,
        string recordId,
        IReadOnlyDictionary<string, object?>? record,
        CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks)
        {
            try
            {
                await hook.AfterWriteAsync(collection, action, recordId, record, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // Deliberately silent toward the caller: logging its own failure is the hook's
                // job — only it knows what it was trying to do.
            }
        }
    }

    private FilterCompiler CompilerFor(CollectionDefinition collection, FilterRequestContext request) =>
        new(Dialect, new CollectionFieldResolver(collection), request, _clock);

    private string Projection(CollectionDefinition collection) => string.Join(
        ", ",
        collection.Fields
            .Where(f => !f.Hidden)
            .Select(f => $"{Dialect.QuoteIdentifier(Alias)}.{Dialect.QuoteIdentifier(f.ColumnName)}"));

    private async Task<IReadOnlyDictionary<string, object?>?> FetchAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CollectionDefinition collection,
        string recordId,
        SqlPredicate rule,
        CancellationToken cancellationToken)
    {
        var parameters = ToParameters(rule.Parameters);
        parameters.Add("cb_id", recordId);

        var sql = $"""
                   SELECT {Projection(collection)}
                   FROM {Dialect.QuoteIdentifier(collection.TableName)} AS {Dialect.QuoteIdentifier(Alias)}
                   WHERE {Dialect.QuoteIdentifier(Alias)}.{Dialect.QuoteIdentifier(SystemFields.Id)} = @cb_id
                     AND ({rule.Sql})
                   """;

        var row = await connection.QueryFirstOrDefaultAsync(
                new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : Materialize(collection, row);
    }

    private Dictionary<string, object?> Materialize(CollectionDefinition collection, dynamic row)
    {
        var source = (IDictionary<string, object?>)row;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["collectionId"] = collection.Id.ToString(),
            ["collectionName"] = collection.Name,
        };

        foreach (var field in collection.Fields)
        {
            if (field.Hidden || !source.TryGetValue(field.ColumnName, out var stored))
            {
                continue;
            }

            result[field.Name] = Dialect.FromStorage(field.Type, field.Multiple, stored);
        }

        return result;
    }

    private static DynamicParameters ToParameters(IReadOnlyDictionary<string, object?> values)
    {
        var parameters = new DynamicParameters();

        foreach (var (name, value) in values)
        {
            parameters.Add(name, value);
        }

        return parameters;
    }
}
