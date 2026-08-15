using System.Data.Common;
using Cratebase.Core;
using Cratebase.Data;
using Cratebase.Schema;
using Dapper;

namespace Cratebase.Records;

/// <summary>
/// Moteur CRUD des enregistrements.
/// </summary>
/// <remarks>
/// <para>
/// L'ordre d'application est celui du §7 du document de conception, et il n'est pas négociable :
/// règle d'accès compilée d'abord, filtre client ensuite, composition par conjonction, bornes,
/// puis une seule requête paramétrée.
/// </para>
/// <para>
/// Les écritures placent la règle dans la clause <c>WHERE</c> de l'instruction elle-même et
/// <b>vérifient le nombre de lignes affectées</b>. C'est la contre-mesure au défaut le plus
/// coûteux de ce genre de couche : une suppression qui traverse le périmètre, répond 204, et
/// détruit la ligne de quelqu'un d'autre.
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

    /// <summary>Liste les enregistrements d'une collection.</summary>
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

        // 1. La règle d'abord. 2. Le filtre du client ensuite. 3. Conjonction — et rien d'autre :
        // SqlPredicate n'expose pas de disjonction, donc le filtre ne peut pas élargir la règle.
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
    /// Renseigne les champs de date automatique déclarés par la collection.
    /// </summary>
    /// <remarks>
    /// <c>created</c> et <c>updated</c> sont posés juste avant, en dur : ce sont des champs système,
    /// et le moteur ne peut pas dépendre d'options que quelqu'un pourrait décocher. Ceux-ci sont
    /// déclarés par l'utilisateur — « vu le », « archivé le » — et suivent exactement la même règle,
    /// sans quoi le type ne serait qu'une étiquette : la console laisserait le choisir et rien ne se
    /// remplirait.
    ///
    /// La valeur soumise est écrasée, jamais respectée : un champ dit automatique dont un client
    /// peut poser la date ne prouve plus rien sur le moment où l'écriture a eu lieu.
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

    /// <summary>Consulte un enregistrement.</summary>
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

        // 404 et non 403 : un 403 confirmerait que la ligne existe, ce qui suffit à cartographier
        // une base par essais d'identifiants.
        return row ?? throw new CratebaseNotFoundException();
    }

    /// <summary>Crée un enregistrement.</summary>
    /// <param name="collectionName">Collection cible.</param>
    /// <param name="data">Valeurs soumises.</param>
    /// <param name="request">Contexte d'évaluation des règles.</param>
    /// <param name="id">
    /// Identifiant imposé. Sert à l'import de fichiers : les objets sont rangés sous
    /// <c>{collection}/{id}/…</c>, donc l'identifiant doit être connu <b>avant</b> l'insertion. À
    /// laisser vide dans tous les autres cas.
    /// </param>
    /// <param name="cancellationToken">Jeton d'annulation.</param>
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

        // Le corps brut est figé avant validation : la validation retire les champs système, et
        // c'est précisément là que les crochets vont chercher le mot de passe soumis.
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

        // BindParameter, et non l'emplacement nu : une colonne JSON native exige un transtypage
        // explicite sur PostgreSQL.
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

        // La règle de création s'évalue sur la ligne telle qu'elle vient d'être écrite, valeurs par
        // défaut comprises — pas sur le corps brut. Une règle « owner = @request.auth.id » doit
        // donc voir le propriétaire posé par un hook, et pas seulement celui soumis par le client.
        var created = await FetchAsync(
                connection, transaction, collection, recordId.ToString(), rule, cancellationToken)
            .ConfigureAwait(false);

        if (created is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            throw new CratebaseBadRequestException(
                "La règle de création de cette collection interdit cet enregistrement.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return created;
    }

    /// <summary>Modifie un enregistrement.</summary>
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

        // L'état antérieur alimente le modificateur « :changed ». Il est lu sans appliquer de
        // règle — sans risque, puisqu'il n'est jamais renvoyé et que l'écriture, elle, porte la
        // règle dans son WHERE.
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

        // ⚠️ La règle est dans le WHERE de l'UPDATE, et le nombre de lignes affectées est vérifié.
        // Contrôler l'accès par une lecture préalable ouvrirait une fenêtre entre le contrôle et
        // l'écriture ; ici, les deux sont la même instruction.
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

        return updated ?? throw new CratebaseNotFoundException();
    }

    /// <summary>Supprime un enregistrement.</summary>
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

        // Les crochets passent avant la règle d'accès, comme à la création : ils protègent une
        // invariante du moteur — le dernier super-admin — que nulle règle ne saurait
        // exprimer, puisque c'est précisément le compte qui a le droit de tout faire.
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

        // Zéro ligne affectée : soit l'enregistrement n'existe pas, soit il est hors périmètre. Les
        // deux cas rendent 404, et c'est délibéré — distinguer les deux dirait à l'appelant que la
        // ligne existe.
        if (affected == 0)
        {
            throw new CratebaseNotFoundException();
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
