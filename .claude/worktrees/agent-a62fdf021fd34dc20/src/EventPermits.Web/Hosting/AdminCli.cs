using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using EventPermits.Application;
using EventPermits.Infrastructure;
using EventPermits.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Web.Hosting;

/// <summary>
/// Commands of the web image, run instead of the web server (deployability contract):
///   init-local [dir]     create ignored local secrets for Compose (no database needed; never overwrites values)
///   storage-init         create the bucket if missing (uses the storage admin credentials only)
///   migrate [--no-seed]  apply migrations, then the idempotent seed (exit code 0 / non-zero)
///   seed                 same as migrate
///   reset --yes          DESTRUCTIVE: drop the database, then migrate and seed again
///   probe [url]          exit 0 when the URL answers 2xx (container health checks; images have no curl)
///   worker               background jobs host (health endpoints only, no UI)
/// </summary>
public static class AdminCli
{
    public static readonly string[] Commands = ["init-local", "storage-init", "migrate", "seed", "reset", "probe", "worker"];

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0];
        var rest = args[1..];
        try
        {
            return command switch
            {
                "init-local" => InitLocal(rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                                          ?? Environment.GetEnvironmentVariable("BOOTSTRAP_DIR") ?? "/bootstrap"),
                "probe" => await ProbeAsync(rest.FirstOrDefault() ?? "http://localhost:8080/health/ready"),
                "worker" => await RunWorkerAsync(rest),
                "storage-init" => await StorageInitAsync(rest),
                _ => await WithHostAsync(command, rest),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{command} failed: {ex.Message}");
            return 1;
        }
    }

    // ------------------------------------------------------------------ secrets

    /// <summary>
    /// Writes local secrets once (values are kept on later runs): a master file plus one folder per consumer in
    /// key-per-file layout (file name = configuration key with "__"). The SeaweedFS identities file grants the app
    /// read/write on its bucket and the partner feed write access to partner-drop/ only (least privilege).
    /// </summary>
    public static int InitLocal(string directory)
    {
        Directory.CreateDirectory(directory);
        var masterPath = Path.Combine(directory, "generated.json");
        var values = File.Exists(masterPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(masterPath)) ?? []
            : [];
        string V(string name, int length = 40) => values.TryGetValue(name, out var v) ? v : values[name] = RandomSecret(length);

        var bucket = "event-permits";
        var secrets = new
        {
            DbPassword = V("db_password", 32),
            StorageAdminKey = V("storage_admin_access", 20), StorageAdminSecret = V("storage_admin_secret"),
            StorageAppKey = V("storage_app_access", 20), StorageAppSecret = V("storage_app_secret"),
            StoragePartnerKey = V("storage_partner_access", 20), StoragePartnerSecret = V("storage_partner_secret"),
            PermitTokenKey = V("permit_token_key", 48), LinkSigningKey = V("link_signing_key", 48),
            RegistryApiKey = V("registry_api_key"), CalendarClientSecret = V("calendar_client_secret"),
            NotifyApiKey = V("notify_api_key"), WebhookSecret = V("notify_webhook_secret", 48), MockAdminKey = V("mock_admin_key"),
        };
        WriteSecret(masterPath, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        WriteSecret(Path.Combine(directory, "db_password"), secrets.DbPassword);

        WriteSecret(Path.Combine(directory, "s3.json"), JsonSerializer.Serialize(new
        {
            identities = new object[]
            {
                new { name = "storage-admin", credentials = new[] { new { accessKey = secrets.StorageAdminKey, secretKey = secrets.StorageAdminSecret } }, actions = new[] { "Admin", "Read", "List", "Tagging", "Write" } },
                new { name = "event-permits-app", credentials = new[] { new { accessKey = secrets.StorageAppKey, secretKey = secrets.StorageAppSecret } }, actions = new[] { $"Read:{bucket}", $"Write:{bucket}", $"List:{bucket}", $"Tagging:{bucket}" } },
                new { name = "partner-feed", credentials = new[] { new { accessKey = secrets.StoragePartnerKey, secretKey = secrets.StoragePartnerSecret } }, actions = new[] { $"Write:{bucket}/partner-drop/*" } },
            },
        }, new JsonSerializerOptions { WriteIndented = true }));

        KeyPerFile(Path.Combine(directory, "app"), new()
        {
            ["Database__Password"] = secrets.DbPassword,
            ["Storage__AccessKey"] = secrets.StorageAppKey,
            ["Storage__SecretKey"] = secrets.StorageAppSecret,
            ["Security__PermitTokenKey"] = secrets.PermitTokenKey,
            ["Security__LinkSigningKey"] = secrets.LinkSigningKey,
            ["Integrations__Registry__ApiKey"] = secrets.RegistryApiKey,
            ["Integrations__Calendar__ClientSecret"] = secrets.CalendarClientSecret,
            ["Integrations__Notify__ApiKey"] = secrets.NotifyApiKey,
            ["Integrations__Notify__WebhookSecret"] = secrets.WebhookSecret,
            ["Integrations__MockAdminKey"] = secrets.MockAdminKey,
        });
        KeyPerFile(Path.Combine(directory, "storage-admin"), new()
        {
            ["Storage__AccessKey"] = secrets.StorageAdminKey,
            ["Storage__SecretKey"] = secrets.StorageAdminSecret,
        });
        KeyPerFile(Path.Combine(directory, "mocks"), new()
        {
            ["Mock__AdminKey"] = secrets.MockAdminKey,
            ["Mock__Registry__ApiKey"] = secrets.RegistryApiKey,
            ["Mock__Calendar__ClientSecret"] = secrets.CalendarClientSecret,
            ["Mock__Notify__ApiKey"] = secrets.NotifyApiKey,
            ["Mock__Notify__WebhookSecret"] = secrets.WebhookSecret,
            ["Mock__PartnerFeed__AccessKey"] = secrets.StoragePartnerKey,
            ["Mock__PartnerFeed__SecretKey"] = secrets.StoragePartnerSecret,
        });
        Console.WriteLine($"Local secrets are ready in {directory} (ignored by Git; never commit them).");
        return 0;
    }

    private static void KeyPerFile(string dir, Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(dir);
        foreach (var (key, value) in entries) WriteSecret(Path.Combine(dir, key), value);
    }

    private static string RandomSecret(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }

    /// <summary>World-readable inside the Git-ignored folder so the PostgreSQL and SeaweedFS container users can read it (local practice only).</summary>
    private static void WriteSecret(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    // ------------------------------------------------------------------ storage

    private static async Task<int> StorageInitAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ServiceSetup.AddSecretFiles(builder.Configuration);
        var storage = StorageOptions.From(builder.Configuration);
        using var client = storage.CreateClient();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var existing = await client.ListBucketsAsync();
                if ((existing.Buckets ?? []).Any(b => b.BucketName == storage.Bucket))
                {
                    Console.WriteLine($"Bucket {storage.Bucket} already exists.");
                    return 0;
                }
                await client.PutBucketAsync(new PutBucketRequest { BucketName = storage.Bucket });
                Console.WriteLine($"Bucket {storage.Bucket} created.");
                return 0;
            }
            catch (Exception ex) when (attempt < 30 && ex is AmazonS3Exception or HttpRequestException or Amazon.Runtime.AmazonClientException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    // ------------------------------------------------------------------ database

    private static async Task<int> WithHostAsync(string command, string[] args)
    {
        if (command is not ("migrate" or "seed" or "reset"))
        {
            Console.Error.WriteLine($"Unknown command '{command}'. Use {string.Join(", ", Commands)}.");
            return 2;
        }
        if (command == "reset" && !args.Contains("--yes"))
        {
            Console.Error.WriteLine("reset deletes every row in the database. Repeat with --yes to confirm.");
            return 2;
        }
        var builder = WebApplication.CreateBuilder(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray());
        // On a fresh database EF logs its expected "no migrations history table yet" probe as an error; hide that noise here only.
        builder.Configuration["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore.Database.Command"] = "Fatal";
        builder.AddEventPermits(HostRole.Admin);
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            if (command == "reset")
            {
                await db.Database.EnsureDeletedAsync();
                Console.WriteLine("Database dropped (reset).");
            }
            await WaitForDatabaseAsync(db);
            await db.Database.MigrateAsync();
            Console.WriteLine("Migrations applied.");
        }
        if (command == "migrate" && args.Contains("--no-seed")) return 0;
        var summary = await scope.ServiceProvider.GetRequiredService<Seeder>().SeedAsync();
        Console.WriteLine($"Seed finished: {summary}.");
        return 0;
    }

    private static async Task WaitForDatabaseAsync(AppDbContext db)
    {
        for (var attempt = 1; attempt <= 30; attempt++)
        {
            if (await DatabaseServerReachable(db)) return;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        throw new InvalidOperationException("The database server did not become reachable within 60 seconds.");
    }

    /// <summary>A missing database (first run) is fine: migrations create it. Only an unreachable server is retried.</summary>
    private static async Task<bool> DatabaseServerReachable(AppDbContext db)
    {
        try
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Database = "postgres" };
            await using var conn = new Npgsql.NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ worker and probe

    private static async Task<int> RunWorkerAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddEventPermits(HostRole.Worker);
        var app = builder.Build();
        app.UseWorkerEndpoints();
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> ProbeAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
