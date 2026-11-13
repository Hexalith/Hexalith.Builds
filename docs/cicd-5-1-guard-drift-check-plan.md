---
story: '5-1'
created: '2026-11-13'
status: ready-for-dev
planning_only: true
baseline_revision: bec78e64087aa4e3668210f47535afcad55258df
implementation_repository: references/Hexalith.Builds
implementation_baseline_revision: 7917c50e21ec5bae15677c9ac3559623e432756e
implementation_integration_revision: 902634577caa66da5ef6364fd3909d136e3e8676
---

# Guard drift detection and release drift check plan

Story 5-1 supplies the read-only CAP-8 comparison integration under AD-4/8/17/19.
This delivery contains planning documentation only. The canonical story in the
enclosing CiCd repository is
`_bmad-output/specs/spec-cicd/stories/5-1-guard-drift-detection-and-the-release-drift-check.md`.
It holds the complete intent contract, Code Map, matrix, tasks and acceptance
criteria for implementation.

## Implementation

1. Add `Github/lib/expected_state.py` for anonymous shallow sparse acquisition of
   CiCd's `delivered/<Repo>` branch, pinned Git snapshots, optional delivered-SHA
   ancestry proof and trusted Builds tag objects. Reuse
   `perimeter_config.load_bytes` and `perimeter.compare`; do not copy ownership
   paths, schema validation or entry-kind semantics. Supplied release SHAs must
   be the live branch tip or an ancestor proven by branch history; deepen as
   needed, never fetch an arbitrary bare SHA. Missing expected state fails closed.
2. Add `Github/lib/guard.py` and `Github/guard/action.yml`, executing trusted
   stdlib code through `python3 -I`. Compare committed owned content first, then
   load matching CI JSON through `ci_config.load_config` and check every enabled
   hook/job's regular committed workspace-root action metadata. Report owned
   differences and the fixed ownership message. Supply an internal drift flag
   for later restore without authorizing repair on unavailable expected state
   or action-only errors.
3. Replace the no-op in `.github/workflows/ci.yml` with `$/Github/guard` after
   checkout and before prepare/config parsing/dependency initialization. Keep
   contents read, existing outputs, SDK/build/test/hook ordering and Windows
   delegation. Keep separate commitlint coverage until commitlint moves into
   the guard; correct the placeholder's premature attribution.
4. Replace the sample's empty perimeter at
   `Samples/Hexalith.Sample/expected-source/rendered/Hexalith.Sample.perimeter.json`
   with a nonempty v1 whole-file entry for `.github/hexalith-ci.json`, using its
   existing expected blob. The sample has no nested Builds gitlink. Builds root
   skips all delivered lookup; only its sample call uses the fixture. Actual
   owned paths resolve under `module-root`; actions stay at workspace root.
   Reject fixture controls outside Builds and unsafe/incomplete pairs.
5. Integrate the same comparison in `Github/lib/release_state.py`, with optional
   fixture expected-source forwarding through `Github/release-resolve/action.yml`
   and `.github/workflows/release.yml`. Select/freeze mode and SHA first. Create
   mode compares before dispatch config parsing, switch or green CI. Retain a
   completed drift verdict, emit valid dispatch security settings, then fail
   drift; ownership wins over simultaneous config failure. Unavailable expected
   state fails before parsing. Complete mode performs no expected-state lookup,
   historical config load or CI check and keeps current-dispatch security
   settings. Builds root release bypasses drift entirely.
6. Add real-Git/CLI/composite regressions in
   `Github/lib/tests/test_expected_state.py`, `test_guard.py` and
   `test_guard_adapter.py`; extend release state/adapter/outcome tests. Exercise
   all nine perimeter kinds, malformed actual config, missing expected state,
   shallow ancestry, tagged gitlinks, dirty/sparse trees, root/sample boundaries,
   action availability, failure ordering and unchanged complete mode. Observe
   execution/results, never workflow source text.
7. Update `Tools/ci/README.md`, `Samples/Hexalith.Sample/README.md` and add later
   implementation evidence in `docs/cicd-5-1-guard-drift-check-results.md`.

## Boundaries and validation

The comparison reads Git objects and never writes module content/index, runs
module code or trusts its checked-out Builds pointer. Anonymous Git acquisition
must not inherit tokens, executable configuration, credential rewrites, hooks,
smudge/LFS execution or submodule recursion. A downloaded action directory need
not have `.git`, so tag peeling uses separately acquired trusted Builds objects.

Restore, delivery/ref writes, local check, action/audit/commitlint lint, module
adoption and production release/administration are outside this story. Preserve
frozen AD-20 runtime, schema/dependency/SDK pins, public APIs, caller permissions,
dispatch security behavior and original-artifact publication/recovery.

During implementation run focused perimeter regressions, the existing locked
`Tools/ci/run_python_tests.py` under Python 3.12 and runner Python,
`Tools/ci/lint_workflows.py`, `Tools/ci/check_public_api.py`, whitespace/frozen-file
checks, and the exact implementation SHA's normal Builds CI. Retain actual
sample/Linux/Windows results and run identities separately from local controls.
No runtime, test or schema change and no implementation verification is claimed
by this planning-only delivery.

Planning checks did validate the proposed nonempty sample manifest with the
existing strict loader and compare its actual/expected Git blobs with zero
drift. Both enabled sample actions are regular committed workspace-root files.
Before delivery, main fast-forwarded to the recorded integration revision;
its unrelated lifecycle tests/README changes do not alter the investigated
CI/perimeter/release surfaces and are not authored story changes.

## Planning delivery

Commit and push this plan in Builds after validating its exact message with the
repository's pinned commitlint CLI. Stage the canonical story and updated Builds
pointer together in CiCd; leave that superproject commit/push to the caller.
Halt at `ready-for-dev` before implementation.
