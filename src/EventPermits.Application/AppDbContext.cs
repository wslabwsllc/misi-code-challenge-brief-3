using System.Text.Json;
using EventPermits.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventPermits.Application;

public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The single EF context. Services use it directly (no generic repositories). Rows are never hard-deleted,
/// audit rows are never updated, and audit columns are stamped from <see cref="ActorId"/> and <see cref="Clock"/>.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole, string>(options), IDataProtectionKeyContext
{
    /// <summary>User the next save is attributed to (set by <see cref="AppDb"/>).</summary>
    public string ActorId { get; set; } = "system";
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Cemetery> Cemeteries => Set<Cemetery>();
    public DbSet<EventCategory> EventCategories => Set<EventCategory>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<VenueBlackout> VenueBlackouts => Set<VenueBlackout>();
    public DbSet<Resource> Resources => Set<Resource>();
    public DbSet<Applicant> Applicants => Set<Applicant>();
    public DbSet<PermitApplication> Applications => Set<PermitApplication>();
    public DbSet<EventDetails> Events => Set<EventDetails>();
    public DbSet<RequestedResource> RequestedResources => Set<RequestedResource>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<EligibilityReview> EligibilityReviews => Set<EligibilityReview>();
    public DbSet<InformationRequest> InformationRequests => Set<InformationRequest>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ResourceAllocation> ResourceAllocations => Set<ResourceAllocation>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<Permit> Permits => Set<Permit>();
    public DbSet<PermitVersion> PermitVersions => Set<PermitVersion>();
    public DbSet<AttendantAssignment> AttendantAssignments => Set<AttendantAssignment>();
    public DbSet<ReadinessChecklistItem> ReadinessItems => Set<ReadinessChecklistItem>();
    public DbSet<CheckIn> CheckIns => Set<CheckIn>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationStatusChange> NotificationStatusChanges => Set<NotificationStatusChange>();
    public DbSet<WebhookReceipt> WebhookReceipts => Set<WebhookReceipt>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<RuleSetting> RuleSettings => Set<RuleSetting>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    public DbSet<ReferenceCounter> ReferenceCounters => Set<ReferenceCounter>();
    public DbSet<SystemState> SystemStates => Set<SystemState>();
    public DbSet<IntegrationAttempt> IntegrationAttempts => Set<IntegrationAttempt>();
    public DbSet<RegistryVerification> RegistryVerifications => Set<RegistryVerification>();
    public DbSet<ExternalCalendarEvent> ExternalCalendarEvents => Set<ExternalCalendarEvent>();
    public DbSet<CalendarSyncRun> CalendarSyncRuns => Set<CalendarSyncRun>();
    public DbSet<ReconciliationItem> ReconciliationItems => Set<ReconciliationItem>();
    public DbSet<WeatherSnapshot> WeatherSnapshots => Set<WeatherSnapshot>();
    public DbSet<GeocodeLookup> GeocodeLookups => Set<GeocodeLookup>();
    public DbSet<GeocodeCandidate> GeocodeCandidates => Set<GeocodeCandidate>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRowError> ImportRowErrors => Set<ImportRowError>();
    public DbSet<PartnerEvent> PartnerEvents => Set<PartnerEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Readable enum values in the database and in exports.
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        foreach (var type in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)).ToList())
        {
            // Keys are assigned in code (Guid.NewGuid), so a new child reached through a navigation is inserted, not updated.
            b.Entity(type.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();
            // PostgreSQL xmin system column as optimistic concurrency token (FR-18).
            b.Entity(type.ClrType).Property(nameof(Entity.Version)).IsRowVersion();
            b.Entity(type.ClrType).Property(nameof(Entity.CreatedBy)).HasMaxLength(64);
            b.Entity(type.ClrType).Property(nameof(Entity.UpdatedBy)).HasMaxLength(64);
        }

        b.Entity<AppUser>(e => e.Property(u => u.DisplayName).HasMaxLength(120));

        b.Entity<Cemetery>(e => e.HasIndex(c => c.Code).IsUnique());
        b.Entity<EventCategory>(e =>
        {
            e.HasIndex(c => c.Code).IsUnique();
            e.Property(c => c.RequiredDocuments).HasConversion(JsonConverter<List<DocumentType>>(), JsonComparer<List<DocumentType>>()).HasColumnType("jsonb");
        });
        b.Entity<Venue>(e =>
        {
            e.HasIndex(v => v.Code).IsUnique();
            e.OwnsMany(v => v.OpeningHours, o => o.ToJson());
        });
        b.Entity<VenueBlackout>(e => e.HasIndex(x => new { x.VenueId, x.StartUtc }));
        b.Entity<Resource>(e => e.HasIndex(r => new { r.CemeteryId, r.Code }).IsUnique());

        b.Entity<PermitApplication>(e =>
        {
            e.ToTable("Applications");
            e.HasIndex(a => a.Reference).IsUnique();
            e.HasIndex(a => a.ClientRequestKey).IsUnique().HasFilter("\"ClientRequestKey\" IS NOT NULL");
            e.HasIndex(a => a.Status);
            e.HasIndex(a => a.OwnerUserId);
            e.HasIndex(a => a.UpdatedAt);
            e.HasOne(a => a.Applicant).WithMany().HasForeignKey(a => a.ApplicantId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Event).WithOne().HasForeignKey<EventDetails>(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Permit).WithOne().HasForeignKey<Permit>(p => p.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.RequestedResources).WithOne().HasForeignKey(r => r.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Attachments).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Comments).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.InformationRequests).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Reviews).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Approvals).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Reservations).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.CheckIns).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Incidents).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Assignments).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.ReadinessItems).WithOne().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(a => a.ActiveReservation);
        });
        b.Entity<EventDetails>(e =>
        {
            e.ToTable("Events");
            e.HasIndex(x => x.StartUtc);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Cemetery).WithMany().HasForeignKey(x => x.CemeteryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PreferredVenue).WithMany().HasForeignKey(x => x.PreferredVenueId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.EndUtc);
        });
        b.Entity<RequestedResource>(e => e.HasOne(x => x.Resource).WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<Attachment>(e =>
        {
            e.HasIndex(x => x.ObjectKey).IsUnique();
            e.HasOne<Incident>().WithMany(i => i.Photos).HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InformationRequest>().WithMany().HasForeignKey(x => x.InformationRequestId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<EligibilityReview>(e =>
            e.Property(x => x.Checklist).HasConversion(JsonConverter<Dictionary<string, bool>>(), JsonComparer<Dictionary<string, bool>>()).HasColumnType("jsonb"));
        b.Entity<Reservation>(e =>
        {
            e.HasIndex(x => new { x.VenueId, x.BlockStartUtc });
            e.HasOne(x => x.Venue).WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ResourceAllocation>(e => e.HasOne(x => x.Resource).WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<Permit>(e =>
        {
            e.HasIndex(p => p.Number).IsUnique();
            e.HasMany(p => p.Versions).WithOne().HasForeignKey(v => v.PermitId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<PermitVersion>(e =>
        {
            e.HasIndex(v => new { v.PermitId, v.VersionNumber }).IsUnique();
            e.HasIndex(v => v.TokenHash).IsUnique();
            e.HasIndex(v => v.ShortCode).IsUnique();
        });
        b.Entity<AttendantAssignment>(e => e.HasIndex(x => new { x.UserId, x.Active }));
        b.Entity<CheckIn>(e => e.HasIndex(x => new { x.ApplicationId, x.PermitVersionId }).IsUnique());
        b.Entity<Incident>(e =>
        {
            e.HasIndex(x => x.Reference).IsUnique();
            e.Ignore(x => x.BlocksClosure);
        });
        b.Entity<NotificationTemplate>(e => e.HasIndex(x => x.Code).IsUnique());
        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.GatewayMessageId);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasMany(x => x.StatusChanges).WithOne().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<WebhookReceipt>(e =>
        {
            e.HasIndex(x => x.EventId).IsUnique().HasFilter("\"Outcome\" IN ('Accepted', 'Ignored')");
            e.HasIndex(x => x.ReceivedAt);
        });
        b.Entity<AuditEvent>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.ApplicationId);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.Action).HasMaxLength(80);
        });
        b.Entity<RuleSetting>(e => e.HasIndex(x => x.Key).IsUnique());
        b.Entity<SavedView>(e => e.HasIndex(x => x.UserId));
        b.Entity<ReferenceCounter>(e => e.HasKey(x => new { x.Prefix, x.Year }));
        b.Entity<SystemState>(e => e.HasKey(x => x.Key));
        b.Entity<IntegrationAttempt>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => new { x.Integration, x.At });
            e.HasIndex(x => x.CorrelationId);
        });
        b.Entity<RegistryVerification>(e => e.HasIndex(x => x.ApplicationId));
        b.Entity<ExternalCalendarEvent>(e =>
        {
            e.HasIndex(x => x.ExternalId).IsUnique();
            e.HasIndex(x => new { x.VenueId, x.StartUtc });
        });
        b.Entity<ReconciliationItem>(e => e.HasIndex(x => new { x.ApplicationId, x.Status }));
        b.Entity<WeatherSnapshot>(e => e.HasIndex(x => new { x.VenueId, x.Date }));
        b.Entity<GeocodeLookup>(e => e.HasMany(x => x.Candidates).WithOne().HasForeignKey(x => x.LookupId).OnDelete(DeleteBehavior.Restrict));
        b.Entity<ImportBatch>(e =>
        {
            e.HasIndex(x => x.BatchId).IsUnique();
            e.HasIndex(x => x.Checksum);
            e.HasMany(x => x.Errors).WithOne().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<PartnerEvent>(e =>
        {
            e.HasIndex(x => x.PartnerEventId).IsUnique();
            e.HasIndex(x => new { x.VenueId, x.StartUtc });
        });
        b.Entity<OutboxMessage>(e => e.HasIndex(x => new { x.Status, x.NextAttemptAt }));
        b.Entity<JobRun>(e => e.HasIndex(x => new { x.Job, x.StartedAt }));
    }

    /// <summary>Used by the Data Protection key store (synchronous); application services always save asynchronously.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Guard();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Guard();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>No hard deletion of business data (FR-18); audit rows are append-only; audit columns are stamped.</summary>
    private void Guard()
    {
        var now = Clock.GetUtcNow();
        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case Entity entity when entry.State == EntityState.Deleted:
                    throw new InvalidOperationException($"Hard deletion of {entity.GetType().Name} is not allowed; deactivate it instead.");
                case AuditEvent when entry.State is EntityState.Modified or EntityState.Deleted:
                    throw new InvalidOperationException("Audit events are append-only.");
                case IntegrationAttempt or WebhookReceipt when entry.State is EntityState.Deleted:
                    throw new InvalidOperationException("Integration evidence is never deleted.");
                case Entity entity when entry.State == EntityState.Added:
                    if (entity.CreatedAt == default) entity.CreatedAt = now;
                    if (string.IsNullOrEmpty(entity.CreatedBy)) entity.CreatedBy = ActorId;
                    entity.UpdatedAt = entity.UpdatedAt == default ? entity.CreatedAt : entity.UpdatedAt;
                    entity.UpdatedBy = string.IsNullOrEmpty(entity.UpdatedBy) ? entity.CreatedBy : entity.UpdatedBy;
                    break;
                case Entity entity when entry.State == EntityState.Modified:
                    entity.UpdatedAt = now;
                    entity.UpdatedBy = ActorId;
                    break;
            }
        }
    }

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, JsonOptions), s => string.IsNullOrEmpty(s) ? new T() : JsonSerializer.Deserialize<T>(s, JsonOptions) ?? new T());

    private static ValueComparer<T> JsonComparer<T>() =>
        new((a, c) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(c, JsonOptions),
            v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

/// <summary>Creates contexts attributed to an actor, using the injected clock. Services never share a context.</summary>
public sealed class AppDb(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
{
    public TimeProvider Clock => clock;

    public async Task<AppDbContext> CreateAsync(Actor actor, CancellationToken ct = default)
    {
        var db = await factory.CreateDbContextAsync(ct);
        db.ActorId = actor.UserId;
        db.Clock = clock;
        return db;
    }

    public Task<AppDbContext> CreateAsync(CancellationToken ct = default) => CreateAsync(Actor.System, ct);
}
