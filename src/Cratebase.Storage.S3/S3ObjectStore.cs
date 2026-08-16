using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Cratebase.Storage.S3;

/// <summary>Configuration of an S3-compatible storage backend.</summary>
public sealed class S3StorageOptions
{
    /// <summary>Bucket name.</summary>
    public required string Bucket { get; init; }

    /// <summary>Access key.</summary>
    public required string AccessKey { get; init; }

    /// <summary>Secret key.</summary>
    public required string SecretKey { get; init; }

    /// <summary>
    /// Endpoint as seen by the application.
    /// </summary>
    /// <remarks>
    /// In a container, this is an internal service name (<c>http://minio:9000</c>) that the browser
    /// cannot resolve.
    /// </remarks>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Endpoint as seen by the browser, if different from the one above.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The most costly trap in this domain.</b> A presigned URL is signed <i>for a specific
    /// host</i>: if the API signs for <c>http://minio:9000</c> and the browser calls
    /// <c>https://files.example.com</c>, the signature no longer matches and <b>every link is
    /// rejected</b>. Hence two separate clients, one to act, one to sign.
    /// </remarks>
    public string? PublicEndpoint { get; init; }

    /// <summary>Region. Meaningless outside AWS, but required by the SDK.</summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>
    /// Declared bucket capacity, in bytes. Zero: unknown.
    /// </summary>
    /// <remarks>
    /// Declared by the operator, because it can't be read anywhere: S3 imposes no per-bucket limit,
    /// and the services that do impose one each expose it their own way. Setting it gives the
    /// operations screen a denominator; leaving it out leaves an unmetered volume, which stays
    /// honest — a made-up gauge would not be.
    /// </remarks>
    public long CapacityBytes { get; init; }

    /// <summary>
    /// Path style rather than subdomain style.
    /// </summary>
    /// <remarks>
    /// Required outside AWS: MinIO, Garage, and SeaweedFS don't expose per-bucket subdomains.
    /// </remarks>
    public bool ForcePathStyle { get; init; } = true;
}

/// <summary>
/// S3-compatible storage: AWS S3, MinIO, Garage, Cloudflare R2, Backblaze B2.
/// </summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly AmazonS3Client _internal;
    private readonly AmazonS3Client _public;
    private readonly string _bucket;
    private readonly bool _disablePayloadSigning;

    /// <summary>Builds the store from its configuration.</summary>
    public S3ObjectStore(S3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _bucket = options.Bucket;
        _internal = Build(options, options.Endpoint);

        // Payload signing can only be disabled over HTTPS: over HTTP, it's the only thing that
        // guarantees the body's integrity, and the SDK rejects the combination. A development MinIO
        // listens in the clear, hence the choice by scheme rather than a fixed flag.
        _disablePayloadSigning = options.Endpoint.StartsWith(
            "https://", StringComparison.OrdinalIgnoreCase);

        // The second client exists only to sign. If there's only one host, we reuse the first
        // instead of instantiating an identical one.
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

            // S3 caps bulk deletion at 1000 keys per call.
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
    /// <remarks>
    /// <c>ListObjectsV2</c> already returns the size of each object. The default implementation
    /// would chain a <c>HEAD</c> call per key: on a bucket of ten thousand files, that's ten
    /// thousand round trips for information the response already carried.
    /// </remarks>
    public async IAsyncEnumerable<ObjectInfo> ListInfoAsync(
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
                // The type isn't part of the listing response: it's inferred from the extension,
                // same as on write. A `HEAD` per object to read it would cost exactly what this
                // override exists to avoid.
                yield return new ObjectInfo(
                    entry.Key,
                    entry.Size ?? 0,
                    ObjectKey.ContentTypeOf(entry.Key));
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

        // Signed by the PUBLIC client: that's the host the browser will call.
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

    /// <summary>Creates the bucket if it doesn't exist.</summary>
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
