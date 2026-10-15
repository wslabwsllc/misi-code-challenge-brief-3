using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

/// <summary>
/// Writes template-driven notifications into the outbox inside the caller's transaction (E9-01).
/// Sending happens later in the worker, so a gateway failure never rolls back the business change (brief §6).
/// </summary>
public static class NotificationOutbox
{
    public static async Task QueueAsync(AppDbContext db, ICorrelationContext correlation, PermitApplication app, string templateCode,
        IReadOnlyDictionary<string, string>? values = null, bool manual = false, CancellationToken ct = default)
    {
        var template = await db.NotificationTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Code == templateCode && t.IsActive, ct);
        if (template is null) return; // deactivated template: nothing is sent, and that is visible on the template page
        var applicant = app.Applicant ?? await db.Applicants.AsNoTracking().FirstAsync(a => a.Id == app.ApplicantId, ct);
        var evt = app.Event ?? await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.ApplicationId == app.Id, ct);
        var cemetery = evt?.CemeteryId is { } cid ? await db.Cemeteries.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        var venue = evt?.PreferredVenueId is { } vid ? await db.Venues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct) : null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ApplicantName"] = applicant.FullName,
            ["Reference"] = app.Reference,
            ["EventTitle"] = evt?.Title ?? "",
            ["EventDate"] = evt?.StartUtc is { } s ? TimeZoneInfo.ConvertTime(s, Zone(cemetery?.TimeZoneId)).ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "",
            ["Venue"] = venue?.Name ?? "",
            ["PermitNumber"] = app.Permit?.Number ?? "",
        };
        if (values is not null) foreach (var (k, v) in values) fields[k] = v;

        var recipient = applicant.Email;
        var channel = template.Channel;
        if (string.IsNullOrWhiteSpace(recipient)) { recipient = applicant.Phone; channel = NotificationChannel.Sms; }
        if (string.IsNullOrWhiteSpace(recipient)) return;

        var now = db.Clock.GetUtcNow();
        var notification = new Notification
        {
            ApplicationId = app.Id,
            ApplicationReference = app.Reference,
            Channel = channel,
            Recipient = recipient,
            TemplateCode = template.Code,
            Subject = Fill(template.Subject, fields),
            Body = Fill(template.Body, fields),
            Status = NotificationStatus.Queued,
            IdempotencyKey = $"ntf-{Guid.NewGuid():N}",
            NextAttemptAt = now,
            Manual = manual,
            CorrelationId = correlation.CorrelationId,
        };
        notification.StatusChanges.Add(new NotificationStatusChange { To = NotificationStatus.Queued, Source = manual ? "manual" : "workflow", Detail = template.Code, At = now });
        db.Notifications.Add(notification);
    }

    public static string Fill(string text, IReadOnlyDictionary<string, string> fields) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\{\{(\w+)\}\}", m => fields.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public static readonly string[] Placeholders = ["ApplicantName", "Reference", "EventTitle", "EventDate", "Venue", "PermitNumber", "Reason", "Items", "DueDate", "Message"];

    private static TimeZoneInfo Zone(string? id)
    {
        try { return id is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }
}

public interface IWebhookVerifier
{
    bool Verify(string timestamp, string rawBody, string signature);
}

public sealed record NotificationRow(Guid Id, Guid? ApplicationId, string? Reference, NotificationChannel Channel, string Recipient, string TemplateCode,
    string Subject, NotificationStatus Status, int Attempts, string? LastError, string? GatewayMessageId, DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt, DateTimeOffset? DeliveredAt, bool Manual, string CorrelationId, uint Version);

public sealed record NotificationDetail(NotificationRow Row, string Body, IReadOnlyList<NotificationStatusChange> History,
    IReadOnlyList<WebhookReceipt> Receipts, IReadOnlyList<IntegrationAttempt> Attempts, IReadOnlyList<AuditEvent> Audit);

public sealed record NotificationFilter(NotificationStatus? Status = null, string? Search = null, int Page = 1);

public sealed record DeliveryResult(int StatusCode, WebhookOutcome Outcome, string Reason);

/// <summary>Notification Center, Messaging Monitor, worker dispatch and the signed delivery webhook (E9-02..E9-05).</summary>
public sealed partial class NotificationService(AppDb dbs, ICorrelationContext correlation, INotificationGateway gateway, RuleService rules, IWebhookVerifier verifier)
    : ServiceBase(dbs, correlation)
{
    public async Task<(IReadOnlyList<NotificationRow> Rows, int Total)> ListAsync(Actor actor, NotificationFilter filter, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewNotifications);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = db.Notifications.AsNoTracking();
        if (filter.Status is { } s) q = q.Where(n => n.Status == s);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            q = q.Where(n => (n.ApplicationReference != null && n.ApplicationReference.Contains(term)) || n.Subject.Contains(term) || n.TemplateCode.Contains(term) || n.CorrelationId == term);
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(n => n.CreatedAt).Skip((Math.Max(filter.Page, 1) - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct);
        var mask = CaseScope.MasksContacts(actor);
        return (rows.Select(n => Row(n, mask)).ToList(), total);
    }

    public async Task<NotificationDetail> GetAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewNotifications);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var n = await db.Notifications.AsNoTracking().Include(x => x.StatusChanges).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        // Receipts stay linked after a retry clears the gateway message id (matched by notification as well as message id).
        var receipts = await db.WebhookReceipts.AsNoTracking().Where(r => r.NotificationId == n.Id || (n.GatewayMessageId != null && r.MessageId == n.GatewayMessageId)).OrderBy(r => r.ReceivedAt).ToListAsync(ct);
        var attempts = await db.IntegrationAttempts.AsNoTracking()
            .Where(a => a.Integration == IntegrationKind.NotificationGateway && a.ApplicationId == n.ApplicationId && a.At >= n.CreatedAt.AddSeconds(-5))
            .OrderBy(a => a.At).Take(50).ToListAsync(ct);
        var audit = await db.AuditEvents.AsNoTracking().Where(a => a.EntityType == nameof(Notification) && a.EntityId == id.ToString()).OrderByDescending(a => a.At).ToListAsync(ct);
        return new NotificationDetail(Row(n, CaseScope.MasksContacts(actor)), CaseScope.MasksContacts(actor) ? "(message body hidden for auditors)" : n.Body,
            n.StatusChanges.OrderBy(c => c.At).ToList(), receipts, attempts, audit);
    }

    /// <summary>Safe manual retry of a Failed message only (E9-04, E9-05). A message the gateway already accepted gets a new idempotency key.</summary>
    public async Task RetryAsync(Actor actor, Guid id, uint? version, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageNotifications);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var n = await db.Notifications.Include(x => x.StatusChanges).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        ExpectVersion(db, n, version);
        if (n.Status != NotificationStatus.Failed)
            throw new BusinessRuleException(ErrorCodes.NotRetryable, $"Only failed messages can be retried; this one is {n.Status}.");
        var previousKey = n.IdempotencyKey;
        if (n.GatewayMessageId is not null)
        {
            // The gateway accepted the earlier send and reported a delivery failure: a fresh key is a deliberate new send.
            n.IdempotencyKey = $"{previousKey.Split(":r")[0]}:r{n.StatusChanges.Count(c => c.Source == "retry") + 1}";
            n.GatewayMessageId = null;
        }
        n.Status = NotificationStatus.Queued;
        n.Attempts = 0;
        n.NextAttemptAt = Now;
        n.LastError = null;
        n.StatusChanges.Add(new NotificationStatusChange { From = NotificationStatus.Failed, To = NotificationStatus.Queued, Source = "retry", Detail = $"Retried by {actor.DisplayName}", At = Now });
        AuditLog(db, actor, "Notification.Retry", nameof(Notification), n.Id, $"Retry of {n.TemplateCode} to {CaseScope.MaskEmail(n.Recipient)}", n.ApplicationId,
            new { idempotencyKey = previousKey }, new { idempotencyKey = n.IdempotencyKey });
        await SaveAsync(db, ct);
    }

    /// <summary>Manual notification from a template to a case contact (E9-04 AC2).</summary>
    public async Task SendManualAsync(Actor actor, Guid applicationId, string templateCode, string? message, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageNotifications);
        var v = new Validator().Text("templateCode", templateCode, 1, 80, "Template");
        if (templateCode == "manual.general") v.Text("message", message, 5, 2000, "Message");
        v.ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await db.Applications.Include(a => a.Applicant).Include(a => a.Event).Include(a => a.Permit).FirstOrDefaultAsync(a => a.Id == applicationId, ct)
                  ?? throw new NotFoundException();
        if (!await db.NotificationTemplates.AnyAsync(t => t.Code == templateCode && t.IsActive, ct))
            throw ValidationException.For("templateCode", "Choose an active template.");
        await NotificationOutbox.QueueAsync(db, Correlation, app, templateCode, new Dictionary<string, string> { ["Message"] = message?.Trim() ?? "", ["Reason"] = message?.Trim() ?? "" }, manual: true, ct);
        AuditLog(db, actor, "Notification.Manual", nameof(PermitApplication), app.Id, $"Manual {templateCode} queued for {app.Reference}", app.Id);
        await SaveAsync(db, ct);
    }

    /// <summary>Worker: sends due Queued messages. Idempotency keys make a crash-and-resend safe (E9-02 AC1).</summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        var maxAttempts = await rules.IntAsync(RuleKeys.NotificationMaxAttempts, ct);
        await using var db = await Dbs.CreateAsync(ct);
        var due = await db.Notifications.Where(n => n.Status == NotificationStatus.Queued && (n.NextAttemptAt == null || n.NextAttemptAt <= Now))
            .OrderBy(n => n.CreatedAt).Take(25).ToListAsync(ct);
        var sent = 0;
        foreach (var n in due)
        {
            AmbientCorrelation.Current = n.CorrelationId;
            var result = await gateway.SendAsync(n, ct);
            n.Attempts++;
            if (result.Ok)
            {
                n.GatewayMessageId = result.Value!.MessageId;
                Change(n, NotificationStatus.Sent, "gateway", $"Accepted as {n.GatewayMessageId}");
                n.SentAt = Now;
                n.LastError = null;
                sent++;
            }
            else if (result.Status is ExternalCallStatus.Unavailable or ExternalCallStatus.RateLimited && n.Attempts < maxAttempts)
            {
                n.LastError = result.Detail;
                n.NextAttemptAt = Now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, n.Attempts)));
            }
            else
            {
                n.LastError = result.Detail;
                n.FailedAt = Now;
                Change(n, NotificationStatus.Failed, "gateway", result.Detail);
            }
            await db.SaveChangesAsync(ct);
        }
        return sent;
    }

    /// <summary>Worker fallback: polls the gateway for messages Sent long ago without a webhook (E9-02 AC2).</summary>
    public async Task<int> PollStaleAsync(TimeSpan olderThan, CancellationToken ct)
    {
        await using var db = await Dbs.CreateAsync(ct);
        var cutoff = Now - olderThan;
        var stale = await db.Notifications.Where(n => n.Status == NotificationStatus.Sent && n.SentAt < cutoff && n.GatewayMessageId != null).Take(25).ToListAsync(ct);
        var updated = 0;
        foreach (var n in stale)
        {
            var result = await gateway.GetStatusAsync(n.GatewayMessageId!, n.ApplicationId, ct);
            if (!result.Ok) continue;
            if (Enum.TryParse<NotificationStatus>(result.Value!.Status, true, out var reported) && Rank(reported) > Rank(n.Status))
            {
                Apply(n, reported, "poll", "Status from GET /messages");
                updated++;
            }
        }
        await db.SaveChangesAsync(ct);
        return updated;
    }

    /// <summary>
    /// Signed delivery webhook (brief §7.1): HMAC + timestamp tolerance (401 when invalid), idempotent by eventId (200),
    /// never regressing Delivered, every receipt stored immutably.
    /// </summary>
    public async Task<DeliveryResult> ProcessDeliveryAsync(string rawBody, string? timestamp, string? signature, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(ct);
        var receipt = new WebhookReceipt { ReceivedAt = Now, CorrelationId = Correlation.CorrelationId };
        var tolerance = TimeSpan.FromMinutes(await rules.IntAsync(RuleKeys.WebhookToleranceMinutes, ct));

        async Task<DeliveryResult> Finish(int status, WebhookOutcome outcome, string reason)
        {
            receipt.Outcome = outcome;
            receipt.Reason = reason;
            db.WebhookReceipts.Add(receipt);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (outcome == WebhookOutcome.Accepted)
            {
                // Lost a race with an identical concurrent delivery: record it as the duplicate it is.
                db.ChangeTracker.Clear();
                db.WebhookReceipts.Add(new WebhookReceipt { ReceivedAt = Now, CorrelationId = receipt.CorrelationId, EventId = receipt.EventId, MessageId = receipt.MessageId,
                    ReportedStatus = receipt.ReportedStatus, OccurredAtUtc = receipt.OccurredAtUtc, Outcome = WebhookOutcome.Duplicate, Reason = "Concurrent duplicate" });
                await db.SaveChangesAsync(ct);
                return new DeliveryResult(200, WebhookOutcome.Duplicate, "Duplicate delivery ignored.");
            }
            return new DeliveryResult(status, outcome, reason);
        }

        if (string.IsNullOrEmpty(timestamp) || !long.TryParse(timestamp, out var unix))
            return await Finish(401, WebhookOutcome.RejectedTimestamp, "Missing or malformed X-Timestamp.");
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        if ((Now - sentAt).Duration() > tolerance)
            return await Finish(401, WebhookOutcome.RejectedTimestamp, $"Timestamp outside the {tolerance.TotalMinutes:0}-minute tolerance (replay protection).");
        if (string.IsNullOrEmpty(signature) || !verifier.Verify(timestamp, rawBody, signature))
            return await Finish(401, WebhookOutcome.RejectedSignature, "Signature does not match.");

        DeliveryBody? body;
        try
        {
            body = JsonSerializer.Deserialize<DeliveryBody>(rawBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            body = null;
        }
        if (body is null || string.IsNullOrWhiteSpace(body.EventId) || string.IsNullOrWhiteSpace(body.MessageId) ||
            !Enum.TryParse<NotificationStatus>(body.Status, true, out var status) || status is not (NotificationStatus.Delivered or NotificationStatus.Failed))
            return await Finish(400, WebhookOutcome.RejectedPayload, "Payload is not a valid delivery event.");

        receipt.EventId = body.EventId;
        receipt.MessageId = body.MessageId;
        receipt.ReportedStatus = status.ToString();
        receipt.OccurredAtUtc = body.OccurredAtUtc;

        if (await db.WebhookReceipts.AnyAsync(r => r.EventId == body.EventId && (r.Outcome == WebhookOutcome.Accepted || r.Outcome == WebhookOutcome.Ignored), ct))
            return await Finish(200, WebhookOutcome.Duplicate, "Duplicate event ignored (idempotent).");

        var notification = await db.Notifications.Include(n => n.StatusChanges).FirstOrDefaultAsync(n => n.GatewayMessageId == body.MessageId, ct);
        if (notification is null) return await Finish(200, WebhookOutcome.Ignored, "Unknown message id.");
        receipt.NotificationId = notification.Id;

        var lastEventAt = notification.StatusChanges.Where(c => c.Source == "webhook").Select(c => (DateTimeOffset?)c.At).Max();
        if (notification.Status == NotificationStatus.Delivered && status != NotificationStatus.Delivered)
            return await Finish(200, WebhookOutcome.Ignored, "Out-of-order event: Delivered is never regressed.");
        if (Rank(status) < Rank(notification.Status) || (lastEventAt is { } last && body.OccurredAtUtc < last && status != NotificationStatus.Delivered))
            return await Finish(200, WebhookOutcome.Ignored, "Older event than the current status; kept for the record.");

        Apply(notification, status, "webhook", $"Event {body.EventId} at {body.OccurredAtUtc:O}", body.OccurredAtUtc);
        return await Finish(200, WebhookOutcome.Accepted, $"Status set to {status}.");
    }

    public async Task<MessagingMonitor> MonitorAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageNotifications);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var since = Now.AddDays(-7);
        var counts = await db.Notifications.AsNoTracking().GroupBy(n => n.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var oldestQueued = await db.Notifications.AsNoTracking().Where(n => n.Status == NotificationStatus.Queued).MinAsync(n => (DateTimeOffset?)n.CreatedAt, ct);
        var recent = await db.Notifications.AsNoTracking().Where(n => n.CreatedAt >= since).ToListAsync(ct);
        var failed = await db.Notifications.AsNoTracking().Where(n => n.Status == NotificationStatus.Failed).OrderByDescending(n => n.FailedAt).Take(20).ToListAsync(ct);
        var receipts = await db.WebhookReceipts.AsNoTracking().OrderByDescending(r => r.ReceivedAt).Take(30).ToListAsync(ct);
        var mask = CaseScope.MasksContacts(actor);
        var delivered = recent.Count(n => n.Status == NotificationStatus.Delivered);
        return new MessagingMonitor(
            counts.ToDictionary(c => c.Key, c => c.Count),
            oldestQueued is { } o ? Now - o : null,
            recent.Count == 0 ? null : Math.Round(100.0 * delivered / recent.Count, 1),
            failed.Select(n => Row(n, mask)).ToList(),
            receipts);
    }

    private void Change(Notification n, NotificationStatus to, string source, string detail, DateTimeOffset? at = null)
    {
        n.StatusChanges.Add(new NotificationStatusChange { From = n.Status, To = to, Source = source, Detail = detail, At = at ?? Now });
        n.Status = to;
    }

    private void Apply(Notification n, NotificationStatus status, string source, string detail, DateTimeOffset? at = null)
    {
        Change(n, status, source, detail, at);
        if (status == NotificationStatus.Delivered) n.DeliveredAt = at ?? Now;
        if (status == NotificationStatus.Failed) { n.FailedAt = at ?? Now; n.LastError = "The gateway reported a failed delivery."; }
    }

    private static int Rank(NotificationStatus s) => s switch
    {
        NotificationStatus.Queued => 0,
        NotificationStatus.Sent => 1,
        NotificationStatus.Failed => 2,
        NotificationStatus.Delivered => 3,
        _ => 0,
    };

    private static NotificationRow Row(Notification n, bool mask) => new(n.Id, n.ApplicationId, n.ApplicationReference, n.Channel,
        mask ? (n.Recipient.Contains('@') ? CaseScope.MaskEmail(n.Recipient) : CaseScope.MaskPhone(n.Recipient)) : n.Recipient,
        n.TemplateCode, n.Subject, n.Status, n.Attempts, n.LastError, n.GatewayMessageId, n.CreatedAt, n.SentAt, n.DeliveredAt, n.Manual, n.CorrelationId, n.Version);

    private sealed record DeliveryBody(string? EventId, string? MessageId, string? Status, DateTimeOffset OccurredAtUtc);

    public static string Signature(string secret, string timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body)));
}

public sealed record MessagingMonitor(IReadOnlyDictionary<NotificationStatus, int> Counts, TimeSpan? OldestQueuedAge, double? DeliveryRatePercent,
    IReadOnlyList<NotificationRow> Failed, IReadOnlyList<WebhookReceipt> Receipts);
