using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

/// <summary>Document Vault query (E3-05 AC1): filters by case, type, uploader and upload date, server paged.</summary>
public sealed record VaultQuery(
    string? Search = null, string? CaseReference = null, string? Uploader = null, AttachmentPurpose? Purpose = null, DocumentType? DocumentType = null,
    DateOnly? From = null, DateOnly? To = null, int Page = 1);

public sealed record FilePreview(byte[] Content, string ContentType, string FileName);

public sealed partial class FileService
{
    public async Task<(IReadOnlyList<AttachmentView> Rows, int Total)> VaultPageAsync(Actor actor, VaultQuery query, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewDocuments);
        var v = new Validator()
            .Require(query.Page is >= 1 and <= 10_000, "page", "Page must be between 1 and 10000.")
            .Require(query.From is null || query.To is null || query.From <= query.To, "to", "The end date must be on or after the start date.")
            .Text("search", query.Search, 0, 100, "Search", false)
            .Text("case", query.CaseReference, 0, 40, "Case reference", false)
            .Text("uploader", query.Uploader, 0, 120, "Uploader", false);
        v.ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var visibleCases = Scoped(db, actor).Select(a => a.Id);
        var q = from att in db.Attachments.AsNoTracking()
                join app in db.Applications.AsNoTracking() on att.ApplicationId equals app.Id
                where visibleCases.Contains(app.Id)
                select new { att, app.Reference };
        if (query.Purpose is { } p) q = q.Where(x => x.att.Purpose == p);
        if (query.DocumentType is { } d) q = q.Where(x => x.att.DocumentType == d);
        if (query.From is { } from) q = q.Where(x => x.att.UploadedAt >= new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (query.To is { } to) q = q.Where(x => x.att.UploadedAt < new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (!string.IsNullOrWhiteSpace(query.CaseReference))
        {
            var term = query.CaseReference.Trim().ToLower();
            q = q.Where(x => x.Reference.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(query.Uploader))
        {
            var term = query.Uploader.Trim().ToLower();
            q = q.Where(x => x.att.UploadedBy.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            q = q.Where(x => x.att.FileName.ToLower().Contains(term) || x.Reference.ToLower().Contains(term) || x.att.UploadedBy.ToLower().Contains(term));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.att.UploadedAt).ThenBy(x => x.att.FileName)
            .Skip((query.Page - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct);
        var canDownload = CanDownload(actor);
        return (rows.Select(x => View(x.att, x.Reference, canDownload)).ToList(), total);
    }

    /// <summary>
    /// Redeems the same signed app link as <see cref="RedeemAsync"/> but returns the bytes for an inline preview
    /// (images and PDFs) served from the application origin. Same checks: signature bound to the user, current
    /// access, expiry, download right; every outcome is audited.
    /// </summary>
    public async Task<FilePreview> PreviewAsync(Actor actor, Guid attachmentId, string? exp, string? sig, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        if (string.IsNullOrEmpty(exp) || string.IsNullOrEmpty(sig) || !long.TryParse(exp, out var unix))
            throw new ForbiddenException("This preview link is incomplete.", ErrorCodes.LinkInvalid);
        if (!Signer.Verify(LinkPurpose, Payload(attachmentId, actor.UserId, exp), sig))
        {
            AuditLog(db, actor, "Attachment.LinkRejected", nameof(Attachment), attachmentId, "Preview refused: link signature invalid or issued to another user");
            await SaveAsync(db, ct);
            throw new ForbiddenException("This preview link is not valid for your account.", ErrorCodes.LinkInvalid);
        }
        var att = await LoadVisibleAsync(db, actor, attachmentId, ct);
        if (DateTimeOffset.FromUnixTimeSeconds(unix) < Now)
        {
            AuditLog(db, actor, "Attachment.LinkExpired", nameof(Attachment), att.Id, $"Expired link used to preview {att.FileName}", att.ApplicationId);
            await SaveAsync(db, ct);
            throw new ForbiddenException("This preview link has expired. Generate a new one.", ErrorCodes.LinkExpired);
        }
        if (!CanDownload(actor)) throw new ForbiddenException("Your role can see file details but not file contents.");
        var content = await Store.GetAsync(att.ObjectKey, ct);
        AuditLog(db, actor, "Attachment.Previewed", nameof(Attachment), att.Id, $"Previewed {att.FileName}", att.ApplicationId);
        await SaveAsync(db, ct);
        return new FilePreview(content, att.ContentType, att.FileName);
    }
}
