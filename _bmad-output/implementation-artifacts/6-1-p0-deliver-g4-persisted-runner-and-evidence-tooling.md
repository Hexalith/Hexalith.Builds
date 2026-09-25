---
work_package_id: 6.1-P0
story_key: 6-1-p0-deliver-g4-persisted-runner-and-evidence-tooling
artifact_kind: implementation-story
created: 2026-07-17
authorized: 2026-07-17
authorized_by: Jerome
source_action_status: open
status: in-progress
repository_authority: Hexalith/Hexalith.Builds
baseline_commit: edbaeaed68bcdb8deffcd98ed5652d237596e1d1
baseline_description: v4.19.2-13-gedbaeae, clean origin/main at authorization
accountable_owners:
  builds_owner: Jérôme Piquot
  platform_owner: Jérôme Piquot
  test_architect: Jérôme Piquot
implementation_dependencies: [6.1-P1R]
qualification_dependencies: [6.1-P1R, G-6]
parallel_with: []
unblocks: [6.1-P4]
target_date: uncommitted
estimate: XL
risk: high/critical evidence-chain risk
delivery_state:
  contract_and_validator: implemented
  descriptor_abi: owner-approved-loader-and-schemas-locally-qualified; runtime-composition-live-qualified-locally
  g6_qualification: refreshed-and-owner-accepted-2026-09-22; g4Approved-false
  stage3_slice_status: public-source-composition-qualified-10-of-10-and-110-of-110; owner-and-named-test-architect-stage3-assessments-accepted; stage3-complete; p0-open
  package_controls: implemented-and-locally-qualified; protected-release-and-remote-proof-pending
  supported_composition: validated-public-run-down-and-full-persisted-profile-qualified-locally; unsupported-profiles-HXR029-nonpassing; packaged-executable-composition-qualified-locally-stage5.6
  persisted_qualification: stage4-public-source-full-profile-qualified; stage5-packaged-vstest-and-mtp-native-report-evidence-qualified-locally-dirty-tree; stage5-review-remediated-and-requalified-0.0.0-stage5.7-2026-09-25; stage5-requalified-clean-tree-0.0.0-stage5.8-eventstore-3.108.1-2026-09-25
  published_consumer_pin: absent
  owner_acceptance: stage3-evidence-accepted-2026-09-23; full-p0-acceptance-absent
  test_architect_acceptance: stage3-evidence-accepted-2026-09-23; full-p0-acceptance-absent
accepted_p1r_baseline:
  acceptance_record: Hexalith.Projects/_bmad-output/implementation-artifacts/6-1-p1r-acceptance.json
  accepted_at_utc: 2026-09-22T17:11:38Z
  eventstore_version: 3.106.0
  eventstore_tag: v3.106.0
  eventstore_revision: 76051c70cbf868c40edc00ca0344fa5bd8879b69
  builds_revision: ad52f350a2f0bc47849179ae17b4594dafff5363
  rollback_eventstore_version: 3.70.1
  rollback_eventstore_revision: f13f9925fdca53efa2ab8c90d396ab106f91bb9c
  rollback_builds_revision: 7af20f8bafbfe561df6f7705913a0800603090b5
  current_head_at_resumption: 2fba3497043fe5ffcfe4dc44c51a09eae9b950ab
  current_head_relation: accepted-revision-descends-from-current-head; alignment-applied-in-working-tree
packages:
  module_cli:
    id: Hexalith.Builds.Module.Cli
    command: hexalith-module
  evidence_cli:
    id: Hexalith.Builds.Evidence.Cli
    command: hexalith-evidence
version_policy:
  mode: Builds semantic-release lockstep
  latest_stable_at_authorization: 4.19.2
  expected_first_stable: 4.20.0
  prerelease_channel: GitHub Packages
  stable_channel: NuGet.org
schemas:
  module_manifest: hexalith.module-manifest.v1
  module_run_evidence: hexalith.module-run-evidence.v1
  readiness_evidence: hexalith.readiness-evidence.v1
  p0_acceptance: hexalith.g4-p0-acceptance.v1
acceptance_record:
  path: evidence/g4/6.1-p0-acceptance.json
  status: absent
  validator: "dotnet tool run hexalith-evidence validate evidence/g4/6.1-p0-acceptance.json"
rollback:
  package_pin: none-greenfield
  previous_released_builds_tag: v4.19.2
  previous_released_builds_revision: 8e0e2da5e1eff07468b41d85d97979c96c2ac975
  behavior: remove consumer tool adoption through an authorized change and keep dependent stories blocked; never reset a working source tree
exit_codes:
  success: 0
  usage_or_manifest: 1
  prerequisite_unavailable: 2
  topology_or_lifecycle: 3
  product_or_test: 4
  persisted_state: 5
  evidence_schema_or_policy: 6
  cancelled: 130
traceability:
  requirements_supported: [fr-2, fr-5]
  nfrs: [nfr-11]
  supporting_nfrs: [nfr-1, nfr-5, nfr-10]
  architecture: [AD-25, AD-30]
  findings: [TEST-001]
  enables_evidence_rows:
    - release-authenticated-persisted-boundary
    - release-cross-tenant-isolation
    - release-restart-concurrency
    - release-privacy
    - release-performance
    - release-smoke
    - release-rollback
---

# Story 6.1-P0: Deliver the G-4 Persisted Runner and Evidence Tooling

Status: in-progress

## Story

As a Hexalith module developer and Test Architect,
I want pinned Builds-owned tools that compose the supported persisted multi-module runtime and validate deterministic evidence,
so that Projects Story 6.1 and later consumers can prove supported-path behavior without consumer-owned topology, copied platform code, or hand-authored pass claims.

## Acceptance Criteria

1. **Owner and package authority is fixed.** Given Jerome's 2026-07-17 authorization, when implementation starts, then both tools are developed in `Hexalith/Hexalith.Builds` from starting baseline `edbaeaed68bcdb8deffcd98ed5652d237596e1d1`; the package IDs are `Hexalith.Builds.Module.Cli` and `Hexalith.Builds.Evidence.Cli`; their commands are `hexalith-module` and `hexalith-evidence`; and both packages use the same semantic-release version. Normal descendant implementation commits produce the delivery revision and do not require story edits. An unrelated rebase/merge before the first implementation commit, package/schema/ownership change, or non-descendant delivery baseline requires both authority records to be updated before qualification; final evidence records the actual delivery revision.

2. **Pinned, independently consumable local tools.** Given a clean consumer checkout with no authoritative `bin` or `obj` output, when `dotnet tool restore` runs against a checked-in `.config/dotnet-tools.json`, then exact published versions of both tools restore and all supported `dotnet tool run` commands work without globally installed tools, source-tree scripts, or copied Builds/platform code. Debug/source qualification and Release/package qualification use the same public command surface; neither becomes a second consumer contract.

3. **Strict `hexalith.module-manifest.v1` validation.** Given a checked-in, non-secret module manifest, when `run`, `down`, or `test` starts, then validation completes before Aspire startup or runtime mutation. A consumer manifest may name one or more module descriptor assemblies, sibling dependencies, deterministic domain/application/resource identifiers, UI descriptor, and known fixture profiles using canonical repository-relative paths; the P0 qualification fixture declares at least two modules. Unknown schema versions or fields, duplicate or nondeterministic IDs, missing assemblies/profiles, malformed dependencies, absolute paths, path escape, unresolved placeholders, and secret-bearing values fail closed with stable diagnostics.

4. **Runner-owned composition and lifecycle.** Given a valid manifest and available prerequisites, when `hexalith-module run`, `down`, or `test` executes, then the runner owns EventStore, Dapr, identity and generated development-secret injection, FrontComposer, dynamic ports/endpoints, health/readiness, telemetry, Aspire lifecycle, invocation state, cancellation, and bounded cleanup. It changes no consumer or sibling repository. Each invocation operates only on resources bearing its run identity; `down` is idempotent; failure and cancellation attempt safe cleanup while retaining metadata-only failure evidence.

5. **Real persisted multi-module qualification.** Given the approved P0 fixture, when its full profile runs, then the supported platform composes at least two modules and proves an authenticated write, expected persisted event, expected projection/read state and sequence, stop, restart, rehydrated read, retry/idempotency behavior, and two-instance access. A run-unique Tenant/domain/resource namespace prevents stale state from satisfying assertions. Missing event or projection state, wrong sequence, stale state, cross-Tenant state, or fake/in-memory persistence is non-passing.

6. **All AD-25 profile classes are orchestratable.** Given a profile and optional test filter, when `hexalith-module test` runs, then the packaged runner supports pure-domain, host-contract/descriptor, persisted-boundary, restart, two-instance, and authenticated browser, CLI, and MCP profile classes with stable endpoint, identity, report, and artifact handoff contracts. P0 proves orchestration; product stories provide their own assertions and pass evidence. VSTest and Microsoft Testing Platform/xUnit v3 execution remain supported, and missing/invalid reports, zero matching tests, all-skipped tests, unavailable prerequisites, failed test steps, or failed assertions never pass.

7. **Deterministic `hexalith.module-run-evidence.v1` output.** Given a completed, failed, unavailable, or cancelled invocation, when evidence is emitted, then a canonical machine-readable artifact records at least schema, run ID, timestamps, repository revision and dirty marker, SDK/OS, tool/package versions, manifest/profile/fixture identities and hashes, exact command, module/platform pins, phase outcomes, persisted assertions and expected sequences, test counts, report/artifact paths and hashes, final status, stable rule IDs, and failure category. Ordering, UTF-8, final newline, and repo-relative paths are canonical; volatile timestamps, ports, and run IDs are identified so semantic comparisons are deterministic.

8. **Execution failure and evidence failure are distinct.** Given a usage, manifest, prerequisite, topology, test/product, persisted-state, parser, evidence-policy, or cancellation outcome, when a command terminates, then human and JSON diagnostics expose a stable phase, category, and rule ID and the process returns exactly: `0` success, `1` usage/manifest, `2` prerequisite unavailable, `3` topology/lifecycle, `4` product/test, `5` persisted-state, `6` evidence schema/policy, or `130` cancellation. Runner/execution failure is therefore machine-distinguishable from evidence failure. Phase-aware short-circuiting retains the first causal failure; numeric precedence cannot overwrite it, and partial output never claims `passed`.

9. **Fail-closed AD-30 readiness validation.** Given a `hexalith.readiness-evidence.v1` YAML matrix, when `hexalith-evidence validate` runs, then YAML parsing rejects duplicate keys and unsupported schemas before business-rule validation, row defaults are resolved to effective rows, and the canonical `key` identity is validated. Missing/duplicate keys, placeholders, missing owner/version/dependencies or gates/command/artifact path/estimate/status/release disposition, incomplete FR/NFR/P1/P2/release coverage, failed critical evidence, unexplained critical skips, passed-on-unavailable, and Markdown/YAML identity drift fail with deterministically sorted source/row/rule/field/location/hint diagnostics. Pending, blocked-external, or not-verified rows may reference future paths; actual artifact existence, readability, schema, and hash are required only when a row claims executed or passing evidence.

10. **Undeclared `blocked` is rejected explicitly.** Given the current Projects matrix uses `blocked` on a terminal release row while its legend does not declare that value, when the validator processes it, then it returns a stable undeclared-status diagnostic and a nonzero evidence-validation outcome. The validator does not silently invent status semantics or mutate the input. Projects owners must reconcile the canonical matrix under their own authority; P0 provides a separate conforming positive sample.

11. **Packaged positive and negative controls block CI.** Given curated fixtures, when the P0 contract suite invokes the packed tools, then the positive manifest, module-run evidence, and readiness matrix pass, while each negative fixture fails with its expected stable category/rule ID. Controls include unsupported schemas, unknown fields, absolute/escaping paths, duplicate IDs/YAML keys, secrets/placeholders, tampered version pins, absent event or projection, stale/cross-Tenant state, missing/invalid/zero/all-skipped test reports, incomplete coverage or required row metadata, missing/invalid actual artifacts for passed rows, failed/unexplained-skipped critical evidence, undeclared status, and passed-on-unavailable. Controls are blocking and never skipped or quarantined.

12. **All retained output is metadata-only.** Given any command path, when logs, invocation state, telemetry, reports, and evidence are inspected, then they contain no bearer token, credential, generated secret, raw environment dump, source payload, transcript, prompt, user content, or protected Tenant/resource detail. The runner owns development credential creation/injection, redacts retained command output, and never serializes secret values into manifests or evidence.

13. **Clean-checkout source/package parity and publication.** Given a clean checkout at the accepted revision, when CI qualifies the delivery, then it builds/tests with the repository SDK, packs both tools in Release, generates an exact-version temporary local-tool manifest, restores those exact local packages, invokes every positive and blocking negative control, and proves equivalent Debug/source and Release/package semantics without stale artifacts. Release must fail unless exactly the two approved package IDs exist at the computed lockstep version with `.nupkg`, `.snupkg`, and recorded hashes. Prereleases publish those Release-built artifacts to GitHub Packages; stable releases publish them to NuGet.org. After publication, an exact-version checked-in sample consumer manifest proves clean remote restore. The expected first stable is `4.20.0` if semantic-release still computes it; evidence and consumer manifests use only the version actually published.

14. **Greenfield rollback is truthful and exercised.** Given there is no previous tool package, when the first rollout is rolled back, then the procedure runs idempotent `down`, retains failed evidence, removes or reverts consumer local-tool/manifest adoption through an authorized consumer change, records the previous released Builds boundary `v4.19.2` / `8e0e2da5e1eff07468b41d85d97979c96c2ac975`, and leaves Story 6.1 on its existing runtime and blocked. Rollback never resets a developer's working source tree. After a prerelease is published and qualified, that exact prerelease becomes the first legitimate known-good package pin for stable promotion; no nonexistent prior tool version is fabricated.

15. **P0 handoff is complete but does not self-accept Story 6.1.** Given all P0 evidence passes, when Builds Owner, Platform Owner, and Test Architect accept the exact revision, published packages, schemas, commands, persisted fixture, samples, negative-control results, and rollback procedure, then Projects can pin and invoke the tools without copying their implementation and 6.1-P4 may consume the record. P0 completion alone does not satisfy P1/P2/P3, create the P4 entry-gate artifact, or unblock Story 6.1.

## Tasks / Subtasks

### Approved remaining acceptance stages — 2026-08-01 rebaseline

**2026-09-22 resumption:** Projects accepted P1R through
`_bmad-output/implementation-artifacts/6-1-p1r-acceptance.json` at
`2026-09-22T17:11:38Z`. The selected Builds revision `ad52f350a2f0bc47849179ae17b4594dafff5363`
is a descendant of this checkout's initial `2fba3497043fe5ffcfe4dc44c51a09eae9b950ab`;
this worktree aligns the runner, schema, and fixture pins to the accepted `3.106.0`
tuple. That acceptance closes only Stage 1. P0 still requires supported composition,
packaged controls, publication, persisted evidence, and its own named-owner acceptance.
Older candidate observations and dated validation entries below remain historical.

This binding stage order supersedes the chronological ordering implied by the older task groups below. Preserve completed implementation and its evidence; do not reopen a checked item unless current-head reconciliation disproves it.

- [x] **Stage 1 — Revalidate the dependency baseline through 6.1-P1R** (AC: 1-6, 13-15)
  - [x] Consume the owner-approved EventStore source/package, Builds catalog, runner manifest/schema, and Architecture Spine tuple recorded in the 2026-09-22 P1R acceptance record.
  - [x] Preserve `3.88.0` as historical candidate evidence; use the accepted `3.106.0` selected tuple and `3.70.1` rollback tuple for subsequent P0 work.
- [x] **Stage 2 — Reconcile release and package findings against the current Builds head** (AC: 1, 2, 11, 13, 14)
  - [x] Recheck SD1, SD2, and SP1-SP13 against the accepted 3.106.0 tuple and current Builds worktree; close the local implementation findings with the full package gate.
  - [x] Implement and locally verify exact-SHA CI, post-approval live-branch recheck, pre-tag qualification, partial-publication recovery, remote verification logic, credential scope, installed-command contracts, and source/package parity. Retain actual protected-run, remote-feed, rollback, and persisted evidence requirements in Stages 3-7.
- [x] **Stage 3 — Implement supported runtime composition** (AC: 4-6, 8, 12; local supported source command scope, with profile execution and packaged live composition retained in later stages)
  - [x] Remove the unconditional prerequisite stop only after P1R and affected G-6 dependencies are accepted.
  - [x] Prove runner-owned EventStore, Dapr, identity, FrontComposer, endpoints, health, telemetry, Aspire lifecycle, run-state isolation, cancellation, and cleanup without consumer-owned topology.
- [x] **Stage 4 — Qualify the real two-module persisted fixture** (AC: 5, 6, 11-14)
  - [x] Execute persisted write/read, stop/restart/rehydration, retry/idempotency, two-instance access, authenticated access, cross-Tenant denial, stale-state, wrong-sequence, and unavailable-prerequisite controls.
- [x] **Stage 5 — Capture native reports and deterministic evidence through packaged tools** (AC: 6-13; local packaged scope at a dirty tree — clean-revision, published-package, and acceptance evidence remain Stages 6-7)
  - [x] Bind native report results, evidence hashes, exact commands, fixture/manifest identities, and negative-control outcomes into deterministic metadata-only artifacts.
  - [x] Implement fail-closed validation of `hexalith.g4-p0-acceptance.v1` through the packaged `hexalith-evidence` command.
- [ ] **Stage 6 — Publish, remotely restore, and prove rollback** (AC: 2, 13, 14)
  - [ ] Publish the exact prerelease, verify both packages and hashes remotely, restore them into a clean consumer using an exact checked-in tool manifest, and exercise duplicate-safe rollback/retry behavior.
- [ ] **Stage 7 — Obtain owner acceptance and hand off to P4** (AC: 15)
  - [ ] Emit `evidence/g4/6.1-p0-acceptance.json` with exact revisions/pins, package/feed identities and hashes, commands, live-lane results, native report/evidence hashes, cleanup/rollback results, and dated approvals.
  - [ ] Require Builds Owner, Platform Owner, and a named Test Architect to approve the exact record before Projects can mark P0 done or P4 can consume it.

- [x] Establish the Builds tool project and package spine (AC: 1, 2, 13)
  - [x] Add root `global.json` pinned to SDK `10.0.302` with the approved patch roll-forward policy.
  - [x] Add root `Directory.Build.props` importing `Hexalith.Build.props`, then override `ProjectRoot` to this repository and set Builds-specific product/repository/package metadata so package contents and SourceLink do not point at the parent workspace or `Hexalith/Hexalith`.
  - [x] Add root `Directory.Packages.props` importing `Props/Directory.Packages.props`, and add `src/libraries/Directory.Build.props` importing the repository package props for packable projects.
  - [x] Add `src/libraries/Hexalith.Builds.Module.Cli/Hexalith.Builds.Module.Cli.csproj` as a `net10.0` packed .NET tool with package ID `Hexalith.Builds.Module.Cli`, `PackAsTool`, and `ToolCommandName=hexalith-module`.
  - [x] Add `src/libraries/Hexalith.Builds.Evidence.Cli/Hexalith.Builds.Evidence.Cli.csproj` with package ID `Hexalith.Builds.Evidence.Cli`, `PackAsTool`, and `ToolCommandName=hexalith-evidence`.
  - [x] Put shared code in narrowly scoped internal namespaces or an owner-approved package/project; do not duplicate parsers/diagnostics or create a third published package without updating the authority record.
  - [x] Add both projects and all test projects to `Hexalith.Builds.slnx`; retain central package management and analyzer/nullability settings.

- [x] Implement the module manifest and command contract (AC: 2-4, 6, 8, 12)
  - [x] Define `hexalith.module-manifest.v1` models and strict validation before lifecycle work.
  - [x] Implement `run`, `down`, and `test` with cancellation, stable human/JSON diagnostics, phase-aware outcomes, and invocation-scoped state. **Code-review correction (2026-07-21, CD1):** the invocation-scoped state store (`ModuleInvocationStateStore.CreateAsync`) and run-identity plan (`ModuleRuntimePlan.Create`) are implemented but currently unreachable dead code — every `run`/`test` short-circuits at the always-unavailable `RuntimePrerequisiteGate` before reaching them. Run-identity resource scoping is neither demonstrable nor test-covered yet. Treat as deferred-until-G-6 and unverified; the Test Architect must re-check this scoping once the prerequisite gate opens under "Implement supported platform composition" below, before P0 acceptance.
  - [x] Implement and contract-test the exact exit-code map in frontmatter; do not assign rule severity by numeric exit-code precedence.
  - [x] Define stable pure-domain, host-contract/descriptor, persisted, restart, two-instance, browser, CLI, and MCP profile classes without product assertions in the runner.
  - [x] Validate path containment, duplicate/unknown fields and IDs, dependencies, assemblies, profiles, placeholders, and the metadata-only secret boundary.

- [ ] Implement supported platform composition (AC: 4-6, 8, 12)
  - [ ] Reuse EventStore/Aspire composition and testing seams; do not copy topology into consumers or reimplement EventStore, Dapr, identity, FrontComposer, health, telemetry, or Aspire ownership.
  - [ ] Own dynamic endpoints, readiness, development identity/secret injection, run-state, cancellation, cleanup, and idempotent teardown inside the tool.
  - [ ] Preserve existing Projects AppHost/runtime until replacement lanes and later cutover gates pass.
  - [x] Treat unavailable critical prerequisites as explicit non-passing evidence, not skipped success.

- [x] Add the real P0 persisted fixture (AC: 5, 6, 11-14)
  - [x] Add a valid two-module manifest and run-unique persisted fixture under owner-repository test assets.
  - [x] Prove authenticated write, persisted event, projection/read state and sequence, stop/restart/rehydration, retry/idempotency, two-instance access, and Tenant isolation.
  - [x] Add stale-state, absent-event, absent-projection, wrong-sequence, cross-Tenant, cancellation, and prerequisite-unavailable controls.
  - [x] Keep fake/static topology checks as unit regressions only; they cannot satisfy P0.

- [ ] Emit deterministic module-run evidence (AC: 6-8, 11, 12)
  - [x] Define `hexalith.module-run-evidence.v1`, canonical serialization, metadata allowlist, hash rules, volatile fields, and artifact retention.
  - [x] Capture VSTest and MTP/xUnit v3 native results without hiding their exit status or report semantics.
  - [ ] Test successful, partial, unavailable, cancelled, runner-failed, test-failed, state-failed, and evidence-failed output.
  - [ ] Seed tokens/secrets in tests and prove redaction across logs, state, reports, JSON, and diagnostics.

- [x] Implement `hexalith-evidence validate` (AC: 8-12)
  - [x] Parse YAML with duplicate-key/unsupported-schema failure separated from business-rule evaluation.
  - [x] Resolve defaults and validate effective row identities, coverage, metadata, gates, artifacts, statuses, critical outcomes, and release dispositions.
  - [x] Reconcile the Markdown view by stable row identity without treating it as another source of truth.
  - [x] Reject undeclared `blocked` with a stable rule ID until Projects declares or corrects it.
  - [x] Add canonical positive samples and one packaged-command negative fixture per stable rule category.

- [ ] Add blocking test projects and clean-checkout qualification (AC: 2-13)
  - [x] Add xUnit v3/Shouldly unit and contract projects under `test/` for manifest, command, lifecycle, evidence, validator, redaction, and fixture behavior.
  - [x] Add a live persisted integration project that exercises the packed runner with at least two modules and actual EventStore/Dapr state.
  - [x] Preserve the existing `Tools/test-domain-workflow-test-platforms.ps1` VSTest/MTP contract; extend or reuse it rather than assuming one platform.
  - [x] Run package-consumer qualification from a clean temporary checkout/feed with no stale `bin`/`obj` authority.
  - [ ] Generate a temporary `.config/dotnet-tools.json` pinned to the computed local package version for prepublication tests; after publication, commit an exact-version sample under `test/fixtures/package-consumer/.config/dotnet-tools.json` and prove remote restore.

- [ ] Extend Builds CI and semantic release for tool packages (AC: 1, 2, 11, 13, 14)
  - [x] Update `.github/workflows/build-release.yml` to verify, build, test, pack, restore, and execute positive/negative controls before release creation.
  - [x] Extend the existing semantic-release lifecycle so this repository publishes tool `.nupkg`/`.snupkg` artifacts: prerelease to GitHub Packages and stable to NuGet.org.
  - [x] Add Builds-local `Tools/build-g4-tool-packages.ps1`, `publish-g4-tool-packages.ps1`, and `test-g4-tool-package-contracts.ps1`; always build the published tool artifacts in Release and preserve the existing shared `Github/scripts/*.ps1` behavior for consumer repositories.
  - [x] Make package preparation fail unless exactly `Hexalith.Builds.Module.Cli` and `Hexalith.Builds.Evidence.Cli` exist at the computed version with both NuGet and symbol packages; record SHA-256 hashes before publication.
  - [x] Update `package.json` and `package-lock.json` with `@semantic-release/exec`, then bind `prepareCmd`/`publishCmd` to the Builds-local tool-package scripts without replacing the existing changelog/GitHub release plugins.
  - [x] Update `Github/create-release/action.yml` and its README only as needed to describe/pass through repository-configured package lifecycles while preserving its external action contract.
  - [x] Supply `NUGET_API_KEY` for stable publication and add exact `packages: write` permission for GitHub Packages prereleases without broadening unrelated workflow permissions.
  - [ ] Record the actual semantic-release version and package hashes; do not hard-code `4.20.0` if repository history computes another value.

- [ ] Document adoption, evidence, and rollback (AC: all)
  - [x] Update `README.md` and `Tools/README.md` with local-tool restore, `run|down|test`, evidence validation, profile, diagnostic, metadata/redaction, and troubleshooting contracts.
  - [ ] Publish the owner-approved revision, package/version inventory, schemas, valid fixture, generated samples, negative-control result, and cleanup/rollback drill.
  - [ ] Provide Projects with exact `.config/dotnet-tools.json` pins and manifest/schema guidance without directly modifying Projects in this story.
  - [ ] Obtain Builds Owner, Platform Owner, and Test Architect acceptance and route the immutable evidence record to 6.1-P4.

### Review Findings

Code review — **Chunk B (Evidence tooling)**, 2026-07-21. Scope: `src/libraries/Hexalith.Builds.Tooling/Evidence/*`, `.../RunEvidence/*`, `test/Hexalith.Builds.Evidence.Tests/*`, `test/Hexalith.Builds.Module.Tests/ModuleRunEvidenceSerializationTests.cs`, `test/fixtures/evidence/*` (baseline `edbaeaed`..HEAD `7708256`). Four blind review layers (adversarial, edge-case, verification-gap, acceptance-audit). Severities set by triage. Chunks A (Input contract), C (Runtime), D (Surface & controls) not yet reviewed. 3 findings dismissed as noise.

**Decision-needed — resolved 2026-07-21 by Jerome (all → patch):**

- [x] \[Review]\[Patch] D1 · HIGH · Readiness `passed` rows are not bound to the artifact they cite — only existence + SHA-256 + schema + `finalStatus`/`exitCode` are checked; `invocation.command`/`profile`/`topology` are never compared to the row's `verification_command`/`fixture`. Shipped positive fixture proves it: row `release-smoke` cites `release-passed.json` whose command is `hexalith-module down …`. [`src/libraries/Hexalith.Builds.Tooling/Evidence/ReadinessEvidenceValidator.cs`:986-998] — **Resolved:** bind now — cross-check the artifact command/profile/fixtureHash against the row, and fix the misleading positive fixture.
- [x] \[Review]\[Patch] D2 · HIGH · `HXE149` ignores test counts — zero-test / all-skipped artifacts satisfy a `passed` row (AC 6/11). [`.../Evidence/ReadinessEvidenceValidator.cs`:993-997; `.../RunEvidence/ModuleRunEvidenceArtifactValidator.cs`:307-310] — **Resolved:** enforce in validator — a `passed` row's artifact must report `reported:true`, `passed>0`, `failed==0`.
- [x] \[Review]\[Patch] D3 · HIGH · Secret detection is a 6-substring denylist — sole AC-12 enforcement in the artifact validator; misses JWT/`ghp_`/PEM/`Pwd=`/connection strings. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ManifestSecretDetector.cs`:18-28] — **Resolved:** strengthen now — add JWT (`eyJ…`), `ghp_`/`github_pat_`, PEM private-key headers, `pwd=`/`pass=`, and broaden key/secret shapes.
- [x] \[Review]\[Patch] D4 · MEDIUM · P1/P2 coverage not enforced — `finding` coverage parses only the first integer of e.g. `"9 P1 + 7 P2"`; row `priority` unused (AC 9). [`.../Evidence/ReadinessEvidenceValidator.cs`:1010,1064-1087] — **Resolved:** implement P1/P2 — parse sub-counts and validate each against rows by `priority`.
- [x] \[Review]\[Patch] D5 · MEDIUM · Evidence envelope cannot represent AC-7 persisted assertions / expected sequences — no field; schema is `additionalProperties:false`. [`.../RunEvidence/ModuleRunEvidence.cs`; `.../RunEvidence/ModuleRunEvidenceArtifactValidator.cs`:72-86] — **Resolved:** extend v1 now — add optional additive `persistedAssertions`/`expectedSequences` fields to the model + schema (factory emits empty for now).

**Patch** (unambiguous fixes):

- [x] \[Review]\[Patch] P1 · HIGH · Human-mode diagnostics violate AC 9 — emits only `Diagnostics[0]` (RuleId/Phase/Category/Message), dropping Source/Row/Field/Location/Hint and later diagnostics; human is the default output. [`src/libraries/Hexalith.Builds.Tooling/Diagnostics/ToolDiagnosticFormatter.cs`:46-52]
- [x] \[Review]\[Patch] P2 · MEDIUM · `critical: yes|on|1|y` silently non-critical (compares to `"true"` only) — evades HXE141/142; fail closed on unrecognized values. [`.../Evidence/ReadinessEvidenceValidator.cs`:898-899]
- [x] \[Review]\[Patch] P3 · MEDIUM · Subprocess capture deadlock + untimed `WaitForExit()`. [`.../RunEvidence/ModuleRunEvidenceFactory.cs`:228-231]
- [x] \[Review]\[Patch] P4 · MEDIUM · Non-scalar `markdown_view` silently skips HXE151 drift control. [`.../Evidence/ReadinessEvidenceValidator.cs`:804-808]
- [x] \[Review]\[Patch] P5 · MEDIUM · Duplicate-key vs invalid-YAML keyed on English substring `"duplicate"` in a YamlDotNet exception message. [`.../Evidence/ReadinessEvidenceValidator.cs`:466]
- [x] \[Review]\[Patch] P6 · MEDIUM · Markdown drift check couples to one renderer (backtick-wrapped first column only). [`.../Evidence/ReadinessEvidenceValidator.cs`:860-874]
- [x] \[Review]\[Patch] P7 · LOW · Rule ID `HXE001` reused for two meanings (duplicate-key vs evidence-write failure), violating AC-8 stable IDs. [`.../RunEvidence/ModuleRunEvidenceWriteResult.cs`:27-35]
- [x] \[Review]\[Patch] P8 · LOW · Test-count consistency defeatable by `int` overflow. [`.../RunEvidence/ModuleRunEvidenceArtifactValidator.cs`:309]
- [x] \[Review]\[Patch] P9 · LOW · Placeholder detection misses `%…%` and `$VAR`, inconsistent with the manifest side. [`.../Evidence/ReadinessEvidenceValidator.cs`:197-203]
- [x] \[Review]\[Patch] P10 · MEDIUM · No negative fixture/test for HXE149/HXE130/HXE147/HXE146/HXE143/HXE144 — deleting/inverting these branches leaves all tests green. [`test/Hexalith.Builds.Evidence.Tests/ReadinessEvidenceValidatorTests.cs`]
- [x] \[Review]\[Patch] P11 · MEDIUM · Secret guards untested: `ManifestSecretDetector` has no test; `ShouldNotContain("Bearer")` assertion guards nothing. [`test/Hexalith.Builds.Module.Tests/ModuleRunEvidenceSerializationTests.cs`:67]
- [x] \[Review]\[Patch] P12 · MEDIUM · Determinism only partially pinned (single-module test hides `OrderBy(Id)`; no golden-byte snapshot; timestamp format unasserted). [`test/Hexalith.Builds.Module.Tests/ModuleRunEvidenceSerializationTests.cs`:30-68]
- [x] \[Review]\[Patch] P13 · MEDIUM · `ModuleRunEvidenceWriter.WriteAsync` never driven — path containment, `.json` requirement, atomic temp→rename unverified. [`src/libraries/Hexalith.Builds.Tooling/RunEvidence/ModuleRunEvidenceWriter.cs`]
- [x] \[Review]\[Patch] P14 · LOW · The 6 evidence `.expected.json` snapshots are dead fixtures (no test loads them). [`test/fixtures/evidence/**/*.expected.json`]

**Deferred**:

- [x] \[Review]\[Defer] YAML parse-bomb — deserialized twice with no anchor/alias/depth bound. [`.../Evidence/ReadinessEvidenceValidator.cs`:459-485] — deferred, low priority (semi-trusted in-repo input; YamlDotNet lacks native alias caps).
- [x] \[Review]\[Defer] `IsArtifactHashes` validates hash values but not keys for path/secret shape. [`.../RunEvidence/ModuleRunEvidenceArtifactValidator.cs`:173-178] — deferred, low priority (keys never propagated to the readiness result).

### Review Findings — Chunk C (Runtime & test-report orchestration)

Code review — **Chunk C (Runtime & test-report orchestration)**, 2026-07-21. Scope: `src/libraries/Hexalith.Builds.Tooling/Runtime/*`, `.../TestReports/*`, `test/Hexalith.Builds.Module.Tests/{ModuleCommandApplicationTests,NativeTestReportLoaderTests,PersistedFixtureAssetTests}.cs` (baseline `edbaeaed`..HEAD `7708256`). Four blind review layers. The reachable/shipped path is fail-closed and correct (live runner deferred behind an always-unavailable prerequisite gate); findings are the classifier fail-opens, one reachable `down` robustness gap, and a latent cluster in the deferred composition/state code. 1 finding dismissed.

**Decision-needed:**

- [x] \[Review]\[Decision] CD1 · Traceability honesty — Task 2 is checked `[x]` ("invocation-scoped state") and Dev Notes claim an invocation-metadata state contract, but `ModuleRuntimePlan.Create` + `ModuleInvocationStateStore.CreateAsync` are unreachable dead code (`RuntimePrerequisiteGate.Check` always returns unavailable). Run-identity resource scoping is neither demonstrable nor tested. — **Resolved 2026-07-21:** annotated Task 2 and Dev Notes with a code-review correction marking the state machinery deferred-until-G-6 and unverified.

**Patch:**

- [x] \[Review]\[Patch] CP1 · HIGH · TRX loader ignores `ResultSummary/@outcome`; an `outcome="Aborted"/"Failed"/"Timeout"` run with clean per-test counters loads as a pass (real VSTest/MTP run-level-failure shape), violating AC 6 "failed test steps never pass". [`src/libraries/Hexalith.Builds.Tooling/TestReports/NativeTestReportLoader.cs`:82-94]
- [x] \[Review]\[Patch] CP2 · MEDIUM · Negative TRX counter components (`failed="1" error="-1"` → `failed=0`) satisfy the aggregate sum check and load as a pass for a failed run. Bounds-check each parsed component ≥ 0. [`.../TestReports/NativeTestReportLoader.cs`:175-192]
- [x] \[Review]\[Patch] CP3 · MEDIUM · `DownAsync` does not catch `UnauthorizedAccessException` on read/delete; a foreign `.json` in the world-shared temp dir turns idempotent `down` into a `TopologyOrLifecycle` (exit 3) failure. Skip unreadable/undeletable files. [`src/libraries/Hexalith.Builds.Tooling/Runtime/ModuleInvocationStateStore.cs`:79-116]
- [x] \[Review]\[Patch] CP4 · LOW · Dead switch arm `skipped >= total` is unreachable after the `passed==0` arm. [`.../TestReports/NativeTestReportLoader.cs`:109]
- [x] \[Review]\[Patch] CP5 · MEDIUM · Test gaps: no coverage for HXT001 (missing/unreadable report), HXT002 (duplicate/absent `ResultSummary`/`Counters`, malformed XML), the CP1/CP2 fail-opens, or HXR004 (lifecycle exit 3), plus `down` idempotency. [`test/Hexalith.Builds.Module.Tests/NativeTestReportLoaderTests.cs`, `ModuleCommandApplicationTests.cs`] — **Resolved 2026-07-21:** added HXT001/HXT002/HXT006/negative-counter coverage to `NativeTestReportLoaderTests.cs`, and a new `ModuleInvocationStateStoreTests.cs` covering scoped/idempotent `down`, tolerance of a foreign file in the shared state dir, and the manifest-reread `IOException` that maps upstream to HXR004/exit 3 (direct unit-level coverage of that failure mode — driving it through the full CLI race is not deterministically reproducible without an instrumentation hook).

**Deferred (belongs with the live runner — P1R/G-6; all latent behind the deferred prerequisite gate):**

- [x] \[Review]\[Defer] Run-identity teardown scoping — `DownAsync` keys cleanup on manifest hash, not RunId (AC 4). [`.../Runtime/ModuleInvocationStateStore.cs`:83] — deferred, latent (CreateAsync unreachable).
- [x] \[Review]\[Defer] World-shared state dir `hexalith-builds/runs` is not user-scoped. [`.../Runtime/ModuleInvocationStateStore.cs`:96] — deferred, latent.
- [x] \[Review]\[Defer] Failure/cancellation paths attempt no resource/state cleanup (AC 4 "attempt safe cleanup"). [`.../Runtime/ModuleCommandExecutionService.cs`:210-262] — deferred, latent (nothing composed pre-gate).
- [x] \[Review]\[Defer] Non-atomic state write (`File.WriteAllTextAsync`) orphans corrupt files `down` can never reclaim. [`.../Runtime/ModuleInvocationStateStore.cs`:58] — deferred, latent.
- [x] \[Review]\[Defer] `TenantNamespace` == `ResourceNamespace` (identical) + 48-bit RunId truncation; AC 5 wants distinct run-unique axes. [`.../Runtime/ModuleRuntimePlan.cs`] — deferred, latent.
- [x] \[Review]\[Defer] Dead HXR003 block persists state then returns unavailable; duplicates HXR002 semantics. [`.../Runtime/ModuleCommandExecutionService.cs`:173-208] — deferred, latent (dead code).
- [x] \[Review]\[Defer] `down` manifest-reread TOCTOU → exit 3 instead of idempotent completion. [`.../Runtime/ModuleCommandExecutionService.cs`:133] — deferred, low priority.
- [x] \[Review]\[Defer] Cancellation during evidence-write on an already-failed path reclassifies the outcome to cancelled (exit 130), dropping the causal failure. [`.../Runtime/ModuleCommandExecutionService.cs`:293] — deferred, narrow timing (both outcomes non-passing).

**Dismissed:** unguarded `Diagnostics[0]` on the invalid-manifest path (`ModuleCommandExecutionService.cs`:87) — the manifest loader guarantees ≥1 diagnostic whenever the manifest is null (`HXM002`/`HXM015`), so it cannot throw today.

### Review Findings — Chunk A (Input contract)

Code review — **Chunk A (Input contract)**, 2026-07-21. Scope: module CLI parsing/hosting, diagnostics, repository-path containment, manifest loading/validation, the module-manifest schema and fixtures, and focused contract tests (baseline `edbaeaed`..HEAD `0e20faa`). Four review layers completed: adversarial, edge-case, verification-gap, and acceptance audit. Severities and routing were assigned after reading reachable call sites. 2 findings were dismissed as noise.

**Patch:**

- [x] [Review][Patch] HIGH · Enforce one resource-safe deterministic-identifier grammar across schema, runtime, and profile keys: 1–63 characters, leading lowercase letter, and lowercase alphanumeric segments separated by single hyphens. Add a shared schema/runtime parity corpus. **Resolved 2026-07-21 by Jerome: option 1.** [`schemas/hexalith.module-manifest.v1.json`:32]
- [x] [Review][Patch] HIGH · Reject blank `--manifest` through the stable usage/manifest diagnostic path instead of throwing before command error handling. [`src/libraries/Hexalith.Builds.Module.Cli/ModuleCommandApplication.cs`:57]
- [x] [Review][Patch] HIGH · Stop interpolating unvalidated module IDs and profile names into diagnostic fields, and escape human diagnostic metadata to prevent secret retention and line injection. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ModuleManifestLoader.cs`:256]
- [x] [Review][Patch] MEDIUM · Replace broad secret-marker substring matches that reject valid identifiers such as `honeyjar` and `api-key-management` with credential-shaped matching and negative controls. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ManifestSecretDetector.cs`:18]
- [x] [Review][Patch] HIGH · Detect common retained credential forms currently missed, including Basic authorization, Azure SAS/account keys, connection-string credentials, and AWS access-key IDs, with representative regression controls. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ManifestSecretDetector.cs`:18]
- [x] [Review][Patch] HIGH · Make the schema and runtime enforce the same portable canonical path grammar, including empty-segment rejection and host-independent Windows drive/UNC absolute-path rejection. [`schemas/hexalith.module-manifest.v1.json`:36]
- [x] [Review][Patch] MEDIUM · Preserve or revalidate physically resolved paths at consumption so a post-validation symlink swap cannot redirect fixture hashing or later descriptor use outside the repository. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ManifestPathValidator.cs`:137]
- [x] [Review][Patch] MEDIUM · Verify referenced descriptor and fixture files are readable during manifest validation rather than checking existence and containment only. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ManifestPathValidator.cs`:125]
- [x] [Review][Patch] HIGH · Replace the obsolete EventStore `3.70.0` manifest pin with the accepted P1 normalization pin `3.70.1` and update schema, fixtures, and tests consistently. [`src/libraries/Hexalith.Builds.Tooling/Manifest/SupportedPlatformPins.cs`:14]
- [x] [Review][Patch] MEDIUM · Parse placeholder syntaxes precisely so legitimate percent characters are accepted and unresolved `$VAR` forms fail closed. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ManifestPathValidator.cs`:157]
- [x] [Review][Patch] HIGH · Bound module-graph complexity or replace recursive cycle traversal so a long dependency chain cannot terminate the process with `StackOverflowException`. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ModuleManifestLoader.cs`:372]
- [x] [Review][Patch] MEDIUM · Enforce a manifest byte-size limit before `File.ReadAllText` to prevent unbounded allocation outside the stable diagnostic/exit-code contract. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ModuleManifestLoader.cs`:51]
- [x] [Review][Patch] MEDIUM · Expose supported profile classes through an immutable set so callers cannot mutate process-wide validation policy by casting the public `IReadOnlySet`. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ModuleProfileClasses.cs`:16]
- [x] [Review][Patch] MEDIUM · Add a multi-error manifest test that pins the complete diagnostic ordering and first-causal CLI rule. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ModuleManifestLoader.cs`:123]
- [x] [Review][Patch] MEDIUM · Exercise the executable console-cancellation wiring rather than only passing a pre-cancelled token directly to the command application. [`src/libraries/Hexalith.Builds.Tooling/Diagnostics/ToolCommandHost.cs`:18]
- [x] [Review][Patch] MEDIUM · Add nested-object and array-element duplicate-property controls so recursive duplicate detection cannot regress unnoticed. [`src/libraries/Hexalith.Builds.Tooling/Manifest/JsonDuplicatePropertyValidator.cs`:33]
- [x] [Review][Patch] MEDIUM · Add self-dependency, duplicate-edge, and multi-node cycle controls for `HXM008`. [`src/libraries/Hexalith.Builds.Tooling/Manifest/ModuleManifestLoader.cs`:151]
- [x] [Review][Patch] MEDIUM · Pin the complete default human diagnostic rendering, including source, row, field, location, and hint metadata. [`src/libraries/Hexalith.Builds.Tooling/Diagnostics/ToolDiagnosticFormatter.cs`:47]

### Review Findings — Chunk D (Surface & controls)

Code review — **Chunk D (Surface & controls)**, 2026-07-21. Scope: package/project spine, Builds release workflow and semantic-release wiring, G-4 build/publish/package-contract scripts, installed CLI entry points, and focused spine tests (baseline `edbaeaed`..HEAD `d74d557`). Four review layers completed: adversarial, edge-case, verification-gap, and acceptance audit. Chunks A-C were not repeated. 6 findings were dismissed as noise after reachability analysis and deduplication.

**Decision-needed:**

- [x] [Review][Patch] SD1 · HIGH · Add an intentional protected prerelease path that publishes to GitHub Packages, retains exact `packages: write`, and preserves the P0 prerelease qualification and rollback contract. [`package.json`:11-13; `.github/workflows/build-release.yml`:18-46] — **Resolved 2026-07-21 by Jerome:** preserve the P0 contract through a protected prerelease release path.
- [x] [Review][Patch] SD2 · MEDIUM · Amend the P0 preservation contract to the protected-branch release policy: retain generated release notes and GitHub releases without changelog/git plugins or generated commits to `main`. [`package.json`:14-35; `Github/publish-containers/tests/test_publish_script_contract.py`:289-303; `README.md`:304-309] — **Resolved 2026-07-21 by Jerome:** adopt the newer protected-branch policy.

**Patch** (unambiguous fixes):

- [x] [Review][Patch] SP1 · HIGH · Restore the mandatory packaged-control gate by adding expectation sidecars for all seven evidence negative fixtures; both release CI and semantic-release currently stop before any packaged control executes. [`Tools/test-g4-tool-package-contracts.ps1`:144-179]
- [x] [Review][Patch] SP2 · HIGH · Limit `GITHUB_TOKEN` and `NUGET_API_KEY` to the semantic-release substep; placing them on the composite-action invocation exposes publication credentials to setup-node, `npm ci`, and signature-audit steps. [`Github/create-release/action.yml`:7-25; `.github/workflows/build-release.yml`:110-114]
- [x] [Review][Patch] SP3 · HIGH · Add non-publishing pull-request/push CI for the G-4 build, tests, package qualification, and controls; changing the only build workflow to manual dispatch made the protected release job the first meaningful validation. [`.github/workflows/build-release.yml`:1-3]
- [x] [Review][Patch] SP4 · HIGH · Enforce the documented exact-SHA successful push-CI prerequisite with `actions: read` before requesting production approval; the current guard verifies only the branch and live SHA. [`.github/workflows/build-release.yml`:12-35]
- [x] [Review][Patch] SP5 · HIGH · Recheck live `main` immediately before semantic-release and behavior-test matching, stale, malformed, and non-main identities; `main` can advance while the release job waits for protected-environment approval, after which checkout still publishes the stale dispatch SHA. [`.github/workflows/build-release.yml`:18-51]
- [x] [Review][Patch] SP6 · HIGH · Move actual-version package qualification from semantic-release `prepareCmd` to a pre-tag `verifyReleaseCmd`, and preflight required registry credentials before tagging; semantic-release creates its tag before `prepare`/`publish`, so qualification or credential failure can strand an unpublished release tag. [`package.json`:17-22]
- [x] [Review][Patch] SP7 · HIGH · Publish only primary `.nupkg` files (or explicitly disable automatic symbol push before separate symbol publication); the current loop submits adjacent `.snupkg` artifacts automatically and then submits each `.snupkg` again. [`Tools/publish-g4-tool-packages.ps1`:129-133]
- [x] [Review][Patch] SP8 · HIGH · Make multi-package publication recoverable after partial success with duplicate-safe retry plus remote post-publication verification; a transient later push currently leaves an immutable partial version that every rerun fails on first duplicate. [`Tools/publish-g4-tool-packages.ps1`:129-133]
- [x] [Review][Patch] SP9 · MEDIUM · Validate installed CLI stream and JSON contracts exactly: keep stdout/stderr separate, parse JSON, assert exact status/diagnostics, scan command output as well as artifacts for secrets, and pin canonical UTF-8/no-BOM/LF bytes. The current merged-output substring and exit-code checks can qualify malformed, contradictory, misrouted, or leaking output. [`Tools/test-g4-tool-package-contracts.ps1`:69-92,182-250,344-398]
- [x] [Review][Patch] SP10 · MEDIUM · Add executable release-contract tests for the semantic-release prepare/publish handoff and publisher routing, credentials, inventory rejection, symbol behavior, and partial-failure recovery; existing qualification invokes only the build script, so release wiring can disappear or invert without a failing test. [`package.json`:17-22; `Tools/publish-g4-tool-packages.ps1`:68-133]
- [x] [Review][Patch] SP11 · MEDIUM · Prove clean Debug/source versus Release/package parity through the same executable commands, including the packaged `test` path; current source validation runs incrementally in the working checkout in Release and only the packaged commands execute controls. [`Tools/test-g4-tool-package-contracts.ps1`:254-399]
- [x] [Review][Patch] SP12 · LOW · Preserve the Builds-specific package tags after importing `Hexalith.Package.props`; evaluated tool packages currently expose generic DDD/Blazor tags instead of Build/Tooling/Evidence. [`src/libraries/Directory.Build.props`:2-8]
- [x] [Review][Patch] SP13 · LOW · Add the repository CRLF normalization needed by the prescribed whitespace gate; current added C# files make `git diff --check` report every CRLF line as trailing whitespace. [`.gitattributes`:1]

### Review Findings — Stage 5 (packaged native reports and acceptance validator)

Code review, 2026-09-25. Scope: the uncommitted Stage 5 working-tree changes against `754d2b4b6615c5004606ba837dd9c925c57e8d14`. The review excludes `qualification-evidence/` and this story. The diff has 97 file sections. The 19 negative acceptance records were reviewed as deltas from the positive record, and CR-at-EOL churn was ignored. Four review layers ran: blind, edge-case, verification-gap, and acceptance audit, with 58 raw findings. Severities were set by triage. Result: 6 decision-needed, 15 patch, 3 defer, 16 rejected. On 2026-09-25 Jerome accepted the recommendations: D1 a, D2 a, D3 a, D4 a, D5 b′, D6 c. The new finding SR-N1 is fixed now, and SR-W1 was folded into SR-P6. All 17 patches were applied and requalified at `0.0.0-stage5.7` on 2026-09-25; see the Dev Agent Record.

**Decision-needed**

- [x] [Review][Decision] SR-D1 · MEDIUM · **Packaged hosts are compiled inside the installed tool package at run time.** **Resolved 2026-09-25 by Jerome → patch (a):** each packaged host project is built in a private per-resource copy in the run workspace, parent build files are not imported, and packaged mode fails closed.
  - `RunTopology.AddEventStoreProject`/`AddUiProject` pass the packaged `g4-host/projects/*/Host.csproj` to path-based `AddProject`. That path gets no `SuppressBuild`, and the package ships no build output for it. `Host.csproj` copies the prebuilt host `AfterTargets="Build"`, so each run's `dotnet run` writes `obj/` and `bin/` into the NuGet tool store.
  - Concurrent runs, and the two EventStore instances of one run, build the same directory. A read-only store fails outright.
  - If `projects/*/Host.csproj` is missing, both helpers silently fall back to the build machine's `Projects.*` source paths.
  - Options: (a) copy `projects/` into the private run workspace, prebuild it once before AppHost start, and fail closed in packaged mode when the files are missing; (b) point the resources at the prebuilt host executables instead; (c) accept it as a documented Stage 5 residual and fix it before Stage 7.
  - (a) or (b) changes runtime behavior and requires a new live campaign.
  - Files: `src/hosts/Hexalith.Builds.Module.AppHost/RunTopology.cs:185-199` and `src/libraries/Hexalith.Builds.Module.Cli/pack/projects/*/Host.csproj`.
- [x] [Review][Decision] SR-D2 · MEDIUM · **Retained native TRX reports are not metadata-only.** **Resolved 2026-09-25 by Jerome → patch (a):** the report is redacted before it is retained, the secret scan runs again, and the retained bytes are hashed. This absorbs SR-P9.
  - `ContainsRetainedSecret` rejects only handoff values and credential shapes.
  - The retained `live/*.trx` files contain `runUser="administrator"`, absolute `/home/administrator/...` `codeBase`/`storage` paths, `computerName="DESKTOP-VIOG240"`, and `<StdOut>`.
  - The Stage 5 residual mentions only `computerName`.
  - Options: (a) redact `runUser`, `computerName`, and absolute paths, drop StdOut/StdErr before retention, and hash the retained bytes; (b) do not retain the TRX and bind only its hash and counts; (c) accept and correct the residual text.
  - This also decides whether the existing Stage 5 TRX evidence can be committed as-is.
  - Files: `src/libraries/Hexalith.Builds.Tooling/Runtime/NativeTestExecutor.cs:142-147` and `Runtime/ModuleCommandExecutionService.cs:560-583`.
- [x] [Review][Decision] SR-D3 · MEDIUM · **HXE207 cleanup and rollback controls are self-declared.** **Resolved 2026-09-25 by Jerome → patch (a):** the cleanup artifact must be clean `down` evidence for a cited persisted run ID. Rollback remains attestation-only until Stage 7, and the README states this.
  - `CompletedControl` checks the record's own `status == "passed"`, a command substring (any string containing `rollback`), and an artifact hash. It never parses the artifact.
  - The positive corpus passes with `{"sample":…}` stubs.
  - Options: (a) now, require the cleanup artifact to be `down` module-run evidence (`completed`/0/`HXI001`) whose `runId` is one of the cited runs, and leave the rollback artifact format to Stage 7; (b) also define a rollback-drill artifact contract now; (c) defer both to Stage 7.
  - File: `src/libraries/Hexalith.Builds.Tooling/Evidence/G4P0AcceptanceValidator.cs:323-328`.
- [x] [Review][Decision] SR-D4 · MEDIUM · **Control runs accept any exit-2 or exit-130 invocation.** **Resolved 2026-09-25 by Jerome → patch (a):** the unavailable control must be a native-test profile `test` run with a prerequisite rule other than `HXR029`. The cancelled control must be a `test` run with `HXC130` on a native-test profile.
  - `ValidateControlRun` checks only the exit code and final status.
  - `HXR029` (unsupported profile) is categorized `Prerequisite`/`PrerequisiteUnavailable`, so `--profile live` satisfies `prerequisite-unavailable`. Any exit-130 run satisfies `cancelled`.
  - Options: (a) require the control evidence to invoke a persisted native-test profile, with outcome rule not `HXR029` for unavailable and `HXC130` for cancelled; (b) pin the exact rules `HXR011` and `HXC130`; (c) leave it as is.
  - File: `G4P0AcceptanceValidator.cs:188-190`.
- [x] [Review][Decision] SR-D5 · MEDIUM · **The `hexalith.g4-p0-acceptance.v1` schema is not published.** **Resolved 2026-09-25 by Jerome → (b′):** a field and rule table goes in `Tools/README.md` now. The JSON Schema file is deferred to Stage 7.
  - The frontmatter `schemas.p0_acceptance` names it next to three schemas that exist in `schemas/`, but its field set lives only in `ExactFields(...)` calls.
  - Options: (a) add `schemas/hexalith.g4-p0-acceptance.v1.json` and a README field table now; (b) defer to Stage 7.
  - This adds a public contract surface.
- [x] [Review][Decision] SR-D6 · MEDIUM · **The acceptance record has no slot for negative-control outcomes.** **Resolved 2026-09-25 by Jerome → (c):** decide when the Stage 7 record is authored. The campaign already hash-binds the 20 corpus results.
  - The Stage 5 task requires binding them into deterministic artifacts.
  - The gate asserts the 40 corpus invocations but keeps them out of the inventory. The Stage 5 campaign retains them only in `packaged-qualification.json`.
  - Options: (a) add a hash-bound `negativeControls` artifact to the record and validate it; (b) rely on the Stage 6 gate inventory and document that; (c) defer to Stage 7.

**Patch**

- [x] [Review][Patch] SR-N1 · MEDIUM · Found during triage and present since `754d2b4`: a cancelled invocation writes its evidence with a null manifest, so `invocation.profile` and the fixture are lost. Stage 5 `live/cancelled.json` records no `--profile full`. **Resolved 2026-09-25 by Jerome → fix now:** a live-campaign rerun is already required. [`Runtime/ModuleCommandExecutionService.cs`:486]
- [x] [Review][Patch] SR-P1 · MEDIUM · The persisted run keys are not bound to their platform. Require `reportPath == RetainedReportPath(evidencePath, platform)`, and require the evidence's hash-bound profile fixture to declare `nativeTests.platform == platform`. Make the corpus MTP TRX distinct and add a "VSTest evidence cited as MTP" negative. Today the byte-identical VSTest TRX passes for both keys. [`G4P0AcceptanceValidator.cs`:209-234]
- [x] [Review][Patch] SR-P2 · MEDIUM · `persistedAssertions` and `expectedSequences` are only checked for non-emptiness. Require the exact six-check set per topology module and `module:1,2` sequences. Use `TryGetProperty` so HXE205 is reported instead of HXE200. Update the bound corpus evidence and add a partial-assertions negative. The positive corpus currently passes with 2 of 12 assertions. [`G4P0AcceptanceValidator.cs`:213-222]
- [x] [Review][Patch] SR-P3 · MEDIUM · Package bytes are never opened. Read each `.nupkg`/`.snupkg` as a zip and require the nuspec `id`/`version` to match. Replace the text placeholders with minimal valid packages, and add lockstep-version and file-name/version negatives. [`G4P0AcceptanceValidator.cs`:268-296]
- [x] [Review][Patch] SR-P4 · LOW · Approval dates are unbounded. Require `acceptedAtUtc` to be no earlier than the latest cited evidence `timestamps.completedUtc` and no later than the current time. [`G4P0AcceptanceValidator.cs`:299-321]
- [x] [Review][Patch] SR-P5 · MEDIUM · The negative corpus never reaches several checks: the `BoundToRevision` revision, tool-version, and pin binding; TRX-count equality; `ValidateControlRun`; `ValidateNoReport`; the `reportPlatform` mismatch; a missing cleanup; a duplicate run key; and a zero or all-skipped report. Add one isolating negative per check. [`test/fixtures/evidence/acceptance/negative/`]
- [x] [Review][Patch] SR-P6 · MEDIUM · The hash-bound corpus `.trx`, `.nupkg`, and `.snupkg` files have no eol attribute. With `core.autocrlf=true` the positive record fails with HXE204/HXE206, while the relaxed `Assert-TrackedFixtureBytesMatchHead` still passes. Add `test/fixtures/evidence/acceptance/bound/** -text`, plus a `.gitattributes` CRLF case in `Tools/test-g4-tool-package-artifact-validator.ps1`. [`.gitattributes`; `Tools/G4PackageQualification.functions.ps1`:323-339]
- [x] [Review][Patch] SR-P7 · MEDIUM · Nothing unit-tests how the profile, native, and cleanup results combine. Extract that combination and the evidence inputs into an internal pure function. Test it for native failure, HXT008, and a cleanup failure after a native pass. [`Runtime/ModuleCommandExecutionService.cs`:370-405,627-633]
- [x] [Review][Patch] SR-P8 · MEDIUM · Packaged host content is not fail-closed. Add a pack-time MSBuild `<Error>` when the AppHost, EventStoreHost, or UiHost Release outputs are missing. The gate should assert that the Module.Cli nupkg contains `g4-host/Hexalith.Builds.Module.AppHost.dll`, `g4-host/bin/{EventStoreHost,UiHost}/`, and `g4-host/projects/{EventStore,Ui}/Host.csproj`. Add a positive `WriteArtifactAsync` test. [`src/libraries/Hexalith.Builds.Module.Cli/Hexalith.Builds.Module.Cli.csproj`:22-28; `Tools/test-g4-tool-package-contracts.ps1`]
- [x] [Review][Patch] SR-P9 · LOW · (absorbed by SR-D2) The retained report hash comes from a second read of the file. Hash the `reportBytes` that are actually written. [`Runtime/ModuleCommandExecutionService.cs`:573]
- [x] [Review][Patch] SR-P10 · LOW · `mtp` passes `--report-xunit-trx`, so it supports xUnit v3 only. Document that restriction in `Tools/README.md` and in the `PersistedProfileNativeTests` XML docs. [`Runtime/NativeTestExecutor.cs`:94]
- [x] [Review][Patch] SR-P11 · LOW · The handoff unit test does not assert `HEXALITH_G4_EVENTSTORE_PEER_URL` or `HEXALITH_G4_DOMAINS`. [`test/Hexalith.Builds.Module.Tests/NativeTestExecutorTests.cs`]
- [x] [Review][Patch] SR-P12 · LOW · The live-lane outer timeout (15 min) equals the inner native-test timeout, so a hang kills the tool before cleanup. Raise it to 30 min. [`test/Hexalith.Builds.Tooling.IntegrationTests/Live/PackagedPersistedProfileTests.cs`:54]
- [x] [Review][Patch] SR-P13 · LOW · The VSTest fixture `global.json` duplicates the root SDK pin. Add a test that asserts they are equal. [`test/fixtures/module/executable/P0Fixture.NativeTests.VsTest/global.json`]
- [x] [Review][Patch] SR-P14 · LOW · The `nativeTests` loader negatives have no positive control. Also, `InvalidReportKeepsLoaderRule` omits HXT002. [`test/Hexalith.Builds.Module.Tests/PersistedProfileNativeTestsTests.cs`:43-48; `NativeTestExecutorTests.cs`]
- [x] [Review][Patch] SR-P15 · LOW · `volatileFields` does not list the per-run native report hash that `artifactHashes` now carries. The TRX GUID, times, and timings change on every run. [`RunEvidence/ModuleRunEvidenceFactory.cs`]

**Defer**

- [x] [Review][Patch] SR-W1 · MEDIUM · The fixture-proof self-test `Tools/test-g4-tool-package-artifact-validator.ps1` never runs in CI. **Resolved 2026-09-25 by Jerome → patch folded into SR-P6:** call it from `Tools/test-g4-tool-package-contract-gate.ps1`, which CI already runs. The G-6-bound `.github/workflows/ci.yml` is not edited.
- [x] [Review][Defer] SR-W2 · MEDIUM · The packaged native-report lane (`PackagedPersistedProfileTests`) is opt-in through `LiveGate`, not a blocking gate control (AC11). [`test/Hexalith.Builds.Tooling.IntegrationTests/Live/PackagedPersistedProfileTests.cs`] — deferred: this needs live-capable CI infrastructure (Stages 6–7).
- [x] [Review][Defer] SR-W3 · MEDIUM · A failing native invocation binds no test counts or report (AC7), because only passing reports are retained. [`Runtime/NativeTestExecutor.cs`:127-138] — deferred: this is a recorded Stage 5 residual. Resolve it with SR-D2 or before the Stage 7 acceptance record.

**Rejected**

- R1 false — hard-coded `PersistedAssertions` labels: `PersistedProfileExecutor` fails closed on every one of the six checks per module and on sequences 1,2 before returning `completed`. The labels statically describe that single executor.
- R2 low — Two findings contradict each other and neither harms a consumer. One says persisted assertions are dropped when native tests fail. The other says they are retained when report retention fails. Either way the evidence says `failed`.
- R3 false — "one person holds all three roles": the story frontmatter names Jérôme Piquot for all three, so a distinct-name rule would block legitimate acceptance.
- R4 low — the synthetic positive revision doesn't exist. The corpus is labelled synthetic, and a repository-existence check would add a Git dependency.
- R5 false — `nativeTests` was added to v1 without a version bump. v1 is unpublished, strict readers fail closed, and the owner decision placed it in v1.
- R6 low — an invalid `nativeTests` gives generic `HXR029`. Every invalid profile field already behaves this way, and a fix would add diagnostic plumbing.
- R7 low — `.json` routing sends JSON readiness matrices to the acceptance validator. No readiness JSON exists, and the help text documents YAML matrices.
- R8 low — the bound evidence read has no size cap. The files are hash-bound repository files, and a cap would be an extra guard.
- R9 low — VSTest `native.trx` would be overwritten on a multi-TFM project. The fixtures are single-TFM.
- R10 low — `ModuleRunEvidenceWriter.WriteAsync` is no longer `async`. All callers await it immediately.
- R11 false — "`completed` status lacks test/doc updates": 198/198 Module tests pass, and no test or README text asserts persisted `passed`.
- R12 false — "fixture native-test projects run in repository tests": they are not in `Hexalith.Builds.slnx`, and the gate excludes fixture projects.
- R13 low — the record cannot carry the literal command. The canonical evidence command form was accepted in Stages 2–4.
- R14 spec-edit — the Stage 5 checkbox cites AC 6 while filter pass-through and browser/CLI/MCP executors are residuals. Fixing it means editing the story, so it is flagged for the owner.
- R15 low — one evidence file cited by several runs is resolved by SR-P1's platform-bound paths. The control runs already need different exit codes.
- R16 low — the fixture test hard-codes `p0-orders` instead of reading `HEXALITH_G4_DOMAINS`. It is fixture-only.

## Dev Notes

### Authority and Scope

- This is the owner-repository implementation story authorized by Jerome on 2026-07-17. It supersedes the repository-selection blocker in the Projects handoff but does not close the Projects `6.1-P0` action until accepted delivery evidence exists.
- P0 owns both packaged tools and their schemas in Hexalith.Builds. It does not select the EventStore/platform compatibility baseline (P1), implement dual-principal query authorization (P2), approve production identity/G-5 (P3), self-accept P4, change Projects routing, or remove the Projects runtime.
- There is no UI feature scope. Browser/CLI/MCP entries are orchestration profile contracts; product journeys and accessibility acceptance remain in their owning stories.
- P0 implementation has no preceding dependency and may proceed in parallel with P1. Authoritative persisted/package qualification depends on P1 accepting the EventStore baseline. At authorization Builds pins EventStore `3.70.0`, while older Projects planning text mentions `3.67.3`; do not silently normalize either value in P0.
- G-6 is also a qualification dependency. Record or resolve the Dapr runtime `1.18.0` versus SDK packages `1.18.4` support disposition before an affected lane is represented as passing.
- Jerome is the named Builds/Platform owner for this authorization. A named Test Architect must be recorded before P0 evidence acceptance; the missing name does not block implementation start.

### Fixed Consumer Commands

```text
dotnet tool restore
dotnet tool run hexalith-module run --manifest module/hexalith-projects.module.json
dotnet tool run hexalith-module down --manifest module/hexalith-projects.module.json
dotnet tool run hexalith-module test --manifest module/hexalith-projects.module.json --profile full
dotnet tool run hexalith-evidence validate _bmad-output/planning-artifacts/implementation-readiness-traceability-matrix.yaml
```

Story 6.1 later narrows the test command to `--profile reads --filter Story=6.1`. P0 must support the filter/profile contract but must not fabricate Story 6.1 results.

### Existing Builds Behavior to Preserve

| File | Current behavior | Required change | Preserve |
| --- | --- | --- | --- |
| `Hexalith.Builds.slnx` | File-only solution with no .NET projects. | Add tool and test projects plus relevant schemas/fixtures. | Existing action/workflow/configuration inventory. |
| Root SDK/MSBuild imports | No root `global.json`, `Directory.Build.props`, or root `Directory.Packages.props`; `Hexalith.Build.props` currently derives `ProjectRoot` for external consumers, and `Hexalith.Package.props` defaults repository metadata to `Hexalith/Hexalith`. | Add repository-local SDK, build/package imports, and correct Builds metadata/root paths before adding projects. | Shared props remain valid for existing external consumers. |
| `Props/Directory.Packages.props` | Central pins include EventStore `3.70.0`, Aspire Testing `13.4.6`, System.CommandLine `2.0.10`, YamlDotNet `18.1.0`, Microsoft.NET.Test.Sdk `18.8.1`, xUnit v3 `3.2.2`, Shouldly `4.3.0`, and NSubstitute `6.0.0`. | Add only genuinely missing package pins. | Central versioning; no inline versions or opportunistic upgrades. |
| `.github/workflows/build-release.yml` | Runs script validators and `Github/create-release`; it does not build/test/pack/publish .NET tools. | Add blocking tool verification and package publication. | Existing validators and release branches/permissions. |
| `Github/scripts/build-packages.ps1` / `publish-packages.ps1` | Shared consumer scripts scan `src/libraries`, tolerate no packages, and build prereleases in Debug. | Preserve them; use new Builds-local G-4 scripts for exact Release-built tool artifacts. | Existing consumer behavior and paths. |
| `package.json` / `package-lock.json` | Semantic-release creates changelog/tag/GitHub release and lacks `@semantic-release/exec`. | Add locked exec lifecycle calling the local G-4 prepare/publish scripts. | Existing analyzer/notes/changelog/git/GitHub plugins. |
| `Github/create-release/action.yml` | Installs dependencies and runs semantic-release; documentation describes no package build. | Document/pass repository-configured package lifecycle without hard-coding G-4 tools into the shared action. | Existing external action behavior. |
| `Tools/test-domain-workflow-test-platforms.ps1` | Protects VSTest/MTP argument and report conventions. | Reuse/extend its contract for runner orchestration. | Both supported platforms and fail-closed report semantics. |
| `README.md` / `Tools/README.md` | Documents build/release assets and PowerShell utilities only. | Document installed .NET tools and evidence contracts. | Existing consumer instructions. |

### Reuse and Anti-Reinvention

- Reuse EventStore's Aspire composition extensions and persisted testing fixtures. Their current generic AppHost and skip-on-unavailable behavior need adaptation; they are not themselves a manifest-driven G-4 runner.
- Reuse the rule-ID, deterministic-ordering, JSON-diagnostic, redaction, and negative-fixture lessons in EventStore's operational-evidence validator. It validates a different schema and cannot be renamed or treated as AD-30.
- Existing Projects static Aspire tests, fake dictionary-backed state tests, and offline/manual E2E lanes are migration regressions, not persisted evidence.
- Use run-unique fixture identities and assert both event and projection/read sequence across restart. Key existence alone can be satisfied by stale state.
- Keep lifecycle, path, schema, and evidence logic independently unit-testable. Do not make every validator test start Aspire.

### Library and Framework Constraints

- Target `net10.0` with nullable, implicit usings, documentation, central package management, analyzers, and repository style intact.
- Use the current central pins: System.CommandLine `2.0.10`, YamlDotNet `18.1.0`, Aspire.Hosting.Testing `13.4.6`, Microsoft.NET.Test.Sdk `18.8.1`, xUnit v3 `3.2.2`, Shouldly `4.3.0`, and NSubstitute `6.0.0`. P0 has no dependency-upgrade scope.
- Use System.CommandLine's parse/invoke model and explicit command exit behavior. Use a YAML parser configuration that rejects duplicate mapping keys before binding; never validate YAML using regular expressions or a lossy deserialize/reserialize pass.
- Official local-tool manifests provide repository-scoped restore/run. Package the commands with `PackAsTool`/`ToolCommandName`; a script wrapper is not the supported contract.
- Aspire testing can manage distributed-application lifecycle, but choose a host form compatible with its test builder. Do not assume every file-based AppHost supports the same testing API.

### Conditional File Map

Likely **NEW** files/directories:

- `global.json`
- `Directory.Build.props`
- `Directory.Packages.props`
- `src/libraries/Directory.Build.props`
- `src/libraries/Hexalith.Builds.Module.Cli/`
- `src/libraries/Hexalith.Builds.Evidence.Cli/`
- owner-approved internal/shared project only if needed without creating an unapproved third package
- `test/Hexalith.Builds.Module.Tests/`
- `test/Hexalith.Builds.Evidence.Tests/`
- `test/Hexalith.Builds.Tooling.IntegrationTests/`
- `test/fixtures/package-consumer/.config/dotnet-tools.json` after an exact version is published
- `schemas/hexalith.module-manifest.v1.json`
- `schemas/hexalith.module-run-evidence.v1.json`
- `schemas/hexalith.readiness-evidence.v1.json`
- `test/fixtures/module/` and `test/fixtures/evidence/` positive/negative corpora
- deterministic generated samples and P0 acceptance record under an owner-approved evidence path
- `Tools/build-g4-tool-packages.ps1`
- `Tools/publish-g4-tool-packages.ps1`
- `Tools/test-g4-tool-package-contracts.ps1`

Likely **UPDATE** files are the preservation-table rows above, `package-lock.json`, and package/release configuration required to publish the tool packages.

### Verification and Evidence Contract

Final paths may be refined without changing the public package/command/schema contracts. Qualification includes:

```text
dotnet build Hexalith.Builds.slnx --configuration Release
dotnet test <module-unit-and-contract-projects> --configuration Release
dotnet test <evidence-unit-and-contract-projects> --configuration Release
dotnet test <persisted-integration-project> --configuration Release
dotnet pack src/libraries/Hexalith.Builds.Module.Cli --configuration Release --output <local-feed>
dotnet pack src/libraries/Hexalith.Builds.Evidence.Cli --configuration Release --output <local-feed>
dotnet tool restore
dotnet tool run hexalith-module test --manifest <valid-two-module-manifest> --profile full
dotnet tool run hexalith-evidence validate <valid-readiness-sample>
dotnet tool run hexalith-evidence validate <each-negative-control>
```

Required owner-acceptance evidence:

- accepted source revision and actual published package/version/hash inventory;
- clean-checkout source and package-mode commands/results;
- schemas and valid at-least-two-module manifest;
- actual persisted event plus projection/read state before and after restart;
- deterministic module-run sample and readiness-validator positive sample;
- packaged-command results for every blocking negative control;
- stable diagnostics/failure-category and metadata/redaction contracts;
- idempotent teardown and exercised greenfield/prerelease rollback record.
- independently validated `hexalith.g4-p0-acceptance.v1` record binding exact revisions and pins, package/feed identities and hashes, commands, every required live-lane outcome, native report/evidence hashes, cleanup/rollback results, and dated Builds Owner, Platform Owner, and named Test Architect approvals.

### Hard Stops

- Stop if implementation would change Projects or another repository without separate authorization.
- Stop if package IDs, command names, schema IDs, or repository ownership must change; update this authority record first.
- Stop if P0 chooses the P1R platform baseline or represents candidate runner/catalog `3.88.0` versus Architecture `3.70.1` as resolved without owner-approved evidence.
- Stop implementation that binds the live composition, and stop all P0 acceptance, until a named Test Architect, P1R baseline, and affected G-6 disposition are recorded.
- Stop if G-6 is unresolved for an affected live lane or an unavailable prerequisite is represented as skipped/pass.
- Stop if a fake store, handler return, static topology assertion, stale key, or hand-authored JSON is offered as G-4 proof.
- Stop if source scripts/global tools become alternate public contracts or consumer fixtures own topology, ports, credentials, Dapr, health, telemetry, or Aspire lifecycle.
- Stop if tokens, secrets, environment dumps, or protected payloads reach retained output.
- Stop if the validator accepts duplicate YAML keys, unknown schemas/statuses, placeholders, incomplete coverage, missing critical evidence, unexplained critical skips, or passed-on-unavailable.
- Stop if Projects AppHost/runtime is removed or routing changes before equivalent lanes and later cutover/rollback gates pass.

### References

- [Source: Hexalith.Builds/AGENTS.md]
- [Source: Hexalith.Builds/README.md#Repository-Structure]
- [Source: Hexalith.Builds/README.md#Version-and-Release-Management]
- [Source: Hexalith.Builds/Hexalith.Builds.slnx]
- [Source: Hexalith.Builds/Props/Directory.Packages.props]
- [Source: Hexalith.Builds/.github/workflows/build-release.yml]
- [Source: Hexalith.Builds/Github/scripts/build-packages.ps1]
- [Source: Hexalith.Builds/Github/scripts/publish-packages.ps1]
- [Source: Hexalith.Builds/Tools/test-domain-workflow-test-platforms.ps1]
- [Source: Hexalith.Projects/_bmad-output/planning-artifacts/sprint-change-proposal-2026-07-17.md#6.1-P0]
- [Source: Hexalith.Projects/_bmad-output/planning-artifacts/architecture/architecture-projects-2026-07-15/ARCHITECTURE-SPINE.md#AD-25]
- [Source: Hexalith.Projects/_bmad-output/planning-artifacts/architecture/architecture-projects-2026-07-15/ARCHITECTURE-SPINE.md#AD-30]
- [Source: Hexalith.Projects/_bmad-output/planning-artifacts/implementation-readiness-traceability-matrix.yaml]
- [Source: Hexalith.Projects/_bmad-output/test-artifacts/test-design-epic-6.md]
- [Source: Hexalith.Projects/_bmad-output/implementation-artifacts/6-1-p0-deliver-g4-persisted-runner-and-evidence-tooling.md]
- [Official .NET local tools: https://learn.microsoft.com/en-us/dotnet/core/tools/local-tools-how-to-use]
- [Official `dotnet tool restore`: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-restore]
- [Official .NET tool packaging: https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-create]
- [Official System.CommandLine: https://learn.microsoft.com/en-us/dotnet/standard/commandline/]
- [Official Aspire AppHost testing: https://learn.microsoft.com/en-us/dotnet/aspire/testing/manage-app-host]
- [Official Microsoft Testing Platform features: https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-features]

## Dev Agent Record

### Agent Model Used

GPT-5 Codex

### Debug Log References

- Story creation and source analysis only; no implementation build or tests were run.
- 2026-07-17 Task 1 plan: establish repository-local SDK/MSBuild authority first, keep shared behavior in a non-packable `Hexalith.Builds.Tooling` project, and expose only the two authorized .NET tool packages.
- 2026-07-17 Task 1 validation: `dotnet build Hexalith.Builds.slnx --configuration Release --no-restore` completed with 0 warnings/errors; each of the three test projects passed with one test and no skips.
- 2026-07-17 Task 2 validation: strict manifest and command contracts passed with 36 module tests, including exact exit codes, causal-outcome retention, canonical evidence output, filter redaction, and explicit G-6 prerequisite unavailability.
- 2026-07-17 Source qualification: `dotnet build Hexalith.Builds.slnx --no-restore --configuration Release --nologo` completed with 0 warnings/errors; Module 52/52, Evidence 11/11, and Integration 1/1 passed in Release.
- 2026-07-17 Native report parser validation: the shared TRX parser accepts VSTest and Microsoft Testing Platform/xUnit v3-compatible counters and rejects missing/duplicate structural elements, zero matches, all-skipped, inconsistent/undercounted, and failed reports. It is not yet invoked by a live runner, so this remains parser coverage rather than native report capture.
- 2026-07-17 Evidence validation: the `hexalith-evidence validate` command passed 11 focused tests covering duplicate YAML keys, unsupported schema/fields, defaults, coverage, complete canonical module-run artifact shape and hash validation, Markdown identities, undeclared `blocked`, execution-claim artifact requirements, and policy controls.
- 2026-07-17 Review hardening: resolver-based physical symlink/reparse-point containment rejects manifest and evidence escapes; full canonical module-run artifacts and an invalid-artifact control are validated; provenance records submodule revision/dirty marker/selected SDK; secret-bearing profile/filter/path values are rejected or redacted; public parse diagnostics and Ctrl+C cancellation evidence are stable; TRX accounting is exact.
- 2026-07-17 Package qualification: `pwsh -NoProfile -File ./Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-ci.15 -RequireControls` passed: Release build had 0 warnings/errors; Evidence 11/11, Module 52/52, Integration 1/1; exactly two tool `.nupkg`/`.snupkg` artifacts were hashed, restored from an isolated local feed into an isolated consumer repository with consumer-owned copied fixtures, and exercised through the curated positive and blocking-negative controls. This was local prepublication qualification only: no package publish, remote restore, exact consumer manifest, or owner acceptance occurred. The `run` control remained explicit non-passing `HXR002` while G-6/P1 live composition is unavailable.
- 2026-07-17 Deliberate live-lane stop: G-6 is unresolved and no owner-approved descriptor/platform composition ABI or P1 baseline is available. `run` and `test` therefore return explicit non-passing `HXR002` prerequisite evidence; no static fixture or hand-authored sample is represented as persisted-runtime proof.
- 2026-07-21 Chunk A review hardening: all 18 accepted input-contract patches were implemented, including the owner-selected 1–63 character identifier grammar, stable blank-manifest handling, diagnostic redaction/escaping, credential-shaped secret detection, portable path/readability checks, symlink consumption revalidation, iterative cycle detection, a 1 MiB manifest limit, immutable profile classes, and EventStore pin normalization to `3.70.1`.
- 2026-07-21 Chunk A validation: `dotnet build Hexalith.Builds.slnx --configuration Release --no-restore -m:1 --nologo` completed with 0 warnings/errors; Module 106/106, Evidence 24/24, and Integration 1/1 passed; `dotnet format whitespace Hexalith.Builds.slnx --no-restore --verify-no-changes` passed.
- 2026-07-21 Package requalification: isolated local packing/restoration and help-contract execution passed for both tools with version `0.0.0-review.2`. The stricter `-RequireControls` lane stopped before control execution at `Tools/test-g4-tool-package-contracts.ps1:175` because the pre-existing negative fixture `artifact-hash-mismatch.yaml` has no required `artifact-hash-mismatch.expected.json`; this evidence-fixture gap is outside Chunk A and remains open.

### 2026-09-22 Stage 2 reconciliation

The Projects P1R acceptance record selects EventStore `3.106.0` / `v3.106.0` /
`76051c70cbf868c40edc00ca0344fa5bd8879b69` and Builds
`ad52f350a2f0bc47849179ae17b4594dafff5363`; its rollback tuple is
EventStore `3.70.1` / `f13f9925fdca53efa2ab8c90d396ab106f91bb9c`
and Builds `7af20f8bafbfe561df6f7705913a0800603090b5`. The checked-out
Builds HEAD before this resumption was `2fba3497043fe5ffcfe4dc44c51a09eae9b950ab`,
an ancestor of the accepted Builds revision. Its runner/schema/fixture pin was
`3.102.0` despite the catalog's `3.106.0`; this working tree now applies the
accepted pin alignment. The exact final delivery revision remains to be recorded.

Current-head review found SP1's seven evidence expectation sidecars, SP3's
nonpublishing CI, and SP7's primary-only symbol publication already implemented.
This pass adds the protected prerelease path (SD1), release notes/GitHub release
policy without generated main commits (SD2), semantic-step credential scope
(SP2), exact-SHA successful push CI and post-approval source rechecks (SP4/SP5),
pre-tag package qualification and credential preflight (SP6), duplicate-safe
publication with downloaded remote primary-package hash and payload checks (SP8), release
contract tests (SP10), package tags (SP12), and CRLF whitespace rules (SP13).
SP8's local publisher tests also cover NuGet.org-style repository signing: a
changed outer archive hash is accepted only when every unsigned payload entry
matches, while a changed payload is rejected. The remote hash is recorded
separately from the qualified local hash. SP9 and SP11 now pass the full local package gate: installed commands capture
stdout/stderr separately, validate canonical JSON bytes and outcomes, scan both
streams for secrets, and compare isolated Debug source with Release/package
commands including `test`. The current `run`/`test` paths return prerequisite
unavailable `HXR003` on both sides; live persisted execution remains Stage 3-4
evidence. SP8's implementation is locally tested with a simulated feed; an actual
registry publication and remote consumer restore remain Stage 6 evidence.

Focused results: `dotnet build test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj -c Release --no-restore -m:1` passed with zero warnings/errors;
`dotnet test test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj -c Release --no-build` passed 117/117;
`dotnet build test/Hexalith.Builds.Evidence.Tests/Hexalith.Builds.Evidence.Tests.csproj -c Release --no-restore -m:1` passed with zero warnings/errors;
`dotnet test test/Hexalith.Builds.Evidence.Tests/Hexalith.Builds.Evidence.Tests.csproj -c Release --no-build` passed 68/68.
`pwsh -NoProfile -File Tools/test-g4-release-contract.ps1`,
`bash Tools/test-g4-release-source.sh`, and
`pwsh -NoProfile -File Tools/test-publish-g4-tool-packages.ps1` passed.
`pwsh -NoProfile -File Tools/test-g4-tool-package-contract-gate.ps1` and
`actionlint .github/workflows/build-release.yml .github/workflows/ci.yml`
passed. No protected GitHub Actions run, real package publication, live persisted
runner, remote exact-version restore, P0 acceptance record, or P0 owner approvals
are claimed. Stage 2 implementation is complete; P0 remains in progress.

The full local package gate, `pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage2.1 -RequireControls`, also passed: Release build had zero warnings/errors, Evidence 68/68, Module 117/117, Integration 1/1, and both exact-version tools restored into the isolated consumer. This was the pre-SP9/SP11 run. The final full gate, `pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage2.2 -RequireControls`, passed after their changes, including isolated Debug builds (zero warnings/errors) and same-command source/package parity. It remains local prepublication evidence and does not close Stage 3-7.

### 2026-09-22 Stage 3 dependency and executable check

P1R is accepted at the selected `3.106.0` tuple above. The G-6 owner record is
`spec-g-6-runtime-toolchain-baseline.md` in Projects and the current Builds
baseline selects Dapr runtime `1.18.2` / .NET packages `1.18.8`, SDK `10.0.401`,
and Aspire CLI `13.5.4`. The G-6 disposition explicitly says `g4Approved: false`.
Revalidation of the affected packet is currently non-passing:
`python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py --workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json`
from the Projects root exited `1` with
`G6-EVIDENCE-INVALID: Expected active literal pin missing: .github/workflows/release.yml::dapr-version: '1.18.0'`.
The local executables also differ from the selected tuple: `dotnet --version`
reported `10.0.401` (exit `0`), `aspire --version` reported `13.5.3` (exit `0`),
and `dapr --version` reported CLI `1.18.2` / runtime `1.18.4` (exit `0`).

No accepted executable descriptor ABI connects the manifest's
`descriptorAssembly` entries to EventStore's Aspire domain-module API, identity,
or FrontComposer. The positive fixture's descriptor files are declarative JSON,
not loadable assemblies. The Builds runner therefore retains `HXR003` and
does not start EventStore, Dapr sidecars, identity, FrontComposer, or Aspire.
The source path previously created an invocation-state file *before* returning
`HXR003`; it now returns the prerequisite result without creating that false
runtime state. A focused test uses a unique manifest fingerprint to verify the
unavailable run leaves no matching state file. `ModuleRuntimePlan` and the
state store remain scaffolding; manifest-hash teardown does not establish
run-ID-scoped cleanup of real resources and must not be treated as Stage 3 proof.

Observed executable checks from the Builds repository:

| Check | Exact result | Stage 3 meaning |
| --- | --- | --- |
| `dotnet build test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj -c Debug --no-restore -m:1 -p:NuGetAudit=false -p:MinVerVersionOverride=1.0.0` | Exit `0`; zero warnings and errors. | Focused source compiles. |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll` | Exit `0`; 117 passed, zero failed/skipped. | Manifest/platform pin, G-6 gate, cancellation, metadata isolation, and idempotent metadata cleanup controls pass; no live runtime proof. |
| Same test assembly with `-method '*Cancellation*'` and `-method '*Down*'` | Both exited `0`; respectively 1/1 and 7/7 passed, zero failed/skipped. | Focused cancellation and metadata cleanup checks executed. |
| `dotnet src/libraries/Hexalith.Builds.Module.Cli/bin/Debug/net10.0/Hexalith.Builds.Module.Cli.dll run --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --output json` | Exit `2`; `unavailable`, `HXR003`. | EventStore/Dapr/identity/FrontComposer composition remains fail-closed. |
| Same CLI with `test --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --profile full --output json` | Exit `2`; `unavailable`, `HXR003`. | No persisted, authenticated, restart, or two-instance test ran. |
| `aspire ps --non-interactive` | Exit `0`; `No running AppHost found`. | No Aspire lifecycle, endpoints, health, or telemetry could be observed. |
| `git diff --check` | Exit `0`. | No whitespace error in the working diff. |

EventStore persistence, Dapr operation, identity authentication, FrontComposer
rendering, Aspire start/stop, live run isolation, cancellation of live resources,
and bounded live cleanup remain **not executed** through G-4. Existing unit
checks verify only the available fail-closed and metadata behavior. Stage 3
and P0 remain open; no persisted or release acceptance is inferred.

### 2026-09-22 Stage 3 resumed revalidation and ABI decision

The owning Builds baseline `Tools/runtime-toolchain-baseline.json` no longer
requires the obsolete `.github/workflows/release.yml` literal
`dapr-version: '1.18.0'`: the Projects root release workflow is an exact-source
gated callee and no longer installs Dapr. Its baseline hash was propagated to
the affected Projects G-6 `source-state.json` and `packet.json` hash chain
temporarily during diagnosis, exposing the next source-freshness failure.
Those partial rebindings were then removed so the accepted 2026-09-20 packet
and source state retain their historical bytes. This resolves the reported
*literal-pin mismatch* in its owning baseline, not the packet's current
qualification. Its old `accepted` status does not constitute a passing
current revalidation.
The accepted 2026-09-22 P1R tuple and completed Stages 1–2 are unchanged.

From the Projects root, the exact current packet check was:

```text
python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py --workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json
```

With the historical packet preserved, it exits `1`:
`G6-EVIDENCE-INVALID: Baseline hash mismatch`. During the temporary diagnostic
rebind it instead exited `1`: `G6-EVIDENCE-INVALID: Source file hash mismatch:
.github/workflows/ci.yml`. The following read-only command from the Projects
root audits every source binding:

```bash
python3 - <<'PY'
import hashlib, json, pathlib
root=pathlib.Path('.')
state=json.loads((root/'_bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/source-state.json').read_text())
for repo in state['repositories']:
    drift=[]
    for entry in repo['files']:
        file=root/entry['path']
        digest=hashlib.sha256(file.read_bytes().replace(b'\r\n', b'\n')).hexdigest() if file.is_file() else 'MISSING'
        if digest != entry['sha256']:
            drift.append(entry['path'])
    if drift:
        print(f"{repo['name']}: {len(drift)} drifted source files")
        for path in drift: print(f'  {path}')
PY
```

It exited `0` and found **27** changed source bindings: Projects 3,
Builds 21 (including the corrected baseline), EventStore 1, and FrontComposer
2. The other **26** source drifts require
source review and a fresh G-6 capture; the accepted historical packet must
not be used as current G-4 live-lane approval. In Builds,
`PYTHONDONTWRITEBYTECODE=1 python3 Tools/test-runtime-toolchain-evidence-validator.py`
exited `0` with `G6-EVIDENCE-MUTATIONS-PASSED: 22 scenarios`.
The selected baseline itself records `containment.g4Approved: false`.
The drift splits into three committed Projects workflow/gate files, twenty
uncommitted Builds Stage 1–2 contract/fixture/workflow files, one committed
EventStore evidence validator, and two committed FrontComposer quality files.
The packet's retained `commands.json` contains earlier GitHub Actions and
tool-version observations. Rehashing these sources into its 2026-09-20 capture
would not rerun those commands. A fresh packet needs source review, matching
toolchain execution, affected OQ8/two-sidecar and supporting controls, new
command/observation artifacts, hashes, timestamp, and owner disposition.

Selected G-6 local toolchain and observed executable results from Builds:

| Exact command | Exit / observation | Selected tuple |
| --- | --- | --- |
| `dotnet --version` | `0`; `10.0.401` | SDK `10.0.401`, matches |
| `aspire --version` | `0`; `13.5.3+b5f143315ffb6968ea939a9978797a5b20e4c688` | Aspire CLI `13.5.4`, mismatch |
| `dapr --version` | `0`; CLI `1.18.2`, runtime `1.18.4` | CLI `1.18.0`, runtime `1.18.2`, mismatches |
| `aspire ps --non-interactive` | `0`; no running AppHost | No live Aspire resources to observe |
| `dapr list` | `0`; no Dapr instances | No live sidecars to observe |

Read-only `git rev-parse HEAD` in the EventStore checkout returned
`ffb6901a5b840af7be030ff246435b4bad00b79c` (`v3.106.0-29-gffb6901a`),
which descends from but is not the accepted P1R source revision
`76051c70cbf868c40edc00ca0344fa5bd8879b69`. FrontComposer HEAD is
`a2581001d9d8d1ffefb4c6c035b25be3c7d35755`
(`v4.5.0-12-ga2581001`), while the Builds manifest pin is `4.0.1`.
Any Debug project-reference live lane must materialize the exact selected
sources in isolation; these working checkouts are not qualification evidence.
At the accepted Builds revision `ad52f350a2f0bc47849179ae17b4594dafff5363`,
`Props/Directory.Packages.props` selects FrontComposer `4.5.0` while
`SupportedPlatformPins.FrontComposerVersion` and the manifest schema require
`4.0.1`. This existing cross-surface pin difference must be reconciled by the
owning baseline before a FrontComposer host can qualify against the manifest.

Current Builds source checks (working-tree Debug output, without a clean
checkout or published package):

| Exact command | Exit / observation |
| --- | --- |
| `dotnet build test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj -c Debug --no-restore -m:1 -p:NuGetAudit=false -p:MinVerVersionOverride=1.0.0` | `0`; zero warnings, zero errors |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll` | `0`; 117 passed, zero failed/skipped |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*Cancellation*'` | `0`; 1 passed, metadata cancellation only |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*Down*'` | `0`; 7 passed, metadata cleanup only |
| `dotnet src/libraries/Hexalith.Builds.Module.Cli/bin/Debug/net10.0/Hexalith.Builds.Module.Cli.dll run --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --output json` | `2`; `unavailable`, `HXR003` |
| `dotnet src/libraries/Hexalith.Builds.Module.Cli/bin/Debug/net10.0/Hexalith.Builds.Module.Cli.dll test --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --profile full --output json` | `2`; `unavailable`, `HXR003` |

The proposed executable descriptor contract is
[6-1-p0-stage3-descriptor-abi-proposal.md](6-1-p0-stage3-descriptor-abi-proposal.md),
SHA-256 `6c80b8a9d08b153eeb91acbcf3f70f62dc999bff6005dfc2e6be886ac9f93e5f`.
It is **proposed, not owner-approved**. P1R approves the selected EventStore
and Builds tuple but contains no loadable descriptor ABI. The manifest fixture
still points to JSON metadata, not compiled descriptors. The selected
EventStore API requires an Aspire `ProjectResource`, while FrontComposer's
current UI host uses statically registered markers. `HXR003` therefore remains
the fail-closed result before runtime state creation. No runner-owned EventStore
persistence, Dapr operation, identity authentication, FrontComposer rendering,
Aspire start/stop, live run isolation, live cancellation, or bounded cleanup
executed in this pass. The passing focused tests only prove manifest,
prerequisite, evidence, and invocation-metadata behavior. Live composition
requires a dated named-owner ABI decision, executable two-module descriptor
fixture and adapter, current passing G-6 disposition, and matching local
Aspire/Dapr versions. Stage 3 and P0 remain open.

### 2026-09-22 descriptor ABI approval continuation

The user replied `approve` to the explicit request for Jérôme Piquot's
Builds/Platform Owner decision on the immutable proposal SHA-256
`6c80b8a9d08b153eeb91acbcf3f70f62dc999bff6005dfc2e6be886ac9f93e5f`.
The decision is recorded at
[6-1-p0-stage3-descriptor-abi-approval.json](6-1-p0-stage3-descriptor-abi-approval.json)
with the current Builds HEAD, named capacity, and UTC timestamp. This approves
the ABI design for implementation; it is not a Stage 3, G-6, P0, live-lane,
or release acceptance. The proposal file is unchanged so its approved hash
remains stable.

Before the next code change, `aspire run --non-interactive` from Builds exited
`7`: `The project argument was not specified and no AppHost project files were
detected.` Builds currently has no AppHost to start for a baseline resource
inspection. This check started no resource and supplies no Stage 3 live proof.

An isolated local toolchain was then prepared under
`/tmp/hexalith-g6.c8hQ7T` without replacing the installed global tools or
starting Dapr instances. The exact setup/verification commands and results:

| Command (from Builds unless noted) | Exit / result |
| --- | --- |
| `mktemp -d /tmp/hexalith-g6.XXXXXX` | `0`; `/tmp/hexalith-g6.c8hQ7T` |
| `NUGET_PACKAGES=/tmp/hexalith-g6.c8hQ7T/nuget NUGET_HTTP_CACHE_PATH=/tmp/hexalith-g6.c8hQ7T/nuget-http DOTNET_CLI_HOME=/tmp/hexalith-g6.c8hQ7T/dotnet-home dotnet tool install Aspire.Cli --version 13.5.4 --tool-path /tmp/hexalith-g6.c8hQ7T/tools --source https://api.nuget.org/v3/index.json` | `0`; Aspire.Cli `13.5.4` installed to temporary path |
| `curl -fL https://github.com/dapr/cli/releases/download/v1.18.0/dapr_linux_amd64.tar.gz -o /tmp/hexalith-g6.c8hQ7T/downloads/dapr_linux_amd64.tar.gz` | `0`; archive downloaded |
| `sha256sum /tmp/hexalith-g6.c8hQ7T/downloads/dapr_linux_amd64.tar.gz` | `0`; `2a94739e0aa101289d88418225319562bc6800db273b3d9cf819a0efd1ea1bfe` |
| `/tmp/hexalith-g6.c8hQ7T/tools/dapr init --slim --runtime-version 1.18.2 --runtime-path /tmp/hexalith-g6.c8hQ7T` | `0`; runtime, placement, scheduler installed under temporary `.dapr/bin` |
| `/tmp/hexalith-g6.c8hQ7T/tools/aspire --version` | `0`; `13.5.4+9c1b401dd67746739044f68959cbf4d3d7af93a6` |
| `DAPR_RUNTIME_PATH=/tmp/hexalith-g6.c8hQ7T /tmp/hexalith-g6.c8hQ7T/tools/dapr --version` | `0`; CLI `1.18.0`, runtime `1.18.2` |
| `/tmp/hexalith-g6.c8hQ7T/.dapr/bin/daprd --version` | `0`; `1.18.2` |
| `DAPR_RUNTIME_PATH=/tmp/hexalith-g6.c8hQ7T /tmp/hexalith-g6.c8hQ7T/tools/dapr list` | `0`; no instances |

The selected Aspire/Dapr executables are now available for a future isolated
lane, but the full G-6 packet is still stale and Builds still has no runnable
AppHost. This setup is no EventStore, Dapr sidecar, identity, FrontComposer, or
Aspire lifecycle proof.

### 2026-09-22 approved descriptor ABI implementation and Stage 3 gate

The approved `hexalith.module-descriptor.v1` and
`hexalith.ui-descriptor.v1` result schemas now exist in Builds. The runner
loads built repository-relative descriptor DLLs through a bounded child
process with its inherited environment cleared. It checks exact exported
entrypoints, strict JSON shape, manifest module identity, canonical existing
project/assembly paths, and exact FrontComposer UI marker bindings before any
runtime state or resource can be created. Invalid assemblies, incompatible
results, and a child timeout fail closed with `HXD` diagnostics. This is a
locally tested adapter for descriptor data, not the generic AppHost or
FrontComposer host proposed for Stage 3. The child runs under the invoking
OS identity; clearing its environment and restricting normal assembly
resolution is **not** an OS sandbox for arbitrary untrusted descriptor code.
No descriptor is allowed to supply a topology callback.

The public `run` command now executes this validation when it sees a built
`.dll`. A malformed DLL returns `HXD002` before runtime; a valid built DLL
still returns `HXR003` without creating invocation state. The original JSON
descriptor fixture retains the same HXR003 result. The earlier dormant
`ModuleInvocationStateStore.CreateAsync` call immediately before HXR003 was
removed, so the fail-closed path cannot persist a fictitious run. These
changes preserve the accepted P1R pins and completed Stage 1–2 source work.

| Exact command from Builds unless stated | Exit / result |
| --- | --- |
| `dotnet build test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj -c Debug --no-restore -m:1 -p:NuGetAudit=false -p:MinVerVersionOverride=1.0.0 -v:q` | `0`; zero warnings/errors after the final test fixture change |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -class Hexalith.Builds.ModuleTool.Tests.ExecutableDescriptorLoaderTests` | Initial new public-command fixture attempt exited `1`: 10 passed, 1 failed, `HXM009` because its manifest omitted required `profiles`. The fixture was corrected; this initial failure is not live platform evidence. |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*PublicRunWithApprovedDescriptorRemainsFailClosed*'` | `0`; 1 passed after fixture correction; built descriptor reaches HXR003 in the public path |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll` | `0`; 129 passed, zero failed/skipped after fixture correction. Tests include two compiled descriptor DLLs, UI marker binding, invalid shape/path/identity, child timeout, cleared environment, public invalid-DLL rejection, and valid-DLL HXR003. |
| `pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage3.1 -RequireControls` | `0`; Release build zero warnings/errors; Evidence 68/68, Module 128/128, Integration 1/1; exact-version tools restored into an isolated consumer, source/package contract gate passed. The later added valid-descriptor public-command test brought Debug Module to 129/129; no production source changed after this package gate. |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*Cancellation*'` | `0`; 1 passed, metadata cancellation only |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*Down*'` | `0`; 7 passed, metadata cleanup only |
| `dotnet src/libraries/Hexalith.Builds.Module.Cli/bin/Debug/net10.0/Hexalith.Builds.Module.Cli.dll run --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --output json` | `2`; `unavailable`, `HXR003`; no runtime starts |
| `dotnet src/libraries/Hexalith.Builds.Module.Cli/bin/Debug/net10.0/Hexalith.Builds.Module.Cli.dll test --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --profile full --output json` | `2`; `unavailable`, `HXR003`; no native persisted test starts |
| `/tmp/hexalith-g6.c8hQ7T/tools/aspire run --non-interactive` | `7`; no AppHost project detected in Builds |
| `/tmp/hexalith-g6.c8hQ7T/tools/aspire ps --non-interactive` | `0`; no running AppHost |
| `DAPR_RUNTIME_PATH=/tmp/hexalith-g6.c8hQ7T /tmp/hexalith-g6.c8hQ7T/tools/dapr list` | `0`; no Dapr instances |
| From Projects root: `python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py --workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json` | `1`; `G6-EVIDENCE-INVALID: Baseline hash mismatch` with the historical packet preserved |
| `python3 -m json.tool _bmad-output/implementation-artifacts/6-1-p0-stage3-descriptor-abi-approval.json >/dev/null && python3 -m json.tool schemas/hexalith.module-descriptor.v1.json >/dev/null && python3 -m json.tool schemas/hexalith.ui-descriptor.v1.json >/dev/null && sha256sum _bmad-output/implementation-artifacts/6-1-p0-stage3-descriptor-abi-proposal.md && git diff --check` | `0`; all JSON parses, approved proposal SHA remains `6c80b8a9d08b153eeb91acbcf3f70f62dc999bff6005dfc2e6be886ac9f93e5f`, no whitespace errors |

Requested live-evidence disposition at this Builds head:

| Lane | Executable observation and remaining blocker |
| --- | --- |
| EventStore persistence | Public `run`/`test` returned HXR003; no AppHost or persisted write/read executed. Accepted EventStore `v3.106.0` source is not the current checkout HEAD; a qualified exact-source runner host and fresh G-6 disposition are needed. |
| Dapr | The selected isolated CLI/runtime tuple reports no instance; no sidecar or service invocation executed because no AppHost exists. G-6 packet is stale. |
| Identity | No runner-owned identity host or development credential injection exists; no authenticated or cross-Tenant live request executed. HXR003 blocks launch. |
| FrontComposer | No generic UI host or live marker rendering exists. The accepted Builds catalog has `4.5.0`, while the manifest pin is `4.0.1`; the owning pin baseline needs reconciliation before qualification. |
| Aspire lifecycle | Selected Aspire `run` exited 7 with no AppHost; `ps` confirmed none. No startup, readiness, stop, or restart was observed. |
| Run isolation | Descriptor and invocation-state unit tests run; no two concurrent live run IDs or exact-resource isolation was exercised. |
| Cancellation | One metadata cancellation test passed; no live AppHost/sidecar cancellation or bounded teardown was exercised. |
| Cleanup | Seven metadata `down` tests passed; no live owned resource existed for teardown or leak checks. |

Stage 3 and P0 remain **in progress**. The remaining implementation is a
runner-owned generic AppHost and FrontComposer host over a real two-module
fixture, followed by live EventStore/Dapr/identity/UI/lifecycle/isolation/
cancellation/cleanup checks. Qualification also requires a newly captured
G-6 packet bound to reviewed current sources and actual observations, and
the FrontComposer version decision. Neither the local package gate nor the
descriptor ABI approval supplies that evidence.

### 2026-09-23 G-6 refresh and FrontComposer pin resolution

**Validator rerun.** From the Projects root, before any change in this pass,
`python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py --workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json`
exited `1`: `G6-EVIDENCE-INVALID: Baseline hash mismatch`. Baseline-only pin
validation (`validate_baseline`) passed against current sources. Projects `main`
CI run `35758640029` fails at the same packet step.

**Source-drift review.** The read-only audit again reported 27 drifted
bindings. Each was diffed against its bound revision; none changes a G-6 tuple pin:

| Repository | Drift | Review result |
| --- | --- | --- |
| Projects (3) | `ci.yml`, `release.yml`, `run-ci-workflow-gates.ps1` | Python venv/unit-test and release-shell gates; release callee no longer installs Dapr, so its literal pin was removed from the owning baseline. |
| Builds (21) | Stage 1–2 working tree | EventStore `3.106.0` runner alignment, fixture rebinding, release/CI contract tests, and the baseline literal-pin removal. |
| EventStore (1) | `tools/validate-oq8-platform-evidence.py` | Redaction, review metadata, and direct-assembly contracts command (`c4bde75d`, `bc39811e`); no runtime pin change. |
| FrontComposer (2) | `quality.yml`, `CiGovernanceTests.cs` | EventStore successor recapture governance (`41409907`, `52135529`, `b5f94ea9`); Aspire/Dapr pins unchanged. |

The drift invalidated the recorded observations, so the packet was
**re-executed**, not rehashed. The Projects owner decisions for this pass were:
temporary Dapr control-plane swap for the capture only, fresh packet at the
canonical path with the 2026-09-20 packet archived byte-identically under
`history/2026-09-20/`, and named-owner review before acceptance.

**Fresh capture (2026-09-22T22:29Z–22:43Z UTC).** Every .NET build/test used an
isolated clean NuGet cache: the global cache contains 579 locally packed
packages, including a `Hexalith.Folders.Client 1.0.0` from a temporary feed whose
hash differs from nuget.org and which broke the Projects Release build
(`CS1501`). The global cache was not modified.

| Purpose | Exact command (private roots elided) | Exit / result |
| --- | --- | --- |
| Dapr tuple | stop and rename global `dapr_placement`/`dapr_scheduler` 1.18.4; run `ghcr.io/dapr/dapr:1.18.2` under those names; `DAPR_RUNTIME_PATH=<isolated> dapr --version && daprd --version` | `0`; CLI 1.18.0, runtime 1.18.2, image `sha256:5a5b6be9…` |
| Tool versions | `PATH=<isolated> dotnet --version && aspire --version && dapr --version` | `0`; 10.0.401, 13.5.4, 1.18.0/1.18.2 |
| OQ8 qualifier | EventStore `tests/…LiveSidecar.Tests.dll -method …ProductionMatrix_IndependentProcessesPreserveAuthorityReplayExpiryAndLeakageInvariants` with isolated `HOME` daprd and `--no-incremental` isolated-cache build | `0`; 1/1 passed; 2 EventStore processes + 2 sidecars; PostgreSQL rows +4 events/metadata/sequence; replay exact |
| Support selectors | EventStore `Server.Tests.dll` with the 21 workflow selectors | `0`; 33/33 passed |
| Capture validation | `python3 tools/validate-oq8-platform-evidence.py --capture-directory … --expected-runtime-version 1.18.2` | `0`; passed |
| Builds tests | `dotnet test` Module and Evidence (Release, isolated cache) | `0`; 130/130 and 68/68 (after the FrontComposer alignment below) |
| Mutation controls | `python3 references/Hexalith.Builds/Tools/test-runtime-toolchain-evidence-validator.py` | `0`; 22 scenarios |
| Root gates | `pwsh -NoProfile -File ./tests/tools/run-ci-workflow-gates.ps1` | `0`; passed |
| Projects build | `CI=true dotnet restore Hexalith.Projects.CI.slnx && dotnet build … --configuration Release --no-restore -warnaserror` | `0`; 0 warnings/errors. Earlier non-accepted attempts: local Debug project references (`572` errors) and contaminated global cache (`CS1501`) |
| Projects Integration | `dotnet tests/Hexalith.Projects.Integration.Tests/bin/Release/net10.0/Hexalith.Projects.Integration.Tests.dll -noColor` | `0`; 27/27 |
| Smoke preflight | `env -u TEST_USER_PASSWORD … ./tests/e2e/run-live-apphost.sh` | `1` expected; stopped before AppHost start |
| Owner AppHosts | Release `-warnaserror` builds of Conversations, Memories, Parties, Tenants | `0`; all four 0 warnings/errors |
| Owner pin assertions | Conversations `GlobalJsonShouldPinRequestedSdkVersion`; FrontComposer `ToolchainPins_MatchApprovedDotnetAndAspireVersions` | `0`; both 1/1 |
| Test platforms | `pwsh -NoProfile -File ./Tools/test-domain-workflow-test-platforms.ps1` | `0`; 169 assertions |
| Package inventory | `pwsh … validate-package-version-exceptions.ps1 -InventoryPath … -CatalogPath … -WorkspaceRoot ../..` | `1` expected; 13 pre-existing drift errors outside G-6 (my first invocation omitted required parameters and is not recorded as the result) |
| Dapr restore | remove temporary containers/volume; rename and restart the original 1.18.4 containers | `0`; originals running |

The packet was assembled as `pending-owner-acceptance`; the real validator
exited `1` (`Packet is not accepted`) and a scratch copy with only `status`
flipped exited `0`. Jérôme Piquot, as named G-6/Builds/Platform/FrontComposer-Web
owner, then accepted both the refreshed packet and the baseline edit. The
Projects record `qualification-evidence/g-6-runtime-toolchain/owner-acceptance.json`
binds packet SHA-256 `cb6b36ae7bc995c96eea5c327caacccd64f4296328d4202c4dddfb4be61d279b`
and baseline SHA-256 `525615c65ada8cabf5a6911a374bb22b62f6a3c1bfa6c91f7245312da9720265`.
The same validator command now exits `0`: `G6-EVIDENCE-VALID`. Containment is
unchanged (`g4Approved: false`); G-6 is cleared only as a Stage 3 prerequisite.
**Any later change to a G-6-bound Builds file invalidates this packet** and
requires re-running the affected commands before re-sealing.

**FrontComposer `4.5.0` versus `4.0.1`.** The P1R acceptance record binds only
EventStore `3.106.0` and Builds `ad52f350a2f0bc47849179ae17b4594dafff5363`. At that
Builds revision the owning package catalog (`Props/Directory.Packages.props`,
owner commit `2ca5965` "update FrontComposer to 4.5.0") selects `4.5.0`; the
runner/schema `4.0.1` was a stale July copy. Both published versions expose
`AddHexalithDomain<T>`. The runner contract therefore follows the owning
catalog, like the Stage 1 EventStore alignment; the accepted tuple is unchanged
and no tuple-change decision was needed. `SupportedPlatformPins`, the manifest
schema `const`, all module fixtures and tests now use `4.5.0`; manifest-bound
evidence fixtures were rebound to the new positive-manifest hash
`CA112F368BA55F91C05FFCCB302C4133698331FE463AEF0F875B93870BC57A96`; and the new
negative control `superseded-frontcomposer-pin.json` fails with `HXM016`.

### 2026-09-23 Stage 3 implementation slice (binding for this dispatch)

This slice implements runner-owned composition and a real two-module executable
fixture, and proves the live lanes through Builds-owned integration tests. It
does **not** open the public command: `hexalith-module run|test` keeps returning
`HXR003` until the owner and a named Test Architect review the live evidence.
Stages 4–7 are out of scope.

**Constraints**

- Do not modify these G-6-bound files: `.github/workflows/ci.yml`,
  `.github/workflows/domain-ci.*`, `.github/workflows/domain-release.*`,
  `README.md`, `global.json`, `Props/Directory.Packages.props`,
  `Tools/runtime-toolchain-baseline.json`, `Tools/*runtime-toolchain-evidence*.py`,
  `schemas/hexalith.module-manifest.v1.json`,
  `schemas/hexalith.runtime-toolchain-evidence.v1.json`,
  `Manifest/SupportedPlatformPins.cs`, `Runtime/RuntimePrerequisiteGate.cs`,
  `test/Hexalith.Builds.Module.Tests/{ManifestValidationTests,ModuleCommandApplicationTests,ModuleRunEvidenceSerializationTests,RuntimePrerequisiteGateTests}.cs`,
  `test/fixtures/module/README.md`, `test/fixtures/module/positive/*`, and the
  existing `test/fixtures/module/negative/*.json`. If a change is unavoidable,
  stop and report it.
- Missing package pins go in the Builds root `Directory.Packages.props`.
  No inline versions, no dependency upgrades.
- Package mode only: Builds hosts consume EventStore `3.106.0` and FrontComposer
  `4.5.0` packages. No `ProjectReference` or source path outside this
  repository. Do not modify any other repository.
- New projects live under `src/hosts/` and `test/`. They are `IsPackable=false`,
  so the release gate still sees exactly two tool packages. Add them to
  `Hexalith.Builds.slnx`.
- New stable diagnostics use unused `HXR010`–`HXR029` IDs. Unavailable
  prerequisites return exit `2`, never a pass or a skip-as-pass.
- Retained output is metadata-only. The per-run HS256 signing key and tokens
  pass only through child-process environment and in-memory test clients.

**Tasks and acceptance**

- [x] **S3-1 Executable two-module fixture** under `test/fixtures/module/executable/`:
  - Two EventStore domain services (`Hexalith.EventStore.DomainService`
    two-line host; one aggregate each with command → event → state `Apply`).
  - One descriptor assembly per module exporting `Hexalith.ModuleDescriptorV1.Describe()`.
  - A FrontComposer UI library with one `[BoundedContext]` marker per module.
  - A UI descriptor assembly exporting `Hexalith.UiDescriptorV1.Describe()`.
  - A manifest that names the built descriptor `.dll` paths, plus a
    deterministic build entry point.
  - *Given* the fixture is built, *when* `ExecutableDescriptorLoader.LoadAsync`
    runs, *then* both module descriptors and the UI marker bindings are valid,
    and the manifest validates with the accepted pins.
- [x] **S3-2 Builds-owned hosts** under `src/hosts/`:
  - `Hexalith.Builds.Module.EventStoreHost`: the EventStore server composition
    root over the `Hexalith.EventStore.Gateway` package.
  - `Hexalith.Builds.Module.UiHost`: a generic FrontComposer Blazor host that
    loads plan-named marker assemblies and calls `AddHexalithDomain<T>`
    reflectively.
  - `Hexalith.Builds.Module.AppHost` (`Aspire.AppHost.Sdk` 13.5.4), driven by a
    run plan. It composes:
    - a run-scoped Redis container;
    - run-scoped placement and scheduler executables from the verified Dapr
      1.18.2 runtime;
    - rendered state-store, pub/sub and Dapr configuration, with per-run name
      resolution;
    - `AddHexalithEventStore` and `AddEventStoreDomainModule` for each module
      project;
    - the UI host.
  - The AppHost writes a readiness document once every resource is healthy.
  - *Given* a valid plan, *when* the AppHost starts, *then* every resource
    reaches healthy and carries the run ID. No module supplies topology,
    ports, credentials, Dapr components or identity.
- [x] **S3-3 Runner composition engine** in `Hexalith.Builds.Tooling/Runtime`
  (new files), not wired to the public command:
  - Probe prerequisites: Docker, and `HEXALITH_DAPR_HOME` with
    `tools/dapr` = CLI 1.18.0 and `.dapr/bin/{daprd,placement,scheduler}` =
    runtime 1.18.2, verified by executing them.
  - Create the run plan: run ID, run-unique tenant/resource namespaces, allocated
    ports, workspace, generated key.
  - Launch the AppHost child with a scrubbed environment and wait for readiness
    with a bound.
  - Persist invocation state keyed by run ID in a per-user directory.
  - Provide idempotent `down` for one run ID.
  - Cancellation and failure run bounded teardown and retain metadata-only
    failure state.
  - *Given* a missing or wrong prerequisite, *then* an `HXR01x` diagnostic is
    returned before any resource starts.
- [x] **S3-4 Live integration tests** in `test/Hexalith.Builds.Tooling.IntegrationTests`,
  opt-in with `HEXALITH_G4_LIVE=1`. Without the opt-in they report an explicit
  skip reason and are never counted as passing. The tests prove:
  - **(a) lifecycle:** start, healthy, stop.
  - **(b) persistence:** an authenticated command for each module completes, and
    the Redis end-state holds the run-unique aggregate events at the expected
    sequence.
  - **(c) Dapr:** EventStore-to-module invocation succeeds through sidecars.
  - **(d) identity:** no token gets `401`; a foreign tenant gets `403`.
  - **(e) FrontComposer:** the UI host serves `200` and shows both bounded
    contexts.
  - **(f) run isolation:** two concurrent runs use distinct ports and resources,
    and `down` of one leaves the other healthy.
  - **(g) cancellation:** cancelling mid-startup leaves no run process or
    container.
  - **(h) cleanup:** after `down` nothing tagged with the run remains, and a
    repeated `down` succeeds.
- [x] **S3-5 Unit coverage** for plan rendering, prerequisite probing, state
  keyed by run ID, redaction of the key and tokens, and the unchanged public
  `HXR003`.

**Verification commands**

Run from Builds with an isolated `NUGET_PACKAGES`:

- `dotnet build Hexalith.Builds.slnx -c Debug -m:1`
- run the Module, Evidence and Integration test assemblies directly
- `HEXALITH_G4_LIVE=1 HEXALITH_DAPR_HOME=<isolated> dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll -class <live classes>`
- `git diff --check`
- rerun the Projects G-6 validator to prove that no bound file drifted

### 2026-09-23 Stage 3 slice implementation (S3-1 to S3-5)

Implemented the binding slice above in Builds only. The public
`hexalith-module run|test` path is unchanged and still returns `HXR003`
(`ModuleCommandExecutionService`/`RuntimePrerequisiteGate` untouched); the
composition engine is reachable only from Builds-owned code and tests. No
G-6-bound file, other repository, dependency version, or package pin changed;
no new package pin was needed (every referenced package is already in the
owning catalog). The AppHost references `CommunityToolkit.Aspire.Hosting.Dapr`
directly so it resolves the cataloged `13.5.1-beta.757` rather than the
transitive `752` floor of `Hexalith.EventStore.Aspire 3.106.0`.

- **S3-1** `test/fixtures/module/executable/`: two EventStore domain services
  (`P0Fixture.Orders` domain `p0-orders`, `P0Fixture.Inventory` domain
  `p0-inventory`; two-line `Hexalith.EventStore.DomainService` host, one
  aggregate each whose event carries the sequence folded from rehydrated
  state), one descriptor assembly per module, a FrontComposer UI library with
  one `[BoundedContext]` marker (plus one `[Command]`) per module, a UI
  descriptor assembly, the manifest `hexalith.module-manifest.v1.json`, a
  `live` profile fixture, and the deterministic build entry point
  `Hexalith.Builds.P0Fixture.slnx`. Fixture build output is redirected to
  `artifacts/g4-fixture/` (configuration-independent descriptor paths; the
  packaged fixture corpus stays source-only). The package gate no longer runs
  `dotnet test` on projects under `test/fixtures/`.
- **S3-2** `src/hosts/`: `Hexalith.Builds.Module.EventStoreHost` (EventStore
  composition root over `Hexalith.EventStore.Gateway` 3.106.0),
  `Hexalith.Builds.Module.UiHost` (loads plan-named marker assemblies and calls
  `AddHexalithDomain<T>` reflectively; ephemeral data protection), and
  `Hexalith.Builds.Module.AppHost` (`Aspire.AppHost.Sdk` 13.5.4). The AppHost
  composes a run-scoped Redis container (run-named, run-labelled), run-scoped
  placement/scheduler executables from the verified Dapr 1.18.2 runtime,
  rendered state-store/pub-sub components and a per-run SQLite
  name-resolution configuration, `AddHexalithEventStore` plus
  `AddEventStoreDomainModule` (isolated module sidecars) and the UI host. The
  per-run HS256 key reaches only the EventStore host (secret parameter) and is
  removed from the AppHost process environment before orchestration starts. A
  fresh run-scoped store is fail-closed until the EventStore v2
  projection-delivery writer protocol is activated, so the AppHost (the run
  operator) activates it with an in-memory administrator token once it is
  the only unhealthy EventStore check. Readiness is written only after every
  project, container, and executable is healthy (explicit-start rebuilder
  resources excluded) and EventStore-to-module Dapr invocation succeeds.
- **S3-3** `Hexalith.Builds.Tooling/Runtime/Composition*`: prerequisite probe
  (Docker server version; `HEXALITH_DAPR_HOME` CLI 1.18.0/runtime 1.18.2;
  `daprd --version`; placement/scheduler start-line versions) returning
  `HXR010`-`HXR013` (exit 2) before any resource starts; `HXR014` missing
  AppHost; run plan (run ID, run-unique tenant/foreign-tenant/resource
  namespaces, serialized disjoint port allocation, private workspace); scrubbed
  AppHost environment; bounded readiness (`HXR020` early exit, `HXR021`
  timeout, `HXR022` foreign readiness); metadata-only state keyed by run ID in
  a per-user directory; idempotent `down` by run ID (stdin/SIGTERM stop,
  label/env-tag sweep, workspace and state removal, `HXR023` leftovers,
  `HXR024` malformed ID); cancellation/failure teardown retaining a
  metadata-only `Cancelled`/`Failed` state record.
- **S3-4** `test/Hexalith.Builds.Tooling.IntegrationTests/Live/`: nine opt-in
  tests (`HEXALITH_G4_LIVE=1`) covering (a)-(h). Without the opt-in each
  reports the explicit skip reason and is not counted as passing.
- **S3-5** `test/Hexalith.Builds.Module.Tests/Composition*Tests.cs`: 27 unit
  tests for plan rendering, component/configuration rendering, prerequisite
  probing (fake executables), run-ID-keyed state, key/token redaction and token
  signing, engine fail-closed/idempotent paths, and the unchanged public
  `HXR003` for the executable fixture (`run` and `test`).

Verification from Builds with the isolated `NUGET_PACKAGES`
(`<scratch>/nuget`) and `HEXALITH_DAPR_HOME=/tmp/hexalith-g6.c8hQ7T`:

| Exact command | Exit / result |
| --- | --- |
| `dotnet build Hexalith.Builds.slnx -c Debug -m:1` | `0`; 0 warnings, 0 errors (15 projects) |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll` | `0`; 157/157 |
| `dotnet test/Hexalith.Builds.Evidence.Tests/bin/Debug/net10.0/Hexalith.Builds.Evidence.Tests.dll` | `0`; 68/68 |
| `dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll` (no opt-in) | `0`; 1 passed, 9 skipped with the explicit opt-in reason |
| `HEXALITH_G4_LIVE=1 HEXALITH_DAPR_HOME=<isolated> dotnet …IntegrationTests.dll` | Six full live runs: `1` (4 failed: module invocation raced readiness, fixed by the invocation probe), `0` (10/10), `1` (isolation `HXR021`, see below), `0` (10/10), then after the port-allocator hardening `0` and `0` (10/10 each, 156 s and 163 s); isolated isolation rerun `0` |
| `pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage3.3 -RequireControls` | `0`; Release build 0 warnings/errors including hosts and fixture; Evidence 68/68, Module 156/156, Integration 1 passed/9 skipped; packed controls passed (before the port-allocator hardening and its one added unit test) |
| `pwsh -NoProfile -File Tools/test-g4-tool-package-contract-gate.ps1` | `0` |
| `validate-central-package-versions.ps1`, `validate-dapr-package-versions.ps1`; `validate-consumer-package-authority.ps1` on an untracked-inclusive copy | `0`; consumer authority passed for all 15 projects |
| `git diff --check` | `0` |
| From Projects root: `python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py --workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json` | `0`; `G6-EVIDENCE-VALID` (no bound file drifted) |

After every live run: no container labelled `hexalith.g4.run`, no process
carrying `HEXALITH_G4_RUN_ID`, and no test workspace remained; the diagnostic
AppHost/resource logs contained neither the signing key nor any bearer token.
`dotnet format whitespace Hexalith.Builds.slnx --verify-no-changes` still
reports `FINALNEWLINE`/`ENDOFLINE` findings, all in pre-existing files (several
G-6-bound); none in files added by this slice.

Observed and fixed while qualifying: a fresh store keeps EventStore unhealthy
until the writer-protocol cutover; Aspire 13.5 adds never-started
`*-rebuilder` resources; module sidecars report `Running` before they are
resolvable, so readiness now probes Dapr invocation. One full live run failed
`ConcurrentRunsAreIsolated…` with `HXR021` (no diagnostic log was captured);
the isolated rerun and later full runs passed. Port allocation is now
serialized and never re-issues a port within one runner process; the root
cause of that single timeout is not proven.

Not done in this slice (Stages 4-7 and the review gate): the public command
still returns `HXR003`; no restart/rehydration-after-restart, retry/idempotency,
two-instance, stale/wrong-sequence/absent-projection controls, native report
capture, packaged live evidence, publication, or owner/Test Architect review.
The live lane requires Linux for the `/proc` process-tag sweep; on other
platforms only container cleanup is verified.

### Review Triage Log

Stage 3 slice review, 2026-09-23. Three layers ran on the slice diff: blind hunter,
edge-case hunter, and verification gap. The diff covers the untracked Stage 3
files plus the modified solution, gate script, integration test project, pins,
schema, and fixture test. Stages 1–2 were reviewed earlier and are not in it.

| # | Verdict | Route | Location | Evidence |
| --- | --- | --- | --- | --- |
| R1 | medium | patch | `CompositionEngine.StartAsync` | Only caller cancellation is caught. After `runId` exists, an IO, port-allocation, bind, or inner-timeout exception escapes. That leaks the AppHost, containers, the workspace, and a `Starting` state record. |
| R2 | medium | patch | `CompositionEngine.FailAndTearDownAsync` | Teardown steps can throw out of the handler. A `false` result from `TryDeleteAsync` is not reported as `HXR023`. |
| R3 | medium | patch | `CompositionRunResourceScanner.FindContainersAsync` | A failed or timed-out `docker ps` returns an empty list. `down` then reports `HXI001` and deletes the state while containers may remain. |
| R4 | medium | patch | `CompositionEngine.DownAsync` / `TryGetRecordedProcess` / kill helpers | Teardown uses the caller's token, so a cancelled `down` aborts halfway. `Win32Exception` and `AggregateException` from process APIs escape. |
| R5 | low | patch | `CompositionEngine.StartAppHost` log mirror | The opt-in diagnostic mirror writes resource output unredacted. Redacting the known run key is a direct fix. |
| R6 | medium | patch | `UiMarkerRegistrar` | A marker assembly loads into the default context without its own dependency resolution. A consumer UI assembly with private dependencies fails. `TypeLoadException` escapes the documented `InvalidOperationException`. |
| R7 | medium | patch | `CompositionToolchainPins` Redis image | The run-scoped infrastructure image uses the mutable tag `7.4-alpine`, so qualification runs are not reproducible. |
| R8 | medium | patch | `Hexalith.Builds.Module.Tests.csproj` | `PublicCommandStillReturnsHxr003ForExecutableFixtureAsync` needs the fixture descriptor DLLs built. The project has no build-order reference to them, so a clean project-only run gets exit 1. |
| R9 | low | patch | `Tools/test-g4-tool-package-contracts.ps1` | The canonical-output guard mixes `-or`/`-and` without parentheses (it fails closed, but with the wrong message). `WaitForExit()` has no timeout, so a hung tool stalls the gate. |
| R10 | gap | patch | `CompositionEngine` readiness branches | `HXR020`/`HXR021`/`HXR022` outcomes and the retained `Failed` state are never executed by a test. |
| R11 | gap | patch | `CompositionProcess.CreateStartInfo` | No test proves the inherited environment is scrubbed. |
| R12 | gap | patch | live lifecycle | No test proves that tagged run processes other than the AppHost and EventStore host lack the signing key. |
| R13 | gap | patch | `DownAsync` PID-reuse guard | The start-time guard is never exercised. |
| R14 | gap | patch | pins vs catalog | Nothing ties `SupportedPlatformPins` to the evaluated catalog properties. The new test goes in a new file because `ManifestValidationTests.cs` is G-6-bound. |
| R15 | gap | defer | live lane in CI | No CI job enables `HEXALITH_G4_LIVE=1` with Docker and a verified Dapr home, so the composition is verified only by manual live runs. |
| R16 | medium | defer | scanner/engine off Linux | Process enumeration is `/proc`-only, and Windows gets no graceful stop. The cleanup proof is Linux-only. Platform enumeration is not a small fix. |
| R17 | medium | defer | AppHost failure diagnosability | A faulted cutover, a blocked stdin listener, and unbounded readiness probes all surface as a generic `HXR021` timeout. The outcome still fails closed. |
| R18 | maybe-false | defer | `RunTopology` module endpoint | Assumes each module project has, or gets, an `http` endpoint. Unverified for consumer modules with https launch profiles. Would be medium. |
| R19 | low | defer | `SupportedPlatformPins` XML doc | Still says "candidate" pin. The file is G-6-bound, so edit it only with the next G-6 re-seal. |
| R20 | low | reject | `IsValidReadiness` resource set | The AppHost writes readiness itself into a private workspace, and the live tests assert every endpoint. Adding set checks adds complexity for an unlikely case. |
| R21 | low | reject | `New-CleanSourceSnapshot` includes untracked files | CI is a clean checkout, so the stray-file case is local-only. |
| R22 | false | reject | `RunTopology` image split | The image and tag are separate constants, so no registry-port or digest input can reach it. |
| R23 | false | reject | single-file `Assembly.Location` | The runner is a framework-dependent .NET tool and is never single-file published. |
| R24 | low | reject | empty descriptor diagnostics | The loader returns at least one diagnostic whenever it is invalid, so the case is unlikely. |
| R25 | low | reject | 48-bit namespace prefix | Collisions are negligible per host; the domain axis stays manifest-declared by design. |
| R26 | low | reject | line-ending renormalization churn | Commit hygiene only, handled when committing. |
| R27 | low | reject | unmapped `requiredLiveAssertions`; hard-coded hint versions; UTF-8 decoding / `quotePath`; removed `Push-Location` | Cosmetic or unlikely; each fix adds branches. |
| R28 | medium | patch | `StopSignal.DisposeAsync` / `CompositionEngine.DownCoreAsync` | Closing stdin was awaited before the listener bound, and a blocked listener could leave `down` reporting success after the AppHost logged HXR028. The close and listener now share a three-second bound; timeout writes HXR028 and the runner returns it from `down` after cleanup. |
| R29 | medium | patch | `RunReadiness.ReportStartupStallAsync` | Choosing the first unhealthy resource alphabetically could name a waiting EventStore when placement had already exited. A terminal failed resource is now preferred for both startup-stall and per-resource diagnostics. |
| R30 | low | patch | `RunReadiness.AppId` | EventStore and its Dapr sidecar were reported with app ID `-`; both now report `eventstore`. |
| R31 | medium | patch | `StopSignal.DisposeAsync` | `CancelAsync` itself waited for cancellation callbacks before the three-second listener bound began. Cancellation, stdin close, and listener completion now share that bound. |
| R32 | low | patch | `RunReadiness.TerminalFailure` | Aspire also reports a completed long-lived executable as `Finished`. It is now preferred over an alphabetically earlier waiting dependent when naming the stalled resource. |
| R33 | medium | patch | `RunTopology.Tag` / `CompositionPortAllocator` | The runner moved its eleven planned ports out of the ephemeral range, but Aspire still assigned dynamic module HTTP ports. In a concurrent live run Dapr internal gRPC bound `44617`, then `p0-orders` Kestrel tried to bind the same port and exited. The runner now allocates EventStore, UI, and one module HTTP port per module in the same checked range and supplies both target and host port to Aspire without a proxy. |
| R34 | medium | patch | `RunTopology` Redis endpoint / `AppHostRunner` resource wait | In a later full suite, Redis logged ready but repeated PINGs through Aspire's proxied endpoint each exhausted their three-second bound; cutover reported `endpoint-unavailable` after two minutes while EventStore waited for Redis. The Redis endpoint now maps the planned port directly to container port 6379, and dependency-ordered resource waits start concurrently with AppHost startup so a sustained Redis health failure reports Redis within its own bound. |
| R35 | medium | patch | `RunReadiness` document order / `StopSignal` disposal | Dependency-order resource waits wrote readiness in that order, violating the existing name-sorted live contract. A failed assertion then exposed a genuine blocked stdin listener on shutdown. The readiness document is sorted by resource name after the dependency-order waits, and the listener leaves stream ownership with `StopSignal`, which closes it synchronously on a bounded worker. |
| R36 | medium | patch | Dapr CLI sidecar endpoint allocation | A later full suite failed its shared fixture when `p0-inventory-app` Dapr gRPC tried to bind dynamically selected port `38759` and found it occupied. The run plan now reserves each sidecar HTTP, gRPC, internal gRPC, and metrics port outside the Linux ephemeral range. The CommunityToolkit creates proxied CLI endpoints by default, which still gave daprd dynamic target ports despite pinned endpoint ports; a Builds-owned subscriber makes those CLI endpoints direct before Aspire allocates them. |

Patch outcome, 2026-09-23. The implementation agent applied all fourteen `patch`
rows, R1–R14. R5–R15 are deferred to the Projects `deferred-work.md` ledger. Independent verification
from Builds (isolated `NUGET_PACKAGES`, `HEXALITH_DAPR_HOME=/tmp/hexalith-g6.c8hQ7T`):

| Exact command | Exit / result |
| --- | --- |
| `dotnet build Hexalith.Builds.slnx -c Debug -m:1` | `0`; 0 warnings, 0 errors |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll` | `0`; 163/163 |
| `dotnet test/Hexalith.Builds.Evidence.Tests/bin/Debug/net10.0/Hexalith.Builds.Evidence.Tests.dll` | `0`; 68/68 |
| Integration assembly without opt-in | `0`; 1 passed, 9 skipped (explicit reason, not counted as passing) |
| `HEXALITH_G4_LIVE=1 … IntegrationTests.dll` before the patches (two runs) | `0`, `0`; 10/10 each (159 s, 179 s), no labelled container or tagged process left |
| Same, after the patches | `1`; 9/10 — `LiveLifecycleTests.DownLeavesNothingTaggedAndIsIdempotentAsync` failed `HXR021` (readiness timeout, 488 s run, no diagnostic log enabled); teardown left nothing |
| Same with `HEXALITH_G4_LOG_DIR`, twice | `0`, `0`; 10/10 each (166 s); no JWT-shaped value in any diagnostic log |
| `pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage3.4 -RequireControls` | `0`; packed controls passed |
| `git diff --check` | `0` |
| Projects G-6 validator | `0`; `G6-EVIDENCE-VALID` |

The intermittent `HXR021` readiness timeout has now been observed twice in
about eleven full live runs: once by the implementation agent in the
concurrent-runs test, and once here in a single-run lifecycle test. Its root
cause is **not established** (see deferred R17 on diagnosability). It is a
known Stage 3 blocker for declaring the live lanes reliable. Teardown after
the failure was complete.

### 2026-09-23 Stage 3 HXR021 reproduction and root-cause repair

This continuation began from the existing uncommitted Builds tree. The
accepted P1R `3.106.0` tuple, the owner-accepted G-6 packet, FrontComposer
`4.5.0`, Stages 1–2, and the public `HXR003` run/test stop were preserved.
No G-6-bound source file was edited. `NUGET_PACKAGES` was isolated at
`/tmp/hexalith-g4-stage3-r17/nuget` for every build and test in this pass.

| Exact command from Builds | Exit / result |
| --- | --- |
| `DAPR_RUNTIME_PATH=/tmp/hexalith-g6.c8hQ7T/.dapr/bin /tmp/hexalith-g6.c8hQ7T/tools/dapr --version` | `0`; CLI `1.18.0` (CLI reports runtime `n/a`) |
| `/tmp/hexalith-g6.c8hQ7T/.dapr/bin/daprd --version` | `0`; runtime `1.18.2`; the placement and scheduler binaries were present and each live prerequisite probe verified their start-line version |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet build Hexalith.Builds.slnx -c Debug -m:1` | `0`; 0 warnings, 0 errors before reproduction |
| `bash /tmp/hexalith-g4-stage3-r17/run-live-loop.sh baseline 10` | Harness `0`; ten full live suite exits `0,0,0,0,0,0,0,1,0,0` — **1/10 failures (10%)**; each suite ran 10 tests and retained its test/AppHost/resource logs under `/tmp/hexalith-g4-stage3-r17/baseline/run-XX/` |

The loop executed this test command for each `XX=01` through `10`, with a
separate log directory and test output for each invocation:

```bash
NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget \
HEXALITH_G4_LIVE=1 \
HEXALITH_DAPR_HOME=/tmp/hexalith-g6.c8hQ7T \
HEXALITH_G4_LOG_DIR=/tmp/hexalith-g4-stage3-r17/baseline/run-XX \
timeout --signal=TERM --kill-after=30s 900s \
dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll -noColor
```

Run 08 failed in `LiveRunIsolationTests.ConcurrentRunsAreIsolatedAndDownOfOneLeavesTheOtherHealthyAsync`
with `HXR021` after 582.743 seconds (`baseline/run-08/test.log`, exit `1`).
The retained `baseline/run-08/isolation-73007963750b4189b11627d3b36cd657.log`
shows Dapr placement starting with `--port 33189`, then a fatal
`listen tcp 127.0.0.1:33189: bind: address already in use` at
`2026-09-23T05:59:14.987Z`. The host's
`/proc/sys/net/ipv4/ip_local_port_range` was `32768 60999`, so `33189` was
inside the outbound ephemeral range. The pre-fix allocator asked the kernel
for port `0`, then released the probe sockets before Dapr bound them. The
captured bind failure proves a released-port collision caused this timeout;
the exact competing socket owner was not retained. The other nine baseline
runs passed. After every baseline run, including the failure, the sweep found
zero `hexalith.g4.run`-labelled containers, zero processes carrying
`HEXALITH_G4_RUN_ID`, zero `hexalith-g4-it-*` workspaces, and zero JWT-shaped
values in retained logs.

Deferred review item R17 was repaired before diagnosing the failure log.
Redis PING now has a 3-second probe bound; each Aspire resource health wait
(including module `/alive`) has a 90-second bound; each Dapr invocation probe
has a 45-second bound; and writer-protocol cutover has a 2-minute bound.
The AppHost has a five-minute startup bound inside the unchanged six-minute
live `ReadinessTimeout`. A faulted cutover interrupts a blocked AppHost start;
a non-200 activation response reports `HXR027` immediately. A blocked stdin
listener is closed and bounded to three seconds, reporting `HXR028` if it
cannot finish. The AppHost writes a metadata-only startup-failure document;
the runner returns `HXR025`/`HXR026`/`HXR027`/`HXR028` with the resource,
app ID, and last status instead of waiting for generic `HXR021`.

The first port-collision fix in `CompositionPortAllocator` chooses checked loopback
ports outside Linux's configured ephemeral range, while retaining the
existing same-process allocation serialization and port memory. A regression
asserts every allocated Linux port lies outside that range; the prior
`port 0` implementation fails that assertion deterministically. No readiness
timeout was raised and no readiness retry was added. The competing socket
owner in the failed run was not captured. The fix removes the normal outbound
ephemeral-port collision path; another process binding a chosen non-ephemeral
port between probe release and child bind remains possible and is a limit of
preallocating child-process ports.

| Exact command from Builds after R17 and port repair | Exit / result |
| --- | --- |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet build Hexalith.Builds.slnx -c Debug -m:1` | `0`; 0 warnings, 0 errors |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*ReadinessFailuresTearDownAndRetainFailedStateAsync' -noColor` | `0`; 4/4, including a deterministic `HXR027` startup-failure document and bounded teardown |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*PortAllocatorReturnsDistinctLoopbackPorts' -method '*ConcurrentAllocationsAreDisjointAsync' -method '*ReadinessFailuresTearDownAndRetainFailedStateAsync' -noColor` | `0`; 6/6 |

The first R17 build attempts returned `1` on analyzer findings in the new
code; the corrections are included in the successful build above. A first
filtered test invocation without the xUnit wildcard returned `0` but ran
**zero** tests; it is not counted. The wildcard-filtered commands above ran
the named cases and passed.

A focused read-only review after the first ten passing suites identified
R28–R30 above. The R28 fix also makes a stalled stdin close itself bounded,
exits the AppHost nonzero with `HXR028`, and preserves that diagnostic through
`DownAsync` after resource cleanup. No G-6-bound file was changed. The first
Debug build of these corrections exited `1` on analyzer `S8949` and `CA2213`;
the final build below exited `0` after passing an explicit cancellation token
to the bounded worker and documenting the stream's worker-side disposal.

| Exact command from Builds after R28–R30 | Exit / result |
| --- | --- |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet build Hexalith.Builds.slnx -c Debug -m:1` | First attempt `1` (`S8949`, `CA2213`); corrected rerun `0`, 0 warnings, 0 errors |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*DownReportsBlockedStdinListenerAsync' -method '*ReadinessFailuresTearDownAndRetainFailedStateAsync' -method '*PortAllocatorReturnsDistinctLoopbackPorts' -method '*ConcurrentAllocationsAreDisjointAsync' -noColor` | `0`; 7/7, including HXR028 propagation through `down` |

The follow-up review found R31–R32. The AppHost now bounds cancellation
callbacks together with the stdin close and listener, and prioritizes Aspire's
`Finished` terminal state. To avoid treating runs on different binaries as one
stability sequence, the intermediate command
`bash /tmp/hexalith-g4-stage3-r17/run-live-loop.sh stability_final 10` was
stopped with `kill -TERM 1532344` (kill exit `0`, harness exit `143`) after five
recorded passes; its in-flight sixth suite completed 10/10 and cleaned up, but
was not counted in the final sequence. Its logs remain under
`/tmp/hexalith-g4-stage3-r17/stability_final/`.

| Exact command from Builds after R31–R32 | Exit / result |
| --- | --- |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet build Hexalith.Builds.slnx -c Debug -m:1` | `0`; 0 warnings, 0 errors |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*DownReportsBlockedStdinListenerAsync' -method '*ReadinessFailuresTearDownAndRetainFailedStateAsync' -method '*PortAllocatorReturnsDistinctLoopbackPorts' -method '*ConcurrentAllocationsAreDisjointAsync' -noColor` | `0`; 7/7 |

The first uninterrupted sequence on that build was
`bash /tmp/hexalith-g4-stage3-r17/run-live-loop.sh stability_verified 10`.
The harness exited `0` and inner suite exits were
`0,0,0,0,0,0,0,0,1,0`: **9/10 passed, 1/10 failed (10%)**. Run 09 failed in
`LiveRunIsolationTests.ConcurrentRunsAreIsolatedAndDownOfOneLeavesTheOtherHealthyAsync`
with specific `HXR025`, resource `p0-orders`, app ID `p0-orders-app`, status
`state=Finished;health=unknown`. Its retained
`stability_verified/run-09/isolation-1592c9425600477e97efa80bd049f83b.log`
shows the `p0-orders` Dapr sidecar binding internal gRPC port `44617` at
`2026-09-23T07:42:54.296Z`, then the module Kestrel host failing to bind
`http://127.0.0.1:44617` with `address already in use` at
`2026-09-23T07:42:55.963Z`. This exposed a second allocation path: Aspire
chose the host HTTP endpoint dynamically, outside the eleven runner-planned
ports. The failed run cleaned up fully; every suite in this sequence reported
zero labelled containers, tagged processes, test workspaces, and JWT-shaped
log files.

R33 addresses the module HTTP allocation path: the runner allocates EventStore HTTP,
UI HTTP, and one HTTP port per module alongside the control-plane and Dapr
HTTP ports, all outside Linux's outbound ephemeral range. `RunTopology.Tag`
passes each port as Aspire's host and target port with no proxy, so a module
host cannot be assigned a Dapr sidecar's internal gRPC port. The plan rejects
an HTTP-port count that differs from its module count. The allocator regression
now asserts all fifteen ports for the two-module fixture are distinct and
outside the ephemeral range. No readiness timeout or retry was increased.

| Exact command from Builds after R33 | Exit / result |
| --- | --- |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet build Hexalith.Builds.slnx -c Debug -m:1` | First attempt `1` on named-argument order analyzer `RCS1205`; corrected rerun `0`, 0 warnings, 0 errors |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -method '*PortAllocatorReturnsDistinctLoopbackPorts' -method '*ConcurrentAllocationsAreDisjointAsync' -method '*CreateDerivesRunUniqueNamespacesAndDeterministicOrder' -method '*CreateRejectsMalformedInputs' -noColor` | `0`; 4/4 |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget HEXALITH_G4_LIVE=1 HEXALITH_DAPR_HOME=/tmp/hexalith-g6.c8hQ7T HEXALITH_G4_LOG_DIR=/tmp/hexalith-g4-stage3-r17/focused_isolation timeout --signal=TERM --kill-after=30s 900s dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll -class '*LiveRunIsolationTests' -noColor` | `0`; 1/1. The retained resource log shows module HTTP listeners on planned ports below `30000`, while Dapr internal gRPC listeners remained in the ephemeral range. |

The first full pinned-HTTP loop,
`bash /tmp/hexalith-g4-stage3-r17/run-live-loop.sh stability_http_pinned 10`,
recorded five suite exits `0`, then run 06 exited `1` (five failed tests
sharing one run; 5/10 tests passed). The retained
`stability_http_pinned/run-06/shared-c972c8c411c14c85bc20bee3cf289430.log`
shows Redis `Ready to accept connections tcp` at
`2026-09-23T08:00:02.277Z`, followed by repeated `g4-redis` PING probe
timeouts at three seconds each. The runner returned `HXR027` with
`cutover-timeout;endpoint-unavailable` because EventStore was still waiting
for Redis. Earlier successful runs showed Docker assigning an ephemeral host
mapping (for example `127.0.0.1:62528->6379/tcp`) behind Aspire's proxy; the
proxy/data path did not relay PONG in the failed run. The exact proxy failure
mechanism is not visible in the retained logs. The direct fixed mapping
removes that proxy path. The loop was stopped with `kill -TERM 2040336`
(kill exit `0`, harness exit `143`) after the failure; its in-flight seventh
suite completed 10/10. All recorded runs and the in-flight run cleaned up.

R34 changes the Redis endpoint to `isProxied: false`; the AppHost now starts
its Redis-first bounded resource wait alongside startup and cutover, so a
sustained Redis PING stall names `redis` before the dependent cutover expires.
The direct-mapping focused lane printed
`hexalith-g4-d7a046ecb8aa3e8b98e0fea98de6295f-redis 127.0.0.1:22859->6379/tcp`
from Docker while running, proving that the planned non-ephemeral port reaches
Redis directly. No readiness timeout was raised and no retry was added.

| Exact command from Builds after R34 | Exit / result |
| --- | --- |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet build Hexalith.Builds.slnx -c Debug -m:1` | First attempt `1` on analyzer `CA2025`; corrected rerun `0`, 0 warnings, 0 errors |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget HEXALITH_G4_LIVE=1 HEXALITH_DAPR_HOME=/tmp/hexalith-g6.c8hQ7T HEXALITH_G4_LOG_DIR=/tmp/hexalith-g4-stage3-r17/redis_direct_focus timeout --signal=TERM --kill-after=30s 900s dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll -class '*LivePersistedRunTests' -noColor` | `0`; 5/5 |
| During that run: `for attempt in $(seq 1 40); do container=$(docker ps --filter label=hexalith.g4.run --format '{{.Names}} {{.Ports}}' \| head -1); if [ -n "$container" ]; then printf '%s\\n' "$container"; exit 0; fi; sleep 1; done; exit 1` | `0`; printed the fixed `127.0.0.1:22859->6379/tcp` mapping above |

The first full direct-Redis loop,
`bash /tmp/hexalith-g4-stage3-r17/run-live-loop.sh stability_direct_redis 10`,
recorded run 01 exit `1` (9/10 tests; lifecycle's readiness resource order
assertion failed) and the in-flight run 02 also finished 9/10 for the same
assertion. Run 01's retained
`stability_direct_redis/run-01/lifecycle-5a2617de13bd405eab83eeae48ebfd4c.log`
also recorded `HXR028` during failed-assertion teardown. The harness was
stopped by `kill -TERM 2163130` (kill exit `0`, harness exit `143`) so no later
run was counted. Both runs removed every tagged resource and workspace.

R35 keeps dependency-order waits but sorts the published readiness document
by resource name, preserving the accepted contract. The stdin listener now
leaves the stream open on its reader and a bounded worker closes the owned
stream synchronously; the three-second HXR028 bound remains. A focused
lifecycle rerun passed both tests and its logs contained no HXR028.

| Exact command from Builds after R35 | Exit / result |
| --- | --- |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet build Hexalith.Builds.slnx -c Debug -m:1` | First attempt `1` on analyzer `IDE0200`; corrected rerun `0`, 0 warnings, 0 errors |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget HEXALITH_G4_LIVE=1 HEXALITH_DAPR_HOME=/tmp/hexalith-g6.c8hQ7T HEXALITH_G4_LOG_DIR=/tmp/hexalith-g4-stage3-r17/lifecycle_order_focus timeout --signal=TERM --kill-after=30s 900s dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll -class '*LiveLifecycleTests' -noColor` | `0`; 2/2 |

The next full loop exposed the remaining sidecar allocation path. From Builds,
`bash /tmp/hexalith-g4-stage3-r17/run-live-loop.sh stability_sorted_direct 10`
was deliberately stopped with `kill -TERM 2205970` (`0`) after run 07 and
returned `143`. Its seven inner exits were `0,0,0,0,0,1,0`; every completed
run recorded zero labelled containers, tagged processes, workspaces, and
JWT-shaped retained-log files. Run 06's shared fixture reported `HXR026` for
`p0-inventory-app` after `HTTP 500;probe-timeout`. The retained AppHost/resource
log at `/tmp/hexalith-g4-stage3-r17/stability_sorted_direct/run-06/shared-984655f8fc324f57ae816a51321c08bd.log`
records `Failed to listen for gRPC server on TCP address :38759 ... bind:
address already in use` at `2026-09-23T08:30:07.211Z`. The test log is
`/tmp/hexalith-g4-stage3-r17/stability_sorted_direct/run-06/test.log`.
Port `38759` falls inside the host's `32768–60999` ephemeral range. The
module HTTP and runner infrastructure ports were pinned, but Aspire's Dapr
CLI sidecars still had dynamically selected API and metrics endpoints.

R36 adds four allocated ports per module sidecar and the remaining three
EventStore sidecar ports to `CompositionRunPorts`, with existing EventStore
Dapr HTTP retained. A deterministic allocator test checks all 26 ports for a
two-module run are distinct and outside the Linux ephemeral range; plan
validation requires a sidecar group for every module. The first focused live
run after assigning sidecar options passed 5/5, but its log showed Aspire's
proxied CLI endpoint used dynamic *target* ports for HTTP, gRPC, and metrics.
The final Builds-owned `DaprDirectEndpointSubscriber` sets these CLI
endpoints to direct, with target port equal to the reserved port, after the
CommunityToolkit's BeforeStart handler creates them and before Aspire binds
them. The next focused live run passed 5/5; its log shows all sidecar HTTP,
gRPC, internal gRPC, and metrics listeners below `32768`.

| Exact command from Builds during R36 | Exit / result |
| --- | --- |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet build Hexalith.Builds.slnx --no-restore -v quiet` | First attempt `1` (`IDE0305`); second `1` (`SA1210`); third `0`, 0 warnings/errors, before the direct-endpoint subscriber. After adding it: first `1` (`S3267`, `IDE0058`, `CA1812`), corrected rerun `0`, 0 warnings/errors. |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget HEXALITH_G4_LIVE=1 HEXALITH_DAPR_HOME=/tmp/hexalith-g6.c8hQ7T HEXALITH_G4_LOG_DIR=/tmp/hexalith-g4-stage3-r17/sidecar_pinned_focus timeout --signal=TERM --kill-after=30s 900s dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll -class '*LivePersistedRunTests' -noColor` | `0`; 5/5; log exposed the still-dynamic proxied target ports. |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget HEXALITH_G4_LIVE=1 HEXALITH_DAPR_HOME=/tmp/hexalith-g6.c8hQ7T HEXALITH_G4_LOG_DIR=/tmp/hexalith-g4-stage3-r17/sidecar_direct_focus timeout --signal=TERM --kill-after=30s 900s dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll -class '*LivePersistedRunTests' -noColor` | `0`; 5/5; all four sidecar port classes on reserved non-ephemeral ports. |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -class '*CompositionRunPlanFactoryTests' -noColor` | `0`; 6/6. |

#### Short live-evidence summary for Stage 3 owner/Test Architect review

The **final post-R36 opt-in live lane** ran ten consecutive full suites on one
unchanged build: **10/10 suites and 100/100 live tests passed; observed flake
rate 0/10 (0%)**. Each suite covered lifecycle and idempotent `down`, persisted
two-module writes and Redis end-state, Dapr invocation, identity and tenant
isolation, FrontComposer, concurrent-run isolation, and mid-start cancellation.
The runner used isolated NuGet packages and the verified Dapr CLI 1.18.0 /
runtime 1.18.2 home. The exact harness command was
`bash /tmp/hexalith-g4-stage3-r17/run-live-loop.sh stability_sidecar_direct 10`
(exit `0`); its per-suite command and environment are preserved in
`/tmp/hexalith-g4-stage3-r17/run-live-loop.sh`. The ten inner exits in
`stability_sidecar_direct/results.txt` were all `0`.

| Full-suite lane / source state | Recorded runs | Passes | Observed suite failure rate |
| --- | ---: | ---: | ---: |
| `baseline` before R17 and port fixes | 10 | 9 | 1/10 (10%); HXR021 placement bind |
| `stability` after first port fix | 10 | 10 | 0/10 (0%); superseded by later source changes |
| `stability_final` after R28–R32, interrupted after five recorded runs | 5 | 5 | 0/5 (0%); sixth in-flight pass not counted |
| `stability_verified` after R28–R32 | 10 | 9 | 1/10 (10%); module HTTP bind |
| `stability_http_pinned` after R33, interrupted after six recorded runs | 6 | 5 | 1/6 (16.7%); Redis proxy stall |
| `stability_direct_redis` after R34, interrupted after one recorded run | 1 | 0 | 1/1 (100%); readiness document order assertion |
| `stability_sorted_direct` after R35, interrupted after seven recorded runs | 7 | 6 | 1/7 (14.3%); sidecar gRPC bind |
| **`stability_sidecar_direct` after R36, final source** | **10** | **10** | **0/10 (0%)** |

The lanes used evolving source; only the final row qualifies the final source.
All **59 recorded full suites**, including the five failed suites, reported
zero `hexalith.g4.run` labelled containers, tagged processes, test workspaces,
and JWT-shaped retained-log files in their per-run result lines. A final
`/proc/*/environ` sweep and a scan of all retained `*.log` files also returned
zero. The baseline HXR021, module HTTP, Redis, and sidecar gRPC failure logs
remain under their lane directories in `/tmp/hexalith-g4-stage3-r17/` for
review. The no-opt-in lane had one passing test and nine explicit live skips;
those skips are excluded from live pass counts. Public `run`/`test` remains
`HXR003`. Stage 3 and P0 checkboxes remain unchecked pending owner and named
Test Architect review.

The final ten per-run lines record UTC start/end, suite exit, labelled-container
count, tagged-process count, workspace count, and JWT-shaped log-file count.
The earliest final run started `2026-09-23T08:44:04Z`; run 10 ended
`2026-09-23T09:06:43Z`.

| Historical command from Builds after the first ten live passes | Exit / result |
| --- | --- |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -noColor` | `0`; 164/164, including both unchanged public `HXR003` run/test cases |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Evidence.Tests/bin/Debug/net10.0/Hexalith.Builds.Evidence.Tests.dll -noColor` | `0`; 68/68 |
| `env -u HEXALITH_G4_LIVE NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll -noColor` | `0`; 1 passed, 9 explicitly skipped, 0 failed; skips are not live passing evidence |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage3.5 -RequireControls` | `0`; Release build 0 warnings/errors; Evidence 68/68, Module 164/164, Integration 1 passed/9 skipped; exact-version two-tool restore, source/package parity, and packed positive/negative controls passed |
| `docker ps -aq --filter label=hexalith.g4.run \| wc -l` | `0`; printed `0` |
| `find /tmp -maxdepth 1 -type d -name 'hexalith-g4-it-*' \| wc -l` | `0`; printed `0` |
| `rg -l 'eyJ[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}\\.[A-Za-z0-9_-]{16,}' /tmp/hexalith-g4-stage3-r17/baseline /tmp/hexalith-g4-stage3-r17/stability \| wc -l` | `0`; printed `0` |

The `/proc/*/environ` sweep also printed `0` for processes carrying
`HEXALITH_G4_RUN_ID`. R17 source is fixed locally; the Projects deferred-work
ledger remains under Projects ownership and was not edited. Stage 3 and P0
checkboxes remain unchecked. Owner and named Test Architect review of this
live evidence, and later Stages 4–7, remain required before P0 acceptance.

| Historical first post-fix command | Exit / result |
| --- | --- |
| From Builds: `git diff --check` | `0`; no whitespace errors. Git warned that the pre-existing G-6-bound `RuntimePrerequisiteGateTests.cs` has LF that would be replaced by CRLF if Git touched it; this file was not edited in this continuation. |
| From Projects: `python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py --workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json` | `0`; `G6-EVIDENCE-VALID` after the Stage 3 changes and live qualification. |

#### Final post-R36 verification and remaining review gate

| Exact command from Builds unless stated otherwise | Exit / result |
| --- | --- |
| `bash /tmp/hexalith-g4-stage3-r17/run-live-loop.sh stability_sidecar_direct 10` | `0`; all ten inner exits `0`; 100/100 live tests, no per-run cleanup or redaction findings. |
| Final ten-run result and test-log validation script below | `0`; printed `10/10 full suites, 100/100 live tests; all 40 per-run cleanup/redaction counts zero`. |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -noColor` | `0`; 165/165, including the unchanged public HXR003 run/test cases. |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Evidence.Tests/bin/Debug/net10.0/Hexalith.Builds.Evidence.Tests.dll -noColor` | `0`; 68/68. |
| `env -u HEXALITH_G4_LIVE NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test/Hexalith.Builds.Tooling.IntegrationTests/bin/Debug/net10.0/Hexalith.Builds.Tooling.IntegrationTests.dll -noColor` | `0`; one pass, nine explicit live skips, zero failures. |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage3.6 -RequireControls` | `0`; Release build 0 warnings/errors; Evidence 68/68, Module 165/165, Integration one pass/nine skips; two-tool exact-version restore, source/package parity, packed positive and negative controls passed. Inventory: `/tmp/hexalith-builds-g4-packages-ae09b325340f4d56b7227faf07c8be5b/g4-tool-package-inventory.json`. |
| `docker ps -aq --filter label=hexalith.g4.run \| wc -l` | `0`; printed `0`. |
| Final `/proc/*/environ` count script below | `0`; printed `0`. |
| `find /tmp -maxdepth 1 -type d -name 'hexalith-g4-it-*' \| wc -l` | `0`; printed `0`. |
| `rg -l 'eyJ[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}' /tmp/hexalith-g4-stage3-r17 -g '*.log' \| wc -l` | `0`; printed `0` across all retained lanes. |
| `git diff --check` | `0`; no whitespace errors. Git repeated the pre-existing LF-to-CRLF warning for G-6-bound `RuntimePrerequisiteGateTests.cs`, which was not edited in this continuation. |
| From Projects: `python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py --workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json` | `0`; `G6-EVIDENCE-VALID` on the final post-R36 tree. |

The exact final ten-run validation command was:

```bash
python3 - <<'PY'
from pathlib import Path
root=Path('/tmp/hexalith-g4-stage3-r17/stability_sidecar_direct')
lines=(root/'results.txt').read_text().splitlines()
assert len(lines)==10
assert all(' exit=0 containers=0 processes=0 workspaces=0 jwt_log_files=0' in line for line in lines)
for i in range(1,11):
    log=(root/f'run-{i:02d}'/'test.log').read_text()
    assert 'Total: 10, Errors: 0, Failed: 0, Skipped: 0' in log,(i,log[-500:])
print('10/10 full suites, 100/100 live tests; all 40 per-run cleanup/redaction counts zero')
PY
```

The exact final tagged-process sweep was:

```bash
python3 - <<'PY'
from pathlib import Path
count = 0
for proc in Path('/proc').iterdir():
    if proc.name.isdigit():
        try:
            if b'HEXALITH_G4_RUN_ID=' in (proc / 'environ').read_bytes():
                count += 1
        except (OSError, PermissionError):
            pass
print(count)
PY
```

No G-6-bound source file was edited, no readiness timeout was raised, and no
retry was added. The exact internal failure in Aspire's Redis proxy path is
not visible in retained logs; the direct Redis mapping removes that observed
failure path. Owner and **named Test Architect** review of the final live
logs remains the Stage 3 gate. Later Stages 4–7 remain outside this slice.

### 2026-09-23 Stage 3 owner and named Test Architect evidence review

**Disposition: acceptance withheld in both role assessments. Stage 3 and P0
remain unchecked; public `run`/`test` remains `HXR003`.** Jérôme Piquot was
supplied as the reviewer name for the requested owner/Test Architect review.
Codex performed and authored the technical assessments below, using independent
adversarial and edge-case review passes. Naming the reviewer does not constitute
Jérôme Piquot's personal sign-off; no such approval is asserted here.

| Review role | Named reviewer | Technical findings | Acceptance decision |
| --- | --- | --- | --- |
| Builds/Platform owner | Jérôme Piquot | Codex verified the observed final live streak, R17/R28–R36 source changes, the retained failure history, current package controls, public HXR003, and G-6. Exact unchanged-build attribution is not bound by the historical run records (S3-REV3). | **WITHHOLD** acceptance on the evidence presented. Personal owner acceptance is not recorded. |
| Test Architect | Jérôme Piquot | Codex independently checked all ten final test logs and cleanup/redaction counters, examined the live assertions and patch tests, and verified fresh package/control results. Cleanup probe success and the general metadata-only output contract remain insufficiently proved (S3-REV1/S3-REV2). | **WITHHOLD** acceptance on the evidence presented. Personal Test Architect acceptance is not recorded. |

#### Evidence independently checked

The retained final `stability_sidecar_direct/results.txt` has exactly ten
sequential runs, each with exit `0`. Every matching `run-01` through
`run-10/test.log` reports `Total: 10, Errors: 0, Failed: 0, Skipped: 0`:
**10/10 suites, 100/100 live tests, observed suite failure rate 0/10**. This is
an observed streak, not a guarantee that the runtime cannot fail. All 40 final
cleanup/redaction counters are zero. The eight lane summaries reconcile to
59 recorded suites and five failed suites; interrupted in-flight passes are
not added to that total.

The reviewer read the retained placement bind failure in `baseline/run-08`,
module HTTP failure in `stability_verified/run-09`, Redis probe stall in
`stability_http_pinned/run-06`, ordering assertion in
`stability_direct_redis/run-01`, and sidecar gRPC bind failure in
`stability_sorted_direct/run-06`. These support the recorded failure sequence;
the competing socket owner and internal Redis proxy fault remain unknown.
Source inspection confirms R17's bounded probes and specific failure documents,
R28–R32's shutdown/diagnostic corrections, R33's project listener ports, R34's
direct Redis endpoint, R35's sorted readiness, and R36's 26 distinct allocated
ports plus direct sidecar endpoints. The fresh Module suite passes the existing
regressions, including the unchanged public HXR003 run/test cases. Port probe
sockets are still released before child binding, so this is not an exclusive
cross-process port reservation.

`python3 /tmp/hexalith-stage3-review-y27o7eu3/check-retained-evidence.py`
exited `0`. Its `retained-evidence-check.json` and `retained-log-sha256.json`
record the reviewed evidence: 393 readable retained logs, zero JWT-shaped
matches, current Docker enumeration exit `0` with zero labelled containers,
zero test workspaces, and zero tagged processes among readable environments.
The process scan also recorded **97 permission-denied environment reads**;
therefore it is not an exhaustive proof over all processes. The new log hashes
identify the files inspected today, not the binaries used in the historical
runs. The live lifecycle tests additionally assert run-scoped teardown,
idempotent `down`, workspace/state removal, and signing-key absence from
retained documents and inspected non-validator processes.

The original final `stage3.6` inventory path above no longer exists; the retained
`package-gate.log` under the R17 directory qualifies historical `stage3.5`.
The gate removes its package directory unless `-RetainPackageDirectory` is set.
To obtain inspectable current evidence, Codex ran from Builds:

```bash
env -u HEXALITH_G4_LIVE NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget \
  pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 \
  -Version 0.0.0-stage3.6 -RequireControls -RetainPackageDirectory \
  -PackageDirectory /tmp/hexalith-stage3-review-y27o7eu3/packages \
  > /tmp/hexalith-stage3-review-y27o7eu3/package-gate.log 2>&1
```

Exit `0`: Release build with zero warnings/errors; Module **165/165**,
Evidence **68/68**, Integration **one pass/nine explicit skips**, exact-version
two-tool restore, Debug/source versus Release/package parity, positive controls,
and **45 negative controls** passed. Those nine skips are excluded from live
pass evidence. The retained packaged run/test outputs both report
`unavailable` / `PrerequisiteUnavailable` / `HXR003`; packaged `down` and the
positive readiness control succeed. The reviewer independently recomputed
all **58** inventory-listed package/evidence file hashes and sizes.

Retained inventory:
`/tmp/hexalith-stage3-review-y27o7eu3/packages/g4-tool-package-inventory.json`;
SHA-256 `ce89a24f1caabe9acb52e56b3271bf7cc5b708d90183315d9bf639544ba87755`.
It contains exactly `Hexalith.Builds.Module.Cli` and
`Hexalith.Builds.Evidence.Cli`, each with `.nupkg` and `.snupkg` at
`0.0.0-stage3.6`. `releaseEligible` is correctly `false` for the existing dirty
source tree and untracked fixture provenance. This new local verification does
not recover the missing historical inventory, prove remote publication, or
satisfy Stages 4–7.

From Projects, the following read-only validation exited `0` with
`G6-EVIDENCE-VALID` during this review:

```bash
python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py \
  --workspace . \
  --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json \
  --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json
```

G-6 validity confirms the bound packet remains valid; it does not grant G-4
acceptance (`g4Approved` remains false).

#### Findings and patch-disposition reconciliation

| Finding / lens | Location and trigger | Required evidence or correction | Consequence / disposition |
| --- | --- | --- | --- |
| S3-REV1 — adversarial and edge-case (independently found) | `run-live-loop.sh` pipes Docker, filesystem and JWT scans into `wc -l` without preserving scanner failure; `/proc` read errors are discarded. | Retain individual probe exits/errors and distinguish inaccessible processes from confirmed absence; treat failed inspection as unverified. | Historical counters report zero but cannot alone establish complete cleanup/redaction proof. Current inaccessible process environments confirm the limitation. **Open acceptance finding**; no leftover run resource was observed. |
| S3-REV2 — edge-case | `CompositionEngine.StartAppHost` diagnostic `Mirror` replaces only the exact signing key before appending arbitrary resource output. A logged bearer token, other credential or payload would remain. | Enforce the metadata-only diagnostic contract and exercise seeded sensitive-output cases, including tokens and payloads, through the actual mirror. | The 393-log JWT scan passes, but does not establish general redaction or metadata-only retention. **Open acceptance finding**; no actual token leak is claimed. |
| S3-REV3 — adversarial | The final harness invokes mutable Debug DLL paths without recording binary hashes or a source fingerprint per run. | Bind future acceptance runs to retained binary/source hashes and verify they do not change during the streak. | Accept the observed test counts; the stronger historical claim of one unchanged final build is not independently provable from these logs. **Open acceptance finding**. |
| S3-REV4 — adversarial | The cited historical `stage3.6` inventory is absent; retained R17 package log is `stage3.5`. | Preserve the fresh retained qualification and its inventory identified above. | **Resolved for current local package-control verification** by the successful rerun; historical evidence remains unavailable. |
| S3-REV5 — adversarial | Earlier patch-outcome prose says both R1–R14 were patched and R5–R15 were deferred; the R17 triage row still shows its original deferral. | Use the reconciliation below when reading the chronological record. | **Resolved in this review record**; the earlier prose is not the current disposition. |

The correct original deferred range is **R15–R19**, as confirmed read-only in
Projects' `deferred-work.md`; **R1–R14 were patched**. **R17 was subsequently
fixed locally**, and R28–R36 record the follow-up patches. Projects' historical
R17 ledger entry was not edited. R15 (live CI), R16 (non-Linux cleanup), R18
(consumer HTTPS-only profile), and R19 (G-6-bound wording) remain deferred.
This review neither reopens verified Stages 1–2 nor treats deferred work as
completed. Both explicit acceptances are absent; the Stage 3 and P0 completion
checkboxes remain unchecked and later-stage requirements remain outstanding.

Only this owner story was edited by the review. Existing tracked/untracked
source bytes and the Git index were preserved; no G-6-bound file was edited,
and no staging, commit, reset, or live-suite rerun was performed.

### 2026-09-23 Stage 3 review-finding remediation and requalification

**Codex technical disposition:** S3-REV1, S3-REV2, and S3-REV3 are corrected
and locally requalified on the final source. This is a technical assessment,
not Builds/Platform owner or Test Architect acceptance. Jérôme Piquot's later
personal assessment is recorded below. **Stage 3 and P0 checkboxes remain
unchecked**, and public `run`/`test` remains `HXR003`.

| Finding | Correction and focused evidence | Technical disposition |
| --- | --- | --- |
| S3-REV1 | Builds-owned `Tools/run-g4-stage3-live-qualification.py` records each Docker, process-environment, workspace, and log scan's command, exit, count, errors, and verified/unverified status. A failed Docker invocation, unreadable owned process environment, missing environment of an existing process, or unreadable log yields a null count and unverified suite. Vanished processes are counted separately. The scan covers same-UID processes started during that suite; earlier unrelated processes are explicitly outside its scope. Five Python controls pass. | **Corrected and demonstrated fail-closed.** Final-code campaign run 02 retained a real `PermissionError` for PID `3251972`; its 10 test cases passed, but its process probe was unverified and the campaign exited `1`. It is excluded from the clean streak. |
| S3-REV2 | `CompositionEngine.StartAppHost` now attaches `CompositionDiagnosticMirror`, which writes only fixed `apphost.stdout.observed` / `apphost.stderr.observed` markers and never writes resource output bytes. The actual process-stream mirror regression sends a seeded JWT-shaped token, bearer credential, password, and JSON payload through stdout and stderr and asserts that none is retained. | **Corrected and focused regression green.** `CompositionRedactionTests` 4/4, full Module tests 166/166. The mirror deliberately retains less troubleshooting detail. |
| S3-REV3 | The live harness hashes its 371 build/source/test input files and 544 Debug binary/descriptor files, retains the per-file manifest, checks both fingerprints before and after every suite, and rejects drift. A focused control proves that a changed source fingerprint rejects an otherwise passing suite. | **Corrected and requalified.** First-campaign runs 03–10 followed immediately by continuation runs 01–02 form ten consecutive verified suites on source SHA-256 `b30ea7ba4898d5a27588b8ca9f9f0829e2cc4dc9c8eb20a01af29785f4fec99c` and binary SHA-256 `18c51ba78e0cc038b12ac4ea9b99692b198a5e67367ac59d7f02ab7b5653ff2b`. Every before/after comparison matched. |

The first final-code ten-suite command below exited `1` because suite 02's
process inspection was unverified. Its failure and all nine passing results,
individual test/resource logs, probe errors, exact command and hashes remain
under `qualification-evidence/stage3-review-remediation-20260923-final/`.
Runs 03–10 then passed consecutively. No build, source edit, or live suite ran
between that campaign and the two-suite continuation; both baseline manifests
are byte-equivalent. The retained `combined-streak.json` and
`verify-evidence.py` recheck the resulting **10/10 consecutive full suites,
100/100 live tests, zero failures/skips, 40 verified zero-count cleanup and
redaction probes**, matching log hashes, and matching source/binary hashes.
This observed streak is not a guarantee of future reliability.

| Exact command (Builds unless marked Projects) | Exit and retained result |
| --- | --- |
| `python3 -m unittest discover -s test/qualification -v` | `0`; 5/5 fail-closed and drift controls. |
| `dotnet build test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj -c Debug --no-restore -m:1 -p:NuGetAudit=false -p:MinVerVersionOverride=1.0.0 -v:q` | `0`; zero warnings/errors. The preceding red build failed as expected with `CS0103` before `CompositionDiagnosticMirror` existed; subsequent test and analyzer errors were fixed before qualification. |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -noColor -class Hexalith.Builds.ModuleTool.Tests.CompositionRedactionTests` | `0`; 4/4. |
| `dotnet test/Hexalith.Builds.Module.Tests/bin/Debug/net10.0/Hexalith.Builds.Module.Tests.dll -noColor` | `0`; 166/166, including public HXR003 checks. |
| `dotnet build test/Hexalith.Builds.Tooling.IntegrationTests/Hexalith.Builds.Tooling.IntegrationTests.csproj -c Debug --no-restore -m:1 -p:NuGetAudit=false -p:MinVerVersionOverride=1.0.0 -v:q` | `0`; zero warnings/errors before the final-code live run. |
| `python3 Tools/run-g4-stage3-live-qualification.py --evidence-dir _bmad-output/implementation-artifacts/qualification-evidence/stage3-review-remediation-20260923-final --dapr-home /tmp/hexalith-g6.c8hQ7T --nuget-packages /tmp/hexalith-g4-stage3-r17/nuget --count 10` | `1`; all 100 test cases passed, but suite 02 was unverified (`/proc/3251972/environ`: permission denied), so only 9/10 suites qualified. Failure retained, not reclassified. |
| `python3 Tools/run-g4-stage3-live-qualification.py --evidence-dir _bmad-output/implementation-artifacts/qualification-evidence/stage3-review-remediation-20260923-continuation --dapr-home /tmp/hexalith-g6.c8hQ7T --nuget-packages /tmp/hexalith-g4-stage3-r17/nuget --count 2` | `0`; 2/2 suites. Together with preceding runs 03–10: 10/10 consecutive, 100/100 tests, 40/40 verified zero-count probes, no hash drift. |
| `env -u HEXALITH_G4_LIVE NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage3.8 -RequireControls -RetainPackageDirectory -PackageDirectory /home/administrator/projects/hexalith/projects/references/Hexalith.Builds/_bmad-output/implementation-artifacts/qualification-evidence/stage3-review-remediation-20260923-continuation/packages > _bmad-output/implementation-artifacts/qualification-evidence/stage3-review-remediation-20260923-continuation/package-gate.log 2>&1` | `0`; Release build zero warnings/errors; Evidence 68/68, Module 166/166, Integration 1 pass/9 explicit opt-out skips; exact-version two-tool restore, source/package parity, positive controls and 45 negative controls passed. The nine package-gate skips are not counted as live passes. |
| `python3 _bmad-output/implementation-artifacts/qualification-evidence/stage3-review-remediation-20260923-continuation/verify-evidence.py > _bmad-output/implementation-artifacts/qualification-evidence/stage3-review-remediation-20260923-continuation/verify-evidence.log 2>&1` | `0`; `LIVE-STREAK-VALID`, 58/58 package/evidence hashes and sizes, packaged run/test `HXR003` with evidence exit `2`. |
| Projects: `python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py --workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json` | `0`; `G6-EVIDENCE-VALID` after the final gate. |
| `git diff --check` | `0`; only the pre-existing LF-to-CRLF warning for untouched G-6-bound `RuntimePrerequisiteGateTests.cs`. |

The final retained package inventory is
`qualification-evidence/stage3-review-remediation-20260923-continuation/packages/g4-tool-package-inventory.json`,
SHA-256 `17c62aa222224098dadd040b7fc239d3f16c559cc233f08c485a8a98c516c3d1`.
All 58 entries (four package archives and 54 qualification-evidence files)
were independently rehashed and size-checked. The umbrella Builds-owned
`qualification-evidence/stage3-review-remediation-20260923-manifest.json`
lists and verifies 298 retained files; its SHA-256 is
`6369963db09f4b3ad7d32a000e3e6b8a8ffd58c966b5860565ada3eff3afaf58`.
The earlier 10/10 campaign and `stage3.7` package gate remain retained as
superseded evidence. Five pre-remediation failed suites are preserved as
metadata-only summaries with original test-log hashes and historical command
rows; their raw resource logs were not copied because the old mirror retained
unbounded output. Their original cleanup counters and source/binary attribution
remain unverified. A first ad hoc inventory verifier exited `1` by reading
`status` instead of evidence artifact `finalStatus`; the corrected retained
verifier exited `0`, with no package or hash mismatch.

The final `stage3.8` inventory correctly has `releaseEligible: false` for the
dirty/untracked local source. Release qualification rebuilt 12 shared fixture
binary/PDB paths after the live streak; the retained post-gate fingerprint
records that expected later change and confirms the 371-file source fingerprint
was unchanged. It does not retroactively invalidate the per-suite hashes, but
a new live streak would need a new Debug build and baseline. Process cleanup
proof is scoped to this Linux same-UID test harness and processes started in
each suite; inaccessible environments fail closed. Public run/test is still
`unavailable` / `PrerequisiteUnavailable` / `HXR003` with exit `2`; G-6 validity
does not grant G-4 approval. The personal Stage 3 assessment below accepts
this evidence review; the public runtime cutover, later Stages 4–7, full P0
acceptance, and deferred R15/R16/R18/R19 remain open. No G-6-bound file,
other repository, dependency pin, or Git index entry was changed.

### 2026-09-23 Jérôme Piquot personal Stage 3 assessment

Jérôme Piquot supplied the decision in this conversation: **“Accept both Stage
3 assessments.”** This is his personal assessment of the remediated Stage 3
evidence in both named roles, distinct from Codex's technical findings and the
earlier historical withholding decision.

| Role | Jérôme Piquot's assessment | Scope |
| --- | --- | --- |
| Builds/Platform owner | **ACCEPT** | Accepts the Stage 3 owner evidence assessment after S3-REV1–S3-REV3 remediation and requalification. |
| Named Test Architect | **ACCEPT** | Accepts the Stage 3 Test Architect evidence assessment after the fail-closed probe, metadata-only mirror, and unchanged-binary streak checks. |

This decision records acceptance of the two Stage 3 evidence assessments. It
does not approve the full P0 acceptance record, publish packages, or open
public `run`/`test`, which still returns `HXR003`. Stage 3's public-composition
task and P0 remain in progress, so their checkboxes stay unchecked.

### 2026-09-23 Stage 3 public runtime composition cutover

**Disposition: Stage 3 local supported public-command composition is complete;
P0 remains in progress.** This cutover follows Jérôme Piquot's accepted owner
and named Test Architect Stage 3 assessments above. The historical `HXR003`
statements in earlier sections describe the source before this cutover.

The public `hexalith-module` command now validates an executable manifest and
its descriptors before calling the qualified `CompositionEngine`. `run` hands
off a ready, run-ID-scoped AppHost that remains live after the CLI process
exits. `down --run-id` selects only the matching manifest/run pair, cleans its
resources, and is idempotent; ambiguous implicit down fails with `HXR024`.
Missing or incorrect Docker/Dapr prerequisites fail before mutation with the
engine's `HXR01x` diagnostics. Cancellation and output/evidence failures
attempt bounded cleanup and preserve the first causal nonpassing outcome.
Human/JSON output, child-process logs, run state, and retained probe records
remain metadata-only.

Public `test --profile live` reaches composition, then tears the run down and
returns exit `2`, `unavailable` / `PrerequisiteUnavailable` / `HXR029` because
no public profile executor or native report handoff is qualified. It is **not**
a test pass. Full persisted profile assertions, restart/rehydration, report
capture, and P0 acceptance remain in Stages 4–5. The locally packed tools pass
their existing command and contract gate, but executable composition from an
installed package was not live-qualified: the `stage3.14` Module package has no
bundled `g4-host` entries (`qualification-evidence/stage3-public-20260923/package-host-inspection.json`). Package consumer and remote proof remain open; this
record does not claim Stage 4, package live composition, publication, or G-4
approval.

| Final check | Result and retained Builds-owned evidence |
| --- | --- |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet build Hexalith.Builds.slnx -c Debug --no-restore -m:1 -p:NuGetAudit=false -p:MinVerVersionOverride=1.0.0 -v:q` | Exit `0`, zero warnings/errors. |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget dotnet test test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj -c Debug --no-build --no-restore -p:NuGetAudit=false -p:MinVerVersionOverride=1.0.0` | Exit `0`, 169/169; `qualification-evidence/stage3-public-20260923/module-tests-qualified-retry.log`. |
| Focused opt-in `LivePublicCommandTests` | Exit `0`, 1/1, no skip; `qualification-evidence/stage3-public-20260923/public-live-focused.log`. |
| `python3 -m unittest discover -s test/qualification -v` | Exit `0`, 5/5 fail-closed controls after updating the suite expectation to 11; `qualification-evidence/stage3-public-20260923/qualification-controls-final.log`. |
| `python3 Tools/run-g4-stage3-live-qualification.py --evidence-dir _bmad-output/implementation-artifacts/qualification-evidence/stage3-public-20260923-live-final-qualified --dapr-home /tmp/hexalith-g6.c8hQ7T --nuget-packages /tmp/hexalith-g4-stage3-r17/nuget --count 10` | Exit `0`; 10/10 consecutive suites, 110/110 live tests, no failures/skips, 40/40 verified zero-count container/process/workspace/redaction probes. Source SHA-256 `3d91190e283bb5637f9c00d59c33b029bad280c126968669b49d2ec78c145108`; Debug binary SHA-256 `7f59b4c661de1a6b1184feb10bff993d636eedc5ebcae2c43be7e297fe8bccfa`; every before/after fingerprint matched. Per-suite logs, hashes, probes, and `qualification.json` are retained in that directory. |
| `python3 _bmad-output/implementation-artifacts/qualification-evidence/stage3-public-20260923/public-command-probe.py` | Exit `0`, same source/binary hashes. Separate public CLI processes proved ready state plus one live Redis container after `run` exited, exact and repeated `down` cleanup, and nonpassing `test` exit `2`/`HXR029` with no retained run resource; `public-command-qualification.json`. |
| `env -u HEXALITH_G4_LIVE NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage3.14 -RequireControls -RetainPackageDirectory -PackageDirectory /home/administrator/projects/hexalith/projects/references/Hexalith.Builds/_bmad-output/implementation-artifacts/qualification-evidence/stage3-public-20260923/packages-qualified` | Exit `0`; Release build zero warnings/errors, Evidence 68/68, Module 169/169, Integration 1 pass/10 explicit opt-out skips, exact-version restore, source/package parity, positive and negative controls. The skips are excluded from live passes. `package-gate-qualified.log`; 58/58 inventory files hash/size verified in `package-hash-verification-qualified.json` (inventory SHA-256 `939c54c44db9cb6977d0b378d2add521c0f2ee9a1f08f25303dd7c80f83bd037`). |
| `python3 _bmad-output/implementation-artifacts/qualification-evidence/stage3-public-20260923/verify-stage3-public-evidence.py` | Exit `0`; independently rechecked the ten log hashes, source/binary bindings, 40 probes, public command record, and 58 package inventory entries; `stage3-public-verification.json`. |
| From Projects: `python3 references/Hexalith.Builds/Tools/validate-runtime-toolchain-evidence.py --workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json` | Exit `0`, `G6-EVIDENCE-VALID`; `qualification-evidence/stage3-public-20260923/g6-final.log`. |
| `git diff --check` | Exit `0`; `qualification-evidence/stage3-public-20260923/diff-check-final.log` contains only the pre-existing LF-to-CRLF warning for untouched G-6-bound `RuntimePrerequisiteGateTests.cs`. |

Failures remain visible. The first `dotnet test ... --logger` command selected
zero xUnit v3 tests (exit `5`); the direct retry above passed 169/169. The
`stage3.10` package gate failed a public down assertion and was corrected;
`stage3.13` failed its second restore without a surfaced root diagnostic, while
a standalone restore and the subsequent `stage3.14` gate passed. The earlier
`stage3-public-20260923-live-final` and
`stage3-public-20260923-live-current` attempts were interrupted during source
changes and are marked unverified. The first public campaign
`stage3-public-20260923-live-qualified` recorded 11/11 passing tests and zero
probes in run 01 but correctly failed its stale ten-test expectation; run 02
was interrupted and remains unverified. Its `interruption.json` records the
orphaned test workspace cleanup. None of those attempts is counted in the
final ten-suite streak. No G-6-bound file was edited for this cutover.

### 2026-09-24 Stage 4 public persisted profile qualification

**Disposition: Stage 4 is complete for the local public source command; P0
remains in progress through Stages 5–7.** The `full` profile uses exactly two
executable modules, Orders and Inventory. Authenticated public writes produce
real Redis-backed EventStore events and aggregate metadata; the profile reads
and checks the persisted projection, Tenant and aggregate IDs, exact event
type/payload, and sequence for each module. It stops the primary EventStore
host and its Dapr CLI resource, verifies their exit and the primary endpoint's
unavailability, reads state through a second EventStore instance, restarts the
primary, and verifies rehydrated reads. It repeats each command with the same
message ID and verifies no extra event, then advances to sequence 2 with a
distinct command and rejects a sequence 3 event. Both instances read the
result. Anonymous and permission-limited writes, cross-Tenant writes/reads,
and foreign persisted state are denied. The profile fails closed on absent or
stale events/projections/aggregate metadata, wrong sequence or Tenant,
unavailable prerequisites, and cancellation.

The final fresh public-command campaign is
`qualification-evidence/stage4-public-20260924/public-qualification.json`.
Its source bundle SHA-256 is
`02d0b8fb21a279c1a7d77ac748193ed2c20660356ae522ae8f17b0835d05783e`;
all 174 source and 8 binary hashes were unchanged across the campaign.
Separate CLI processes returned:

| Public command/control | Exit and outcome |
| --- | --- |
| `hexalith-module test --manifest test/fixtures/module/executable/hexalith.module-manifest.v1.json --profile full --output json` | `0`, `passed`, run `73fe58376a05161657858760935fd5d1`; persisted assertions above passed. |
| Same command with `--profile live` | `2`, `unavailable` / `HXR029`; unsupported profile remains nonpassing. |
| `full` with Dapr unavailable | `2`, `unavailable` / `HXR011`, before mutation. |
| Active `full` interrupted with SIGINT | `130`, `cancelled` / `HXC130`; bounded cleanup completed. |

Every campaign run reported zero retained run containers, AppHosts, run-state
files, and workspaces. `verification-final.json` independently verified the
report hash `5df9a363516eec2c1a8bc26403543508a63c522435b87f454d0fe40ac1e9dd51`,
174 source hashes, 8 binary hashes, 8 log hashes, and 36 G-6-bound hashes.
The G-6-bound-file audit matched 36/36 and no bound file was edited for Stage 4.

| Additional check | Result and retained evidence |
| --- | --- |
| Focused `PersistedProfileStateTests` | 2/2 passed, zero fail/skip; `focused-state-tests-final.log`. |
| `NUGET_PACKAGES=/tmp/hexalith-g4-stage3-r17/nuget pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage4.4 -RequireControls` | Exit `0`, Release build zero warnings/errors; Evidence 68/68, Module 172/172, Integration 1 pass/10 explicit opt-out skips, exact-version restore and source/package parity; `package-gate-stage4.4.log`. These integration tests and package contracts are not counted as public persisted-profile qualification. |
| G-6 validator from Projects root with `--workspace . --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain/packet.json` | Exit `0`, `G6-EVIDENCE-VALID`; `g6-final.log`. |
| `git diff --check` | Exit `0`; `diff-check-final.log` retains only the pre-existing LF-to-CRLF warning for untouched G-6-bound `RuntimePrerequisiteGateTests.cs`. |

The Builds-owned evidence directory retains failed and superseded public
attempts, including missing Redis client assembly, peer access before the
correct sidecar resource control, early cancellation cleanup, and a campaign
that rejected binary drift. The final report supersedes those attempts; they
are not counted as passes. Installed-package live composition, native reports,
and deterministic packaged acceptance evidence remain Stage 5 work. No package
was published, and no P0 owner acceptance is claimed.

### 2026-09-24 Stage 5 packaged native-report and acceptance-validator qualification

**Disposition: Stage 5 is complete in the local packaged scope; P0 remains in
progress for Stages 6–7.** Its local evidence was produced through the installed
`0.0.0-stage5.6` tools only, and no package was published. No consumer pin
changed, nothing was staged or committed, and no owner or Test Architect
acceptance is claimed. The working tree is dirty at base revision
`754d2b4b6615c5004606ba837dd9c925c57e8d14`. Every run therefore records
`repositoryDirtyMarker: dirty`, and none of this evidence can satisfy the
acceptance record, which requires a clean revision.

**Resumption checks.** The interrupted second live run (exit 58, xUnit
`OperationCanceledException`, 18:41:28–18:42:04Z,
`native/packaged-persisted-bound.{log,trx}`) is retained as a cancelled attempt,
not a product failure. It left stale run state `739bd2a186dfc3272060515161b8eee7`
(`Ready`, AppHost pid 2949494 absent after the 20:49 reboot) and its workspace,
but no labelled container. Packaged `hexalith-module down --run-id
739bd2a186dfc3272060515161b8eee7` (stage5.5) returned `0`/`HXI001` and removed
both (`stale-run-cleanup-output.json`). `/tmp` had been cleared by the reboot,
which removed the previous isolated Dapr home and package consumer. A new
isolated home is at
`~/.local/state/hexalith-qualification/g4-dapr-home-1.18.2`: Dapr CLI 1.18.0
from the official release archive (SHA-256
`2a94739e0aa101289d88418225319562bc6800db273b3d9cf819a0efd1ea1bfe`), then
`DAPR_RUNTIME_PATH=<home> dapr init --slim --runtime-version 1.18.2`. The global
Dapr install and containers were untouched, and binary hashes are in
`stage5-verification.json`.

**Owner decision (2026-09-24, Jerome).** Native reports bind through a
runner-owned test executor. Because `schemas/hexalith.module-manifest.v1.json`
and several Module test files are hash-bound by the accepted G-6 source state,
the declaration lives in the runner-owned `hexalith.g4-persisted-profile.v1`
fixture, not the manifest. `nativeTests` holds a repository-relative `project`
and a `platform` of `vstest` or `mtp`. The G-6 audit matched all 36 bound files
after EOL normalization (`g6-bound-audit.json`).

**Implementation.**

- `Runtime/NativeTestExecutor.cs` runs after the persisted assertions pass and
  before teardown. It calls `dotnet test` from the test project directory, so
  the consumer's `global.json` still selects the platform: VSTest uses
  `--logger trx;LogFileName=native.trx`, and MTP uses `--report-xunit-trx`.
- It passes the handoff contract in `Runtime/NativeTestHandoff.cs`: run ID,
  EventStore/peer/UI URLs, tenant, resource namespace, domains, and a run-scoped
  token. The signing key is never handed off.
- Failure rules:
  - a nonzero native exit or timeout is `HXT007`;
  - the loader keeps `HXT001`–`HXT006`;
  - an unstartable process is `HXR030`;
  - a report containing a handoff secret, or a credential in any individual XML
    value, is `HXT008` (exit 6) and is not retained.
- A passing report is written beside `--evidence` as
  `<evidence>.<platform>.trx`. Its counts become `testCounts` and its
  upper-case SHA-256 is added to `artifactHashes`.
- The executable fixture gains `full` (VSTest) and a new `full-mtp` profile. It
  also gains product-side tests in `P0Fixture.NativeTests` (MTP) and
  `P0Fixture.NativeTests.VsTest`. The VSTest variant opts out of the xUnit v3
  MTP application mode and has a directory-local `global.json` without the
  repository MTP runner selection.
- **Contract correction:** a successful persisted profile previously emitted
  `status`/`finalStatus` `passed`. The evidence schema, artifact validator, and
  readiness `HXE149` accept only `completed` as success, so every Stage 4 and
  earlier Stage 5 persisted artifact failed canonical artifact validation. The
  profile and native step now emit `completed`. Earlier
  `native/packaged-profile-*.json` and `packaged-host*` artifacts are superseded.
- `G4P0AcceptanceValidator.cs` is finished, and `validate` dispatches `.json`
  records to it. It fails closed with these rules:
  - `HXE200`: unreadable input, duplicate keys, or secrets;
  - `HXE201`: schema or field set;
  - `HXE202`: not `accepted`, revision, or `SupportedPlatformPins`;
  - `HXE203`: manifest hash;
  - `HXE204`: exactly two lockstep packages, feed by version kind, `.nupkg` and
    `.snupkg` hashes;
  - `HXE205`: exact `dotnet tool run <evidence command>`, status, exit code,
    manifest, clean revision and tool version, and exactly `persisted-vstest`,
    `persisted-mtp`, `prerequisite-unavailable`, and `cancelled`;
  - `HXE206`: native report hash equals the evidence `artifactHashes` entry and
    counts;
  - `HXE207`: completed cleanup and rollback;
  - `HXE208`: three dated named-role approvals of the exact revision.
- The synthetic corpus `test/fixtures/evidence/acceptance/` holds one positive
  record (`HXI210`), 19 negatives covering every rule, and bound artifacts. A
  README labels it as never being acceptance evidence, and a narrow
  `.gitignore` exception makes its placeholder `.nupkg`/`.snupkg` files
  trackable.
- The package gate runs the corpus through the source and installed commands as
  blocking controls without inventory entries, so the publisher's
  evidence-name contract is unchanged. The gate's help contract and its
  self-test now expect the updated `validate` description.

| Command/check | Result and retained evidence |
| --- | --- |
| `dotnet build Hexalith.Builds.slnx --configuration Release -m:1` (isolated `NUGET_PACKAGES`, `CI=true`) | Exit `0`, 0 warnings/errors after red→green analyzer fixes. |
| Module tests, built assembly direct run | 198/198 passed: 172 existing plus 26 new in `NativeTestExecutorTests`, `PersistedProfileNativeTestsTests`. |
| Evidence tests, built assembly direct run | 90/90 passed: 68 existing plus 22 new in `G4P0AcceptanceValidatorTests`. |
| `pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage5.6 -RequireControls -PackageDirectory <E>/packages6 -RetainPackageDirectory` | First attempt exit `1` on the stale help contract (`package-gate6-failed-help-contract.log`). I discarded that attempt's package directory instead of retaining it. Rerun exit `0` (`package-gate6.log` `0210deae…`): Evidence 90/90, Module 198/198, Integration 1 passed and 12 explicit live opt-out skips, and 40 acceptance invocations (20 cases × source and package). Inventory `packages6/g4-tool-package-inventory.json` `2d8bb2b2…`. |
| Packages | `Hexalith.Builds.Module.Cli.0.0.0-stage5.6.nupkg` `e0d4f12d…653c`, `.snupkg` `b66fd3b8…b72d`; `Hexalith.Builds.Evidence.Cli.0.0.0-stage5.6.nupkg` `7ed8c0b9…1444`, `.snupkg` `ded94da8…1570`. Restored exactly into a clean consumer (`restore6.log`, `consumer-dotnet-tools.stage5.6.json`). |
| `run-packaged-qualification.py <consumer> <E>/packages6` with `G4_CAMPAIGN=live` | Exit `0`, `live/packaged-qualification.json` `1eea0eb9…`, `status: passed`, no failures, source bundle `7ed3cf1e…4066` unchanged. |
| `dotnet tool run hexalith-module -- test --manifest test/fixtures/module/executable/hexalith.module-manifest.v1.json --profile full --evidence <E>/live/persisted-vstest.json --output json` | `0`/`completed`, run `91a74fcf…`. 12 persisted assertions, 2 sequences, `testCounts` 2/2. VSTest report `live/persisted-vstest.vstest.trx` `0af33e78…06c3`, bound in `artifactHashes`. |
| Same with `--profile full-mtp` | `0`/`completed`, run `3a1c95fa…`. 12 assertions, 2 sequences, 2/2. MTP report `live/persisted-mtp.mtp.trx` `f31be5d0…9948`, bound. |
| Same with `--profile live` | `2`/`unavailable`/`HXR029`. |
| `full` with `HEXALITH_DAPR_HOME` absent | `2`/`unavailable`/`HXR011` before mutation. |
| `full` with SIGINT to the process group at 40 s, while running | `130`/`cancelled`/`HXC130`. |
| `down --run-id 91a74fcf…` after that run completed | `0`/`completed`/`HXI001` (idempotent). |
| Retained resources after every run | No labelled container, run-state file, workspace, or Aspire AppHost remained. |
| Installed `hexalith-evidence validate` on the 20 corpus records | 20/20 matched exit and ordered rule IDs. |
| Installed `validate live/candidate-acceptance.json` (real Stage 5 runs, `status: candidate`, unpublished feed, no approvals, rollback not run) | `6`/`HXE202`: the real local evidence fails closed. |
| Opt-in native xUnit live lane: `HEXALITH_G4_LIVE=1 … Hexalith.Builds.Tooling.IntegrationTests -class …PackagedPersistedProfileTests -trx <E>/native-lane/packaged-persisted-lane.trx` | 2/2 passed (`full`/VSTest, `full-mtp`/MTP), 118 s. Lane TRX `527d8d7b…`, outcome `Completed`; per-run reports and evidence are in `native-lane/`. |
| `git diff --check` | Exit `2`, only for the three added `.gitignore` lines. They follow that file's tracked CRLF convention (`diff-check.log`). |
| `stage5-verification.json` | SHA-256 of 87 Stage 5 artifacts plus the isolated Dapr binaries; file hash `043c8504…ce75`. |

**Residuals:**

- Retained TRX files carry test identities, timings, the adapter banner, and
  the host computer name (`computerName`), but no credential, tenant data, or
  payload.
- `--filter` still makes persisted profiles unsupported (`HXR029`); filter
  pass-through to native platforms is not qualified.
- Browser, CLI, and MCP profile classes have no executor.
- Only a passing native report is retained. A failing report is represented by
  its rule ID alone.
- Before Stage 7, the acceptance record needs a clean exact revision, published
  packages, a rollback drill, and named approvals.
- Earlier `packages`–`packages5`, `packaged-host*`, `native/*`, and build logs
  in this directory are retained as superseded attempts.

### 2026-09-25 Stage 5 code review, remediation, and `0.0.0-stage5.7` requalification

**Disposition: the Stage 5 review is remediated and requalified in the local
packaged scope. P0 remains in progress for Stages 6–7.** The tree is still dirty
at base `754d2b4b6615c5004606ba837dd9c925c57e8d14`. Nothing was staged,
committed, pushed, branched, or published. No consumer pin changed, no
dependency was updated, and no owner or Test Architect acceptance is claimed.
P0, the dependent P2, and Story 6.1 remain open, and Stage 6 has not started.

**Preflight.**
- `main` tracks `origin/main` with nothing ahead or behind.
- Only Stage 5 files were dirty.
- An independent re-hash of the Projects
  `g-6-runtime-toolchain/source-state.json` Builds entries matched 36/36 after
  EOL normalization (30/36 byte-identical). No bound file is dirty, before or
  after remediation (`stage5.7/g6-bound-audit.json` `21722b3c…`).

**Review.**
- `bmad-code-review` ran four layers: blind, edge-case, verification-gap, and
  acceptance audit.
- The scope was the Stage 5 diff only: 97 file sections, CR-at-EOL churn
  ignored, `qualification-evidence/` and this story excluded.
- 58 raw findings were triaged into 6 decisions, 15 patches, 3 deferrals, and
  16 rejections, plus SR-N1 found during triage. The full dispositions are in
  "Review Findings — Stage 5".
- Jerome accepted the recommendations: D1 a, D2 a, D3 a, D4 a, D5 b′, D6 c.

**Remediation (all patches applied).**
- **D1 (packaged hosts).** `Runtime/PackagedHostProject.cs` and
  `RunTopology.cs`: each packaged host resource builds a private shim copy in
  `<workspace>/hosts/<resource>/`. The copy uses `PackagedHost.props` for the
  absolute host directory. The shim no longer imports `Directory.Build.*` or
  `Directory.Packages.props`. A partial package layout throws, and a
  source-only layout still uses `Projects.*`.
- **D2 (report redaction).** `Runtime/NativeTestReportRedactor.cs`: after the
  raw secret check, the runner removes `runUser`, `computerName`,
  `runDeploymentRoot`, the run name, `Output`, `RunInfos`, `ResultFiles`, and
  `CollectorDataEntries`, and reduces `codeBase`/`storage` to file names. It
  then re-scans the report and hashes and retains the redacted bytes. The
  retained-report hash is computed from the written bytes (SR-P9).
- **SR-N1.** A cancelled invocation now records its manifest, so
  `invocation.profile` and the fixture are preserved.
- **SR-P7.** `ModuleCommandExecutionService.CombineTestResults` is a pure,
  unit-tested combination of the profile, native, and cleanup results.
- **SR-P15.** `artifactHashes` is listed in `volatileFields` whenever a native
  report is bound.
- **Shared assertion labels.** `Runtime/PersistedProfileEvidence.cs` holds the
  labels that both the runner and the validator use.
- **Validator** (`Evidence/G4P0AcceptanceValidator.cs`):
  - platform-bound persisted runs: declared `nativeTests.platform` plus the
    `<evidence>.<platform>.trx` path (SR-P1);
  - the exact 12 assertions and sequences (SR-P2);
  - nuspec `id`/`version` for each `.nupkg`/`.snupkg` (SR-P3);
  - approvals dated no earlier than the last run's completion and not in the
    future (SR-P4);
  - control runs must be native-test-profile `test` runs, with a prerequisite
    rule other than `HXR029` or with `HXC130` (D4);
  - cleanup must be clean `down` evidence for a cited persisted run; rollback
    stays attested until Stage 7 (D3);
  - run IDs must be distinct, and bound evidence reads are capped at 4 MiB.
- **Corpus.** It was regenerated deterministically: 1 positive and 36
  negatives, 17 of them new (SR-P5). The generator is kept outside the
  repository at
  `<scratchpad>/gen_acceptance_corpus.py`. The previous corpus is backed up in
  the scratchpad. `.gitattributes` pins `bound/**` with `-text`, and the
  `.gitignore` exception now covers `bound/**` (SR-P6).
- **Gate.**
  - `test-g4-tool-package-contracts.ps1` asserts the seven packaged host
    entries (SR-P8).
  - `Hexalith.Builds.Module.Cli.csproj` fails pack when the host Release
    outputs are missing.
  - `test-g4-tool-package-artifact-validator.ps1` adds `eol=crlf` and `-text`
    cases.
  - `test-g4-tool-package-contract-gate.ps1`, which CI already runs, now runs
    that self-test (SR-W1). The G-6-bound `ci.yml` was not edited.
- **Docs and tests.**
  - `Tools/README.md` gains the acceptance field/rule table (D5 b′), the
    xUnit-v3-only `mtp` note (SR-P10), redaction, and packaged-host builds.
  - New tests: `TestResultCombinationTests`, `NativeTestReportRedactorTests`,
    `PackagedHostProjectTests`, a loader positive control (SR-P14), an HXT002
    case, peer and domain handoff assertions (SR-P11), an SDK-pin equality test
    (SR-P13), and a positive `WriteArtifactAsync` test.
  - The live-lane outer timeout is now 30 min (SR-P12).
  - Red→green analyzer fixes: IDE0046, SA1118, RCS1146, SA1507, IDE0370,
    SA1202, RCS1238, S3358, RCS1181, CA1849/S6966/VSTHRD103, and SA1515.

| Command/check (`<E>` = `qualification-evidence/stage5-local-20260924`, isolated `NUGET_PACKAGES=~/.local/state/hexalith-qualification/nuget-stage5.7`, `CI=true`) | Result |
| --- | --- |
| `dotnet build Hexalith.Builds.slnx --configuration Release -m:1` | Exit `0`, 0 warnings, 0 errors (`stage5.7/build.log`, ignored). |
| Evidence tests, built assembly direct run | 107/107 (90 + 17 new corpus negatives). |
| Module tests, built assembly direct run | 214/214 (198 + 16 new). |
| Installed-CLI walk of the 37 corpus records | Every record fails on its intended field. |
| `pwsh -NoProfile -File Tools/test-g4-tool-package-contract-gate.ps1` | Exit `0`. The artifact-validator self-test passed 30 scenarios. |
| `pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage5.7 -RequireControls -PackageDirectory <E>/packages7 -RetainPackageDirectory` | Exit `0`. Evidence 107/107, Module 214/214, Integration 1 passed and 12 live opt-out skips. The 37 acceptance cases ran through source and package as blocking controls. The packaged host entries were asserted. Inventory `455a2d89…`, log `76dfa114…`. |
| Packages | `Module.Cli.0.0.0-stage5.7.nupkg` `654dc5f2…`, `.snupkg` `68103da3…`; `Evidence.Cli.0.0.0-stage5.7.nupkg` `e2e76e79…`, `.snupkg` `5259f187…`. Restored into a fresh consumer at `~/.local/state/hexalith-qualification/consumer-stage5.7` (`stage5.7/consumer-dotnet-tools.stage5.7.json`). |
| `G4_CAMPAIGN=live-stage5.7 … python3 run-packaged-qualification-stage5.7.py <consumer> packages7` | Exit `0`, `status: passed`, no failures (`live-stage5.7/packaged-qualification.json` `ada14b57…`, source bundle `679dc471…` unchanged). **Persisted runs:** `full`/VSTest run `0608dcbc…` and `full-mtp`/MTP run `955d41e7…` each passed 2/2 with 12 assertions and 2 sequences. Their redacted reports are `015833c6…` and `b8615b97…`, and `artifactHashes` is volatile. **Controls:** `live` returned `2`/`HXR029`, the prerequisite control `2`/`HXR011`, and the cancelled run `130`/`HXC130` with profile `full` recorded (SR-N1); the idempotent `down` returned `0`. **Validation:** 37/37 corpus cases matched through the installed tool, and the candidate record failed closed with `6`/`HXE202`. **Residue:** no build output in the tool store, and no retained containers, run state, workspaces, or runner AppHosts. |
| Packaged native xUnit live lane (`native-lane-stage5.7/command.txt`) | 2/2 passed in 128.4 s. Lane TRX `88786bb2…`; retained per-run reports `86acd4fb…` (VSTest) and `0a9e3ed1…` (MTP) are redacted. |
| Real-evidence validator probe | Copies of the `live-stage5.7` runs were relabeled `clean` in the ignored `artifacts/stage5.7-probe/`, and the candidate record was set to `accepted`. The installed validator returned `HXI210`. Along the way, P4 correctly rejected an approval dated before the cancelled run's completion. The probe also showed that a record `command` must be `dotnet tool run ` plus the canonical evidence command; `Tools/README.md` now documents this. The probe was deleted afterwards. |
| Verification manifest | `stage5.7/stage5.7-verification.json` `79c1c4bb…`: SHA-256 of 177 stage5.7 artifacts. The isolated Dapr binaries were re-hashed and are unchanged. |
| `git diff --check HEAD` | Exit `2`, only for the three `.gitignore` exception lines, which follow the file's tracked CRLF convention. |
| Commit messages | All 7 proposed messages pass `@commitlint/cli@21.2.2` with `@commitlint/config-conventional@21.2.2` and the repository `commitlint.config.mjs`, installed in an isolated scratch directory. A negative control is rejected. The repository's installed `node_modules` has CLI `21.2.1` while `package-lock.json` pins `21.2.2`. The 7 messages also pass `21.2.1`. `node_modules` was not updated. |

**Blockers and notes:**
- The first campaign scan found an unrelated developer AppHost
  (`Hexalith.Tenants.AppHost`) running. It was left untouched. The stage5.7
  campaign counts only `Hexalith.Builds.Module.AppHost` instances and records
  the others (`otherAppHostPaths`).
- The xUnit harness's own lane report (`native-lane-stage5.7/packaged-persisted-lane.trx`)
  contains `runUser` and the host name. The runner does not produce this file.
- `live/` and `native-lane/` (stage5.6) hold raw, unredacted reports.

**Evidence decision (2026-09-25, Jerome: option E3).**
- **Why the move was necessary.** The runner (`git status --porcelain --untracked-files=all`) and the gate (`Get-SourceTreeState`) mark any untracked, non-ignored file as dirty. Leaving superseded evidence uncommitted in the tree would therefore make every Stage 6 run dirty.
- **Committed:** `stage5.7/`, `live-stage5.7/`, `native-lane-stage5.7/`, `packages7/` (inventory and qualification JSON), `run-packaged-qualification-stage5.7.py`, and `stage5-verification.json`, which is the hash index of the 87 stage5.6 artifacts and contains no host data. `*.log`, `*.nupkg`, and `*.snupkg` remain ignored.
- **Moved, not deleted,** to `~/.local/state/hexalith-qualification/stage5-superseded-20260924/` with the same relative paths:
  - `live/`, `native-lane/`, `native/`, and `packages`–`packages6`;
  - `packaged-host*`, `stale-run-cleanup-*`, and the stage5.6 root logs;
  - `g6-bound-audit.json` and `run-packaged-qualification.py` (stage5.6);
  - the xUnit harness report `native-lane-stage5.7/packaged-persisted-lane.trx`, which contains the host name and `runUser`. Its hash `88786bb2…` stays in `stage5.7/stage5.7-verification.json`.
- **Move verification.** 257 files (107,350,958 bytes) were hashed before and after the move, with 0 mismatches. The manifest `stage5.7/superseded-archive-manifest.json` (`99859121…`) is committed, and a copy is kept in the archive.

**Commits (2026-09-25, Jerome: option B on local `main`, no push).**
- **Commit 0** used an index-only change: LF blobs of the `HEAD` content went in through `git hash-object -w` and `git update-index --cacheinfo`. The diff is 452/452 lines and empty once CR at end of line is ignored. The working-tree bytes are unchanged, so no build check was needed.
- **Commits 1–4:** each tree was first built in a temporary index and a detached worktree with `<scratchpad>/verify-commit.sh`. The real commit tree was then confirmed identical to the verified tree.
- **Messages:** all pass `@commitlint/cli@21.2.2`, installed in isolation with the repository config, and the repository's own `21.2.1`. No commit-msg hook is installed locally, and none was bypassed.

| Commit | Subject | Pre-commit verification (Release build; Module; Evidence) |
| --- | --- | --- |
| `05ed57d` | `style: normalize line endings of three evidence sources` | Content-identical: 3 files, EOL only |
| `c7fb9fc` | `fix(runner): report completed for passing persisted profiles` | 1 file; 0 warnings/errors; 172/172; 68/68 |
| `f34552b` | `feat(runner): bind native test reports into persisted profile evidence` | 23 files; 0 warnings/errors; 211/211; 68/68 |
| `3c8a520` | `feat(runner): ship the composition hosts inside the module tool` | 10 files; 0 warnings/errors; 214/214; 68/68 |
| `aa2ef5c` | `feat(evidence): validate G-4 P0 acceptance records and gate them` | 123 files; 0 warnings/errors; 214/214; 107/107. Previously planned as two commits (validator, then gate) and merged, so no commit has the new `validate` help text without the gate expecting it. The corpus `-text` blobs are committed as raw bytes. |
| next | `docs(g4): record Stage 5 packaged qualification and review` | This story, `deferred-work.md`, and the committed evidence set |

**Stage 6 note (not started).** Evidence written inside the repository marks every later run dirty. Stage 6 must write its run evidence to an ignored or external location and commit it afterwards.

### 2026-09-25 Stage 6 readiness restoration at EventStore `3.108.1` and `0.0.0-stage5.8` requalification

**Disposition: `main` is repaired and Stage 5 is requalified at EventStore
`3.108.1` in the local packaged scope. Stage 6 is not ready and has not started.**
Readiness still needs three things: a green `origin/main` CI after Jerome pushes,
a P1R tuple accepted at `3.108.1`, and a G-6 refresh that the owner accepts.
Nothing was pushed or published. No consumer pin changed, no dependency was
updated, and no owner acceptance is claimed. P0, the dependent P2, and Story 6.1
remain open.

**Preflight (verified at `origin/main` = `e9543dc`).**

| Fact | Result |
| --- | --- |
| `e9543dc` merges Stage 5 (`05ed57d..248b01d`) with remote work up to `23f4798` | Confirmed. The runner pin changed in `349d660`, a 65-file commit that does not touch `Props`. The catalog `HexalithEventStoreVersion` moved to `3.108.1` through Dependabot `203cc49`, alongside `ceebf8b` (`CommunityToolkit.Aspire.Hosting.Dapr` beta.757→beta.767), `85664e3` (`StackExchange.Redis` 3.3.1), and `40fb0bb` (`JsonSchema.Net` 9.4.0). |
| CI `36115506731` | Confirmed: 7 audit errors. Every later step was skipped. |
| Evidence tests at `e9543dc` | Confirmed: 31/107 failed (HXE202 and dependent binding rules). The corpus declared `3.106.0`. |
| G-6 (Projects `g-6-runtime-toolchain/source-state.json`, 36 Builds bindings) | Confirmed 14/36 after EOL normalization. 19 files drift from the pin change, 2 from Dependabot action-SHA bumps in `domain-ci.yml`/`domain-release.yml`, and 1 from `Props`. |
| Accepted P1R tuple | EventStore `3.106.0` / `76051c70…`. The tuple is hard-coded in Projects `tools/planning/validate_production_authority.py:30-33` and `sprint-status.yaml`. |
| Release `36111365114` | Confirmed failure at the publish guard. **Contrary to the brief, semantic-release created tag `v4.27.5` (→ `23f4798`) before the guard failed.** No GitHub Release exists, and nuget.org has no `4.27.5` of either tool (latest `4.27.4`). |
| Projects `references/Hexalith.Builds` pointer | **Contrary to the brief, it is not uncommitted.** Projects `aa3f00b` already records `e9543dc` and is pushed. |

**Decision (2026-09-25, Jerome): option (a).** Adopt EventStore `3.108.1`.
Keep `349d660` and the catalog bumps. Regenerate the audit and corpus,
requalify Stage 5, then revalidate P1R and refresh G-6 in Projects.

**Root causes found during the local CI run.**
- **Release publish guard.** `754d2b4` changed `.gitattributes` to
  `*.cs text eol=crlf` without renormalizing 54 blobs stored with CRLF. Every
  fresh checkout reports them modified. `Get-SourceTreeState` therefore returns
  dirty, the gate writes `releaseEligible: false`, and
  `publish-g4-tool-packages.ps1` throws "incomplete or bypassed". The fix
  changes only the index: the blobs are stored as LF, and the diff with CR at
  end of line ignored is empty.
- **Consumer package authority (CI step not reached on GitHub).** The Stage 5
  packaged-host shims `pack/projects/{EventStore,Ui}/Host.csproj` deliberately
  import no catalog (SR-D1). Tracked as `*.csproj`, they failed
  `validate-consumer-package-authority.ps1` with 606 errors.
  - They are now `Host.csproj.template`.
  - NuGet will not rename a file across extensions (a first attempt packed
    `Host.csproj/Host.csproj.template`, and the gate caught it). So
    `PackPackagedHostProjects`, a `TargetsForTfmSpecificContentInPackage`
    hook, copies them to `obj/…/Host.csproj` and packs those copies.
  - The package entries are unchanged, and each packed `Host.csproj` has the
    same bytes as its template.
- **`dotnet test -m:1`.** Under Microsoft.Testing.Platform, the argument is
  forwarded to the test application, which then reports "Zero tests ran"
  (exit 5). The local CI replay therefore runs the test steps exactly as
  `ci.yml` does. Only restore and build use `-m:1`.

**Repairs.**
- **Corpus.** `~/.local/state/hexalith-qualification/tools/gen_acceptance_corpus.py`
  first reproduced the committed corpus byte for byte at `3.106.0`. After its
  PINS were changed to `3.108.1`, it rewrote 52 files: 37 `eventStoreVersion`
  values and the hashes that depend on them. The pre-edit generator is backed
  up in the session scratchpad.
- **Audit.** From `349c3cb`, the repository generator ran:
  `pwsh -NoProfile -Command "& ./Tools/audit-central-package-versions.ps1 -PriorAuditPath ./Tools/package-version-audit.json -ChangedFamily 'package:microsoft.net.test.sdk','package:shouldly','package:system.commandline','xunit'"`.
  - It refreshed 4 families, preserved 142, and wrote 300 packages.
  - No audited or selected version changed.
  - `validate-package-version-audit.ps1` passed.
  - (`pwsh -File` passes a comma list as one string, so the generator's
    first invocation was rejected.)

**Proposed commits.** They were built in a scratch clone
(`~/.local/state/hexalith-qualification/builds-stage5.8`, `--shared`), each with
fixed Jerome author and committer metadata. Each tree was verified with
`verify-commit.sh`: Release build, then the built Module and Evidence test
assemblies. Every message passes `@commitlint/cli` 21.2.2 and 21.2.3 (the
current `package-lock.json` pin), installed in isolation with the repository
config, and a negative control is rejected. The repository's stale
`node_modules` (21.2.1) was not touched.

| Commit | Subject | Verification |
| --- | --- | --- |
| `3c5122c` | `test(evidence): regenerate acceptance corpus for EventStore 3.108.1` | 52 files; 0/0; Module 214/214; Evidence 107/107 |
| `9458e35` | `style: renormalize C# blobs to their eol attribute` | 54 files, CR-only; 0/0; 214/214; 107/107 |
| `349c3cb` | `fix(runner): keep packaged host shims out of project validation` | 5 files; 0/0; 214/214; 107/107 |
| `90228a7` | `build(audit): refresh consumer evidence for G-4 runner projects` | 1 file; 0/0; 214/214; 107/107 |
| next | `docs(g4): record Stage 5 requalification at EventStore 3.108.1` | This record and the retained `stage5.8` evidence |

The working-tree content of every path in the chain matches the chain's blobs
(110/110 by `git hash-object --path`). Superseded candidates from the reorder
and the shim-fix iteration remain unreferenced objects in the scratch clone only.

**Local CI replay of `.github/workflows/ci.yml` at `90228a7`.**
- **Setup.** A fresh detached worktree, with an isolated `NUGET_PACKAGES`,
  `CI=true`, and the sandbox disabled. Toolchain: Node `24.21.0` (a
  checksum-verified tarball in the scratchpad, matching the CI pin), .NET
  `10.0.401`, PowerShell 7.6.2, Python 3.14.4.
- **Result.** All 33 steps of the `build-and-test` and `python-tests` jobs
  exited `0`. The worktree had 0 dirty lines before and after.
  - Audit gate: `Validate package version audit` 0.
  - Consumer authority: 17 projects.
  - Tests: Module, Evidence, and Integration all passed.
  - Package qualification: `0.0.0-ci.9004`.
- **Earlier replays.** At candidate `1057a3a` (before the shim fix), step 07
  failed with 606 errors. At `40acdb6` (the first shim attempt), step 27
  failed on the missing packaged `Host.csproj`.
- **Not reproduced locally.** CodeQL has no local CLI. On GitHub it succeeded
  at `e9543dc`. Dependency review runs on pull requests only.

**Stage 5.8 requalification (installed tools, clean tree at `90228a7`).**
- **Where evidence was written.** The runner resolves `--evidence` inside the
  repository root. Runs therefore used the scratch clone, with the evidence
  paths listed in that clone's `.git/info/exclude`. Every run recorded
  `repositoryDirtyMarker: clean`. Only the retained set was copied into this
  repository.

| Command/check (`NUGET_PACKAGES=~/.local/state/hexalith-qualification/nuget-stage5.8`, `CI=true`) | Result |
| --- | --- |
| `dotnet build Hexalith.Builds.slnx --configuration Release -m:1` | `0`; 0 warnings, 0 errors. The EventStoreHost assets resolve `Hexalith.EventStore.*` `3.108.1`, and the AppHost resolves `Hexalith.EventStore.Aspire` `3.108.1` and `CommunityToolkit.Aspire.Hosting.Dapr` `13.5.1-beta.767`. |
| Built Module and Evidence test assemblies | 214/214; 107/107 |
| `pwsh -NoProfile -File Tools/test-g4-tool-package-contract-gate.ps1` | `0`; artifact-validator self-test 32 scenarios |
| `pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-stage5.8 -RequireControls -PackageDirectory <E>/packages8 -RetainPackageDirectory` | `0`. Integration 13. Inventory `8c8921ec…`: source validation executed and passed, controls passed, `sourceTree.clean: true` at `90228a7`, **`releaseEligible: true`**, no ineligibility reasons. Packages: `Module.Cli` `50396ac5…` / `.snupkg` `a1507193…`; `Evidence.Cli` `38b592cd…` / `.snupkg` `33c43bf1…`. |
| Fresh consumer `~/.local/state/hexalith-qualification/consumer-stage5.8`, `dotnet tool restore` | `0`; `hexalith-module --version` = `0.0.0-stage5.8+90228a7…`; the store holds `g4-host/projects/{EventStore,Ui}/Host.csproj` |
| `G4_CAMPAIGN=live-stage5.8 … HEXALITH_DAPR_HOME=~/.local/state/hexalith-qualification/g4-dapr-home-1.18.2 python3 run-packaged-qualification-stage5.8.py <consumer> packages8` | `0`, `status: passed`, no failures, `repositoryDirty: false`, source unchanged (bundle `bb66cd8f…`), report `626e3f7b…`. **Persisted runs:** `full`/VSTest `09b0604c…` and `full-mtp`/MTP `a7a673fe…`, each 2/2 with 12 assertions and 2 sequences; redacted reports `706e5034…` and `0a8fb0ab…`; evidence platform `eventStoreVersion` `3.108.1`. **Controls:** `live` `2`/`HXR029`; missing Dapr home `2`/`HXR011`; cancelled `130`/`HXC130` with profile `full`; idempotent `down` `0`. **Validation:** 37/37 corpus cases matched through the installed tool, and the candidate record failed closed with `6`/`HXE202`. **Residue:** none: no tool-store build output, containers, run state, workspaces, or runner AppHosts. No developer AppHost was running this time. |
| Packaged native xUnit live lane (`native-lane-stage5.8/command.txt`) | 2/2 passed in 128.2 s. The retained per-run reports `39a19cf4…` (VSTest) and `8ab0d9b9…` (MTP) are redacted, and their evidence is `clean`/`completed` at `3.108.1`. The harness lane TRX (`79efbdbd…`, containing `runUser` and the host name) was written outside the repository. |
| Verification manifest `stage5.8/stage5.8-verification.json` | `1b0e21d4…`: SHA-256 of 174 artifacts plus the external lane TRX. The isolated Dapr binaries are unchanged since 5.7. |

**Campaign script.** `run-packaged-qualification-stage5.8.py` is a copy of the
5.7 script with three changes: `VERSION`, the candidate `eventStoreVersion`
`3.108.1`, and a `repositoryDirty` value measured by
`git status --porcelain --untracked-files=all` instead of hard-coded. As in
5.7, `packaged-qualification.json` records the absolute
`toolStoreProjectsDirectory`.

**Retained evidence.** The committed set is 120 files: `stage5.8/`,
`live-stage5.8/`, `native-lane-stage5.8/`, `packages8/` (inventory and
qualification JSON), and the campaign script. The 55 ignored files (`*.log`,
`*.nupkg`, `*.snupkg`) stay in the scratch clone and are hash-bound by the
verification manifest.

**Remaining before "Stage 6 ready" (not done here):**
- **Push.** Jerome fast-forwards `main` to the verified chain and pushes. Then
  `origin/main` CI must be green.
- **P1R at `3.108.1`.** This means:
  - revalidation of EventStore `3.108.1` / `v3.108.1` /
    `b15ad59abca82d5980ef92a510c2379e05f4d46f` with the pushed Builds
    revision;
  - updates to the Projects guard constants, the qualification contract, and
    the sprint index;
  - four named role decisions.
  - The accepted tuple (frontmatter `accepted_p1r_baseline`) stays at
    `3.106.0` until then.
- **G-6 re-execution and owner acceptance.** 22 Builds bindings drifted.
  The previous refresh required a temporary Dapr control-plane swap, which
  needs an owner decision.
- **Outside this repair.**
  - Tag `v4.27.5` exists without a release. The next semantic-release version
    will be `4.27.6` or later.
  - The Projects pointer must follow the pushed Builds `main`.

### Completion Notes List

- Ultimate context engine analysis completed - comprehensive developer guide created.
- Jerome authorized Hexalith.Builds to own both P0 tools; authorized baseline `edbaeae` replaced the stale earlier observation `01e48ee`. The delivery revision remains unaccepted and the current worktree is not represented as a published package.
- Package IDs, command names, schema IDs, semantic-release channels, expected initial stable version, greenfield rollback, matrix-status disposition, file impact, negative controls, and P4 handoff are explicit.
- Established the SDK/MSBuild/package spine at the authorized baseline with exact tool identities, a non-packable shared core, central package management, warning-as-error analysis, and nonzero contract-test execution.
- Initial source work implemented strict module-manifest validation, public `hexalith-module` command parsing, invocation-metadata state contract, first-causal diagnostics, canonical run-evidence output, and metadata-only secret/filter handling. **Historical code-review correction (2026-07-21, CD1):** the initial state contract was scaffolding because the prerequisite gate reported unavailable; Stage 3 later opened and qualified the public composition path.
- Implemented the strict readiness-evidence validator, JSON/human command output, deterministic source/row/rule diagnostics, packaged positive/negative contract corpora, JSON schemas, and documentation.
- Added Release-only exact-package build/publish/contract scripts and semantic-release lifecycle wiring. No package was published and no consumer pin was invented.
- Hardened the public contracts against symlink/reparse-point escape, partial/forged evidence artifacts, secret-bearing input retention, unstable parser/Ctrl+C diagnostics, incomplete TRX counter accounting, and missing source/SDK provenance; package qualification now runs the packed tools against consumer-owned copied fixtures and retained invocation evidence.
- Stage 4 now qualifies local public source-command persisted composition. Installed-package live composition, native reports, an exact published version, and full P0 named-owner/Test-Architect acceptance remain outstanding; this story remains in progress and does not modify Projects.
- The 2026-08-01 P1R implementation preserves completed source work, records EventStore/catalog/runner `3.88.0` as an unaccepted candidate at Builds revision `4351d7cba7545a96661ca2ee2ca2629df6d0a118`, retains Architecture `3.70.1`, increases remaining effort to XL, and requires a fail-closed machine-checkable P0 acceptance record.
- Chunk A code-review remediation is complete: 18 patches applied, 2 findings dismissed as noise, and no Chunk A item deferred. The story remains in progress because unreviewed delivery surfaces and external qualification/acceptance dependencies remain.
- 2026-09-23: Connected validated executable public `run/down/test` to the Builds-owned composition engine. Local public `run/down` and nonpassing unsupported `test` are proven by a fresh hash-bound ten-suite/110-test live campaign and separate public CLI probe. Stage 3 is complete in that local supported scope; packaged executable composition, full profiles, native reports, publication, rollback, and full P0 acceptance remain open.
- 2026-09-24: Qualified the two-module persisted `full` profile with the public source `test` command and exact event/projection/sequence, restart, retry, peer, authentication, Tenant, and fail-closed controls. The final source/binary-bound public campaign and independent verification passed with zero retained resources; packaged live and native-report acceptance remains in Stage 5, so P0 stays open.
- 2026-09-25: Stage 5 code review completed: 6 decisions, 15 patches, 3 deferrals, 16 rejections, plus SR-N1. All decisions were resolved by Jerome, and 17 patches were applied. The installed `0.0.0-stage5.7` gate, live campaign, and native lane requalified the remediated tree, and the hardened validator accepts real runner output. The evidence is dirty-tree and unpublished, so P0, P2, and Story 6.1 stay open, with no commit or owner acceptance.
- 2026-09-25: With Jerome's go-ahead, Stage 5 is committed on local `main` in six Conventional Commits, from `05ed57d` through the `docs(g4)` commit. Each code commit tree was built and tested before committing. Superseded evidence was moved out of the tree with a hash manifest. Nothing was pushed or published, and Stage 6 has not started.
- 2026-09-25: Restored `main` for Stage 6 readiness at EventStore `3.108.1` (Jerome: option a). Proposed a four-commit repair: regenerated corpus, renormalized C# blobs (the root cause of the release publish-guard refusal), packaged-host shims kept out of project validation, and a refreshed audit. The local replay of all 33 CI steps passed. Requalified `0.0.0-stage5.8` on a clean tree through the gate (`releaseEligible: true`), the live campaign, and the native lane. Not pushed. P1R at `3.108.1` and the G-6 refresh remain open, so Stage 6 is not ready.
- 2026-09-24: Stage 5 local packaged scope complete. The runner-owned native test executor binds VSTest and MTP TRX counts and hashes into evidence. The successful-status contract is corrected to `completed`. The `hexalith.g4-p0-acceptance.v1` validator and its 20-case corpus run blocking through the installed tool. The installed `0.0.0-stage5.6` campaign, native xUnit live lane, and package gate passed with zero retained resources. The evidence is dirty-tree and unpublished, so P0, P2, and Story 6.1 remain open, and no owner acceptance is claimed.

### File List

- `_bmad-output/implementation-artifacts/6-1-p0-deliver-g4-persisted-runner-and-evidence-tooling.md` (new)
- `Directory.Build.props` (new)
- `Directory.Packages.props` (new)
- `Hexalith.Builds.slnx` (modified)
- `global.json` (new)
- `src/libraries/Directory.Build.props` (new)
- `src/libraries/Hexalith.Builds.Evidence.Cli/Hexalith.Builds.Evidence.Cli.csproj` (new)
- `src/libraries/Hexalith.Builds.Evidence.Cli/Program.cs` (new)
- `src/libraries/Hexalith.Builds.Module.Cli/Hexalith.Builds.Module.Cli.csproj` (new)
- `src/libraries/Hexalith.Builds.Module.Cli/Program.cs` (new)
- `src/libraries/Hexalith.Builds.Tooling/Hexalith.Builds.Tooling.csproj` (new)
- `test/Hexalith.Builds.Evidence.Tests/EvidenceToolSpineTests.cs` (new)
- `test/Hexalith.Builds.Evidence.Tests/Hexalith.Builds.Evidence.Tests.csproj` (new)
- `test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj` (new)
- `test/Hexalith.Builds.Module.Tests/ToolProjectSpineTests.cs` (new)
- `test/Hexalith.Builds.Tooling.IntegrationTests/Hexalith.Builds.Tooling.IntegrationTests.csproj` (new)
- `test/Hexalith.Builds.Tooling.IntegrationTests/ToolAssemblySpineTests.cs` (new)
- `.github/workflows/build-release.yml` (modified)
- `Github/create-release/action.yml` (modified)
- `Github/create-release/README.md` (modified)
- `README.md` (modified)
- `Tools/README.md` (modified)
- `Tools/build-g4-tool-packages.ps1` (new)
- `Tools/publish-g4-tool-packages.ps1` (new)
- `Tools/test-g4-tool-package-contracts.ps1` (new)
- `package.json` (modified)
- `package-lock.json` (modified)
- `schemas/hexalith.module-manifest.v1.json` (new)
- `schemas/hexalith.module-run-evidence.v1.json` (new)
- `schemas/hexalith.readiness-evidence.v1.json` (new)
- `src/libraries/Hexalith.Builds.Module.Cli/AssemblyInfo.cs` and `ModuleCommandApplication.cs` (new)
- `src/libraries/Hexalith.Builds.Evidence.Cli/AssemblyInfo.cs` and `EvidenceCommandApplication.cs` (new)
- `src/libraries/Hexalith.Builds.Tooling/AssemblyInfo.cs` and `Diagnostics/`, `Filesystem/`, `Manifest/`, `Runtime/`, `RunEvidence/`, and `Evidence/` source sets (new)
- `src/libraries/Hexalith.Builds.Tooling/Diagnostics/ToolCommandHost.cs`, `Diagnostics/ToolDiagnosticFormatter.cs`, `Evidence/ReadinessEvidenceValidator.cs`, `Filesystem/RepositoryPathResolver.cs`, `Manifest/ManifestPathValidator.cs`, `RunEvidence/ModuleRunEvidenceArtifactValidator.cs`, `RunEvidence/ModuleRunEvidenceArtifactSummary.cs`, `RunEvidence/ModuleRunEvidenceFactory.cs`, `RunEvidence/ModuleRunEvidenceWriter.cs`, and `Runtime/ModuleCommandExecutionService.cs` (new)
- `src/libraries/Hexalith.Builds.Tooling/TestReports/` source set and `test/Hexalith.Builds.Module.Tests/NativeTestReportLoaderTests.cs` (new)
- `test/Hexalith.Builds.Module.Tests/ManifestValidationTests.cs`, `ModuleCommandApplicationTests.cs`, `ModuleRunEvidenceSerializationTests.cs`, `PersistedFixtureAssetTests.cs`, and `ToolOutcomeTests.cs` (new)
- `test/Hexalith.Builds.Evidence.Tests/EvidenceCommandApplicationTests.cs`, `EvidenceFixturePath.cs`, and `ReadinessEvidenceValidatorTests.cs` (new)
- `test/fixtures/module/` and `test/fixtures/evidence/` curated positive, negative, expected-result, full-valid-module-run-artifact, invalid-artifact, and contract-only control corpora (new)
- `Tools/runtime-toolchain-baseline.json` (Stage 3 obsolete release-workflow literal pin removed)
- `_bmad-output/implementation-artifacts/6-1-p0-stage3-descriptor-abi-proposal.md` and `6-1-p0-stage3-descriptor-abi-approval.json` (Stage 3 owner decision)
- `schemas/hexalith.module-descriptor.v1.json` and `schemas/hexalith.ui-descriptor.v1.json` (Stage 3 executable result contracts)
- `src/libraries/Hexalith.Builds.Tooling/Manifest/DescriptorAssemblyLoadContext.cs`, `DescriptorChildWorker.cs`, `DescriptorDocumentValidator.cs`, `ExecutableDescriptor*.cs`, `ExecutableModuleDescriptor.cs`, and `ExecutableUi*.cs` (Stage 3 precomposition adapter)
- `test/Hexalith.Builds.Module.Tests/ExecutableDescriptorLoaderTests.cs` (Stage 3 adapter and public fail-closed checks)
- `Hexalith.Builds.slnx` (Stage 3 slice: hosts and executable fixture projects added)
- `src/hosts/Directory.Build.props` and `src/hosts/Hexalith.Builds.Module.{AppHost,EventStoreHost,UiHost}/` (Stage 3 slice hosts)
- `src/libraries/Hexalith.Builds.Tooling/Runtime/Composition*.cs` (Stage 3 slice composition engine, plan, state, probe, scanner, token factory)
- `test/fixtures/module/executable/` (Stage 3 slice executable two-module fixture, manifest, profile, and build entry point)
- `test/Hexalith.Builds.Module.Tests/Composition*Tests.cs` and `CompositionTestFiles.cs` (Stage 3 slice unit coverage)
- `test/Hexalith.Builds.Tooling.IntegrationTests/Hexalith.Builds.Tooling.IntegrationTests.csproj` and `Live/` (Stage 3 slice opt-in live lane)
- `Tools/test-g4-tool-package-contracts.ps1` (fixture projects excluded from per-project `dotnet test`)
- `src/libraries/Hexalith.Builds.Tooling/Runtime/CompositionEngine.cs` and `CompositionDiagnosticMirror.cs` (Stage 3 metadata-only diagnostic mirror)
- `test/Hexalith.Builds.Module.Tests/CompositionRedactionTests.cs` (seeded sensitive-output regression through the mirror)
- `Tools/run-g4-stage3-live-qualification.py` and `test/qualification/test_stage3_live_qualification.py` (fail-closed live probe and fingerprint harness with controls)
- `_bmad-output/implementation-artifacts/qualification-evidence/stage3-review-remediation-20260923*/` and `stage3-review-remediation-20260923-manifest.json` (retained failed/passing suites, source/binary hashes, commands, probe outcomes, logs, package inventory, and verification)
- `src/libraries/Hexalith.Builds.Module.Cli/ModuleCommandApplication.cs`, `src/libraries/Hexalith.Builds.Tooling/Diagnostics/ToolCommandResult.cs`, `Runtime/ModuleCommandExecutionService.cs`, `Runtime/CompositionCommandOptions.cs`, and `RunEvidence/ModuleRunEvidenceFactory.cs` (Stage 3 public command routing, run identity, output, and evidence)
- `src/libraries/Hexalith.Builds.Tooling/Runtime/{CompositionEngine,CompositionEngineOptions,CompositionEnvironment,CompositionRunStateStore}.cs` and `src/hosts/Hexalith.Builds.Module.AppHost/StopSignal.cs` (persistent public run handoff and exact bounded teardown)
- `test/Hexalith.Builds.Module.Tests/PublicCompositionCommandTests.cs`, `CompositionEngineTests.cs`, `ExecutableDescriptorLoaderTests.cs`, and `Hexalith.Builds.Module.Tests.csproj` (public prerequisite, isolation, evidence, and expectation checks)
- `test/Hexalith.Builds.Tooling.IntegrationTests/Live/LivePublicCommandTests.cs` and `test/fixtures/module/executable/profiles/p0-executable-live.fixture.json` (opt-in public process lane and accurate unsupported-profile metadata)
- `Tools/run-g4-stage3-live-qualification.py`, `test/qualification/test_stage3_live_qualification.py`, and `_bmad-output/implementation-artifacts/qualification-evidence/stage3-public-20260923*/` (fresh final-source hash-bound campaign, package inventory, public probe, and retained failures)
- `test/fixtures/module/executable/profiles/p0-two-module-full.fixture.json`, `test/fixtures/module/executable/hexalith.module-manifest.v1.json`, and `test/fixtures/module/executable/*/` projection sources (Stage 4 real two-module persisted fixture)
- `src/libraries/Hexalith.Builds.Tooling/Runtime/PersistedProfile*.cs`, `CompositionResourceController.cs`, `CompositionRunPlanFactory.cs`, and `src/hosts/Hexalith.Builds.Module.AppHost/` (Stage 4 public profile, persisted state checks, peer and restart resource control)
- `src/libraries/Hexalith.Builds.Module.Cli/{ModuleCommandApplication,Program}.cs`, `src/libraries/Hexalith.Builds.Tooling/Diagnostics/ToolCommandHost.cs`, and `Runtime/ModuleCommandExecutionService.cs` (Stage 4 public execution and cancellation cleanup)
- `test/Hexalith.Builds.Module.Tests/{PersistedProfileStateTests,CompositionRunPlanFactoryTests}.cs` and `_bmad-output/implementation-artifacts/qualification-evidence/stage4-public-20260924/` (focused controls, retained failed and final campaigns, package gate, G-6 audit, independent hash verification)
- `src/libraries/Hexalith.Builds.Tooling/Runtime/{NativeTestExecutor,NativeTestExecutionResult,NativeTestHandoff,PersistedProfileNativeTests}.cs` (new) and `Runtime/{PersistedProfileDefinition,PersistedProfileLoader,PersistedProfileExecutor,ModuleCommandExecutionService}.cs`, `RunEvidence/{ModuleRunEvidenceFactory,ModuleRunEvidenceWriter}.cs` (modified) (Stage 5 native test executor, handoff, TRX binding, `completed` status correction)
- `src/libraries/Hexalith.Builds.Tooling/Evidence/G4P0AcceptanceValidator.cs` (new), `Evidence/ReadinessEvidenceCommandExecutionService.cs`, and `src/libraries/Hexalith.Builds.Evidence.Cli/EvidenceCommandApplication.cs` (Stage 5 acceptance validator and JSON dispatch)
- `src/libraries/Hexalith.Builds.Module.Cli/Hexalith.Builds.Module.Cli.csproj`, `src/libraries/Hexalith.Builds.Module.Cli/pack/`, `src/hosts/Hexalith.Builds.Module.AppHost/RunTopology.cs`, and `Tools/build-g4-tool-packages.ps1` (Stage 5 packaged hosts without a source AppHost override)
- `test/fixtures/module/executable/{P0Fixture.NativeTests,P0Fixture.NativeTests.VsTest}/`, `profiles/p0-two-module-full-mtp.fixture.json` (new), `profiles/p0-two-module-full.fixture.json`, and `hexalith.module-manifest.v1.json` (Stage 5 native test fixture and `full-mtp` profile)
- `test/fixtures/evidence/acceptance/` (new) and `.gitignore` (Stage 5 synthetic acceptance validator corpus)
- `test/Hexalith.Builds.Module.Tests/{NativeTestExecutorTests,PersistedProfileNativeTestsTests}.cs`, `test/Hexalith.Builds.Evidence.Tests/G4P0AcceptanceValidatorTests.cs`, and `test/Hexalith.Builds.Tooling.IntegrationTests/Live/PackagedPersistedProfileTests.cs` (new; Stage 5 unit, contract, and opt-in packaged live coverage)
- `Tools/{test-g4-tool-package-contracts,test-g4-tool-package-contract-gate}.ps1`, `Tools/G4PackageQualification.functions.ps1`, and `Tools/README.md` (Stage 5 blocking acceptance controls, help contract, Git-attribute-aware fixture proof, documentation)
- `_bmad-output/implementation-artifacts/qualification-evidence/stage5-local-20260924/` (Stage 5 package gate, packages, campaign, native lane, candidate record, G-6 audit, and verification manifest; the superseded stage5.6 attempts were moved to `~/.local/state/hexalith-qualification/stage5-superseded-20260924/`, and their hashes stay in `stage5-verification.json` and `stage5.7/superseded-archive-manifest.json`)
- `src/libraries/Hexalith.Builds.Tooling/Runtime/{PackagedHostProject,NativeTestReportRedactor,PersistedProfileEvidence}.cs` (new), `src/libraries/Hexalith.Builds.Module.Cli/pack/projects/{EventStore,Ui}/Host.csproj` (Stage 5 review: private packaged host builds, report redaction, shared assertion labels)
- `test/Hexalith.Builds.Module.Tests/{TestResultCombinationTests,NativeTestReportRedactorTests,PackagedHostProjectTests}.cs` (new), `.gitattributes`, `Tools/test-g4-tool-package-artifact-validator.ps1` (Stage 5 review remediation)
- `_bmad-output/implementation-artifacts/deferred-work.md` (Stage 5 review deferrals)
- `_bmad-output/implementation-artifacts/qualification-evidence/stage5-local-20260924/{stage5.7,packages7,live-stage5.7,native-lane-stage5.7}/` and `run-packaged-qualification-stage5.7.py` (review-remediated `0.0.0-stage5.7` qualification)

### Change Log

- 2026-07-17: Implemented the Builds-owned G-4 tool spine, strict module/evidence contracts, deterministic evidence output, package qualification/release wiring, schemas, fixtures, tests, and adoption documentation; hardened fail-closed physical-path, provenance, artifact, diagnostic, cancellation, and package-consumer behavior; retained live persisted execution and acceptance as explicit external-dependency work.
- 2026-07-21: Completed Chunk A adversarial code-review remediation (18 patches), expanded boundary/security/verification controls, normalized the accepted EventStore pin to `3.70.1`, and requalified source build/tests plus isolated package restore/help behavior; retained the pre-existing strict-control fixture gap and live qualification as open work.
- 2026-08-01: Approved Correct Course rebaseline added P1R, separated implemented source contracts from supported capability, reordered remaining work into seven acceptance stages, and made remote restore plus independently validated owner acceptance mandatory.
- 2026-09-22: Resumed Stage 3 from the uncommitted Builds tree; removed the obsolete G-6 release-workflow literal pin in its owning baseline, retained the historical G-6 packet, verified selected tools in an isolated temporary lane, recorded the named-owner descriptor ABI approval, implemented and locally qualified its executable loader and schemas, and kept runtime composition at HXR003 while fresh G-6, FrontComposer pin reconciliation, a generic host, and live evidence remain outstanding.
- 2026-09-23: Refreshed and re-executed the affected G-6 qualification (named-owner accepted; validator `G6-EVIDENCE-VALID`), aligned the runner/schema/fixture FrontComposer pin to the owning catalog `4.5.0` with a superseded-pin negative control, and recorded the binding Stage 3 implementation slice.
- 2026-09-23: Implemented the Stage 3 slice (S3-1 to S3-5): executable two-module fixture, Builds-owned EventStore/UI/Aspire hosts, runner composition engine, opt-in live lane, and unit coverage; the public command still returns HXR003 pending owner and Test Architect review.
- 2026-09-23: Reproduced HXR021 in 1/10 full live suites, traced it to a placement port bind collision inside the Linux ephemeral range, added bounded per-probe R17 diagnostics, selected allocation ports outside that range, and observed 10/10 consecutive full live suites with clean teardown and no JWT-shaped log value. Stage 3 remains open for owner and named Test Architect review.
- 2026-09-23: Follow-up full live loops exposed module HTTP and sidecar gRPC ephemeral-port binds, a stalled proxied Redis PING, and a readiness document order regression. Pinned project and all sidecar listeners to run-reserved non-ephemeral ports, mapped Redis directly, restored document ordering, and observed a final 10/10 full live suite streak with clean teardown and redacted logs. Stage 3 and P0 remain unchecked pending owner and named Test Architect review.
- 2026-09-23: Remediated S3-REV1–S3-REV3 with fail-closed per-probe evidence, metadata-only AppHost output mirroring, and source/binary-bound qualification. Retained the unverified process-probe failure, then verified a fresh ten-suite/100-test consecutive streak, the `stage3.8` package gate and 58 inventory hashes, public HXR003, and G6-EVIDENCE-VALID. Codex's technical disposition does not supply Jérôme Piquot's personal owner or Test Architect acceptance; Stage 3 and P0 remain unchecked.
- 2026-09-23: Jérôme Piquot personally accepted both remediated Stage 3 evidence assessments, for the Builds/Platform owner and named Test Architect roles. Public HXR003, Stage 3 public-composition work, later P0 stages, and full P0 acceptance remain open; Stage 3 and P0 checkboxes remain unchecked.
- 2026-09-23: Opened validated executable public `run/down/test` onto the qualified composition engine after the accepted Stage 3 assessments. Fresh final-source 10/10 live suites and 110/110 tests, a same-hash public CLI probe, `stage3.14` local package contract gate, 58/58 package inventory check, and G6-EVIDENCE-VALID support the local Stage 3 completion. Unsupported public profile execution remains exit 2/HXR029; packaged executable live composition and Stages 4–7 remain open, so P0 stays in progress.
- 2026-09-24: Implemented and publicly qualified the local two-module persisted `full` profile. The final hash-bound public campaign passed `full` and retained nonpassing unsupported, prerequisite, and active-cancellation controls with zero resources left; focused state tests, the `stage4.4` package contract gate, independent evidence verification, and G6-EVIDENCE-VALID passed. Stage 4 is complete in that scope; packaged live composition, native reports, publication, rollback, and named-owner P0 acceptance remain in Stages 5–7.
- 2026-09-24: Stage 5 (local packaged scope). Added the runner-owned VSTest/MTP native test executor, handoff contract, and TRX binding into `testCounts`/`artifactHashes`. Corrected successful profile status to `completed`. Completed the fail-closed `hexalith.g4-p0-acceptance.v1` validator with a 20-case packaged corpus in the package gate. Cleaned the stale interrupted run and rebuilt the isolated Dapr 1.18.0/1.18.2 home. Qualified installed `0.0.0-stage5.6` with the gate, a hash-bound campaign, and the native xUnit live lane. No publication, consumer pin, staging, commit, or acceptance.
- 2026-09-25: Stage 5 review remediation.
  - Packaged hosts now build in private per-run copies.
  - Native reports are redacted before they are retained.
  - Cancelled evidence keeps its profile.
  - The acceptance validator binds platforms, assertion sets, nuspec identity, controls, cleanup, and approval dates, with a 37-case corpus.
  - The gate asserts packaged hosts and runs the fixture-proof self-test.
  - `0.0.0-stage5.7` was requalified through the gate, the live campaign, and the native lane.
  - No publication, pin, or acceptance.
  - Committed on local `main` (not pushed) after moving superseded evidence out of the tree.
- 2026-09-25: Stage 6 readiness repair (not pushed). EventStore `3.108.1` corpus, CR-only renormalization of 54 C# blobs, `Host.csproj.template` shims packed through a pack-time copy, consumer-evidence audit refresh, and a clean-tree `0.0.0-stage5.8` requalification (gate, live campaign, native lane). P1R and G-6 remain open.
