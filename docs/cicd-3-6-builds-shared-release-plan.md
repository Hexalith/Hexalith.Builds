---
story: '3-6'
created: '2026-11-11'
status: in-progress
planning_only: false
baseline_revision: 22387551f497bfef53af37c0c35d86b0d40c8d3f
implementation_repository: references/Hexalith.Builds
planning_baseline_revision: d4f75283221fc11779416c5f2b531ea09f22582c
implementation_baseline_revision: aca0183fdeb4a7fcd02546ff8f082acbf73a0585
implementation_integration_revision: 6ad5854aa14a9950e1fbfc32d7e27953a1c4b82b
---

# Builds shared release plan

Story 3-6 replaces Builds' legacy release caller and removes its matching
qualification CI gates for CAP-10/12/14/15 and AD-11/19/20. The user authorized
implementation on 2026-11-12; this companion now records that implementation
contract. The canonical intent, matrix, Code Map, execution tasks and
acceptance criteria live in the enclosing CiCd repository at
`_bmad-output/specs/spec-cicd/stories/3-6-rewrite-builds-release-yml-onto-the-shared-release.md`.

## Caller contract

Replace [builds-release.yml](../.github/workflows/builds-release.yml) with two
jobs. Keep manual dispatch only, empty top-level permissions, cache disabled
and serialized `release` concurrency without cancellation. Shared resolve
already rejects non-main dispatches.

1. `release` calls `./.github/workflows/release.yml`, passing
   `ci-workflow: builds-ci.yml` and `release-rules: builds`. Grant exactly
   `contents: write`, `actions: read`, `security-events: write`; pass only the
   named registry pull secrets from `HEXALITH_REGISTRY_PULL_USERNAME/PASSWORD`.
2. `publish` needs `release`, runs only for its `publish == 'true'` output,
   and uses `hexalith-release`. Grant `contents: write`, `actions: read`,
   `id-token: write`. Its sole step is `$/Github/publish`. Forward `publish`,
   `version`, `sha`, `last-tag`, `draft-id`, `artifact-id`, `artifact-digest`,
   `artifact-run` unchanged; pass `vars.HEXALITH_NUGET_USER` and environment
   secrets `HEXALITH_REGISTRY_USERNAME/PASSWORD`.

The shared runtime already implements Builds version rules, exact-source green
CI, strict boolean switch, current-dispatch security, one audited build,
original-artifact recovery, duplicate-safe publication, tag-last behavior and
exclusive outcome ownership. Remove legacy source checks, SDK/login/checkout,
semantic-release execution and qualification uploads from the caller. No new
release controller, API-key fallback, signed provenance or attestation grant.
The publisher executes no repository checkout or source build.

## CI cleanup and retained product checks

In [builds-ci.yml](../.github/workflows/builds-ci.yml), delete `build-and-test`,
the entire legacy `contracts` job, and the `codeql.yml` call together. Preserve
`dotnet`, `sample`, dependent `sample-windows`, `catalog`, `quality` and both
Python interpreter lanes, with their SDK prerequisites, outputs and secrets.
Align triggers with push-to-main, PR-to-main and dispatch. Only PR concurrency
cancels by ref; push/manual runs use SHA and are never cancelled. Retained job
failures determine the workflow result directly.

All fifteen current catalog commands are **KEEP** product checks under AD-11;
execute unchanged in their current language:

| `Tools/` file | Product reason |
| --- | --- |
| `validate-central-package-versions.ps1` | Evaluated package identities/versions |
| `test-central-package-version-validator.ps1` | Validator acceptance/rejection |
| `test-authoritative-package-catalog.ps1` | Catalog package/family authority |
| `test-logging-generator-selection.py` | Executed MSBuild generator selection |
| `validate-package-version-audit.ps1` | Catalog/source/dependency agreement |
| `test-package-version-audit-generator.ps1` | Audit generator behavior |
| `test-package-version-audit-validator.ps1` | Audit validator behavior |
| `validate-consumer-package-authority.ps1` | Evaluated consumer package graph |
| `test-consumer-package-authority-validator.ps1` | Authority validator behavior |
| `validate-package-version-exceptions.ps1` | Catalog exceptions/evaluated SDK requirements |
| `test-package-version-exception-validator.ps1` | Exception validator behavior |
| `validate-platform-version-catalog.ps1` | Platform/EventStore hosting compatibility |
| `test-platform-version-catalog.ps1` | Compatibility validator controls |
| `validate-dapr-package-versions.ps1` | Evaluated Dapr family compatibility |
| `test-dapr-package-version-validator.ps1` | Identity/timeout/rejection controls |

G-6 is still discovered by [run_python_tests.py](../Tools/ci/run_python_tests.py),
despite having no separately named YAML job. Delete these qualification-only
regression suites with the caller rewrite:

```text
Tools/test-archive-g4-qualification-evidence.py
Tools/test-prepare-g4-synthetic-acceptance.py
Tools/test-g6-cli-paths.py
Tools/test_g6_current.py
Tools/test-runtime-toolchain-disposition.py
Tools/test-runtime-toolchain-evidence-validator.py
Tools/test_runtime_toolchain_v2.py
test/qualification/test_stage3_live_qualification.py
Tools/test-g4-release-contract.ps1
Tools/test-g4-release-source.sh
Tools/test-g4-source-ci.ps1
Tools/test-g4-tool-package-contract-gate.ps1
Tools/test-g4-tool-package-artifact-validator.ps1
Tools/test-publish-g4-tool-packages.ps1
```

Remove the now-empty mandatory `test/qualification` runner group and adjust
[test_python_runner.py](../Tools/ci/tests/test_python_runner.py) behavioral
fixtures. Preserve interpreter selection, complete retained discovery and
failure propagation; do not add skip/exclusion controls.

Keep all .NET product test projects in `Hexalith.Builds.slnx`: Evidence.Tests,
Module.Tests and Tooling.IntegrationTests. Their public CLI/runtime behavior
tests remain product checks even when their names mention G-4 or G-6.

Preserve `Tools/test-g4-tool-package-contracts.ps1`,
`test-g4-packaged-platform-loader.ps1` and
`test-g4-packaged-seed-subscriptions.ps1`: despite their names these remain
executable dependencies of deferred legacy release tooling/root npm config.
Do not wildcard-delete `test-g4-*`. Keep frozen module-executed workflows and
composites, including `codeql.yml`, unchanged. Guard commitlint and
`commitlint-caller.yml` retirement remain story 5-1 work.

Remove only obsolete `builds-release.yml` exceptions from `.github/zizmor.yml`.
Keep the frozen-composite baseline and other legacy exemptions. Update
[Tools/ci/README.md](../Tools/ci/README.md) to describe the resulting jobs,
caller contract and classification. Retain executed release/adapter/shell
tests; remove any actual caller source-text assertion discovered during final
inspection. New tests must observe behavior rather than YAML strings.

## Coordinated rollout prerequisites

The maintainer switches Builds' nuget.org policy from `production` to
`hexalith-release` in the rewrite window, retaining workflow
`builds-release.yml` and package scope `Hexalith.Builds*` (AD-19). This plan
does not change that policy or any repository setting.

Before a separate production bootstrap, recheck App installation, exact
boolean publishing switch, org variable/pull-secret access, main-only
`hexalith-release` protection without reviewers/timer, Builds-owned push pair,
scoped nuget.org policy, immutable releases, 90-day retention, AD-21 rulesets
and a published release for every remaining stable `v*` tag.

Read-only planning checks found a concrete bootstrap stop: bot draft
`441591623`, `v4.47.1`, still names source
`75e1d4a3809b30684e67c7e9b2c8043720b49c53`, producer `43010609603` from
`.github/workflows/release-publish-probe.yml`, and original artifact
`13850760915`, digest
`sha256:18db4ed7b07ffa3c5fd4496afce65dd50888705687d42695260e7573a7456d30`,
unexpired until `2027-02-09T11:16:45Z`. A production caller selects that global
draft but fails its producer-path identity; its package inventory then
requires retaining it and failing. The maintainer must record authorized
disposition of the disposable draft/tag, reconcile probe-only NuGet policies
and deferred spike credentials, and recheck no interfering marked draft or
unpublished stable tag remains. Do not bypass authentication, create another
marked draft, delete resources or publish probe content to unblock the run.

The intended profile was `/home/administrator/.config/gh-cicd-agent`, with
token overrides absent, account `jpiquot`, target `Hexalith/Hexalith.Builds`.
`gh api repos/Hexalith/Hexalith.Builds/actions/variables/HEXALITH_RELEASE_PUBLISH_ENABLED`
returned HTTP 403, `Resource not accessible by personal access token`, with
required permission `actions_variables=read`. The switch remains unverified;
repository account metadata `admin: true` does not prove token authorization.
Verify it through an authorized read or maintainer confirmation before
bootstrap. No credential expansion or administration is performed now.

Preserve SDK/catalog pins and legacy root release configuration. SDK 10.0.404
supports isolated offline CI fixtures alongside SDK 11 from `global.json`;
the shared audited release restores online and does not run those fixtures.
No production release SDK defect is established by source inspection.

## Verification and delivery

The planning companion was pushed as `aca0183fdeb4a7fcd02546ff8f082acbf73a0585`.
The current authorized delivery implements the seven execution tasks in Builds,
then commits/pushes that repository and stages the canonical story with its
pushed gitlink in CiCd. CiCd is not committed or pushed. The separate production
bootstrap remains unauthorized; no retained resource or live setting is changed.

Implementation runs real actionlint/zizmor with authenticated online
audits; both locked Python suites; focused runner behavioral controls; all
fifteen unchanged catalog commands; and the committed-object public API check.
Compare frozen runtime bytes with the integrated upstream revision above, and
record concurrent upstream changes separately from this story. Observe a green
exact-source hosted **push** CI with the retained shared/sample/Windows jobs
and no retired qualification/aggregator/CodeQL jobs. Reuse existing executable
release-expression/adapter controls for the matrix; extend only uncovered
changed behavior.

Record full revisions/run/artifact identities and limits in
`docs/cicd-3-6-builds-shared-release-results.md`. Do not dispatch production to
manufacture test evidence while bootstrap preconditions fail or change the
switch just to obtain a branch. Local controls and earlier invalid-switch
probes do not establish positive hosted publication. A later authorized first
bootstrap must verify actual original artifact/package/outcome/final tag;
`ready` follows the separate AD-18 maintainer publication process.
