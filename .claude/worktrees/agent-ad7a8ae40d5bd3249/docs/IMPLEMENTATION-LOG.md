# Implementation log

Dated entries per story or batch: what, why, evidence. Newest first.

## 2026-10-15

- **M1 core services and tests** (E2, E3-02, E3-04, E3-06, E4–E9 service layer). Application services for every transition T1–T14 with guards, audit in the same transaction and optimistic concurrency; outbound integration pipeline; worker jobs with advisory locks; HTTP API for every command. Evidence: integration suite 18/18 (see STATUS.md).
- **PDF library replaced** (A-31): PDFsharp/MigraDoc 6.5.0 is AGPL-3.0 and threw `PdfSharpLicenseException` during the first permit test; replaced by `SimplePdf` (Helvetica base-14 fonts, QR as vector modules).
- **EF key fix**: new children added through navigations were updated instead of inserted (GUID keys looked "existing"); keys are now `ValueGeneratedNever` (D-03).
- **tools/Dev runner** (E0-03 AC5) built by a delegated agent: doctor, setup, build, up, down, migrate, seed (`--reset --yes`), test, smoke, scan (gitleaks v8.30.1 pinned), screenshots.
- **Mock services** (E3-01) built by a delegated agent: six services, scenario modes, admin API, call counts, OpenAPI, probe; verified with curl and Docker.
- **Local skeleton** (M0): solution, domain model, Compose stack (SeaweedFS replaces MinIO, A-30), landing page, sign-in, shell. Evidence: `docker compose up -d --wait` healthy, sign-in verified.
- **Planning**: plans committed by the user on `acham-app-infra-prompts`; work continues on `app-track-build`.
