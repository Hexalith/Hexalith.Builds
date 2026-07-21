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
  builds_owner: Jerome
  platform_owner: Jerome
  test_architect: unassigned-required-before-acceptance
implementation_dependencies: []
qualification_dependencies: [6.1-P1, G-6]
parallel_with: [6.1-P1]
unblocks: [6.1-P4]
target_date: uncommitted
estimate: L
risk: high evidence-chain risk
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

- [ ] Add the real P0 persisted fixture (AC: 5, 6, 11-14)
  - [ ] Add a valid two-module manifest and run-unique persisted fixture under owner-repository test assets.
  - [ ] Prove authenticated write, persisted event, projection/read state and sequence, stop/restart/rehydration, retry/idempotency, two-instance access, and Tenant isolation.
  - [ ] Add stale-state, absent-event, absent-projection, wrong-sequence, cross-Tenant, cancellation, and prerequisite-unavailable controls.
  - [ ] Keep fake/static topology checks as unit regressions only; they cannot satisfy P0.

- [ ] Emit deterministic module-run evidence (AC: 6-8, 11, 12)
  - [x] Define `hexalith.module-run-evidence.v1`, canonical serialization, metadata allowlist, hash rules, volatile fields, and artifact retention.
  - [ ] Capture VSTest and MTP/xUnit v3 native results without hiding their exit status or report semantics.
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
  - [ ] Add a live persisted integration project that exercises the packed runner with at least two modules and actual EventStore/Dapr state.
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

**Deferred (belongs with the live runner — P1/G-6; all latent behind the deferred prerequisite gate):**

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

### Hard Stops

- Stop if implementation would change Projects or another repository without separate authorization.
- Stop if package IDs, command names, schema IDs, or repository ownership must change; update this authority record first.
- Stop if P0 chooses the P1 platform baseline or represents the 3.70.0/3.67.3 mismatch as resolved.
- Stop P0 acceptance, but not implementation, until a named Test Architect, P1 baseline, and G-6 disposition are recorded.
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

### Completion Notes List

- Ultimate context engine analysis completed - comprehensive developer guide created.
- Jerome authorized Hexalith.Builds to own both P0 tools; authorized baseline `edbaeae` replaced the stale earlier observation `01e48ee`. The delivery revision remains unaccepted and the current worktree is not represented as a published package.
- Package IDs, command names, schema IDs, semantic-release channels, expected initial stable version, greenfield rollback, matrix-status disposition, file impact, negative controls, and P4 handoff are explicit.
- Established the SDK/MSBuild/package spine at the authorized baseline with exact tool identities, a non-packable shared core, central package management, warning-as-error analysis, and nonzero contract-test execution.
- Implemented strict module-manifest validation, public `hexalith-module` command parsing, invocation-metadata state contract, first-causal diagnostics, canonical run-evidence output, and metadata-only secret/filter handling; this is not live platform composition. **Code-review correction (2026-07-21, CD1):** the invocation-metadata state contract is scaffolding only — `ModuleInvocationStateStore`/`ModuleRuntimePlan` are not yet reachable from any command path (the prerequisite gate always reports unavailable), so run-identity resource scoping is unverified and must be re-checked when platform composition opens the gate.
- Implemented the strict readiness-evidence validator, JSON/human command output, deterministic source/row/rule diagnostics, packaged positive/negative contract corpora, JSON schemas, and documentation.
- Added Release-only exact-package build/publish/contract scripts and semantic-release lifecycle wiring. No package was published and no consumer pin was invented.
- Hardened the public contracts against symlink/reparse-point escape, partial/forged evidence artifacts, secret-bearing input retention, unstable parser/Ctrl+C diagnostics, incomplete TRX counter accounting, and missing source/SDK provenance; package qualification now runs the packed tools against consumer-owned copied fixtures and retained invocation evidence.
- Live persisted qualification, actual EventStore/Dapr composition, P1/G-6 disposition, an exact published version, and named-owner/Test-Architect acceptance remain outstanding; this story remains in progress and does not modify Projects.
- Chunk A code-review remediation is complete: 18 patches applied, 2 findings dismissed as noise, and no Chunk A item deferred. The story remains in progress because unreviewed delivery surfaces and external qualification/acceptance dependencies remain.

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

### Change Log

- 2026-07-17: Implemented the Builds-owned G-4 tool spine, strict module/evidence contracts, deterministic evidence output, package qualification/release wiring, schemas, fixtures, tests, and adoption documentation; hardened fail-closed physical-path, provenance, artifact, diagnostic, cancellation, and package-consumer behavior; retained live persisted execution and acceptance as explicit external-dependency work.
- 2026-07-21: Completed Chunk A adversarial code-review remediation (18 patches), expanded boundary/security/verification controls, normalized the accepted EventStore pin to `3.70.1`, and requalified source build/tests plus isolated package restore/help behavior; retained the pre-existing strict-control fixture gap and live qualification as open work.
