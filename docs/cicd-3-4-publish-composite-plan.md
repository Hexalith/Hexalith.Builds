---
story: '3-4'
created: '2026-11-11'
status: ready-for-dev
planning_only: true
baseline_revision: 0d29ed715475567155cee2d6f078b7a4bc5faa6a
implementation_repository: references/Hexalith.Builds
implementation_baseline_revision: a0b95ba2b8e0a94cdcd090c1d4ec44eabb9184e6
---

# Publish composite plan

Story 3-4 adds the AD-17 production publisher for CAP-12/13. This delivery
contains planning only. The canonical workflow state and acceptance contract
live in the enclosing CiCd repository at
`_bmad-output/specs/spec-cicd/stories/3-4-the-publish-composite.md`.

## Publication contract

The new `Github/publish/action.yml` takes the existing eight-field handoff:
`publish`, `version`, `sha`, `last-tag`, `draft-id`, `artifact-id`,
`artifact-digest`, `artifact-run`, plus NuGet user and registry push pair.
Caller owns the `hexalith-release` environment and grants `contents: write`,
`actions: read`, `id-token: write`. Execute trusted action-path code without
checkout, source/config execution or rebuilding. Place original artifact data
and empty command working directory outside `GITHUB_WORKSPACE`.

Authenticate the bot-marked draft and all five trailers, full source SHA,
stable version and captured last-tag ancestry; authenticate the original
producer run's caller workflow path, dispatch on main and source SHA, artifact
ID/name/run/expiry/API digest, then downloaded bytes. Independently validate
manifest, safe exact member set, package version/repository commit and complete
OCI graphs. All failures and uncertain reads fail closed before publication.

**Before any NuGet or registry write, reject an existing `v<version>` tag
that resolves anywhere other than the built SHA.** Peel annotated tags as well
as lightweight tags; non-commit targets fail. An absent or correct-target tag
is accepted for retry. The exact failure message is:

```text
tag vX.Y.Z does not point at the built commit
```

Never move/delete/replace that tag. Preflight every container destination
version root before the first package/image write, then recheck each directly
before copying. Preserve a conflicting destination and fail.

After preflight, use nested
`NuGet/login@8d196754b4036150537f80ac539e15c2f1028841` (v1.2.0), as established
by story 3-1. Push original packages with `dotnet nuget push`,
`--source https://api.nuget.org/v3/index.json` and `--skip-duplicate`, from an
empty directory so module NuGet configuration cannot affect publication.
No public API-key fallback input is added.

Copy original uncompressed OCI archives with official Skopeo 1.22.3,
`copy --all --preserve-digests`. Reuse the exact image digest and reported
source commit in [publish-tools.json](../Tools/spikes/publish-tools.json).
Verify reported version/source before copying and every remote
index/child/config/layer digest and size afterward. Keep authfile private,
credentials endpoint-bound and operations bounded; remove auth material on
every exit. Never relabel, flatten or rebuild the payload.

Only after package/image success, refresh main and recheck source ancestry
and the complete `.github/workflows/` trees. Different workflow trees yield
successful `ready`, leaving the marked draft and tag uncreated for the nightly
App. Other API/auth/transport failures remain failures. Equal trees permit one
draft PATCH containing `tag_name`, full `target_commitish`, `draft: false`.
GitHub creates the tag last. Revalidate live draft/tag before PATCH and verify
the returned published release and independently fetched peeled tag afterward.
Never emit `published` for a mismatched response/tag.

Publisher owns exactly one uploaded `hexalith-release-outcome` artifact with
`overwrite: true`. Reuse existing schema/helpers for `published`, `ready` or
`failed`; `ready` includes `draftId` and captured `lastTag`. An `always()`
finalizer must cover nested login and later command failures, not only Python
controller exceptions. Initialize failure reporting with trusted dispatch SHA
until the built SHA/version validates, including malformed handoff failures.
Preserve reusable reporter's validated handoff and
failure-first semantics. Empty inventories permit a GitHub-only release.
Partial retry reuses the identical original artifact and duplicate-safe pushes.

## Existing source and constraints

- [Reusable release](../.github/workflows/release.yml) exports all eight fields;
  [outcome reducer](../Github/lib/release_outcome.py) already relinquishes its
  upload only after successful, identity-matching handoff.
- [Draft helpers](../Github/lib/release_draft.py) provide bounded authenticated
  API/download operations, artifact metadata/inventory validation and marked
  draft validation. Inventory currently returns identities only; factor safe
  access to validated original payload files without weakening create behavior.
- [Release state](../Github/lib/release_state.py) provides repository-bound API
  and bare Git operations. Reuse ancestry/tag handling, but do not recompute the
  captured last reachable tag. Factor workflow comparison if necessary while
  preserving create-stage failure when workflows differ.
- [OCI validation](../Github/lib/oci_archive.py) is generalized production
  code. [Spike publisher](../Github/lib/publish_spike.py) proves registry/copy
  behavior but has fixture-specific constants; preserve spike evidence and
  generalize new production code.
- [Draft tests](../Github/lib/tests/test_release_draft.py) execute composite
  mappings through the existing expression harness, with real temporary Git
  histories and HTTP/tool effects. Extend this pattern; no workflow-text tests.

All prior stories are done. Their reviews deliberately assign wrong-target
tag refusal to this publisher. Story 3-1 additionally requires all-image
preflight before NuGet. Signed build provenance and `attestations: write` remain
excluded; OCI labels are metadata.

Preserve AD-20 frozen legacy runtime, root npm release configuration,
`builds-release.yml`, production callers and switches. Complete-mode
orchestration/recovery/deletion, scheduler/App publication, caller rewrite,
5-2 audit-bypass lint and prior unrelated deferrals remain downstream.
The publisher can accept an original-run handoff without implementing recovery.

## Ordered implementation

1. Add `Github/lib/release_publish.py`: handoff/draft/run/artifact authentication,
   validated payload materialization, live Git-tag preflight and finalization.
   Make minimal reusable changes to `release_draft.py`/`release_state.py` only
   where required; preserve their existing behavior and safe download limits.
2. Add `Github/lib/oci_publish.py`: generic pinned-tool/registry operations,
   all-image preflight, immediate per-image recheck and complete remote graph
   verification using `oci_archive.py` and existing tool pins.
3. Add `Github/publish/action.yml`: preflight, pinned nested login, original
   package pushes, OCI publication, final draft publication or ready, and
   always-run schema validation/outcome upload. Use env for expressions,
   trusted action-path Python and explicit credentials; no checkout or cache.
4. Add `Github/lib/tests/test_release_publish.py`, `test_oci_publish.py` and
   release fixtures; extend existing draft/OCI/outcome regressions as needed.
   Execute actual composite inputs/finalizer and assert observable operations:
   wrong lightweight/annotated/non-commit tag gives zero writes; correct-target
   retry; corrupt/mismatched original data; any image conflict before NuGet;
   late image race; partial retry; empty inventory; login/push/API failure;
   product-only main advance; workflow difference; post-PATCH tag mismatch.
5. Add `Github/publish/README.md`, update `Tools/ci/README.md`, and retain
   `docs/cicd-3-4-publish-composite-results.md`. During implementation only,
   temporary `release-publish-probe.yml` and `Tools/ci/probe_release_publish.py`
   exercise a real remote composite with isolated identities, caller
   environment, original artifact and poisoned workspace. Observe actual
   NuGet/OCI bytes, GitHub release/tag and downloaded outcome. Remove temporary
   controls and clean up identified disposable resources within authorization;
   distinguish hosted evidence from local negative controls.

## Verification and planning delivery

Future implementation runs focused publisher/OCI tests, retained full Python
suites under 3.12 and runner Python, authenticated pinned workflow lint, and
committed-source public API comparison. Commands and full Given/When/Then
acceptance are in the canonical story and [quality guide](../Tools/ci/README.md).
Preserve frozen bytes. Local execution controls do not establish hosted Actions
behavior; retain sanitized full source/run/artifact/release/tag observations.

This invocation checks planning frontmatter, full canonical Git baselines,
existing source links, unchecked implementation tasks, document-only scope and
whitespace. Validate the exact full commit message with pinned commitlint
21.2.4, fetch/check divergence, commit/push this Builds plan only, and verify the
remote full SHA. Stage the canonical story with the updated Builds gitlink in
CiCd without committing/pushing CiCd. Halt at `ready-for-dev`; no runtime code,
live probe, test release or publication is performed during this invocation.
