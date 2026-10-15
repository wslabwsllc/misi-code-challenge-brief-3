using System.Diagnostics;
using EventPermits.Application;
using EventPermits.Application.Services;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog.Context;

namespace EventPermits.Web.Hosting;

/// <summary>One scheduled job: a name, a period and the work to do in a fresh scope.</summary>
public sealed record JobDefinition(string Name, Func<IServiceProvider, TimeSpan> Period, Func<IServiceProvider, CancellationToken, Task<string?>> Run, bool RecordAlways = false);

/// <summary>
/// Runs every job on its own timer inside the worker process. A PostgreSQL session advisory lock per job means
/// two workers never run the same job at once (E3-06 AC1); runs that did something are recorded in JobRuns.
/// </summary>
public sealed class JobHost(IServiceProvider services, IEnumerable<JobDefinition> jobs) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(jobs.Select(job => LoopAsync(job, stoppingToken)));

    private async Task LoopAsync(JobDefinition job, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct).ContinueWith(_ => { }, CancellationToken.None);
        while (!ct.IsCancellationRequested)
        {
            TimeSpan period;
            await using (var scope = services.CreateAsyncScope())
            {
                period = job.Period(scope.ServiceProvider);
                await RunOnceAsync(scope.ServiceProvider, job, "schedule", ct);
            }
            try
            {
                await Task.Delay(period, ct);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Runs a job now under its lock; returns false when another worker holds the lock.</summary>
    public static async Task<bool> RunOnceAsync(IServiceProvider sp, JobDefinition job, string trigger, CancellationToken ct)
    {
        var logger = sp.GetRequiredService<ILogger<JobHost>>();
        var correlation = sp.GetRequiredService<CorrelationContext>();
        var id = correlation.BeginOperation();
        var factory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var lockDb = await factory.CreateDbContextAsync(ct);
        var key = LockKey(job.Name);
        var connection = lockDb.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT pg_try_advisory_lock({key})";
            if (await cmd.ExecuteScalarAsync(ct) is not true) return false;
        }
        var started = sp.GetRequiredService<TimeProvider>().GetUtcNow();
        var watch = Stopwatch.StartNew();
        string? details = null;
        var ok = true;
        try
        {
            using (LogContext.PushProperty("CorrelationId", id))
            using (LogContext.PushProperty("Job", job.Name))
                details = await job.Run(sp, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ok = false;
            details = ex.Message;
            logger.LogError(ex, "Job {Job} failed", job.Name);
        }
        finally
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT pg_advisory_unlock({key})";
            await cmd.ExecuteScalarAsync(CancellationToken.None);
        }
        if (!ok || job.RecordAlways || !string.IsNullOrEmpty(details))
        {
            lockDb.JobRuns.Add(new JobRun
            {
                Job = job.Name, Trigger = trigger, StartedAt = started, CompletedAt = started + watch.Elapsed, Succeeded = ok,
                Details = (details ?? "nothing to do").Length > 1000 ? details![..1000] : details ?? "nothing to do",
            });
            await lockDb.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Job {Job} ({Trigger}) {Outcome} in {ElapsedMs} ms: {Details}", job.Name, trigger, ok ? "succeeded" : "failed", watch.ElapsedMilliseconds, details);
        }
        return true;
    }

    private static long LockKey(string name)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("event-permits-job:" + name));
        return BitConverter.ToInt64(hash, 0);
    }
}

public static partial class Jobs
{
    public static readonly JobDefinition NotificationDispatch = new("notifications.dispatch", _ => TimeSpan.FromSeconds(5), async (sp, ct) =>
    {
        var sent = await sp.GetRequiredService<NotificationService>().DispatchDueAsync(ct);
        return sent > 0 ? $"{sent} message(s) sent" : null;
    });

    public static readonly JobDefinition NotificationPoll = new("notifications.poll", _ => TimeSpan.FromMinutes(2), async (sp, ct) =>
    {
        var updated = await sp.GetRequiredService<NotificationService>().PollStaleAsync(TimeSpan.FromMinutes(2), ct);
        return updated > 0 ? $"{updated} status(es) updated by polling" : null;
    });

    public static readonly JobDefinition CalendarOutbox = new("calendar.outbox", _ => TimeSpan.FromSeconds(5), async (sp, ct) =>
    {
        var processed = await sp.GetRequiredService<CalendarSyncService>().ProcessOutboxAsync(ct);
        return processed > 0 ? $"{processed} calendar command(s) processed" : null;
    });

    public static readonly JobDefinition CalendarSync = new("calendar.sync",
        sp => TimeSpan.FromMinutes(sp.GetRequiredService<RuleService>().IntAsync(RuleKeys.CalendarSyncMinutes).GetAwaiter().GetResult()),
        async (sp, ct) =>
        {
            var run = await sp.GetRequiredService<CalendarSyncService>().SyncAsync("schedule", Actor.System, ct);
            return run.Succeeded ? $"{run.Imported} new, {run.Updated} updated" : $"failed: {run.Error}";
        }, RecordAlways: true);

    public static readonly JobDefinition Overdue = new("overdue.markers", _ => TimeSpan.FromMinutes(10), async (sp, ct) =>
    {
        var requests = await sp.GetRequiredService<ReviewService>().MarkOverdueAsync(ct);
        var incidents = await sp.GetRequiredService<IncidentService>().MarkOverdueAsync(ct);
        return requests + incidents > 0 ? $"{requests} request(s) and {incidents} incident(s) marked overdue" : null;
    });

    public static readonly JobDefinition OrphanCleanup = new("storage.orphans", _ => TimeSpan.FromHours(1), async (sp, ct) =>
    {
        var store = sp.GetRequiredService<IObjectStore>();
        var factory = sp.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var referenced = (await db.Attachments.AsNoTracking().Select(a => a.ObjectKey).ToListAsync(ct)).ToHashSet();
        var removed = 0;
        foreach (var prefix in new[] { "attachments/", "permits/" })
            foreach (var item in await store.ListAsync(prefix, ct))
            {
                // Uploads are stored before their database row commits; only objects unreferenced for a day are orphans (E3-04 AC4).
                if (referenced.Contains(item.Key) || item.LastModified > DateTimeOffset.UtcNow.AddHours(-24)) continue;
                await store.RemoveOrphanAsync(item.Key, ct);
                removed++;
            }
        return removed > 0 ? $"{removed} orphaned object(s) removed" : "no orphans";
    }, RecordAlways: true);

    public static readonly JobDefinition PartnerImport = new("partner.import", _ => TimeSpan.FromMinutes(15), async (sp, ct) =>
    {
        // The drop folder is scanned every 15 minutes; the mock drops a clean file nightly (OD-5).
        var batches = await sp.GetRequiredService<PartnerImportService>().ImportDropFolderAsync(Actor.System, "schedule", ct);
        return batches.Count > 0 ? $"{batches.Count} file(s) imported" : null;
    });

    static partial void AddJobs(IServiceCollection services)
    {
        foreach (var job in All) services.AddSingleton(job);
        services.AddHostedService<JobHost>();
    }

    public static IReadOnlyList<JobDefinition> All => [NotificationDispatch, NotificationPoll, CalendarOutbox, CalendarSync, Overdue, OrphanCleanup, PartnerImport];

    static partial void AddChecks(IHealthChecksBuilder builder)
    {
        foreach (var kind in new[] { IntegrationKind.Registry, IntegrationKind.Calendar, IntegrationKind.NotificationGateway, IntegrationKind.Weather, IntegrationKind.Geocoding })
            builder.Add(new HealthCheckRegistration(Name(kind), sp => new IntegrationHealthCheck(sp.GetRequiredService<IDbContextFactory<AppDbContext>>(), kind),
                HealthStatus.Degraded, ["dependency"]));
    }

    public static string Name(IntegrationKind kind) => kind switch
    {
        IntegrationKind.NotificationGateway => "notificationGateway",
        _ => char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..],
    };
}

/// <summary>
/// Degraded, never dead (FR-16): an integration's health comes from its latest recorded attempt. An outage shows
/// as Degraded with the reason, while the app process and database stay Healthy.
/// </summary>
public sealed class IntegrationHealthCheck(IDbContextFactory<AppDbContext> factory, IntegrationKind kind) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var last = await db.IntegrationAttempts.AsNoTracking().Where(a => a.Integration == kind).OrderByDescending(a => a.At).FirstOrDefaultAsync(cancellationToken);
        if (last is null) return HealthCheckResult.Healthy("No calls yet.");
        var age = DateTimeOffset.UtcNow - last.At;
        return last.Outcome switch
        {
            AttemptOutcome.Success => HealthCheckResult.Healthy($"Last call succeeded {age.TotalMinutes:0} min ago."),
            AttemptOutcome.Offline or AttemptOutcome.Timeout => HealthCheckResult.Degraded($"Offline: last call {last.Outcome.ToString().ToLowerInvariant()} {age.TotalMinutes:0} min ago."),
            _ => HealthCheckResult.Degraded($"Last call answered HTTP {last.StatusCode} {age.TotalMinutes:0} min ago."),
        };
    }
}
