using System.Globalization;
using System.Text.Json;
using EventPermits.Application;
using EventPermits.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EventPermits.Infrastructure.Seeding;

public sealed record SeedSummary(int Users, int ReferenceRows, int Applications, int Files, int ApplicationsTotal)
{
    public override string ToString() =>
        $"{Users} users created, {ReferenceRows} reference rows created, {Applications} applications created, {Files} files stored, {ApplicationsTotal} applications in the database";
}

/// <summary>
/// Repeatable seed (E12-01). Adds only what is missing, identified by natural keys, so re-running creates nothing.
/// A destructive reset is a separate, explicit command.
/// </summary>
public sealed partial class Seeder(
    IDbContextFactory<AppDbContext> factory,
    UserManager<AppUser> users,
    RoleManager<IdentityRole> roles,
    IConfiguration config,
    TimeProvider clock)
{
    public async Task<SeedSummary> SeedAsync(CancellationToken ct = default)
    {
        var passwords = ResolvePasswords(config);
        var userCount = await SeedUsersAsync(passwords);
        var referenceRows = await SeedReferenceAsync(ct);
        var (applications, files) = await SeedCasesAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        return new SeedSummary(userCount, referenceRows, applications, files, await db.Applications.CountAsync(ct));
    }

    /// <summary>
    /// OD-2: with Seed:AllowDemoPassword=true every account uses the documented demo password (local only).
    /// Otherwise Seed:CredentialsJson (user name to password) is required and the demo password is refused.
    /// Values never appear in messages.
    /// </summary>
    public static Dictionary<string, string> ResolvePasswords(IConfiguration config)
    {
        if (config.GetValue("Seed:AllowDemoPassword", false))
        {
            var demo = config["Seed:DemoPassword"];
            if (string.IsNullOrEmpty(demo)) demo = DemoAccounts.DocumentedPassword;
            return DemoAccounts.All.ToDictionary(a => a.UserName, _ => demo);
        }
        var json = config["Seed:CredentialsJson"];
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException(
                "Seed:CredentialsJson is required when Seed:AllowDemoPassword is not true (the documented demo password is for local runs only).");
        Dictionary<string, string> parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new JsonException();
        }
        catch (JsonException)
        {
            // No JsonException text: it can quote part of the secret content.
            throw new InvalidOperationException("Seed:CredentialsJson must be a JSON object of user name to password.");
        }
        if (parsed.Values.Any(v => v == DemoAccounts.DocumentedPassword))
            throw new InvalidOperationException("The documented local demo password is refused here; set Seed:AllowDemoPassword=true only for local runs.");
        var missing = DemoAccounts.All.Select(a => a.UserName).Where(u => !parsed.ContainsKey(u)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"Seed:CredentialsJson has no password for: {string.Join(", ", missing)}.");
        return parsed;
    }

    private async Task<int> SeedUsersAsync(IReadOnlyDictionary<string, string> passwords)
    {
        foreach (var role in Roles.All)
            if (!await roles.RoleExistsAsync(role))
                Ensure(await roles.CreateAsync(new IdentityRole(role)), $"role {role}");

        var created = 0;
        foreach (var account in DemoAccounts.All)
        {
            if (await users.FindByNameAsync(account.UserName) is not null) continue;
            var user = new AppUser
            {
                UserName = account.UserName,
                Email = $"{account.UserName}@example.test",
                EmailConfirmed = true,
                DisplayName = account.DisplayName,
                IsActive = true,
                LockoutEnabled = true,
                CreatedAt = clock.GetUtcNow(),
            };
            Ensure(await users.CreateAsync(user, passwords[account.UserName]), $"user {account.UserName}");
            Ensure(await users.AddToRolesAsync(user, account.Roles), $"roles of {account.UserName}");
            created++;
        }
        return created;
    }

    private async Task<int> SeedReferenceAsync(CancellationToken ct)
    {
        var sites = ReferenceData.LoadSites(config["Seed:ReferenceDirectory"]);
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Clock = clock;
        var added = 0;

        foreach (var c in sites.Cemeteries)
        {
            if (await db.Cemeteries.AnyAsync(x => x.Code == c.Code, ct)) continue;
            db.Cemeteries.Add(new Cemetery { Code = c.Code, Name = c.Name, Address = c.Address, TimeZoneId = c.TimeZoneId });
            added++;
        }
        await db.SaveChangesAsync(ct);
        var cemeteries = await db.Cemeteries.ToDictionaryAsync(c => c.Code, ct);

        foreach (var v in sites.Venues)
        {
            if (await db.Venues.AnyAsync(x => x.Code == v.Code, ct)) continue;
            db.Venues.Add(new Venue
            {
                Code = v.Code,
                CemeteryId = cemeteries[v.CemeteryCode].Id,
                Name = v.Name,
                Address = v.Address,
                Capacity = v.Capacity,
                SetupBufferMinutes = v.SetupBufferMinutes,
                TeardownBufferMinutes = v.TeardownBufferMinutes,
                AllowedCategoryCodes = v.AllowedCategories,
                OpeningHours = v.OpeningHours.Select(h => new OpeningHours
                {
                    Day = h.Day,
                    Opens = TimeOnly.Parse(h.Opens, CultureInfo.InvariantCulture),
                    Closes = TimeOnly.Parse(h.Closes, CultureInfo.InvariantCulture),
                }).ToList(),
            });
            added++;
        }

        foreach (var cemetery in cemeteries.Values)
            foreach (var r in sites.Resources)
            {
                if (await db.Resources.AnyAsync(x => x.CemeteryId == cemetery.Id && x.Code == r.Code, ct)) continue;
                db.Resources.Add(new Resource { CemeteryId = cemetery.Id, Code = r.Code, Name = r.Name, Unit = r.Unit, TotalQuantity = r.QuantityPerCemetery });
                added++;
            }

        foreach (var category in Categories)
        {
            if (await db.EventCategories.AnyAsync(x => x.Code == category.Code, ct)) continue;
            db.EventCategories.Add(category);
            added++;
        }

        foreach (var (key, value, description, min, max) in RuleKeys.Defaults)
        {
            if (await db.RuleSettings.AnyAsync(x => x.Key == key, ct)) continue;
            db.RuleSettings.Add(new RuleSetting { Key = key, Value = value, Description = description, Min = min, Max = max });
            added++;
        }

        foreach (var template in Templates)
        {
            if (await db.NotificationTemplates.AnyAsync(x => x.Code == template.Code, ct)) continue;
            db.NotificationTemplates.Add(template);
            added++;
        }

        await db.SaveChangesAsync(ct);
        return added;
    }

    private static IEnumerable<EventCategory> Categories =>
    [
        new()
        {
            Code = "MEMORIAL", Name = "Memorial service", Description = "A graveside or chapel service remembering an individual.",
            RequiredDocuments = [DocumentType.ProofOfAffiliation, DocumentType.EventPlan],
            ReviewChecklist = ["Applicant identity matches the registry record", "Proof of affiliation is legible and current", "Event plan describes conduct and timings", "Requested venue suits a memorial service"],
        },
        new()
        {
            Code = "COMMEMORATION", Name = "Commemoration ceremony", Description = "An organised ceremony for a remembrance date or anniversary.",
            RequiredDocuments = [DocumentType.EventPlan, DocumentType.InsuranceCertificate],
            ReviewChecklist = ["Applicant identity matches the registry record", "Event plan covers programme, speakers and marshals", "Insurance certificate covers the event date", "Attendee estimate is realistic for the venue"],
        },
        new()
        {
            Code = "WREATH", Name = "Wreath laying", Description = "A short wreath-laying at a memorial.",
            RequiredDocuments = [DocumentType.ProofOfAffiliation],
            ReviewChecklist = ["Applicant identity matches the registry record", "Proof of affiliation is legible and current", "Wreath placement respects site rules"],
        },
        new()
        {
            Code = "FLYOVER", Name = "Ceremonial flyover", Description = "A coordinated ceremony including a fictional flyover segment.",
            RequiredDocuments = [DocumentType.EventPlan, DocumentType.InsuranceCertificate, DocumentType.SitePlan],
            ReviewChecklist = ["Applicant identity matches the registry record", "Site plan shows safe viewing areas", "Insurance certificate covers the event date", "Event plan includes a weather contingency"],
        },
        new()
        {
            Code = "CONCERT", Name = "Remembrance concert", Description = "A musical performance in remembrance.",
            RequiredDocuments = [DocumentType.EventPlan, DocumentType.InsuranceCertificate],
            ReviewChecklist = ["Applicant identity matches the registry record", "Programme respects quiet hours", "Insurance certificate covers the event date"],
        },
    ];

    private static IEnumerable<NotificationTemplate> Templates =>
    [
        T("application.submitted", "Application received", "Application {{Reference}} received",
            "Dear {{ApplicantName}},\n\nWe received application {{Reference}} for \"{{EventTitle}}\" on {{EventDate}}. An eligibility reviewer will look at it soon.\n\nThis is a fictional practice message."),
        T("info.requested", "Information requested", "More information needed for {{Reference}}",
            "Dear {{ApplicantName}},\n\nTo continue reviewing {{Reference}} we need: {{Items}}.\nPlease respond by {{DueDate}}.\n\nThis is a fictional practice message."),
        T("info.answered", "Information received", "Thank you, information for {{Reference}} received",
            "Dear {{ApplicantName}},\n\nYour response for {{Reference}} has been received and the application is back in review.\n\nThis is a fictional practice message."),
        T("eligibility.passed", "Eligibility confirmed", "{{Reference}} is eligible",
            "Dear {{ApplicantName}},\n\nApplication {{Reference}} passed the eligibility review. Scheduling will confirm the venue next.\n\nThis is a fictional practice message."),
        T("eligibility.rejected", "Eligibility rejected", "{{Reference}} was not approved",
            "Dear {{ApplicantName}},\n\nApplication {{Reference}} could not be approved. Reason: {{Reason}}\n\nThis is a fictional practice message."),
        T("reservation.tentative", "Venue held", "Venue held for {{Reference}}",
            "Dear {{ApplicantName}},\n\n{{Venue}} is tentatively held for {{EventDate}}. Final approval is pending.\n\nThis is a fictional practice message."),
        T("approval.approved", "Application approved", "{{Reference}} approved",
            "Dear {{ApplicantName}},\n\nApplication {{Reference}} is approved. Your permit will follow.\n\nThis is a fictional practice message."),
        T("approval.rejected", "Application rejected", "{{Reference}} was not approved",
            "Dear {{ApplicantName}},\n\nApplication {{Reference}} was not approved at the final decision. Reason: {{Reason}}\n\nThis is a fictional practice message."),
        T("permit.issued", "Permit issued", "Permit {{PermitNumber}} for {{EventTitle}}",
            "Dear {{ApplicantName}},\n\nPermit {{PermitNumber}} is issued for {{EventTitle}} at {{Venue}} on {{EventDate}}. Bring the permit QR code on the day.\n\nThis is a fictional practice message."),
        T("permit.reissued", "Permit reissued", "Updated permit {{PermitNumber}}",
            "Dear {{ApplicantName}},\n\nPermit {{PermitNumber}} was reissued. Reason: {{Reason}}. Use the newest version only.\n\nThis is a fictional practice message."),
        T("permit.revoked", "Permit revoked", "Permit {{PermitNumber}} revoked",
            "Dear {{ApplicantName}},\n\nPermit {{PermitNumber}} was revoked. Reason: {{Reason}}\n\nThis is a fictional practice message."),
        T("case.cancelled", "Application cancelled", "{{Reference}} cancelled",
            "Dear {{ApplicantName}},\n\nApplication {{Reference}} was cancelled. Reason: {{Reason}}\n\nThis is a fictional practice message."),
        T("incident.escalated", "Incident escalated", "Incident escalated for {{Reference}}",
            "An incident on {{Reference}} was escalated to operations management: {{Reason}}\n\nThis is a fictional practice message."),
        T("manual.general", "General message", "Message about {{Reference}}",
            "Dear {{ApplicantName}},\n\n{{Message}}\n\nThis is a fictional practice message."),
    ];

    private static NotificationTemplate T(string code, string name, string subject, string body) =>
        new() { Code = code, Name = name, Channel = NotificationChannel.Email, Subject = subject, Body = body };

    private static void Ensure(IdentityResult result, string what)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Seeding {what} failed: {string.Join("; ", result.Errors.Select(e => e.Code))}.");
    }
}
