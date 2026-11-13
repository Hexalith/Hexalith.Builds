# Guard action, audit and commit-policy enforcement

Implementation baseline: `ceb3257147917002a2433be0ce9aac7db99e7698`.
During implementation, remote main independently advanced to
`02ed624aa19781cac75d2c9112940e9bd830441a` (native-process test grouping).
The story commits were rebased onto that linear continuation. That pre-existing
upstream test-only change is visible in the full baseline diff; this story did
not author or revise it.

Qualification date: 2026-11-13. Exact-source hosted qualification passed for
`af34585a72b220753d7f83bcb48a400a301b6eac`.

The existing stage-1 guard now checks committed custom-action metadata,
module-owned audit declarations and event commit policy after ownership and
enabled-action availability succeed. The sample audits its own subtree, lints
workspace-root actions and validates Builds-wide messages once; Builds root
bypasses the new lint/tool work. No product stage follows a lint failure and only
actual perimeter drift emits `drift=true`.

Tools use exact CLI/preset 21.2.4 and js-yaml 4.3.2 manifests and an integrity lock
installed outside the module with scripts disabled and signatures required.
The explicit action-sourced commitlint configuration keeps the complete existing
conventional policy and invokes stdlib Python for raw-message skip detection.
The root validator uses the same added policy. PR base/head and title, complete
push ranges, zero-before creation pushes, dispatch HEAD and trusted explicit
local full-SHA ranges are supported independently of changed paths and checkout
merge identity. PR title edits retain trigger coverage.

Custom actions permit ordinary quoting, flow syntax and aliases, validated actual
uses references and explicit-project test commands. Audit XML is selected by
Project root rather than extension, and NuGet configurations by canonical name
or structure. Response files are scanned conservatively. Literal matching can
reject harmless text; shell indirection, computed imports and obfuscated MSBuild
declarations remain outside this lint. Existing release audit behavior is intact.

## Caller retirement prerequisite

The scoped CiCd-agent profile was used with no GH_TOKEN/GITHUB_TOKEN overrides.
Account/repository readback identified `jpiquot`, `Hexalith/Hexalith.Builds`, default
branch `main`. Immediately before deleting the Builds-only caller, read-only
`gh api repos/Hexalith/Hexalith.Builds/rulesets/19196298` returned:

```json
{"id":19196298,"name":"Protect main","target":"branch","enforcement":"active","conditions":{"ref_name":{"exclude":[],"include":["~DEFAULT_BRANCH"]}},"rules":[{"type":"deletion"},{"type":"non_fast_forward"}],"bypass_actors":[]}
```

Before that deletion, the actual sample guard CLI passed with a real isolated
locked installation, npm signature audit, parser and commitlint:

```sh
CI_REPOSITORY=Hexalith/Hexalith.Builds CI_MODULE_ROOT=Samples/Hexalith.Sample CI_EXPECTED_SOURCE=Samples/Hexalith.Sample/expected-source CI_EVENT_NAME=workflow_dispatch python3 -I Github/lib/guard.py
```

The caller, live solution entry and unused caller test constant were then removed
together. `.github/workflows/commitlint.yml` and legacy module runtime stayed
unchanged. Its retained execution tests now use an isolated legacy consumer's
standalone pre-migration conventional configuration and the real pinned tools.
That is the configuration its remaining module consumers execute; Builds root
uses the new guard policy. This matters because the frozen workflow's manual
title-validation API does not pass custom plugins. Running those legacy tests
against the new Builds-only custom root rule initially failed with
`Found rules without implementation: no-skip-ci`; no frozen-runtime edit or
policy exemption was made.

## Local verification

Real pinned tools passed under Node 24.22.1, including all ten allowed types,
header/body 200 and footer 100 boundaries, conventional rules and URL handling,
merge linting, all skip forms and root policy parity. Poison module npm/Node/
commitlint configuration did not execute. Real committed sample guard tests
exercise multi-commit push and PR title selection, exclude a forbidden synthetic
checkout merge, and reject an earlier bad commit, bad title and selected merge.

Composite/shared-job tests execute actual uses, conditions and environment
mappings; their external npm/node/SDK commands are explicitly labeled doubles.
They verify root/sample/no-solution scopes, full event mappings, drift outputs
and suppression of preparation/build/tests/hooks. Action tests additionally use
the real pinned YAML parser and audit tests read real committed Git fixtures,
including dirty, staged, missing and sparse worktrees.

`python3 Tools/ci/lint_workflows.py` passed with online audits using a scoped token
supplied in memory: zero new findings; the existing exact frozen-composite
baseline remained unchanged. `python3 Tools/ci/check_public_api.py` passed
against `v5.2.1` with 2,589 entries and no removals, including full source
`af34585a72b220753d7f83bcb48a400a301b6eac`. The exact implementation commit message passed pinned
commitlint with zero problems/warnings. `git diff --check` passed.

Both locked Python lanes passed every discovered group (14/14, 878 tests per
lane, exit 0): Python 3.12.13 and runner Python 3.14.4, with Node 24.22.1.
The final two regressions were added after those processes imported the lib
suite; affected final-source groups passed afterward. Parent verification of
final source passed 57 guard tests, 15 audit tests and 3 raw-policy tests.
Final adapter/tool/action/environment totals are 16/9/14/24. No covering test
was skipped. Matrix audit matched all 14 scenario rows to executed passing
checks in both broad logs, supplemented by final-source regression execution.
The initial broad failures were the legacy consumer fixture above and absent
read-only online-audit credentials; corrected full runs passed with the scoped
token supplied only in memory. No gate was weakened.

Commands: `uv run --no-cache --locked --script --python 3.12
Tools/ci/run_python_tests.py`, and the same locked command with
`--python /usr/bin/python3`; focused discovery used `python3 -m unittest
discover -s Github/lib/tests -p 'test_guard*.py' -v` and the audit/policy
patterns. Complete lane logs and structured command/exit summaries are retained
in the execution workspace under `/tmp/cicd-5-2-python312-final.log`,
`/tmp/cicd-5-2-python-runner-final.log` and
`/tmp/cicd-5-2-python-final-results.json`.

The first implementation source `fde085eeccc11617f42e71a1e5d7db23ed5e4290`
triggered normal push run [43463294597](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43463294597).
Sample job `132015660283` failed before preparation because the attempted
`github.event_path` context mapping was empty, shadowing the runner's actual
`GITHUB_EVENT_PATH`. The follow-up removes that nonexistent context mapping and
reads the standard runner event file, including when an optional override is
empty. The harness no longer invents that context property and an adapter
regression covers the runner-file fallback. No event fallback to HEAD or last
commit was introduced; missing event endpoints still fail closed.


## Exact-source hosted qualification

Normal push [run 43464051267](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43464051267)
completed successfully for `af34585a72b220753d7f83bcb48a400a301b6eac`.
Both hosted Python lanes passed 14/14 groups and 880 tests, including 464 lib
tests and the final two regressions. All retained jobs passed:

| Job | Job ID | Result |
| --- | --- | --- |
| Workflow lint and public API | 132018300216 | success |
| Python contract tests (3.12) | 132018300622 | success |
| Package catalog contracts | 132018300640 | success |
| Python contract tests (runner) | 132018300799 | success |
| Sample module / Build and tests | 132018300983 | success |
| .NET tests / Build and tests | 132018301028 | success |
| Sample Windows tests | 132018868553 | success |

The source-SHA Actions run listing returned only this CI run and
`.github/workflows/builds-ci.yml`; no independent commitlint caller executed.
The sample log records one successful guard policy execution before its
product preparation/tests/hooks, and the dependent Windows job passed. Root
performed its normal product pipeline while bypassing module lint tools.
Source changes are committed and pushed in `fde085eeccc11617f42e71a1e5d7db23ed5e4290`
and `af34585a72b220753d7f83bcb48a400a301b6eac`.


## Independent review corrections

All four review lenses completed. Verification review found no gaps. The edge
and blind reviews produced concrete literal-boundary findings, and parent
probes confirmed them before correction. Intent observations were checked
against the current implementation dispatch and separate GitHub/readback
surfaces. Triage records every layer finding in the canonical CiCd story.

Corrections inspect every literal chained dotnet invocation; recognize quoted
primitives/executable paths and actual Helm/crane login forms; accept ordinary
redirection on explicit-project commands; reject malformed env/with mappings;
and recognize quoted response properties, complete response values, valid XML
prologs and BOM-less UTF-32. A parent patch probe exposed DOCTYPE fallback
backtracking; atomic alternatives and subprocess-bounded 20/25/1,000-comment
regressions now prevent it. The parser still does not evaluate shell/MSBuild
expressions or load external entities. Targeted tests passed: actions 16,
audit 18. Post-review full qualification will be recorded below when complete.
