---
story: '3-3'
created: '2026-11-10'
status: ready-for-dev
planning_only: true
baseline_revision: f39f45c06b9ded0a473b96221c559e5443e41b1c
implementation_repository: references/Hexalith.Builds
implementation_baseline_revision: fa5f48734ca44ce63b55fc645f53c9c6bdccaa1c
---

# Release create-mode plan

Story 3-3 adds version, audited build and marked draft creation to the existing
reusable release. This delivery contains planning only. The canonical workflow
state and full acceptance contract live in the enclosing CiCd repository at
`_bmad-output/specs/spec-cicd/stories/3-3-create-mode-version-audited-build-marked-draft.md`.
CAP-12/14 and architecture AD-10/11/16/17/20 govern implementation.

## Required draft contract

Draft independently recomputes the highest stable reachable tag with
[`highest_reachable_stable_tag`](../Github/lib/release_state.py). From `v2.3.4`,
the complete accepted set is **`2.3.5`, `2.4.0`, `3.0.0`**. Reject jumps,
incorrect resets, equal/lower versions, prerelease/build metadata and leading
zeros. With no reachable tag, accept only `1.0.0`. This strict AD-17 creation
test is separate from AD-16's selection of an existing marked draft, which
requires a version above its base.

Create only after this run has uploaded its original `release` artifact.
Authenticate the artifact's ID, name, run, expiry and API SHA-256 digest;
verify the downloaded bytes. Read inventory as data from that artifact,
never from additional trusted build-job outputs. Relist drafts, reuse an
identical same-run marked draft, reject any other marked draft or conflicting
same-run metadata, and preserve unmarked drafts. Recheck the frozen SHA's entire
`.github/workflows/` tree against fresh `main` immediately before creating.
If different, fail with:

```text
workflows changed on main during the release, nothing released
```

The API request sets `draft=true`, `tag_name=v<version>` and
`target_commitish=<full-sha>`. The notes are followed by exactly these five
reserved trailers, each once; notes cannot inject additional reserved lines:

```text
hexalith-release: <full-sha>
hexalith-release-run: <run-id>
hexalith-release-artifact: <artifact-id> <sha256-digest>
hexalith-release-packages: <package-ids>
hexalith-release-containers: <container-names>
```

Use deterministic comma-separated identity lists and empty suffixes for empty
lists. Preserve the API `sha256:` digest prefix. Outputs are `publish`,
`version`, `sha`, `last-tag`, `draft-id`, `artifact-id`, `artifact-digest`,
`artifact-run`. Validate the returned draft before authorizing a handoff.
No tag, published release or main commit is created by these stages.

## Existing surfaces and boundaries

- [Reusable release](../.github/workflows/release.yml) currently contains
  resolve, CodeQL, nightly-hook and outcome. Create stages consume resolve's
  frozen SHA; CodeQL/nightly retain dispatch `github.sha`.
- [Outcome reducer](../Github/lib/release_outcome.py) already supports
  `no-version`, intentional skips, failed/cancelled precedence and validated
  publisher ownership. Wire these seams instead of adding outcome values.
- [Pipeline helpers](../Github/lib/ci_pipeline.py) provide config preparation,
  direct-submodule initialization and SDK solution membership. Their current
  `build_flags()` disables audit; the complete
  [prepare composite](../Github/prepare/action.yml) builds at dispatch SHA
  without audit. Neither can be used unchanged for the release build.
- [Shared props](../Hexalith.Build.props) exempt NU1903/4; override those
  exemptions in the new release path. Keep the trusted signed nuget.org-only
  [restore configuration](../NuGet.ci.Config) and `-noAutoResponse`.
- [SDK archive spike](../Tools/spikes/publish_spike.py) and
  [artifact/OCI controls](../Github/lib/publish_spike.py) prove the mechanism;
  preserve the evidence and generalize new production helpers without spike
  repository, platform, version or destination constants.
- All nine preceding stories are done. Preserve strict `ci_config`,
  direct-submodule preparation and the real sample. Prior unrelated CodeQL,
  spike-credential and Linux solution-path deferrals stay outside this story;
  use absolute SDK paths for new calls.

Version/build have `contents: read`, no secrets, no stored checkout credentials
and no cache. Draft has `contents: write`, `actions: read`, and only trusted
stdlib Python/git/API code from the workflow-owned action. Module payload,
notes and inventory are data. No third-party or product code runs with its
write token. Every job declares permissions. Actions/tools are pinned;
workflow expressions reach scripts through `env`. Only the thin direct npm
plugin adapter is JavaScript; runtime orchestration is Python >=3.12.

Preserve frozen legacy module-used runtime, root legacy npm release config,
the legacy `builds-release.yml`, production callers and signed-provenance
exclusion. Complete-mode recovery/deletion, publisher, tag creation, scheduler,
caller rewrite and 5-1's drift check are downstream. Complete mode skips
version/build and retains an explicit incomplete-stage failure with no
historical config/build or existing-draft mutation.

## Ordered implementation

1. Add `Github/lib/release_version.py` and
   `Github/release-version/{action.yml,invoke_plugins.mjs,package.json,package-lock.json}`.
   Directly invoke analyzer **13.0.1** and notes generator **14.1.1**, never
   semantic-release core or module release/npm config. Use an isolated trusted
   installation with `npm ci --ignore-scripts` and `npm audit signatures`.
   Analyze highest-reachable-tag through frozen SHA, or full tagless history.
   Module feat/fix/perf release, build/ci/build(deps) do not, and fix(deps)
   does. Builds releases every type except docs/test/style, with normal
   major/minor/patch severity and no prerelease. Guard the `release-rules`
   selector: `module` default, `builds` only for actual Builds, not sample.
   No releasable commits means explicit `no-version` and `nothing to release`.
2. Add `Github/lib/release_build.py` and `Github/release-build/action.yml`;
   parameterize only necessary low-level discovery in `ci_pipeline.py`, keeping
   CI defaults. Select the frozen source's SDK and evaluate solution projects
   in Release/package/version context. Packages use `IsPackable=true`;
   containers use `HexalithPublishContainer=true` and exclude tests. Read
   actual package/container identity and platform/RID properties, including
   imports and multitarget projects. Reject duplicate/conflicting identities.
   Build once, pack once without another restore/build, stamp actual version
   and repository commit; do not rerun tests/hooks/Windows preparation.
3. Enforce audited restore/build with trusted restore configuration,
   `-noAutoResponse`, `UseHexalithProjectReferences=false`,
   `-warnaserror:NU1900;NU1903;NU1904;NU1905`, `NuGetAudit=true`,
   `NuGetAuditMode=all`, `NuGetAuditLevel=low`. Remove inherited exemptions
   for those four codes without promoting low/moderate audit findings.
   Audit failure emits `vX.Y.Z blocked by a vulnerable package` and suppresses
   artifact/draft; preserve compiler/analyzer failures and original exit codes.
4. Add `Github/lib/oci_archive.py`. For SDK 10.0.403, publish each evaluated
   platform once with single-RID SDK publication and assemble only the index
   packaging. Preserve complete child manifests/configs/layers and
   colon-containing labels; output uncompressed `containers/*.tar` and
   validate the full graph before upload.
5. Write internal `release-manifest.json` with frozen SHA/version and package
   and container identity-to-relative-path lists. It also makes an empty
   payload uploadable: empty lists for `solution: none` or no selected
   projects, and no SDK/submodule work for `none`. Upload one `release`
   artifact containing manifest, `packages/*.nupkg`, `containers/*.tar`, kept
   90 days. Build outputs **only artifact ID/digest**. The manifest is data,
   not a new public schema; reject unsafe paths, duplicates and payload/list
   disagreement.
6. Add `Github/lib/release_draft.py` and `Github/release-draft/action.yml` for
   the independent version/artifact/draft/workflow checks above, safe
   authenticated API writes/downloads and returned-draft validation. Reuse
   existing bounded API/Git patterns and marked-draft validation. Do not
   execute downloaded payload or touch registry/NuGet/publish credentials.
7. Wire version/build/draft into `.github/workflows/release.yml`, expose the
   eight draft outputs, and update outcome integration in
   `Github/lib/release_outcome.py` / `Github/release-outcome/action.yml` only
   where needed. After all gates, no-version skips build/draft and writes one
   schema-valid `nothing` outcome. Successful validated draft enables the
   later publisher handoff and the reusable reporter uploads nothing. Never
   report `published` or `ready` for draft creation. Complete mode remains
   fail-closed until recovery lands. Production callers remain unchanged.
8. Add `Github/lib/tests/test_release_{version,build,draft,oci}.py`, extend
   existing outcome/adapter tests and add `test/fixtures/release/`. Cover
   every canonical matrix edge with real temporary histories, executed
   plugins/adapters, HTTP effects, real SDK audit/pack controls and preserved
   OCI graph bytes. Include one-step negatives, each required audit code,
   low/moderate controls, tagless/empty payloads, artifact tampering,
   same-run retry, competing markers and workflow races. No workflow-text
   assertions.
9. During implementation only, temporarily add
   `.github/workflows/release-create-probe.yml` and
   `Tools/ci/probe_release_create.py`, using disposable release fixtures and
   no publisher. Observe the real Actions job graph, no-version skips, failed
   audit, authenticated artifact handoff and persisted five-trailer draft.
   Keep production callers/switch unchanged, remove temporary probe files and
   clean up only identified probe drafts. Retain sanitized full source,
   run/attempt, artifact and draft observations in
   `docs/cicd-3-3-release-create-mode-results.md`; distinguish hosted evidence
   from deterministic local controls. Update `Tools/ci/README.md` with
   interfaces, permission union and downstream seams.

## Verification and planning delivery

Future implementation runs focused release tests, retained full Python suites
on Python 3.12 and runner Python, pinned online workflow lint, committed-source
public API comparison and frozen-byte checks. Use the exact commands in the
canonical story and [quality guide](../Tools/ci/README.md). Validate pack
version/repository commit and all OCI bytes, not just helper return values.
A local adapter harness does not establish Actions scheduling; retain hosted
job conclusions, downloaded artifact metadata and the persisted draft response.

This invocation validates document frontmatter, full Git baselines, existing
context links, unchecked runtime tasks, documentation-only scope and whitespace.
Validate the exact commit message using pinned commitlint 21.2.4, fetch and
inspect divergence, then commit/push this plan only in Builds. Stage the
canonical story and updated pushed gitlink together in CiCd without committing
or pushing CiCd. Halt at `ready-for-dev`; no runtime implementation, probe,
test release or publication is performed by the planning invocation.
