# AI implementation prompt: build and run the Brief 3 application locally (app track)

Copy everything below the line into the AI coding agent, with this repository open as the working directory. Use the same prompt to resume after a break or a context reset; the agent continues from `docs/plan/app-only/STATUS.md`. AWS deployment is a separate track (`docs/plan/deploy-infra-only/`).

---

You are the lead engineer for the fictional **Memorial Event Operations & Permit Case Management** application (VA MISI practice brief 3). Build the complete working application in .NET 10 (the user's decision), with source, migrations, seed data, six mock external services, tests, local tooling, Dockerfiles, `compose.yml` and documentation, and run it locally. A scaffold, a design proposal or a dashboard of static cards fails the brief. Continue through verification.

**This session is the app track.** These instructions are the user's latest and take precedence over the general pointers in `CLAUDE.md`: your plan is `docs/plan/app-only/IMPLEMENTATION-PLAN.md` and your status file is `docs/plan/app-only/STATUS.md` (not `docs/plan/IMPLEMENTATION-PLAN.md` or `docs/plan/STATUS.md`). All other rules in `CLAUDE.md` still apply. Do not create or edit `infra/`, `pipelines/` or `docs/aws/`, and do not run anything that needs AWS credentials.

## 1. Read first (every session, and again after a context reset)

1. `CLAUDE.md`: stable rules, source hierarchy, action boundaries.
2. `docs/plan/app-only/STATUS.md`: your progress record. If it does not exist, you are at E0-01.
3. `docs/VA_MISI_Technical_Challenge_3 1.pdf`: the brief. Read all six pages on first entry. After a reset, re-read the sections the next story cites. If extraction is truncated, retrieve the missing part before relying on it.
4. `docs/plan/app-only/IMPLEMENTATION-PLAN.md`: **your specification.** It covers the deployability contract (section 1), decisions, product summary (26 pages, roles, transitions T1–T14, error contract), architecture, assumptions A-01…A-29, milestones M0–M4, epics E0–E13 and E16 with acceptance criteria, Definition of Done, seed counts, traceability and the local release checklist.
5. For tooling patterns (the `tools/Dev` runner, `init-local`, `probe`, Compose health checks, Playwright/axe setup), look at `../../code-challenge-2/misi-code-challenge-wed/` and adapt. Never copy its product rules, counts, results or versions as facts about this project.
6. If `docs/plan/deploy-infra-only/STATUS.md` exists, read its "Requests to the app track" section: those are contract gaps the deploy track found. Treat each as a story inside E0-08.

## 2. How to work

Repeat until every story is done or blocked:

1. Pick the first story in STATUS.md that is not done or blocked and whose dependencies are done, in milestone order (M0 → M4).
2. Copy its acceptance criteria into STATUS.md as a checklist. Name the tests that will prove each one.
3. Implement only that story. Keep changes small. Reuse the shared components (E0-04), the transition table (E2-03), the adapter pipeline (E3-02) and the object store (E3-04). Never duplicate business rules between UI and API.
4. Verify: build with 0 warnings; run the unit and integration suites and the browser tests of the pages you touched; for pages, run the axe scan and check about 390 px and 1280 px widths; confirm `docker compose up -d --wait` is still healthy. Run the full `tools/Dev test` at the end of every epic and before every milestone exit.
5. Meet the plan's Definition of Done (section 6.2), including "the deployability contract still holds". Update STATUS.md, the traceability matrix, the implementation log and any affected docs (including `docs/CONFIGURATION.md` for every new setting). Record new assumptions in the range **A-30 to A-59** only.
6. Commit with the message `<story-id>: <summary>` and the attribution trailer this session requires. Commit only that story's files. Never commit secrets, `.data/`, test output with credentials, or local settings.
7. Move on to the next story.

Stop and ask the user only when:
- a decision is genuinely the user's and blocks the current story (put the question and your recommended default in STATUS.md, then continue other work);
- an official source contradicts the plan (for example a version is unavailable, or the MinIO image is no longer published). Quote the source and propose the smallest change.

## 3. Non-negotiable rules

**Product**
- Implement every page, role rule, transition, guard, validation range, integration contract and failure behaviour exactly as the brief and plan state. Where the brief is silent, apply the plan's assumption; if none exists, add a narrow one and log it. Never weaken an explicit restriction to make something easier.
- Every page is a real routed page with working persistence, client and server validation, meaningful empty/error states, and a browser test. Run the placeholder scan from E13-04 before declaring any page done. Follow `docs/UI-DESIGN.md` (E0-04 AC5) so every page looks like the same polished product.
- Every state-changing UI action goes through an application-service method that is also exposed as an `/api/...` endpoint; authorization, validation, guards and audit live in that method, never only in a component.
- Server-side authorization on everything, with crafted-request tests. Error order and codes follow plan section 3.5.
- Status changes and audit events commit together. Optimistic concurrency everywhere, with a visible 409 merge/reload UI. No hard deletes.

**Integrations**
- One adapter pipeline for all outbound HTTP: correlation ID, credentials from configuration only, timeouts, transient-only retries (404 never retried, `Retry-After` honoured), circuit breaker, sanitised IntegrationAttempt records.
- Mocks are independent processes with scenario modes (Scenario Panel and `Mock__Mode`), call counters and an admin key. Tests assert mock call counts to prove real requests.
- Webhooks: HMAC with constant-time comparison, 5-minute tolerance, idempotent by `eventId`, no status regression. Sending goes through the outbox and worker, never inline in permit issuance.

**Deployability (the hand-off to the deploy track)**
- Keep every item in plan section 1 true at all times: same images everywhere, all settings configurable with environment-variable override, secrets accepted as environment variables, no hard-coded host names, S3-compatible storage only (no local-disk file storage), Data Protection keys in PostgreSQL, jobs only in `worker` with advisory locks, logs to stdout, `probe` and `migrate` commands working inside the image.

**Security**
- No secrets in Git, images, logs, test output, UI, health or errors. Local secrets are generated by `init-local` into ignored files. The one deliberate exception is the fixed local demo password `EventPermits!Demo2026` (OD-2): it appears only in the README, `compose.yml` and test configuration, is enabled only by `Seed:AllowDemoPassword=true`, is allowlisted in Gitleaks with the reason, and never appears on the landing or login pages.
- Files: PDF/PNG/JPG, at most 5 MiB, extension + MIME + magic bytes, private storage, random keys, checksum, role-checked short-lived links (A-16), audited.

**Versions**
- Verify the latest stable compatible versions from official sources and pin them exactly (global.json, central packages with lock files, image digests, tool checksums). Record source and date in `docs/DEPENDENCIES.md`. Never guess.

## 4. What you may run without asking, and what needs approval

Without asking: reading files; `dotnet` build/test/run; `docker` and `docker compose` against local stacks (only stacks this runner owns: use distinct Compose project names for tests); `tools/Dev` commands; `gitleaks`; `trivy image`; documentation lookups.

Work on `main` in this working tree unless the user says otherwise. If the deploy track runs at the same time, it uses its own Git worktree and branch; never edit its files. Once the branch policy on `main` exists (deploy track, setup guide), deliver through pull requests instead of direct commits to `main`.

Ask **at that moment**: pushing to Azure Repos, creating pull requests, and anything destructive outside this track's own test resources (deleting volumes of the development stack, resetting seeded data the user may be using, rewriting Git history). Never run AWS commands in this track.

## 5. Evidence rules

- Report a check as **passed** only with successful command output. Otherwise it is **failed** (with output), **not executed** (with reason and the exact command) or **blocked** (with what is missing). Never merge these states.
- Record commit SHAs, UTC timestamps and environment (OS, architecture, Docker, browser). Link or summarise logs; do not paste them whole.
- After relevant code or configuration changes, rerun the checks before repeating an earlier result.

## 6. Order of work (summary)

1. **M0 Local skeleton:** E0-01…E0-06, E0-09 (landing page skeleton), E1-01, E3-01 (registry mock only), E0-08 deployability contract. Exit: a clean checkout runs with `docker compose up -d --wait`, users can sign in, `tools/Dev test` is green. Tell the user M0 is done, because the deploy track can start then.
2. **M1 Thin end-to-end journey:** E2-*, E1-02…E1-04, E3-02, E3-04, E3-06, the basic versions of E4, E5, E6, E7 and E8 stories, E9-01/02, E12-01 core, E12-03 happy path.
3. **M2 All 26 pages complete**, with every integration in normal mode.
4. **M3 Failure matrix:** every scenario mode, E13-01, E12-04.
5. **M4 Local release:** full seed, docs, screenshots, test report, clean clone, local release checklist (plan section 11).

At the end of each milestone, update STATUS.md with the milestone exit evidence (plan section 6.1) and give the user a short summary.

## 7. Final report

When everything is done or blocked, give the user:
1. Local URL (landing page) and the README's demo accounts table (user names, roles, the local demo password).
2. What exists (pages, services, commands) with key commit SHAs.
3. Test results per suite with counts, plus the failure-matrix and demo-journey results.
4. The local release checklist (plan section 11) with passed / failed / not executed / blocked per item.
5. The deployability contract status and any open requests from the deploy track.
6. Assumptions added and decisions that differ from the plan, with reasons.
7. Remaining human actions, in order.
