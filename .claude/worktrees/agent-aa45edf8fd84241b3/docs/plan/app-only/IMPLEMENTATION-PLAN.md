# Memorial Event Operations & Permit Case Management — application plan (Brief 3, app track)

Status: **proposed plan, nothing implemented.** Written 2026-10-15. The implementing agent records progress in `docs/plan/app-only/STATUS.md`, never in this file. Changes to this plan are made deliberately (by the user, or by the agent after the user agrees) and noted in the change log at the end.

This plan builds the application described in `docs/VA_MISI_Technical_Challenge_3 1.pdf` (the **brief**) in .NET 10 and runs it **locally** with Docker Compose. Deploying it to AWS is a separate track with its own plan and prompt in `docs/plan/deploy-infra-only/`. It is organised as **epics → user stories → acceptance criteria**, ordered into **milestones** so that a working, runnable system exists from the first milestone and grows from there.

| File | Audience | Purpose |
| --- | --- | --- |
| `CLAUDE.md` (repository root) | Agent, every session | Stable rules: source hierarchy, non-negotiables, evidence rules. For this track, the app-track prompt names which plan and status file apply |
| `docs/plan/app-only/IMPLEMENTATION-PLAN.md` (this file) | Agent and humans | Scope, architecture, decisions, assumptions, epics and stories for the application |
| `docs/plan/app-only/AI-IMPLEMENTATION-PROMPT.md` | Agent | The prompt that starts or resumes the app track |
| `docs/plan/app-only/STATUS.md` | Agent and humans | Created by the agent in E0-01; the live progress record of this track |
| `docs/plan/deploy-infra-only/*` | Deploy track | Terraform, Azure DevOps pipelines and AWS deployment. Not part of this track |
| `docs/plan/IMPLEMENTATION-PLAN.md` | Reference | The combined plan both tracks were split from. Story IDs are identical across all three plans (E0-07 lives in the deploy track; E0-08 exists only here; E0-09 is in both) |

---

## 1. Scope of this track and the hand-off to the deploy track

**In scope:** the complete application (26 pages, 7 roles, 14 transitions, six mock services, object storage, background worker), seed data, tests, the local tooling (`tools/Dev`), Dockerfiles and `compose.yml`, and the brief's documentation deliverables. Done means a stranger can clone the repository, run it locally from the README alone and see every requirement of the brief working and tested.

**Out of scope:** Terraform, Azure DevOps pipelines, AWS resources, AWS credentials, pushing images to a registry. Do not create `infra/` or `pipelines/`; the deploy track owns them.

**The deployability contract (story E0-08).** The application is built so that the deploy track can run the **same images** on AWS ECS Fargate by configuration alone, with no application code change. Concretely, the app track guarantees:

| Contract item | What the app provides |
| --- | --- |
| Images | Two images (`Dockerfile` → web/worker/migrate; `src/EventPermits.Mocks/Dockerfile` → six mocks), multi-stage, non-root, pinned, building for `linux/amd64` and `linux/arm64` |
| Processes | One web image with commands `web` (default), `worker`, `migrate` (migrations + idempotent seed, exit code 0/non-zero), `init-local`, `probe <url>`; mocks image with `--service <name>` |
| Health | `/health/live`, `/health/ready` (database only), `/health` (all dependencies) on the web; `/health` on each mock; the `probe` command works inside the image (no curl assumed) |
| Configuration | Every setting comes from configuration with environment-variable override (`Section__Key`); secrets accepted **directly as environment variables** (not only as files): `Database__Password`, `Seed__CredentialsJson`, integration secrets, permit token signing key, mock admin key |
| Database | Connection string fully configurable including SSL options (`SSL Mode`, `Root Certificate` path). The web image contains the AWS RDS global CA bundle at `/app/certs/rds-global-bundle.pem`, downloaded at build time from the official AWS URL and verified against a pinned SHA-256, so the deploy track can use RDS with `VerifyFull` (A-15) |
| Storage | `Storage:ServiceUrl` optional (empty = AWS default endpoint), `Storage:ForcePathStyle`, `Storage:PublicEndpoint` optional, `Storage:Bucket`, credentials optional (empty = default AWS credential chain, i.e. the ECS task role) |
| Proxy and TLS | `Hosting:BehindTlsProxy` (default `false`), `Hosting:TrustedProxyNetworks` (CIDRs), `Hosting:HstsMaxAgeDays`: forwarded headers from trusted networks only, `Secure` cookies and HSTS only when enabled. `AllowedHosts` stays `*` (load-balancer health checks use IP host headers) |
| Scale-out | Data Protection keys in PostgreSQL; no in-memory state that breaks with two web instances other than Blazor circuits (sticky sessions); background jobs run only in `worker` and are protected by PostgreSQL advisory locks |
| Webhooks and mocks | Mock base URLs, webhook target URL (`Mock__Notify__WebhookUrl`) and mock admin URLs are configuration; nothing assumes Compose host names |
| Demo password guard | The local demo password works only with `Seed__AllowDemoPassword=true`; otherwise the seeder requires `Seed__CredentialsJson` and refuses the demo value (OD-2) |
| Logs | JSON to stdout only; no log files required |
| Remote testing | `tools/Dev smoke --remote --url <base-url> --credentials-file <path>`; the browser suite runs against any base URL (`E2E__BaseUrl`) and tags a quick subset with the trait `Category=RemoteSmoke` (login per role, all 26 routes reachable, one permit download); credentials come from a file, never from code |
| Documentation | `docs/CONFIGURATION.md` lists every setting, its default, whether it is secret, and its local source |

---

## 2. Decisions for this track

| ID | Question | Decision / default | Why |
| --- | --- | --- | --- |
| OD-1 | .NET version | **.NET 10 LTS — decided by the user 2026-10-15** (brief prefers .NET 8) | .NET 8 support ends 2026-11-10, weeks from now; .NET 10 is supported to 2028-11-14. Same choice as challenge 2. The brief allows a different stack if documented |
| OD-2 | Demo credentials | **Fixed local demo password — decided by the user 2026-10-15.** Every seeded account uses the same documented password `EventPermits!Demo2026` on **local runs only**. The README lists each demo user name, its role(s) and this password (brief FR-01: "seeded demo credentials in README only"); the landing and login pages never show it. The seeder accepts it only when `Seed:AllowDemoPassword=true`, which only `compose.yml` and the local test stack set. Any other environment (AWS) must supply per-user passwords through `Seed:CredentialsJson` and refuses the demo password | Simplest for evaluators. It is a deliberate, documented exception to "no secrets in Git": the accounts are fictional, local and loopback-only, and Gitleaks allowlists this one value with the reason |
| OD-5 | "Batch CSV / SFTP" pattern | **Drop-folder simulation in object storage** (`partner-drop/` prefix in MinIO), fed by the Partner Feed mock and documented as an SFTP simulation. A real SFTP adapter (Compose SFTP container + SSH.NET) is a stretch story (E10-04) | The brief explicitly allows "an equivalent local mock drop directory"; it also deploys to AWS without an SFTP server |
| OD-6 | UI technology | **Blazor Web App, Interactive Server** (Identity pages static SSR), like challenge 2 | Team already knows it; the brief lists Blazor. (In AWS it needs sticky sessions; the deploy track handles that) |

OD-3 (public access and host names) and OD-4 (isolation from challenge 2 in AWS) belong to the deploy track. The agent records each decision in `docs/TECHNICAL-DECISIONS.md` in E0-01 and does not reopen it without a new instruction.

---

## 3. Product summary

### 3.1 Scope

An internal case-management web application that takes a memorial event application from draft to closure: intake → eligibility review (with information requests) → scheduling and resource allocation → approval → permit issuance → day-of-event check-in → incidents → closure, plus management reporting, administration and seven integrations with fictional external systems. All data is invented. This is a practice exercise, not a VA specification.

Out of scope: real email/SMS delivery, real government systems, SSO, applicant self-service portal, payments, multi-region, mobile apps, drag-and-drop calendar (optional in the brief; stretch only).

### 3.2 Page catalogue (26 mandatory routed pages)

Every page is a distinct route with its own navigation entry, real data binding, validation and working actions. A modal or tab never counts as a page. "Primary roles" is the default access; E1-02 finalises the matrix.

| # | Page | Route | Primary roles | Story |
| --- | --- | --- | --- | --- |
| 01 | Operations Dashboard | `/dashboard` | all (role-tailored widgets) | E11-01 |
| 02 | Application Register | `/applications` | Intake (own), Reviewer, Scheduling, Ops Manager, Auditor (masked) | E4-02 |
| 03 | New Application Wizard | `/applications/new`, `/applications/new/{id}/step/{n}` | Intake | E4-01 |
| 04 | Application Detail | `/applications/{id}` | by record scope | E4-04 |
| 05 | Eligibility Review Queue | `/reviews` | Reviewer | E5-01 |
| 06 | Request Information Inbox | `/information-requests` | Intake (respond), Reviewer (track) | E5-03 |
| 07 | Event Calendar | `/calendar` | Scheduling, Ops Manager, Reviewer (read) | E6-04 |
| 08 | Venue Availability | `/venues/availability` | Scheduling, Ops Manager | E6-03 |
| 09 | Resource Planner | `/resources` | Scheduling, Ops Manager | E6-06 |
| 10 | Approval Workspace | `/approvals` | Ops Manager | E7-01 |
| 11 | Permit Center | `/permits` | Ops Manager; Auditor (metadata) | E7-03 |
| 12 | Notification Center | `/notifications` | Ops Manager, Admin; Auditor (read) | E9-04 |
| 13 | Daily Event Roster | `/operations/roster` | Ops Manager, Attendant (own), Scheduling (read) | E8-01 |
| 14 | Event Check-In | `/operations/check-in` | Assigned Attendant (record); Ops Manager (read-only verify) | E8-02 |
| 15 | Incident Register | `/incidents` | Attendant (log), Ops Manager (resolve), Auditor (read) | E8-03 |
| 16 | Reports & Exports | `/reports` | Ops Manager, Auditor | E11-02 |
| 17 | Administration: Venues | `/admin/venues` | Admin; Scheduling (read) | E6-01 |
| 18 | Administration: Users & Rules | `/admin/users-rules` | Admin; Auditor (audit viewer, read) | E1-05 |
| 19 | External Integration Hub | `/integrations` | Admin, Ops Manager (read) | E3-03 |
| 20 | Applicant Registry Verification | `/integrations/registry` | Reviewer, Admin; Auditor (read) | E5-02 |
| 21 | Regional Calendar Sync | `/integrations/calendar` | Scheduling, Ops Manager, Admin | E6-07 |
| 22 | Messaging Delivery Monitor | `/integrations/messaging` | Ops Manager, Admin | E9-05 |
| 23 | Document Vault | `/documents` | by record scope; Auditor metadata only | E3-05 |
| 24 | Weather & Advisories | `/integrations/weather` | Ops Manager, Scheduling, Admin | E7-05 |
| 25 | Data Import & Reconciliation | `/integrations/imports` | Admin, Scheduling | E10-02 |
| 26 | Geolocation & Venue Map | `/venues/map` | Admin (confirm), Scheduling, Ops Manager (read) | E6-08 |

Supporting routes (not counted): `/` (public landing page, E0-09), `/account/login`, `/account/logout`, `/access-denied`, `/integrations/attempts/{id}` (Integration Attempt detail, brief §7.2), `/health`, `/health/live`, `/health/ready`, API and webhook endpoints.

### 3.3 Roles (brief §3)

Intake Coordinator, Eligibility Reviewer, Scheduling Officer, Operations Manager, Event Attendant, Compliance Auditor, System Admin. Seed at least one account per role and **two** Attendants (8+ accounts). A user may hold more than one role (A-03); separation-of-duties rules apply per user, not per role.

### 3.4 Workflow (brief §5)

| # | From | To | Actor | Guards (all server-side) |
| --- | --- | --- | --- | --- |
| T1 | Draft | Submitted | Intake Coordinator (owner) | All wizard sections valid; ≥1 required attachment; event start in the future; duration 30–300 min; attendees 1..venue capacity; applicant name and contact present; idempotent (no duplicate submit) |
| T2 | Submitted | Needs Information | Eligibility Reviewer | Missing items listed; response due date set (future) |
| T3 | Needs Information | Submitted | Intake Coordinator (owner) | Response supplied to every open request; earlier requests and responses preserved |
| T4 | Submitted | Eligible | Eligibility Reviewer | Checklist complete; registry evidence `Valid`, or a rule-permitted override recorded (A-10) |
| T5 | Submitted | Rejected | Eligibility Reviewer | Rejection reason required |
| T6 | Eligible | Tentatively Reserved | Scheduling Officer | Eligibility checklist complete; no venue/time collision incl. buffers, blackouts, opening hours, external and partner events; capacity valid; resources within inventory; tentative hold pushed to the regional calendar (A-27; offline push leaves the reservation tentative and flags it) |
| T7 | Tentatively Reserved | Pending Approval | Scheduling Officer | Final reservation summary complete (venue, slot, resources, attendant needs) |
| T8 | Pending Approval | Approved | Operations Manager | Approver did not review this case (SoD); external calendar reconciled for the slot (§7.1); no unresolved calendar conflict |
| T9 | Pending Approval | Rejected | Operations Manager | Reason required; SoD as T8; reservation, resource allocations and the external calendar hold released |
| T10 | Approved | Permitted | Operations Manager | Immutable PermitVersion generated; issuer recorded; severe-weather advisory acknowledged if present (§7.1) |
| T11 | Permitted | Checked In | Assigned Event Attendant | Permit token valid (current version, not revoked/cancelled/expired) and inside the configured check-in window |
| T12 | Checked In | Event Completed | Assigned Event Attendant | Check-out recorded; readiness checklist complete |
| T13 | Event Completed | Closed | Operations Manager | No unresolved High or Critical incident |
| T14 | Submitted, Eligible, Tentatively Reserved, Pending Approval, Approved, Permitted | Cancelled | Operations Manager (A-04) | Reason required; reservations and resource allocations released; current permit invalidated |

Every other transition returns **422**. Status change and its audit event commit in **one transaction**. Rejected and cancelled cases stay visible with full history.

### 3.5 HTTP error contract

Order of checks: **401** unauthenticated (API never redirects to HTML login) → **404** record does not exist or is outside the caller's scope without leaking details (A-06) → **403** known-scope record but forbidden action → **400** malformed input / field validation (ProblemDetails with field errors) → **409** stale row version (latest safe values + reload path) → **422** invalid transition or business guard (stable error code) → **503** dependency unavailable with `retryable: true`. Every error body carries `correlationId`.

---

## 4. Architecture

### 4.1 Solution layout

```text
EventPermits.slnx
src/
  EventPermits.Domain/            entities, value objects, workflow transition table, guards, conflict engine (pure)
  EventPermits.Application/       commands/queries, authorization policies, validation, transactions, outbox, ports
  EventPermits.Infrastructure/    EF Core (PostgreSQL), Identity stores, Data Protection keys, S3 object store,
                                  integration adapters (typed HttpClients), PDF/QR, background job locks
  EventPermits.Web/               Blazor UI (26 pages), Identity pages, minimal-API endpoints, webhook receiver,
                                  health, admin CLI (migrate, seed, init-local, probe, worker)
  EventPermits.Mocks/             ONE image, SIX independently runnable mock services selected by --service:
                                  registry, calendar, notify, weather, geocode, partner-feed; shared scenario engine
tests/
  EventPermits.UnitTests/         domain rules, conflict engine, HMAC, token cache, retry classification, dedupe
  EventPermits.IntegrationTests/  WebApplicationFactory + Testcontainers (PostgreSQL, MinIO) + in-process mocks;
                                  adapter contract tests; authorization matrix; 409/422; storage; webhooks; CSV
  EventPermits.BrowserTests/      Playwright + axe: every page, role journeys, failure-matrix UI, screenshots
tools/Dev/                        cross-platform runner: doctor, setup, up, down, seed, test, smoke, scan, screenshots
(infra/ and pipelines/ are created later by the deploy track; this track does not create them)
docs/                             brief, plan, architecture, ERD, workflow, API spec, test report, screenshots, logs
Dockerfile                        web image (web, worker, migrate/seed, probe)
src/EventPermits.Mocks/Dockerfile mocks image
compose.yml
```

Rules: one server-side application service path for UI and HTTP endpoints. Every state-changing UI action calls an application-service method that performs authorization, validation, guards and auditing itself, and the same method is exposed as a minimal-API endpoint under `/api/...`, so crafted-request tests (brief §3: "server-side authorization is mandatory for every endpoint") exercise exactly the code the UI uses. Blazor components never touch `DbContext` for writes. No generic repository over EF; no mediator framework unless a concrete need appears; `IDbContextFactory` for Blazor circuits; Identity cookie issuance only through HTTP Identity pages.

### 4.2 Local runtime topology

| Concern | Docker Compose service | Notes |
| --- | --- | --- |
| Secrets bootstrap | `init` (web image, `init-local`) | Generates ignored secrets into `.data/secrets` (DB password, MinIO user keys, integration secrets); user passwords are not generated locally (OD-2); no dependencies; idempotent |
| Database | `db` PostgreSQL 18 | Named volume; internal only |
| Object storage | `minio` + `minio-init` | Private bucket and prefixes `attachments/`, `permits/`, `partner-drop/`; S3 API on `127.0.0.1:9000` for pre-signed redirects (A-16); console on loopback. Least privilege (brief §7.4): `minio-init` alone uses the root credentials, to create the bucket and two users: `app` (web, worker, migrate: read/write on the bucket) and `partner-feed` (write to `partner-drop/` only) |
| Migrations + seed | `migrate` (web image, `migrate`) | Runs before `web`/`worker` via `service_completed_successfully`; idempotent |
| Web UI + API | `web` | `127.0.0.1:5280` (`WEB_PORT`); HTTP on loopback (A-25) |
| Background jobs | `worker` (web image, `worker`) | Outbox sender, status poller, calendar sync, partner import schedule, overdue marker, orphan cleanup |
| Mocks | `mock-registry`, `mock-calendar`, `mock-notify`, `mock-weather`, `mock-geocode`, `mock-partner-feed` | Internal; optional `debug` profile publishes them on loopback |
| Webhook callbacks | `mock-notify` → `http://web:8080/webhooks/delivery` | Target URL is configuration |
| Logs | stdout JSON | `docker compose logs <service>` |

A host-process development mode (`tools/Dev up --host`, running web and worker with `dotnet run` against the Compose `db`, `minio` and mocks) is optional and may be added for debugging; Compose remains the reference way to run.

The application never hard-codes where it runs: everything above is configuration (section 1, deployability contract).

### 4.3 Technology (verify and pin exact versions in E0-02)

.NET 10 LTS SDK pinned in `global.json`; ASP.NET Core 10 Blazor Web App (Interactive Server); ASP.NET Core Identity; EF Core 10 + Npgsql 10 (xmin row version); PostgreSQL 18; AWSSDK.S3 (MinIO with path-style addressing; the same client works against S3); `Microsoft.Extensions.Http.Resilience` for timeouts/retries/circuit breakers; Serilog compact JSON; built-in `Microsoft.AspNetCore.OpenApi` for the API spec; PDF generation with a permissively licensed library (default PDFsharp-MigraDoc, MIT; **QuestPDF only if its licence terms fit the organisation**, record the check); QRCoder (MIT); Chart.js vendored and pinned in `wwwroot` (no CDN); xUnit v3; Testcontainers for .NET; Microsoft.Playwright + pinned local axe-core; Gitleaks; Trivy (image scan).

Version policy: latest stable compatible versions verified from official sources at E0-02, exact pins (central package management + lock files, container images by digest, CLI tools by checksum). No floating tags, previews or remembered version numbers. **MinIO check:** the brief names MinIO; verify an official MinIO server image is still published. If not, pin the last official image by digest or use another S3-compatible emulator, and record the deviation.

### 4.4 Cross-cutting design rules

1. **Transition table** in the domain: `(from, to) → (allowed roles, guards[])`. Only command endpoints change status; no generic status setter. Unlisted pairs → 422.
2. **Audit**: `AuditEvent` (actor, UTC time, action, entity, before/after summary, reason, correlation ID) is appended in the same transaction as the change. EF `SaveChanges` interceptor rejects hard deletes and updates of audit rows. Soft deactivation only.
3. **Concurrency**: every mutable aggregate has a row version (PostgreSQL `xmin`); child mutations bump the parent (`Application`) version. Every write sends the version it read. Conflict → 409 with current safe values; UI shows a merge/reload panel (FR-18).
4. **Idempotency**: client request keys for draft creation, submission, check-in/out, notification sends (gateway `Idempotency-Key`), webhook `eventId`, CSV batch checksum + `partnerEventId`.
5. **Outbox**: domain changes write their outbound work in the same transaction: `Notification` rows (status Queued) for messages, and `OutboxMessage` rows for other outbound integration commands (calendar reservation pushes and their retries). The worker processes both. A failed send never rolls back a permit or a reservation.
6. **No external HTTP inside a DB transaction.** Call, then re-check state/version, then commit.
7. **Time**: store UTC (`timestamptz`); each cemetery has an IANA time zone; UI shows venue-local time with the zone label. Injected `TimeProvider` for testable clocks.
8. **Config over code** for business thresholds (`RuleSetting` table, page 18) with defaults from the brief.
9. **Accessibility first**: semantic HTML, labelled controls, focus management, live regions for status/validation/conflict messages, text on every status chip, 44 px targets, responsive breakpoints, `prefers-reduced-motion`.

### 4.5 Integration platform (brief §7)

Every outbound integration goes through the same pipeline:

```text
UI action / job → Application service → Port (IApplicantRegistry, ICalendarClient, ...) → Adapter (typed HttpClient)
   → handlers: correlation ID → auth (API key / OAuth bearer) → resilience (timeout, transient-only retry with
     bounded exponential backoff + Retry-After, circuit breaker) → IntegrationAttempt recorder (sanitised)
```

- `IntegrationAttempt` row per HTTP call: integration, operation, request ID, correlation ID, linked case ID, sanitised URL (no keys/tokens), HTTP status, outcome class (Success, ClientError, Transient, Timeout, Offline), duration ms, attempt number, error summary. Viewable at `/integrations/attempts/{id}`.
- Request/response logs (brief §7): each attempt also stores a sanitised request summary (method, path, query, selected headers) and a response excerpt (status, selected headers, body truncated to 2 KB), shown on the attempt detail page.
- **Redaction**: headers `Authorization`, `X-API-Key`, `X-Signature`, query `access_token`, body fields named like `secret|password|token|client_secret` are masked before logging or storing. Tested.
- Every integration page (19–26) shows live service results, the last-request time, meaningful empty and error states, and at least one action that makes a real call (brief §7.2).
- Configuration per integration: `Integrations:<Name>:BaseUrl`, `...:TimeoutSeconds`, credentials only from environment/secret store. Hub shows values masked (`ab••••yz`) and never returns secrets from any API.
- Health per integration: Healthy / Degraded / Offline from the last N attempts plus an active probe; included in `/health`, never failing `/health/ready` (the app must keep running when an integration is down).

**Mock services** (one project, one image, `--service <name>`), each with:

| Mock | Contract (brief §7, §7.1) | Scenario modes (beyond `normal`) |
| --- | --- | --- |
| registry | `GET /api/applicants/{reference}` with `X-API-Key` → `{referenceId, matchedName, eligibilityStatus: Valid\|Flagged\|Unknown, verifiedAt}`; 404; 429 + `Retry-After`; 500 | slow, 401, 404, 429, 500, timeout, offline |
| calendar | `POST /oauth/token` (client_credentials) → `{access_token, expires_in, token_type}`; `GET /events?from=&to=&modifiedSince=` → `[{externalId, venueCode, startUtc, endUtc, modifiedUtc}]`; `POST /reservations` → 201 or 409 with conflicting event + alternatives | slow, invalid_client, short-token (expires in seconds), 401-once, 409, 500, timeout, offline |
| notify | `POST /messages` (`Idempotency-Key`) → 202 `{messageId, status}`; `GET /messages/{messageId}`; callback `POST {app}/webhooks/delivery` with `{eventId, messageId, status: Delivered\|Failed, occurredAtUtc}` and `X-Signature`, `X-Timestamp` headers | slow, 500, timeout, offline, fail-delivery, duplicate-callbacks, out-of-order, bad-signature, stale-timestamp |
| weather | `GET /forecast?venue=&date=` → `{venueCode, date, summary, temperatureC, windKph, precipitationPct, advisory: None\|Advisory\|Severe, sourceTimestampUtc}` | slow, 429, 500, timeout, offline, severe |
| geocode | `GET /geocode?q=` → `[{label, latitude, longitude, confidence 0..1}]` | slow, 500, timeout, offline, ambiguous (tie), no-match |
| partner-feed | Writes CSV files (`partnerEventId, siteCode, startUtc, endUtc, attendeeCount, updatedAtUtc`) into the `partner-drop/` prefix on schedule or on demand | clean, bad-rows, duplicate-file, updated-rows, malformed-header |

Admin API on every mock (separate `X-Mock-Admin-Key`, never reachable from the browser directly): `GET/PUT /_admin/scenario`, `GET /_admin/stats` (call counts per endpoint, for "prove the UI makes real requests"), `POST /_admin/reset`. The app's Integration Hub (page 19) exposes a **Scenario Panel** for System Admins that calls these through the server. The starting mode can also be set with an environment variable (`Mock__Mode`), which satisfies the brief's "environment switches" option (§7.4). Mock data is seeded deterministically from committed JSON files that are copied into **both** the web and mocks images, so the app seed and the mocks always agree (challenge-2 pattern).

The notification mock calls back the app at the URL in `Mock__Notify__WebhookUrl` (`http://web:8080/webhooks/delivery` in Compose). The endpoint is anonymous but HMAC-verified; the deploy track additionally blocks `/webhooks/*` at the load balancer.

---

## 5. Assumptions (record in `docs/ASSUMPTIONS-LOG.md` in E0-01)

Columns: ID, assumption, reason, impact if wrong, verification/mitigation, decision owner (`team` until a person confirms). These are implementation choices, **not** brief requirements.

| ID | Assumption |
| --- | --- |
| A-01 | .NET 10 LTS instead of the preferred .NET 8 (OD-1; decision owner: user, 2026-10-15) |
| A-02 | All seeded accounts share the documented local demo password `EventPermits!Demo2026` (OD-2); it is enabled only by `Seed:AllowDemoPassword=true` (local Compose and test stacks). Without the flag the seeder requires `Seed:CredentialsJson` and refuses the demo password, so it can never reach AWS. Lockout still applies, so evaluators who mistype five times wait 15 minutes |
| A-03 | Users may hold several roles (admin role assignment exists; SoD "cannot approve case they directly reviewed" needs it to be meaningful). Seed one user with Reviewer + Ops Manager to demonstrate SoD. System Admin role never grants case actions |
| A-04 | "Authorized Manager" for cancellation = Operations Manager only |
| A-05 | System Admin has no access to applicant/case data beyond what admin pages need (least privilege); sees integration and audit metadata |
| A-06 | A case outside the caller's scope returns 404 (no existence leak); a visible case with a forbidden action returns 403. IDs are random GUIDs; reference numbers are not route keys |
| A-07 | APP-YYYY-NNNNN is assigned when the draft is created (year of creation); PER-YYYY-NNNNN on first issuance; later versions keep the number with version `v2`, `v3`… Per-year sequences in a counter table under row lock, unique index; exhausting 99,999 returns a clear error |
| A-08 | Event duration 30–300 minutes excludes setup/teardown buffers; buffers are per venue (defaults 30/30 min) and included in collision checks |
| A-09 | Check-in window default: 60 min before start until scheduled end (RuleSetting) |
| A-10 | Registry override: a Reviewer may override `Flagged` or `Unknown` only when the rule setting `RegistryOverrideAllowed` permits it for that status, with a reason ≥ 20 chars; default allows `Unknown` override, forbids `Flagged`. Each check and override is audited |
| A-11 | Permit revocation is a permit-level action (Ops Manager, justification required): it invalidates the current version; the case stays Permitted until a new version is issued or the case is cancelled. Check-in rejects revoked tokens |
| A-12 | Permit token = opaque signed token (HMAC-SHA256 over permit ID + version + expiry) encoded in the QR and printed as a short typed code. Token expiry = scheduled event end + 2 hours (check-out grace). Results: before the window opens → OutsideWindow (too early); after expiry → Expired; valid = current version, case Permitted, not revoked/cancelled, inside the check-in window |
| A-13 | Information-request due date default 5 business days, minimum 1 day; overdue requests are flagged, not auto-rejected |
| A-14 | Files: PDF/PNG/JPG, max 5 MiB (5,242,880 bytes), checked by extension, declared MIME and magic bytes; signature checks are not antivirus; Auditor sees metadata only; Attendant uploads incident photos only |
| A-15 | (Deploy track: RDS TLS verification. The app track only guarantees the connection string accepts SSL options; see the deployability contract) |
| A-16 | Expiring download links have two layers. (1) The UI gets an **app link** `/files/{id}/download?exp=…&sig=…` (HMAC-signed, TTL 5 minutes from RuleSetting, bound to user and file), issued only after a role/scope check and audited. (2) Opening it re-checks the user's current role/scope and the expiry, audits the download, then answers 302 to a **storage-native pre-signed URL** (MinIO/S3) valid for 60 seconds. An expired or forged app link shows a friendly 403 "link expired" page with a role-authorised **Regenerate link** action, rather than raw S3 XML. The bucket stays private throughout. MinIO pre-signing uses a browser-reachable public endpoint (`Storage:PublicEndpoint`) |
| A-17 | Partner CSV policy: partial success. Valid rows upsert by `partnerEventId` (newer `updatedAtUtc` wins); invalid rows go to a downloadable error report; same file checksum = replay no-op unless "Replay" is chosen; imported partner events occupy venues in the conflict engine |
| A-18 | External calendar events occupy venues in the conflict engine; if the calendar is offline, tentative reservations are allowed but T8 is blocked until a successful sync covers the slot |
| A-19 | Severe weather: Ops Manager acknowledgement (with note) is required before T10 for that event date; advisories never cancel cases |
| A-20 | Webhook signature = hex HMAC-SHA256 over `timestamp + "." + rawBody`; tolerance 5 minutes; delivery status order `Queued < Sent < Delivered`, `Failed` terminal unless retried; never regress from Delivered |
| A-21 | Compliance Auditor sees applicant contact details masked |
| A-22 | Event Attendant sees only events assigned to them on the roster, with name and on-site contact only |
| A-23 | Saved views on the register are per user (name + query string), not shared |
| A-24 | Event date "future at submission" is evaluated in venue local time against the current instant |
| A-25 | Local and Compose runs serve plain HTTP on 127.0.0.1 only (no generated certificate); internal service-to-service traffic is HTTP inside the Compose network or VPC. HTTPS applies in AWS when `base_domain` is set ("HTTPS where configured", brief §7.4) |
| A-26 | Case edit and upload rights after creation: the Intake Coordinator edits applicant/event/facility sections and uploads documents on own cases only in Draft and Needs Information; the Reviewer adds review notes but does not edit applicant data; the Scheduling Officer edits reservation/resources only in Eligible and Tentatively Reserved; Attendants upload incident photos only; nobody edits a case after Closed, Rejected or Cancelled except append-only comments by staff with access |
| A-27 | External calendar push: a tentative hold is pushed (`POST /reservations`) when T6 creates or changes a reservation. A 409 opens a ReconciliationItem; T8 requires the push to have succeeded **and** a successful sync covering the slot after the push |
| A-28 | Data Protection keys are stored in PostgreSQL without an extra key-encryption certificate; protection relies on RDS/volume encryption and database access control (practice-scope limitation, listed in KNOWN-LIMITATIONS) |
| A-29 | Seed timeline is relative to an anchor date (the day of the first seed, stored in the database): events today, past events for completed/closed cases, future events for open cases, so the dashboard, roster and check-in window work on that day. Re-seeding never shifts existing data; `tools/Dev seed --reset` (destructive, explicit) re-anchors for a demo day. Tests use an injected `TimeProvider`, never the wall clock |

The app-track agent adds new assumptions in the range **A-30 to A-59** (the deploy track uses A-60 onward, so both tracks can share one log without collisions) as gaps are found, and labels each in UI help text or error messages where it affects users.

---

## 6. Delivery approach

### 6.1 Milestones

| Milestone | Goal | Exit evidence |
| --- | --- | --- |
| **M0 Local skeleton** | Repo, pinned toolchain, Compose stack (init, db, minio, migrate, web, worker, one mock), landing page, login with seeded users, health, logs, test projects, deployability contract in place | `docker compose up -d --wait` healthy from a clean checkout; `tools/Dev test` green; contract checklist (E0-08) passed |
| **M1 Thin end-to-end journey** | All 14 transitions enforced with minimal but real pages; audit, 409, 422, 403; storage upload; permit PDF; check-in | Integration tests for every transition and ≥6 forbidden moves; browser test of the full brief §9 journey (happy path) |
| **M2 All 26 pages complete** | Every page fully functional, validated and accessible; all integrations working in `normal` mode | Browser test per page; axe clean on all pages; screenshots generated |
| **M3 Failure matrix** | All scenario modes, Scenario Panel, every row of brief §7.3 and §8 demonstrated and tested | Failure-matrix test suite green; mock call counts recorded |
| **M4 Local release** | Full seed, docs set, test report, screenshots, clean-clone check | Section 11 checklist complete with evidence |

Stories are tagged with their milestone. Within a milestone follow dependencies; across milestones, a story may be **started** early but is finished in its milestone. The deploy track can start once M0 is done and redeploys as each later milestone lands, so keep `main` buildable and the Compose stack healthy after every story.

### 6.2 Story format, Definition of Ready and Definition of Done

Each story: ID, title, milestone, brief references, user story sentence, acceptance criteria (AC), notes, dependencies. ACs are testable statements; "Given/When/Then" is used where the sequence matters.

**Ready** when: brief references read; dependencies done; open assumptions recorded; the proving tests named.

**Done** when all are true:
1. Every AC is met and proven by an automated test (unit, integration or browser) or, where automation is impossible, by recorded manual evidence.
2. Server-side authorization is tested with crafted requests for each new endpoint (allowed role passes; at least one forbidden role gets 403/404).
3. New or changed pages: browser test, axe scan (WCAG 2.1 A/AA tags) with 0 violations, keyboard path, phone (≈390 px) and desktop (≈1280 px) layouts checked; no dead controls or placeholder text.
4. No secret in code, config, logs or test output (the documented local demo password, OD-2, is the single deliberate exception); structured log entries for the new actions.
5. `docs/plan/app-only/STATUS.md`, `docs/REQUIREMENTS-TRACEABILITY.md` and affected docs updated; assumptions logged.
6. Unit and integration suites green, plus the browser tests of the pages the story touched; `docker compose up -d --wait` healthy; the deployability contract still holds. The **full** `tools/Dev test` (all suites, all pages) runs green at the end of every epic and before every milestone exit. Committed as `<story-id>: <summary>`. If the deploy track's PR validation pipeline exists, it must be green too.

### 6.3 Evidence rules

Passed only with successful command output. Failed (with output), not executed (reason + exact command) and blocked (what is missing) are separate states. A test that was written but not run is not a passing test. Never copy results, counts or versions from challenge 2.

---

## 7. Epics and user stories

### E0 — Foundation and local skeleton

#### E0-01 Repository, governance and status files · M0
As the team, we want a clean repository with governance documents, so that every session starts from the same facts.
- AC1 `.gitignore`, `.gitattributes`, `.editorconfig`, `Directory.Build.props` (nullable, warnings as errors, analyzers), `Directory.Packages.props` (central package management), `global.json` exist.
- AC2 `docs/plan/app-only/STATUS.md` lists every story in this plan with state `not started`, plus sections: Objective, Inputs, Verified results, Not executed/blocked, Decisions, Running services, Next action.
- AC3 `docs/ASSUMPTIONS-LOG.md` (A-01…A-29), `docs/TECHNICAL-DECISIONS.md` (OD-1…OD-6 with alternatives), `docs/REQUIREMENTS-TRACEABILITY.md` skeleton (rows from section 10), `docs/IMPLEMENTATION-LOG.md` exist.
- AC4 Commits on `main` with real incremental history; pushing to Azure Repos `misi-code-challenge-brief-3` only after the user approves at that moment.

#### E0-02 Toolchain verification and pinning · M0
As the team, we want exact, verified versions, so that builds are repeatable on a clean machine.
- AC1 `docs/DEPENDENCIES.md` lists every SDK, package, container image (digest), CLI tool and browser with version, official source URL and date checked; unverifiable items say `pending verification`.
- AC2 A compatibility spike restores (`--locked-mode`) and builds all projects with 0 warnings.
- AC3 MinIO image availability checked (section 4.3) and the outcome recorded.

#### E0-03 Containers and one-command local stack · M0
As a developer, I want `docker compose up -d --wait` to start the whole system, so that a clean machine needs only Git and Docker to run it.
- AC1 Multi-stage `Dockerfile` (web) and `src/EventPermits.Mocks/Dockerfile`, non-root users, pinned base images, no secrets in layers, `.dockerignore`.
- AC2 `compose.yml` services: `init` (secrets, no deps), `db`, `minio`, `minio-init`, `migrate` (migrations + seed, idempotent), `web`, `worker`, and the mock services (skeleton: `mock-registry` only in M0; others added by their stories). Health checks use the image's own `probe` command (no curl assumed).
- AC3 Web published on `127.0.0.1:5280` (variable `WEB_PORT`); MinIO S3 API (`127.0.0.1:9000`, needed so the browser can follow pre-signed download redirects, A-16) and console on loopback only; db and mocks internal (an optional `debug` profile may publish mock ports on loopback).
- AC3b `docker compose up -d --wait` alone produces a working, seeded system: `init`, `minio-init` and `migrate` run first through `depends_on: condition: service_completed_successfully` (`migrate` after `minio-init`, because the seed uploads files), all idempotent.
- AC4 Data survives `docker compose down` / `up`; destructive reset documented separately.
- AC5 `tools/Dev` commands `doctor`, `setup`, `build`, `migrate`, `seed` (with explicit `--reset`), `up`, `down`, `test`, `smoke`, `scan`, `screenshots` work on macOS and Windows (pwsh/zsh/bash, no shell-specific quoting). The README presents install → build → migrate → seed → run → test as separate commands (brief §10), plus the one-command `docker compose up -d --wait` shortcut.

#### E0-04 Application shell and design system · M0
As any user, I want a consistent, accessible layout with role-aware navigation, so that 26 pages feel like one product.
- AC1 Header with product name, user/role indicator and logout; grouped navigation (Cases, Scheduling, Operations, Integrations, Reports, Administration) showing only permitted pages; skip link; breadcrumbs.
- AC2 Design tokens (colour, spacing, type) in one stylesheet; status chip component always shows text; table, form, wizard, drawer, toast/live-region, empty-state, error-state, pager and conflict-panel components built once and reused.
- AC3 Responsive at 1280/900/720/420 px; tables become labelled cards on phones; 44 px targets; reduced motion respected.
- AC4 Friendly 403/404/500 pages with correlation ID; no stack traces in Production.
- AC5 Visual direction chosen once and recorded in `docs/UI-DESIGN.md` (palette tokens with measured contrast ratios, type scale, spacing, status colours that always pair with text, layout patterns for lists, detail pages, wizards and dashboards), so all 26 pages look like one polished product (brief §10). Local design skills such as `frontend-design` or `impeccable` may be used; they are not repository content, so record the resulting decisions in the document. No web fonts or CDN assets.

#### E0-05 Observability baseline · M0 · FR-16
As an operator, I want structured logs with correlation IDs and dependency health, so that I can trace any request.
- AC1 Correlation ID middleware: accepts/creates `X-Correlation-ID` (bounded length), adds it to logs, responses, ProblemDetails and outbound calls.
- AC2 Serilog compact JSON to stdout with actor, roles, case reference (when known), action, result, elapsed ms; passwords, tokens, file bytes and full form bodies never logged (tested).
- AC3 `/health/live` (process), `/health/ready` (database), `/health` (app, database, object storage, each integration, notification gateway) with Healthy/Degraded/Unhealthy JSON and no secrets. Integration outage → Degraded, not Unhealthy.

#### E0-06 Test harness and report · M0 · FR-17
As the team, we want unit, integration and browser suites from day one, so that every story adds tests to a working harness.
- AC1 Three test projects run with one command (`tools/Dev test`), nonzero exit on any failure or missing suite; counts of discovered/executed tests printed.
- AC2 Integration tests use Testcontainers (PostgreSQL 18, MinIO) and in-process mocks; no EF InMemory.
- AC3 Browser tests run Playwright (Chromium, pinned) against a Compose stack started by the runner under its own Compose project name, ports and volumes, so tests never touch the developer's running stack or data; axe pinned locally; the runner cleans up only what it started.
- AC4 TRX results converted to a readable HTML/Markdown report under `docs/test-report/` by a scripted step (brief deliverable).

#### E0-08 Deployability contract · M0 (then kept true in every story)
As the deploy track, I want the application to run on AWS by configuration alone, so that deployment never forces an application rewrite.
- AC1 Every item of the deployability contract (section 1) is implemented and covered by a test or a scripted check: `migrate` exit codes; `probe` inside both images; secrets accepted from environment variables (unit tests for precedence: env var over file; clear error naming both options when missing; no secret in the error); `Hosting:BehindTlsProxy=true` integration test (trusted `X-Forwarded-Proto: https` → `Secure` auth and antiforgery cookies, HSTS, `https` redirect `Location`; untrusted source ignored); storage client works with `ServiceUrl` empty and default credentials (unit test of configuration binding); two web instances against one database share login cookies (integration test with two hosts and one key store); without `Seed:AllowDemoPassword` the seeder rejects the demo password and demands `Seed:CredentialsJson` (test).
- AC2 Both images build for `linux/amd64` (checked with `docker build --platform linux/amd64`) as well as the native architecture; record the UID the images run as.
- AC3 `docs/CONFIGURATION.md` lists every setting (name, default, secret yes/no, local source); a test fails if a bound options class has a key missing from the document.
- AC4 `tools/Dev smoke --url <base-url> --credentials-file <path> --remote` runs an end-to-end smoke (login per role, page reachability for all 26 routes, one upload and download) against any running instance, skipping only Docker-dependent steps (clearly reported as SKIPPED). The deploy track reuses it after every deployment. The browser suite honours `E2E__BaseUrl` and the `RemoteSmoke` trait (contract row "Remote testing"); a test proves the subset runs against the local Compose URL given as a remote base URL.
- AC5 The RDS CA bundle is present in the web image at the contract path with the pinned checksum (scripted check).

#### E0-09 Public landing page (home, `/`) · M0 (skeleton) / M2 (final content)
As a visitor or evaluator, I want a home page that explains the application before I sign in, so that I understand what it does and how to try it.
- AC1 `/` is anonymous, server-rendered without an interactive circuit, and is where the app opens. It is **not** one of the 26 counted pages and shows no case data, counts, user names or integration status (nothing that needs authorization).
- AC2 Content: product name and a one-paragraph purpose; a prominent notice "Fictional practice exercise, not a VA system; all data is invented"; the case lifecycle as an accessible diagram with a text equivalent (Draft → Submitted → … → Closed, plus Needs Information, Rejected, Cancelled); the main modules (intake and register, eligibility review, scheduling and resources, approvals and permits, event-day operations, notifications, reports, administration, external integrations), each with one line; the seven roles and what each does; a primary "Sign in" action; a footer with the version (build commit) and a pointer to the repository README for setup and demo accounts. Demo credentials never appear on this page (brief FR-01: README only).
- AC3 Signed-in users see the same page with "Go to your dashboard" instead of "Sign in". Signing in from the landing page goes to `/dashboard`, or to a validated local `returnUrl`; signing out returns to `/`. Anonymous visits to protected pages still go to the login page with `returnUrl`.
- AC4 Uses the design system (E0-04, `docs/UI-DESIGN.md`): polished, responsive at the same breakpoints, axe clean, keyboard path to "Sign in", meaningful `<title>` and meta description, no external assets.
- AC5 Browser tests: anonymous render and working sign-in link; signed-in variant; the HTML contains neither the demo password nor any case data.

### E1 — Identity, roles and access control (FR-01)

#### E1-01 Local login and logout · M0
As a staff user, I want to sign in with a local account, so that the system knows my roles.
- AC1 ASP.NET Core Identity with Identity HTTP pages for login/logout; lockout after 5 failed attempts for 15 minutes; generic error text; no self-registration.
- AC2 Cookies `HttpOnly`, `SameSite=Lax`; `Secure` when `Hosting:BehindTlsProxy=true` and the request was HTTPS (config-gated, see E0-08); antiforgery on all form posts and state-changing API calls.
- AC3 Seeded accounts: one per role, two Attendants, one Reviewer+Ops Manager dual-role user (A-03); all use the local demo password (OD-2), enabled by `Seed:AllowDemoPassword=true` in Compose; README table lists every account.
- AC4 Inactive users cannot sign in and existing sessions are rejected on the next request.

#### E1-02 Authorization policies and role matrix · M1
As a security owner, I want every endpoint and page protected by server-side policies, so that the UI can never be the only line of defence.
- AC1 A capability matrix (`docs/ROLE-MATRIX.md`, generated from code) lists every capability × role and matches brief §3 allowed/prohibited columns.
- AC2 Every Razor page and endpoint has an explicit policy; a test fails if any endpoint lacks one (reflection/route-table test). The anonymous allowlist is exactly: `/` (landing), login/logout and access-denied pages, `/health*`, `/webhooks/delivery` (HMAC-verified) and static assets; the test fails on any other anonymous route.
- AC3 Prohibited capabilities from brief §3 each have a crafted-request test returning 403 (e.g., Reviewer approves, Scheduling overrides hard closure, Admin approves, Auditor mutates, Attendant edits approval, Intake issues permit).
- AC4 API calls without a session return 401 JSON, never login HTML.

#### E1-03 Record-level scoping · M1
As an applicant's coordinator, I want only authorised people to see a case, so that private details do not leak.
- AC1 Scope filters are applied **before** search, counts, paging, export, dashboards and attachment links: Intake sees own cases; Attendant sees assigned events only (A-22); Auditor sees all metadata with masked contacts (A-21); Admin sees no case content (A-05).
- AC2 Direct URL to an out-of-scope case returns 404 without details (A-06); tests cover cases, attachments, permits, incidents and information requests.
- AC3 Role/assignment re-checked from the database on every command (not trusted from a long-lived Blazor circuit); test removes an attendant assignment while their page is open.

#### E1-04 Role-aware navigation · M1
- AC1 Navigation shows only permitted pages; each of the 26 pages is reachable from navigation for at least one role.
- AC2 Direct navigation to a forbidden page shows the 403 page and logs a denied event.
- AC3 Actions a user cannot perform are hidden or disabled with a reason; the server still enforces them.

#### E1-05 Administration: Users & Rules (page 18) · M2 · FR-14
As a System Admin, I want to manage accounts, roles, business rules, templates and view the audit log, so that the system is configurable without code.
- AC1 Users: list/search, create, activate/deactivate (no delete), assign/remove roles with reason; admin cannot remove their own Admin role; changes audited.
- AC2 Rules: edit RuleSettings (durations, buffers defaults, check-in window, info-request due days, link TTL, file limits, registry override policy, weather cache minutes) with type/range validation and audit; changes take effect without restart.
- AC3 Notification templates: edit subject/body with placeholder validation and preview using sample data.
- AC4 Audit viewer: filter by date, actor, entity, action, correlation ID; read-only; Auditor can view it; CSV export of the filtered audit (Auditor).

### E2 — Case core: model, workflow, audit and concurrency (FR-18)

#### E2-01 Data model and migrations · M1
As the team, we want a complete relational model, so that every page binds to real persisted data.
- AC1 Entities from brief §4 (Application, Applicant, Event, Venue, VenueBlackout, Resource, ResourceAllocation, Reservation, EligibilityReview, InformationRequest, ApprovalDecision, Permit, PermitVersion, Notification, CheckIn, Incident, Attachment, Comment, AuditEvent, User, Role) plus Cemetery, RuleSetting, NotificationTemplate, SavedView, IntegrationAttempt, RegistryVerification, ExternalCalendarEvent, CalendarSyncRun, ReconciliationItem, WebhookReceipt, WeatherSnapshot, GeocodeCandidate, ImportBatch, ImportRowError, PartnerEvent, ReadinessChecklistItem, AttendantAssignment, OutboxMessage, JobLock/JobRun, DataProtectionKey.
- AC2 Foreign keys, indexes for every filter/sort used by pages, `timestamptz` UTC audit columns (`CreatedAt/By`, `UpdatedAt/By`), row versions; committed EF migrations; migration applied only by the `migrate` command, never at web startup.
- AC3 Mermaid ERD in `docs/ERD.md` generated or checked against the model by a test (fails when an entity is missing from the diagram).

#### E2-02 Reference numbers · M1
- AC1 `APP-YYYY-NNNNN` and `PER-YYYY-NNNNN` (A-07) are unique under 50 parallel creations (integration test), unique index enforced, year rollover tested, exhaustion returns a clear error.

#### E2-03 Workflow engine · M1
As a case worker, I want only legal status changes, so that every case follows the approved process.
- AC1 Transition table T1–T14 (section 3.4) in the domain; each transition has its own command endpoint and application method; guards return stable error codes and human messages.
- AC2 Tests: every allowed transition with its guard passing and failing; at least six forbidden moves (e.g., Draft→Approved, Submitted→Permitted, Closed→Submitted, Rejected→Eligible, Checked In→Cancelled, Permitted→Closed) return 422.
- AC3 Wrong role returns 403 before transition validation (section 3.5 order).
- AC4 Mermaid state diagram in `docs/WORKFLOW.md` generated from the transition table (test keeps them in sync).

#### E2-04 Append-only audit and no hard deletion · M1
- AC1 Every status change writes an AuditEvent in the same transaction (test forces a failure after the status update and proves neither row is committed).
- AC2 All mutations audited with actor, UTC time, reason and before/after summary; downloads/link issuance audited.
- AC3 Deletes of any business entity and updates of AuditEvent are rejected by the persistence layer (tested); no delete endpoints exist (route test).

#### E2-05 Optimistic concurrency and conflict UI · M1
As a user, I want to know when someone changed the case while I was editing, so that I never silently overwrite their work.
- AC1 Every write requires the row version; stale writes return 409 with the latest values the caller may see and a reload link.
- AC2 Child writes (comments, attachments, allocations, information requests) bump the parent version.
- AC3 UI shows a conflict panel (your value vs current value per changed field) with Reload and Re-apply-my-changes options; announced via live region; browser test with two sessions.

#### E2-06 Business rule settings · M1
- AC1 RuleSettings seeded with brief values (30–300 min, capacity checks, 5 MiB, 5-minute webhook tolerance, 15-minute weather cache) and A-08…A-16 defaults; read through a cached service invalidated on change.

#### E2-07 Cancellation · M1 · brief §5
As an Operations Manager, I want to cancel a case at any permitted stage with a reason, so that venues and resources are freed.
- AC1 T14 from each of the six permitted states; reason required; reservations released, allocations released, current permit version invalidated (check-in then rejects it), notification queued.
- AC2 Cancel from Draft, Needs Information, Rejected, Checked In, Event Completed or Closed returns 422.

### E3 — Integration platform, mocks and object storage (brief §7)

#### E3-01 Mock services host · M0 (registry) / M2 (all)
As a tester, I want six independently runnable mock services with switchable scenarios, so that every integration and failure mode can be demonstrated without real accounts.
- AC1 `EventPermits.Mocks` runs one service per process (`--service registry|calendar|notify|weather|geocode|partner-feed`) with the contracts in section 4.5; each appears as its own Compose service (and can later run as its own cloud service by configuration).
- AC2 Scenario engine: modes from section 4.5, latency injection, admin API with its own key, call counters per endpoint, reset; deterministic seed data from committed JSON.
- AC3 Each mock exposes `/health` and an OpenAPI document; credentials (API key, client secret, HMAC secret, admin key) come from environment variables only.
- AC4 Every mode the brief lists (§7.4: success, slow, 401, 404, 409, 429, 500, callback duplicates, outage) is available on at least one mock and selectable both from the Scenario Panel and through `Mock__Mode` at startup; `offline` aborts connections so the client sees a transport failure, not an HTTP status.

#### E3-02 Outbound integration pipeline · M1
As an operator, I want every external call logged, sanitised and resilient, so that failures are visible and recoverable.
- AC1 Typed clients per port with the handler chain in section 4.5; timeouts per integration; retries only for transient classes (5xx, 408, 429, network) with bounded exponential backoff honouring `Retry-After`; **404 and 4xx never retried**; circuit breaker opens after configurable failures.
- AC2 IntegrationAttempt recorded for every attempt (including retries) with sanitised data; redaction unit tests prove keys/tokens never appear in logs, attempts or UI.
- AC3 Unit tests for retry classification and backoff bounds; contract tests run each adapter against its mock in-process.

#### E3-03 External Integration Hub (page 19) · M2
As a System Admin, I want one page showing every integration's health and recent activity, so that I can diagnose problems quickly.
- AC1 Seven cards (registry, calendar, notification gateway, object storage, weather, partner feed, geocoding) with Healthy/Degraded/Offline state, request totals (24 h), last successful call, recent errors, masked configuration (base URL shown, secrets masked).
- AC2 "Test connection" per integration performs a real call and shows the result and timing.
- AC3 Recent attempts table links to `/integrations/attempts/{id}` (request ID, correlation ID, sanitised status code, timing, retry attempt, linked case).
- AC4 Scenario Panel (Admin only): set mode per mock, view mock call counts, reset. Ops Manager sees the hub read-only without the panel.

#### E3-04 Object storage service · M1 · FR-15
As a user, I want files stored privately and checked, so that unsafe or oversized files never enter the system.
- AC1 `IObjectStore` over AWSSDK.S3; MinIO locally (path style), S3-compatible by configuration (deployability contract); bucket private; random object keys (`attachments/{guid}`); SHA-256 checksum, size, verified content type, uploader, original sanitised name stored on Attachment.
- AC2 Upload accepts PDF/PNG/JPG only; extension, declared MIME and magic bytes must agree; max 5 MiB (exact boundary tested: 5,242,880 accepted, +1 rejected); empty files rejected; streaming limits configured in Kestrel and Blazor.
- AC3 Download follows A-16: role/scope check → signed app link (TTL from RuleSetting, audited) → on open, re-check + audit → 302 to a 60-second pre-signed storage URL. Tests: expired app link → friendly 403 "expired" page with Regenerate; tampered signature → 403; link used by a different user → 403; user who lost access after issue → 403/404; Auditor never gets a link (metadata only); direct unsigned bucket URL → denied by storage.
- AC4 A failed DB commit after upload leaves no referenced object; orphan cleanup job removes unreferenced staged objects after 24 h.

#### E3-05 Document Vault (page 23) · M2
As a case worker, I want all attachments I may see in one place, so that I can find, preview and audit documents.
- AC1 Server-paged list with filters (case, type, uploader, date), metadata (size, checksum, type, uploader, uploaded at), preview for images and PDFs via a short-lived link, download link with visible expiry countdown, Regenerate link.
- AC2 Per-file audit history (uploaded, link issued, link expired attempt, denied attempts).
- AC3 Upload to a case the user may modify; Auditor metadata only; integration test proves an unauthorised role cannot obtain a link through page or API.

#### E3-06 Background job host with locking · M1
- AC1 `worker` process runs scheduled jobs (notification dispatch, outbox commands such as calendar pushes and their retries, delivery status poll, calendar sync, partner import, overdue marker for information requests and incidents, orphan cleanup) with PostgreSQL advisory locks so two workers never run the same job; job runs recorded (start, end, outcome).
- AC2 Jobs can be triggered manually from their pages through the server (not by calling the worker directly).

### E4 — Intake and case management (FR-02, FR-03)

#### E4-01 New Application Wizard (page 03) · M1
As an Intake Coordinator, I want a four-step wizard that saves as I go, so that I can complete applications across sessions.
- AC1 Steps: Applicant (name, contact email/phone, organisation, registry reference), Event (type/category, cemetery, preferred venue, date, start time, duration, attendee count, description), Facilities (resources requested with quantities, special needs), Supporting documents (uploads via E3-04, required document types per event category).
- AC2 Client and server validation with the same rules; errors linked to fields; focus moves to the first invalid field; step indicator shows completion state.
- AC3 Autosave: draft created once (idempotent request key) on first valid field, then saved on step change and after 2 s idle; "Saved at hh:mm" status announced politely; resume from Register ("My drafts") and from `/applications/new/{id}/step/{n}`.
- AC4 Submit (T1) prevents double submission (button disabled + idempotency key + row version); success lands on Application Detail with confirmation.

#### E4-02 Application Register (page 02) · M1 (basic) / M2 (complete) · FR-03
As a case worker, I want a fast searchable list, so that I can find any case I am allowed to see.
- AC1 Server paging 20 rows; server sort (reference, event date, status, updated, applicant); search (reference, applicant name, event title); filters status, cemetery/site, venue, event type, applicant, date range; total count respects scope.
- AC2 Filters, sort and page are in the URL (deep links restore them); dashboard KPI cards link here with filters applied.
- AC3 Saved views: name, save, rename, delete (soft) own views (A-23).
- AC4 CSV export of the current filtered result (role-permitted columns, masked where required, formula-injection neutralised, correct quoting).

#### E4-03 Comments · M1
- AC1 Staff with case access add comments on Application Detail; comments are immutable, audited and shown in the timeline.

#### E4-04 Application Detail (page 04) · M1 (basic) / M2 (complete)
As a case worker, I want one page with the full case, so that I can act without hunting.
- AC1 Summary header (reference, status chip, applicant, event, venue, slot, permit number/version, risk flags: overdue info request, severe weather, unreconciled calendar, open incidents).
- AC2 Sections editable only when the status and role allow (field policy table in code, tested); others read-only.
- AC3 Timeline: status changes, reviews, information requests/responses, decisions, permits, notifications, check-ins, incidents, comments, newest first.
- AC4 Action bar shows only permitted transitions with their unmet guards explained (e.g., "Approval blocked: calendar not reconciled").

### E5 — Eligibility review and information requests (FR-04)

#### E5-01 Eligibility Review Queue (page 05) · M1
As an Eligibility Reviewer, I want a queue of submitted cases with a checklist, so that I can review consistently.
- AC1 Queue of Submitted cases (oldest first, filters, paging); open a case into a review view with documents (preview links), event rules and the checklist (configurable items per event category).
- AC2 Pass (T4) requires every checklist item resolved and registry evidence Valid or a permitted override; Reject (T5) requires a reason; Request information (T2) opens E5-03.
- AC3 EligibilityReview records reviewer, checklist answers, decision, reason, UTC time; reviewer identity feeds SoD for T8/T9.

#### E5-02 Applicant Registry verification (page 20) · M2 · brief §7.1
As an Eligibility Reviewer, I want to verify the applicant reference against the registry, so that eligibility never passes without evidence.
- AC1 Verification runs on review open and on demand; stores RegistryVerification (referenceId, matchedName, eligibilityStatus, verifiedAt, attempt link) and an audit event.
- AC2 Flagged/Unknown blocks T4 unless an override permitted by A-10 is recorded with reason.
- AC3 429: honour `Retry-After` within a bounded delay, then show "verification pending" with Retry; never pass without evidence. 404: "not found" result, not retried. 500/timeout: retry transient within bounds, then show retry action.
- AC4 Page 20: lookup form (reference), match result card, verification history table, retry button, recent attempts with timings; API key never shown or logged.

#### E5-03 Information requests and inbox (page 06) · M1
As a Reviewer, I want to ask for missing items with a deadline, and as an Intake Coordinator I want to answer them, so that cases can be completed.
- AC1 Request: missing items (list), message, due date (A-13) → T2; notification queued to the coordinator.
- AC2 Inbox (page 06): open, answered, overdue tabs-as-filters (single route with query state), due date sorting, overdue flag computed by the worker and shown with text.
- AC3 Response: coordinator replies per item, may attach files, then resubmits (T3); previous requests and responses stay visible and immutable.

### E6 — Venues, scheduling and resources (FR-05, FR-06, FR-07, FR-14)

#### E6-01 Administration: Venues (page 17) · M1 (basic) / M2 (complete) · FR-14
As a System Admin, I want to manage cemeteries, venues and closures, so that scheduling uses correct data.
- AC1 Venues CRUD (no hard delete; deactivate): cemetery, code, name, address, capacity, opening hours per weekday, setup/teardown buffers, allowed event categories, coordinates (from E6-08) with verified flag.
- AC2 Blackout windows CRUD with reason (hard closures); overlapping existing reservations listed as warnings.
- AC3 Resource inventory per cemetery: resource type, total quantity, active flag; event categories managed here.

#### E6-02 Conflict engine · M1 · FR-06
As a Scheduling Officer, I want every reservation checked against all constraints, so that double bookings cannot happen.
- AC1 Pure domain service: inputs venue, slot, buffers, attendees, resources; checks opening hours, blackouts, overlaps with other reservations (buffers included on both sides), external calendar events (A-18), partner events (A-17), capacity, resource availability by site/time.
- AC2 Returns all violations (not just the first) with types and the conflicting record.
- AC3 Property-style unit tests for boundaries (touching intervals allowed, 1-minute overlap rejected, buffer overlap rejected); integration test for two concurrent reservations of the same slot (one wins, the other gets 409/422).
- AC4 Scheduling Officers cannot override hard closures (403/422 tested).

#### E6-03 Venue Availability (page 08) · M2
- AC1 Pick cemetery, venue, date → timeline of opening hours, blackouts, reservations with buffers, external and partner events.
- AC2 Propose a slot → live collision validation as you type (debounced server call) with all violations listed; "next free slots" suggestions (at least three).

#### E6-04 Event Calendar (page 07) · M2 · FR-05
- AC1 Month, week and day views of persisted reservations, external calendar events and blackouts; filters by cemetery, venue, status; keyboard navigable grid with accessible labels.
- AC2 Clicking a free slot opens a reservation proposal for an Eligible case (with case picker) prefilled with venue/time, validated by E6-02.
- AC3 Views and filters in the URL.

#### E6-05 Reservations · M1
As a Scheduling Officer, I want to reserve and adjust tentative slots, so that eligible cases move to approval.
- AC1 Create reservation (T6) only when E6-02 returns no violations; change tentative reservation (re-validated); release on cancel/reject.
- AC2 Submit for approval (T7) requires the reservation summary complete (venue, slot, resources, attendant needs) and shows it read-only.

#### E6-06 Resource Planner (page 09) · M2 · FR-07
- AC1 Per cemetery and date: inventory, allocated, remaining by time window; conflicts highlighted with text.
- AC2 Allocate/change/release quantities for a reservation; quantity cannot exceed availability for the window (server enforced, concurrent allocation test).
- AC3 Cancellation and rejection release allocations (tested).

#### E6-07 Regional Calendar Sync (page 21) · M2 / M3 · brief §7.1
As a Scheduling Officer, I want external events imported and conflicts reconciled, so that approvals respect the regional calendar.
- AC1 OAuth2 client credentials; token cached server-side and renewed before expiry (configurable skew); on 401 refresh token and replay **once**; `invalid_client` → admin-actionable error without secret leakage.
- AC2 Sync job and "Sync now": imports `GET /events` since `lastSyncCursor`, dedupes by `externalId`, updates on newer `modifiedUtc`, stores CalendarSyncRun with counts.
- AC3 Reservations are pushed on T6 and on every tentative change (A-27). A 409 creates a ReconciliationItem showing the source event, slot alternatives and history; the case cannot be approved (T8) until reconciliation succeeds; calendar offline → push queued for retry, tentative reservation kept, T8 blocked with an explanation.
- AC4 Page 21: connection status, token status (expiry time, never the token), last sync, imported events table, conflicts, reconciliation queue with actions (choose alternative slot, mark resolved with reason, retry push), history.
- AC5 Unit tests for token cache expiry and renewal; integration test for 409 and offline paths.

#### E6-08 Geolocation & Venue Map (page 26) · M2 / M3 · brief §7.1
As a System Admin, I want venue addresses geocoded and confirmed, so that maps show correct locations without inventing coordinates.
- AC1 Geocode from the venue editor and page 26; candidates with label, lat/long, confidence; a tie on top confidence requires explicit selection; coordinates are stored only after confirmation (audited).
- AC2 Geocoder offline/no match: the venue editor stays usable, address saved, coordinates flagged "unverified"; no invented GPS.
- AC3 Page 26: map of venues using a local static rendering (SVG over a local base image or Leaflet with local tiles; no internet needed) plus an accessible list with coordinates, verified state and confidence; selecting a list row highlights the marker and vice versa.

### E7 — Approval and permits (FR-08, FR-09)

#### E7-01 Approval Workspace (page 10) · M1
As an Operations Manager, I want a queue of cases ready for approval, so that I can decide with full context.
- AC1 Pending Approval list with reservation summary, review outcome, registry evidence, weather status, calendar reconciliation status.
- AC2 Approve (T8) / Reject (T9) with decision notes (reason required on reject); ApprovalDecision stores approver, decision, notes, UTC time (digital sign-off record shown on the case).
- AC3 SoD: a user who recorded an EligibilityReview on the case cannot approve or reject it (403 with explanation; dual-role seeded user demonstrates it).
- AC4 Guards: calendar reconciled (A-18); otherwise 422 with the blocking reason.

#### E7-02 Permit issuance · M1 · FR-09
As an Operations Manager, I want a permit generated when I issue it, so that the event has an official, verifiable document.
- AC1 Issue (T10) creates Permit (first time, PER number) and immutable PermitVersion (version, issuer, issued at, schedule, venue, conditions/restrictions, token hash, PDF object key, checksum).
- AC2 Server-generated PDF: permit reference and version, applicant, event, venue and local time window, conditions, issuer, QR code with the token payload and a typed short code; stored in `permits/` in object storage.
- AC3 Notification queued via the outbox in the same transaction; a gateway failure later never changes the permit (test with notify in 500 mode).
- AC4 Severe weather advisory for the event date blocks issuance until acknowledged (A-19).

#### E7-03 Permit Center (page 11) · M2
- AC1 "Ready to issue" queue of Approved cases with an Issue action (T10, also available on Application Detail); list/search permits (number, case, venue, date, status: Active/Revoked/Superseded/Cancelled), version history per permit.
- AC2 Download PDF via expiring link (E3-04); Reissue creates a new version (reason required) and supersedes the old token; Revoke (A-11) with justification.
- AC3 Auditor sees metadata and history, no PDF link.

#### E7-04 Permit token verification service · M1
- AC1 Verifies signature, permit/version currency, status and check-in window; distinct results Valid, Expired, Revoked, Superseded, Cancelled, NotFound, OutsideWindow; unit tests for each.

#### E7-05 Weather & Advisories (page 24) · M2 / M3 · brief §7.1
As an Operations Manager, I want forecasts per venue and date, so that I can react to severe weather.
- AC1 Forecast cached 15 minutes (RuleSetting) per venue+date; page shows source timestamp, retrieved time, details and Fresh/Stale label.
- AC2 Timeout/outage: show the last cached result labelled **Stale** (never fabricate); no cache → clear "unavailable" state; case pages still load.
- AC3 Severe advisory marker on the page, on Application Detail and in the Approval Workspace; Manager acknowledgement (note) recorded and audited; manual refresh button.
- AC4 Unit tests for cache expiry and fresh/stale classification.

### E8 — Event-day operations (FR-10, FR-11)

#### E8-01 Daily Event Roster (page 13) · M2
- AC1 Pick date and cemetery → events with time, venue, status, permit validity, readiness flags (resources allocated, weather ack, attendant assigned, checklist state).
- AC2 Ops Manager assigns/unassigns attendants (audited); attendants see only their events.
- AC3 Staff readiness checklist per event (configurable items) completed by the attendant.
- AC4 Printable run-of-show (print stylesheet or PDF) for the date/site.

#### E8-02 Event Check-In (page 14) · M1 · FR-10
As an Event Attendant, I want to verify a permit and record arrival and departure, so that only valid events proceed.
- AC1 Enter typed code or paste QR payload (camera scanning optional stretch); result clearly distinguishes valid / expired / revoked / superseded / cancelled / not-found / outside window, with text and icon.
- AC2 Check-in (T11) and check-out records are idempotent (repeat submit returns the existing record). Only the attendant assigned to that event may record them (brief §5); an unassigned attendant gets 403 and the Ops Manager can verify a token read-only but not record (tested).
- AC3 Complete event (T12) requires check-out and a complete readiness checklist.

#### E8-03 Incident Register (page 15) · M2 · FR-11
- AC1 Create incident against an event: category, severity (Low/Medium/High/Critical), description, photos (E3-04, images only), follow-up owner, due date.
- AC2 Corrective action, escalation (to Ops Manager, audited), resolution with notes; every change audited; overdue incidents flagged.
- AC3 Filters by severity, status, cemetery, date; Auditor read-only.

#### E8-04 Case closure · M1
- AC1 Close (T13) blocked while any High/Critical incident on the case is unresolved (422 naming the incidents); allowed after resolution.

### E9 — Notifications and messaging (FR-12, brief §6 external mock service, §7.1)

#### E9-01 Templates and outbox · M1
- AC1 Template-driven messages (email/SMS channel) for: submitted, information requested, response received, eligible/rejected, reserved, approved/rejected, permit issued/reissued/revoked, cancelled, incident escalated.
- AC2 OutboxMessage written in the business transaction; each message has an idempotency key.

#### E9-02 Gateway sender (worker) · M1
- AC1 Worker sends queued messages to `POST /messages` with `Idempotency-Key`; 202 stores `messageId`, status Sent; transient failures retried with backoff up to a limit, then Failed with reason; duplicates never sent (test: crash after send before save, then resend uses the same key and the mock returns the same messageId).
- AC2 Fallback poller uses `GET /messages/{id}` for messages without a webhook after N minutes.

#### E9-03 Delivery webhook receiver · M2 / M3
As the platform, I want signed delivery callbacks processed safely, so that statuses are trustworthy.
- AC1 `POST /webhooks/delivery`: anonymous but HMAC-verified (A-20) with constant-time comparison; invalid signature → 401; timestamp outside 5 minutes → 401; both logged without the secret.
- AC2 Duplicate `eventId` → 200 with no second effect; out-of-order events never regress Delivered; every receipt stored immutably (WebhookReceipt with verification result).
- AC3 Exempt from antiforgery only for this endpoint; request body size limited.
- AC4 Unit tests for HMAC and tolerance; integration tests for forgery, replay, duplicate and out-of-order.

#### E9-04 Notification Center (page 12) · M2
- AC1 Outbox list with status (Queued, Sent, Delivered, Failed), channel, recipient (masked for Auditor), template, case link, attempts, last error.
- AC2 Retry failed message (same idempotency key semantics, audited); send manual notification from a template to a case contact.
- AC3 Message detail with status transition history and audit trail.

#### E9-05 Messaging Delivery Monitor (page 22) · M2 / M3
- AC1 Queue depth and age, delivery rates, failed deliveries list, webhook receipts (accepted/rejected with reason), correlation IDs linking message → attempts → receipts.
- AC2 Safe manual retry (only Failed; confirmation; no duplicate send).

### E10 — Partner data import (brief §7, §7.1)

#### E10-01 Ingestion service · M2 / M3
As a System Admin, I want partner CSV files ingested safely and repeatably, so that partner events appear without duplicates.
- AC1 Sources: drop prefix `partner-drop/` (OD-5) scanned on schedule (nightly default, RuleSetting) and on demand; manual upload of a CSV on page 25.
- AC2 Schema validation of header and each row (required fields, ISO-8601 UTC times, end after start, known site code, attendee count ≥ 1); partial-success policy (A-17).
- AC3 ImportBatch stores batch ID, source, file name, checksum, row counts (read, inserted, updated, unchanged, rejected), started/completed, mapping diagnostics; ImportRowError per bad row.
- AC4 Re-import of the same file is a no-op; Replay reprocesses on request; tests for malformed rows, duplicate file and updated rows.

#### E10-02 Data Import & Reconciliation (page 25) · M2
- AC1 Upload or "Fetch from drop folder"; preview of column mapping and the first 20 rows with per-row validation before commit.
- AC2 Batches list (status, counts, checksum, last completed sync), batch detail with row errors, downloadable error report CSV, Replay button, imported partner events list with conflicts against reservations.

#### E10-03 Partner feed mock scenarios · M3
- AC1 Scenario Panel can drop clean, bad-rows, duplicate, updated and malformed-header files; call/drop counts visible.

#### E10-04 (Stretch) Real SFTP source · after M4
- AC1 Optional Compose SFTP container and SSH.NET adapter behind the same `IPartnerFeedSource`; not deployed to AWS.

### E11 — Dashboard and reporting (FR-13)

#### E11-01 Operations Dashboard (page 01) · M2
- AC1 KPI cards computed from the database for the caller's scope (open cases by status, today's events, pending approvals, overdue information requests, open High/Critical incidents, failed notifications, degraded integrations, upcoming deadlines).
- AC2 Today's events, pending actions for the user's roles, recent incidents, deadlines lists.
- AC3 Every card and list item links to the filtered page (deep link) that shows the same count (test compares numbers).

#### E11-02 Reports & Exports (page 16) · M2
- AC1 Date-range and cemetery filters; charts: application throughput by status over time, approval time (median, p90), venue utilisation %, incidents by severity — calculated by SQL queries, never hard-coded.
- AC2 Clicking a chart segment drills down to the matching Register/Incident list.
- AC3 Accessible charts: data table alternative and text summary for each chart.
- AC4 Filtered CSV exports for each report; Auditor audit CSV export (E1-05).

### E12 — Seed data and demonstration (brief §9)

#### E12-01 Deterministic seed · M1 (core) / M4 (full counts)
- AC1 One idempotent command seeds: 120 applications spanning all states, 3 cemeteries, 12 venues, 8 resource types, 20 blackout windows, 50 reservations (incl. test collisions), 30 clarification requests, 40 review decisions, 20 issued permits, 15 check-ins, 12 incidents, 25 notifications, 30 attachments (generated valid PDF/PNG/JPG), 35 mock external responses (IntegrationAttempts), 12 webhook events (successful and failed), 3 partner import batches, 8+ users.
- AC2 History is coherent with workflow (each seeded state has the audit trail that would have produced it); seeded data exercises scenarios: SoD, overdue requests, severe weather, unreconciled calendar conflict, unresolved critical incident blocking closure, expired link, revoked permit.
- AC3 Re-running seed creates nothing new (test asserts 0 created); destructive reset is a separate explicit command.
- AC4 All names, addresses and references are clearly fictional; a test asserts counts match the brief.
- AC5 Dates follow A-29 (relative to the stored anchor date), so a fresh seed always has events today for the roster and check-in demo.

#### E12-02 Mock seed data · M2
- AC1 Registry references for seeded applicants (Valid/Flagged/Unknown mix and unknown references), 40 external calendar events (some colliding), weather per venue (one Severe), geocode candidates (including a tie and a no-match), three partner CSV files (one with bad rows).
- AC2 Shared reference data (venue codes, applicant references, external event IDs) lives in committed JSON copied into both images; a test fails if the app seed and the mock data disagree.

#### E12-03 Required live journey as an automated test · M1 (happy path) / M4 (complete)
- AC1 Browser test reproduces brief §9 end to end with the seeded roles: create multi-step application → reviewer requests information → coordinator responds → reviewer passes → scheduling officer hits a collision and picks a free slot → allocates resources → manager approves → downloads PDF permit → attendant checks in → logs an incident → manager resolves and closes → auditor exports report.
- AC2 Separate tests demonstrate unauthorised 403, invalid transition 422, stale update 409 and notification retry.

#### E12-04 Integration demo extension as automated tests · M3
- AC1 One test per brief §8 demo item: registry verification with API key; OAuth calendar conflict resolution; notification + signed webhook; PDF via expiring link; stale weather while offline; CSV with bad rows; geocode candidate selection; Integration Hub correlated failure logs.
- AC2 Each test asserts mock call counts increased (proves real requests).

#### E12-05 Demo runbook · M4
- AC1 `docs/DEMO-RUNBOOK.md`: timed script for the journey and the integration extension, scenario switches to use, accounts, reset steps, fallback plan if a step fails.

### E13 — Quality, security and accessibility hardening (FR-15, FR-17)

#### E13-01 Failure matrix suite (brief §7.3) · M3
- AC1 One automated test per row: registry 429; OAuth token expiry and invalid_client; calendar 409; webhook duplicates/forgery; storage link expiry; weather timeout; CSV bad rows; geocoder ambiguous/offline. Each asserts the user-visible recovery and a traceable log/attempt with correlation ID.
- AC2 Brief §8 checklist made explicit in the traceability matrix: a contract test per adapter (6); unit tests for credential/token handling, deduplication, retry classification, HMAC and cache expiration; integration tests for storage access, failed webhook verification, calendar 409 and malformed CSV; a browser test for **each** of pages 19–26 that shows live mock results (asserting call counts).

#### E13-02 Security hardening · M3
- AC1 Security headers (CSP without inline script where feasible, `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, HSTS in TLS mode); antiforgery tests for HTTP and Blazor paths; output encoding; request size limits.
- AC2 Gitleaks over history and working tree; sensitive-pattern check tuned for fictional data; `dotnet list package --vulnerable` clean; Trivy image scan with no fixable CRITICAL.
- AC3 Secrets never in logs, attempts, health or errors (log capture test across the suite).

#### E13-03 Accessibility (WCAG 2.1 AA target) · M2 / M4
- AC1 Axe scan (wcag2a, wcag2aa, wcag21a, wcag21aa) on all 26 pages, the landing and login pages, and key states (validation error, conflict panel, empty, error): 0 violations.
- AC2 Keyboard-only journey test for the wizard, calendar, check-in and approval; visible focus; live-region announcements asserted.
- AC3 `docs/ACCESSIBILITY-CHECKLIST.md` manual review (screen reader, zoom 200 %, contrast tool results) kept separate from automated evidence.

#### E13-04 Responsive and polish review · M4
- AC1 Screenshots at 390 px and 1280 px for all pages reviewed; no horizontal scroll on phones; no placeholder text, lorem ipsum, dead buttons or decorative-only charts (automated scan for "TODO", "lorem", `href="#"`, buttons without handlers).

#### E13-05 Clean-machine verification · M4
- AC1 Fresh clone in a new directory: README-only steps for install, migrate, seed, run, test succeed on macOS; Windows steps documented (verified if a Windows machine is available, otherwise recorded as not executed).

### E16 — Documentation and submission package (brief §10)

#### E16-01 README · M0 (skeleton) / M4 (final)
- AC1 Purpose, architecture summary and diagram, prerequisites in install order (Windows 11 and macOS), exact versions, clean-machine install → migrate → seed → run → test commands, demo accounts table (user name, role(s), the local demo password from OD-2, with a note that it works only on local runs; AWS passwords come from Secrets Manager), URLs, health and logs, scenario switching, troubleshooting, known limitations. Leave a clearly marked "Deploying to AWS" heading with one line pointing to `docs/plan/deploy-infra-only/`; the deploy track fills it.

#### E16-02 Architecture, ERD and workflow docs · M1 / M4
- AC1 `docs/ARCHITECTURE.md` (C4-style context/container diagrams in Mermaid for the local runtime; the deploy track adds the AWS deployment view), `docs/ERD.md` (E2-01), `docs/WORKFLOW.md` (E2-03), `docs/INTEGRATIONS.md` (contracts, failure behaviours, configuration).

#### E16-03 API specification · M2
- AC1 OpenAPI documents for the app API and each mock exported to `docs/api/` by a build step; a test fails if the committed spec is out of date.

#### E16-04 Test report and traceability · M4
- AC1 Final test report (counts per suite, environment, commit) in `docs/test-report/`; traceability matrix complete with code locations, named tests and evidence for every row in section 10.

#### E16-05 Screenshots of all 26 pages · M4
- AC1 `tools/Dev screenshots` (Playwright) captures every page (desktop and phone) with seeded data into `docs/screenshots/`, including integration pages 19–26, the attempt detail, and the landing and login pages.

#### E16-06 Implementation log and known limitations · continuous / M4
- AC1 `docs/IMPLEMENTATION-LOG.md` (dated entries per story: what, why, evidence links); `docs/KNOWN-LIMITATIONS.md` (honest list: e.g., HTTP on loopback locally, signature checks are not antivirus, SFTP simulated, Windows not verified if so).

---

## 8. Seed dataset (brief §9)

| Item | Count | Notes |
| --- | --- | --- |
| Applications | 120 | every state represented, including Rejected and Cancelled with history |
| Cemeteries / venues | 3 / 12 | fictional names, addresses and time zones |
| Resource types | 8 | chairs, tents, parking attendants, AV kits, + 4 more |
| Blackout windows | 20 | some overlapping proposed slots |
| Reservations | 50 | includes deliberate collisions for demos |
| Clarification requests | 30 | open, answered, overdue |
| Review decisions | 40 | pass, reject, overrides |
| Issued permits | 20 | incl. a reissued version and a revoked one |
| Check-ins | 15 | incl. completed events |
| Incidents | 12 | one unresolved Critical blocking a closure |
| Notifications | 25 | sent, delivered, failed |
| Attachments | 30 | valid synthetic PDF/PNG/JPG |
| Mock external responses | 35 | IntegrationAttempt history across all integrations |
| Webhook events | 12 | accepted, duplicate, rejected signature |
| Partner import batches | 3 | one with row errors |
| Users | 8+ | one per role, two attendants, one dual-role |

---

## 9. Demo scenarios mapped to stories

| Demo item (brief §8, §9) | Proven by |
| --- | --- |
| Full multi-role journey | E12-03 |
| Unauthorised 403 / invalid 422 / stale 409 / notification retry | E12-03 AC2, E1-02, E2-03, E2-05, E9-04 |
| Registry verification with API key | E5-02, E12-04 |
| Resolve OAuth calendar conflict | E6-07, E12-04 |
| Notification + signed webhook | E9-02, E9-03, E12-04 |
| PDF via expiring object-storage link | E3-04, E7-03, E12-04 |
| Cached weather while forecast offline | E7-05, E12-04 |
| Partner CSV with bad rows | E10-01, E10-02, E12-04 |
| Geocode candidate selection | E6-08, E12-04 |
| Integration Hub correlated failure logs | E3-03, E12-04 |

---

## 10. Traceability index (brief → stories)

| Brief reference | Stories |
| --- | --- |
| §2 pages 01–26 | see section 3.2 |
| §3 roles, 403, no leaks, seeded accounts | E1-01…E1-04 |
| §4 entities, FKs, migrations, UTC, row versioning, numbering, validation rules | E2-01, E2-02, E2-05, E4-01, E6-02, E6-06 |
| §5 transitions, 422, 409, same-transaction audit, visible history | E2-03, E2-04, E2-05, E2-07 |
| FR-01 Authentication | E1-01, E1-04, E0-09 (no credentials on the landing page) |
| FR-02 Applications | E4-01 |
| FR-03 Register | E4-02 |
| FR-04 Reviews | E5-01, E5-03 |
| FR-05 Calendar | E6-04 |
| FR-06 Conflict engine | E6-02, E6-03 |
| FR-07 Resources | E6-06 |
| FR-08 Approvals | E7-01 |
| FR-09 Permit PDF | E7-02, E7-03 |
| FR-10 Check-in | E7-04, E8-02 |
| FR-11 Incidents | E8-03 |
| FR-12 Notifications | E9-01…E9-04 |
| FR-13 Reports | E11-01, E11-02 |
| FR-14 Administration | E1-05, E6-01 |
| FR-15 Files and security | E3-04, E3-05, E13-02 |
| FR-16 Observability | E0-05, E3-02 |
| FR-17 Tests and accessibility | E0-06, E13-03, every story DoD |
| FR-18 Concurrency and audit | E2-04, E2-05 |
| §6 Notification Gateway mock | E3-01, E9-02 |
| §7 seven integration patterns | E5-02, E6-07, E9-03, E3-04, E7-05, E10-01, E6-08 |
| §7.2 hub, pages 19–26, attempt detail | E3-03, E5-02, E6-07, E9-05, E3-05, E7-05, E10-02, E6-08 |
| §7.3 failure matrix | E13-01 |
| §7.4 security, local environment, scenario switches | E3-01, E3-02, E3-06, E13-02, E0-03 |
| §8 integration acceptance tests | E3-02 AC3, E13-01, E12-04, E13-03 |
| §9 seed and live journey | E12-01…E12-05 |
| §10 technical delivery, acceptance and submission | E0-02, E0-03, E13-04, E13-05, E16-* |
| Deployability contract (user requirement: deploy to AWS later) | E0-08 |
| Public landing page (user requirement) | E0-09 |

---

## 11. Local release checklist (definition of done for this track)

1. All 26 pages reachable from navigation and fully functional; browser test per page; axe clean.
2. Full journey and all demo items pass as automated tests on the local Compose stack.
3. Failure matrix suite passes; mock call counts recorded.
4. Seed matches section 8; re-seed creates nothing.
5. Clean-clone install → migrate → seed → run → test verified from README alone (macOS verified; Windows verified or recorded as not executed).
6. No secrets in Git (Gitleaks clean, with only the OD-2 demo password allowlisted and justified), images or logs; image scan has no fixable CRITICAL.
10. The landing page (E0-09) explains the app, links to sign-in and shows no credentials.
7. Docs present: README (accounts, architecture, setup), ERD, workflow diagram, API spec, configuration reference, test report, screenshots of all 26 pages, implementation log, known limitations, assumptions, decisions, traceability, accessibility checklist, demo runbook.
8. Deployability contract (E0-08) passes on the final commit.
9. STATUS.md separates passed, failed, not executed and blocked items, with exact commands.

## 12. Risks

| Risk | Mitigation |
| --- | --- |
| Scope (26 pages, 7 integrations) overwhelms delivery | Milestones deliver a working journey first (M1); shared components (E0-04) and the integration pipeline (E3-02) remove repetition; never leave a page half-built across milestones |
| Pages become shallow "forms and cards" (brief pass/fail rule) | DoD requires real actions and a browser test per page; E13-04 automated placeholder scan |
| Integration code duplicated per service | One adapter pipeline and one mock host with a shared scenario engine |
| App only works in Compose (hard-coded hosts, file secrets, local disk) and needs rework to deploy | Deployability contract E0-08, checked in M0 and kept true by the Definition of Done |
| MinIO image availability changed | E0-02 check with documented fallback |
| Large Compose stack is slow on laptops | Mock services are small; 0.25 CPU limits; optional `debug` profile only when needed |
| Licence of PDF library | Default MIT library; check recorded in DEPENDENCIES.md |

## 13. Change log

| Date | Change | By |
| --- | --- | --- |
| 2026-10-15 | User decisions: fixed local demo password `EventPermits!Demo2026` in README (OD-2, A-02, guarded by `Seed:AllowDemoPassword`); public landing page at `/` (new E0-09). `credentials` runner command removed | user + Claude |
| 2026-10-15 | Review fixes before the build: every UI command also an API endpoint (crafted-request tests cover the UI's code path); MinIO least-privilege users; Notification vs OutboxMessage roles; attempt request/response excerpts and the §7.2 rule for pages 19–26; token expiry semantics (A-12); relative seed timeline (A-29, agent range now A-30…A-59); T9 releases holds; test cadence in DoD; `build`/`migrate` commands; UI design document; isolated test stack; T10 issue queue on page 11 | user + Claude |
| 2026-10-15 | App track split from `docs/plan/IMPLEMENTATION-PLAN.md` (same story IDs). AWS stories (E0-07 walking-skeleton deploy, E14, E15) moved to the deploy track; E0-07 is therefore absent here; new E0-08 deployability contract; local milestones; assumption range A-29…A-59 | user + Claude |
