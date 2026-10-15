using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(EventPermits.IntegrationTests.Support.TestInfrastructure))]

namespace EventPermits.IntegrationTests.Support;

/// <summary>
/// Real PostgreSQL 18 and SeaweedFS (S3-compatible) containers shared by the whole test run, started on first use.
/// Every fixture gets its own database and bucket, so test classes never see each other's data.
/// Images and digests are the same as compose.yml.
/// </summary>
public sealed class TestInfrastructure : IAsyncLifetime
{
    public const string PostgresImage = "postgres:18.6@sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336";
    public const string StorageImage = "chrislusf/seaweedfs:4.48@sha256:4e61d15fd35994cb1e43e1e553dff106794841fd9a99ade2fc8c8bfce4d7872d";
    public const string AdminKey = "test-admin-access", AdminSecret = "test-admin-secret-0123456789";
    public const string AppKey = "test-app-access", AppSecret = "test-app-secret-0123456789";
    public const string PartnerKey = "test-partner-access", PartnerSecret = "test-partner-secret-0123456789";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private PostgreSqlContainer? _postgres;
    private IContainer? _storage;
    private int _next;

    public string StorageUrl { get; private set; } = "";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    private async Task EnsureStartedAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_postgres is null)
            {
                var postgres = new PostgreSqlBuilder(PostgresImage).Build();
                var config = """
                    {"identities":[
                      {"name":"admin","credentials":[{"accessKey":"test-admin-access","secretKey":"test-admin-secret-0123456789"}],"actions":["Admin","Read","List","Tagging","Write"]},
                      {"name":"app","credentials":[{"accessKey":"test-app-access","secretKey":"test-app-secret-0123456789"}],"actions":["Read","Write","List","Tagging"]},
                      {"name":"partner","credentials":[{"accessKey":"test-partner-access","secretKey":"test-partner-secret-0123456789"}],"actions":["Write"]}
                    ]}
                    """;
                var storage = new ContainerBuilder(StorageImage)
                    .WithCommand("server", "-s3", "-s3.port=8333", "-s3.config=/etc/s3.json", "-dir=/data", "-ip.bind=0.0.0.0", "-master.volumeSizeLimitMB=64")
                    .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/s3.json")
                    .WithPortBinding(8333, true)
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("nc", "-z", "127.0.0.1", "8333"))
                    .Build();
                await Task.WhenAll(postgres.StartAsync(), storage.StartAsync());
                _postgres = postgres;
                _storage = storage;
                StorageUrl = $"http://127.0.0.1:{storage.GetMappedPublicPort(8333)}";
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> NewDatabaseAsync()
    {
        await EnsureStartedAsync();
        return new Npgsql.NpgsqlConnectionStringBuilder(_postgres!.GetConnectionString())
        {
            Database = $"t{Interlocked.Increment(ref _next)}_{Guid.NewGuid():N}",
        }.ConnectionString;
    }

    /// <summary>Creates a fresh private bucket with the storage admin identity (retries while SeaweedFS finishes starting).</summary>
    public async Task<string> NewBucketAsync()
    {
        await EnsureStartedAsync();
        var bucket = $"test-{Guid.NewGuid():N}"[..30];
        using var client = new AmazonS3Client(new BasicAWSCredentials(AdminKey, AdminSecret), new AmazonS3Config
        {
            ServiceURL = StorageUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1", UseHttp = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED, ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await client.PutBucketAsync(bucket);
                return bucket;
            }
            catch (Exception) when (attempt < 30)
            {
                await Task.Delay(500);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_storage is not null) await _storage.DisposeAsync();
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
