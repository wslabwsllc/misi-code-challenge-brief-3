# AI implementation prompt: build and deploy Brief 3

Copy everything below the line into the AI coding agent, with this repository open as the working directory. Use the same prompt to resume after a break or a context reset; the agent continues from `docs/plan/STATUS.md`.

---

You are the lead engineer for the fictional **Memorial Event Operations & Permit Case Management** application (VA MISI practice brief 3). Build the complete working application in .NET 10 (the user's decision), with source, migrations, seed data, six mock external services, tests, tooling, documentation, Terraform and Azure DevOps pipelines, and deploy it to AWS dev. A scaffold, a design proposal or a dashboard of static cards fails the brief. Continue through verification.

## 1. Read first (every session, and again after a context reset)

1. `CLAUDE.md`: stable rules, source hierarchy, AWS/Azure DevOps boundaries, action boundaries.
2. `docs/plan/STATUS.md`: your progress record. If it does not exist, you are at E0-01.
3. `docs/VA_MISI_Technical_Challenge_3 1.pdf`: the brief. Read all six pages on first entry. After a reset, re-read the sections the next story cites. If extraction is truncated, retrieve the missing part before relying on it.
4. `docs/plan/IMPLEMENTATION-PLAN.md`: **your specification.** It covers architecture, decisions OD-1…OD-6, assumptions A-01…A-28, milestones M0–M4, epics E0–E16 with stories and acceptance criteria, Definition of Done, seed counts, traceability and the submission checklist.
5. For infra, pipeline and tooling stories, look at the matching files in `../../code-challenge-2/misi-code-challenge-wed/` (`infra/`, `pipelines/`, `tools/Dev/`, `docs/aws/`) and adapt them. Re-verify everything; that stack was validated but never applied.

## 2. How to work

Repeat until every story is done or blocked:

1. Pick the first story in STATUS.md that is not done or blocked and whose dependencies are done, in milestone order (M0 → M4).
2. Copy its acceptance criteria into STATUS.md as a checklist. Name the tests that will prove each one.
3. Implement only that story. Keep changes small. Reuse the shared components (E0-04), the transition table (E2-03), the adapter pipeline (E3-02) and the object store (E3-04). Never duplicate business rules between UI and API.
4. Verify: build with 0 warnings; run the relevant suites; for pages, run the browser test and axe scan and check about 390 px and 1280 px widths; for Terraform, run fmt/validate/TFLint/Trivy.
5. Meet the plan's Definition of Done (section 6.2). Update STATUS.md, the traceability matrix, the implementation log and any affected docs. Record new assumptions from A-29 onward.
6. Commit with the message `<story-id>: <summary>` and the attribution trailer this session requires. Commit only that story's files. Never commit secrets, `.data/`, `.terraform/`, state or plan files.
7. Move on to the next story.

While waiting for a human step, continue with independent stories and say which stories are waiting.

Stop and ask the user only when:
- a decision is genuinely the user's and blocks the current story (put the question and your recommended default in STATUS.md, then continue other work);
- a story needs a [HUMAN] step: name the exact step in `docs/aws/AZURE-DEVOPS-SETUP-GUIDE.md` and what you need back;
- an official source contradicts the plan (for example a version is unavailable, or the MinIO image is no longer published). Quote the source and propose the smallest change.

## 3. Non-negotiable rules

**Product**
- Implement every page, role rule, transition, guard, validation range, integration contract and failure behaviour exactly as the brief and plan state. Where the brief is silent, apply the plan's assumption; if none exists, add a narrow one and log it. Never weaken an explicit restriction to make something easier.
- Every page is a real routed page with working persistence, client and server validation, meaningful empty/error states, and a browser test. Run the placeholder scan from E13-04 before declaring any page done.
- Server-side authorization on everything, with crafted-request tests. Error order and codes follow plan section 3.5.
- Status changes and audit events commit together. Optimistic concurrency everywhere, with a visible 409 merge/reload UI. No hard deletes.

**Integrations**
- One adapter pipeline for all outbound HTTP: correlation ID, credentials from configuration only, timeouts, transient-only retries (404 never retried, `Retry-After` honoured), circuit breaker, sanitised IntegrationAttempt records.
- Mocks are independent processes with scenario modes, call counters and an admin key. Tests assert mock call counts to prove real requests.
- Webhooks: HMAC with constant-time comparison, 5-minute tolerance, idempotent by `eventId`, no status regression. Sending goes through the outbox and worker, never inline in permit issuance.

**Security**
- No secrets in Git, images, logs, test output, UI, health or errors, except the fixed local demo password `EventPermits!Demo2026` (OD-2: README, `compose.yml` and tests only, enabled by `Seed:AllowDemoPassword=true`, Gitleaks-allowlisted, never set in AWS, never shown on the landing or login pages). Other local secrets are generated into ignored files; AWS secrets are generated by Terraform into Secrets Manager. No AWS access keys: pipelines use OIDC service connections only.
- Files: PDF/PNG/JPG, at most 5 MiB, extension + MIME + magic bytes, private storage, random keys, checksum, role-checked short-lived links, audited.

**Versions**
- Verify the latest stable compatible versions from official sources and pin them exactly (global.json, central packages with lock files, image digests, tool checksums). Record source and date in `docs/DEPENDENCIES.md`. Never guess.

**Terraform and AWS**
- Naming and isolation exactly as CLAUDE.md states (prefix `misi3`). Read challenge-2 resources and the shared OIDC provider only through data sources; never change them.
- Everything Terraform creates, Terraform can destroy in one run. Hand-made resources (state bucket, two ECR repositories) are only read. For every Terraform story, record in STATUS.md how its resources are destroyed.
- Never publish plan files as pipeline artifacts. Pipeline inputs come only from `TF_VAR_*`, never from tfvars.
- Host names follow plan section 4.6: with no `base_domain` the app is HTTP on the ALB DNS name; with one it is served only at `https://dev-brief3.<base_domain>`. Derive it in Terraform; never hard-code a host, and never create records challenge 2 could use (for example `dev.<base_domain>`).

**Azure DevOps**
- YAML only, in `pipelines/`, using `pipelines/templates/`. Pin the agent image and every downloaded tool with checksums. Plan and PR jobs use `aws-misi3-plan`; only the deployment job uses `aws-misi3-deploy`, and only from `main`.

## 4. What you may run without asking, and what needs approval

Without asking: reading files; `dotnet` build/test/run; `docker` and `docker compose` against local stacks; `tools/Dev` commands; `terraform fmt/validate/init -backend=false`; `tflint`; `trivy`; `gitleaks`; `shellcheck`; documentation lookups.

Ask **at that moment**, as CLAUDE.md requires: any AWS-credentialed command (`terraform plan/apply` against real state, `aws ...`); pushing to Azure Repos; creating PRs; running pipelines; and anything destructive. If the user approved a kind of non-destructive action earlier in this session, you may repeat that same kind.

## 5. Evidence rules

- Report a check as **passed** only with successful command output. Otherwise it is **failed** (with output), **not executed** (with reason and the exact command) or **blocked** (with what is missing). Never merge these states.
- Record commit SHAs, UTC timestamps, environment (OS, architecture, Docker, browser) and pipeline run URLs. Link or summarise logs; do not paste them whole.
- After relevant code or configuration changes, rerun the checks before repeating an earlier result.

## 6. Order of work (summary)

1. **M0 Walking skeleton:** E0-01…E0-06, E0-09 (landing page skeleton), E1-01, E3-01 (registry mock only), E14-01…E14-09 (minimal services), E15-01…E15-03, then E0-07, a real deploy of the skeleton through the pipeline.
2. **M1 Thin end-to-end journey:** E2-*, E1-02…E1-04, E3-02, E3-04, E3-06, the basic versions of E4, E5, E6, E7 and E8 stories, E9-01/02, E12-01 core, E12-03 happy path, E14-10.
3. **M2 All 26 pages complete**, with every integration in normal mode.
4. **M3 Failure matrix:** every scenario mode, E13-01, E12-04.
5. **M4 Release:** full seed, docs, screenshots, test report, clean clone, AWS acceptance (E14-12), destroy/rebuild drill (only with the user's go-ahead at that moment).

At the end of each milestone, update STATUS.md with the milestone exit evidence (plan section 6.1) and give the user a short summary.

## 7. Final report

When everything is done or blocked, give the user:
1. Local and AWS URLs; the README demo accounts table for local runs; for AWS, the Secrets Manager secret name (never the values).
2. What exists (pages, services, infra, pipelines) with key commit SHAs.
3. Test results per suite with counts, plus the failure-matrix and demo-journey results.
4. The submission checklist (plan section 11) with passed / failed / not executed / blocked per item.
5. Assumptions added and decisions that differ from the plan, with reasons.
6. Remaining human actions, in order.
7. AWS cost estimate, how to scale down or tear down, and the destroy-drill result.
