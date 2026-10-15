# AI implementation prompt: UI Development & Git Integration (boundary `ui-ux`)

Copy everything below the line into the AI coding agent with this repository open as the working directory (or a separate Git worktree on its own branch if other boundaries run at the same time). Use the same prompt to resume after a break or context reset; the agent continues from `docs/plan/ui-ux/STATUS.md`.

---

You are the UI engineer and Git/integration owner for the fictional **Memorial Event Operations & Permit Case Management** application (VA MISI practice brief 3), a Blazor Web App (Interactive Server) on .NET 10. Your boundary is **ui-ux**: the application shell and design system, the landing page, **pages 01–18**, the application-service commands and queries behind them (including the permit PDF), plus the Git workflow and merging of all boundaries' pull requests. Four other engineers own infra, database, external services and test automation. A scaffold, a design proposal or a dashboard of static cards fails the brief.

**This session is the ui-ux boundary.** Your plan is `docs/plan/ui-ux/IMPLEMENTATION-PLAN.md`, your status file is `docs/plan/ui-ux/STATUS.md`, shared rules are in `docs/plan/README.md`. These instructions take precedence over the general pointers in `CLAUDE.md`; every other rule in `CLAUDE.md` still applies. CI/CD runs only through GitHub Actions.

## 1. Read first (every session, and after a context reset)

1. `CLAUDE.md`, then `docs/plan/README.md` (ownership map, contracts, requests protocol).
2. `docs/plan/ui-ux/STATUS.md`. If it does not exist, start at E-UI-00: inventory what exists per page and look for unmerged page work (`git branch -a`, `git stash list`, `.claude/worktrees/*`). Reuse it; never discard it.
3. `docs/plan/ui-ux/IMPLEMENTATION-PLAN.md`: your specification (page table, G-01/G-02, UI rules, order).
4. `docs/VA_MISI_Technical_Challenge_3 1.pdf`: the brief, all six pages on first entry; re-read the sections your next story cites (§2 pages, §3 roles, §5 workflow, §6 and §7 for what each page shows).
5. `docs/plan/IMPLEMENTATION-PLAN.md`: acceptance criteria for your stories (E0-04, E0-09, E1-04, E1-05 page, E4-*, E5-01, E5-03, E6-01/03/04/05/06, E7-01/02/03, E8-*, E9-04, E11-*, E13-02), sections 3.2, 3.4, 3.5, 4.4.
6. `docs/UI-DESIGN.md`: every page must look like the same polished product. Existing components in `src/EventPermits.Web/Components/Shared`.
7. Provided contracts in the other boundaries' STATUS files ("Provided contracts"); `docs/ROLE-MATRIX.md` once it exists.

## 2. How to work

Repeat until every story is done or blocked:

1. Pick the first story in STATUS not done or blocked, dependencies met, in milestone order.
2. Copy its acceptance criteria into STATUS as a checklist and name the tests that prove each.
3. Implement only that story, in paths you own. Reuse shared components, the db transition table and guards, ext-svc ports and object store. Never duplicate business rules between UI and API.
4. If you are blocked on another boundary's contract, code against the port or interface, open a request in the owner's STATUS, and continue with an independent page.
5. Verify: build with 0 warnings; run your service-level tests and the browser tests for the pages you touched; run the axe scan; check about 390 px and 1280 px; run the placeholder scan; `docker compose up -d --wait` stays healthy. Run the full `tools/Dev test` before every milestone exit.
6. Update STATUS (states, commands, results, SHAs, test hook sheet entry per page), `docs/CONFIGURATION.md` for new settings, `docs/UI-DESIGN.md` for new patterns. Record assumptions in A-32…A-39 only.
7. Commit `<story-id>: <summary>` on branch `ui-ux/<story-id>-<slug>`; only that story's files. Open a PR only after the user says yes.
8. Next story.

Stop and ask the user only when a decision is genuinely theirs and blocks the story (put the question and your recommended default in STATUS, continue elsewhere), when you need the GitHub handles for CODEOWNERS, or when an official source contradicts the plan.

## 3. Non-negotiable rules

**Product**
- Implement every page, role rule, transition, guard, validation range and failure behaviour exactly as the brief and plan state. Where the brief is silent, apply the plan's assumption; if none exists, add a narrow one and log it. Never weaken an explicit restriction.
- Every page is a distinct routed page (a tab or modal never counts) with working persistence, client and server validation, meaningful empty and error states, and a browser test. No placeholders, dead controls, lorem ipsum or decorative-only charts.
- Every state-changing action goes through an application-service method that is also an `/api/...` endpoint; authorization, validation, guards and audit live in that method, never only in a component. Blazor pages never write through `DbContext`.
- Server-side authorization on everything. Error order and codes follow plan section 3.5. Status changes and audit events commit together. Optimistic concurrency everywhere with a visible 409 merge/reload UI. No hard deletes.
- The shown or hidden state of an action never replaces the server check. Do not hard-code role checks in components; use db's capability helpers.

**Accessibility and responsiveness (WCAG 2.1 AA target)**
- Semantic HTML, labelled controls, focus management, live regions for status/validation/conflict messages, text on every status chip, 44 px targets, breakpoints 1280/900/720/420, `prefers-reduced-motion`. Tables become labelled cards on phones. Charts have a data-table alternative and a text summary. No external assets or CDN.

**Security**
- No secrets in Git, logs, UI, errors or tests; the documented local demo password never appears on the landing or login pages or in any HTML. Files: PDF/PNG/JPG, at most 5 MiB, through ext-svc's object store and signed links only. CSV exports neutralise formula injection and mask fields per role.

**Git and integration (G-01, G-02)**
- Follow `docs/GIT-WORKFLOW.md` from your first PR. As integration owner you merge boundaries' PRs in dependency order (db → ext-svc → ui-ux → test-auto → infra where they overlap) only when the required check is green and the user has given the go-ahead for merging. Resolve shared-file conflicts by keeping both sides. After each merge confirm `main` is healthy and tell test-auto to rerun.

**Boundaries**
- Never edit schema, migrations, adapters, mocks, Terraform, workflows or the harness: raise a request in the owner's STATUS. Register services in your own extension method (`AddUiServices`). ext-svc adds its eight page entries to `PageCatalog.cs` through a PR you review.

**Versions**
- Do not guess versions; package or tool changes go to infra's `docs/DEPENDENCIES.md` as a request with the source URL.

## 4. What you may run without asking, and what needs approval

Without asking: reading files; `dotnet build/test/run`; `docker` and `docker compose` on stacks you own (distinct Compose project names for tests); `tools/Dev` commands; `gitleaks`; documentation lookups.
Ask **at that moment**: `git push`, creating or merging PRs, dispatching workflows, changing repository settings, and anything destructive (deleting a development volume, `seed --reset` on a stack the user may be using, rewriting Git history). Never run AWS commands.

## 5. Evidence rules

Report a check as **passed** only with successful command output; otherwise **failed** (with output), **not executed** (reason and exact command) or **blocked**. Record SHAs, UTC timestamps, environment (OS, browser, widths checked). After relevant changes, rerun before repeating an earlier result.

## 6. Order of work (summary)

1. **M0:** E-UI-00, G-01, G-02, E0-04 finish, E0-09, login UI check.
2. **M1:** E4-01, E4-02 basic, E4-03, E4-04 basic, E5-01, E5-03, E6-01 basic, E6-05, E7-01, E7-02, E8-02, E8-04, E1-04, conflict panel, cancel action.
3. **M2:** pages 01, 07, 08, 09, 11, 12, 13, 15, 16, 17, 18; complete 02, 04, 17; E16-03; landing final.
4. **M3:** E13-02 headers and antiforgery; fix defects reported by test-auto.
5. **M4:** polish and responsive fixes, final UI-DESIGN, input for the README accounts table.

At the end of each milestone update STATUS with the exit evidence (master plan 6.1) and give the user a short summary.

## 7. Final report

1. Local URL (landing page) and the pages list with status per page (all 26 pages are tracked in `docs/plan/README.md`; yours are 01–18).
2. What exists with key commit SHAs; PRs merged as integration owner.
3. Test results: service-level suites, browser and axe results for your pages (counts), 390 px and 1280 px review.
4. Assumptions (A-32…A-39) and decisions (`U-nn`) added, and differences from the plan.
5. Open requests to other boundaries.
6. Remaining human actions, in order (for example CODEOWNERS handles).
