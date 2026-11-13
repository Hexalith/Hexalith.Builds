---
story: '5-1'
created: '2026-11-13'
status: blocked
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
| Python contract tests (3.12 / runner) | unfinished at halt | Hosted lanes were running/queued when the already-failed required product checks established the blocker; both full local lanes above passed |

The [pre-story upstream run](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43387068103)
at `05a75b626fb73c6e386d16c41e885f193d3cff16` already has both failures.
A normalized comparison confirmed that all 14 catalog error messages and all
44 failing Evidence.Tests test identities match exactly. The story commit
changes neither the package catalog, its authoritative validator, nor
`test/Hexalith.Builds.Evidence.Tests`. Concurrent upstream dependency edits were
preserved by rebasing; they are not authored story changes.

Local fixtures and the Builds sample do not establish a normal module's live
delivered branch. No module adoption, production release or administration was
performed.

## Terminal result

Status: blocked.
Blocking condition: implementation verification failed because exact-source
Builds CI retains the independently confirmed upstream catalog and evidence-test
failures. The runtime implementation and self-test evidence are pushed, but the
required all-green product verification is unsatisfied. No dependency pins or
unrelated validation expectations were changed to hide those failures.

The build-auto workflow halts at its implementation-verification gate before
step 4 review. Review remains outstanding after the upstream failures are
resolved. This terminal record does not claim the story is done.
