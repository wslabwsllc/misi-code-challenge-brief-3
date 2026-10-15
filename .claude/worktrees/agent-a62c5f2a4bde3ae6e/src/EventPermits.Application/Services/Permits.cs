using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public enum TokenResult { Valid, Expired, Revoked, Superseded, Cancelled, NotFound, OutsideWindow, Malformed }

public sealed record TokenCheck(TokenResult Result, string Message, Guid? ApplicationId, string? Reference, string? PermitNumber, int? Version, string? EventTitle,
    string? VenueName, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, string? TimeZoneId, CaseStatus? CaseStatus, DateTimeOffset? WindowOpensAt, bool AssignedToYou);

public sealed record PermitRow(Guid PermitId, Guid ApplicationId, string Number, string Reference, string Title, string Venue, DateTimeOffset StartUtc, string TimeZoneId,
    PermitStatus Status, int CurrentVersion, PermitVersionStatus CurrentVersionStatus, DateTimeOffset IssuedAt, string IssuedBy, Guid? PdfAttachmentId);

public sealed record IssueQueueRow(Guid ApplicationId, string Reference, string Title, string Venue, DateTimeOffset? StartUtc, string? TimeZoneId, bool SevereWeather, bool WeatherAcknowledged, uint Version);

/// <summary>Permit issuance, versions, revocation and token verification (FR-09, FR-10, E7-02..E7-04, A-11, A-12).</summary>
public sealed partial class PermitService(AppDb dbs, ICorrelationContext correlation, FileService files, IPdfRenderer pdf, ISigner signer, RuleService rules)
    : ServiceBase(dbs, correlation)
{
    public const string TokenPurpose = "permit-token";

    public static readonly string[] DefaultReadiness =
    [
        "Venue set up as described in the reservation",
        "Requested resources delivered and counted",
        "Accessible route from parking to venue is clear",
        "On-site contact confirmed",
        "Permit displayed at the entrance",
    ];

    public static readonly string DefaultConditions = string.Join('\n',
        "Use only the reserved venue and the reserved times, including setup and teardown.",
        "Keep pathways and graves clear; no stakes or fixings without staff approval.",
        "Amplified sound must stay below conversational level outside the venue.",
        "Follow staff instructions and the site's quiet hours.",
        "Report any incident to the event attendant immediately.");

    public async Task<IReadOnlyList<IssueQueueRow>> ReadyToIssueAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ManagePermits);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var apps = await db.Applications.AsNoTracking().Where(a => a.Status == CaseStatus.Approved)
            .Include(a => a.Event!).ThenInclude(e => e!.PreferredVenue).Include(a => a.Event!).ThenInclude(e => e!.Cemetery)
            .OrderBy(a => a.Event!.StartUtc).ToListAsync(ct);
        var rows = new List<IssueQueueRow>();
        foreach (var a in apps)
        {
            var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.ApplicationId == a.Id && r.Status != ReservationStatus.Released, ct);
            var severe = await CaseFacts.SevereWeatherAsync(db, a.Id, reservation, ct);
            rows.Add(new IssueQueueRow(a.Id, a.Reference, a.Event?.Title ?? "", a.Event?.PreferredVenue?.Name ?? "", a.Event?.StartUtc, a.Event?.Cemetery?.TimeZoneId, severe, a.WeatherAcknowledgedAt is not null, a.Version));
        }
        return rows;
    }

    public async Task<(IReadOnlyList<PermitRow> Rows, int Total)> ListAsync(Actor actor, string? search, PermitStatus? status, int page, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewPermits);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = from p in db.Permits.AsNoTracking()
                join a in db.Applications.AsNoTracking() on p.ApplicationId equals a.Id
                join v in db.PermitVersions.AsNoTracking() on new { p.Id, V = p.CurrentVersion } equals new { Id = v.PermitId, V = v.VersionNumber }
                select new { p, a.Reference, a.Event!.Title, Venue = a.Event.PreferredVenue!.Name, Tz = a.Event.Cemetery!.TimeZoneId, v };
        if (status is { } s) q = q.Where(x => x.p.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            q = q.Where(x => x.p.Number.Contains(term) || x.Reference.Contains(term) || x.Title.ToUpper().Contains(term));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.v.IssuedAt).Skip((Math.Max(page, 1) - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct);
        var keys = rows.Select(r => r.v.PdfObjectKey).ToList();
        var pdfs = await db.Attachments.AsNoTracking().Where(a => keys.Contains(a.ObjectKey)).ToDictionaryAsync(a => a.ObjectKey, a => a.Id, ct);
        return (rows.Select(x => new PermitRow(x.p.Id, x.p.ApplicationId, x.p.Number, x.Reference, x.Title, x.Venue, x.v.StartUtc, x.Tz, x.p.Status, x.p.CurrentVersion,
            x.v.Status, x.v.IssuedAt, x.v.IssuedByName, pdfs.TryGetValue(x.v.PdfObjectKey, out var pdfId) ? pdfId : null)).ToList(), total);
    }

    public async Task<IReadOnlyList<PermitVersion>> VersionsAsync(Actor actor, Guid permitId, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewPermits);
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.PermitVersions.AsNoTracking().Where(v => v.PermitId == permitId).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
    }

    /// <summary>T10: immutable PermitVersion with PDF and signed token; issuer recorded; notification queued (never rolled back by a gateway failure).</summary>
    public async Task<CaseRef> IssueAsync(Actor actor, Guid applicationId, uint? version, string? conditions, CancellationToken ct = default)
    {
        actor.Require(Cap.ManagePermits);
        new Validator().Text("conditions", conditions, 0, 3000, "Conditions", false).ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadForPermitAsync(db, actor, applicationId, ct);
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.IssuePermit);
        var reservation = app.Reservations.First(r => r.Status != ReservationStatus.Released);
        if (await CaseFacts.SevereWeatherAsync(db, app.Id, reservation, ct) && app.WeatherAcknowledgedAt is null)
            throw new BusinessRuleException(ErrorCodes.WeatherNotAcknowledged, "A severe weather advisory is in force for the event date. A manager must acknowledge it before the permit is issued.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var permit = new Permit { ApplicationId = app.Id, Number = await ReferenceGenerator.NextAsync(db, Limits.PermitPrefix, Now.Year, ct), CurrentVersion = 0 };
        db.Permits.Add(permit);
        app.Permit = permit;
        var attachment = await NewVersionAsync(db, actor, app, permit, reservation, string.IsNullOrWhiteSpace(conditions) ? DefaultConditions : conditions.Trim(), null, ct);
        foreach (var (label, i) in DefaultReadiness.Select((l, i) => (l, i)))
            if (!app.ReadinessItems.Any(r => r.Label == label)) db.ReadinessItems.Add(new ReadinessChecklistItem { ApplicationId = app.Id, Label = label, Order = i });
        ApplyTransition(db, actor, app, Transition.IssuePermit, $"Permit {permit.Number} v1 issued");
        AuditLog(db, actor, "Permit.Issued", nameof(Permit), permit.Id, $"{permit.Number} v1 issued by {actor.DisplayName} ({attachment.FileName})", app.Id);
        await NotificationOutbox.QueueAsync(db, Correlation, app, "permit.issued", new Dictionary<string, string> { ["PermitNumber"] = permit.Number }, ct: ct);
        await SaveAsync(db, ct);
        await tx.CommitAsync(ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>New version (reason required); the previous version is superseded and its token stops working.</summary>
    public async Task<CaseRef> ReissueAsync(Actor actor, Guid applicationId, uint? version, string? reason, string? conditions, CancellationToken ct = default)
    {
        actor.Require(Cap.ManagePermits);
        new Validator().Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Reissue reason").Text("conditions", conditions, 0, 3000, "Conditions", false).ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadForPermitAsync(db, actor, applicationId, ct);
        ExpectVersion(db, app, version);
        if (app.Status != CaseStatus.Permitted || app.Permit is null)
            throw new BusinessRuleException(ErrorCodes.InvalidTransition, "A permit can be reissued only while the case is Permitted.");
        var permit = app.Permit;
        var current = permit.Versions.First(v => v.VersionNumber == permit.CurrentVersion);
        if (current.Status == PermitVersionStatus.Active) current.Status = PermitVersionStatus.Superseded;
        var reservation = app.Reservations.First(r => r.Status != ReservationStatus.Released);
        await NewVersionAsync(db, actor, app, permit, reservation, string.IsNullOrWhiteSpace(conditions) ? current.Conditions : conditions.Trim(), reason!.Trim(), ct);
        permit.Status = PermitStatus.Active;
        Touch(db, app);
        AuditLog(db, actor, "Permit.Reissued", nameof(Permit), permit.Id, $"{permit.Number} v{permit.CurrentVersion} issued; v{current.VersionNumber} superseded", app.Id, reason: reason.Trim());
        await NotificationOutbox.QueueAsync(db, Correlation, app, "permit.reissued", new Dictionary<string, string> { ["PermitNumber"] = permit.Number, ["Reason"] = reason.Trim() }, ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>A-11: revokes the current version with a justification; the case stays Permitted until reissue or cancellation.</summary>
    public async Task<CaseRef> RevokeAsync(Actor actor, Guid applicationId, uint? version, string? reason, CancellationToken ct = default)
    {
        actor.Require(Cap.ManagePermits);
        new Validator().Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Revocation justification").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadForPermitAsync(db, actor, applicationId, ct);
        ExpectVersion(db, app, version);
        if (app.Status != CaseStatus.Permitted || app.Permit is null || app.Permit.Status != PermitStatus.Active)
            throw new BusinessRuleException(ErrorCodes.InvalidTransition, "Only an active permit of a Permitted case can be revoked.");
        var current = app.Permit.Versions.First(v => v.VersionNumber == app.Permit.CurrentVersion);
        current.Status = PermitVersionStatus.Revoked;
        current.RevokedAt = Now;
        current.RevokedBy = actor.DisplayName;
        current.RevokedReason = reason!.Trim();
        app.Permit.Status = PermitStatus.Revoked;
        Touch(db, app);
        AuditLog(db, actor, "Permit.Revoked", nameof(Permit), app.Permit.Id, $"{app.Permit.Number} v{current.VersionNumber} revoked", app.Id, reason: reason.Trim());
        await NotificationOutbox.QueueAsync(db, Correlation, app, "permit.revoked", new Dictionary<string, string> { ["PermitNumber"] = app.Permit.Number, ["Reason"] = reason.Trim() }, ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>Verifies a QR token or typed short code (E7-04): distinct results for every invalid case.</summary>
    public async Task<TokenCheck> VerifyAsync(Actor actor, string? input, CancellationToken ct = default)
    {
        actor.Require(Cap.VerifyPermitToken);
        var text = (input ?? "").Trim();
        if (text.Length == 0) throw ValidationException.For("code", "Enter the short code or paste the QR payload.");
        await using var db = await Dbs.CreateAsync(actor, ct);
        PermitVersion? version;
        if (text.Contains('.'))
        {
            var parts = text.Split('.');
            if (parts.Length != 2 || !signer.Verify(TokenPurpose, parts[0], parts[1]))
                return Empty(TokenResult.Malformed, "This is not a valid permit token (signature check failed).");
            var hash = Hash(text);
            version = await db.PermitVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TokenHash == hash, ct);
        }
        else
        {
            var code = text.ToUpperInvariant().Replace("-", "").Replace(" ", "");
            version = await db.PermitVersions.AsNoTracking().FirstOrDefaultAsync(v => v.ShortCode == code, ct);
        }
        if (version is null) return Empty(TokenResult.NotFound, "No permit matches this code.");
        var permit = await db.Permits.AsNoTracking().FirstAsync(p => p.Id == version.PermitId, ct);
        var app = await db.Applications.AsNoTracking().Include(a => a.Event!).ThenInclude(e => e!.Cemetery).FirstAsync(a => a.Id == permit.ApplicationId, ct);
        var assigned = await db.AttendantAssignments.AsNoTracking().AnyAsync(a => a.ApplicationId == app.Id && a.UserId == actor.UserId && a.Active, ct);
        // Attendants may only verify their own events (A-22): an unassigned code reveals nothing.
        if (actor.Has(Roles.EventAttendant) && !actor.Has(Roles.OperationsManager) && !assigned)
            return Empty(TokenResult.NotFound, "No permit for your assigned events matches this code.");
        var opensBefore = await rules.IntAsync(RuleKeys.CheckInOpensMinutesBefore, ct);
        var opens = version.StartUtc.AddMinutes(-opensBefore);
        var (result, message) = (version.Status, app.Status) switch
        {
            (PermitVersionStatus.Revoked, _) => (TokenResult.Revoked, $"Revoked on {version.RevokedAt:yyyy-MM-dd HH:mm} UTC: {version.RevokedReason}"),
            (PermitVersionStatus.Superseded, _) => (TokenResult.Superseded, $"This is version {version.VersionNumber}; version {permit.CurrentVersion} replaced it."),
            (PermitVersionStatus.Cancelled, _) or (_, CaseStatus.Cancelled) => (TokenResult.Cancelled, "The event was cancelled and the permit is no longer valid."),
            _ when Now > version.TokenExpiresAt => (TokenResult.Expired, $"The permit expired at {version.TokenExpiresAt:yyyy-MM-dd HH:mm} UTC."),
            _ when Now < opens => (TokenResult.OutsideWindow, $"Too early: check-in opens {opensBefore} minutes before the start."),
            _ when app.Status is not (CaseStatus.Permitted or CaseStatus.CheckedIn or CaseStatus.EventCompleted or CaseStatus.Closed)
                => (TokenResult.Cancelled, $"The case is {Workflow.Display(app.Status)}."),
            _ => (TokenResult.Valid, $"Valid permit {permit.Number} version {version.VersionNumber}."),
        };
        return new TokenCheck(result, message, app.Id, app.Reference, permit.Number, version.VersionNumber, app.Event?.Title, version.VenueName, version.StartUtc, version.EndUtc,
            app.Event?.Cemetery?.TimeZoneId, app.Status, opens, assigned);
    }

    /// <summary>Renders, stores and records one immutable version (PDF in object storage before the commit; orphan cleanup covers a failed commit).</summary>
    private async Task<Attachment> NewVersionAsync(AppDbContext db, Actor actor, PermitApplication app, Permit permit, Reservation reservation, string conditions, string? reason, CancellationToken ct)
    {
        var next = permit.CurrentVersion + 1;
        var grace = await rules.IntAsync(RuleKeys.TokenGraceHoursAfterEnd, ct);
        var expires = reservation.EndUtc.AddHours(grace);
        var (token, hash, shortCode) = CreateToken(signer, permit.Id, next, expires);
        var venue = await db.Venues.AsNoTracking().Include(v => v.Cemetery).FirstAsync(v => v.Id == reservation.VenueId, ct);
        var category = app.Event?.CategoryId is { } cid ? await db.EventCategories.AsNoTracking().FirstAsync(c => c.Id == cid, ct) : null;
        var zone = CaseService.Zone(venue.Cemetery!.TimeZoneId);
        var localStart = TimeZoneInfo.ConvertTime(reservation.StartUtc, zone);
        var localEnd = TimeZoneInfo.ConvertTime(reservation.EndUtc, zone);
        var bytes = pdf.RenderPermit(new PermitPdfModel(permit.Number, next, app.Reference, app.Applicant?.FullName ?? "", app.Event?.Title ?? "", category?.Name ?? "",
            venue.Name, venue.Cemetery.Name, $"{localStart:dddd d MMMM yyyy, HH:mm}–{localEnd:HH:mm}", venue.Cemetery.TimeZoneId, reservation.AttendeeCount,
            conditions, actor.DisplayName, Now, token, FormatShortCode(shortCode), reason));
        var attachment = await files.StoreAsync(actor, new UploadFile($"{permit.Number}-v{next}.pdf", "application/pdf", bytes), AttachmentPurpose.PermitPdf, DocumentType.Other, false, ct);
        attachment.ApplicationId = app.Id;
        db.Attachments.Add(attachment);
        permit.Versions.Add(new PermitVersion
        {
            VersionNumber = next, IssuedBy = actor.UserId, IssuedByName = actor.DisplayName, IssuedAt = Now, VenueName = venue.Name,
            StartUtc = reservation.StartUtc, EndUtc = reservation.EndUtc, Conditions = conditions, ReissueReason = reason,
            TokenHash = hash, ShortCode = shortCode, TokenExpiresAt = expires, PdfObjectKey = attachment.ObjectKey, PdfSha256 = attachment.Sha256,
        });
        permit.CurrentVersion = next;
        return attachment;
    }

    private static async Task<PermitApplication> LoadForPermitAsync(AppDbContext db, Actor actor, Guid id, CancellationToken ct) =>
        await LoadCaseAsync(db, actor, id, ct, q => q.Include(a => a.Applicant).Include(a => a.Event).Include(a => a.Reservations)
            .Include(a => a.Permit!).ThenInclude(p => p.Versions).Include(a => a.ReadinessItems));

    /// <summary>Signed permit token (A-12): base64url("permitId:version:expiry") + "." + HMAC; stored only as a hash plus a short code.</summary>
    public static (string Token, string Hash, string ShortCode) CreateToken(ISigner signer, Guid permitId, int version, DateTimeOffset expires)
    {
        var payload = Base64Url(Encoding.UTF8.GetBytes($"{permitId:N}:{version}:{expires.ToUnixTimeSeconds()}"));
        var token = payload + "." + signer.Sign(TokenPurpose, payload);
        var hash = Hash(token);
        return (token, hash, ShortCode(hash));
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>10-character Crockford-style code derived from the token hash (no ambiguous letters).</summary>
    public static string ShortCode(string hash)
    {
        const string alphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";
        var bytes = Convert.FromHexString(hash);
        var sb = new StringBuilder();
        for (var i = 0; i < 10; i++) sb.Append(alphabet[bytes[i] % alphabet.Length]);
        return sb.ToString();
    }

    public static string FormatShortCode(string code) => code.Length == 10 ? $"{code[..5]}-{code[5..]}" : code;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static TokenCheck Empty(TokenResult result, string message) => new(result, message, null, null, null, null, null, null, null, null, null, null, null, false);
}
