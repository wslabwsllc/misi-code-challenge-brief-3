using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

/// <summary>A case the caller may attach documents to (own case in Draft or Needs Information, A-26).</summary>
public sealed record UploadTarget(Guid Id, string Reference, string Title, CaseStatus Status, uint Version, IReadOnlyList<DocumentType> RequiredDocuments);

/// <summary>Read queries used by the intake wizard and the Document Vault (pages 03 and 23).</summary>
public sealed partial class CaseService
{
    /// <summary>
    /// Cases where an upload would be accepted by <see cref="UploadDocumentAsync"/>: owned by the caller and editable.
    /// Roles without intake rights get an empty list (uploads are refused for them anyway).
    /// </summary>
    public async Task<IReadOnlyList<UploadTarget>> UploadTargetsAsync(Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.CreateApplication)) return [];
        await using var db = await Dbs.CreateAsync(actor, ct);
        var rows = await Scoped(db, actor).AsNoTracking()
            .Where(a => a.OwnerUserId == actor.UserId && (a.Status == CaseStatus.Draft || a.Status == CaseStatus.NeedsInformation))
            .OrderByDescending(a => a.UpdatedAt).Take(100)
            .Select(a => new { a.Id, a.Reference, a.Event!.Title, a.Status, a.Version, Required = a.Event.Category == null ? null : a.Event.Category.RequiredDocuments })
            .ToListAsync(ct);
        return rows.Select(r => new UploadTarget(r.Id, r.Reference, r.Title, r.Status, r.Version, r.Required ?? [])).ToList();
    }
}
