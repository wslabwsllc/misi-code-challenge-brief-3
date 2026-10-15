using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record ReviewQueueFilter(string? Search = null, Guid? CategoryId = null, Guid? CemeteryId = null, int Page = 1);

public sealed record InboxCounts(int Open, int Answered, int Overdue, int All);

/// <summary>Read queries for the review queue (page 05) and the information request inbox (page 06).</summary>
public sealed partial class ReviewService
{
    /// <summary>Submitted cases, oldest submission first, with search plus category and cemetery filters (E5-01 AC1).</summary>
    public async Task<(IReadOnlyList<ReviewQueueRow> Rows, int Total)> QueueAsync(Actor actor, ReviewQueueFilter filter, CancellationToken ct = default)
    {
        actor.Require(Cap.ReviewEligibility);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = Scoped(db, actor).AsNoTracking().Where(a => a.Status == CaseStatus.Submitted);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            q = q.Where(a => a.Reference.ToLower().Contains(term) || a.Applicant!.FullName.ToLower().Contains(term) || a.Event!.Title.ToLower().Contains(term));
        }
        if (filter.CategoryId is { } category) q = q.Where(a => a.Event!.CategoryId == category);
        if (filter.CemeteryId is { } cemetery) q = q.Where(a => a.Event!.CemeteryId == cemetery);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(a => a.SubmittedAt).ThenBy(a => a.Reference)
            .Skip((Math.Max(filter.Page, 1) - 1) * Limits.PageSize).Take(Limits.PageSize)
            .Select(a => new
            {
                a.Id, a.Reference, Name = a.Applicant!.FullName, a.Event!.Title, Category = a.Event.Category!.Name, Cemetery = a.Event.Cemetery!.Name,
                a.Event.StartUtc, Tz = a.Event.Cemetery.TimeZoneId, a.SubmittedAt,
                Verification = db.RegistryVerifications.Where(v => v.ApplicationId == a.Id).OrderByDescending(v => v.CreatedAt).FirstOrDefault(),
                Open = db.InformationRequests.Count(r => r.ApplicationId == a.Id && r.Status == InformationRequestStatus.Open),
            }).ToListAsync(ct);
        return (rows.Select(r => new ReviewQueueRow(r.Id, r.Reference, r.Name, r.Title, r.Category, r.Cemetery, r.StartUtc, r.Tz, r.SubmittedAt,
            r.Verification?.Outcome, r.Verification?.Eligibility, r.Open)).ToList(), total);
    }

    /// <summary>Counts for the inbox filters (open, answered, overdue, all) within the caller's scope.</summary>
    public async Task<InboxCounts> InboxCountsAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.TrackInformationRequests);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var visible = Scoped(db, actor).Select(a => a.Id);
        var q = db.InformationRequests.AsNoTracking().Where(r => visible.Contains(r.ApplicationId));
        var open = await q.CountAsync(r => r.Status == InformationRequestStatus.Open, ct);
        var answered = await q.CountAsync(r => r.Status == InformationRequestStatus.Answered, ct);
        var overdue = await q.CountAsync(r => r.Status == InformationRequestStatus.Open && r.DueDate < today, ct);
        return new InboxCounts(open, answered, overdue, await q.CountAsync(ct));
    }

    /// <summary>Files attached to information request responses, keyed by request (only requests the caller may see).</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<AttachmentView>>> ResponseFilesAsync(Actor actor, IReadOnlyCollection<Guid> requestIds, CancellationToken ct = default)
    {
        actor.Require(Cap.TrackInformationRequests);
        if (requestIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<AttachmentView>>();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var visible = Scoped(db, actor).Select(a => a.Id);
        var files = await (from att in db.Attachments.AsNoTracking()
                           join app in db.Applications.AsNoTracking() on att.ApplicationId equals app.Id
                           where att.InformationRequestId != null && requestIds.Contains(att.InformationRequestId.Value) && visible.Contains(app.Id)
                           orderby att.UploadedAt
                           select new { att, app.Reference }).ToListAsync(ct);
        var canDownload = FileService.CanDownload(actor);
        return files.GroupBy(f => f.att.InformationRequestId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AttachmentView>)g.Select(f => FileService.View(f.att, f.Reference, canDownload)).ToList());
    }
}
