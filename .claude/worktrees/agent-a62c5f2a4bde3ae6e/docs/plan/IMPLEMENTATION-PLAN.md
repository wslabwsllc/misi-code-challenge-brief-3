# Memorial Event Operations & Permit Case Management — delivery plan (Brief 3)

Status: **proposed plan, nothing implemented.** Written 2026-10-15. The implementing agent records progress in `docs/plan/STATUS.md`, never in this file. Changes to this plan are made deliberately (by the user, or by the agent after the user agrees) and noted in the change log at the end.

This plan builds the application described in `docs/VA_MISI_Technical_Challenge_3 1.pdf` (the **brief**) in .NET, and deploys it to AWS with Terraform and Azure DevOps Pipelines. It is organised as **epics → user stories → acceptance criteria**, ordered into **milestones** so that a working, deployed system exists from the first milestone and grows from there.

| File | Audience | Purpose |
| --- | --- | --- |
| `CLAUDE.md` (repository root) | Agent, every session | Stable rules: source hierarchy, non-negotiables, evidence rules |
| `docs/plan/IMPLEMENTATION-PLAN.md` (this file) | Agent and humans | Scope, architecture, decisions, assumptions, epics and stories |
| `docs/plan/AI-IMPLEMENTATION-PROMPT.md` | Agent | The prompt that starts or resumes the work |
| `docs/plan/STATUS.md` | Agent and humans | Created by the agent in E0-01; the live progress record |
| `docs/plan/app-only/*`, `docs/plan/deploy-infra-only/*` | Agents | The same work split into an app track and a deploy track (see `CLAUDE.md` "Tracks"); use either the combined track or the split tracks, never both |
| `docs/aws/AZURE-DEVOPS-SETUP-GUIDE.md` | Human | Created by the agent in E15-01; click-by-click steps only a person can do |

---

## 1. What is different from the challenge-2 approach

The challenge-2 repository (`misi-code-challenge-wed`, same Azure DevOps project) is the reference implementation for patterns. These lessons drive this plan:

| Challenge 2 | This plan |
| --- | --- |
| Separate app plan (day-based) and AWS plan (story-based) written at different times | One plan, one backlog, one status file. App and platform stories live in the same milestones |
| AWS added after the app was finished; the app had to be retrofitted (forwarded headers, secrets from env vars, remote smoke) | Cloud parity by design from the first commit: config-gated proxy handling, secrets from env vars, S3-compatible storage, Data Protection keys in the database |
| Deployment written but never executed end to end (no service connection, no pipeline run) | **Walking skeleton (M0)**: a login page plus health checks is deployed to AWS dev through the real pipelines before feature work starts |
| Files on EFS; keys on EFS | Files in object storage (MinIO locally, S3 in AWS) through one `IObjectStore` API; Data Protection keys in PostgreSQL. **No EFS** |
| SQLite fallback doubled migrations and concurrency work | PostgreSQL only (the brief asks for PostgreSQL in Compose). Testcontainers for tests |
| Browser suite skipped in CI | Full browser + axe suite runs in PR validation against the Compose stack and publishes a test report (a brief deliverable) |
| Screenshots, ERD and API spec produced by hand | Generated: Playwright screenshots of all 26 pages, OpenAPI export, Mermaid ERD and state diagram kept next to code |
| Requirements spread over prompt, plan and CLAUDE.md | Each brief rule maps to a story ID (section 10 traceability index) |

Reuse from challenge 2 (copy, then adapt; never reference it at runtime): `infra/modules/*`, `infra/scripts/run-one-off-task.sh`, `infra/scripts/check-guards.sh`, `infra/scripts/smoke-http*.sh`, `pipelines/templates/*`, the `tools/Dev` runner pattern, the `probe` health command pattern, and the Azure DevOps setup guide structure. Re-verify every version and every argument; challenge-2 Terraform was validated but **never applied**.

---

## 2. Decisions to confirm (defaults apply until the user says otherwise)

| ID | Question | Default (recommended) | Why |
| --- | --- | --- | --- |
| OD-1 | .NET version | **.NET 10 LTS — decided by the user 2026-10-15** (brief prefers .NET 8) | .NET 8 support ends 2026-11-10, weeks from now; .NET 10 is supported to 2028-11-14. Same choice as challenge 2. The brief allows a different stack if documented |
| OD-2 | Demo credentials | **Fixed local demo password — decided by the user 2026-10-15.** Every seeded account uses the same documented password `EventPermits!Demo2026` on **local runs only**. The README lists each demo user name, its role(s) and this password (brief FR-01: "seeded demo credentials in README only"); the landing and login pages never show it. The seeder accepts it only when `Seed:AllowDemoPassword=true`, which only `compose.yml` and the local test stack set. Any other environment (AWS) must supply per-user passwords through `Seed:CredentialsJson` and refuses the demo password | Simplest for evaluators. It is a deliberate, documented exception to "no secrets in Git": the accounts are fictional, local and loopback-only, and Gitleaks allowlists this one value with the reason |
| OD-3 | Public access in AWS | **HTTP on the ALB DNS name** when no domain is given, as in challenge 2. HTTPS mode is fully implemented and switched on by setting `base_domain`; the app is then served **only** at `https://dev-brief3.<base_domain>` (host name rule decided by the user 2026-10-15, see section 4.6) | No domain is available today. The brief says "HTTPS where configured". All data is fictional. The `-brief3` host label keeps this app apart from challenge 2 under the same domain |
| OD-4 | Isolation from challenge 2 in the shared AWS account and Azure DevOps project | **Reuse only the Azure DevOps OIDC provider**; everything else is new: state bucket `misi-event-permits-tf-state`, roles `misi3-ado-plan` / `misi3-ado-deploy`, service connections `aws-misi3-plan` / `aws-misi3-deploy`, resource prefix `misi3-dev-`, secrets under `misi3/dev/`, VPC `10.41.0.0/16` | One OIDC provider per issuer URL per account (already exists). Separate state bucket keeps challenge-2 roles (which can read `misi/*` and their own state keys) away from this stack's state, which holds secrets |
| OD-5 | "Batch CSV / SFTP" pattern | **Drop-folder simulation in object storage** (`partner-drop/` prefix in MinIO locally, S3 in AWS), fed by the Partner Feed mock and documented as an SFTP simulation. A real SFTP adapter (Compose SFTP container + SSH.NET) is a stretch story (E10-04) | The brief explicitly allows "an equivalent local mock drop directory". Avoids AWS Transfer Family (~USD 220/month) or an NLB for port 22 |
| OD-6 | UI technology | **Blazor Web App, Interactive Server** (Identity pages static SSR), like challenge 2 | Team already knows it; the brief lists Blazor. Needs ALB sticky sessions (solved in challenge 2) |

The agent records each confirmed or defaulted decision in `docs/TECHNICAL-DECISIONS.md` in E0-01 and does not reopen it without a new instruction.

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
| T9 | Pending Approval | Rejected | Operations Manager | Reason required; SoD as T8 |
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
infra/                            Terraform (manual/, bootstrap/, modules/, envs/dev/, scripts/)
pipelines/                        Azure DevOps YAML (pr-validation, deploy, destroy, templates/)
docs/                             brief, plan, architecture, ERD, workflow, API spec, test report, screenshots, logs
Dockerfile                        web image (web, worker, migrate/seed, probe)
src/EventPermits.Mocks/Dockerfile mocks image
compose.yml
```

Rules: one server-side application service path for UI and HTTP endpoints; no generic repository over EF; no mediator framework unless a concrete need appears; `IDbContextFactory` for Blazor circuits; Identity cookie issuance only through HTTP Identity pages.

### 4.2 Runtime topology and cloud mapping

| Concern | Local (Docker Compose) | AWS dev (ECS Fargate) |
| --- | --- | --- |
| Web UI + API | `web` (port 5280 on 127.0.0.1) | ECS service `web` ×2 behind ALB, sticky sessions (`lb_cookie`) |
| Background jobs (outbox sender, calendar sync, CSV import schedule, overdue marker, status poller) | `worker` (same image, command `worker`) | ECS service `worker` ×1 |
| Migrations + seed | one-shot `migrate` service | one-off Fargate task run by Terraform before every web rollout |
| Database | `db` PostgreSQL 18 | RDS PostgreSQL 18, `rds.force_ssl=1`, client `SSL Mode=VerifyFull` with the RDS CA bundle baked into the image (A-15) |
| Object storage (attachments, permits, partner drop) | `minio` + `minio-init` (bucket, policy) | S3 bucket `misi3-dev-files-<suffix>`, private, SSE-S3, TLS-only, Terraform-owned (`force_destroy`) |
| Data Protection keys | PostgreSQL table (EF) | same (no EFS) |
| Six mock services | `mock-registry`, `mock-calendar`, `mock-notify`, `mock-weather`, `mock-geocode`, `mock-partner-feed` | six ECS services (desired 1, `FARGATE_SPOT` by variable) reached through Service Connect |
| Webhook callbacks | `mock-notify` → `http://web:8080/webhooks/delivery` | same name through Service Connect (`web` is client-server) |
| Secrets | generated by `init-local` into ignored `.data/secrets` | generated by Terraform into Secrets Manager `misi3/dev/*`, injected as env vars |
| Logs | JSON to stdout, `docker compose logs` | CloudWatch log groups `/misi3/dev/<service>` (Terraform-owned) |
| TLS | HTTP on loopback (A-25) | HTTP on ALB DNS name (OD-3) or HTTPS with ACM at `dev-brief3.<base_domain>` when `base_domain` is set (section 4.6) |

The application never knows which column it runs in except through configuration (`Storage:*`, `Database:*`, `Hosting:BehindTlsProxy`, `Integrations:*`). Same images run in both.

### 4.3 Technology (verify and pin exact versions in E0-02)

.NET 10 LTS SDK pinned in `global.json`; ASP.NET Core 10 Blazor Web App (Interactive Server); ASP.NET Core Identity; EF Core 10 + Npgsql 10 (xmin row version); PostgreSQL 18; AWSSDK.S3 (MinIO with path-style addressing locally, S3 in AWS); `Microsoft.Extensions.Http.Resilience` for timeouts/retries/circuit breakers; Serilog compact JSON; built-in `Microsoft.AspNetCore.OpenApi` for the API spec; PDF generation with a permissively licensed library (default PDFsharp-MigraDoc, MIT; **QuestPDF only if its licence terms fit the organisation**, record the check); QRCoder (MIT); Chart.js vendored and pinned in `wwwroot` (no CDN); xUnit v3; Testcontainers for .NET; Microsoft.Playwright + pinned local axe-core; Gitleaks; Trivy; Terraform ≥ 1.14 with `hashicorp/aws` 6.x; TFLint.

Version policy: latest stable compatible versions verified from official sources at E0-02, exact pins (central package management + lock files, container images by digest, CLI tools by checksum). No floating tags, previews or remembered version numbers. **MinIO check:** the brief names MinIO; verify an official MinIO server image is still published. If not, pin the last official image by digest or use another S3-compatible emulator, and record the deviation.

### 4.4 Cross-cutting design rules

1. **Transition table** in the domain: `(from, to) → (allowed roles, guards[])`. Only command endpoints change status; no generic status setter. Unlisted pairs → 422.
2. **Audit**: `AuditEvent` (actor, UTC time, action, entity, before/after summary, reason, correlation ID) is appended in the same transaction as the change. EF `SaveChanges` interceptor rejects hard deletes and updates of audit rows. Soft deactivation only.
3. **Concurrency**: every mutable aggregate has a row version (PostgreSQL `xmin`); child mutations bump the parent (`Application`) version. Every write sends the version it read. Conflict → 409 with current safe values; UI shows a merge/reload panel (FR-18).
4. **Idempotency**: client request keys for draft creation, submission, check-in/out, notification sends (gateway `Idempotency-Key`), webhook `eventId`, CSV batch checksum + `partnerEventId`.
5. **Outbox**: domain changes enqueue `Notification` rows in the same transaction; the worker sends them. A failed send never rolls back a permit.
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
- **Redaction**: headers `Authorization`, `X-API-Key`, `X-Signature`, query `access_token`, body fields named like `secret|password|token` are masked before logging or storing. Tested.
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

The notification mock calls back the app at the URL in `Mock__Notify__WebhookUrl` (`http://web:8080/webhooks/delivery` both in Compose and through Service Connect in AWS). The webhook path is internal-only in AWS: the ALB answers `/webhooks/*` with a fixed 403, so only the in-VPC mock can reach it; the HMAC check still applies.

### 4.6 Environments, host names and URLs

| Environment | No domain (`base_domain = ""`) | With a domain (`base_domain = example.com`) |
| --- | --- | --- |
| Local | `http://127.0.0.1:5280` | n/a |
| AWS dev | `http://<alb-dns-name>` (changes on rebuild) | `https://dev-brief3.example.com` only; `http://` redirects (301); the ALB DNS name and any other host return 404 |
| Future AWS prod (not built) | not allowed (validation rule) | `https://prod-brief3.<base_domain>` or an explicit override |

Terraform derives the host name; nobody types it: `app_host = "${var.environment}-brief3.${var.base_domain}"` (the `-brief3` label is a fixed local in Terraform, not a variable, so it cannot drift). The ACM certificate, the ALB host-header rule, the Route 53 alias (Route 53 modes) or the CNAME the human adds (external DNS mode), the smoke scripts and the `app_url` output all use `app_host`. Challenge 2 used a bare `<APP_DOMAIN>`; never reuse its record names. `base_domain` must be a valid lower-case DNS name without a scheme, trailing dot or leading `dev-` label (Terraform validation).

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
| A-12 | Permit token = opaque signed token (HMAC-SHA256 over permit ID + version + expiry) encoded in the QR and printed as a short typed code; valid = current version, case Permitted, not revoked/cancelled, inside the check-in window |
| A-13 | Information-request due date default 5 business days, minimum 1 day; overdue requests are flagged, not auto-rejected |
| A-14 | Files: PDF/PNG/JPG, max 5 MiB (5,242,880 bytes), checked by extension, declared MIME and magic bytes; signature checks are not antivirus; Auditor sees metadata only; Attendant uploads incident photos only |
| A-15 | RDS connection uses `SSL Mode=VerifyFull` with the AWS RDS global CA bundle downloaded at image build with a pinned checksum (improves on challenge-2 A-35 `Trust Server Certificate=true`) |
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

The agent adds new assumptions from A-29 onward as gaps are found, and labels each in UI help text or error messages where it affects users.

---

## 6. Delivery approach

### 6.1 Milestones

| Milestone | Goal | Exit evidence |
| --- | --- | --- |
| **M0 Walking skeleton** | Repo, pinned toolchain, Compose stack (db, minio, web, worker, migrate, one mock), landing page, login with seeded users, health, logs, test projects, PR pipeline, AWS dev deployed through the deploy pipeline | `docker compose up -d --wait` healthy; PR validation green; deploy pipeline run URL; login works on the AWS `app_url` |
| **M1 Thin end-to-end journey** | All 14 transitions enforced with minimal but real pages; audit, 409, 422, 403; storage upload; permit PDF; check-in | Integration tests for every transition and ≥6 forbidden moves; browser test of the full brief §9 journey (happy path) |
| **M2 All 26 pages complete** | Every page fully functional, validated and accessible; all integrations working in `normal` mode | Browser test per page; axe clean on all pages; screenshots generated |
| **M3 Failure matrix** | All scenario modes, Scenario Panel, every row of brief §7.3 and §8 demonstrated and tested | Failure-matrix test suite green; mock call counts recorded |
| **M4 Release** | Full seed, docs set, test report, clean-clone check, AWS acceptance, destroy/rebuild drill | Section 11 deliverables checklist complete with evidence |

Stories are tagged with their milestone. Within a milestone follow dependencies; across milestones, a story may be **started** early but is finished in its milestone.

### 6.2 Story format, Definition of Ready and Definition of Done

Each story: ID, title, milestone, brief references, user story sentence, acceptance criteria (AC), notes, dependencies. ACs are testable statements; "Given/When/Then" is used where the sequence matters.

**Ready** when: brief references read; dependencies done; open assumptions recorded; the proving tests named.

**Done** when all are true:
1. Every AC is met and proven by an automated test (unit, integration or browser) or, where automation is impossible, by recorded manual evidence.
2. Server-side authorization is tested with crafted requests for each new endpoint (allowed role passes; at least one forbidden role gets 403/404).
3. New or changed pages: browser test, axe scan (WCAG 2.1 A/AA tags) with 0 violations, keyboard path, phone (≈390 px) and desktop (≈1280 px) layouts checked; no dead controls or placeholder text.
4. No secret in code, config, logs or test output (the documented local demo password, OD-2, is the single deliberate exception); structured log entries for the new actions.
5. `docs/plan/STATUS.md`, `docs/REQUIREMENTS-TRACEABILITY.md` and affected docs updated; assumptions logged.
6. Committed as `<story-id>: <summary>`; PR validation green once pipelines exist (M0 onward). After M0, `main` stays deployable.

### 6.3 Evidence rules

Passed only with successful command output. Failed (with output), not executed (reason + exact command) and blocked (what is missing) are separate states. Configured CI is not executed CI. A green apply is not acceptance. Never copy results, counts or versions from challenge 2.

---

## 7. Epics and user stories

### E0 — Foundation and walking skeleton

#### E0-01 Repository, governance and status files · M0
As the team, we want a clean repository with governance documents, so that every session starts from the same facts.
- AC1 `.gitignore`, `.gitattributes`, `.editorconfig`, `Directory.Build.props` (nullable, warnings as errors, analyzers), `Directory.Packages.props` (central package management), `global.json` exist.
- AC2 `docs/plan/STATUS.md` lists every story in this plan with state `not started`, plus sections: Objective, Inputs, Verified results, Not executed/blocked, Decisions, Running services, Next action.
- AC3 `docs/ASSUMPTIONS-LOG.md` (A-01…A-28), `docs/TECHNICAL-DECISIONS.md` (OD-1…OD-6 with alternatives), `docs/REQUIREMENTS-TRACEABILITY.md` skeleton (rows from section 10), `docs/IMPLEMENTATION-LOG.md` exist.
- AC4 First commit on `main` pushed to Azure Repos `misi-code-challenge-brief-3` (ask before pushing).

#### E0-02 Toolchain verification and pinning · M0
As the team, we want exact, verified versions, so that builds are repeatable on a clean machine.
- AC1 `docs/DEPENDENCIES.md` lists every SDK, package, container image (digest), CLI tool, browser and Terraform provider with version, official source URL and date checked; unverifiable items say `pending verification`.
- AC2 A compatibility spike restores (`--locked-mode`) and builds all projects with 0 warnings.
- AC3 MinIO image availability checked (section 4.3) and the outcome recorded.

#### E0-03 Containers and one-command local stack · M0
As a developer, I want `docker compose up -d --wait` to start the whole system, so that a clean machine needs only Git and Docker to run it.
- AC1 Multi-stage `Dockerfile` (web) and `src/EventPermits.Mocks/Dockerfile`, non-root users, pinned base images, no secrets in layers, `.dockerignore`.
- AC2 `compose.yml` services: `init` (secrets, no deps), `db`, `minio`, `minio-init`, `migrate` (migrations + seed, idempotent), `web`, `worker`, and the mock services (skeleton: `mock-registry` only in M0; others added by their stories). Health checks use the image's own `probe` command (no curl assumed).
- AC3 Web published on `127.0.0.1:5280` (variable `WEB_PORT`); MinIO S3 API (`127.0.0.1:9000`, needed so the browser can follow pre-signed download redirects, A-16) and console on loopback only; db and mocks internal (an optional `debug` profile may publish mock ports on loopback).
- AC3b `docker compose up -d --wait` alone produces a working, seeded system: `init` and `migrate` run first through `depends_on: condition: service_completed_successfully`, both idempotent.
- AC4 Data survives `docker compose down` / `up`; destructive reset documented separately.
- AC5 `tools/Dev` commands `doctor`, `setup`, `up`, `down`, `seed`, `test`, `smoke`, `scan`, `screenshots` work on macOS and Windows (pwsh/zsh/bash, no shell-specific quoting).

#### E0-04 Application shell and design system · M0
As any user, I want a consistent, accessible layout with role-aware navigation, so that 26 pages feel like one product.
- AC1 Header with product name, user/role indicator and logout; grouped navigation (Cases, Scheduling, Operations, Integrations, Reports, Administration) showing only permitted pages; skip link; breadcrumbs.
- AC2 Design tokens (colour, spacing, type) in one stylesheet; status chip component always shows text; table, form, wizard, drawer, toast/live-region, empty-state, error-state, pager and conflict-panel components built once and reused.
- AC3 Responsive at 1280/900/720/420 px; tables become labelled cards on phones; 44 px targets; reduced motion respected.
- AC4 Friendly 403/404/500 pages with correlation ID; no stack traces in Production.

#### E0-05 Observability baseline · M0 · FR-16
As an operator, I want structured logs with correlation IDs and dependency health, so that I can trace any request.
- AC1 Correlation ID middleware: accepts/creates `X-Correlation-ID` (bounded length), adds it to logs, responses, ProblemDetails and outbound calls.
- AC2 Serilog compact JSON to stdout with actor, roles, case reference (when known), action, result, elapsed ms; passwords, tokens, file bytes and full form bodies never logged (tested).
- AC3 `/health/live` (process), `/health/ready` (database), `/health` (app, database, object storage, each integration, notification gateway) with Healthy/Degraded/Unhealthy JSON and no secrets. Integration outage → Degraded, not Unhealthy.

#### E0-06 Test harness and report · M0 · FR-17
As the team, we want unit, integration and browser suites from day one, so that every story adds tests to a working harness.
- AC1 Three test projects run with one command (`tools/Dev test`), nonzero exit on any failure or missing suite; counts of discovered/executed tests printed.
- AC2 Integration tests use Testcontainers (PostgreSQL 18, MinIO) and in-process mocks; no EF InMemory.
- AC3 Browser tests run Playwright (Chromium, pinned) against a Compose stack started by the runner, with axe pinned locally.
- AC4 TRX results converted to a readable HTML/Markdown report under `docs/test-report/` by a scripted step (brief deliverable).

#### E0-07 Walking skeleton deployed to AWS dev · M0
As the owner, I want the skeleton live in AWS through the real pipelines before feature work, so that deployment risk is retired early.
- AC1 Depends on E14-01…E14-09 (minimal: network, database, secrets, S3, ALB, ECS web + worker + migrate + mock-registry) and E15-01…E15-03.
- AC2 Deploy pipeline run from `main` finishes green; `app_url` shows the login page; seeded users can sign in (password from Secrets Manager); `/health` shows database and storage Healthy.
- AC3 Run URL, commit SHA and timestamps recorded in STATUS.md.

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
- AC2 Cookies `HttpOnly`, `SameSite=Lax`; `Secure` when `Hosting:BehindTlsProxy=true` and the request was HTTPS (config-gated, A-15 of challenge 2 pattern); antiforgery on all form posts and state-changing API calls.
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
- AC1 `EventPermits.Mocks` runs one service per process (`--service registry|calendar|notify|weather|geocode|partner-feed`) with the contracts in section 4.5; each appears as its own Compose service and ECS service.
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
- AC1 `IObjectStore` over AWSSDK.S3; MinIO locally (path style), S3 in AWS; bucket private; random object keys (`attachments/{guid}`); SHA-256 checksum, size, verified content type, uploader, original sanitised name stored on Attachment.
- AC2 Upload accepts PDF/PNG/JPG only; extension, declared MIME and magic bytes must agree; max 5 MiB (exact boundary tested: 5,242,880 accepted, +1 rejected); empty files rejected; streaming limits configured in Kestrel and Blazor.
- AC3 Download follows A-16: role/scope check → signed app link (TTL from RuleSetting, audited) → on open, re-check + audit → 302 to a 60-second pre-signed storage URL. Tests: expired app link → friendly 403 "expired" page with Regenerate; tampered signature → 403; link used by a different user → 403; user who lost access after issue → 403/404; Auditor never gets a link (metadata only); direct unsigned bucket URL → denied by storage.
- AC4 A failed DB commit after upload leaves no referenced object; orphan cleanup job removes unreferenced staged objects after 24 h.

#### E3-05 Document Vault (page 23) · M2
As a case worker, I want all attachments I may see in one place, so that I can find, preview and audit documents.
- AC1 Server-paged list with filters (case, type, uploader, date), metadata (size, checksum, type, uploader, uploaded at), preview for images and PDFs via a short-lived link, download link with visible expiry countdown, Regenerate link.
- AC2 Per-file audit history (uploaded, link issued, link expired attempt, denied attempts).
- AC3 Upload to a case the user may modify; Auditor metadata only; integration test proves an unauthorised role cannot obtain a link through page or API.

#### E3-06 Background job host with locking · M1
- AC1 `worker` process runs scheduled jobs (outbox dispatch, status poll, calendar sync, partner import, overdue marker, orphan cleanup) with PostgreSQL advisory locks so two workers never run the same job; job runs recorded (start, end, outcome).
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
- AC1 List/search permits (number, case, venue, date, status: Active/Revoked/Superseded/Cancelled), version history per permit.
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

### E14 — AWS infrastructure (Terraform)

Rules for every Terraform story (carried from challenge 2): exact pins and committed `.terraform.lock.hcl` (linux_amd64, darwin_arm64, darwin_amd64); `fmt`, `validate`, TFLint and Trivy config clean (accepted findings suppressed inline with a reason); module README, typed and described variables, sensitive outputs marked; names `misi3-<env>-<name>`; `default_tags` `Project=misi-event-permits`, `Environment`, `ManagedBy=terraform`, `Repository=misi-code-challenge-brief-3`; **everything Terraform creates, Terraform can destroy** in one run (no `prevent_destroy`, protection flags as variables, `recovery_window_in_days = 0`, Terraform owns every log group, `force_destroy` on created buckets); for each story the status file answers "can `terraform destroy` remove everything this creates?".

#### E14-01 [HUMAN + agent] Hand-made resources · M0
- AC1 Agent writes `infra/manual/README.md` with CLI commands and a verification block for: S3 state bucket `misi-event-permits-tf-state` (versioning, SSE-S3, Block Public Access, bucket-owner-enforced, TLS-only policy, non-current versions expire after 90 days) and ECR repositories `misi/event-permits-web`, `misi/event-permits-mocks` (immutable tags, scan on push, AES-256, lifecycle: keep last 30 tagged, expire untagged after 7 days).
- AC2 Human runs them and returns the verification output; Terraform only reads these (data sources/backend config). A PR guard fails on `resource "aws_ecr_repository"` or a resource for the state bucket.

#### E14-02 Bootstrap: pipeline roles (reusing the OIDC provider) · M0
- AC1 `infra/bootstrap` adapted from challenge 2: reads the **existing** Azure DevOps OIDC provider (`vstoken.dev.azure.com/<org GUID>`) by data source, never creates or destroys it; creates `misi3-ado-plan` and `misi3-ado-deploy` under path `/ado/` with trust `sub = sc://WorldServices/RFI VA MISI - Code Challenge/aws-misi3-{plan|deploy}` (both the spaced and `%20` forms, as challenge 2 learned).
- AC2 Plan role: ReadOnly + state read for `dev/*` and `bootstrap/*` keys + lock files + `secretsmanager:GetSecretValue` on `misi3/*` only. Deploy role: create/destroy the dev stack services (EC2/VPC, ELB, ACM, Route 53, ECS, RDS, S3 for `misi3-*` buckets, Secrets Manager `misi3/*`, CloudWatch Logs, Service Discovery, IAM under `/misi3/` with scoped `iam:PassRole`), ECR push/pull on the two repositories only, explicit denies on deleting the hand-made resources and on modifying `/ado/` roles.
- AC3 Human applies it (guide step) with state key `bootstrap/terraform.tfstate`; outputs recorded; challenge-2 roles and provider unchanged (verified by `terraform plan` showing no changes outside new roles/policies).

#### E14-03 Terraform skeleton · M0
- AC1 Layout `infra/{manual,bootstrap,modules/*,envs/dev,scripts}`; modules copied from challenge 2 and renamed; `envs/dev` backend `s3` with `use_lockfile = true`; `terraform init -backend=false && validate` clean.
- AC2 Pipeline inputs (`image_tag`, `base_domain`, `dns_mode`) come only from `TF_VAR_*`; `terraform.tfvars` holds sizing only (challenge-2 lesson: tfvars overrides env vars).

#### E14-04 Network · M0
- AC1 VPC `10.41.0.0/16`, two AZs, public / private-app / database subnets, one NAT gateway (accepted single-AZ risk), flow logs (30-day Terraform-owned log group), default SG with no rules.
- AC2 Database subnets have no internet route (verified after apply).

#### E14-05 Database and secrets · M0
- AC1 RDS PostgreSQL 18 (latest 18.x available in us-east-1, verified), `db.t4g.micro`, gp3 20→100 GiB, encrypted, private, 7-day backups, deletion protection and final snapshot as variables, Enhanced Monitoring off, parameter group `rds.force_ssl=1`.
- AC2 Secrets Manager (`recovery_window_in_days = 0`), all generated by `random_password`: `misi3/dev/db-password`, `misi3/dev/seed-credentials` (JSON keyed by the seed user names read from code; `Seed__AllowDemoPassword` is never set in AWS, so the README demo password cannot work there), `misi3/dev/integration-secrets` (registry API key, calendar client secret, webhook HMAC secret, mock admin key, permit token signing key) shared by app and mocks.
- AC3 App connection uses `SSL Mode=VerifyFull` with the RDS CA bundle in the image (A-15); a non-SSL connection is refused (evidence in E14-12).

#### E14-06 Object storage · M0
- AC1 S3 bucket `misi3-dev-files-<random suffix>`: Block Public Access, bucket-owner-enforced, SSE-S3, TLS-only bucket policy, `force_destroy = true`, lifecycle expiring orphan staging objects; no CORS (browser downloads use pre-signed GETs only).
- AC2 Task roles: web and worker read/write `attachments/*`, `permits/*`, read/delete `partner-drop/*`; partner-feed mock write `partner-drop/*` only; migrate task write for seed files. Pre-signed URLs work with the task role (tested in E14-12).

#### E14-07 Load balancer · M0
- AC1 Reuse challenge-2 module: HTTP mode (no domain, OD-3) and HTTPS mode (ACM DNS validation, TLS 1.3/1.2 policy verified by name, :80 → 301, host-header rule, other hosts 404); sticky `lb_cookie` 1 day; health check `/health/ready`; idle timeout 120 s; `drop_invalid_header_fields`.
- AC2 Only the ALB is public; tasks have no public IPs. A listener rule (both modes) answers `/webhooks/*` with a fixed 403 so callbacks arrive only from inside the VPC.
- AC3 Host name per section 4.6: replace challenge-2's `app_domain` input with `base_domain` + derived `app_host` (`dev-brief3.<base_domain>`). The certificate, host rule, Route 53 alias (`route53-existing` / `route53-new`) and the external-DNS CNAME output all use `app_host`; `terraform plan` with `base_domain=example.com` shows `dev-brief3.example.com` everywhere and nothing named `dev.example.com` (tested with a plan on a scratch copy, as in challenge 2). Validation rejects a `base_domain` with a scheme, trailing dot or upper case.

#### E14-08 ECS cluster and services · M0 (web, worker, mock-registry) / M2 (all mocks)
- AC1 Cluster `misi3-dev` with Container Insights (its log group created by Terraform first), Service Connect namespace `misi3-dev.local`.
- AC2 Task definitions (X86_64, Linux): `web` (0.5 vCPU/1 GiB, ×2, Service Connect client-server as `web:8080` for webhooks), `worker` (×1, no port), `migrate` (one-off), six mocks (0.25 vCPU/0.5 GiB, ×1, capacity provider from variable, default `FARGATE_SPOT`); container health checks via `probe`; secrets injected from Secrets Manager; `Hosting__BehindTlsProxy` per mode; storage settings point to the S3 bucket.
- AC3 Security groups: ALB → web 8080; web/worker → mocks 8080; notify mock → web 8080; web/worker/migrate → RDS 5432; nothing else.
- AC4 Web deployment circuit breaker with rollback, minimum healthy 100 %, `wait_for_steady_state`; web does not depend on mock health.
- AC5 IAM roles under `/misi3/`, least privilege per service; ECS Exec enabled for web, worker and mocks.

#### E14-09 Migrations and seed before every rollout · M0
- AC1 `terraform_data` + `run-one-off-task.sh` (from challenge 2) runs `migrate` and waits for exit 0 before the web service updates; failure fails the apply and keeps old tasks serving (proven or recorded as not executed).
- AC2 Second deploy: seed reports 0 created.

#### E14-10 Configuration parity matrix · M1
- AC1 `docs/aws/CONFIGURATION.md` lists every setting with local value source and AWS value source; a test loads the Production config schema and fails on unknown/missing keys.

#### E14-11 Operations runbook and cost control · M4
- AC1 `docs/aws/AWS-OPERATIONS.md`: credentials retrieval, logs, ECS Exec, switching mock scenarios in AWS (through the Scenario Panel), rollback, scaling to zero, RDS stop, snapshot restore, secret rotation, teardown and what survives it.
- AC2 Monthly cost estimate from the AWS Pricing Calculator recorded (expected roughly USD 170–230 while running: ALB, NAT, 3 app tasks, 6 Spot mock tasks, RDS micro, S3, logs).

#### E14-12 AWS acceptance evidence · M4
- AC1 Table in STATUS.md, each row passed/failed/not executed with evidence: smoke script; remote browser journey (E15-03); each role logs in; upload + pre-signed download + expiry; webhook delivered through Service Connect; Data Protection keys shared (login survives forced redeploy); mock outage drill (scale a mock to 0 → hub Offline, app serving); seed idempotent; RDS non-SSL refused; tasks not directly reachable; `/webhooks/*` from the internet → 403; the README demo password is rejected; the landing page `/` loads anonymously and shows no credentials; HTTPS mode only: certificate ISSUED for `dev-brief3.<base_domain>` and auto-renewal eligible, cookies `Secure`.

### E15 — Azure DevOps CI/CD

#### E15-01 [HUMAN + agent] Azure DevOps setup guide · M0
- AC1 `docs/aws/AZURE-DEVOPS-SETUP-GUIDE.md` adapted from challenge 2 with only the steps still needed: verify parallel jobs and AWS Toolkit (already done for challenge 2), create ECR/state bucket (E14-01), apply bootstrap (E14-02), service connections `aws-misi3-plan` / `aws-misi3-deploy` (OIDC), environment `aws-misi3-dev` with an Exclusive Lock check, branch control on the deploy connection (`refs/heads/main`), variable group `misi3-aws-dev` (non-secret values: `AWS_REGION`, `AWS_ACCOUNT_ID`, `TF_STATE_BUCKET`, `BASE_DOMAIN` (empty = HTTP mode), `DNS_MODE`), a "Domain" section explaining that a domain gives `https://dev-brief3.<domain>` and which DNS record the human adds in external mode, three pipelines `misi3-pr-validation`, `misi3-deploy`, `misi3-destroy`, branch policy on `main`.
- AC2 A temporary run prints `aws sts get-caller-identity` per connection showing the expected role.

#### E15-02 PR validation pipeline · M0
- AC1 `pipelines/pr-validation.yml` (pinned `ubuntu-24.04`, tools by checksum): locked restore, build, unit + integration tests (Testcontainers), Compose stack up + browser/axe suite, publish TRX and the HTML test report, Docker build of both images + Trivy, Terraform fmt/validate/TFLint/Trivy config + plan with the plan role (no plan file artifacts), Gitleaks, static guards.
- AC2 A PR with a failing test, a fmt error or a fake secret fails the right job (recorded run URLs).

#### E15-03 Deploy pipeline · M0
- AC1 Manual run from `main` (parameter `imageTag` for redeploy/rollback): build + test + images tagged with the commit SHA → plan (log only) → deployment job on `aws-misi3-dev` pushes images and applies (migrate first) → verify job runs `infra/scripts/smoke-https.sh dev-brief3.<base_domain>` (redirect, certificate for that exact host, TLS 1.2/1.3 only, headers, ALB DNS name → 404, `/webhooks/delivery` → 403) or `smoke-http.sh <alb-dns-name>` in HTTP mode (both also check that `/` returns the landing page anonymously), then a remote Playwright smoke subset (login per role, page reachability for all 26 routes, one permit download) using seed credentials from Secrets Manager written to a temp file deleted with `always()`.
- AC2 Rollback by `imageTag` proven once.

#### E15-04 Destroy pipeline and drill · M4
- AC1 `pipelines/destroy.yml`: manual, double confirmation, protection off then destroy, leftover check by tags, log groups, Cloud Map namespace and secrets; only the hand-made resources and the optional final snapshot remain.
- AC2 Destroy → redeploy drill executed with the user's go-ahead at that moment, or recorded as not executed.

### E16 — Documentation and submission package (brief §10)

#### E16-01 README · M0 (skeleton) / M4 (final)
- AC1 Purpose, architecture summary and diagram, prerequisites in install order (Windows 11 and macOS), exact versions, clean-machine install → migrate → seed → run → test commands, demo accounts table (user name, role(s), the local demo password from OD-2, with a note that it works only on local runs; AWS passwords come from Secrets Manager), URLs, health and logs, scenario switching, AWS deployment pointer, troubleshooting, known limitations.

#### E16-02 Architecture, ERD and workflow docs · M1 / M4
- AC1 `docs/ARCHITECTURE.md` (C4-style context/container diagrams in Mermaid, local and AWS), `docs/ERD.md` (E2-01), `docs/WORKFLOW.md` (E2-03), `docs/INTEGRATIONS.md` (contracts, failure behaviours, configuration).

#### E16-03 API specification · M2
- AC1 OpenAPI documents for the app API and each mock exported to `docs/api/` by a build step; a test fails if the committed spec is out of date.

#### E16-04 Test report and traceability · M4
- AC1 Final test report (counts per suite, environment, commit) in `docs/test-report/`; traceability matrix complete with code locations, named tests and evidence for every row in section 10.

#### E16-05 Screenshots of all 26 pages · M4
- AC1 `tools/Dev screenshots` (Playwright) captures every page (desktop and phone) with seeded data into `docs/screenshots/`, including integration pages 19–26, the attempt detail, and the landing and login pages.

#### E16-06 Implementation log and known limitations · continuous / M4
- AC1 `docs/IMPLEMENTATION-LOG.md` (dated entries per story: what, why, evidence links); `docs/KNOWN-LIMITATIONS.md` (honest list: e.g., HTTP-only dev in AWS, signature checks are not antivirus, SFTP simulated, Windows not verified if so).

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
| User requirement: AWS deployment via Azure DevOps | E0-07, E14-*, E15-* |
| User requirement: public landing page | E0-09 |

---

## 11. Submission checklist (definition of done for the whole plan)

1. All 26 pages reachable from navigation and fully functional; browser test per page; axe clean.
2. Full journey and all demo items pass as automated tests on the local stack and the AWS smoke subset passes on `app_url`.
3. Failure matrix suite passes; mock call counts recorded.
4. Seed matches section 8; re-seed creates nothing.
5. Clean-clone install → migrate → seed → run → test verified from README alone.
6. No secrets in Git (Gitleaks clean), images or logs.
7. Docs present: README (accounts, architecture, setup), ERD, workflow diagram, API spec, test report, screenshots of all 26 pages, implementation log, known limitations, assumptions, decisions, traceability, accessibility checklist, demo runbook, AWS operations and setup guide.
8. AWS: deployed through `misi3-deploy` from `main`; E14-12 table complete; destroy drill passed or recorded as not executed.
9. STATUS.md separates passed, failed, not executed and blocked items, with exact commands.

## 12. Risks

| Risk | Mitigation |
| --- | --- |
| Scope (26 pages, 7 integrations) overwhelms delivery | Milestones deliver a working journey first (M1); shared components (E0-04) and the integration pipeline (E3-02) remove repetition; never leave a page half-built across milestones |
| Pages become shallow "forms and cards" (brief pass/fail rule) | DoD requires real actions and a browser test per page; E13-04 automated placeholder scan |
| Integration code duplicated per service | One adapter pipeline and one mock host with a shared scenario engine |
| Collision with challenge-2 resources in the shared account/project | OD-4 naming isolation; bootstrap plan must show no changes to challenge-2 resources |
| AWS pipeline issues found late | M0 walking skeleton deploy |
| MinIO image availability changed | E0-02 check with documented fallback |
| Blazor circuits dropped during deploys | Sticky sessions, 30 s deregistration delay, reconnect UI, manual deploys |
| Cost while idle | Mocks on Spot; scale-to-zero and destroy pipeline documented |
| Licence of PDF library | Default MIT library; check recorded in DEPENDENCIES.md |

## 13. Change log

| Date | Change | By |
| --- | --- | --- |
| 2026-10-15 | Initial plan | user + Claude |
| 2026-10-15 | User decisions: fixed local demo password `EventPermits!Demo2026` in README (OD-2, A-02, guarded by `Seed:AllowDemoPassword`; AWS keeps generated passwords); public landing page at `/` (new E0-09). Pointer to the split tracks | user + Claude |
| 2026-10-15 | User confirmed .NET 10 (OD-1). Domain rule: app host `dev-brief3.<base_domain>` (section 4.6, OD-3, E14-07, E15-01/03, E14-12). Review fixes: check-in recorded only by the assigned attendant (brief §5); two-layer expiring links with friendly expiry page (A-16); MinIO S3 port on loopback; one-command seeded start; webhook path internal-only; calendar push timing (A-27); edit/upload rights (A-26); local HTTP (A-25); Data Protection limitation (A-28); env-var mock modes; shared mock/seed reference data; explicit brief §8 test checklist; T6 guard completed | user + Claude |
