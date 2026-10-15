# Test Automation & Validation: plan (boundary `test-auto`)

Owner: one test automation engineer (SDET). Read [`../README.md`](../README.md) first (ownership, contracts, requests protocol). Story acceptance criteria (AC) live in the master [`../IMPLEMENTATION-PLAN.md`](../IMPLEMENTATION-PLAN.md); this file lists what you own and the order to work in. Progress goes in `docs/plan/test-auto/STATUS.md`.

## 1. Mission and scope

1. Develop the automated test scripts: harness, browser (Playwright + axe), end-to-end journeys, failure matrix, security and accessibility suites.
2. Validate functionality against the brief and the master plan, and produce the evidence (test report, traceability, screenshots).
3. **Identify integration issues** between the other four boundaries: run the whole stack after every merge, find contract breaks, and file precise defect requests.

You do not fix product code. A failing test is a request to the owning boundary, never a reason to weaken the test or to patch someone else's files.

Division of testing (important):

| Test kind | Written by |
| --- | --- |
| Unit and service-level integration tests for a story (`tests/EventPermits.UnitTests/<Boundary>/**`, `tests/EventPermits.IntegrationTests/<Boundary>/**`) | the story's owning boundary |
| Harness (`Support/`), browser tests for **all 26 pages**, axe sweeps, journeys, failure matrix, security checks, agreement tests, remote smoke, report, screenshots | **test-auto** |

## 2. Baseline (verify against code; do not rebuild)

Exists per the 2026-10-15 snapshot: `tests/EventPermits.IntegrationTests` (Testcontainers PostgreSQL + object storage + in-process mocks; 18 passing tests including the T1–T13 journey), `tests/EventPermits.BrowserTests` (`ShellTests.cs`, `Support/BrowserFixture.cs`), `tests/EventPermits.UnitTests` (placeholder only), `tools/Dev` with `test`, `smoke`, `scan`, `screenshots` commands, `TestResults/` gitleaks output. Not written: the unit suites per boundary, page browser tests, axe sweep, failure matrix, report, traceability. Your first task (E-TA-00) is to reconcile and write STATUS.

Your files: `tests/**/Support/**`, `tests/EventPermits.BrowserTests/**`, `tests/**/Journeys/**`, `tests/**/Security/**`, `docs/test-report/**`, `docs/screenshots/**`, `docs/{REQUIREMENTS-TRACEABILITY,DEMO-RUNBOOK,ACCESSIBILITY-CHECKLIST,KNOWN-LIMITATIONS}.md`. In `tools/Dev` you may edit only `TestCommand.cs`, `Smoke.cs`, `Scan.cs` (infra owns the rest); the README ownership table is updated to match.

## 3. Decisions and assumptions you carry

A-63 (CI browser suite = Chromium on Ubuntu, not a Windows/Edge check), A-29 (seed anchored; use an injected `TimeProvider`, never the wall clock). New assumptions A-70…A-79; decisions `T-nn`.

## 4. Stories you own

| Story | Title | Milestone | Notes |
| --- | --- | --- | --- |
| E-TA-00 (new) | Reconcile and STATUS file | M0 | Test inventory per boundary and story |
| E0-06 | Test harness and report | M0 | Three suites run with one command (`tools/Dev test`), nonzero exit on any failure **or missing suite**, discovered/executed counts printed; Testcontainers (PostgreSQL 18, object storage) and in-process mocks, no EF InMemory; Playwright Chromium pinned with local axe-core; TRX → HTML/Markdown report in `docs/test-report/` |
| T-01 (new) | Per-page browser and axe tests (26) | M1–M2 | For every page: loads for allowed roles, forbidden role gets the 403 page, real data shown, primary action works and persists, validation errors, empty and error states, keyboard path, axe (wcag2a, wcag2aa, wcag21a, wcag21aa) 0 violations, 390 px and 1280 px, no placeholder text. Built from each owner's **test hook sheet** |
| T-02 (new) | Integration validation after merges | continuous | After each merge to `main`: fresh `tools/Dev up`, `smoke`, full suites; compare with the previous result; file defect requests (section 6) |
| T-03 (new) | Agreement and contract tests | M2 | App seed vs mock reference data (E12-02 AC2); seeded accounts vs `tools/Dev` demo accounts vs README table; committed OpenAPI vs generated (E16-03); every endpoint has a policy (with db's E1-02 test) and every page in `PageCatalog.cs` has a browser test; ERD/WORKFLOW freshness (db's tests) run in the suite |
| E12-03 | Required live journey | M1 happy / M4 complete | Brief §9 end to end with the seeded roles: wizard → reviewer requests info → coordinator responds → reviewer passes → scheduling hits a collision and picks a free slot → allocates resources → manager approves → downloads PDF permit → attendant checks in → logs an incident → manager resolves and closes → auditor exports report. Separate tests: 403, 422, 409, notification retry |
| E12-04 | Integration demo extension | M3 | One test per brief §8 demo item (registry with API key; OAuth calendar conflict; notification + signed webhook; PDF via expiring link; stale weather offline; CSV with bad rows; geocode candidate selection; Hub correlated failure logs); each asserts mock call counts increased |
| E12-05 | Demo runbook | M4 | `docs/DEMO-RUNBOOK.md`: timed script, scenario switches, accounts, reset steps, fallback plan |
| E13-01 | Failure matrix (brief §7.3) | M3 | One test per row: registry 429; OAuth expiry and `invalid_client`; calendar 409; webhook duplicates and forgery; storage link expiry; weather timeout; CSV bad rows; geocoder ambiguous and offline. Each asserts the user-visible recovery and a traceable log/attempt with correlation ID. Brief §8 checklist explicit in the traceability matrix, including a browser test for each of pages 19–26 showing live mock results (asserting call counts) |
| E13-02 (verification) | Security checks | M3 | Gitleaks over history and working tree; `dotnet list package --vulnerable` clean; Trivy image scan with no fixable CRITICAL; headers test; antiforgery tests (HTTP and Blazor); log-capture test proving secrets never appear in logs, attempts, health, UI or errors; crafted-request sweep across all endpoints (allowed role passes, forbidden role 403/404). Headers and antiforgery **code** are ui-ux's |
| E13-03 | Accessibility | M2 / M4 | Axe on all 26 pages, landing, login and key states (validation error, conflict panel, empty, error): 0 violations; keyboard-only journeys (wizard, calendar, check-in, approval); live-region assertions; `docs/ACCESSIBILITY-CHECKLIST.md` manual review (screen reader, 200 % zoom, contrast) kept separate from automated evidence |
| E13-04 | Responsive and polish review | M4 | Screenshots at 390 px and 1280 px for all pages; no horizontal scroll on phones; automated scan for `TODO`, `lorem`, `href="#"`, buttons without handlers |
| E13-05 | Clean-machine verification | M4 | Fresh clone in a new directory: README-only steps for install, migrate, seed, run, test succeed on macOS; Windows steps recorded as *not executed* if no Windows machine |
| Remote smoke | `Category=RemoteSmoke` subset | M0 / M2 | Login per role, all 26 routes reachable, one permit download; honours `E2E__BaseUrl`; credentials from a file; infra's `deploy.yml` runs it |
| E16-04 | Test report and traceability | M4 | Counts per suite, environment, commit; matrix complete with code locations, named tests and evidence for every brief row (master section 10) |
| E16-05 | Screenshots of all 26 pages | M4 | `tools/Dev screenshots` desktop and phone with seeded data, including integration pages, attempt detail, landing and login |
| E16-06 | Known limitations | M4 | Honest list (HTTP-only dev in AWS, signature checks are not antivirus, SFTP simulated, Windows not verified if so) |

## 5. Test design rules

- Deterministic: no `Thread.Sleep`; wait on conditions; injected `TimeProvider`; seed anchor date; seeded randomness. A test that depends on the wall clock or on test order fails review.
- Isolated: distinct Compose project names and ports for each stack you start; never touch the developer's running stack; clean up what you start.
- Real dependencies: PostgreSQL 18 and the object-storage emulator via Testcontainers; mocks started in-process or as processes; **no EF InMemory**.
- Prove real requests: assert mock call counts through `GET /_admin/stats` before and after the action.
- Assert user-visible outcomes plus a traceable correlation ID in logs/attempts for failures.
- Every browser test uses accessible roles and names first, `data-testid` second; no brittle CSS selectors.
- Never skip a test silently. A skipped or quarantined test needs a STATUS entry with reason, owner and exit date; the runner prints discovered vs executed counts and fails on a missing suite.
- Test data and screenshots must contain no secrets; the documented local demo password appears only in test configuration.

## 6. Defect protocol (integration issues)

When a test fails because of product behaviour or a contract break:

1. Confirm it reproduces on a clean stack (`tools/Dev down` with volumes only for your own project, then `up`).
2. Add an entry to the **target boundary's** STATUS under "Requests from other boundaries": title, severity (blocker / major / minor), failing test name, steps, expected vs actual, correlation ID or log line, commit SHA, requester `test-auto`, date.
3. Keep the failing test in the suite, marked with the request ID in a trait or comment, until the owner closes it. Do not weaken the assertion.
4. Track open defects per boundary in your STATUS ("Open defects"); report them in the milestone summary.

## 7. Order of work

1. **M0:** E-TA-00, E0-06 (three-suite runner, report skeleton, Playwright + axe pinned), remote smoke skeleton.
2. **M1:** T-01 for the M1 pages as they land, E12-03 happy path, T-02 after every merge.
3. **M2:** T-01 for all remaining pages, T-03, E13-03 sweep, mock-seed agreement, OpenAPI freshness.
4. **M3:** E13-01, E12-04, E13-02 verification; coordinate fixes with owners.
5. **M4:** E12-03 complete, E12-05, E13-04, E13-05, E16-04, E16-05, E16-06; final report and traceability.

## 8. Boundary Definition of Done (adds to master 6.2)

1. Every story's ACs are mapped in `docs/REQUIREMENTS-TRACEABILITY.md` to a named, passing test (or recorded manual evidence) with its command and result.
2. All 26 pages have browser and axe tests; the journey and the failure matrix pass on a clean Compose stack; the remote smoke subset passes against any base URL.
3. `tools/Dev test` exits nonzero on failure or on a missing suite and prints discovered/executed counts; the HTML/Markdown report is generated.
4. Open defects are zero, or listed with owner, severity and reason in the final report.
5. No flaky test is accepted: a test that fails once in three clean reruns is fixed or quarantined with a request.

## 9. Risks

| Risk | Mitigation |
| --- | --- |
| Test suite becomes the bottleneck for page delivery | Hook sheets from owners; owners may add a smoke test and you adopt it; T-01 runs per page as they land |
| Flaky browser tests | Condition-based waits, isolated stacks, retry-free policy, quarantine protocol |
| Long CI duration | Parallel suites, cached Playwright browsers (infra caches keyed by lock files) |
| Test data drifts from seed | Seed anchor and counts asserted in tests; agreement tests (T-03) |
| Defects argued between boundaries | Precise defect template with reproduction, SHA and correlation ID |
