using System.Text.Json;
using Cratebase.Core;
using Cratebase.Data;
using Dapper;

namespace Cratebase.Admin;

/// <summary>
/// Réglages persistés de l'instance.
/// </summary>
/// <remarks>
/// <para>
/// Une seule ligne, dont la valeur est un document JSON, plutôt qu'une colonne par réglage :
/// ajouter un réglage deviendrait sinon une migration de schéma, sur une table que personne ne
/// requête autrement que par sa clé. C'est le compromis de la table <c>_params</c> de PocketBase.
/// </para>
/// <para>
/// La valeur courante est gardée en mémoire : elle est lue à chaque requête par le middleware de
/// journalisation, et une lecture en base par requête pour trois booléens serait absurde. Le cache
/// est écrit par <see cref="SaveAsync"/>, donc une instance ne voit pas les réglages modifiés par
/// une autre — limite assumée tant que le déploiement de référence tient dans un conteneur.
/// </para>
/// </remarks>
public sealed class SettingsStore(IDbConnectionFactory connections, IClock clock)
{
    /// <summary>Table portant les réglages.</summary>
    public const string TableName = "_settings";

    /// <summary>Clé de la ligne unique.</summary>
    private const string RowKey = "app";

    private readonly IDbConnectionFactory _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private volatile AppSettings _current = new();

    /// <summary>Réglages en vigueur.</summary>
    public AppSettings Current => _current;

    /// <summary>Crée la table des réglages si elle n'existe pas.</summary>
    public async Task EnsureTableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;
        var text = dialect.ColumnType(FieldType.Text, multiple: false);

        await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 CREATE TABLE IF NOT EXISTS {dialect.QuoteIdentifier(TableName)} (
                   {dialect.QuoteIdentifier("key")} {text} NOT NULL PRIMARY KEY,
                   {dialect.QuoteIdentifier("value")} {text} NOT NULL,
                   {dialect.QuoteIdentifier("updated")} {text} NOT NULL
                 )
                 """,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Relit les réglages depuis la base et met le cache à jour.</summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                $"SELECT {dialect.QuoteIdentifier("value")} FROM {dialect.QuoteIdentifier(TableName)} " +
                $"WHERE {dialect.QuoteIdentifier("key")} = @key",
                new { key = RowKey },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        _current = Deserialize(stored);

        return _current;
    }

    /// <summary>Valide, enregistre et publie de nouveaux réglages.</summary>
    public async Task<AppSettings> SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var validated = settings.Validated();
        var payload = JsonSerializer.Serialize(validated, StorageJson.Options);
        var now = Timestamp.Normalize(_clock.UtcNow);

        await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var dialect = _connections.Dialect;

        // Mise à jour puis insertion si rien n'a bougé : « INSERT … ON CONFLICT » s'écrit
        // différemment selon le moteur, et cette table est écrite une fois par changement de
        // réglage — la course n'y a aucune conséquence observable.
        var updated = await connection.ExecuteAsync(new CommandDefinition(
                $"""
                 UPDATE {dialect.QuoteIdentifier(TableName)}
                 SET {dialect.QuoteIdentifier("value")} = @value,
                     {dialect.QuoteIdentifier("updated")} = @updated
                 WHERE {dialect.QuoteIdentifier("key")} = @key
                 """,
                new { key = RowKey, value = payload, updated = now },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (updated == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    $"""
                     INSERT INTO {dialect.QuoteIdentifier(TableName)}
                       ({dialect.QuoteIdentifier("key")}, {dialect.QuoteIdentifier("value")},
                        {dialect.QuoteIdentifier("updated")})
                     VALUES (@key, @value, @updated)
                     """,
                    new { key = RowKey, value = payload, updated = now },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        _current = validated;

        return validated;
    }

    private static AppSettings Deserialize(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(stored, StorageJson.Options) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Réglages illisibles — écrits par une version future, ou corrompus. Les valeurs par
            // défaut laissent l'instance démarrer ; refuser le démarrage rendrait la console
            // inaccessible, donc le problème irréparable depuis l'interface.
            return new AppSettings();
        }
    }
}
