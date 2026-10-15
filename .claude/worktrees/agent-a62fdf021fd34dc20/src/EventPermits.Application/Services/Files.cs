using System.Globalization;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record UploadFile(string FileName, string ContentType, byte[] Content);

public sealed record AttachmentView(Guid Id, Guid? ApplicationId, string? ApplicationReference, Guid? IncidentId, AttachmentPurpose Purpose, DocumentType DocumentType,
    string FileName, string ContentType, long SizeBytes, string Sha256, string UploadedBy, DateTimeOffset UploadedAt, bool CanDownload);

public sealed record SignedLink(string Url, DateTimeOffset ExpiresAt);

public sealed record VaultFilter(string? Search = null, AttachmentPurpose? Purpose = null, DocumentType? DocumentType = null, DateOnly? From = null, DateOnly? To = null, int Page = 1);

/// <summary>
/// Attachments in private object storage (FR-15, E3-04, E3-05). Download links have two layers (A-16): a signed app
/// link bound to user, file and expiry; redeeming it re-checks access, audits, then redirects to a 60-second
/// storage-native pre-signed URL. Auditors never receive links (metadata only).
/// </summary>
public sealed partial class FileService(AppDb dbs, ICorrelationContext correlation, IObjectStore store, ISigner signer, RuleService rules)
    : ServiceBase(dbs, correlation)
{
    public const string LinkPurpose = "download-link";

    // Primary-constructor parameters are visible only in this declaration; the partial in Files.Queries.cs uses these.
    private IObjectStore Store => store;
    private ISigner Signer => signer;

    /// <summary>Validates and stores bytes under a random key; the caller adds the Attachment row in its own unit of work.</summary>
    public async Task<Attachment> StoreAsync(Actor actor, UploadFile file, AttachmentPurpose purpose, DocumentType type, bool imagesOnly, CancellationToken ct)
    {
        var maxBytes = Math.Min(Limits.MaxAttachmentBytes, (long)(await rules.IntAsync(RuleKeys.MaxUploadMegabytes, ct)) * 1024 * 1024);
        var checkedFile = FileSignatures.Validate(file.FileName, file.ContentType, file.Content, maxBytes, imagesOnly ? FileSignatures.ImagesOnly : FileSignatures.All);
        var folder = purpose == AttachmentPurpose.PermitPdf ? "permits" : "attachments";
        var key = $"{folder}/{Guid.NewGuid():N}";
        await store.PutAsync(key, file.Content, checkedFile.ContentType, ct);
        return new Attachment
        {
            Purpose = purpose,
            DocumentType = type,
            FileName = checkedFile.SafeName,
            ContentType = checkedFile.ContentType,
            SizeBytes = checkedFile.Size,
            Sha256 = checkedFile.Sha256,
            ObjectKey = key,
            UploadedBy = actor.DisplayName,
            UploadedAt = Now,
        };
    }

    public async Task<SignedLink> IssueLinkAsync(Actor actor, Guid attachmentId, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        var att = await LoadVisibleAsync(db, actor, attachmentId, ct);
        if (!CanDownload(actor)) throw new ForbiddenException("Your role can see file details but not download file contents.");
        var minutes = await rules.IntAsync(RuleKeys.DownloadLinkMinutes, ct);
        var expires = Now.AddMinutes(minutes);
        var exp = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sig = signer.Sign(LinkPurpose, Payload(att.Id, actor.UserId, exp));
        AuditLog(db, actor, "Attachment.LinkIssued", nameof(Attachment), att.Id, $"Download link for {att.FileName} (expires in {minutes} min)", att.ApplicationId);
        await SaveAsync(db, ct);
        return new SignedLink($"/files/{att.Id}?exp={exp}&sig={sig}", expires);
    }

    /// <summary>Redeems an app link: checks signature, expiry, the user it was issued to and current access; audits; returns the storage URL.</summary>
    public async Task<string> RedeemAsync(Actor actor, Guid attachmentId, string? exp, string? sig, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        if (string.IsNullOrEmpty(exp) || string.IsNullOrEmpty(sig) || !long.TryParse(exp, out var unix))
            throw new ForbiddenException("This download link is incomplete.", ErrorCodes.LinkInvalid);
        if (!signer.Verify(LinkPurpose, Payload(attachmentId, actor.UserId, exp), sig))
        {
            AuditLog(db, actor, "Attachment.LinkRejected", nameof(Attachment), attachmentId, "Download refused: link signature invalid or issued to another user");
            await SaveAsync(db, ct);
            throw new ForbiddenException("This download link is not valid for your account.", ErrorCodes.LinkInvalid);
        }
        var att = await LoadVisibleAsync(db, actor, attachmentId, ct); // current access, not access at issue time
        if (DateTimeOffset.FromUnixTimeSeconds(unix) < Now)
        {
            AuditLog(db, actor, "Attachment.LinkExpired", nameof(Attachment), att.Id, $"Expired link used for {att.FileName}", att.ApplicationId);
            await SaveAsync(db, ct);
            throw new ForbiddenException("This download link has expired. Generate a new one.", ErrorCodes.LinkExpired);
        }
        if (!CanDownload(actor)) throw new ForbiddenException("Your role can see file details but not download file contents.");
        AuditLog(db, actor, "Attachment.Downloaded", nameof(Attachment), att.Id, $"Downloaded {att.FileName}", att.ApplicationId);
        await SaveAsync(db, ct);
        return store.PresignGet(att.ObjectKey, TimeSpan.FromSeconds(60), att.FileName, att.ContentType);
    }

    public async Task<(IReadOnlyList<AttachmentView> Rows, int Total)> VaultAsync(Actor actor, VaultFilter filter, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewDocuments);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var visibleCases = Scoped(db, actor).Select(a => a.Id);
        var q = from att in db.Attachments.AsNoTracking()
                join app in db.Applications.AsNoTracking() on att.ApplicationId equals app.Id
                where visibleCases.Contains(app.Id)
                select new { att, app.Reference };
        if (filter.Purpose is { } p) q = q.Where(x => x.att.Purpose == p);
        if (filter.DocumentType is { } d) q = q.Where(x => x.att.DocumentType == d);
        if (filter.From is { } from) q = q.Where(x => x.att.UploadedAt >= new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (filter.To is { } to) q = q.Where(x => x.att.UploadedAt < new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            q = q.Where(x => x.att.FileName.ToLower().Contains(term) || x.Reference.ToLower().Contains(term) || x.att.UploadedBy.ToLower().Contains(term));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.att.UploadedAt).Skip((Math.Max(1, filter.Page) - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct);
        var canDownload = CanDownload(actor);
        return (rows.Select(x => View(x.att, x.Reference, canDownload)).ToList(), total);
    }

    public async Task<IReadOnlyList<AuditEvent>> HistoryAsync(Actor actor, Guid attachmentId, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        var att = await LoadVisibleAsync(db, actor, attachmentId, ct);
        return await db.AuditEvents.AsNoTracking().Where(a => a.EntityType == nameof(Attachment) && a.EntityId == att.Id.ToString())
            .OrderByDescending(a => a.At).ToListAsync(ct);
    }

    public static AttachmentView View(Attachment a, string? reference, bool canDownload) => new(a.Id, a.ApplicationId, reference, a.IncidentId, a.Purpose, a.DocumentType,
        a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.UploadedBy, a.UploadedAt, canDownload);

    public static bool CanDownload(Actor actor) => actor.Can(Cap.DownloadDocuments) || actor.Has(Roles.EventAttendant);

    /// <summary>404 when the file or its case is outside the caller's scope (A-06).</summary>
    private static async Task<Attachment> LoadVisibleAsync(AppDbContext db, Actor actor, Guid attachmentId, CancellationToken ct)
    {
        var att = await db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId, ct) ?? throw new NotFoundException();
        if (att.ApplicationId is not { } appId || !await Scoped(db, actor).AnyAsync(a => a.Id == appId, ct)) throw new NotFoundException();
        // Attendants only see incident photos and permits of their assigned events, not applicant documents (A-22).
        if (actor.Has(Roles.EventAttendant) && !actor.Can(Cap.ViewDocuments) && att.Purpose is not (AttachmentPurpose.IncidentPhoto or AttachmentPurpose.PermitPdf))
            throw new NotFoundException();
        return att;
    }

    private static string Payload(Guid id, string userId, string exp) => $"{id:N}.{userId}.{exp}";
}
