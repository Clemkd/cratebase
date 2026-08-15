using Cratebase.Auth;
using Cratebase.Data;
using Cratebase.Data.Postgres;
using Cratebase.Data.Sqlite;
using Cratebase.Storage;
using Cratebase.Storage.S3;

namespace Cratebase.Server;

/// <summary>
/// Configuration de Cratebase.
/// </summary>
/// <remarks>
/// Le choix du moteur se fait ici, et <b>nulle part ailleurs</b>. Passer de SQLite à PostgreSQL est
/// un changement de deux lignes dans cette configuration : c'est l'engagement du §1 du document de
/// conception, et il ne tient que parce qu'aucune autre partie du code ne nomme un moteur.
/// </remarks>
public sealed class CratebaseOptions
{
    /// <summary>Chaîne de connexion du moteur retenu.</summary>
    public string ConnectionString { get; private set; } =
        "Data Source=./data/cratebase.db";

    /// <summary>Dialecte retenu.</summary>
    public ISqlDialect Dialect { get; private set; } = SqliteDialect.Instance;

    /// <summary>Générateur de DDL retenu.</summary>
    public ISchemaDdl Ddl { get; private set; } = SqliteDialect.Instance;

    /// <summary>Racine des données : base, fichiers, sauvegardes.</summary>
    public string DataDirectory { get; set; } = "./data";

    /// <summary>Préfixe des endpoints de l'API.</summary>
    public string ApiPrefix { get; set; } = "/api";

    /// <summary>
    /// Le lot transactionnel <c>POST /api/batch</c> est-il ouvert ?
    /// </summary>
    /// <remarks>
    /// Fermé par défaut, comme chez PocketBase : un lot permet d'amplifier une requête en centaines
    /// d'écritures, donc il ne s'ouvre que si l'application en a l'usage.
    /// </remarks>
    public bool EnableBatch { get; set; }

    /// <summary>
    /// Les requêtes de l'API sont-elles journalisées ?
    /// </summary>
    /// <remarks>
    /// Ouvert par défaut : un backend sans journal ne se diagnostique pas. Ce drapeau est un
    /// interrupteur d'hôte — il retire le middleware et le service d'entretien du pipeline —, à ne
    /// pas confondre avec le réglage <c>logs.enabled</c>, que la console modifie à chaud.
    /// </remarks>
    public bool EnableRequestLog { get; set; } = true;

    /// <summary>Utilise SQLite. Défaut.</summary>
    public CratebaseOptions UseSqlite(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        ConnectionString = connectionString;
        Dialect = SqliteDialect.Instance;
        Ddl = SqliteDialect.Instance;

        return this;
    }

    /// <summary>Utilise PostgreSQL.</summary>
    public CratebaseOptions UsePostgres(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        ConnectionString = connectionString;
        Dialect = PostgresDialect.Instance;
        Ddl = PostgresDialect.Instance;

        return this;
    }

    /// <summary>Fabrique du magasin d'objets retenu.</summary>
    public Func<IObjectStore> ObjectStoreFactory { get; private set; } = () =>
        new LocalObjectStore("./data/storage");

    /// <summary>
    /// Fournisseurs d'identité externes activés, indexés par nom technique.
    /// </summary>
    /// <remarks>
    /// Configurés au démarrage plutôt que stockés en base : un secret client n'a rien à faire dans
    /// une table que la console peut lire, ni dans une sauvegarde.
    /// </remarks>
    public Dictionary<string, OAuth2Provider> OAuth2Providers { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Active un fournisseur externe à partir de son préréglage.</summary>
    public CratebaseOptions AddOAuth2(string name, string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            // Silencieux plutôt que fatal : une configuration partielle est le cas normal en
            // développement, où les identifiants ne sont pas renseignés.
            return this;
        }

        OAuth2Providers[name] = OAuth2Presets.Apply(name, clientId, clientSecret, enabled: true);

        return this;
    }

    /// <summary>Stocke les fichiers sur le disque local. Défaut.</summary>
    public CratebaseOptions UseLocalFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        ObjectStoreFactory = () => new LocalObjectStore(directory);

        return this;
    }

    /// <summary>Stocke les fichiers sur un service compatible S3 : MinIO, Garage, R2, B2, AWS.</summary>
    public CratebaseOptions UseS3(S3StorageOptions storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        ObjectStoreFactory = () => new S3ObjectStore(storage);

        return this;
    }
}
