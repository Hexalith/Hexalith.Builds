# Hexalith CI/CD — Change Implementation Plan

## 0. Purpose and how to use this document

This plan converts the findings of the Hexalith.Builds CI/CD audit (auditing commit `5041efe` on `main`, dated 2026-07-26) into an ordered, self-contained set of implementation tasks. It is written for an implementing agent that will apply the changes.

Each task uses a fixed template: **Objective**, **Target files**, **Current state**, **Required change**, **Implementation notes**, **Constraints**, **Dependencies**, **Risks**, **Validation**. Apply tasks in the order given within each repository, and respect the cross-repository sequencing in §4. Do not batch unrelated changes into one commit; one logical change per commit with a Conventional Commit message.

The work spans **three separate Git repositories**. Treat each as an independent unit of work with its own branch, commits, and pull request:

| Alias | Path | Remote |
| --- | --- | --- |
| **builds** | `/home/administrator/projects/hexalith/builds` | `Hexalith/Hexalith.Builds` |
| **eventstore** | `/home/administrator/projects/hexalith/eventstore` | `Hexalith/Hexalith.EventStore` |
| **tenants** | `/home/administrator/projects/hexalith/tenants` | `Hexalith/Hexalith.Tenants` |

## 1. Repository boundaries and global constraints

These rules come from the repositories' own `CLAUDE.md`/baseline and must not be violated:

1. **Work from the repository that owns the change.** Never edit files inside any `references/` submodule to fix a superproject problem. A change to shared build logic belongs in **builds**; a change to a consumer's workflow belongs in that consumer.
2. **Conventional Commits are mandatory** and commit hooks must not be bypassed (`--no-verify` forbidden). Never use the `chore` type — pick the specific type (`ci`, `build`, `fix`, `feat`, `docs`, `test`, `refactor`). Note the release effect in **builds** and both consumers: `feat` → minor, `fix`/`perf` → patch, everything else → no release. Choose types deliberately so you do not trigger an unintended version bump.
3. **Submodules:** initialize only root-declared `references/` submodules; never `--recursive` or `--remote`.
4. **Preserve unrelated user changes.** In **builds** the working tree currently carries six untracked fixtures under `test/fixtures/evidence/negative/*.expected.json` — do not stage, commit, or delete them; they are out of scope.
5. **No publishing side effects during implementation.** Do not run semantic-release, `dotnet nuget push`, container pushes, registry logins, or deployments. Validation is limited to build/test/lint.
6. **Do not weaken a gate to make it pass.** If a build/test gate blocks you, record the blocker with the exact command and output; do not disable audits, warnings-as-errors, or fail-closed checks to get green.

## 2. Global validation harness

These commands are known-good in the current environment (SDK 10.0.302, Node 26, pwsh 7.6, Python 3.14, `actionlint` 1.7.12). Use them to validate changes.

```bash
# Workflow/action linting (from a repo root). Note: this actionlint build treats
# -color as a boolean flag; use -no-color, NOT "-color never".
actionlint -no-color

# .NET tests — run per project (never solution-level dotnet test), Release config
dotnet test <path/to/Test.csproj> -c Release

# Python contract tests (pytest is absent; use unittest from inside the test dir)
( cd Github/publish-containers/tests && python3 -m unittest discover -v )
( cd Github/commitlint/tests && python3 -m unittest discover -v )

# PowerShell validators/contract tests
pwsh -NoProfile -File Tools/<script>.ps1

# commitlint config resolves from the committed lockfile
npx --no-install commitlint --version
```

Baseline to preserve: `actionlint` currently reports **0 findings**; the three **builds** test projects pass **131/131**; the Python suites pass **55/55**; the nine PowerShell validators pass. Any change that regresses these baselines is a defect in the change, not an acceptable side effect.

---

## 3. Change sets — repository **builds**

Apply B01–B10 in order. B11–B12 are opportunity-level and may be deferred, but are specified for completeness.

### CHG-B01 — Diagnose and fix the failing Release workflow *(finding CICD-002, High, release blocker)*

**Objective.** Restore the ability to cut a release from current `main`.

**Target files.** `.github/workflows/build-release.yml` (step `Qualify G-4 tool source and Release packages`, lines 113–115, invoking `Tools/test-g4-tool-package-contracts.ps1 -Version "0.0.0-ci.$env:GITHUB_RUN_NUMBER" -RequireControls`); possibly `Tools/test-g4-tool-package-contracts.ps1`, `Tools/build-g4-tool-packages.ps1`.

**Current state.** The last five `Release` runs (2026-07-20 → 07-21) all failed at that step. The last successful release was `v4.20.0` (2026-07-17). Critically, `Tools/test-g4-tool-package-contracts.ps1` **passes when run locally** with a synthetic version, so the failure is environment-specific to CI (candidate causes: NuGet source resolution for the isolated tool restore, the `-RequireControls` flag requiring control fixtures that are absent or unstaged in CI, or the `0.0.0-ci.$GITHUB_RUN_NUMBER` version shape). The root cause is **not yet confirmed** — logs were not retrievable during the audit.

**Required change.** This is an **investigation-first** task; do not blind-edit.
1. Retrieve the failing logs: `gh run view 29829693999 -R Hexalith/Hexalith.Builds --log-failed` (and the same for one or two prior failed runs to confirm the failure is stable at the same step).
2. Reproduce locally with the CI-equivalent invocation (`-RequireControls`, a `0.0.0-ci.<n>` version, and the same NuGet source configuration the CI job uses).
3. Fix the identified root cause. Prefer a fix in the tooling/fixtures over relaxing the gate. Do **not** remove `-RequireControls` or the SHA-256 verification to force a pass.

**Implementation notes.** If the cause is a missing/unstaged control fixture, ensure the required fixtures are tracked in Git (relevant because six evidence fixtures are currently untracked — see §1 rule 4; confirm whether the G-4 controls the workflow needs overlap with those). If the cause is NuGet source isolation, make the isolated `nuget.config` explicit rather than environment-dependent.

**Constraints.** No publishing during reproduction — the qualify step is pre-publication and safe, but stop before any `create-release`/push step.

**Dependencies.** None. This is the highest-priority task; other **builds** release-path fixes should land on top of a green release path.

**Risks.** Misdiagnosis could mask the real defect. Mitigate by confirming the fix reproduces green with the exact CI invocation before committing.

**Validation.** `pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 -Version "0.0.0-ci.1" -RequireControls` succeeds; after merge, a manual `workflow_dispatch` of `Release` on `main` completes `success`.

**Commit type.** `fix(release): …` (this should be a patch-worthy fix).

---

### CHG-B02 — Add self-CI for the builds repository *(finding CICD-001, High)*

**Objective.** Make the repository run its own build, tests, CodeQL, and dependency-review on every push and pull request to `main`, so regressions in shared actions/workflows/tooling are caught before merge — not only at manual release time.

**Target files.** New `.github/workflows/ci.yml`; new thin callers for CodeQL and dependency-review (either separate files `.github/workflows/codeql-caller.yml` and `.github/workflows/dependency-review-caller.yml`, or jobs wired into `ci.yml`).

**Current state.** On push/PR, only `commitlint-caller.yml` runs. `codeql.yml`, `dependency-review.yml`, `domain-ci.yml`, `domain-release.yml` are all `workflow_call`-only and have **no local caller**. The 131 .NET tests, 55 Python tests, and nine PowerShell validators execute only inside `build-release.yml` (`workflow_dispatch`), which is currently red. Remote check-runs on `main` confirm only Dependabot/SonarCloud/commitlint run.

**Required change.** Add a `ci.yml` triggered on `pull_request` (branches `main`) and `push` (branches `main`) that runs the **non-publishing** validation already proven in `build-release.yml`, i.e. mirror its steps from `Initialize .NET` (line 53) through `Test multi-platform container publisher contracts` (line 117–119), **excluding** the final `Create Release` step (lines 121–125). Concretely it should:
- checkout with `submodules: false` then initialize root submodules via `./Github/initialize-build` (or `initialize-dotnet` as `build-release.yml` does);
- run the `Tools/validate-*.ps1` and `Tools/test-*.ps1` steps;
- run `npm ci` + `npm audit signatures`;
- run `dotnet restore`/`build -c Release -warnaserror` and `dotnet test -c Release` for the three test projects (`test/Hexalith.Builds.Module.Tests`, `test/Hexalith.Builds.Evidence.Tests`, `test/Hexalith.Builds.Tooling.IntegrationTests`), one project at a time;
- add a CodeQL job (call `./.github/workflows/codeql.yml` with `languages: csharp`, plus consider `python`) and a dependency-review job on `pull_request` (call `./.github/workflows/dependency-review.yml`).

**Implementation notes.**
- Set top-level `permissions: contents: read`; grant `security-events: write` only on the CodeQL job and `pull-requests: write` only on the dependency-review job (both reusable workflows already declare these).
- To avoid divergence, prefer factoring the shared validation steps so `ci.yml` and `build-release.yml` reference the same composite/action rather than copy-pasting; if that is too large, duplicate now and note the follow-up.
- Give the workflow a `concurrency` group keyed on the ref with `cancel-in-progress: true` (CI, unlike release, may cancel superseded runs).
- Pin any third-party actions by full SHA + version comment, reusing the SHAs already present in the repo (`actions/checkout@9c091bb… # v7.0.0`, `actions/setup-dotnet@a98b568… # v6.0.0`, etc. — see CHG-B09 for the canonical versions).

**Constraints.** Dependency-review only runs meaningfully on `pull_request`; gate that job accordingly. CodeQL `build-mode: none` is appropriate (matches `codeql.yml` default).

**Dependencies.** Should land after **CHG-B01** so the first CI run is green, and should be consistent with the action-version alignment in **CHG-B09**.

**Risks.** New required checks are **not** automatically enforced on `main`; enabling them in the branch ruleset is a remote configuration change (see §5 / opportunity O-6) and must be done by the repository owner. Flag this explicitly in the PR description. Low risk of increased CI minutes — acceptable.

**Validation.** Open a draft PR; confirm build/test, CodeQL, and dependency-review appear as check-runs and pass. `actionlint -no-color` clean.

**Commit type.** `ci: add self-hosted CI pipeline for builds repository`.

---

### CHG-B03 — Complete Dependabot ecosystem and directory coverage *(finding CICD-004, High)*

**Objective.** Ensure Dependabot surveils every dependency ecosystem actually present and every location that pins third-party actions.

**Target files.** `.github/dependabot.yml`.

**Current state.** Only one entry: `package-ecosystem: github-actions`, `directory: "/"` (lines 4–8). `directory: "/"` for the `github-actions` ecosystem covers only `.github/workflows/` — it does **not** scan the ten composite actions under `Github/*/action.yml` (confirmed against GitHub docs). NuGet (`package.json`-adjacent `.csproj` + `Props/Directory.Packages.props`, 283 central versions) and npm (`package.json` + `package-lock.json`) are unmonitored. The file's own header comment claims composite-action coverage that does not exist. This violates the repo's own `ci-cd-standards.md:145-147`.

**Required change.**
1. Extend the `github-actions` entry to also scan composite-action subdirectories using multiple directories: `directories: ["/", "/Github/*"]` (Dependabot supports directory globs for `github-actions`).
2. Add a `package-ecosystem: nuget` entry (`directory: "/"`, weekly).
3. Add a `package-ecosystem: npm` entry (`directory: "/"`, weekly).
4. Keep `commit-message.prefix` values that are Conventional-Commit-valid and **not** `chore` (the existing `ci(deps)` is fine; for nuget/npm use e.g. `build(deps)`).
5. Fix the header comment to reflect actual coverage.

**Implementation notes.** Optionally add `groups:` to reduce PR noise (see CHG-B11); not required here. Do not set update schedules more aggressive than weekly.

**Constraints.** Ensure the `directories`/`directory` keys are used correctly (they are mutually exclusive per entry).

**Dependencies.** None.

**Risks.** More open PRs initially. Acceptable; grouping mitigates.

**Validation.** `git` diff review; after merge, confirm three ecosystems and composite-action PRs appear in the repository's Dependabot view (owner-side check).

**Commit type.** `ci(deps): cover nuget, npm and composite actions in Dependabot`.

---

### CHG-B04 — Remove or harden the legacy container-publish actions *(finding CICD-003, High)*

**Objective.** Eliminate the script-injection and floating-submodule-pull hazards in the deprecated container actions.

**Target files.** `Github/publish-container-to-registry/action.yml` (+ its `README.md`), `Github/publish-azure-container-app/action.yml` (+ its `README.md`).

**Current state.**
- `publish-container-to-registry/action.yml:65-69` interpolates `${{ inputs.version }}`/`${{ inputs.registry }}` **unquoted** directly into a `run:` block; its README (lines 67, 102) recommends `version: ${{ github.ref_name }}`, an attacker-influenceable value. Lines 31–36 run `git checkout main` + `git pull` on `HexalithApp` and `references/Hexalith.Builds` immediately before build+publish, discarding pinned commits.
- `publish-azure-container-app/action.yml:56-59` interpolates `inputs.app-id`/`registry`/`version` unquoted into `az containerapp update`.
- Both actions are marked deprecated in their READMEs; the modern, hardened path is `Github/publish-containers` + `domain-release.yml`.

**Required change.** **Preferred: remove** both action directories and their READMEs, after confirming they are unreferenced.
1. Verify no references: `git grep -n "publish-container-to-registry\|publish-azure-container-app"` across **builds**, and confirm neither **eventstore** nor **tenants** references them (the audit confirmed the consumers do not).
2. If confirmed unreferenced, delete both directories and remove their entries from `README.md` and from `Hexalith.Builds.slnx` (the slnx cleanup overlaps CHG-B10).

**Fallback (only if a consumer outside the audited set still depends on them):** harden in place — move every `${{ inputs.* }}` used in `run:` into `env:` and reference quoted (`"$VERSION"`, `"$REGISTRY"`, …); validate `version` against a SemVer pattern before use (mirror `publish-containers.sh:37`); delete the `git checkout main && git pull` lines and rely on pinned gitlinks via `git -c submodule.recurse=false submodule update --init`; and correct the READMEs to stop recommending `github.ref_name` and mutable `@master`/`@main` references.

**Implementation notes.** Removal is cleaner and removes doc-drift debt (CHG-B10) in the same stroke. Do not remove `Github/publish-containers` — that is the modern action in active use by `domain-release.yml`.

**Constraints.** Removal must be preceded by the reference check; do not remove if any live caller exists.

**Dependencies.** Coordinate the slnx edit with CHG-B10 to avoid two conflicting edits to `Hexalith.Builds.slnx`.

**Risks.** A hidden external consumer breaks. Mitigate with the grep across all Hexalith repos available; if uncertain, choose the hardening fallback instead of removal.

**Validation.** `actionlint -no-color` clean; `git grep` shows no dangling references; if hardened, confirm no `${{ inputs.* }}` remains inside any `run:`.

**Commit type.** `fix(actions): remove legacy container-publish actions with unsafe interpolation` (or `fix(actions): harden …` for the fallback).

---

### CHG-B05 — Pin the Node runtime on publication paths *(finding CICD-009, Medium)*

**Objective.** Make the release/publish jobs run on a deterministic Node major line compatible with semantic-release 25.

**Target files.** `.github/workflows/build-release.yml:99-101`; `.github/workflows/domain-release.yml:38-41, 149-151`; `Github/package-release/action.yml:19-21`; `package.json`. Optionally `.github/workflows/commitlint.yml:12-15`.

**Current state.** These paths use `node-version: 'node'` (latest available Node). `package.json` declares neither `engines` nor `packageManager`. semantic-release 25 requires Node `^22.14.0 || >=24.10.0`.

**Required change.**
1. Pin `node-version: '22'` (resolves to the latest 22.x, which satisfies `^22.14.0`) in `build-release.yml` and `package-release/action.yml`.
2. In `domain-release.yml`, change the `node-version` input **default** from `'node'` to `'22'` (keep it an overridable input so consumers can opt out).
3. Add `"engines": { "node": ">=22.14 <23 || >=24.10" }` to `package.json`.
4. Optionally align `commitlint.yml`'s default to `'22'` for consistency.

**Implementation notes.** Do not hard-pin a full patch version; a major line pin keeps security patches flowing while removing the "latest major" jump risk.

**Constraints.** `domain-release.yml` is a reusable workflow consumed by modules; changing the default is a behavior change (see Risks).

**Dependencies.** None, but coordinate with the consumer re-pins (CHG-E01) since those consumers will pick up the new default only when they re-pin to a builds SHA containing this change.

**Risks.** Consumers relying implicitly on "latest Node" get 22.x instead. This is the intended, safer behavior; document it in the PR and in `domain-release.md` if that file lists defaults.

**Validation.** `actionlint -no-color` clean; `node -v` in a dry CI run reports 22.x; `npm ci` still resolves.

**Commit type.** `ci: pin Node runtime on release paths`.

---

### CHG-B06 — Build prereleases in Release configuration *(finding CICD-008, Medium)*

**Objective.** Stop distributing Debug (unoptimized) binaries as prerelease packages.

**Target files.** `Github/scripts/build-packages.ps1:14`.

**Current state.** `$configuration = if ($Version -notlike '*-*') { 'Release' } else { 'Debug' }` — any prerelease (version containing `-`) is built Debug, then published. Contradicts `ci-cd-standards.md:43`. This is on the deprecated `package-release` path.

**Required change.** Always build `Release`: replace the conditional with `$configuration = 'Release'` (the prerelease suffix already flows from `-p:Version`). Remove the now-dead branch and any comments implying Debug prereleases.

**Implementation notes.** Confirm downstream steps in `build-packages.ps1`/`publish-packages.ps1` don't rely on `$configuration` being Debug for path resolution.

**Constraints.** Deprecated path — keep the change minimal.

**Dependencies.** None.

**Risks.** Negligible; Release is stricter (warnings-as-errors) and may surface a latent warning — that is a correct signal, not a regression to suppress.

**Validation.** `pwsh -NoProfile -File Tools/test-*` suites still pass; run a PowerShell syntax check via `pwsh -NoProfile -Command "[scriptblock]::Create((Get-Content -Raw ./Github/scripts/build-packages.ps1)) | Out-Null"` (do not dot-source the script, which would execute it).

**Commit type.** `fix(release): build prereleases in Release configuration`.

---

### CHG-B07 — Remove `--skip-duplicate` and avoid API key on the command line (legacy publish) *(finding CICD-010, Low)*

**Objective.** Align the legacy stable-publish script with the fail-closed, no-duplicate-skip principle of the modern chain, and reduce API-key exposure in the process table.

**Target files.** `Github/scripts/publish-packages.ps1:27`. Secondary: `Tools/publish-g4-tool-packages.ps1:132` (already redacts the arg in error paths).

**Current state.** `dotnet nuget push "…/*.$Extension" --api-key $ApiKey --source $Source --skip-duplicate`. `--skip-duplicate` masks version collisions; `--api-key` on argv is visible in `ps`/`/proc`.

**Required change.**
1. Remove `--skip-duplicate` from the stable-publish invocation so a duplicate version fails closed (matching `publication_preflight.py`'s `version-collision` behavior).
2. Prefer supplying the API key without exposing it on argv — e.g. write a restricted-permission `nuget.config` with an encrypted/env-sourced key, or use `dotnet nuget push` with the key provided through a NuGet source config rather than `--api-key`. If argv cannot be avoided with the current tooling, leave `Tools/publish-g4-tool-packages.ps1` as-is (it already redacts on error) and document the residual exposure.

**Implementation notes.** Deprecated path; keep changes conservative. Do not introduce a new global tool.

**Constraints.** No live publish during validation.

**Dependencies.** None.

**Risks.** Removing `--skip-duplicate` could make a re-run fail if a version was already pushed; that is the intended fail-closed behavior. Note it in the PR.

**Validation.** Static review + `pwsh` parse check; no execution of the push.

**Commit type.** `fix(release): fail closed on duplicate NuGet versions in legacy publish`.

---

### CHG-B08 — Remove the dead AI-instructions copy workflow *(finding CICD-011, Low)*

**Objective.** Remove a broken, write-capable workflow.

**Target files.** `.github/workflows/copy-ai-assistant-instructions.yml`.

**Current state.** Triggers on `push` touching `ai-assistant-instructions.md`, which no longer exists in the repo (content now lives in `AGENTS.md`/`CLAUDE.md`/`.github/copilot-instructions.md`). If it ever ran, `cp ai-assistant-instructions.md …` would fail. It holds `contents: write` and pushes directly to `main`.

**Required change.** Delete the workflow file. If the sync intent is still desired, replace it with a workflow that reads the real source of truth and pushes only on an actual diff; otherwise removal is sufficient because `AGENTS.md`/`CLAUDE.md`/`.github/copilot-instructions.md` are already kept in sync manually per the baseline.

**Implementation notes.** Prefer deletion; do not resurrect a path-triggered self-push without a clear source file.

**Constraints.** None.

**Dependencies.** None.

**Risks.** None functional (workflow is inert). Removes an unnecessary write surface on `main`.

**Validation.** `actionlint -no-color` clean; repo has no reference to `ai-assistant-instructions.md`.

**Commit type.** `ci: remove dead AI-instructions sync workflow`.

---

### CHG-B09 — Align internal action versions *(finding CICD-012, Low)*

**Objective.** Remove chatter between action versions used by composite actions vs reusable workflows.

**Target files.** `Github/initialize-dotnet/action.yml:21,26`; `Github/verify/action.yml:21`; `Github/package-release/action.yml:16`; `Github/dapr-init/action.yml:14`. Cross-check every other `uses:` for consistency.

**Current state.** Composite actions pin `actions/setup-dotnet@26b0ec14… # v5.4.0` while reusable workflows pin `@a98b568… # v6.0.0` (`domain-ci.yml:137`, `domain-release.yml:146`). `dapr/setup-dapr@8d98… # v2` carries a floating major in its version comment instead of an exact version.

**Required change.**
1. Bump the `setup-dotnet` pins in the composite actions to the same SHA/version used by the reusable workflows (`a98b568… # v6.0.0`). Verify the SHA maps to the upstream `v6.0.0` tag before committing.
2. Replace the `# v2` comment on `dapr/setup-dapr` with the exact version the SHA corresponds to (resolve the SHA `8d980918fd43a2765c143ce7f687665b2d46a6b9` to its precise release tag and use it).

**Implementation notes.** Do not change SHAs to arbitrary "latest"; only align to versions already vetted elsewhere in the repo, or to the exact upstream release the current SHA points to. Reference current upstream stable majors: `checkout` v7.x, `setup-dotnet` v6.0.0, `setup-node` v7.x, `cache` v6.1.0, `upload-artifact` v7.0.1.

**Constraints.** Keep every third-party `uses:` pinned to full SHA + version comment (the repo's contract).

**Dependencies.** Overlaps with CHG-B02/CHG-B05 which also touch action pins — reconcile so all files end on one consistent set.

**Risks.** A version bump could change behavior; `setup-dotnet` v5→v6 is low-risk. Validate the build still runs.

**Validation.** `actionlint -no-color` clean; the `initialize-dotnet`/`verify` actions still resolve the SDK from `global.json`.

**Commit type.** `ci: align setup-dotnet pins to v6.0.0 across shared actions`.

---

### CHG-B10 — Documentation and dead-file cleanup cluster *(finding CICD-013, Low)*

**Objective.** Fix documentation that contradicts the implementation and remove dead references that mislead maintainers or break generated modules.

**Target files (each is an independent sub-edit; group into one focused `docs:`/`fix:` PR or a few small commits):**

1. `.github/workflows/ci-cd-standards.md:136-137` — reword the NuGet-audit rule; it currently reads "exclude the audit advisory codes (`NU1901`–`NU1904`) **from** `WarningsNotAsErrors`", which is the inverse of the implementation (`Hexalith.Build.props:33` **adds** them to `WarningsNotAsErrors`). Reword to "exclude … from warnings-as-errors **by adding them to** `WarningsNotAsErrors`".
2. `Github/publish-azure-container-app/README.md:7` and `Github/publish-container-to-registry/README.md:26` — stop describing mutable `@master` references; the actions pin SHAs. *(If CHG-B04 removes these actions, delete these READMEs instead and skip this sub-edit.)*
3. `Github/create-release/README.md:23`, `Github/package-release/README.md:33,36` — reconcile the documented `@v6`/`@v5` with the real SHA pins (state the pinned version explicitly).
4. `Hexalith.Builds.slnx` — remove dead virtual references to files that no longer exist (`Github/build-packages`, `Github/publish-packages`, `Github/version`, `.clinerules`, `.cursorrules`, `ai-assistant-instructions.md`, `ai-commit-prompt.md`, root `Environment.Build.props`, `Hexalith.Version.props`); optionally add the now-missing real folders. The six `.csproj` project references are correct and must be preserved.
5. `DEVELOPMENT.md:42-52` — add `revert` to the commit-type table (it is allowed by `commitlint.config.mjs:10`); reconcile the "50/72 character" guidance with the enforced `header-max-length: 200` / `body-max-line-length: 200`, either by tightening the config or by reframing the doc numbers as recommendations.
6. `Hexalith.Package.props:24` — replace the Azure-Pipelines-only `$(Date:yyyyMMddHHmmss)` (invalid in MSBuild) with `$([System.DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))` so the local non-Release version suffix is actually produced.
7. `Samples/Module.Directory.Build.props` — add `<ProjectRoot>$(MSBuildThisFileDirectory)</ProjectRoot>` so a module generated from the template does not attempt to pack `<module>/references/README.md`.
8. `stylecop.json:6` — restore the corrupted documentation URL (`…/master/MyNewModuleation/EnableConfiguration.md` → `…/master/documentation/EnableConfiguration.md`).

**Implementation notes.** Sub-edit 6 changes real build behavior (local prerelease suffix) and sub-edit 7 fixes a real consumer-template defect — treat those as `fix:`; the rest are `docs:`. Verify sub-edit 6 with a local non-Release build to see the suffix appear.

**Constraints.** Do not alter the enforced-value side of item 5 without deciding intentionally (tighten config vs relax doc) — pick one and make doc and config agree.

**Dependencies.** Item 4 overlaps CHG-B04 (slnx). Sequence CHG-B04 first if it removes the legacy actions.

**Risks.** Low. Item 6/7 touch MSBuild; validate a sample pack.

**Validation.** Build a sample/prerelease locally to confirm items 6–7; `actionlint`/tests unaffected.

**Commit type(s).** `docs: …` for 1–5, 8; `fix(build): …` for 6, 7.

---

### CHG-B11 — Supply-chain hardening opportunities *(audit opportunities, optional)*

**Objective.** Raise reproducibility and reduce dependency noise/risk without changing the release contract.

**Target files.** `Directory.Build.props` or `Props/Directory.Packages.props`; `.github/dependabot.yml`; `package.json`/`package-lock.json`.

**Required change (each independent, opt-in):**
1. Enable central-package transitive pinning: set `CentralPackageTransitivePinningEnabled=true` and evaluate whether to commit `packages.lock.json` + `RestoreLockedMode=true` **for the application/tool projects only** (not for library packages — library lock files are not honored by consumers). Do not force this on libraries.
2. Resolve the npm advisories surfaced by `npm audit --package-lock-only` (1 high `brace-expansion` GHSA-mh99-v99m-4gvg, 8 moderate `tar`) via `npm audit fix` **without** a breaking downgrade of semantic-release; if the only fix is a breaking semantic-release downgrade, leave it and document the residual (dev/release-only dependencies).
3. Add `groups:` to `.github/dependabot.yml` to batch minor/patch bumps per ecosystem and cut PR noise.

**Constraints.** Item 1 must not regress restore for consumers; test a full restore/build. Item 2 must not weaken the pinned, provenance-verified release toolchain.

**Dependencies.** Item 3 builds on CHG-B03.

**Risks.** `RestoreLockedMode` will fail builds when the lock drifts — that is the point, but it adds a maintenance step; scope it narrowly.

**Validation.** `dotnet restore`/`build -c Release` clean with the lock present; `npm ci` clean; `npm audit --package-lock-only` improved.

**Commit type.** `build(deps): …` / `ci(deps): …`.

---

### CHG-B12 — Provenance/attestations and OIDC publishing *(audit opportunities, design task — defer unless prioritized)*

> **Superseded in part by BUILD-REL-1.** Track 1 (artifact attestations) is
> implemented as the opt-in governed release mode: `domain-release.yml` gained a
> `governed-release` job that signs a candidate, runs
> `actions/attest-build-provenance` over those exact `.nupkg` bytes before any
> publication side effect, and carries `id-token: write` /
> `attestations: write` on that job alone. Track 2 (NuGet.org Trusted Publishing
> via OIDC) remains open and still requires its own design pass; governed mode
> continues to publish with `NUGET_API_KEY`.

**Objective.** Move toward SLSA Build L3 provenance and eliminate long-lived publishing credentials.

**Scope.** This is a larger design change, not a mechanical edit. Two independent tracks:
1. **Artifact attestations:** add `actions/attest-build-provenance` (permissions `id-token: write`, `attestations: write`, `contents: read`; plus `packages: write` for container subjects) to the NuGet-pack and container-publish steps in `domain-release.yml`/`publish-containers`. Because this repo is public and its release runs through reusable workflows, SLSA Build L3 is attainable.
2. **NuGet.org Trusted Publishing (OIDC):** replace the long-lived `NUGET_API_KEY` with a short-lived token via `NuGet/login@v1` + `id-token: write`, gated by a trusted-publishing policy (owner/repo/workflow/environment).

**Constraints.** Both interact with the protected-environment release contract and with consumer callers; design and stage carefully, validate on a test module first, and keep the existing fail-closed preflight intact. Treat this as a separate initiative with its own plan; do not attempt inline with B01–B10.

**Validation.** `gh attestation verify` on a produced artifact; a test release using OIDC yields a valid short-lived token and provenance.

---

## 3b. Change sets — repository **tenants**

### CHG-T01 — Repair the commitlint caller *(finding CICD-005, High for the consumer)*

**Objective.** Make the commitlint check pass on pull requests and cover post-open title edits.

**Target files.** `tenants/.github/workflows/commitlint.yml`.

**Current state.** No `types:` on the `pull_request` trigger (so `edited` is not covered) and the reusable `commitlint.yml@main` is called **without** `pull-request-title`. Since the builds hardening of 2026-07-20, the reusable workflow fails "pull-request-title is required for pull_request validation" — so the check fails on every Tenants PR.

**Required change.** Align to the EventStore pattern:
```yaml
on:
  pull_request:
    branches: [main]
    types: [opened, synchronize, reopened, edited]
  push:
    branches: [main]
# …
    with:
      pull-request-title: ${{ github.event.pull_request.title }}
```
Keep the existing `push` trigger and `contents: read` permissions.

**Implementation notes.** Mirror `eventstore/.github/workflows/commitlint.yml` exactly for the caller shape.

**Constraints.** Do not pin the reusable `commitlint.yml` to a SHA (routine, non-publication references use `@main` per contract).

**Dependencies.** None — this is safe to land immediately and independently of the release rework.

**Risks.** None; it restores intended behavior.

**Validation.** Open a Tenants PR with a valid Conventional-Commit title and confirm the commitlint check passes; confirm editing the title re-triggers the check.

**Commit type.** `ci: pass PR title and cover title edits in commitlint`.

---

### CHG-T02 — Rebuild the release caller on the current contract *(finding CICD-006, Medium)*

**Objective.** Replace the old-generation, mutable, auto-triggered release caller with the hardened, operator-driven pattern, and supply the missing package manifest.

**Target files.** `tenants/.github/workflows/release.yml`; new `tenants/tools/release-packages.json`.

**Current state.** `release.yml` triggers on `workflow_run` (auto-publish after CI), calls `domain-release.yml@main` (mutable ref on a publication path), passes no `builds-execution-sha` (so the call currently fails closed on the missing required input), has no caller-owned `verify-source` job, no `expected-package-count`, a concurrency group `release-${{ github.ref }}` instead of the literal `release-production`, and no `actions: read` permission. The reusable workflow's default `package-manifest: tools/release-packages.json` points to a file that does not exist in Tenants.

**Required change.** Port the EventStore release caller (`eventstore/.github/workflows/release.yml`) to Tenants:
1. `on: workflow_dispatch` (remove `workflow_run` auto-publish).
2. Add a caller-owned `verify-source` job that fails closed unless the dispatch ref is `refs/heads/main` and the dispatch SHA equals the live `main` tip and an exact-SHA successful push CI run of Tenants' `ci.yml` exists — copy EventStore's job.
3. `concurrency: { group: release-production, cancel-in-progress: false }`.
4. Call `Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@<reviewed full SHA ≥ 5041efe>` and pass the **identical** literal as `builds-execution-sha`.
5. Pass `environment-name: production`, `publish-containers: true`, `container-projects` (as today), and `expected-package-count: <N>` where **N is the count of Tenants' publishable NuGet package IDs** (determine it — see notes).
6. Grant the release job `actions: read` in addition to `contents/issues/pull-requests: write`.
7. Keep secrets explicit (`NUGET_API_KEY`, `HEXALITH_ZOT_USERNAME`, `HEXALITH_ZOT_API_KEY`); never `secrets: inherit`.
8. Create `tenants/tools/release-packages.json` following EventStore's schema (`{ "packages": [ { "id", "project" }, … ] }`), enumerating every packable Tenants project.

**Implementation notes.** To compute `expected-package-count` and build the manifest, enumerate Tenants' `src/**/*.csproj` that are packable (produce NuGet packages) — do not guess; derive from the solution and project properties. The count in the caller must equal the number of package IDs in the manifest (the value is caller-declared precisely so a drift fails closed).

**Constraints.** This is a publication path: the `uses:` ref and `builds-execution-sha` must be the same reviewed full SHA, and that SHA must be a real ancestor of `Hexalith.Builds` `main` that already contains the CHG-B* release fixes. Therefore this task **depends on** a merged, released builds state (see §4).

**Dependencies.** Depends on **CHG-B01** (release path fixed) and a chosen builds SHA ≥ `5041efe` that includes the relevant fixes; coordinate with CHG-E01 to use a consistent SHA.

**Risks.** Getting the package inventory wrong makes the release fail closed (safe but blocking). Verify the manifest against the actual packable projects. Removing `workflow_run` means releases become intentional dispatches — this is the desired behavior; communicate the operational change.

**Validation.** `actionlint -no-color` clean; `git -C ../builds cat-file -t <sha>` returns `commit` and `git -C ../builds merge-base --is-ancestor <sha> main` succeeds; a dispatched dry Tenants release reaches the protected environment approval and the package-count validation passes.

**Commit type.** `ci: rebuild Tenants release on hardened dispatch contract`.

---

## 3c. Change sets — repository **eventstore**

### CHG-E01 — Re-pin the release execution SHA and declare the package count *(finding CICD-007, Medium)*

**Objective.** Restore the fail-closed package-count control by re-pinning to a builds revision that includes it.

**Target files.** `eventstore/.github/workflows/release.yml:79,85`, plus the `with:` block.

**Current state.** Pinned to `domain-release.yml@cf04c419…` (2026-07-20) with the same literal in `builds-execution-sha`. That SHA predates `expected-package-count` (added in builds `5041efe`, 2026-07-26). `release.yml` sets `publish-containers: true` but declares no `expected-package-count`. The manifest `tools/release-packages.json` declares **14** packages.

**Required change.**
1. Re-pin both `uses: …@<reviewed full SHA ≥ 5041efe>` and `builds-execution-sha: <same SHA>` to one reviewed builds commit that contains the CHG-B release fixes.
2. Add `expected-package-count: 14` to the `with:` block.

**Implementation notes.** Confirm 14 by counting entries in `eventstore/tools/release-packages.json` (`packages` array) at implementation time — if the inventory changed, use the current count and keep it in sync with the manifest.

**Constraints.** The two SHA occurrences must be byte-identical; the SHA must be a full 40-hex commit that is an ancestor of builds `main`.

**Dependencies.** Depends on the builds fixes being merged and a chosen SHA; use the **same** builds SHA as CHG-T02 for consistency.

**Risks.** If the count and manifest disagree, the release fails closed (safe). Low risk.

**Validation.** `actionlint -no-color` clean; `git -C ../builds cat-file -t <sha>` = `commit`; `git -C ../builds merge-base --is-ancestor <sha> main` succeeds; a dispatched dry release passes the count validation.

**Commit type.** `ci: re-pin release toolchain and declare package count`.

---

### CHG-E02 — Align setup-dotnet in the advisory workflow *(finding CICD-012, Low, consumer side)*

**Objective.** Remove toolchain drift between EventStore CI jobs.

**Target files.** `eventstore/.github/workflows/advisory-tests.yml` (the `setup-dotnet` pin, ~line 42).

**Current state.** `advisory-tests.yml` pins `setup-dotnet@…# v5.4.0` while `ci.yml` uses `# v6.0.0`.

**Required change.** Bump the advisory workflow's `setup-dotnet` pin to the same SHA/version used by `ci.yml` (`# v6.0.0`), keeping full-SHA + version-comment format.

**Constraints.** Verify the SHA maps to the intended upstream tag.

**Dependencies.** None.

**Risks.** Negligible.

**Validation.** `actionlint -no-color` clean.

**Commit type.** `ci: align setup-dotnet to v6.0.0 in advisory tests`.

---

### CHG-E03 — Disable credential persistence on remaining checkouts *(consumer finding, Low)*

**Objective.** Keep the `GITHUB_TOKEN` out of `.git/config` during jobs that run arbitrary tests/tooling.

**Target files.** `eventstore/.github/workflows/advisory-tests.yml` (~lines 33–36), `eventstore/.github/workflows/integration.yml` (~lines 32–35), and the tenants-source-mode job checkout in `eventstore/.github/workflows/ci.yml` (~lines 70–73).

**Current state.** `ci.yml`'s primary checkout sets `persist-credentials: false`, but the listed checkouts leave the default (`true`).

**Required change.** Add `persist-credentials: false` to those `actions/checkout` steps, unless a subsequent step in the same job genuinely needs the persisted credential (verify each; these jobs run tests, so they should not).

**Constraints.** Do not disable persistence on a step that later performs an authenticated Git push.

**Dependencies.** None.

**Risks.** If a job actually relied on the token in `.git/config`, it would break — verify each job first.

**Validation.** `actionlint -no-color` clean; a CI run of the affected workflows still succeeds.

**Commit type.** `ci: stop persisting credentials in test checkouts`.

---

## 4. Cross-repository sequencing and dependencies

Because the consumer re-pins must point at a reviewed **builds** commit that already contains the release-path fixes, follow this order:

1. **builds first.** Land, in order: CHG-B01 (unblock release) → CHG-B02, CHG-B03, CHG-B04, CHG-B05, CHG-B06, CHG-B07, CHG-B08, CHG-B09, CHG-B10. Then, if in scope, CHG-B11. Merge to `main` and confirm a **green Release** (verifies CHG-B01) so a releasable, reviewed SHA exists.
2. **Choose one canonical builds SHA** `S` (≥ the merge of the above, an ancestor of `main`) to be used by both consumers.
3. **eventstore:** CHG-E01 (pin to `S`, count 14), then CHG-E02, CHG-E03 (independent, can go anytime).
4. **tenants:** CHG-T01 (independent — can land first, immediately), then CHG-T02 (pin to `S`, create manifest, declare count).

CHG-T01 and CHG-E02/E03 have no cross-repo dependency and may be implemented immediately in parallel. Everything that touches a publication `uses:`/`builds-execution-sha` (CHG-T02, CHG-E01) must wait for step 2.

## 5. Risk register (cross-cutting)

- **New CI checks are not auto-enforced.** After CHG-B02 (and the consumers' `ci.yml`), the branch ruleset on each `main` still lists only the previously required checks. Adding build/test/CodeQL to the required set, requiring ≥1 PR review, removing "always" bypasses, enabling `sha_pinning_required`, disabling `can_approve_pull_request_reviews`, enabling Dependabot security updates, and setting `prevent_self_review` on the `production` environment are **remote configuration** changes the repository owner must make. They are out of file-edit scope; enumerate them in the relevant PR descriptions as follow-ups.
- **Reusable-workflow default changes propagate.** CHG-B05's `domain-release.yml` Node default change reaches consumers only when they re-pin. Keep it an overridable input.
- **Package-inventory drift fails closed.** CHG-T02/CHG-E01 counts must match the manifests exactly; a mismatch blocks release by design.
- **Legacy-action removal (CHG-B04)** is safe only after a repo-wide (ideally org-wide) reference check.

## 6. Explicitly out of scope

- The six untracked `test/fixtures/evidence/negative/*.expected.json` files in **builds** (do not stage/commit/delete).
- Any edit inside a `references/` submodule.
- Live publishing, registry logins, deployments, or running semantic-release.
- Remote GitHub settings (rulesets, environment protection, org secrets) — recorded as owner follow-ups in §5, not implemented via file edits.
- CHG-B12 (attestations/OIDC) unless separately prioritized; it requires its own design pass.

## 7. Definition of done

- **builds:** `actionlint -no-color` clean; 131/131 .NET tests, 55/55 Python tests, and all PowerShell validators pass; a `workflow_dispatch` Release on `main` succeeds; Dependabot shows three ecosystems; no `${{ inputs.* }}` remains inside any `run:`; the dead workflow and dead slnx/doc references are gone; a self-CI pipeline runs on PRs.
- **eventstore:** release caller pinned to the canonical builds SHA with `expected-package-count: 14`; no action-version drift; test checkouts do not persist credentials; `actionlint` clean.
- **tenants:** commitlint check passes on PRs (with `edited` coverage); release caller is dispatch-driven with `verify-source`, pinned execution SHA, `expected-package-count`, `release-production` concurrency, and `actions: read`; `tools/release-packages.json` exists and matches the declared count; `actionlint` clean.
- Every commit uses a valid non-`chore` Conventional Commit type and passes commitlint without `--no-verify`.
