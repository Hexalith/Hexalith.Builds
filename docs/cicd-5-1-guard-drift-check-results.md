---
story: '5-1'
created: '2026-11-13'
status: ready-for-review
implementation_baseline_revision: f01c3ab70f7e7401b3b73217983955f7771a1465
implementation_integration_revision: 05a75b626fb73c6e386d16c41e885f193d3cff16
implementation_revision: 0f97426edb6f88ee6e534d95ca9029f7e0b20311
---

# Guard and release drift-check results

The canonical CiCd story is
`_bmad-output/specs/spec-cicd/stories/5-1-guard-drift-detection-and-the-release-drift-check.md`.
It governs this implementation together with architecture AD-4/8/9/17/19.
The earlier planning-only delivery remains recorded in
`docs/cicd-5-1-guard-drift-check-plan.md`.

## Implementation scope

Shared CI acquires committed module objects, then runs workflow-owned guard
before prepare, SDK/build/tests or hooks. The read-only acquisition/comparison
layer uses the existing perimeter loader and engine. Ordinary modules read
their anonymous accepted `delivered/<Repo>` branch from the fixed CiCd remote;
the Builds root skips lookup and its existing sample compares a committed
nonempty fixture owning only sample CI configuration. Enabled hook/job action
availability is checked from committed workspace-root metadata after a match.

Create-mode release resolve compares its frozen source against the live tip or
an exact ancestry-validated delivered commit before switch/green-CI decisions.
Complete mode preserves original-artifact recovery and reads only current
dispatch settings. The paired sample fixture inputs are validated in all modes.
Existing public interfaces, permissions, frozen runtime, publisher behavior,
sample imports/actions and Windows delegation remain in place. Commitlint stays
in its separate caller until its actual migration.

## Local verification

The sample's new nonempty manifest passed `perimeter_config.load_bytes` and
`perimeter.compare` against its existing committed actual/expected configuration
with zero drift. `git diff --check` passed during workflow integration.

Focused behavioral checks on runner Python 3.14.4 passed:

- `test_expected_state.py`: 12 tests, including all nine entry kinds, anonymous
  branch selection, shallow ancestry, sibling/future rejection, corrupt sources,
  tag peeling, control-character diagnostics, and non-execution of hooks/filters.
- `test_guard*.py`: 16 tests exercising the guard and actual isolated composite,
  successor suppression, action availability, dirty/sparse trees, sample controls,
  and downloaded action directories without Git metadata.
- `test_ci_environment.py`: 20 tests executing the guarded shared job and its
  environment/hook/integration behavior with committed disposable fixtures.
- `test_release*.py`: 199 tests, preserving original-artifact completion and
  publication behavior while exercising create-mode drift/ancestry, current
  security outputs, failed-resolve outcomes, and complete-mode lookup bypass.

The focused transcripts are retained for this session as
`/tmp/cicd-5-1-expected-state-tests.log`, `/tmp/cicd-5-1-guard-tests.log`, and
`/tmp/cicd-5-1-ci-environment-tests.log`. The full release suite is recorded
separately in `/tmp/cicd-5-1-release-tests.log`.

The required complete suites passed against implementation source
`0f97426edb6f88ee6e534d95ca9029f7e0b20311` on both interpreter lanes:

- `uv run --no-cache --locked --script --python 3.12 Tools/ci/run_python_tests.py`:
  Python 3.12.13, 14/14 groups, 817 tests, no skips, exit 0.
- `uv run --no-cache --locked --script --python /usr/bin/python3 Tools/ci/run_python_tests.py`:
  Python 3.14.4, 14/14 groups, 817 tests, no skips, exit 0.
- `python3 -m unittest discover -s Github/lib/tests -p 'test_perimeter*.py' -v`:
  58 tests, exit 0.
- `python3 Tools/ci/lint_workflows.py`: pinned actionlint and authenticated online
  zizmor passed; 10 exact existing frozen-composite findings, zero new findings.
- `uv run --no-cache --locked --script --python 3.12 Tools/ci/check_public_api.py`:
  2,581 compatible entries, no removals against stable `v5.1.0`
  (`9ebc31f606bc02f8a0ec2f1ec344127e2468c6b4`).
- Whitespace checks and explicit scope inspection passed. The story commit
  changes no SDK/dependency pins, frozen runtime, schemas, publisher or sample
  import/action/Windows files. Its exact Conventional Commit message passed
  pinned commitlint 21.2.4 with zero problems or warnings.

The full transcripts remain at `/tmp/cicd-5-1-full-python312.log`,
`/tmp/cicd-5-1-full-runner.log`, `/tmp/cicd-5-1-perimeter-tests.log`,
`/tmp/cicd-5-1-workflow-lint.log` and `/tmp/cicd-5-1-public-api.log`.
The online lint and complete policy suites received the established audit
credential only through their subprocess environment.

## Exact-source hosted verification

The implementation was pushed on Builds `main` as
`0f97426edb6f88ee6e534d95ca9029f7e0b20311`; the remote head was verified directly.
Its normal [Builds push run](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43389324372)
uses that exact source. Observed job results:

| Job | Result | Evidence |
|---|---|---|
| Sample module / Build and tests | success | [job 131765199742](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43389324372/job/131765199742): checkout, guard, prepare, SDK, Release build, units, environment, root after-unit hook and integration all succeeded |
| Sample Windows tests | success | Dependent Windows preparation and sample action succeeded |
| Workflow lint and public API | success | Authenticated workflow lint and committed-object API both succeeded |
| Package catalog contracts | failure | [job 131765199775](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43389324372/job/131765199775): 14 existing catalog assertions require EventStore 4.2.0 after upstream changed its catalog to 5.0.0; exit 1 |
| .NET tests / Build and tests | failure | [job 131765199927](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43389324372/job/131765199927): guard, prepare and build succeeded; Evidence.Tests has 44 failures, 147 passes, exit 2 |
| Python contract tests (3.12) | success | [job 131765199513](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43389324372/job/131765199513): Python 3.12.16, all 14 groups passed; completed 08:09:27 UTC |
| Python contract tests (runner) | success | [job 131765199976](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43389324372/job/131765199976): Python 3.14.4, all 14 groups passed; completed 08:22:27 UTC |

The [pre-story upstream run](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43387068103)
at `05a75b626fb73c6e386d16c41e885f193d3cff16` already has both failures.
A normalized comparison confirmed that all 14 catalog error messages and all
44 failing Evidence.Tests test identities match exactly. The story commit
changes neither the package catalog, its authoritative validator, nor
`test/Hexalith.Builds.Evidence.Tests`. Concurrent upstream dependency edits were
preserved by rebasing; they are not authored story changes.

The resumed verification re-fetched the four failing-job logs and compared
the full sorted signature arrays and their exit codes. The retained
[verification record](cicd-5-1-guard-drift-check-verification.json) contains
both actual arrays, including parameterized test identities, source/run/job
identities, decoded-log SHA-256 fingerprints, normalization rules, all seven
completed exact-source job results and the local transcript fingerprints.
The comparison requires exact equality; matching counts alone are insufficient.
Only timestamps, ANSI display sequences and the final per-test runtime duration
are removed. The two jobs remain failures, and the overall run remains failed.

Local fixtures and the Builds sample do not establish a normal module's live
delivered branch. No module adoption, production release or administration was
performed.

## Independent review corrections

All four review layers completed. The edge-case layer reported no findings.
The other layers produced 17 individual observations: four medium, six low
and seven false. Seven findings formed six patch groups (three medium and
three low); seven false findings and three uncommon cosmetic/timeout
extensions were rejected with individual evidence in the canonical story.
No finding was deferred.

The corrections preserve filtered history deepening, remove an expired unused
snapshot handle, and give loader failures their committed logical filename.
Behavioral tests now cover actual lazy blob reads, a delivered-branch advance
during deepening, committed drift through the shared CI job, and real release
composite input/environment mapping. These changes touch
`Github/lib/expected_state.py` and the four affected test modules.

The implementation handoff passed all 55 focused tests. Independent in-memory
mutation checks then removed the deepening filter, shared-job guard, and each
of the two release input mappings. Each mutation was caught by an assertion in
its new behavioral regression; the repository files were not mutated.
Complete post-review verification is recorded in the following update.

## Terminal result

Status: ready for independent review under the story-specific verification
exception approved by the human on 2026-11-13.

The original implementation run halted while the hosted Python lanes were
unfinished because the two product jobs had already failed. The approved
exception now permits those two failures only when their actual signatures
match the recorded integration baseline. Resumed verification established that
exact match, and both hosted Python lanes subsequently completed successfully.
All other required local and exact-source hosted checks passed. No assertion,
dependency pin or runtime release-green requirement was changed.

The implementation-verification gate is satisfied under that exception.
Independent review remains outstanding and must pass before story completion;
this handoff does not claim that the story is done. The exception does not make
this failed Builds run green for release purposes.
