using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record TemplateUsage(string Code, string Name, NotificationChannel Channel, bool IsActive, string Subject, int Total, int Delivered, int Failed, DateTimeOffset? LastUsedAt);

public sealed record CaseTarget(Guid ApplicationId, string Reference, string Title, CaseStatus Status);

public sealed record GatewayAttemptRow(Guid Id, DateTimeOffset At, string Operation, string Method, string Url, int? StatusCode, AttemptOutcome Outcome,
    int DurationMs, int AttemptNumber, string? Error, string CorrelationId, string RequestId, Guid? ApplicationId);

public sealed record MessagingQueue(IReadOnlyList<NotificationRow> Queued, IReadOnlyList<GatewayAttemptRow> Attempts, IReadOnlyDictionary<WebhookOutcome, int> ReceiptCounts);

/// <summary>Read queries for the Notification Center (page 12) and the Messaging Delivery Monitor (page 22).</summary>
public sealed partial class NotificationService
{
    /// <summary>Templates with how often each was used and how those messages ended.</summary>
    public async Task<IReadOnlyList<TemplateUsage>> TemplatesAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewNotifications);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var templates = await db.NotificationTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        var usage = await db.Notifications.AsNoTracking().GroupBy(n => n.TemplateCode).Select(g => new
        {
            g.Key,
            Total = g.Count(),
            Delivered = g.Count(n => n.Status == NotificationStatus.Delivered),
            Failed = g.Count(n => n.Status == NotificationStatus.Failed),
            Last = g.Max(n => (DateTimeOffset?)n.CreatedAt),
        }).ToDictionaryAsync(x => x.Key, ct);
        return templates.Select(t => usage.TryGetValue(t.Code, out var u)
            ? new TemplateUsage(t.Code, t.Name, t.Channel, t.IsActive, t.Subject, u.Total, u.Delivered, u.Failed, u.Last)
            : new TemplateUsage(t.Code, t.Name, t.Channel, t.IsActive, t.Subject, 0, 0, 0, null)).ToList();
    }

    public async Task<IReadOnlyDictionary<NotificationStatus, int>> StatusCountsAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewNotifications);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var counts = await db.Notifications.AsNoTracking().GroupBy(n => n.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return Enum.GetValues<NotificationStatus>().ToDictionary(s => s, s => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0);
    }

    /// <summary>Finds the case a manual notification is for by its APP reference (400 on the reference field when there is none).</summary>
    public async Task<CaseTarget> FindCaseAsync(Actor actor, string? reference, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageNotifications);
        var text = (reference ?? "").Trim().ToUpperInvariant();
        new Validator().Text("reference", text, 5, 40, "Case reference").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await db.Applications.AsNoTracking().Include(a => a.Event).FirstOrDefaultAsync(a => a.Reference == text, ct)
                  ?? throw ValidationException.For("reference", $"No case has the reference {text}.");
        return new CaseTarget(app.Id, app.Reference, app.Event?.Title ?? "", app.Status);
    }

    /// <summary>Oldest queued messages, recent gateway calls and webhook receipt totals by outcome.</summary>
    public async Task<MessagingQueue> QueueAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageNotifications);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var mask = CaseScope.MasksContacts(actor);
        var queued = await db.Notifications.AsNoTracking().Where(n => n.Status == NotificationStatus.Queued).OrderBy(n => n.CreatedAt).Take(20).ToListAsync(ct);
        var attempts = await db.IntegrationAttempts.AsNoTracking().Where(a => a.Integration == IntegrationKind.NotificationGateway)
            .OrderByDescending(a => a.At).Take(25)
            .Select(a => new GatewayAttemptRow(a.Id, a.At, a.Operation, a.Method, a.Url, a.StatusCode, a.Outcome, a.DurationMs, a.AttemptNumber, a.Error, a.CorrelationId, a.RequestId, a.ApplicationId))
            .ToListAsync(ct);
        var receipts = await db.WebhookReceipts.AsNoTracking().GroupBy(r => r.Outcome).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return new MessagingQueue(queued.Select(n => Row(n, mask)).ToList(), attempts,
            Enum.GetValues<WebhookOutcome>().ToDictionary(o => o, o => receipts.FirstOrDefault(r => r.Key == o)?.Count ?? 0));
    }

    /// <summary>Webhook receipts, newest first, optionally one outcome only (accepted, duplicates ignored, rejected...).</summary>
    public async Task<(IReadOnlyList<WebhookReceipt> Rows, int Total)> ReceiptsAsync(Actor actor, WebhookOutcome? outcome, int page, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageNotifications);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = db.WebhookReceipts.AsNoTracking();
        if (outcome is { } o) q = q.Where(r => r.Outcome == o);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.ReceivedAt).Skip((Math.Max(page, 1) - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct);
        return (rows, total);
    }
}
