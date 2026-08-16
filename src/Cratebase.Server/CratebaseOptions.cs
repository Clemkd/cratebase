using Cratebase.Auth;
using Cratebase.Data;
using Cratebase.Data.Postgres;
using Cratebase.Data.Sqlite;
using Cratebase.Storage;
using Cratebase.Storage.S3;

namespace Cratebase.Server;

/// <summary>
/// Cratebase configuration.
/// </summary>
/// <remarks>
/// The engine choice is made here, and <b>nowhere else</b>. Switching from SQLite to PostgreSQL is
/// a two-line change in this configuration: that's the commitment made in §1 of the design
/// document, and it only holds because no other part of the code names an engine.
/// </remarks>
public sealed class CratebaseOptions
{
    /// <summary>Connection string of the chosen engine.</summary>
    public string ConnectionString { get; private set; } =
        "Data Source=./data/cratebase.db";

    /// <summary>Chosen dialect.</summary>
    public ISqlDialect Dialect { get; private set; } = SqliteDialect.Instance;

    /// <summary>Chosen DDL generator.</summary>
    public ISchemaDdl Ddl { get; private set; } = SqliteDialect.Instance;

    /// <summary>Data root: database, files, backups.</summary>
    public string DataDirectory { get; set; } = "./data";

    /// <summary>Prefix of the API endpoints.</summary>
    public string ApiPrefix { get; set; } = "/api";

    /// <summary>
    /// Is the transactional batch endpoint <c>POST /api/batch</c> open?
    /// </summary>
    /// <remarks>
    /// Closed by default, like PocketBase: a batch lets a single request amplify into hundreds of
    /// writes, so it only opens if the application has a use for it.
    /// </remarks>
    public bool EnableBatch { get; set; }

    /// <summary>
    /// Are API requests logged?
    /// </summary>
    /// <remarks>
    /// Open by default: a backend with no log can't be diagnosed. This flag is a host switch — it
    /// removes the middleware and the maintenance service from the pipeline — not to be confused
    /// with the <c>logs.enabled</c> setting, which the console changes live.
    /// </remarks>
    public bool EnableRequestLog { get; set; } = true;

    /// <summary>Uses SQLite. Default.</summary>
    public CratebaseOptions UseSqlite(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        ConnectionString = connectionString;
        Dialect = SqliteDialect.Instance;
        Ddl = SqliteDialect.Instance;

        return this;
    }

    /// <summary>Uses PostgreSQL.</summary>
    public CratebaseOptions UsePostgres(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        ConnectionString = connectionString;
        Dialect = PostgresDialect.Instance;
        Ddl = PostgresDialect.Instance;

        return this;
    }

    /// <summary>Chosen object store factory.</summary>
    public Func<IObjectStore> ObjectStoreFactory { get; private set; } = () =>
        new LocalObjectStore("./data/storage");

    /// <summary>
    /// External identity providers enabled, indexed by technical name.
    /// </summary>
    /// <remarks>
    /// Configured at startup rather than stored in the database: a client secret has no business
    /// being in a table the console can read, nor in a backup.
    /// </remarks>
    public Dictionary<string, OAuth2Provider> OAuth2Providers { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Enables an external provider from its preset.</summary>
    public CratebaseOptions AddOAuth2(string name, string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            // Silent rather than fatal: a partial configuration is the normal case in development,
            // where credentials aren't set.
            return this;
        }

        OAuth2Providers[name] = OAuth2Presets.Apply(name, clientId, clientSecret, enabled: true);

        return this;
    }

    /// <summary>
    /// What the console can say about the file store, without ever being able to say too much.
    /// </summary>
    /// <remarks>
    /// A description frozen at startup, distinct from the factory: the operations screen must be
    /// able to name the bucket and its endpoint — that's what one checks when files stop showing
    /// up — with no path leading to the secret key. It is therefore not copied here: only its
    /// <b>presence</b> is.
    /// </remarks>
    public StorageDescription StorageDescription { get; private set; } =
        new() { Kind = "local", Directory = "./data/storage" };

    /// <summary>
    /// Declared capacity of the database volume, in bytes. Zero: unknown.
    /// </summary>
    /// <remarks>
    /// Meaningless on SQLite, whose file occupies the host's disk — which can be measured. On
    /// PostgreSQL, however, no portable query returns remaining space, and a managed instance often
    /// exposes none: capacity therefore comes from the operator, or from nowhere.
    /// </remarks>
    public long DatabaseCapacityBytes { get; set; }

    /// <summary>Stores files on local disk. Default.</summary>
    public CratebaseOptions UseLocalFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        ObjectStoreFactory = () => new LocalObjectStore(directory);
        StorageDescription = new StorageDescription { Kind = "local", Directory = directory };

        return this;
    }

    /// <summary>Stores files on an S3-compatible service: MinIO, Garage, R2, B2, AWS.</summary>
    public CratebaseOptions UseS3(S3StorageOptions storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        ObjectStoreFactory = () => new S3ObjectStore(storage);
        StorageDescription = new StorageDescription
        {
            Kind = "s3",
            CapacityBytes = storage.CapacityBytes,
            Bucket = storage.Bucket,
            Endpoint = storage.Endpoint,
            PublicEndpoint = storage.PublicEndpoint ?? string.Empty,
            Region = storage.Region,
            ForcePathStyle = storage.ForcePathStyle,
            AccessKeyHint = Hint(storage.AccessKey),
            HasSecretKey = !string.IsNullOrWhiteSpace(storage.SecretKey),
        };

        return this;
    }

    /// <summary>
    /// Reduces an access key to its last four characters.
    /// </summary>
    /// <remarks>
    /// Enough to recognize which of a keyring's three keys is in service, too little to use it. An
    /// access key isn't a secret, but showing it in full would let it end up in screenshots and in
    /// the request log.
    /// </remarks>
    private static string Hint(string? accessKey) =>
        string.IsNullOrWhiteSpace(accessKey)
            ? string.Empty
            : accessKey.Length <= 4 ? new string('•', accessKey.Length) : $"••••{accessKey[^4..]}";
}

/// <summary>Description of the file store, as the console receives it.</summary>
public sealed record StorageDescription
{
    /// <summary><c>local</c> or <c>s3</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Root directory, for local disk.</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>Declared store capacity, in bytes. Zero: unknown.</summary>
    public long CapacityBytes { get; init; }

    /// <summary>Bucket name, for S3.</summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>Endpoint as seen by the API.</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>Endpoint as seen by the browser, if different.</summary>
    public string PublicEndpoint { get; init; } = string.Empty;

    /// <summary>Declared region.</summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>Path style rather than subdomain style.</summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>Last four characters of the access key.</summary>
    public string AccessKeyHint { get; init; } = string.Empty;

    /// <summary>Is the secret key set? Its value never leaves the process.</summary>
    public bool HasSecretKey { get; init; }
}
