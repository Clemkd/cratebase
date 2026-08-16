using System.Data.Common;

namespace Cratebase.Data;

/// <summary>
/// Opens already-configured connections.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>Dialect of the target engine.</summary>
    ISqlDialect Dialect { get; }

    /// <summary>DDL generator of the target engine.</summary>
    ISchemaDdl Ddl { get; }

    /// <summary>Opens a connection, initialization statements already executed.</summary>
    Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default implementation.</summary>
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

            // SQLite pragmas are attached to the connection, not to the database: reapplying them
            // on every open is not a precaution, it is a requirement. A pooled connection that
            // missed them would run with foreign keys disabled for its entire lifetime.
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
