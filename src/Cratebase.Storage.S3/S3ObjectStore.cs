using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Cratebase.Storage.S3;

/// <summary>Configuration d'un stockage compatible S3.</summary>
public sealed class S3StorageOptions
{
    /// <summary>Nom du seau.</summary>
    public required string Bucket { get; init; }

    /// <summary>Clé d'accès.</summary>
    public required string AccessKey { get; init; }

    /// <summary>Clé secrète.</summary>
    public required string SecretKey { get; init; }

    /// <summary>
    /// Point de terminaison vu par l'application.
    /// </summary>
    /// <remarks>
    /// En conteneur, c'est un nom de service interne (<c>http://minio:9000</c>) que le navigateur
    /// ne sait pas résoudre.
    /// </remarks>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Point de terminaison vu par le navigateur, s'il diffère du précédent.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Le piège le plus coûteux du domaine.</b> Une URL présignée est signée <i>pour un hôte
    /// donné</i> : si l'API signe pour <c>http://minio:9000</c> et que le navigateur appelle
    /// <c>https://fichiers.exemple.fr</c>, la signature ne correspond plus et <b>tous les liens
    /// sont rejetés</b>. D'où deux clients distincts, l'un pour agir, l'autre pour signer.
    /// </remarks>
    public string? PublicEndpoint { get; init; }

    /// <summary>Région. Sans objet hors AWS, mais exigée par le SDK.</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>
    /// Style de chemin plutôt que de sous-domaine.
    /// </summary>
    /// <remarks>
    /// Indispensable hors AWS : MinIO, Garage et SeaweedFS n'exposent pas de sous-domaines par seau.
    /// </remarks>
    public bool ForcePathStyle { get; init; } = true;
}

/// <summary>
/// Stockage compatible S3 : AWS S3, MinIO, Garage, Cloudflare R2, Backblaze B2.
/// </summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly AmazonS3Client _internal;
    private readonly AmazonS3Client _public;
    private readonly string _bucket;
    private readonly bool _disablePayloadSigning;

    /// <summary>Construit le magasin depuis sa configuration.</summary>
    public S3ObjectStore(S3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _bucket = options.Bucket;
        _internal = Build(options, options.Endpoint);

        // La signature de charge utile ne se désactive qu'en HTTPS : sur HTTP, c'est le seul
        // élément qui garantit l'intégrité du corps, et le SDK refuse la combinaison. Un MinIO de
        // développement écoute en clair, d'où le choix par le schéma plutôt qu'un drapeau fixe.
        _disablePayloadSigning = options.Endpoint.StartsWith(
            "https://", StringComparison.OrdinalIgnoreCase);

        // Le second client n'existe que pour signer. S'il n'y a qu'un hôte, on réutilise le premier
        // plutôt que d'en instancier un identique.
        _public = string.IsNullOrWhiteSpace(options.PublicEndpoint)
            || string.Equals(options.PublicEndpoint, options.Endpoint, StringComparison.Ordinal)
                ? _internal
                : Build(options, options.PublicEndpoint);
    }

    /// <inheritdoc />
    public string Name => "s3";

    /// <inheritdoc />
    public async Task PutAsync(
        string key,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ObjectKey.Validate(key);

        await _internal.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    InputStream = content,
                    ContentType = contentType,
                    DisablePayloadSigning = _disablePayloadSigning,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ObjectKey.Validate(key);

        try
        {
            var response = await _internal
                .GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);

            return response.ResponseStream;
        }
        catch (AmazonS3Exception error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        ObjectKey.Validate(key);

        try
        {
            var response = await _internal
                .GetObjectMetadataAsync(
                    new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);

            return new ObjectInfo(
                key,
                response.ContentLength,
                response.Headers.ContentType ?? ObjectKey.ContentTypeOf(key));
        }
        catch (AmazonS3Exception error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ObjectKey.Validate(key);

        await _internal
            .DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var keys = new List<KeyVersion>();

        await foreach (var key in ListAsync(prefix, cancellationToken).ConfigureAwait(false))
        {
            keys.Add(new KeyVersion { Key = key });

            // S3 plafonne la suppression groupée à 1000 clés par appel.
            if (keys.Count == 1000)
            {
                await DeleteBatchAsync(keys, cancellationToken).ConfigureAwait(false);
                keys.Clear();
            }
        }

        if (keys.Count > 0)
        {
            await DeleteBatchAsync(keys, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuation = null;

        do
        {
            var response = await _internal.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = _bucket,
                        Prefix = prefix,
                        ContinuationToken = continuation,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var entry in response.S3Objects ?? [])
            {
                yield return entry.Key;
            }

            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuation is not null);
    }

    /// <inheritdoc />
    public async Task<Uri?> PresignedGetAsync(
        string key,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ObjectKey.Validate(key);

        // Signature par le client PUBLIC : c'est l'hôte que le navigateur appellera.
        var url = await _public.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
        }).ConfigureAwait(false);

        return new Uri(url);
    }

    /// <inheritdoc />
    public Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
        EnsureBucketAsync(cancellationToken);

    /// <summary>Crée le seau s'il n'existe pas.</summary>
    public async Task EnsureBucketAsync(CancellationToken cancellationToken = default)
    {
        var buckets = await _internal.ListBucketsAsync(cancellationToken).ConfigureAwait(false);

        if (buckets.Buckets?.Any(b => string.Equals(b.BucketName, _bucket, StringComparison.Ordinal)) == true)
        {
            return;
        }

        await _internal
            .PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!ReferenceEquals(_internal, _public))
        {
            _public.Dispose();
        }

        _internal.Dispose();
    }

    private async Task DeleteBatchAsync(List<KeyVersion> keys, CancellationToken cancellationToken) =>
        await _internal.DeleteObjectsAsync(
                new DeleteObjectsRequest { BucketName = _bucket, Objects = keys }, cancellationToken)
            .ConfigureAwait(false);

    private static AmazonS3Client Build(S3StorageOptions options, string endpoint)
    {
        var configuration = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
        };

        return new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey), configuration);
    }
}
