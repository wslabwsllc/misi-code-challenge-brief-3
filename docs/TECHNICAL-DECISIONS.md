# Technical decisions

Decisions for the app track. Plan references: `docs/plan/app-only/IMPLEMENTATION-PLAN.md` sections 2 and 4.

| ID | Decision | Alternatives considered | Why | Consequences |
| --- | --- | --- | --- | --- |
| OD-1 | .NET 10 LTS (ASP.NET Core 10, EF Core 10, Npgsql 10) | .NET 8 (the brief's preference) | User decision 2026-10-15; .NET 8 support ends 2026-11-10 | Documented deviation (A-01) |
| OD-2 | Fixed local demo password `EventPermits!Demo2026` for every seeded account, enabled only by `Seed:AllowDemoPassword=true` | Per-environment generated passwords | User decision 2026-10-15: simplest for evaluators | AWS must supply `Seed:CredentialsJson`; the seeder refuses the demo value without the flag |
| OD-5 | Partner CSV/SFTP pattern as a drop folder (`partner-drop/`) in object storage | Compose SFTP container; AWS Transfer Family | Brief allows an equivalent mock drop directory; deploys to AWS without an SFTP server | Real SFTP is a stretch story (E10-04) |
| OD-6 | Blazor Web App, Interactive Server; Identity pages and the landing page are static server-rendered | MVC + JS; WebAssembly | Team knowledge; one C# code path | Needs sticky sessions behind a load balancer |
| D-01 | One application-service method per command, also exposed as `/api/...`; authorization, validation, guards and audit live in that method | Separate UI and API logic | Crafted-request tests exercise the UI's code path (brief §3) | Blazor pages never write through DbContext |
| D-02 | PostgreSQL `xmin` as row version; child writes touch the parent | Explicit version columns | No extra column; database-maintained | Every command needs the caller's version (409 on mismatch) |
| D-03 | Entity keys are client-assigned GUIDs (`ValueGeneratedNever`) | Database-generated keys | New children reached through navigations are inserted, not updated | — |
| D-04 | Outbox for notifications (Notification rows) and calendar commands (OutboxMessage rows) processed by the worker | Inline HTTP inside the request | A gateway failure never rolls back a permit; retries and idempotency are explicit | Statuses lag by a few seconds |
| D-05 | One outbound HTTP pipeline (correlation, auth, resilience, sanitised attempt recorder) | Per-adapter code | Uniform evidence, redaction and retry rules (brief §7) | Every adapter returns `ExternalResult` instead of throwing |
| D-06 | Integration health derived from the latest recorded attempt | Active probes on every `/health` call | Health stays cheap and never calls a mock's real endpoint per request | A never-called integration shows Healthy "No calls yet" |
| D-07 | Two-layer expiring download links (A-16): signed app link → 60-second storage presigned URL | Presigned URL only | Friendly expiry page and per-download audit | Browser needs to reach the storage endpoint (loopback port 9000 locally) |
| D-08 | SeaweedFS replaces MinIO (A-30) | Garage, RustFS, LocalStack | Public images, Apache-2.0, per-prefix identities, presigned URLs | Bucket created by a one-shot `storage-init` |
| D-09 | In-repo PDF writer instead of PDFsharp/MigraDoc (A-31) | PDFsharp 6.2.x (older MIT line); QuestPDF | PDFsharp 6.5 is AGPL with a licence gate; QuestPDF licence depends on organisation revenue | WinAnsi text only |
| D-10 | Secrets as key-per-file locally (`.data/secrets/<consumer>/Section__Key`) and environment variables in AWS | `.env` files | Same configuration keys everywhere (deployability contract) | `init-local` writes the files once |
