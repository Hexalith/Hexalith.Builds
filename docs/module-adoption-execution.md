# Shared execution for module adoption

These capabilities prepare EventStore → FrontComposer → Tenants adoption. They
require a separately released, qualified Builds infrastructure version and CiCd
delivery using that exact release. Checkout bytes are not a released contract.
No product release, package publication or image publication is part of adoption.

CiCd owns the setup registry and generated callers. The authoritative fields are
in `schemas/hexalith.ci.v1.json`; the strict reader rejects unknown settings and
does not insert defaults. Older tagged contracts and their rendered callers keep
their existing behavior.

## Supported configuration

| Setting | Behavior |
| --- | --- |
| `ciTimeoutMinutes` | CI build/test job budget, 1–360 minutes; default 30. The existing `job-timeout-minutes` override takes precedence. |
| `releaseTimeoutMinutes`, `nightlyTimeoutMinutes` | Optional 1–360 minute budgets for audited release build and the existing release nightly hook. Omission retains 45 minutes. |
| `jobs.<action>.runner` | `ubuntu-latest`, `ubuntu-24.04`, `windows-latest`, `macos-latest`, or a protected manual Linux runner group. |
| `timeoutMinutes`, `trigger` | Hosted 1–360 minute budget (private 1–180) and `ci` (default), `nightly`, or `manual` execution. |
| `matrix.include`, `maxParallel` | Independent fresh jobs, each with a unique kebab-case `id` and literal string parameters. Fail-fast is false. A serial hook is not a substitute. |
| `needs`, `runAfterFailure` | Same-trigger job dependencies, validated as a DAG; allow an aggregate to inspect failed workers. The aggregate's product action validates complete reports and worker results. |
| `resourceProfile: mutation` | Builds-owned immutable SDK container with four CPUs and 10 GiB memory/swap bounds. Profile bytes come from the selected Builds schema, not from module shell workarounds. |
| `setup` | Hosted Linux job overrides of environment flags, `npmWorkspaces`, `dotnetTools` and `prerequisites`; private jobs preserve owner-prepared fixtures. Test discovery/classification stays unchanged. |
| `tests.additional` | Explicit test projects outside the solution. Stage 2 restores/builds them with the same flags and verifies evaluated test identity; stage 3/4 discovers them normally. |
| `tests.reports.trx` | Require fresh, valid native TRX from every selected project/target framework. |
| `tests.reports.coverage` | Collect Cobertura for named nonexcluded projects; aggregate measured source lines/branches with optional declared scopes and thresholds. |
| `prerequisites` | Exact Node/npm pair, shared APT preparation, development TLS/NSS trust, and the catalog Dapr profile. |

Choose budgets from measured retained workloads. Historical 180/90/360 minute
limits establish required representability, not measured suite durations.
Each matrix worker has independent checkout, build outputs, processes and backend
ownership. Actions still own product host lifetime, ports, assertions and cleanup.

The generated action environment supplies `HEXALITH_JOB_NAME`, JSON
`HEXALITH_JOB_PARAMETERS` (the matrix row), JSON `HEXALITH_JOB_NEEDS` (dependency
outcomes), and JSON `HEXALITH_QUALIFICATION_PARAMETERS` for manual inputs.
Product actions parse these as data; never interpolate them into executable shell
text. Optional qualification is a separate generated workflow with its own
schedule/manual dispatch. It does not create release drafts or publish artifacts
as product packages/images. See CiCd's setup contract for the execution protocol.

Protected Azure execution uses an explicit runner group, labels
`[self-hosted, linux, x64]`, `trigger: manual`, and a required protected environment.
Explicit environment-variable-to-GitHub-secret mappings reach only the action
step. The generated condition permits dispatch from `main` only. General CI never
receives these credentials. Private dispatch requires an explicit authority digest
and successful fresh CI for the same source. Preparation builds in `preserve`
mode, validates that request before checkout, and leaves the private fixture
runtime intact. Actions receive `HEXALITH_AUTHORITY_SHA256` and
`HEXALITH_SOURCE_SHA`; retained `preparation: build-only`/secret-list declarations
use the same protected path. The schema cannot establish the live environment's
protection rules, runner ephemerality, network boundary or actual secret custody;
the maintainer must verify those before adoption.

## Results and exact product coverage

Results live under `TestResults/shared-ci/`, with fresh stage directories and a
project-path/target-framework identity. The shared workflow retains reports on
failure as well as success. Missing, empty, malformed or changed required reports
fail. Failed product exit statuses remain the primary failure; report diagnostics
do not hide them. All tests in a project, including its target frameworks, finish
before stopping ahead of the next project after failure.

MTP uses native reporter and coverage switches; VSTest uses its TRX logger and
XPlat collector. Projects must supply the corresponding extensions:
`Microsoft.Testing.Extensions.CodeCoverage` for MTP and `coverlet.collector` for
VSTest, with a native TRX reporter (xUnit provides its own). Unsupported extensions
fail instead of silently omitting results.

For Tenants, preserve the existing rule precisely: **line coverage greater than
80%**, and **100% named branch coverage**. The old line gate is strict, despite
being commonly described as an 80% minimum. Its source scopes are:

```yaml
tests:
  reports:
    trx: true
    coverage:
      projects: # enumerate the retained, nonexcluded coverage-producing projects
        - tests/Hexalith.Tenants.Contracts.Tests/Hexalith.Tenants.Contracts.Tests.csproj
        - tests/Hexalith.Tenants.Client.Tests/Hexalith.Tenants.Client.Tests.csproj
        - tests/Hexalith.Tenants.Server.Tests/Hexalith.Tenants.Server.Tests.csproj
        - tests/Hexalith.Tenants.Testing.Tests/Hexalith.Tenants.Testing.Tests.csproj
        - tests/Hexalith.Tenants.UI.Tests/Hexalith.Tenants.UI.Tests.csproj
        - samples/Hexalith.Tenants.Sample.Tests/Hexalith.Tenants.Sample.Tests.csproj
        - tests/Hexalith.Tenants.AppHost.Tests/Hexalith.Tenants.AppHost.Tests.csproj
      lineScope:
        - src/Hexalith.Tenants.Contracts/
        - src/Hexalith.Tenants.Client/
        - src/Hexalith.Tenants.Server/
        - src/Hexalith.Tenants.Testing/
      minimumLine: 80
      lineComparison: greater-than
      branchTargets:
        - src/Hexalith.Tenants.Server/Aggregates/TenantAggregate.cs
        - src/Hexalith.Tenants.Server/Aggregates/GlobalAdministratorsAggregate.cs
        - src/Hexalith.Tenants.Server/Validators/ChangeUserRoleValidator.cs
        - src/Hexalith.Tenants.Server/Projections/TenantAuditOwnership.cs
      minimumBranch: 100
```

This illustrates configuration shape, not a registered Tenants setup. Copy the
exact evaluated coverage project and source paths from the retained product gate
before delivery; a stale/nonexistent scope or target must fail qualification.
FrontComposer may select projects and reporting without either threshold. Do not
introduce a coverage threshold as part of adoption.

When a branch threshold is enabled, every named target must contribute at
least one instrumented branch with a positive denominator. A class containing
only line hits fails the gate even when the other targets have full coverage.

Aggregation preserves Tenants' existing union of covered source lines and
best-per-line branch counts. Cobertura counts alone cannot identify complementary
covered branch arms, so two separate 50% observations do not establish 100%.

The local checker still imports `restore_and_build` and `run_unit_tests` from the
same runtime as CI. It collects/validates unit reports and enforces coverage when
all coverage projects are unit-stage projects. Cross-stage aggregation completes
after integration in hosted CI. Local success does not certify environment setup,
integration tests, custom jobs, browser matrices or private qualifications.

## Shared prerequisites and exact historical tools

`npmWorkspaces` uses committed contained package/lock inputs, installs without
lifecycle scripts, verifies npm signatures, and supplies locked Playwright tools.
`.NET` Playwright tooling remains supported. An explicit exact pair is selected
and verified before installation, for example:

```yaml
prerequisites:
  nodeVersion: '24.10.0'
  npmVersion: '11.6.1'
  apt: true
  tlsTrust: true
  daprProfile: catalog
```

This bundled pair is recorded by [Node's release notes](https://nodejs.org/en/blog/release/v24.10.0).
Use the product's chosen qualified pair; the installer never silently replaces
npm. Omitted exact versions retain the older Node 24 preparation contract. APT
uses the existing shared mirror recovery and preserves signed Ubuntu source
settings while selecting HTTPS. TLS setup verifies the development certificate,
imports it into both Linux NSS locations, and fails on missing/empty certificates.
It exports `SSL_CERT_DIR` for subsequent OpenSSL clients while preserving system
and existing trust directories, following the [ASP.NET Core Linux trust guidance](https://learn.microsoft.com/en-us/aspnet/core/security/enforcing-ssl).
Product actions own HTTPS host startup and client assertions.

`dapr: true` still initializes the catalog tuple and emits the existing fresh
job-bound backend receipt. Aspire setup remains a distinct enabled prerequisite.
Catalog initialization does not replace historical Dapr compatibility tests,
private archive caches, mTLS tools, backend images or a product qualification.

For exact isolated historical CLI/runtime/kind artifacts, an enabled product action
can call `Github/qualification-tools` from the same acceptable Builds release.
The module owns its declarative `hexalith.qualification-tools.v1` manifest and
profile approval; Builds owns bounded official HTTPS downloads, exact size/archive
and executable SHA-256 checks, safe member selection, private directory creation
and integrity receipts. Formats are `file` (including original archives/checksum
records), `binary`, and `tar.gz` (one exact executable member). See
`schemas/hexalith.qualification-tools.v1.json` for the closed contract. Outputs are
`directory`, `receipt`, JSON `files`, and optional `dapr-receipt`. Staging alone
replaces no global runtime or PATH. The receipt explicitly says qualification is
not established. This supports side-by-side historical inputs without silently
upgrading them to the catalog.

For fresh hosted Linux qualification, set the action's optional
`dapr-runtime-version` to an exact retained runtime version (at least 1.18.0)
and include an official `dapr/cli` tar artifact named `dapr`, with member `dapr`.
Builds uses the same shared full initializer and job-bound backend receipt as
catalog setup, then exports the verified CLI to PATH. Leave shared `dapr` setup
disabled for this job: an existing Dapr home or initialization receipt fails
closed. This option refuses private/self-hosted workers and cannot initialize
over owner-prepared Azure fixtures. Product assertions still decide whether the
retained tuple qualifies; initialization never promotes a runtime.

## Package consumer mechanics

Reuse `Github/product-validation` from an enabled module action. Its
`hexalith.product-validation.v1` contract names a product-owned package manifest,
archive validator, target framework and one consumer source per package. Builds
packs existing Release outputs without rebuilding the module, validates archive
identity, authenticates job-local signed candidates, and restores/builds/runs
consumers with isolated feeds/caches and audits enabled. Failures propagate.
The signing identity is temporary validation infrastructure and never a publisher.

Product actions retain API/package/dependency assertions, historical wire
compatibility, CLI installation, pacts and ApiCompat baselines. Shared release
metadata validation alone does not prove these consumers work. Keep the audited
release build/security stages and centrally approved waivers unchanged.
