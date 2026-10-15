using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record PermitVersionView(int VersionNumber, PermitVersionStatus Status, string IssuedByName, DateTimeOffset IssuedAt, string VenueName,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Conditions, string? ReissueReason, string? RevokedReason, string? RevokedBy, DateTimeOffset? RevokedAt,
    DateTimeOffset TokenExpiresAt, string PdfSha256, Guid? PdfAttachmentId);

public sealed record PermitDetail(Guid PermitId, Guid ApplicationId, string Number, string Reference, string Title, CaseStatus CaseStatus, uint CaseVersion,
    PermitStatus Status, int CurrentVersion, string? TimeZoneId, IReadOnlyList<PermitVersionView> Versions, bool CanDownload);

/// <summary>Read query for the Permit Center (page 11): one permit with its version history and the case version needed for commands.</summary>
public sealed partial class PermitService
{
    public async Task<PermitDetail> DetailAsync(Actor actor, Guid permitId, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewPermits);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var permit = await db.Permits.AsNoTracking().Include(p => p.Versions).FirstOrDefaultAsync(p => p.Id == permitId, ct) ?? throw new NotFoundException();
        var app = await Scoped(db, actor).AsNoTracking().Include(a => a.Event!).ThenInclude(e => e!.Cemetery)
            .FirstOrDefaultAsync(a => a.Id == permit.ApplicationId, ct) ?? throw new NotFoundException();
        var keys = permit.Versions.Select(v => v.PdfObjectKey).ToList();
        var pdfs = await db.Attachments.AsNoTracking().Where(a => keys.Contains(a.ObjectKey)).ToDictionaryAsync(a => a.ObjectKey, a => a.Id, ct);
        var versions = permit.Versions.OrderByDescending(v => v.VersionNumber).Select(v => new PermitVersionView(v.VersionNumber, v.Status, v.IssuedByName, v.IssuedAt,
            v.VenueName, v.StartUtc, v.EndUtc, v.Conditions, v.ReissueReason, v.RevokedReason, v.RevokedBy, v.RevokedAt, v.TokenExpiresAt, v.PdfSha256,
            pdfs.TryGetValue(v.PdfObjectKey, out var id) ? id : null)).ToList();
        return new PermitDetail(permit.Id, app.Id, permit.Number, app.Reference, app.Event?.Title ?? "", app.Status, app.Version, permit.Status, permit.CurrentVersion,
            app.Event?.Cemetery?.TimeZoneId, versions, FileService.CanDownload(actor));
    }
}
