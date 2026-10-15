using System.Net;
using EventPermits.Application;
using EventPermits.Application.Services;
using EventPermits.Domain;
using EventPermits.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventPermits.IntegrationTests;

public sealed class WorkflowFixture(TestInfrastructure infra) : AppFixture(infra);

/// <summary>Brief §5 transitions T1–T14, guards, 422/403/404/409 (E2-03, E2-05, E2-07, E7, E8).</summary>
public sealed class WorkflowTests(WorkflowFixture app) : IClassFixture<WorkflowFixture>
{
    private readonly Scenario _s = new(app);

    [Fact]
    public async Task T1_to_T13_full_journey_with_information_request_incident_and_closure()
    {
        // T1 Draft -> Submitted
        var c = await _s.SubmittedAsync();
        Assert.Equal(CaseStatus.Submitted, await _s.StatusAsync(c.Id));

        // T2 Submitted -> Needs Information
        using var reviewer = await app.SignInAsync("reviewer.charlie");
        (await reviewer.PostAsync($"/api/applications/{c.Id}/information-requests", new
        {
            version = await _s.VersionAsync(c.Id), items = new[] { "Signed event plan" }, message = "Please sign the plan.", dueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5),
        })).Ok();
        Assert.Equal(CaseStatus.NeedsInformation, await _s.StatusAsync(c.Id));

        // T3 Needs Information -> Submitted (resubmit refused while a request is open)
        using var intake = await app.SignInAsync("intake.alpha");
        (await intake.PostAsync($"/api/applications/{c.Id}/resubmit", new { version = await _s.VersionAsync(c.Id) }))
            .Expect(HttpStatusCode.UnprocessableEntity);
        var requestId = await app.QueryAsync(db => db.InformationRequests.Where(r => r.ApplicationId == c.Id).Select(r => r.Id).FirstAsync());
        (await intake.PostAsync($"/api/information-requests/{requestId}/respond", new { version = await _s.VersionAsync(c.Id), response = "Signed plan uploaded." })).Ok();
        (await intake.PostAsync($"/api/applications/{c.Id}/resubmit", new { version = await _s.VersionAsync(c.Id) })).Ok();
        Assert.Equal(CaseStatus.Submitted, await _s.StatusAsync(c.Id));

        // T4 Submitted -> Eligible (registry evidence + checklist)
        (await reviewer.PostAsync($"/api/applications/{c.Id}/eligibility/pass", new { version = await _s.VersionAsync(c.Id), checklist = await _s.ChecklistAsync(c.Id) }))
            .Expect(HttpStatusCode.UnprocessableEntity); // no registry evidence yet
        (await reviewer.PostAsync($"/api/applications/{c.Id}/verify-registry")).Ok();
        (await reviewer.PostAsync($"/api/applications/{c.Id}/eligibility/pass", new { version = await _s.VersionAsync(c.Id), checklist = await _s.ChecklistAsync(c.Id) })).Ok();

        // T6 Eligible -> Tentatively Reserved (a collision is refused first)
        using var scheduler = await app.SignInAsync("scheduler.delta");
        var blocker = await _s.ReservedAsync(); // another case already holds a slot
        var collide = await scheduler.PostAsync($"/api/applications/{c.Id}/reservation", new
        {
            version = await _s.VersionAsync(c.Id), venueId = blocker.VenueId, localDate = blocker.Date, localStart = blocker.Start, durationMinutes = 60, attendeeCount = 30,
            resources = Array.Empty<object>(), attendantNeeds = "Door attendant.",
        });
        collide.Expect(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.ReservationConflict, collide.Code);
        var chairs = await app.QueryAsync(db => db.Resources.Where(r => r.Code == "CHAIRS" && r.Cemetery!.Code == "RMP").Select(r => r.Id).FirstAsync());
        (await scheduler.PostAsync($"/api/applications/{c.Id}/reservation", new
        {
            version = await _s.VersionAsync(c.Id), venueId = c.VenueId, localDate = c.Date, localStart = c.Start, durationMinutes = 60, attendeeCount = 30,
            resources = new[] { new { resourceId = chairs, quantity = 30 } }, attendantNeeds = "Door attendant.",
        })).Ok();

        // T7 -> Pending Approval; T8 blocked until the calendar is reconciled
        (await scheduler.PostAsync($"/api/applications/{c.Id}/submit-for-approval", new { version = await _s.VersionAsync(c.Id) })).Ok();
        using var manager = await app.SignInAsync("manager.echo");
        var early = await manager.PostAsync($"/api/applications/{c.Id}/approve", new { version = await _s.VersionAsync(c.Id) });
        early.Expect(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.CalendarNotReconciled, early.Code);
        await _s.ReconcileCalendarAsync();
        (await manager.PostAsync($"/api/applications/{c.Id}/approve", new { version = await _s.VersionAsync(c.Id), notes = "Fine." })).Ok();

        // T10 Approved -> Permitted (PDF stored, token, readiness checklist)
        (await manager.PostAsync($"/api/applications/{c.Id}/permit", new { version = await _s.VersionAsync(c.Id) })).Ok();
        var detail = (await manager.GetAsync($"/api/applications/{c.Id}")).Ok();
        Assert.Equal("Permitted", detail.Body!["status"]!.GetValue<string>());
        Assert.Matches(@"^PER-\d{4}-\d{5}$", detail.Body["permit"]!["number"]!.GetValue<string>());
        (await manager.PostAsync($"/api/applications/{c.Id}/attendants", new { version = await _s.VersionAsync(c.Id), userId = app.UserIds["attendant.foxtrot"], assign = true })).Ok();

        // T11 Permitted -> Checked In (assigned attendant, inside the window, idempotent)
        var code = await _s.ShortCodeAsync(c.Id);
        using var attendant = await app.SignInAsync("attendant.foxtrot");
        var tooEarly = await attendant.PostAsync("/api/check-in", new { code });
        tooEarly.Expect(HttpStatusCode.UnprocessableEntity);
        try
        {
            await _s.EnterCheckInWindowAsync(c.Id);
            // Sessions are time-bound: after moving the clock to the event day, everyone signs in again.
            using var attendantDay = await app.SignInAsync("attendant.foxtrot");
            using var managerDay = await app.SignInAsync("manager.echo");
            using var other = await app.SignInAsync("attendant.golf");
            (await other.PostAsync("/api/check-in", new { code })).Expect(HttpStatusCode.UnprocessableEntity); // not assigned: code reveals nothing
            (await attendantDay.PostAsync("/api/check-in", new { code })).Ok();
            (await attendantDay.PostAsync("/api/check-in", new { code })).Ok(); // idempotent repeat
            Assert.Equal(1, await app.QueryAsync(db => db.CheckIns.CountAsync(x => x.ApplicationId == c.Id)));
            Assert.Equal(CaseStatus.CheckedIn, await _s.StatusAsync(c.Id));

            // Incident (High) blocks closure until resolved
            var incident = (await attendantDay.PostAsync($"/api/applications/{c.Id}/incidents", new
            {
                version = await _s.VersionAsync(c.Id), category = "Trip hazard", severity = "High", description = "Loose paving stone near the chapel door.",
            })).Ok();
            var incidentId = incident.Id;

            // T12 Checked In -> Event Completed (check-out and readiness checklist)
            (await attendantDay.PostAsync($"/api/applications/{c.Id}/complete", new { version = await _s.VersionAsync(c.Id) })).Expect(HttpStatusCode.UnprocessableEntity);
            (await attendantDay.PostAsync($"/api/applications/{c.Id}/check-out", new { version = await _s.VersionAsync(c.Id), notes = "All guests left." })).Ok();
            foreach (var item in await app.QueryAsync(db => db.ReadinessItems.Where(r => r.ApplicationId == c.Id).Select(r => r.Id).ToListAsync()))
                (await attendantDay.PutAsync($"/api/readiness/{item}", new { version = await _s.VersionAsync(c.Id), done = true })).Ok();
            (await attendantDay.PostAsync($"/api/applications/{c.Id}/complete", new { version = await _s.VersionAsync(c.Id) })).Ok();

            // T13 Event Completed -> Closed, blocked by the unresolved High incident
            var blocked = await managerDay.PostAsync($"/api/applications/{c.Id}/close", new { version = await _s.VersionAsync(c.Id) });
            blocked.Expect(HttpStatusCode.UnprocessableEntity);
            Assert.Equal(ErrorCodes.BlockingIncidents, blocked.Code);
            var incidentVersion = await app.QueryAsync(db => db.Incidents.Where(i => i.Id == incidentId).Select(i => i.Version).FirstAsync());
            (await managerDay.PostAsync($"/api/incidents/{incidentId}/resolve", new { version = incidentVersion, reason = "Stone re-set and checked." })).Ok();
            (await managerDay.PostAsync($"/api/applications/{c.Id}/close", new { version = await _s.VersionAsync(c.Id), notes = "Event closed." })).Ok();
            Assert.Equal(CaseStatus.Closed, await _s.StatusAsync(c.Id));
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }

        // Every status change has its audit event (FR-18)
        var transitions = await app.QueryAsync(db => db.AuditEvents.CountAsync(a => a.ApplicationId == c.Id && a.Action.StartsWith("Transition.")));
        Assert.True(transitions >= 11, $"Expected at least 11 transition audit events, found {transitions}.");
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("permit")]
    [InlineData("complete")]
    [InlineData("close")]
    [InlineData("submit-for-approval")]
    [InlineData("resubmit")]
    public async Task Forbidden_moves_from_Submitted_return_422_for_an_authorised_role(string action)
    {
        var c = await _s.SubmittedAsync();
        var user = action switch
        {
            "approve" or "permit" or "close" => "manager.echo",
            "submit-for-approval" => "scheduler.delta",
            "resubmit" => "intake.alpha",
            _ => "attendant.foxtrot",
        };
        if (user == "attendant.foxtrot") return; // attendants cannot see this case: covered by the scope tests
        using var client = await app.SignInAsync(user);
        var result = await client.PostAsync($"/api/applications/{c.Id}/{action}", new { version = await _s.VersionAsync(c.Id) });
        result.Expect(HttpStatusCode.UnprocessableEntity);
        Assert.Equal(ErrorCodes.InvalidTransition, result.Code);
        Assert.NotNull(result.Body!["correlationId"]);
    }

    [Fact]
    public async Task Forbidden_moves_from_other_states_return_422()
    {
        using var manager = await app.SignInAsync("manager.echo");
        var draft = await _s.DraftAsync();
        (await manager.PostAsync($"/api/applications/{draft.Id}/cancel", new { version = await _s.VersionAsync(draft.Id), reason = "Cancel a draft is not allowed." }))
            .Expect(HttpStatusCode.UnprocessableEntity); // Draft -> Cancelled
        (await manager.PostAsync($"/api/applications/{draft.Id}/approve", new { version = await _s.VersionAsync(draft.Id) }))
            .Expect(HttpStatusCode.UnprocessableEntity); // Draft -> Approved

        var rejected = await _s.SubmittedAsync();
        using var reviewer = await app.SignInAsync("reviewer.charlie");
        (await reviewer.PostAsync($"/api/applications/{rejected.Id}/eligibility/reject", new { version = await _s.VersionAsync(rejected.Id), reason = "Not a memorial event." })).Ok();
        (await reviewer.PostAsync($"/api/applications/{rejected.Id}/eligibility/pass", new { version = await _s.VersionAsync(rejected.Id), checklist = await _s.ChecklistAsync(rejected.Id) }))
            .Expect(HttpStatusCode.UnprocessableEntity); // Rejected -> Eligible
        (await manager.PostAsync($"/api/applications/{rejected.Id}/cancel", new { version = await _s.VersionAsync(rejected.Id), reason = "Cannot cancel a rejected case." }))
            .Expect(HttpStatusCode.UnprocessableEntity); // Rejected -> Cancelled
    }

    [Fact]
    public async Task Wrong_role_gets_403_before_transition_validation()
    {
        var c = await _s.SubmittedAsync();
        using var reviewer = await app.SignInAsync("reviewer.charlie");
        var r = await reviewer.PostAsync($"/api/applications/{c.Id}/approve", new { version = await _s.VersionAsync(c.Id) });
        r.Expect(HttpStatusCode.Forbidden); // Reviewer approves (prohibited, brief §3)
        using var admin = await app.SignInAsync("admin.india");
        (await admin.PostAsync($"/api/applications/{c.Id}/approve", new { version = await _s.VersionAsync(c.Id) })).Expect(HttpStatusCode.Forbidden);
        using var auditor = await app.SignInAsync("auditor.hotel");
        (await auditor.PostAsync($"/api/applications/{c.Id}/comments", new { version = await _s.VersionAsync(c.Id), body = "Auditors cannot write." })).Expect(HttpStatusCode.Forbidden);
        using var intake = await app.SignInAsync("intake.alpha");
        (await intake.PostAsync($"/api/applications/{c.Id}/permit", new { version = await _s.VersionAsync(c.Id) })).Expect(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Out_of_scope_case_is_404_without_details()
    {
        var c = await _s.DraftAsync("intake.alpha");
        using var bravo = await app.SignInAsync("intake.bravo");
        var r = await bravo.GetAsync($"/api/applications/{c.Id}");
        r.Expect(HttpStatusCode.NotFound);
        Assert.DoesNotContain(c.Reference, r.Raw);
        using var attendant = await app.SignInAsync("attendant.golf");
        (await attendant.GetAsync($"/api/applications/{c.Id}")).Expect(HttpStatusCode.NotFound);
        (await bravo.GetAsync($"/api/applications/{Guid.NewGuid()}")).Expect(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Concurrent_edits_second_writer_gets_409_with_current_version()
    {
        var c = await _s.DraftAsync();
        using var a = await app.SignInAsync("intake.alpha");
        var v = await _s.VersionAsync(c.Id);
        var body = new { version = v, complete = false, fullName = "Name One", organization = "", email = "one@example.test", phone = "", registryReference = "REF-10001" };
        (await a.PutAsync($"/api/applications/{c.Id}/applicant", body)).Ok();
        var second = await a.PutAsync($"/api/applications/{c.Id}/applicant", body with { fullName = "Name Two" });
        second.Expect(HttpStatusCode.Conflict);
        Assert.Equal("STALE_VERSION", second.Code);
        Assert.True(second.Body!["currentVersion"]!.GetValue<uint>() > v);
        Assert.Equal("Name One", await app.QueryAsync(db => db.Applicants.Where(x => x.Id == db.Applications.First(ap => ap.Id == c.Id).ApplicantId).Select(x => x.FullName).FirstAsync()));
    }

    [Fact]
    public async Task Separation_of_duties_blocks_the_reviewer_from_the_final_decision()
    {
        var c = await _s.PendingApprovalAsync(reviewer: "dual.juliet");
        using var juliet = await app.SignInAsync("dual.juliet");
        var r = await juliet.PostAsync($"/api/applications/{c.Id}/approve", new { version = await _s.VersionAsync(c.Id) });
        r.Expect(HttpStatusCode.Forbidden);
        Assert.Equal(ErrorCodes.SeparationOfDuties, r.Code);
        using var echo = await app.SignInAsync("manager.echo");
        (await echo.PostAsync($"/api/applications/{c.Id}/approve", new { version = await _s.VersionAsync(c.Id) })).Ok();
    }

    [Fact]
    public async Task Cancel_releases_reservation_allocations_and_invalidates_the_permit()
    {
        var c = await _s.PermittedAsync();
        var code = await _s.ShortCodeAsync(c.Id);
        using var manager = await app.SignInAsync("manager.echo");
        (await manager.PostAsync($"/api/applications/{c.Id}/cancel", new { version = await _s.VersionAsync(c.Id) })).Expect(HttpStatusCode.BadRequest); // reason required
        (await manager.PostAsync($"/api/applications/{c.Id}/cancel", new { version = await _s.VersionAsync(c.Id), reason = "The family postponed the event." })).Ok();
        Assert.Equal(CaseStatus.Cancelled, await _s.StatusAsync(c.Id));
        Assert.All(await app.QueryAsync(db => db.Reservations.Where(r => r.ApplicationId == c.Id).ToListAsync()), r => Assert.Equal(ReservationStatus.Released, r.Status));
        var check = (await manager.PostAsync("/api/permits/verify", new { code })).Ok();
        Assert.Equal("Cancelled", check.Body!["result"]!.GetValue<string>());
        // The slot is free again: another case can take it.
        Assert.False(await app.QueryAsync(db => db.Reservations.AnyAsync(r => r.ApplicationId == c.Id && r.Status != ReservationStatus.Released)));
    }

    [Fact]
    public async Task Duplicate_submit_is_prevented()
    {
        var c = await _s.DraftAsync();
        using var intake = await app.SignInAsync("intake.alpha");
        var v = await _s.VersionAsync(c.Id);
        var first = intake.PostAsync($"/api/applications/{c.Id}/submit", new { version = v });
        var second = intake.PostAsync($"/api/applications/{c.Id}/submit", new { version = v });
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, r => r.Status == HttpStatusCode.OK);
        Assert.Single(results, r => r.Status is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity);
        Assert.Equal(1, await app.QueryAsync(db => db.AuditEvents.CountAsync(a => a.ApplicationId == c.Id && a.Action == "Transition.Submit")));
    }

    [Fact]
    public async Task Draft_creation_is_idempotent_by_request_key()
    {
        using var intake = await app.SignInAsync("intake.alpha");
        var key = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = key };
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => intake.SendAsync(HttpMethod.Post, "/api/applications", new { }, headers: headers)));
        Assert.Single(results.Select(r => r.Ok().Id).Distinct());
    }

    [Fact]
    public async Task Reference_numbers_are_unique_under_parallel_creation()
    {
        var actor = await app.ActorAsync("intake.alpha");
        var refs = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => app.WithServiceAsync(sp => sp.GetRequiredService<CaseService>().CreateDraftAsync(actor, null))));
        Assert.Equal(50, refs.Select(r => r.Reference).Distinct().Count());
        Assert.All(refs, r => Assert.Matches(@"^APP-\d{4}-\d{5}$", r.Reference));
    }

    [Fact]
    public async Task Status_change_and_audit_commit_together()
    {
        var c = await _s.DraftAsync();
        var actor = await app.ActorAsync("intake.alpha");
        // A failing save after the status change must leave neither the status nor its audit row.
        await app.WithDbAsync(async db =>
        {
            var row = await db.Applications.FirstAsync(a => a.Id == c.Id);
            row.Status = CaseStatus.Submitted;
            db.AuditEvents.Add(new AuditEvent { At = DateTimeOffset.UtcNow, Action = "Transition.Submit", EntityType = "x", EntityId = "x", ApplicationId = c.Id, Summary = "test" });
            db.Comments.Add(new Comment { ApplicationId = Guid.NewGuid(), Body = "violates the foreign key" });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
        Assert.Equal(CaseStatus.Draft, await _s.StatusAsync(c.Id));
        Assert.False(await app.QueryAsync(db => db.AuditEvents.AnyAsync(a => a.ApplicationId == c.Id && a.Action == "Transition.Submit")));
    }

    [Fact]
    public async Task Hard_deletes_and_audit_updates_are_rejected()
    {
        var c = await _s.DraftAsync();
        await app.WithDbAsync(async db =>
        {
            db.Applications.Remove(await db.Applications.FirstAsync(a => a.Id == c.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
        await app.WithDbAsync(async db =>
        {
            var audit = await db.AuditEvents.FirstAsync(a => a.ApplicationId == c.Id);
            audit.Summary = "tampered";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
    }
}
