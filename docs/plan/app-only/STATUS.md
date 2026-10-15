# App track status

Updated 2026-10-15 (UTC). Branch `app-track-build` (from `acham-app-infra-prompts`). This file records changing state; `CLAUDE.md` and the app-track prompt define how to maintain it.

## Objective

Build and run the Brief 3 application locally (plan `docs/plan/app-only/IMPLEMENTATION-PLAN.md`). Current milestone: **M1/M2** — services, API, mocks, worker and integration tests are committed (M1 core and M2 services); 23 of 26 pages, the case seed and the unit suite are being built (session of 2026-10-15, parallel page agents per group).

## Story checklist

| Story | Title | State |
| --- | --- | --- |
| E0-01 | Repository, governance and status files | done (governance files, status, assumptions, decisions; traceability skeleton pending) |
| E0-02 | Toolchain verification and pinning | done (DEPENDENCIES.md; MinIO check → A-30) |
| E0-03 | Containers and one-command local stack | done (Dockerfiles, compose.yml one-command start verified healthy; tools/Dev runner) |
| E0-04 | Application shell and design system | in progress (design tokens, layout, shared components; UI-DESIGN.md pending) |
| E0-05 | Observability baseline | done (correlation IDs, JSON logs, /health/live|ready|health; log redaction test pending) |
| E0-06 | Test harness and report | in progress (integration harness with PostgreSQL+SeaweedFS+mocks: 18 tests pass; unit and browser suites pending) |
| E0-08 | Deployability contract | in progress (secrets via env/key-per-file, BehindTlsProxy, S3 config, RDS CA bundle, probe; contract tests pending) |
| E0-09 | Public landing page (home, `/`) | done (landing page; browser test pending) |
| E1-01 | Local login and logout | done (login/logout, lockout, 10 demo accounts, demo-password guard) |
| E1-02 | Authorization policies and role matrix | not started |
| E1-03 | Record-level scoping | not started |
| E1-04 | Role-aware navigation | not started |
| E1-05 | Administration: Users & Rules (page 18) | not started |
| E2-01 | Data model and migrations | not started |
| E2-02 | Reference numbers | not started |
| E2-03 | Workflow engine | not started |
| E2-04 | Append-only audit and no hard deletion | not started |
| E2-05 | Optimistic concurrency and conflict UI | not started |
| E2-06 | Business rule settings | not started |
| E2-07 | Cancellation | not started |
| E3-01 | Mock services host | not started |
| E3-02 | Outbound integration pipeline | not started |
| E3-03 | External Integration Hub (page 19) | not started |
| E3-04 | Object storage service | not started |
| E3-05 | Document Vault (page 23) | not started |
| E3-06 | Background job host with locking | not started |
| E4-01 | New Application Wizard (page 03) | not started |
| E4-02 | Application Register (page 02) | not started |
| E4-03 | Comments | not started |
| E4-04 | Application Detail (page 04) | not started |
| E5-01 | Eligibility Review Queue (page 05) | not started |
| E5-02 | Applicant Registry verification (page 20) | not started |
| E5-03 | Information requests and inbox (page 06) | not started |
| E6-01 | Administration: Venues (page 17) | not started |
| E6-02 | Conflict engine | not started |
| E6-03 | Venue Availability (page 08) | not started |
| E6-04 | Event Calendar (page 07) | not started |
| E6-05 | Reservations | not started |
| E6-06 | Resource Planner (page 09) | not started |
| E6-07 | Regional Calendar Sync (page 21) | not started |
| E6-08 | Geolocation & Venue Map (page 26) | not started |
| E7-01 | Approval Workspace (page 10) | not started |
| E7-02 | Permit issuance | not started |
| E7-03 | Permit Center (page 11) | not started |
| E7-04 | Permit token verification service | not started |
| E7-05 | Weather & Advisories (page 24) | not started |
| E8-01 | Daily Event Roster (page 13) | not started |
| E8-02 | Event Check-In (page 14) | not started |
| E8-03 | Incident Register (page 15) | not started |
| E8-04 | Case closure | not started |
| E9-01 | Templates and outbox | not started |
| E9-02 | Gateway sender (worker) | not started |
| E9-03 | Delivery webhook receiver | not started |
| E9-04 | Notification Center (page 12) | not started |
| E9-05 | Messaging Delivery Monitor (page 22) | not started |
| E10-01 | Ingestion service | not started |
| E10-02 | Data Import & Reconciliation (page 25) | not started |
| E10-03 | Partner feed mock scenarios | not started |
| E10-04 | (Stretch) Real SFTP source | not started |
| E11-01 | Operations Dashboard (page 01) | not started |
| E11-02 | Reports & Exports (page 16) | not started |
| E12-01 | Deterministic seed | not started |
| E12-02 | Mock seed data | not started |
| E12-03 | Required live journey as an automated test | not started |
| E12-04 | Integration demo extension as automated tests | not started |
| E12-05 | Demo runbook | not started |
| E13-01 | Failure matrix suite (brief §7.3) | not started |
| E13-02 | Security hardening | not started |
| E13-03 | Accessibility (WCAG 2.1 AA target) | not started |
| E13-04 | Responsive and polish review | not started |
| E13-05 | Clean-machine verification | not started |
| E16-01 | README | not started |
| E16-02 | Architecture, ERD and workflow docs | not started |
| E16-03 | API specification | not started |
| E16-04 | Test report and traceability | not started |
| E16-05 | Screenshots of all 26 pages | not started |
| E16-06 | Implementation log and known limitations | not started |

## Verified results

| When (UTC) | Command | Result |
| --- | --- | --- |
| 2026-10-15 14:42 | `docker compose up -d --wait` (fresh volumes) | passed: 13 services, all long-running ones healthy; migrate seeded 10 users, 74 reference rows |
| 2026-10-15 14:45 | curl checks on 127.0.0.1:5280 | passed: landing 200 (no demo password in HTML), /health Healthy, anonymous /api/me 401, /dashboard 302 to login, CSP/nosniff headers, form sign-in 302 to /dashboard, /api/me returns roles |
| 2026-10-15 15:15 | `dotnet test --project tests/EventPermits.IntegrationTests` | passed: 18/18 (full T1–T13 journey, 6 forbidden moves 422, 403 roles, 404 scope, 409, SoD, cancel releases, idempotent draft and submit, 50 parallel references, atomic audit, no hard delete) |
| 2026-10-15 | `tools/Dev doctor`, `smoke` (by the runner agent) | doctor passed; smoke: health, landing, anonymous and all 10 sign-ins/roles passed; page routes not yet built |

## Not executed / blocked

- Browser/axe suite: not written yet.
- Unit suite: not written yet.
- Windows clean-machine run: no Windows machine available (to be recorded as not executed).

## Decisions and deviations

- A-30: SeaweedFS replaces MinIO (MinIO images no longer public).
- A-31: in-repo PDF writer (PDFsharp 6.5 is AGPL with a licence gate).
- Working branch `app-track-build`; `main` untouched (the user works through branches).

## Running services

- Compose project `event-permits` on 127.0.0.1:5280 (web) and 127.0.0.1:9000 (S3) — started by this session; `docker compose down` stops it (data kept).
- Challenge-2 stack `cemetery-maintenance` was already running (not ours).

## Next action

Merge the page-group branches (cases; review/decisions; scheduling; event day/comms; overview/integrations/admin) and the seed/unit-test branch, run the full `tools/Dev test`, restart the compose stack with the new image, then continue with M3 (failure matrix) and M4 (docs, screenshots, clean clone).
