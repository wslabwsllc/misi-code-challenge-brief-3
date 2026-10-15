# Dependencies

All versions are exact pins. Checked 2026-10-15 against the official registries (api.nuget.org, mcr.microsoft.com, Docker Hub, GitHub releases).

## Toolchain

| Item | Version | Source | Notes |
| --- | --- | --- | --- |
| .NET SDK | 10.0.402 (`global.json`, rollForward latestPatch) | https://dotnet.microsoft.com/download/dotnet/10.0 | Runtime 10.0.13 |
| dotnet-ef (local tool) | 10.0.13 | https://www.nuget.org/packages/dotnet-ef | `.config/dotnet-tools.json` |
| Docker / Compose | 29.8.2 / v5.6.0 (verified locally) | https://docs.docker.com/ | Compose v2 syntax |

## NuGet packages (`Directory.Packages.props`, lock files committed)

| Package | Version | Licence | Used by |
| --- | --- | --- | --- |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.13 | MIT | Application |
| Microsoft.AspNetCore.DataProtection.EntityFrameworkCore | 10.0.13 | MIT | Application (keys in PostgreSQL) |
| Microsoft.EntityFrameworkCore(.Relational/.Design) | 10.0.13 | MIT | Application, Infrastructure |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 | PostgreSQL licence | Infrastructure |
| Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore | 10.0.13 | MIT | Web |
| Microsoft.Extensions.Http.Resilience | 10.11.0 | MIT | Infrastructure |
| Microsoft.AspNetCore.OpenApi | 10.0.13 | MIT | Web, Mocks |
| Serilog.AspNetCore / Serilog.Formatting.Compact | 10.0.0 / 3.0.0 | Apache-2.0 | Web |
| AWSSDK.S3 | 4.0.104.4 | Apache-2.0 | Infrastructure, Mocks |
| QRCoder | 1.8.1 | MIT | Infrastructure (QR module matrix) |
| xunit.v3 | 4.0.2 | Apache-2.0 | tests |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.13 | MIT | integration tests |
| Testcontainers / Testcontainers.PostgreSql | 4.16.0 | MIT | integration tests |
| Microsoft.Playwright | 1.63.0 | Apache-2.0 | browser tests |
| Deque.AxeCore.Playwright | 4.14.0 | MPL-2.0 | browser tests (axe-core bundled) |

Not used, and why:
- **PDFsharp-MigraDoc 6.5.0**: changed to AGPL-3.0 and fails without a licence key (A-31); replaced by `SimplePdf`.
- **Microsoft.Extensions.Configuration.KeyPerFile** explicit reference: part of the ASP.NET Core shared framework already (an explicit reference triggers NU1510).

## Container images (pinned by digest)

| Image | Tag | Digest | Notes |
| --- | --- | --- | --- |
| mcr.microsoft.com/dotnet/sdk | 10.0.402 | sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa | build stages |
| mcr.microsoft.com/dotnet/aspnet | 10.0.13 | sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac | runtime (non-root `$APP_UID`) |
| postgres | 18.6 | sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336 | Compose and Testcontainers |
| chrislusf/seaweedfs | 4.48 (GitHub release 2026-09-28) | sha256:4e61d15fd35994cb1e43e1e553dff106794841fd9a99ade2fc8c8bfce4d7872d | replaces MinIO (A-30) |
| zricethezav/gitleaks | v8.30.1 | sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f | `tools/Dev scan` |

**MinIO check (E0-02 AC3):** on 2026-10-15 `docker pull minio/minio` failed ("repository does not exist") and `quay.io/minio/minio` returned 401 for anonymous pulls. Recorded as A-30.

## Other pinned assets

| Asset | Pin | Source |
| --- | --- | --- |
| AWS RDS global CA bundle (in the web image at `/app/certs/rds-global-bundle.pem`) | sha256 90a6f1a7978a1b1b096dca6c57511ca48d3b8cf052f8e704baf358f34ea2f21b | https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem |

## Upgrade policy

Upgrades are deliberate: bump the pin, restore with `--locked-mode` after updating lock files, run `dotnet run --project tools/Dev -- test`, record the new version and date here.
