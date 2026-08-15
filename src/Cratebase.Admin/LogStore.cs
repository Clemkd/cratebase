using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Admin;

/// <summary>
/// Journal des requêtes et des erreurs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Écriture tamponnée.</b> Journaliser sur le chemin de la requête ajouterait une écriture
/// synchrone à chaque appel — sur SQLite, où les écritures sont sérialisées, le journal deviendrait
/// le goulot de l'API qu'il observe. Les entrées passent donc par un canal borné, qu'un service de
/// fond vide par lots. C'est le compromis de PocketBase, pour la même raison.
/// </para>
/// <para>
/// <b>Le canal est borné et perd plutôt que d'attendre.</b> Un tampon illimité transformerait une
/// base bloquée en épuisement mémoire ; une écriture bloquante ferait attendre les requêtes
/// utiles. Les pertes sont comptées et affichées dans la console : un journal qui ment sur sa
/// complétude est pire qu'un journal incomplet.
/// </para>
/// <para>
/// La table vit dans la base principale, et non dans un fichier séparé comme chez PocketBase :
/// deux bases obligeraient à répondre « où sont les journaux ? » différemment selon le moteur, et
/// PostgreSQL n'a pas de second fichier à ouvrir.
/// </para>
/// </remarks>
public sealed class LogStore(IDbConnectionFactory connections, IClock clock) : IDisposable
{
    /// <summary>Table portant les entrées.</summary>
    public const string TableName = "_logs";

    private const int BufferCapacity = 4096;
    private const int BatchSize = 500;

    private static readonly IReadOnlyDictionary<string, object?> NoData =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private static readonly string[] Columns =
    [
        "id", "created", "level", "message", "method", "url", "status", "duration",
        "authCollection", "authId", "ip", "userAgent", "referer", "data",
    ];

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly Channel<LogEntry> _pending = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    // Un seul écrivain à la fois : le service de fond et la consultation de l'écran des journaux
    // peuvent demander une purge du tampon en même temps.
    private readonly SemaphoreSlim _writing = new(1, 1);

    private long _dropped;

    /// <summary>Entrées perdues faute de place dans le tampon, depuis le démarrage.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Crée la table du journal si elle n'existe pas.</summary>
    public async Task EnsureTableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var text = dialect.ColumnType(FieldType.Text, multiple: false);
        var number = dialect.ColumnType(FieldType.Number, multiple: false);

        string Column(string name, string type) => $"{dialect.QuoteIdentifier(name)} {type} NOT NULL";

        var statements = new[]
        {
            $"""
             CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(TableName)} (
               {dialect.QuoteIdentifier("id")} {text} NOT NULL PRIMARY KEY,
               {Column("created", text)},
               {Column("level", text)},
               {Column("message", text)},
               {Column("method", text)},
               {Column("url", text)},
               {Column("status", number)},
               {Column("duration", number)},
               {Column("authCollection", text)},
               {Column("authId", text)},
               {Column("ip", text)},
               {Column("userAgent", text)},
               {Column("referer", text)},
               {Column("data", text)}
             )
             """,

            // La fenêtre temporelle est le premier critère de toute consultation, et la purge par
            // rétention balaie exactement la même colonne.
            $"""
             CREATE INDEX IF NOT EXISTS {dialect.QuoteIdentifier("idx_logs_created")}
               ON {dialect.QuoteIdentifier(TableName)} ({dialect.QuoteIdentifier("created")})
             """,
            $"""
             CREATE INDEX IF NOT EXISTS {dialect.QuoteIdentifier("idx_logs_level_created")}
               ON {dialect.QuoteIdentifier(TableName)}
               ({dialect.QuoteIdentifier("level")}, {dialect.QuoteIdentifier("created")})
             """,
        };

        foreach (var statement in statements)
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(statement, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dépose une entrée dans le tampon. Ne touche jamais la base, donc ne bloque jamais l'appelant.
    /// </summary>
    public void Record(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var stamped = entry.Created == default ? entry with { Created = _clock.UtcNow } : entry;

        if (!_pending.Writer.TryWrite(stamped))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Écrit une entrée d'application.</summary>
    public void Record(
        LogSeverity level,
        string message,
        IReadOnlyDictionary<string, object?>? data = null) =>
        Record(new LogEntry
        {
            Level = level,
            Message = message,
            Data = data ?? NoData,
        });

    /// <summary>
    /// Vide le tampon dans la base. Rend le nombre d'entrées écrites.
    /// </summary>
    /// <remarks>
    /// Appelée par le service de fond à intervalle régulier, et par la consultation du journal :
    /// sans cela, l'écran afficherait toujours l'état d'il y a quelques secondes, et un
    /// administrateur qui vient de provoquer une erreur ne la trouverait pas.
    /// </remarks>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var batch = new List<LogEntry>(BatchSize);

            while (batch.Count < BatchSize && _pending.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return 0;
            }

            await WriteAsync(batch, cancellationToken).ConfigureAwait(false);

            return batch.Count;
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task WriteAsync(IReadOnlyList<LogEntry> batch, CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var columns = string.Join(", ", Columns.Select(dialect.QuoteIdentifier));
        var placeholders = string.Join(", ", Columns.Select(column => $"@{column}"));

        var sql = $"INSERT INTO {dialect.QuoteIdentifier(TableName)} ({columns}) VALUES ({placeholders})";

        // Un lot, une transaction : sur SQLite, c'est la différence entre une synchronisation de
        // disque par entrée et une seule pour tout le lot.
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var entry in batch)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    sql,
                    new
                    {
                        id = entry.Id.ToString(),
                        created = Timestamp.Normalize(entry.Created),
                        level = entry.Level.ToString(),
                        message = entry.Message,
                        method = entry.Method,
                        url = entry.Url,
                        status = (double)entry.Status,
                        duration = entry.Duration,
                        authCollection = entry.AuthCollection,
                        authId = entry.AuthId,
                        ip = entry.Ip,
                        userAgent = entry.UserAgent,
                        referer = entry.Referer,
                        data = JsonSerializer.Serialize(entry.Data, StorageJson.Options),
                    },
                    transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Consulte le journal.</summary>
    public async Task<PagedResult<LogEntry>> QueryAsync(
        LogQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var normalized = query.Normalized();
        var dialect = _connections.Dialect;
        var (where, parameters) = Predicate(normalized);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM {dialect.QuoteIdentifier(TableName)}{where}",
                parameters,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var selection = string.Join(", ", Columns.Select(column =>
            $"{dialect.QuoteIdentifier(column)} AS {dialect.QuoteIdentifier(column)}"));

        // Le tri porte sur « created » et non sur l'identifiant : les deux coïncident tant que les
        // entrées sont produites par un seul processus, mais deux instances derrière un répartiteur
        // n'ont pas la même horloge de génération — l'ordre visible doit rester celui des instants.
        var order = normalized.Ascending ? "ASC" : "DESC";
        var offset = (normalized.Page - 1) * normalized.PerPage;

        var rows = await connection.QueryAsync<LogRow>(new CommandDefinition(
                $"""
                 SELECT {selection}
                 FROM {dialect.QuoteIdentifier(TableName)}{where}
                 ORDER BY {dialect.QuoteIdentifier("created")} {order},
                          {dialect.QuoteIdentifier("id")} {order}
                 {dialect.LimitOffset(normalized.PerPage, offset)}
                 """,
                parameters,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return PagedResult.Counted(
            normalized.Page,
            normalized.PerPage,
            total,
            [.. rows.Select(Materialize)]);
    }

    /// <summary>Charge une entrée, ou rend <see langword="null"/>.</summary>
    public async Task<LogEntry?> GetAsync(RecordId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var selection = string.Join(", ", Columns.Select(column =>
            $"{dialect.QuoteIdentifier(column)} AS {dialect.QuoteIdentifier(column)}"));

        var row = await connection.QueryFirstOrDefaultAsync<LogRow>(new CommandDefinition(
                $"""
                 SELECT {selection}
                 FROM {dialect.QuoteIdentifier(TableName)}
                 WHERE {dialect.QuoteIdentifier("id")} = @id
                 """,
                new { id = id.ToString() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return row is null ? null : Materialize(row);
    }

    /// <summary>
    /// Compte les entrées par tranche de temps et par niveau.
    /// </summary>
    /// <remarks>
    /// Le découpage se fait sur le <b>préfixe textuel</b> de l'instant, pas avec une fonction de
    /// date : la forme canonique est de longueur fixe, donc ses 13 premiers caractères désignent
    /// l'heure et ses 10 premiers le jour, à l'identique sur les deux moteurs. Une fonction de
    /// troncature aurait exigé une méthode de dialecte de plus, donc une entrée de plus dans la
    /// suite de conformité, pour un résultat que la convention de format donne déjà.
    /// </remarks>
    public async Task<IReadOnlyList<LogBucket>> StatsAsync(
        LogQuery query,
        LogGranularity granularity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var dialect = _connections.Dialect;
        var (where, parameters) = Predicate(query);
        // 2026-08-13T14:05:09.123Z : dix caractères pour le jour, treize pour l'heure, seize pour
        // la minute.
        var length = granularity switch
        {
            LogGranularity.Minute => 16,
            LogGranularity.Hour => 13,
            _ => 10,
        };

        var bucket = $"substr({dialect.QuoteIdentifier("created")}, 1, {length})";

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<BucketRow>(new CommandDefinition(
                $"""
                 SELECT {bucket} AS {dialect.QuoteIdentifier("bucket")},
                        {dialect.QuoteIdentifier("level")} AS {dialect.QuoteIdentifier("level")},
                        COUNT(*) AS {dialect.QuoteIdentifier("count")}
                 FROM {dialect.QuoteIdentifier(TableName)}{where}
                 GROUP BY {bucket}, {dialect.QuoteIdentifier("level")}
                 ORDER BY {bucket} ASC
                 """,
                parameters,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return [.. rows.Select(row => new LogBucket(row.Bucket, ParseLevel(row.Level), row.Count))];
    }

    /// <summary>Nombre total d'entrées conservées.</summary>
    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM {_connections.Dialect.QuoteIdentifier(TableName)}",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Supprime les entrées plus anciennes que la rétention. Rend le nombre supprimé.</summary>
    public async Task<int> PurgeAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0)
        {
            // Zéro signifie « conserver sans limite ». Traiter cette valeur comme une date de
            // coupure au présent viderait le journal à chaque passage du service de fond.
            return 0;
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        return await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {dialect.QuoteIdentifier(TableName)} " +
                $"WHERE {dialect.QuoteIdentifier("created")} < @cutoff",
                new { cutoff = Timestamp.Normalize(_clock.UtcNow.AddDays(-retentionDays)) },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Vide le journal.</summary>
    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        // Le tampon part avec le reste : garder des entrées en attente les ferait réapparaître
        // quelques secondes après une purge que l'administrateur croit terminée.
        while (_pending.Reader.TryRead(out _))
        {
        }

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {_connections.Dialect.QuoteIdentifier(TableName)}",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _writing.Dispose();

    private (string Where, DynamicParameters Parameters) Predicate(LogQuery query)
    {
        var dialect = _connections.Dialect;
        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        // Un ensemble complet ne restreint rien : le laisser passer ferait payer un `IN` à quatre
        // valeurs pour un filtre qui n'écarte aucune ligne.
        if (query.Levels.Count > 0 && query.Levels.Count < LogSeverities.All.Count)
        {
            // Les niveaux sont stockés par leur nom : c'est donc ici, en C#, que se décide
            // l'ensemble retenu. Stocker le rang en base aurait décalé tout l'historique le jour où
            // un niveau s'insère au milieu.
            var levels = query.Levels.Distinct().Select(level => level.ToString()).ToArray();

            conditions.Add($"{dialect.QuoteIdentifier("level")} IN @levels");
            parameters.Add("levels", levels);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var message = dialect.LikeExpression(dialect.QuoteIdentifier("message"), "@search", negated: false);
            var url = dialect.LikeExpression(dialect.QuoteIdentifier("url"), "@search", negated: false);

            conditions.Add($"({message} OR {url})");
            parameters.Add("search", LikePattern.Contains(query.Search.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(query.Method))
        {
            conditions.Add($"{dialect.QuoteIdentifier("method")} = @method");
            parameters.Add("method", query.Method.Trim().ToUpperInvariant());
        }

        if (query.Status is { } status)
        {
            conditions.Add($"{dialect.QuoteIdentifier("status")} = @status");
            parameters.Add("status", (double)status);
        }

        if (query.From is { } from)
        {
            conditions.Add($"{dialect.QuoteIdentifier("created")} >= @from");
            parameters.Add("from", Timestamp.Normalize(from));
        }

        if (query.To is { } to)
        {
            conditions.Add($"{dialect.QuoteIdentifier("created")} < @to");
            parameters.Add("to", Timestamp.Normalize(to));
        }

        return (conditions.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", conditions), parameters);
    }

    private static LogEntry Materialize(LogRow row) => new()
    {
        Id = RecordId.TryParse(row.Id, out var id) ? id : RecordId.Empty,
        Created = Timestamp.Parse(row.Created),
        Level = ParseLevel(row.Level),
        Message = row.Message,
        Method = row.Method,
        Url = row.Url,
        Status = (int)Math.Round(row.Status, MidpointRounding.AwayFromZero),
        Duration = row.Duration,
        AuthCollection = row.AuthCollection,
        AuthId = row.AuthId,
        Ip = row.Ip,
        UserAgent = row.UserAgent,
        Referer = row.Referer,
        Data = ReadData(row.Data),
    };

    private static LogSeverity ParseLevel(string raw) =>
        Enum.TryParse<LogSeverity>(raw, ignoreCase: true, out var level) ? level : LogSeverity.Info;

    private static IReadOnlyDictionary<string, object?> ReadData(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return NoData;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, StorageJson.Options);

            return parsed is null
                ? NoData
                : parsed.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Une entrée écrite par une version antérieure ne doit pas rendre l'écran des journaux
            // inutilisable : la charge est rendue telle quelle, à charge de l'humain de la lire.
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["brut"] = json };
        }
    }

    private sealed record LogRow
    {
        public string Id { get; init; } = string.Empty;

        public string Created { get; init; } = Timestamp.Normalize(DateTimeOffset.UnixEpoch);

        public string Level { get; init; } = nameof(LogSeverity.Info);

        public string Message { get; init; } = string.Empty;

        public string Method { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        public double Status { get; init; }

        public double Duration { get; init; }

        public string AuthCollection { get; init; } = string.Empty;

        public string AuthId { get; init; } = string.Empty;

        public string Ip { get; init; } = string.Empty;

        public string UserAgent { get; init; } = string.Empty;

        public string Referer { get; init; } = string.Empty;

        public string Data { get; init; } = string.Empty;
    }

    private sealed record BucketRow
    {
        public string Bucket { get; init; } = string.Empty;

        public string Level { get; init; } = nameof(LogSeverity.Info);

        public long Count { get; init; }
    }
}

/// <summary>Mise en forme des tranches de l'histogramme.</summary>
public static class LogBuckets
{
    /// <summary>
    /// Rend un début de tranche en instant. Le préfixe canonique est complété par des zéros,
    /// puisqu'il désigne le début de l'heure ou du jour.
    /// </summary>
    public static DateTimeOffset ToInstant(string bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);

        var completed = bucket.Length switch
        {
            10 => bucket + "T00:00:00.000Z",
            13 => bucket + ":00:00.000Z",
            16 => bucket + ":00.000Z",
            _ => bucket,
        };

        return DateTimeOffset.TryParse(
            completed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }
}
