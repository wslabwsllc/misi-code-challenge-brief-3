# UI Development & Git Integration: plan (boundary `ui-ux`)

Owner: one full-stack UI engineer (Blazor Web App, Interactive Server) who is also the **Git and integration owner**. Read [`../README.md`](../README.md) first (ownership, contracts, requests protocol). Story acceptance criteria (AC) live in the master [`../IMPLEMENTATION-PLAN.md`](../IMPLEMENTATION-PLAN.md); this file lists what you own and the order to work in. Progress goes in `docs/plan/ui-ux/STATUS.md`.

## 1. Mission and scope

1. Build the application UI: shell, design system, landing page, **pages 01–18**, and the application-service commands/queries behind them (permit PDF included).
2. Manage Git commits and integration: commit and PR conventions, CODEOWNERS, and merging all boundaries' PRs in dependency order so `main` stays deployable.

Out of scope: schema, policies, workflow table (db), pages 19–26, adapters, mocks, storage, webhooks (ext-svc), Terraform and workflows' content (infra), browser/axe test suites (test-auto; you supply the hooks they need).

## 2. Baseline (verify against code; do not rebuild)

Exists per the 2026-10-15 snapshot: Blazor shell (`Layout/MainLayout`, `NavMenu`, `PublicLayout`), shared components (`StatusChip`, `LiveRegion`, `ConflictPanel`, `Pager`, `ProblemAlert`, `PageHeader`, `EmptyState`, `Forbidden`, `AppPage`), `PageCatalog.cs`, landing, login, error/404/access-denied pages, `Dashboard.razor`, application services (`Cases`, `Register`, `Reviews`, `Approvals`, `Permits`, `Operations`, `Dashboard`, `Scheduling`), API endpoints (`CaseEndpoints`, `ApiEndpoints`), `SimplePdf`/`PdfRenderer`, `docs/UI-DESIGN.md`. The earlier STATUS notes 23 of 26 pages being built on separate agent branches. Before writing any page, check `git branch -a`, `git stash list` and `.claude/worktrees/*` for unmerged page work and reuse it; never discard it. Your first task (E-UI-00) is to inventory what exists per page (route, persistence, validation, empty/error states) and write STATUS.

Your files: `src/EventPermits.Web/Components/**` (except ext-svc page folders), `Application/Services/{Cases,CaseFacts,Register,Reviews,Approvals,Permits,Operations,Dashboard,Scheduling}.cs`, `Web/Api/{CaseEndpoints,ApiEndpoints,ProblemExceptionHandler}.cs`, `Infrastructure/{PdfRenderer,SimplePdf}.cs`, `wwwroot/**`, `.github/{CODEOWNERS,pull_request_template.md}`, `docs/{UI-DESIGN,GIT-WORKFLOW}.md`, `tests/.../Ui/**`.

## 3. Decisions and assumptions you carry

OD-6 (Blazor Interactive Server; Identity pages and landing are static SSR), D-01 (one application-service method per command, also exposed as `/api/...`; Blazor never writes through `DbContext`), D-09/A-31 (in-repo PDF writer; WinAnsi text only), A-16, A-23, A-26. New assumptions A-32…A-39; decisions `U-nn`.

## 4. Pages you own

| # | Page | Route | Story | Milestone | Needs |
| --- | --- | --- | --- | --- | --- |
| 01 | Operations Dashboard | `/dashboard` | E11-01 | M2 | db scoping; ext-svc status read models |
| 02 | Application Register | `/applications` | E4-02 | M1 basic / M2 | db scope filters |
| 03 | New Application Wizard | `/applications/new`, `/applications/new/{id}/step/{n}` | E4-01 | M1 | ext-svc object store (uploads) |
| 04 | Application Detail | `/applications/{id}` | E4-04 | M1 basic / M2 | db guards; ext-svc risk flags |
| 05 | Eligibility Review Queue | `/reviews` | E5-01 | M1 | ext-svc registry evidence |
| 06 | Request Information Inbox | `/information-requests` | E5-03 | M1 | |
| 07 | Event Calendar | `/calendar` | E6-04 | M2 | db conflict engine; ext-svc calendar events |
| 08 | Venue Availability | `/venues/availability` | E6-03 | M2 | db conflict engine |
| 09 | Resource Planner | `/resources` | E6-06 | M2 | |
| 10 | Approval Workspace | `/approvals` | E7-01 | M1 | db SoD; ext-svc weather/calendar status |
| 11 | Permit Center | `/permits` | E7-03 | M2 | ext-svc signed links |
| 12 | Notification Center | `/notifications` | E9-04 | M2 | ext-svc sender; db outbox |
| 13 | Daily Event Roster | `/operations/roster` | E8-01 | M2 | |
| 14 | Event Check-In | `/operations/check-in` | E8-02 | M1 | db token verification |
| 15 | Incident Register | `/incidents` | E8-03 | M2 | ext-svc object store (photos) |
| 16 | Reports & Exports | `/reports` | E11-02 | M2 | |
| 17 | Administration: Venues | `/admin/venues` | E6-01 | M1 basic / M2 | ext-svc geocoder (coordinates) |
| 18 | Administration: Users & Rules | `/admin/users-rules` | E1-05 (page) | M2 | db `Admin.cs` |

## 5. Other stories you own

| Story | Title | Milestone | Notes |
| --- | --- | --- | --- |
| E-UI-00 (new) | Inventory and STATUS file | M0 | Per-page inventory; unmerged branches reused |
| E0-04 | Application shell and design system | M0 | Finish: breakpoints 1280/900/720/420, 44 px targets, reduced motion, friendly 403/404/500 with correlation ID, `docs/UI-DESIGN.md` (E0-04 AC5) |
| E0-09 | Public landing page `/` | M0 / M2 | Final content in M2; HTML contains no demo password and no case data |
| E1-04 | Role-aware navigation | M1 | Uses db capabilities; each of the 26 pages reachable for at least one role; forbidden direct navigation shows the 403 page and logs a denied event; disabled actions explain why (server still enforces) |
| E2-05 (UI part) | Conflict panel and reload flow | M1 | Per-field "yours vs current", Reload / Re-apply, live-region announcement |
| E4-03 | Comments | M1 | |
| E6-05 | Reservations (commands) | M1 | T6/T7 via the db engine; calendar push is ext-svc's |
| E7-02 | Permit issuance | M1 | T10, immutable `PermitVersion`, server PDF with QR and typed code stored via ext-svc `IObjectStore`, notification queued through the outbox in the same transaction, severe-weather acknowledgement guard |
| E8-04 | Case closure (UI) | M1 | Guard from db; show blocking incidents |
| E2-07 (UI part) | Cancel action | M1 | Reason dialog; shows released side effects |
| E13-02 (headers) | Security headers and antiforgery | M3 | CSP without inline script where feasible, `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, HSTS only in TLS mode; antiforgery on HTTP and Blazor paths; request limits |
| E16-03 (app) | OpenAPI for the app API | M2 | Exported to `docs/api/`; a test (test-auto) fails when out of date |
| G-01 (new) | Git workflow and PR conventions | M0 | Section 6 |
| G-02 (new) | CODEOWNERS and integration duty | M0 | Section 6 |

## 6. Git integration stories (new)

### G-01 Git workflow and PR conventions · M0

As a team, we want one predictable Git flow, so five engineers can merge without stepping on each other.

- AC1 `docs/GIT-WORKFLOW.md`: branch `<boundary>/<story-id>-<slug>`; commit message `<story-id>: <summary>` (plus the attribution trailer the session requires); one story per PR, small PRs, rebase on `main` before opening; squash-merge with the same title; delete the branch after merge; never commit secrets, `.data/`, `.terraform/`, state or plan files; no force-push to shared branches.
- AC2 `.github/pull_request_template.md` checklist: story ID and ACs; owned paths only; tests run with results; for pages: axe clean, 390 px and 1280 px checked, no placeholder text; `docs/CONFIGURATION.md` updated; STATUS updated; requests opened or answered; screenshots attached.
- AC3 `.gitignore`/`.gitattributes` reviewed with infra: no generated or secret files tracked (see master DoD 6.2 item 4).
- AC4 Rule applied to your own work from the first PR.

### G-02 CODEOWNERS and integration duty · M0, continuous

- AC1 `.github/CODEOWNERS` encodes the ownership map in `../README.md` (path → boundary owner). The GitHub handles are **pending**: the user supplies them; until then use placeholders and note it in STATUS.
- AC2 You are the integration owner. When PRs are open from several boundaries, you merge in dependency order (db → ext-svc → ui-ux → test-auto → infra where they touch the same area), only after the required check `pr-validation / required` is green. Merging needs the user's go-ahead for the first time and whenever settings are involved; afterwards follow the user's standing instruction.
- AC3 You resolve conflicts in shared files (`ServiceSetup.cs`, `PageCatalog.cs`, `docs/*`) by keeping both sides' rows, never by dropping one. If a conflict reveals a contract problem, open a request to the owning boundary.
- AC4 After each merge, confirm the Compose stack is still healthy on `main` (`tools/Dev up`, `smoke`) and tell test-auto to rerun the full suites.
- AC5 Propose (as a request to infra) an optional guard in `check-guards.sh`/workflow that fails a PR touching files outside its branch-prefix boundary, except shared files.

## 7. Cross-boundary rules for the UI

- Every state-changing action goes through an application-service method that is also an `/api/...` endpoint; authorization, validation, guards and audit live in that method, never only in a component.
- One shared validation rule set for client and server; errors link to fields; focus moves to the first invalid field; live regions announce save, validation and conflict messages.
- No hard-coded role checks in components: ask db's capability helpers. Hide or disable with a reason, but trust only the server.
- No external assets or CDN: Chart.js and any map base are vendored in `wwwroot`. Charts have a data-table alternative and a text summary.
- Status chips always carry text. Tables become labelled cards on phones. Print stylesheet for the roster run-of-show.
- CSV exports neutralise formula injection and mask fields per role (A-21).
- Run the placeholder scan (`TODO`, `lorem`, `href="#"`, buttons without handlers) before declaring a page done.

**Test hook sheet.** For every finished page add a short entry to STATUS: route, roles, seeded data used, stable selectors (accessible names or `data-testid`), key states (empty, error, conflict, success). test-auto builds the browser and axe tests from it. If you need a quick smoke before they are ready, put it in `tests/EventPermits.BrowserTests/Pages/<Page>Tests.cs`; test-auto adopts it.

## 8. Order of work

1. **M0:** E-UI-00, G-01, G-02, E0-04 finish, E0-09, login UI check.
2. **M1:** E4-01, E4-02 basic, E4-03, E4-04 basic, E5-01, E5-03, E6-01 basic, E6-05, E7-01, E7-02, E8-02, E8-04, E1-04, E2-05 UI, cancel action. Each page waits only on the db/ext-svc contract it lists; if blocked, build with the contract's interface and a request, then continue.
3. **M2:** remaining pages (01, 07, 08, 09, 11, 12, 13, 15, 16, 17, 18), complete versions of 02/04/17, E16-03, landing final content.
4. **M3:** E13-02 headers/antiforgery; fix defects from the failure matrix and accessibility sweeps.
5. **M4:** responsive and polish fixes from test-auto's review (E13-04), README demo-account table input to infra, final UI-DESIGN, screenshots support.

## 9. Boundary Definition of Done (adds to master 6.2)

1. Every AC proven: service-level integration tests (`tests/.../Ui/**`) for commands; the page's browser and axe tests exist (yours or test-auto's) and pass.
2. Crafted-request test for each new endpoint: allowed role passes; a forbidden role gets 403/404.
3. Each page has persistence, client and server validation, meaningful empty and error states, a 409 path where it edits, and passes the placeholder scan.
4. `docs/CONFIGURATION.md` and the test hook sheet updated; STATUS updated; PR follows `docs/GIT-WORKFLOW.md`.

## 10. Risks

| Risk | Mitigation |
| --- | --- |
| Pages become shallow cards and forms (brief fail rule) | DoD requires real actions, tests per page, placeholder scan |
| Waiting on db/ext-svc contracts | Code against ports; requests; independent pages first |
| Merge conflicts in shared files | Per-boundary DI extension methods; integration owner rules (G-02) |
| Blazor circuit drops during deploys | Reconnect UI; sticky sessions (infra); manual deploys |
| Lost unmerged page work | E-UI-00 inventory of branches/worktrees before writing |
