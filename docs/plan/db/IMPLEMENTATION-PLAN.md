# Database & User Management: plan (boundary `db`)

Owner: one database / backend-core engineer. Read [`../README.md`](../README.md) first (ownership, contracts, requests protocol). Story acceptance criteria (AC) live in the master [`../IMPLEMENTATION-PLAN.md`](../IMPLEMENTATION-PLAN.md); this file lists what you own and the order to work in. Progress goes in `docs/plan/db/STATUS.md`.

## 1. Mission and scope

Everything about **persisted data, identity and who may do what**:

1. Database creation: the relational model, EF Core migrations, indexes, constraints, row versioning, `migrate`.
2. Seed data: deterministic, idempotent, coherent with the workflow (brief §9 counts).
3. User account creation: Identity setup, seeded accounts, activation, lockout, the demo-password guard.
4. Role-based access control: capability matrix, policies on every endpoint, record-level scoping, separation of duties.
5. The integrity core other boundaries build on: workflow transition table T1–T14, append-only audit, optimistic concurrency, reference numbers, rule settings, conflict engine, permit token verification.

Out of scope: pages and page-level commands (ui-ux), adapters, mocks, storage, webhooks (ext-svc), Terraform, Dockerfiles, `compose.yml` (infra), browser/E2E suites (test-auto). You do provide the service contracts they consume.

## 2. Baseline (verify against code; do not rebuild)

The 2026-10-15 snapshot (`docs/IMPLEMENTATION-LOG.md`, `z_app-only/STATUS.md`) says: domain model, workflow table, conflict engine, initial migration (`20261015151037_Initial`), seeder (`Seeder.cs`, `Seeder.Cases.cs`, `ReferenceData.cs`), Identity login/logout with lockout and 10 demo accounts, and integration tests (T1–T13 journey, six forbidden moves, 403 roles, 404 scope, 409, SoD, cancel releases, idempotent draft/submit, 50 parallel references, atomic audit, no hard delete) exist. The story checklist in that STATUS still shows many E1/E2 stories as "not started": your first task (E-DB-00) is to reconcile story states with the code and tests, and to write the STATUS file.

Your files: `src/EventPermits.Domain/**`; `src/EventPermits.Application/{AppDbContext,Core,Actor,Capabilities,Errors}.cs`; `Application/Services/Admin.cs`; `src/EventPermits.Infrastructure/{Persistence.cs,Migrations/**,Seeding/**,HmacSigner.cs}`; `src/EventPermits.Web/Api/{AccountEndpoints,AdminEndpoints}.cs`; `data/reference/sites.json`; `tests/.../Db/**`; docs `ERD.md`, `WORKFLOW.md`, `ROLE-MATRIX.md`.

## 3. Decisions and assumptions you carry

OD-1 (.NET 10), OD-2 (fixed local demo password, guarded by `Seed:AllowDemoPassword`), A-02…A-14, A-21, A-22, A-24, A-26, A-28, A-29 (seed anchored to the day of first seed; tests use an injected `TimeProvider`). Existing technical decisions D-01 (one service method per command), D-02 (`xmin` row version), D-03 (client-assigned GUID keys, `ValueGeneratedNever`). New assumptions: A-40…A-49; new decisions: `B-nn`.

## 4. Stories you own

| Story | Title | Milestone | Notes for this boundary |
| --- | --- | --- | --- |
| E-DB-00 (new) | Reconcile status and start the STATUS file | M0 | Map every owned story to code + named test + state; list gaps |
| E1-01 | Local login and logout | M0 | Identity stores, lockout 5/15 min, cookies per `Hosting:BehindTlsProxy`, inactive users rejected on next request, demo-password guard; login **page UI** stays with ui-ux |
| E1-02 | Authorization policies and role matrix | M1 | Policy on every endpoint and page; reflection/route-table test fails on a missing policy; anonymous allowlist exactly as the AC; `docs/ROLE-MATRIX.md` generated from code; crafted-request test per prohibited capability (brief §3) |
| E1-03 | Record-level scoping | M1 | Scope filters applied before search, counts, paging, export, dashboards and attachment links; 404 outside scope; role/assignment re-checked from the database on every command |
| E1-05 (service side) | Users, roles, rules, templates, audit viewer | M2 | `Admin.cs` + `AdminEndpoints.cs`: create/search/activate/deactivate users (no delete), assign/remove roles with reason, admin cannot remove own Admin role, RuleSetting edit with type/range validation and audit, template edit with placeholder validation and preview data, audit query with filters and CSV. ui-ux builds page 18 on it |
| E2-01 | Data model and migrations | M1 | All entities in the AC list; FKs, indexes for every filter/sort a page uses, UTC `timestamptz`, audit columns, row versions; migrations applied only by `migrate`, never at web startup; `docs/ERD.md` checked by a test |
| E2-02 | Reference numbers | M1 | APP/PER per-year counters under row lock; 50-parallel test; rollover; exhaustion error |
| E2-03 | Workflow engine | M1 | Transition table T1–T14 with stable error codes; wrong role 403 before 422; `docs/WORKFLOW.md` Mermaid generated and kept in sync by a test |
| E2-04 | Append-only audit, no hard deletes | M1 | Audit in the same transaction; interceptor rejects deletes and audit updates; route test shows no delete endpoints |
| E2-05 | Concurrency (service + contract) | M1 | Row version on every write; 409 body with latest safe values + reload link; child writes bump the parent. The conflict **panel UI** is ui-ux's |
| E2-06 | Business rule settings | M1 | Seeded with brief values; cached service invalidated on change |
| E2-07 | Cancellation | M1 | T14 from the six permitted states, side effects (release reservations/allocations, invalidate permit, queue notification). The button is ui-ux's |
| E6-02 | Conflict engine | M1 | Pure domain service; all violations returned; boundary property tests; concurrent same-slot test; Scheduling Officer cannot override hard closures |
| E7-04 | Permit token verification | M1 | Valid, Expired, Revoked, Superseded, Cancelled, NotFound, OutsideWindow, with unit tests (A-12) |
| E9-01 | Templates and outbox (data side) | M1 | Template-driven messages for the listed events; `OutboxMessage` written in the business transaction with an idempotency key. The sender worker is ext-svc's |
| E12-01 | Deterministic seed | M1 core / M4 full | Brief §9 counts (master section 8); coherent audit trails; scenarios (SoD, overdue, severe weather, unreconciled conflict, unresolved critical incident, expired link, revoked permit); idempotent (re-run creates 0); destructive reset only via the explicit `seed --reset --yes` |
| E12-02 (app side) | Shared reference data | M2 | Own `sites.json` and the app-side loader; mock-side files belong to ext-svc; the "app seed and mock data agree" test is test-auto's |
| E16-02 (data docs) | `ERD.md`, `WORKFLOW.md`, `ROLE-MATRIX.md` | M1/M4 | Generated or test-checked |
| B-01 (optional stretch, needs the user's yes) | Database role hardening | M3 | Separate migrator and runtime database roles; revoke UPDATE/DELETE on `AuditEvent` from the runtime role as defence in depth. Requires an infra request (RDS users, secrets) |

## 5. Contracts you provide

| To | What | Where |
| --- | --- | --- |
| ui-ux | Transition table and guard error codes; `Actor`, capability and policy names; scope-filtered query helpers; 409/422/403/404 problem shapes (plan section 3.5); rule-setting reader; audit helper; seeded demo accounts (user name, roles) | `Domain/`, `Application/`, `docs/ROLE-MATRIX.md` |
| ext-svc | Entities for attempts, receipts, batches, snapshots, outbox, job runs; `HmacSigner`; rule settings (token TTL, webhook tolerance, weather cache, file limits) | `Entities.cs`, migrations |
| test-auto | Seed anchor date and `TimeProvider` seam; deterministic seed counts; list of accounts; role matrix; ERD/WORKFLOW diagrams | `Seeding/`, docs |
| infra | `migrate` entry points (`Migrator`, `Seeder.RunAsync`) with exit code 0/non-zero; seed credential contract (`Seed__CredentialsJson` keyed by user name, from code); demo-password guard | `Seeding/`, `Persistence.cs` |

**Schema changelog.** Keep a table in STATUS: date, migration name, what changed, who asked, who must react. Never ship a breaking migration without a note.

**Accounts single source.** The seeded user list in `Seeding/ReferenceData.cs` is the single source. `tools/Dev` currently hard-codes `DemoAccounts`; file a request to infra to read from one source, or test-auto adds a test that fails when they differ.

## 6. Order of work

1. **M0:** E-DB-00, E1-01, E2-01 baseline migration verified, demo-password guard test.
2. **M1:** E2-01 complete, E2-02…E2-07, E1-02, E1-03, E6-02, E7-04, E9-01, E12-01 core. These unblock ui-ux and ext-svc; deliver in this order and announce each in STATUS "Provided contracts": E2-01/E2-03 → E1-02/E1-03 → E6-02/E7-04 → E9-01 → E12-01 core.
3. **M2:** E1-05 service side, E12-02 app side, seed adjustments for pages (coordinate with ui-ux).
4. **M3:** security-sensitive review of scoping and policies with test-auto; B-01 if approved.
5. **M4:** E12-01 full counts and coherence, docs final, `seed --reset` verified for demo day.

## 7. Boundary Definition of Done (adds to master 6.2)

1. Every AC has an automated proof in `tests/.../Db/**`: service-level integration tests with Testcontainers PostgreSQL (no EF InMemory) and pure-domain unit tests.
2. Every endpoint you add has a crafted-request test: an allowed role passes, at least one forbidden role gets 403 or 404 in the order of plan section 3.5.
3. `migrate` is idempotent; a second run reports 0 seeded rows created; a failed migration leaves the database usable.
4. No secret in code, seed output or logs, except the documented local demo password under its guard.
5. `docs/CONFIGURATION.md` updated for any new setting (`Seed:*`, `Identity:*`, `Rules:*` defaults).
6. STATUS updated: states, commands, results, SHAs, schema changelog, requests.

## 8. Risks

| Risk | Mitigation |
| --- | --- |
| Schema churn breaks other boundaries | Single owner of `Entities.cs`; changelog; no breaking migration without notice |
| Policies drift from the brief matrix | Matrix generated from code and compared with brief §3 in a test |
| Scope filters forgotten in a new query | Shared scoped-query helper; route/reflection tests; test-auto crafted-request sweep |
| Seed becomes slow or non-deterministic | Seeded `Random`, injected clock, anchor date (A-29); timing recorded in STATUS |
| Demo password reaches AWS | Guard test plus infra E14-12 evidence that it is rejected |
