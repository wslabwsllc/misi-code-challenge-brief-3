# External Service Integrations: plan (boundary `ext-svc`)

Owner: one integration engineer. Read [`../README.md`](../README.md) first (ownership, contracts, requests protocol). Story acceptance criteria (AC) live in the master [`../IMPLEMENTATION-PLAN.md`](../IMPLEMENTATION-PLAN.md); this file lists what you own and the order to work in. Progress goes in `docs/plan/ext-svc/STATUS.md`.

## 1. Mission and scope

Implement and configure every external-service connection of the application, end to end:

| # | Integration | Pattern (brief §7) | Mock | Pages |
| --- | --- | --- | --- | --- |
| 1 | Applicant registry | REST + API key (`X-API-Key`) | `registry` | 20 |
| 2 | Regional calendar | OAuth2 client credentials + conflict handling | `calendar` | 21 |
| 3 | Notification gateway | REST + idempotency key + signed webhook callback | `notify` | 12 (ui-ux), 22 |
| 4 | Object storage | S3 API, private bucket, expiring links (SeaweedFS locally per A-30, S3 in AWS) | storage emulator | 23 |
| 5 | Weather | REST + cache + stale fallback | `weather` | 24 |
| 6 | Partner CSV drop | Batch file ingestion (drop-folder simulation, OD-5) | `partner-feed` | 25 |
| 7 | Geocoding | REST + ambiguity handling | `geocode` | 26 |

Plus the platform: one outbound adapter pipeline, the Integration Hub (page 19) with a Scenario Panel, the background job host, and the webhook receiver. Pages **19–26** are yours; the shell and shared components are ui-ux's.

Out of scope: schema (db: request entities), pages 01–18, Terraform and ECS wiring of the six mocks (infra; you supply names, ports, environment variables and health checks), the cross-boundary failure-matrix and browser suites (test-auto).

## 2. Baseline (verify against code; do not rebuild)

Per the 2026-10-15 snapshot: six mock services in `EventPermits.Mocks` (scenario modes, admin API, call counts, OpenAPI, probe, verified with curl and Docker), `Infrastructure/Integrations/{Adapters,Pipeline,MockAdmin,WebhookVerifier,IntegrationRegistration}.cs`, `ObjectStore.cs`, `Application/Services/{Files,Integrations,CalendarSync,Notifications}.cs`, `Web/Api/{FileEndpoints,WebhookEndpoints}.cs`, `Hosting/{Jobs,JobHost}.cs`, `data/reference/{registry,calendar-events,weather,geocode}.json`. None of pages 19–26 exist yet. Your first task (E-EXT-00) is to reconcile every owned story with code and tests and write STATUS.

Your files: see `../README.md` ownership table; tests live in `tests/EventPermits.UnitTests/ExtSvc/**` and `tests/EventPermits.IntegrationTests/ExtSvc/**`.

## 3. Decisions and assumptions you carry

OD-5 (drop folder), D-04 (outbox), D-05 (one outbound pipeline; adapters return `ExternalResult`), D-06 (integration health from the latest recorded attempt), D-07 (two-layer expiring links), D-08/A-30 (SeaweedFS), A-14, A-16, A-17, A-18, A-19, A-20, A-27. New assumptions A-50…A-59; decisions `X-nn`.

## 4. Stories you own

| Story | Title | Milestone | Notes |
| --- | --- | --- | --- |
| E-EXT-00 (new) | Reconcile and STATUS file | M0 | |
| E3-01 | Mock services host | M0 registry / M2 all | One image, `--service registry|calendar|notify|weather|geocode|partner-feed`; scenario engine; `/_admin/{scenario,stats,reset}` with own key; `/health`; OpenAPI; `Mock__Mode`; `offline` aborts connections |
| E3-02 | Outbound integration pipeline | M1 | Handler chain (correlation → auth → resilience → attempt recorder); retries only transient (5xx, 408, 429, network), `Retry-After` honoured, 404 and other 4xx never retried; circuit breaker; redaction tests; contract test per adapter (six) |
| E3-03 | Integration Hub (page 19) | M2 | Seven cards, Test connection, attempts table → `/integrations/attempts/{id}`, Scenario Panel (Admin only), Ops Manager read-only |
| E3-04 | Object storage service | M1 | `IObjectStore`; PDF/PNG/JPG, extension + MIME + magic bytes agree, 5 MiB boundary exact, random keys, checksum; A-16 two-layer links; orphan cleanup 24 h |
| E3-05 | Document Vault (page 23) | M2 | Paged list, preview via short-lived link, countdown, regenerate, per-file audit; Auditor metadata only |
| E3-06 | Background job host with locking | M1 | `worker` jobs with PostgreSQL advisory locks, job runs recorded, manual trigger through the server |
| E5-02 | Registry verification (page 20) | M2 | Stores `RegistryVerification`; 429 honours `Retry-After` within a bound then "verification pending"; 404 not retried; Flagged/Unknown blocks T4 unless an A-10 override |
| E6-07 | Calendar sync (page 21) | M2/M3 | OAuth token cache renewed before expiry, 401 → refresh and replay once, `invalid_client` admin-actionable; sync job + Sync now; push on T6 and every tentative change; 409 → `ReconciliationItem`; offline → push queued, T8 blocked with a reason |
| E6-08 | Geocoding and venue map (page 26) | M2/M3 | Candidates with confidence; a tie needs explicit selection; coordinates stored only after confirmation (audited); offline → "unverified", no invented GPS; local map rendering, no internet |
| E7-05 | Weather (page 24) | M2/M3 | 15-minute cache; stale label on outage, never fabricate; severe advisory marker and Ops-Manager acknowledgement (audited); manual refresh |
| E9-02 | Gateway sender (worker) | M1 | `POST /messages` with `Idempotency-Key`; backoff then Failed; no duplicates (crash-after-send test); fallback status poller |
| E9-03 | Delivery webhook receiver | M2/M3 | `POST /webhooks/delivery`, HMAC-SHA256 over `timestamp + "." + rawBody`, constant-time compare, 5-minute tolerance, idempotent by `eventId`, no regression from Delivered, immutable `WebhookReceipt`, antiforgery-exempt only here, body size limit |
| E9-05 | Messaging Delivery Monitor (page 22) | M2/M3 | Queue depth/age, rates, failed list, receipts with reason, safe manual retry |
| E10-01 | Partner ingestion | M2/M3 | Drop prefix scan (nightly, on demand) + manual CSV upload; header and row validation; partial success; checksum no-op replay; `ImportBatch`, `ImportRowError` |
| E10-02 | Data Import & Reconciliation (page 25) | M2 | Preview of mapping and first 20 rows, batches, row errors, error-report CSV, Replay, imported events vs reservations conflicts |
| E10-03 | Partner feed scenarios | M3 | clean, bad-rows, duplicate-file, updated-rows, malformed-header from the Scenario Panel |
| E10-04 | (Stretch) real SFTP source | after M4 | Only with the user's go-ahead; not deployed to AWS |
| E12-02 (mock side) | Mock seed data | M2 | Registry mix of Valid/Flagged/Unknown + unknown references, 40 calendar events (some colliding), weather per venue (one Severe), geocode with a tie and a no-match, three CSV files (one with bad rows); shared reference JSON agrees with db's `sites.json` |
| E16-02 (part) | `docs/INTEGRATIONS.md` | M2/M4 | Contracts, failure behaviours, configuration |
| E16-03 (mocks) | OpenAPI per mock | M2 | Exported to `docs/api/` |

## 5. Contracts you provide and require

| Direction | Contract |
| --- | --- |
| To ui-ux | Ports in `Ports.cs`: `IApplicantRegistry`, `ICalendarClient`, `INotificationGateway`, `IWeatherClient`, `IGeocoder`, `IPartnerFeedSource`, `IObjectStore`; signed-link service (issue, validate, regenerate); read models: registry evidence per case, weather/acknowledgement status per event date, calendar reconciliation status per reservation, open attempts per integration; upload API used by the Wizard, incidents and the Document Vault |
| To db | List of entities you need as a request (attempts, receipts, batches, snapshots, outbox, job runs); rule keys you read (token TTL, webhook tolerance, weather cache minutes, file limits, import schedule) |
| To infra | For each mock: container name, port (8080), environment variables, secret names, health endpoint; `compose.yml` blocks `# BEGIN ext-svc` … `# END ext-svc`; `docs/CONFIGURATION.md` rows for every `Integrations:*`, `Storage:*`, `Mock__*` key |
| To test-auto | Scenario matrix (mock → modes → expected user-visible recovery), admin API usage for call-count assertions, deterministic mock seed, selectors for pages 19–26 |
| From db | Entities and migrations, `HmacSigner`, rule settings, scoped query helpers, capability names |
| From infra | Compose stack, secret injection, S3 bucket and task roles in AWS |

## 6. Rules specific to this boundary

- **One adapter pipeline for all outbound HTTP.** Correlation ID, credentials from configuration only, per-integration timeouts, transient-only retries with bounded exponential backoff, circuit breaker, sanitised `IntegrationAttempt` per HTTP call (including retries): integration, operation, request ID, correlation ID, linked case, sanitised URL, status, outcome class, duration, attempt number, error summary.
- **Redaction.** Mask `Authorization`, `X-API-Key`, `X-Signature`, query `access_token` and body fields named like `secret|password|token` before logging or storing. Tested. The Hub shows masked configuration and never returns secrets from any API.
- **No external HTTP inside a database transaction.** Call, re-check state and version, then commit. Sending goes through the outbox and worker, never inline in permit issuance.
- **Mocks** are independent processes with scenario modes, call counters and an admin key (`X-Mock-Admin-Key`, never reachable from the browser directly). Credentials only from environment variables. `offline` aborts connections so the client sees a transport failure.
- **Mock data and app seed agree**: one committed JSON per domain, copied into both images.
- **Webhook path** is internal-only in AWS (ALB fixed 403); the HMAC check still applies everywhere.
- Integration health per integration: Healthy / Degraded / Offline from the latest attempts; `/health` includes it but `/health/ready` never fails because an integration is down.
- Never invent GPS coordinates, forecasts or registry results. Never pass eligibility without evidence.
- Compose: edit only your marked blocks. Adding a seventh mock is a request to infra (Terraform uses a list of mocks).

## 7. Order of work

1. **M0:** E-EXT-00, E3-01 registry slice verified in Compose and `probe`, object store reachable (`/health` storage Healthy).
2. **M1:** E3-02, E3-04, E3-06, E9-02. Announce each in STATUS "Provided contracts"; ui-ux waits on E3-04 (Wizard uploads) and the registry/weather/calendar read models.
3. **M2:** all six mocks complete (E3-01), E3-03 (Hub, publish `PageCatalog` entries by PR), E3-05, E5-02, E6-07, E6-08, E7-05, E9-03, E9-05, E10-01/02, E12-02 mock side, OpenAPI exports.
4. **M3:** every scenario mode selectable from the Scenario Panel and via `Mock__Mode`; E10-03; support test-auto's failure matrix (E13-01) and fix defects.
5. **M4:** `INTEGRATIONS.md` final; E10-04 only if the user asks.

## 8. Boundary Definition of Done (adds to master 6.2)

1. Every AC proven by tests under `tests/.../ExtSvc/**`: unit (retry classification, backoff bounds, token cache, HMAC and tolerance, dedupe, cache freshness, redaction), contract test per adapter against its mock in-process, integration (storage access, webhook forgery/replay/duplicate/out-of-order, calendar 409 and offline, malformed CSV). Tests assert **mock call counts** to prove real requests.
2. Crafted-request tests on every endpoint you add (Admin-only Scenario Panel, Auditor metadata only on the Vault, forged/expired links).
3. Pages 19–26: real data, validation, empty/error states, masked secrets, placeholder scan clean; browser/axe tests by test-auto from your hook sheet.
4. No secret in code, logs, attempts, health, UI or errors. `docs/CONFIGURATION.md` rows added. STATUS updated with the scenario matrix.

## 9. Risks

| Risk | Mitigation |
| --- | --- |
| Integration code duplicated per service | One pipeline and one mock host with a shared scenario engine |
| Secret leakage through attempts or logs | Central redaction, log-capture test (test-auto), review of every new log line |
| Mock/app data drift | Shared JSON, agreement test (test-auto) |
| Storage emulator differences from S3 | Same `AWSSDK.S3` calls; AWS acceptance (infra E14-12) covers presigned download and expiry |
| Spot interruption looks like an outage | Intended failure mode; app already tolerates it |
