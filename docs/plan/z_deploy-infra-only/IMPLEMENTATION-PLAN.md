# Memorial Event Operations & Permit Case Management — AWS deployment plan (Brief 3, deploy track)

Status: **proposed plan, nothing implemented.** Written 2026-10-15. The implementing agent records progress in `docs/plan/deploy-infra-only/STATUS.md`, never in this file. Changes are made deliberately (by the user, or by the agent after the user agrees) and noted in the change log at the end.

This plan deploys the Brief 3 application to **AWS ECS on Fargate**. **Terraform** defines all infrastructure and **Azure DevOps Pipelines** (YAML) run CI/CD with OIDC, so no AWS access keys exist anywhere. The application itself is built by the app track (`docs/plan/app-only/`). This track never changes product behaviour; it runs the app track's images by configuration.

| File | Audience | Purpose |
| --- | --- | --- |
| `CLAUDE.md` (repository root) | Agent, every session | Stable rules, AWS/Azure DevOps boundaries, action boundaries. The deploy-track prompt names which plan and status file apply |
| `docs/plan/deploy-infra-only/IMPLEMENTATION-PLAN.md` (this file) | Agent and humans | Architecture, decisions, epics and stories for infrastructure and pipelines |
| `docs/plan/deploy-infra-only/AI-IMPLEMENTATION-PROMPT.md` | Agent | The prompt that starts or resumes this track |
| `docs/plan/deploy-infra-only/STATUS.md` | Agent and humans | Created by the agent in E14-00; the live progress record of this track |
| `docs/aws/AZURE-DEVOPS-SETUP-GUIDE.md` | Human | Created by the agent in E15-01; click-by-click steps only a person can do |
| `docs/aws/AWS-OPERATIONS.md` | Operators | Created in E14-11; runbook |
| `docs/plan/app-only/IMPLEMENTATION-PLAN.md` | Reference | The app track. Its section 1 "deployability contract" is this track's input |
| `docs/plan/IMPLEMENTATION-PLAN.md` | Reference | The combined plan both tracks were split from. Story IDs are identical (E0-07, E14-*, E15-*) |

---

## 1. Scope, prerequisites and lessons from challenge 2

**In scope:** hand-made resource instructions, the bootstrap identity stack, the `dev` environment in Terraform, the three Azure DevOps pipelines, deployment and acceptance evidence, the operations runbook, and the deployment sections of shared documentation.

**Out of scope:** application features and product rules. WAF, CloudFront, multi-region, blue/green deploys, a `prod` environment (the layout allows adding one later), alarms/budgets for dev, real SFTP (Transfer Family).

**Prerequisites from the app track.**

| Needed for | App-track milestone | What it gives this track |
| --- | --- | --- |
| Writing and validating Terraform and pipelines (D0) | none | Nothing. This work can start immediately, against the contract in the app plan section 1 |
| First real deployment (D2, story E0-07) | **M0** with E0-08 passed | Buildable images, `migrate`/`worker`/`probe` commands, health endpoints, env-var secrets, S3-compatible storage, `BehindTlsProxy` |
| Full-stack deployment and acceptance (D3–D4) | **M2** (all pages and mocks), **M3** for the failure drills | All six mocks, the full seed, the `RemoteSmoke` browser subset |

If the app does not meet a contract item, do not work around it in infrastructure and do not change application code. Add an entry to "Requests to the app track" in this track's STATUS.md (what is missing, why, the smallest change, how to test it), tell the user, and continue with independent stories. The only exception is an explicit user instruction for a specific change.

**Lessons from challenge 2** (repository `misi-code-challenge-wed`; its stack was validated but never applied, so treat its Terraform as a starting point, not as proven):

| Lesson | Applied here |
| --- | --- |
| Deployment came last and was never executed end to end | A walking-skeleton deploy (E0-07) happens as soon as the app reaches M0 |
| One IAM OIDC provider per issuer URL per account; it already exists in account `641944125809` | Read it with a data source; never create, import or destroy it |
| The OIDC `sub` uses the project name, which has spaces | Trust both `RFI VA MISI - Code Challenge` and `RFI%20VA%20MISI%20-%20Code%20Challenge`; record which form AWS actually receives on the first login |
| `terraform.tfvars` overrides `TF_VAR_*` | Pipeline inputs only through `TF_VAR_*`; tfvars hold sizing only |
| Npgsql `SSL Mode=Require` needed `Trust Server Certificate=true` | `VerifyFull` with the RDS CA bundle shipped in the image (A-15) |
| Container Insights and RDS Enhanced Monitoring create log groups Terraform does not own, which breaks a clean destroy | Terraform creates the Container Insights group first; Enhanced Monitoring off |
| `lockBehavior: sequential` only queues runs if the environment has an Exclusive Lock check | Add the check in the setup guide |
| ALB health checks send the target IP as the Host header | Keep `AllowedHosts=*`; filter hosts at the ALB |
| Azure DevOps SSH rejects ed25519 keys | RSA key instructions kept in the guide |
| EFS for files and Data Protection keys | Not needed: files in S3, keys in PostgreSQL |
| Plan files hold secrets in plain text | Never published as artifacts; deleted with `always()` |

---

## 2. Inputs and decisions

### 2.1 Inputs (the agent never invents these)

| Input | Value | State |
| --- | --- | --- |
| AWS account / region / local CLI profile | `641944125809` / `us-east-1` / `wsllc` | known (challenge 2) |
| Azure DevOps org / org GUID | `WorldServices` / `8a2cd247-4609-4e02-8418-58ad4ef2b76c` | known (challenge 2); re-confirm via `_apis/connectionData` |
| Project / repository | `RFI VA MISI - Code Challenge` / `misi-code-challenge-brief-3` | known |
| `base_domain` | empty (HTTP mode) until the user provides one | optional |
| `dns_mode` | `route53-existing`, `route53-new` or `external` (default `external`) | only with a domain |

### 2.2 Decisions

| ID | Decision | Reason |
| --- | --- | --- |
| OD-3 | **Access mode.** No `base_domain`: plain HTTP on the ALB DNS name (dev only). With `base_domain`: HTTPS only at `dev-brief3.<base_domain>` (user decision 2026-10-15, section 3.3) | No domain available today; "HTTPS where configured"; the `-brief3` label separates this app from challenge 2 under the same domain |
| OD-4 | **Isolation from challenge 2.** Reuse only the OIDC provider. New: state bucket `misi-event-permits-tf-state`, ECR `misi/event-permits-web` and `misi/event-permits-mocks`, roles `misi3-ado-plan` / `misi3-ado-deploy`, connections `aws-misi3-plan` / `aws-misi3-deploy`, environment `aws-misi3-dev`, variable group `misi3-aws-dev`, prefix `misi3-dev-`, secrets `misi3/dev/*`, IAM path `/misi3/`, VPC `10.41.0.0/16` | Challenge-2 roles can read `misi/*` secrets and their own state; a separate bucket and prefix keep this stack's secrets out of their reach and avoid name collisions |
| D-1 | ECS on Fargate, `X86_64`, images built `--platform linux/amd64` | No servers; Microsoft-hosted agents are x64 |
| D-2 | RDS PostgreSQL 18 (latest 18.x in the region), `db.t4g.micro`, `rds.force_ssl=1`, client `VerifyFull` | Matches the app; if 18 is unavailable, stop and ask (never fall back silently) |
| D-3 | S3 for attachments, permits and the partner drop prefix; Data Protection keys in PostgreSQL; **no EFS** | App contract; fewer moving parts; clean destroy |
| D-4 | Two web tasks with ALB sticky sessions (`lb_cookie`, 1 day) | Blazor Server circuits stay on one task; shared keys keep cookies valid on both |
| D-5 | One `worker` task; jobs locked with PostgreSQL advisory locks | Exactly one scheduler; safe even if two run |
| D-6 | Six mock services as separate ECS services on `FARGATE_SPOT` (variable), reached through Service Connect namespace `misi3-dev.local` | Brief requires independently runnable mocks; Spot halves their cost |
| D-7 | Webhooks: `mock-notify` → `http://web:8080/webhooks/delivery` through Service Connect; the ALB answers `/webhooks/*` with 403 | Callbacks never come from the internet; HMAC still enforced |
| D-8 | TLS ends at the ALB (HTTPS mode); the app trusts forwarded headers only from the VPC CIDR (`Hosting__BehindTlsProxy=true`) | Standard ALB pattern; app contract |
| D-9 | Secrets generated by Terraform (`random_password`) into Secrets Manager with `recovery_window_in_days = 0`, injected as environment variables | No secret in Git, tfvars, YAML or variable groups; clean destroy and rebuild with the same names |
| D-10 | Migrations and seed run as a one-off `migrate` task before every web rollout (`terraform_data` + `run-one-off-task.sh`); web `depends_on` it | New code never serves against an old schema; failure keeps old tasks serving |
| D-11 | Azure DevOps → AWS via OIDC (AWS Toolkit "Use OIDC" connections); two roles: plan (read-only + state locks) and deploy (write, `main` only) | Short-lived credentials; a PR can never change infrastructure |
| D-12 | Terraform state in S3 with native lock files (`use_lockfile = true`); no DynamoDB | Current recommended locking |
| D-13 | Hand-made, never Terraform-managed: the state bucket and the two ECR repositories | They must outlive every destroy and exist before the first push |
| D-14 | Everything Terraform creates, Terraform can destroy in one run (no `prevent_destroy`, protection flags as variables, Terraform owns every log group, `force_destroy` on created buckets) | Repeatable teardown; no orphaned billed resources |
| D-15 | One environment `dev`; one NAT gateway (accepted single-AZ risk) | Cost |
| D-16 | Deploy stage re-plans and applies its own fresh plan; the plan stage output is the human review copy | Plan files are never artifacts |
| D-17 | Same fictional seed data as local; seed passwords generated per user into Secrets Manager; the local README demo password is never enabled in AWS | No real data in AWS; the public ALB must not accept a password published in Git |

---

## 3. Target architecture

### 3.1 Diagram

```text
                  Internet  (HTTP mode: ALB DNS name; HTTPS mode: dev-brief3.<base_domain>)
                                      |
             +------------------------v-------------------------+
             | ALB (public subnets, 2 AZ)                        |
             |  HTTPS mode: :80 -> 301, :443 ACM, host rule      |
             |  /webhooks/* -> fixed 403   sticky lb_cookie      |
             +------------------------+-------------------------+
                                      | HTTP 8080 (VPC only)
   +----------------------------------v------------------------------------------+
   | ECS cluster misi3-dev (private subnets, Service Connect misi3-dev.local)    |
   |  web x2  <---- webhook ---- mock-notify                                     |
   |  worker x1 ------------->  mock-registry  mock-calendar  mock-weather       |
   |  migrate (one-off)         mock-geocode   mock-partner-feed   (Spot, x1 each)|
   +------+------------------------------+----------------------------------------+
          | 5432 TLS (VerifyFull)        | HTTPS (task roles, VPC -> NAT/S3)
   +------v-----------+          +-------v--------------------------------------+
   | RDS PostgreSQL 18|          | S3 misi3-dev-files-<suffix> (private, SSE)   |
   | (database subnets)|         |  attachments/  permits/  partner-drop/       |
   +------------------+          +----------------------------------------------+

   Secrets Manager misi3/dev/* -> env vars    CloudWatch Logs /misi3/dev/<service>
   ECR misi/event-permits-web, misi/event-permits-mocks (hand-made)   NAT gateway x1
```

### 3.2 Mapping from Compose to AWS

| Compose service | AWS |
| --- | --- |
| `init` (local secrets) | Not used: Terraform generates secrets into Secrets Manager |
| `db` | RDS PostgreSQL 18 |
| `minio` + `minio-init` | S3 bucket (Terraform-owned) with prefixes; `Storage__ServiceUrl` empty, default credential chain (task role), no `PublicEndpoint` |
| `migrate` | One-off Fargate task, web image, command `migrate` |
| `web` | ECS service ×2 behind the ALB; Service Connect client-server `web:8080` |
| `worker` | ECS service ×1, no port |
| six `mock-*` | Six ECS services ×1, Service Connect client-server, Spot |
| volumes | RDS storage and S3; nothing else is stateful |
| `docker compose logs` | CloudWatch log groups `/misi3/dev/<service>` (30-day retention) |

The exact setting-by-setting mapping is produced in E14-10 from the app's `docs/CONFIGURATION.md`.

### 3.3 Host names and URLs (user decision 2026-10-15)

| Environment | No domain (`base_domain = ""`) | With a domain (`base_domain = example.com`) |
| --- | --- | --- |
| AWS dev | `http://<alb-dns-name>` (changes on rebuild) | `https://dev-brief3.example.com` only; `http://` redirects (301); the ALB DNS name and any other host return 404 |
| Future AWS prod (not built) | not allowed (validation rule) | `https://prod-brief3.<base_domain>` |

Terraform derives the host name; nobody types it: `app_host = "${var.environment}-brief3.${var.base_domain}"`, with `-brief3` a fixed local (not a variable), so it cannot drift. The ACM certificate, the ALB host-header rule, the Route 53 alias (Route 53 modes), the CNAME the human adds (external mode), the smoke scripts and the `app_url` output all use `app_host`. Never create records challenge 2 could use (for example `dev.<base_domain>`). `base_domain` validation: lower-case DNS name, no scheme, no trailing dot, no leading `dev-` label.

### 3.4 Repository layout created by this track

```text
infra/
  README.md                        how plan/apply run locally (admins) and in pipelines
  manual/README.md                 CLI commands + verification block for the hand-made resources (E14-01)
  bootstrap/                       applied once by a human admin: pipeline roles (E14-02)
  modules/
    network/  database/  secrets/  storage_s3/  load_balancer/  dns_certificate/  ecs_app/
  envs/dev/                        backend.tf versions.tf providers.tf main.tf variables.tf outputs.tf terraform.tfvars (sizing only)
  scripts/
    run-one-off-task.sh            run the migrate task, wait, fail on non-zero, print the log tail
    smoke-http.sh  smoke-https.sh  edge checks (redirect, certificate, TLS versions, headers, host rule, /webhooks 403)
    check-guards.sh                static guards (D-13, D-14)
  .tflint.hcl
pipelines/
  pr-validation.yml  deploy.yml  destroy.yml
  templates/  dotnet-build-test.yml  compose-browser-tests.yml  docker-build-scan.yml  terraform-setup.yml  aws-terraform.yml
docs/aws/
  AZURE-DEVOPS-SETUP-GUIDE.md  AWS-OPERATIONS.md  CONFIGURATION-AWS.md
docs/plan/deploy-infra-only/STATUS.md
```

Naming: `misi3-<env>-<name>`. `default_tags`: `Project=misi-event-permits`, `Environment=<env>`, `ManagedBy=terraform`, `Repository=misi-code-challenge-brief-3`, `Stack=<bootstrap|dev>`.

---

## 4. Assumptions (append to the shared `docs/ASSUMPTIONS-LOG.md`)

This track adds assumptions in the range **A-60 onward** (the app track uses A-29…A-59), with the same six columns.

| ID | Assumption |
| --- | --- |
| A-15 | RDS connection uses `SSL Mode=VerifyFull` with the AWS RDS global CA bundle shipped in the web image (app contract) |
| A-60 | HTTP mode is acceptable for `dev` only because all data is fictional; passwords and cookies cross the internet unencrypted in that mode. Ingress can be narrowed with `allowed_ingress_cidrs` |
| A-61 | S3 versioning is off for the files bucket (fictional, re-seeded data; versioning would complicate a clean destroy); SSE-S3 rather than KMS (no key to manage or orphan) |
| A-62 | ECS mock services run on Fargate Spot; an interruption looks like an outage, which the app already tolerates (and which is itself a demonstrable failure mode) |
| A-63 | The browser suite in PR validation runs on Microsoft-hosted Ubuntu with Chromium against the Compose stack; this is CI evidence, not a Windows or Edge check |
| A-64 | Presigned S3 URLs are generated with task-role credentials; their validity is capped by the credential lifetime, which is far above the 60-second redirect URL (A-16) |

---

## 5. Delivery approach

### 5.1 Milestones

| Milestone | Goal | Depends on | Exit evidence |
| --- | --- | --- | --- |
| **D0 Written and validated** | All Terraform, scripts, pipelines and the setup guide written; static checks clean; no AWS writes | nothing | fmt/validate/TFLint/Trivy/shellcheck/guards passed; YAML reviewed; STATUS lists what is unexecuted |
| **D1 Identity and access** | Hand-made resources exist; bootstrap applied; service connections, environment, variable group, pipelines and branch policy created; first PR validation run | human steps; app repo builds | `sts get-caller-identity` per connection; PR validation run URL green |
| **D2 Walking skeleton live (E0-07)** | First deploy of app M0 through `misi3-deploy` | app **M0** | Deploy run URL; login on `app_url`; `/health` database + storage Healthy |
| **D3 Full stack** | All six mocks, webhooks through Service Connect, S3 flows, remote smoke subset; redeploy after app M2 and M3 | app **M2** (M3 for failure drills) | Remote smoke and RemoteSmoke browser subset green on AWS |
| **D4 Acceptance and operations** | E14-11, E14-12, E15-04 drill, documentation updates | app **M3/M4** | E14-12 table complete; drill passed or recorded as not executed |

### 5.2 Definition of Done (per story)

1. Every AC met with evidence (command output, run URL or recorded human output).
2. Terraform: exact pins and committed `.terraform.lock.hcl` (linux_amd64, darwin_arm64, darwin_amd64); `fmt`, `validate`, TFLint and Trivy config clean (accepted findings suppressed inline with a reason); module README, typed and described variables, sensitive outputs marked.
3. Destroyability answered in STATUS.md: "Can `terraform destroy` remove everything this story creates, in one run, with no manual step?" Side-effect resources created by AWS are either owned by Terraform or the feature is off, with the reason recorded.
4. Pipelines: pinned agent image and tools (checksums), correct service connection per stage, no secret echoed, plan files never published.
5. No secret in Git, tfvars, YAML, variable groups, logs or STATUS.md.
6. STATUS.md updated (state, commands, UTC time, commit SHA, run URLs, what is still unverified); committed as `<story-id>: <summary>`.

### 5.3 Evidence rules

Passed only with successful output. Written-but-not-run is "written, not executed". A green PR pipeline is not a deployment; a successful apply is not acceptance (E14-12 is). Never copy versions, counts or results from challenge 2.

---

## 6. Epics and user stories

### E14 — AWS infrastructure (Terraform)

#### E14-00 Start the deployment workstream · D0
As the implementing agent, I set up the status file and verify tool versions before writing infrastructure.
- AC1 `docs/plan/deploy-infra-only/STATUS.md` with sections Objective, Inputs (section 2.1), Story checklist (all stories, `not started`), Verified results, Not executed/blocked, Destroyability per story, Requests to the app track, Decisions and deviations, Next action.
- AC2 Latest stable versions looked up and recorded in `docs/DEPENDENCIES.md` under an "AWS deployment" heading (version, official URL, date): Terraform CLI, `hashicorp/aws`, `hashicorp/random`, TFLint + AWS ruleset, Trivy, Gitleaks, ShellCheck, AWS CLI v2, AWS Toolkit for Azure DevOps, agent image, RDS PostgreSQL 18 minors in `us-east-1`, the current ALB TLS 1.3/1.2 policy name. Unverifiable items say `pending verification`.
- AC3 The app plan's deployability contract is copied as a checklist into STATUS.md with state `unverified` per item.

#### E14-01 [HUMAN + agent] Hand-made resources · D0 (write) / D1 (human)
- AC1 Agent writes `infra/manual/README.md` with CLI commands and a verification block for: S3 state bucket `misi-event-permits-tf-state` (versioning, SSE-S3, Block Public Access, bucket-owner-enforced, TLS-only policy, non-current versions expire after 90 days) and ECR repositories `misi/event-permits-web`, `misi/event-permits-mocks` (immutable tags, scan on push, AES-256, lifecycle: keep last 30 tagged, expire untagged after 7 days). Console alternatives included.
- AC2 Human runs them and returns the verification output; Terraform only reads these (data sources/backend config). A PR guard fails on `resource "aws_ecr_repository"` or a resource for the state bucket.

#### E14-02 Bootstrap: pipeline roles (reusing the OIDC provider) · D0 (write) / D1 (human apply)
- AC1 `infra/bootstrap` adapted from challenge 2: reads the **existing** Azure DevOps OIDC provider (`vstoken.dev.azure.com/<org GUID>`) by data source, never creates or destroys it; creates `misi3-ado-plan` and `misi3-ado-deploy` under path `/ado/` with trust `sub = sc://WorldServices/RFI VA MISI - Code Challenge/aws-misi3-{plan|deploy}` (both the spaced and `%20` forms).
- AC2 Plan role: ReadOnly + state read for `dev/*` and `bootstrap/*` keys + lock files + `secretsmanager:GetSecretValue` on `misi3/*` only. Deploy role: create/destroy the dev stack services (EC2/VPC, ELB, ACM, Route 53, ECS, RDS, S3 for `misi3-*` buckets, Secrets Manager `misi3/*`, CloudWatch Logs, Service Discovery, IAM under `/misi3/` with scoped `iam:PassRole`), ECR push/pull on the two repositories only, explicit denies on deleting the hand-made resources and on modifying `/ado/` roles. Policy sizes checked against IAM limits.
- AC3 Human applies it (guide step) with state key `bootstrap/terraform.tfstate`; outputs recorded; challenge-2 roles and provider unchanged (`terraform plan` shows nothing outside the new roles/policies).
- AC4 With the deploy role, `aws ecr delete-repository` on either repository is denied (recorded once).

#### E14-03 Terraform skeleton · D0
- AC1 Layout per section 3.4; modules copied from challenge 2 and renamed (EFS module dropped, `storage_s3` added); `envs/dev` backend `s3` with `use_lockfile = true`, `encrypt = true`; `terraform init -backend=false && validate` clean.
- AC2 Pipeline inputs (`image_tag`, `base_domain`, `dns_mode`) come only from `TF_VAR_*`; `terraform.tfvars` holds sizing only.
- AC3 `.gitignore` covers `.terraform/`, `*.tfstate*`, `*.tfplan`, `crash.log`, `*.auto.tfvars`.

#### E14-04 Network · D0 / D2
- AC1 VPC `10.41.0.0/16`, two AZs, public / private-app / database subnets, one NAT gateway (accepted single-AZ risk), flow logs (30-day Terraform-owned log group), default SG with no rules.
- AC2 Database subnets have no internet route (verified after apply).

#### E14-05 Database and secrets · D0 / D2
- AC1 RDS PostgreSQL 18 (latest 18.x available in us-east-1, verified), `db.t4g.micro`, gp3 20→100 GiB, encrypted, private, 7-day backups, deletion protection and final snapshot as variables, Enhanced Monitoring off, parameter group `rds.force_ssl=1`.
- AC2 Secrets Manager (`recovery_window_in_days = 0`), all generated by `random_password`: `misi3/dev/db-password`, `misi3/dev/seed-credentials` (JSON keyed by the seed user names **read from the app code**, never guessed). Never set `Seed__AllowDemoPassword` in AWS: the README demo password is for local runs only (app OD-2), `misi3/dev/integration-secrets` (registry API key, calendar client secret, webhook HMAC secret, mock admin key, permit token signing key, link-signing key) shared by app and mocks; keys named exactly as in the app's `docs/CONFIGURATION.md`.
- AC3 App connection uses `SSL Mode=VerifyFull` with the RDS CA bundle path from the app contract (A-15); a non-SSL connection is refused (evidence in E14-12).

#### E14-06 Object storage · D0 / D2
- AC1 S3 bucket `misi3-dev-files-<random suffix>`: Block Public Access, bucket-owner-enforced, SSE-S3, TLS-only bucket policy, `force_destroy = true`, lifecycle expiring orphan staging objects; no CORS (downloads are 302 redirects to presigned GETs, A-16).
- AC2 Task roles: web and worker read/write `attachments/*`, `permits/*`, read/delete `partner-drop/*`; partner-feed mock write `partner-drop/*` only; migrate task write for seed files. Pre-signed URLs work with the task role (tested in E14-12).

#### E14-07 Load balancer and host names · D0 / D2
- AC1 Reuse the challenge-2 module: HTTP mode (no domain, OD-3) and HTTPS mode (ACM DNS validation, TLS 1.3/1.2 policy verified by name, :80 → 301, host-header rule, other hosts 404); sticky `lb_cookie` 1 day; health check `/health/ready`; idle timeout 120 s; deregistration delay 30 s; `drop_invalid_header_fields`.
- AC2 Only the ALB is public; tasks have no public IPs. A listener rule (both modes) answers `/webhooks/*` with a fixed 403.
- AC3 Host names per section 3.3: replace challenge-2's `app_domain` with `base_domain` + derived `app_host`. Certificate, host rule, Route 53 alias and the external-DNS CNAME output all use `app_host`; a plan on a scratch copy with `base_domain=example.com` shows `dev-brief3.example.com` everywhere and nothing named `dev.example.com`. Validation rejects a scheme, trailing dot, upper case or leading `dev-`; `environment=prod` with an empty domain is rejected.
- AC4 `dns_certificate` module: `route53-existing` (data lookup), `route53-new` (zone with `force_destroy`, outputs name servers), `external` (outputs the ACM validation CNAMEs and the app CNAME `dev-brief3.<base_domain>` → ALB; apply waits on validation with a 45-minute timeout). With no domain it creates nothing.

#### E14-08 ECS cluster and services · D0 / D2 (web, worker, migrate, mock-registry) / D3 (all mocks)
- AC1 Cluster `misi3-dev` with Container Insights (its log group created by Terraform first), Service Connect namespace `misi3-dev.local`.
- AC2 Task definitions (X86_64, Linux): `web` (0.5 vCPU/1 GiB, ×2, Service Connect client-server as `web:8080` for webhooks), `worker` (×1, no port), `migrate` (one-off), six mocks (0.25 vCPU/0.5 GiB, ×1, capacity provider from variable, default `FARGATE_SPOT`); container health checks via `probe` (`/health/live` for web so a database blip does not restart tasks; the ALB uses `/health/ready`); secrets injected from Secrets Manager; `Hosting__BehindTlsProxy` and `Hosting__TrustedProxyNetworks__0` per mode; storage settings point to the S3 bucket; mock base URLs and the webhook URL use Service Connect names.
- AC3 Security groups: ALB → web 8080; web/worker → mocks 8080; notify mock → web 8080; web/worker/migrate → RDS 5432; nothing else.
- AC4 Web deployment circuit breaker with rollback, minimum healthy 100 %, maximum 200 %, `wait_for_steady_state`; web does not depend on mock health.
- AC5 IAM roles under `/misi3/`, least privilege per service; task execution role reads only the needed secrets; ECS Exec enabled for web, worker and mocks.
- AC6 Outputs: `image_tag`, `app_url`, `app_host`, `alb_dns_name`, `cluster_name`, `ecr_repository_urls`, `log_group_names`, `files_bucket`.

#### E14-09 Migrations and seed before every rollout · D0 / D2
- AC1 `terraform_data` (trigger: migrate task definition ARN) + `run-one-off-task.sh` (from challenge 2, ShellCheck clean) runs `migrate` and waits for exit 0 before the web service updates; failure prints the log tail, fails the apply and keeps old tasks serving (proven or recorded as not executed).
- AC2 First deploy: the migrate log shows the brief §9 seed counts; second deploy: 0 created.

#### E14-10 Configuration mapping · D2
- AC1 `docs/aws/CONFIGURATION-AWS.md` maps every setting in the app's `docs/CONFIGURATION.md` to its AWS source (task definition env var, Secrets Manager key, Terraform output) or "not used in AWS" with a reason; a script fails the PR if a setting in the app document is missing from the mapping.
- AC2 Every contract item from E14-00 AC3 is marked verified (with evidence from D2/D3) or raised as a request to the app track.

#### E14-11 Operations runbook and cost control · D4
- AC1 `docs/aws/AWS-OPERATIONS.md`: credentials retrieval, logs, ECS Exec, switching mock scenarios in AWS (through the Scenario Panel; ECS Exec as fallback), rollback, scaling to zero, RDS stop (7-day limit), snapshot restore, secret rotation, certificate renewal, teardown and what survives it (hand-made resources, optional final snapshot, external DNS records).
- AC2 Monthly cost estimate from the AWS Pricing Calculator recorded (expected roughly USD 170–230 while running: ALB, NAT, 3 app tasks, 6 Spot mock tasks, RDS micro, S3, logs).

#### E14-12 AWS acceptance evidence · D4
- AC1 Table in STATUS.md, each row passed/failed/not executed with evidence: smoke script; `tools/Dev smoke --remote`; `RemoteSmoke` browser subset; each role logs in; upload + redirect download + link expiry page; webhook delivered through Service Connect (message reaches Delivered); Data Protection keys shared (login survives `--force-new-deployment`); mock outage drill (scale a mock to 0 → hub Offline, app serving, recovery after scaling back); seed idempotent; RDS non-SSL refused; tasks not directly reachable; `/webhooks/*` from the internet → 403; the README demo password is rejected; the landing page `/` loads anonymously and shows no credentials; HTTPS mode only: certificate ISSUED for `dev-brief3.<base_domain>`, auto-renewal eligible, cookies `Secure`, ALB DNS name → 404.

### E15 — Azure DevOps CI/CD

#### E15-01 [HUMAN + agent] Azure DevOps setup guide · D0 (write) / D1 (human)
- AC1 `docs/aws/AZURE-DEVOPS-SETUP-GUIDE.md` adapted from challenge 2, with a checklist table and only the steps still needed: confirm parallel jobs and AWS Toolkit (done for challenge 2), SSH/RSA access to the new repo, create the hand-made resources (E14-01), apply bootstrap (E14-02), service connections `aws-misi3-plan` / `aws-misi3-deploy` (OIDC), environment `aws-misi3-dev` with an Exclusive Lock check and no approvals, branch control on the deploy connection (`refs/heads/main`), variable group `misi3-aws-dev` (non-secret: `AWS_REGION`, `AWS_ACCOUNT_ID`, `TF_STATE_BUCKET`, `BASE_DOMAIN` (empty = HTTP mode), `DNS_MODE`), a "Domain" section (a domain gives `https://dev-brief3.<domain>`; which records to add in external mode), three pipelines `misi3-pr-validation`, `misi3-deploy`, `misi3-destroy`, branch policy on `main`, first-run "Permit" prompts, and a troubleshooting table.
- AC2 A temporary run prints `aws sts get-caller-identity` per connection showing the expected role; an attempt from a wrong connection name fails.
- AC3 The guide warns that the branch policy makes pull requests mandatory for both tracks from that point.

#### E15-02 PR validation pipeline · D0 (write) / D1 (first run)
- AC1 `pipelines/pr-validation.yml` (pinned `ubuntu-24.04`, tools by checksum, `trigger: none`, run by the branch policy): locked restore, build, unit + integration tests (Testcontainers), Compose stack up + full browser/axe suite (A-63), publish TRX and the HTML test report, Docker build of both images (`linux/amd64`) + Trivy (fail on fixable CRITICAL), Terraform fmt/validate/TFLint/Trivy config + plan with the plan role (no plan file artifacts), Gitleaks on the diff, static guards (`check-guards.sh`), configuration-mapping check (E14-10).
- AC2 Jobs run in parallel where independent; a PR with a failing test, a fmt error or a fake secret fails the right job (recorded run URLs).

#### E15-03 Deploy pipeline · D0 (write) / D2 (first run)
- AC1 Manual run from `main` (`trigger: none`, `lockBehavior: sequential`, parameter `imageTag` for redeploy/rollback): build + test + both images tagged with the full commit SHA (no AWS access) → plan with the plan role (log only; when `imageTag` is given, check both repositories contain it) → deployment job on `aws-misi3-dev` with the deploy role pushes images (an existing immutable tag counts as success only if the digest matches) and applies (migrate first) → verify job runs `smoke-https.sh dev-brief3.<base_domain>` (redirect, certificate for that exact host, TLS 1.2/1.3 only, headers, ALB DNS name → 404, `/webhooks/delivery` → 403) or `smoke-http.sh <alb-dns-name>` in HTTP mode (both also check that `/` returns the landing page anonymously), then `tools/Dev smoke --remote` and the `RemoteSmoke` browser subset (the verify job installs the pinned .NET SDK from `global.json` and the Playwright browsers matching the app's package version), with seed credentials from Secrets Manager written to a mode-600 temp file deleted with `always()`.
- AC2 Rollback by `imageTag` proven once.

#### E15-04 Destroy pipeline and drill · D0 (write) / D4 (drill)
- AC1 `pipelines/destroy.yml`: manual, `lockBehavior: sequential`, parameters `confirmEnvironment` (= `dev`), `iUnderstandDataLoss` (= `true`), `keepFinalSnapshot` (default `true`); apply with protection off, then destroy; leftover check by tags, log groups (`/misi3/dev/`, Container Insights), Cloud Map namespace and `misi3/dev/*` secrets; only the hand-made resources and the optional final snapshot remain.
- AC2 Destroy → redeploy drill executed with the user's go-ahead at that moment, or recorded as not executed; on rebuild the same names are recreated without "already exists" errors and the smoke passes.

#### E15-05 Deployment documentation · D2 / D4
- AC1 Fill the README's "Deploying to AWS" heading (short; links to `docs/aws/`), add the AWS deployment view to `docs/ARCHITECTURE.md` (Mermaid), add D-1…D-17 and OD-3/OD-4 to `docs/TECHNICAL-DECISIONS.md`, A-60+ to the assumptions log, AWS items to `docs/KNOWN-LIMITATIONS.md` (HTTP mode, single NAT, Spot mocks). Edit only these marked sections of shared documents.

### E0 — Walking skeleton (from the combined plan)

#### E0-07 Walking skeleton deployed to AWS dev · D2
As the owner, I want the app track's M0 skeleton live in AWS through the real pipelines before feature work goes far, so that deployment risk is retired early.
- AC1 Depends on E14-01…E14-09 (minimal: network, database, secrets, S3, ALB, ECS web + worker + migrate + mock-registry), E15-01…E15-03, and app-track M0.
- AC2 `misi3-deploy` run from `main` finishes green; `app_url` shows the login page; seeded users can sign in (password from Secrets Manager); `/health` shows database and storage Healthy.
- AC3 Run URL, commit SHA and timestamps recorded in STATUS.md; any contract gaps raised as requests to the app track.

---

## 7. Working alongside the app track

| Area | Owner | Rule for the other track |
| --- | --- | --- |
| `src/`, `tests/`, `tools/`, `Dockerfile`s, `compose.yml`, product docs | app track | Deploy track does not edit; raises requests in its STATUS.md |
| `infra/`, `pipelines/`, `docs/aws/`, `docs/plan/deploy-infra-only/STATUS.md` | deploy track | App track does not edit |
| `docs/DEPENDENCIES.md`, `docs/TECHNICAL-DECISIONS.md`, `docs/ASSUMPTIONS-LOG.md`, `docs/ARCHITECTURE.md`, `docs/KNOWN-LIMITATIONS.md`, README | shared | Deploy track edits only its marked sections and its A-60+ rows |

If both tracks run at the same time, run them in **separate Git worktrees on separate branches** (for example `deploy/infra` for this track) and merge through pull requests, which also exercises PR validation. Never run two agents in the same working tree.

---

## 8. Definition of done for this track

1. All stories done, or blocked/not executed with a reason and the exact command that would finish them.
2. `misi3-deploy` from `main` deploys, migrates/seeds first and verifies itself; HTTP mode serves `app_url`; HTTPS mode (if a domain is given) serves only `https://dev-brief3.<base_domain>` with a valid, auto-renewing certificate.
3. No AWS access keys; no secret in Git, images, pipeline logs or variable groups.
4. Everything Terraform created can be removed by the destroy pipeline in one run (drill passed or recorded as not executed); only the hand-made resources and the optional final snapshot survive.
5. Challenge-2 resources untouched (bootstrap plan evidence; no `misi-*` non-`misi3` changes in any plan).
6. Local development and every app-track test are unaffected by this track's changes.
7. STATUS.md records exact commands, results, timestamps and run URLs, separating passed, failed, skipped and not executed.

## 9. Risks

| Risk | Mitigation |
| --- | --- |
| App not ready when infra is | D0/D1 need no app features; D2 needs only app M0 |
| Contract gap found during deploy | Requests to the app track; no workarounds in infra |
| Collision with challenge-2 resources | OD-4 naming; data sources only; bootstrap plan reviewed for foreign changes |
| OIDC `sub` mismatch (project name with spaces) | Both forms trusted; first login result recorded |
| Destroy stuck or leaves billed leftovers | D-14 rules, static guards, leftover check, drill |
| Secrets in Terraform state | Encrypted, versioned, TLS-only state bucket readable only by the two roles and admins |
| Blazor users disconnected during deploys | Sticky sessions, 30 s deregistration, reconnect UI, manual deploys |
| Spot interruption of a mock during a demo | Scale the mock to on-demand with the capacity-provider variable before a demo (runbook) |
| Cost while idle | Scale to zero or destroy; cost section in the runbook |
| Long PR pipeline (Compose + browser suite) | Parallel jobs; cache NuGet and Playwright browsers keyed by lock files |

## 10. Change log

| Date | Change | By |
| --- | --- | --- |
| 2026-10-15 | App decisions: local demo password stays local (never `Seed__AllowDemoPassword` in AWS; E14-12 proves it is rejected); smoke checks the public landing page `/` | user + Claude |
| 2026-10-15 | Review: verify job installs the pinned SDK and Playwright browsers; app seed timeline is relative to a stored anchor date (app A-29), so the first AWS seed has events on deploy day | user + Claude |
| 2026-10-15 | Deploy track split from `docs/plan/IMPLEMENTATION-PLAN.md` (same story IDs E0-07, E14-*, E15-*); added E14-00, E15-05, milestones D0–D4, ownership rules, assumption range A-60+ | user + Claude |
