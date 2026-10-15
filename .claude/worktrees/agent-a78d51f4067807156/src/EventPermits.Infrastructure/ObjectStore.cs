using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using EventPermits.Application;
using Microsoft.Extensions.Configuration;

namespace EventPermits.Infrastructure;

/// <summary>
/// S3-compatible storage settings (deployability contract). Locally: SeaweedFS with path-style addressing and
/// a least-privilege access key. In AWS: <see cref="ServiceUrl"/> and the keys are empty, so the SDK uses the
/// regional endpoint and the default credential chain (the ECS task role).
/// </summary>
public sealed class StorageOptions
{
    public string ServiceUrl { get; set; } = "";
    /// <summary>Browser-reachable endpoint used only to sign download URLs (e.g. http://127.0.0.1:9000 locally).</summary>
    public string PublicEndpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; }
    public string Bucket { get; set; } = "event-permits";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    public static StorageOptions From(IConfiguration config) => config.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();

    public AmazonS3Client CreateClient(bool forBrowser = false)
    {
        var s3 = new AmazonS3Config
        {
            ForcePathStyle = ForcePathStyle,
            AuthenticationRegion = Region,
            Timeout = TimeSpan.FromSeconds(20),
            MaxErrorRetry = 2,
        };
        if (!string.IsNullOrWhiteSpace(ServiceUrl))
        {
            // S3-compatible emulators (SeaweedFS) reject the SDK's default trailing-checksum uploads.
            s3.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            s3.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        var endpoint = forBrowser && !string.IsNullOrWhiteSpace(PublicEndpoint) ? PublicEndpoint : ServiceUrl;
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            s3.ServiceURL = endpoint;
            if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s3.UseHttp = true;
        }
        else
        {
            s3.RegionEndpoint = RegionEndpoint.GetBySystemName(Region);
        }
        return string.IsNullOrEmpty(AccessKey)
            ? new AmazonS3Client(s3)
            : new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), s3);
    }
}

public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly StorageOptions _options;
    private readonly AmazonS3Client _client;
    private readonly AmazonS3Client _signer;

    public S3ObjectStore(StorageOptions options)
    {
        _options = options;
        _client = options.CreateClient();
        _signer = options.CreateClient(forBrowser: true);
    }

    public async Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken ct)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        await Wrap(() => _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
            UseChunkEncoding = string.IsNullOrWhiteSpace(_options.ServiceUrl),
        }, ct));
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        var response = await Wrap(() => _client.GetObjectAsync(_options.Bucket, key, ct));
        using (response)
        {
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
    }

    public string PresignGet(string key, TimeSpan lifetime, string downloadFileName, string contentType)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
            Protocol = (_options.PublicEndpoint.Length > 0 ? _options.PublicEndpoint : _options.ServiceUrl)
                .StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS,
        };
        request.ResponseHeaderOverrides.ContentType = contentType;
        request.ResponseHeaderOverrides.ContentDisposition = $"attachment; filename=\"{downloadFileName.Replace("\"", "")}\"";
        return _signer.GetPreSignedURL(request);
    }

    public async Task<IReadOnlyList<StoredObjectInfo>> ListAsync(string prefix, CancellationToken ct)
    {
        var keys = new List<StoredObjectInfo>();
        string? token = null;
        do
        {
            var page = await Wrap(() => _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _options.Bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }, ct));
            keys.AddRange((page.S3Objects ?? []).Select(o => new StoredObjectInfo(o.Key, o.Size ?? 0, o.LastModified is { } m ? new DateTimeOffset(m.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.MinValue)));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);
        return keys;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_options.Bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task RemoveOrphanAsync(string key, CancellationToken ct) => Wrap(() => _client.DeleteObjectAsync(_options.Bucket, key, ct));

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _options.Bucket, MaxKeys = 1 }, ct);
            return true;
        }
        catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or TaskCanceledException or AmazonClientException)
        {
            return false;
        }
    }

    private static async Task<T> Wrap<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or AmazonClientException
                                   || (ex is TaskCanceledException && ex.InnerException is TimeoutException))
        {
            throw new DependencyUnavailableException(ErrorCodes.StorageUnavailable, "File storage is not available right now. Try again in a moment.");
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _signer.Dispose();
    }
}
