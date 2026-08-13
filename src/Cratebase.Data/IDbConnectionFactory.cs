using System.Data.Common;

namespace Cratebase.Data;

/// <summary>
/// Ouvre des connexions déjà configurées.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>Dialecte du moteur cible.</summary>
    ISqlDialect Dialect { get; }

    /// <summary>Générateur de DDL du moteur cible.</summary>
    ISchemaDdl Ddl { get; }

    /// <summary>Ouvre une connexion, instructions d'initialisation déjà exécutées.</summary>
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Implémentation par défaut.</summary>
public sealed class DbConnectionFactory(ISqlDialect dialect, ISchemaDdl ddl, string connectionString)
    : IDbConnectionFactory
{
    private readonly string _connectionString = connectionString
        ?? throw new ArgumentNullException(nameof(connectionString));

    /// <inheritdoc />
    public ISqlDialect Dialect { get; } = dialect ?? throw new ArgumentNullException(nameof(dialect));

    /// <inheritdoc />
    public ISchemaDdl Ddl { get; } = ddl ?? throw new ArgumentNullException(nameof(ddl));

    /// <inheritdoc />
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = Dialect.CreateConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Les pragmas de SQLite sont attachés à la connexion, pas à la base : les réappliquer
            // à chaque ouverture n'est pas une précaution, c'est une obligation. Une connexion du
            // pool qui les aurait ratés désactiverait les clés étrangères pour toute sa durée de vie.
            foreach (var statement in Dialect.ConnectionInitializationStatements)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
