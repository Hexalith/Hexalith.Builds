---
story: '5-1'
created: '2026-11-13'
status: in-progress
implementation_baseline_revision: f01c3ab70f7e7401b3b73217983955f7771a1465
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

`python3 Tools/ci/lint_workflows.py` passed pinned actionlint, then stopped before
zizmor with `Online zizmor audits require GH_TOKEN, GITHUB_TOKEN or
ZIZMOR_GITHUB_TOKEN`. No authenticated online-audit result is claimed by that
attempt. Dual-Python complete-suite, authenticated
workflow-lint, committed-object public-API and final frozen-scope checks remain
pending and will be recorded after they run.

## Exact-source hosted verification

Hosted verification is pending. Record the full implementation SHA, normal
`builds-ci.yml` push-run link, guarded sample Linux/hooks/integration results
and dependent Windows job result after observing that run. Local fixtures do
not establish a normal module's live delivered branch, and this story requires
no module adoption or production release.
