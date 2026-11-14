# Story 5-3 restore job qualification

The AD-8 restore job independently fetches current module `main` and the latest
accepted `delivered/<Repo>` tree. It applies eligible perimeter entries in a
private bare repository and index, creates one bot commit parented by the
fetched main, pushes once, then dispatches `ci.yml` on `main`. A rejected push
stops the transaction without retry, rebase, force or dispatch. Dispatch failure
retains a successful repair; a fully clean rerun can dispatch without another
commit. Excluded-only drift causes neither an empty commit nor a dispatch loop.

The failed CI job supplies only its exact `drift == 'true'` flag. Restore reads
no module checkout, failed-job artifacts, repair plan or module configuration
to authorize its work. It runs trusted Builds code with `python3 -I`, rejects
ineligible contexts before network access, and bounds acquired documents and
HTTP responses to 4 MiB. Anonymous readers receive no token. Only the push
subprocess receives the GitHub-scoped authentication header; dispatch uses a
fixed GitHub API endpoint and refuses redirects.

Every committed workflow path and mode is protected, including indirect
absence-pattern matches and ancestor replacements. Builds gitlink and section
repairs require the accepted perimeter tag to equal the verified running
reusable-workflow tag. CI configuration additionally requires the complete
workflow directory to match the accepted render. Branch/SHA workflow sources
authorize independent repairs only. Skipped drift waits for delivery.

Reusable caller jobs must grant the `contents: write` and `actions: write`
permission union. This is an AD-10 caller-permission API change. The called CI
job, custom actions, and standalone Windows jobs retain `contents: read`;
only the isolated restore job receives write permissions. Builds and every
nonempty `module-root` skip restoration. Original guard failure remains visible.

Implementation baseline: `2f4f1cf9ae4889f8d96b4af3ceb01442163d1215` in
`references/Hexalith.Builds`; superproject baseline:
`06a3998be613e31affac5b69a755840e6a4bbcf3`.

Focused qualification passed 26 restore tests (22 runtime/HTTP and four
composite/job adapters), 16 expected-state tests, 51 perimeter tests, 12 guard
tests, 16 guard-adapter tests and 24 CI-environment tests. Parent execution of
`python3 -m unittest discover -s Github/lib/tests -p 'test_restore*.py' -v`
passed all 26 again after the workflow lint correction. No covering test was
skipped. Restore’s runtime uses stdlib Python only.

| Contract scenario | Executed restore tests |
| --- | --- |
| Ordinary repair | `test_real_repair_preserves_product_and_foreign_partial_content_in_one_commit` |
| Mixed product/ownership edits | The ordinary-repair test and `test_missing_and_malformed_partial_files_are_recreated_with_owned_content` |
| Workflow drift | `test_workflow_content_missing_extra_and_mode_drift_stays_identical_and_config_is_skipped`, indirect-glob and ancestor/descendant controls |
| Different Builds tags | `test_both_tag_transition_directions_preserve_lockstep_and_repair_independent_files` |
| Matching Builds tag | `test_verified_matching_tag_repairs_gitlink_section_and_config` |
| No eligible changes | `test_excluded_only_drift_never_commits_pushes_or_dispatches` and clean-rerun recovery |
| Unavailable sources | `test_unavailable_invalid_and_oversized_expected_state_never_publishes`, tag/source spoofing and nonregular-source controls |
| Concurrent module push | `test_concurrent_product_push_rejects_once_without_retry_or_dispatch` |
| Dispatch failure | `test_post_push_dispatch_failure_then_clean_rerun_does_not_commit_twice` |
| Forbidden invocation | Direct-invocation rejection and `test_job_conditions_skip_every_forbidden_invocation_after_failure` |

The real-composite test executes production orchestration and Git transport
through a fixed-remote local shim, inspecting remote main and its sole repair
parent independently. External GitHub dispatch is doubled there; separate
HTTP tests exercise the real dispatch adapter's endpoint, body, bounds and
redirect/error handling. Poisoned execution surfaces leave no marker. A real
attribute conversion failure preserves the private index and removes temporary
candidate files. No test writes to a production module.

Authenticated `python3 Tools/ci/lint_workflows.py` passed pinned actionlint
1.7.12 and zizmor 1.32.0: ten unchanged exact frozen-composite findings and zero
new findings. Initial lint rejected `cancelled()` in a step environment mapping;
the final mapping uses the legal `job.status == 'cancelled'` expression while
the job retains `!cancelled()` scheduling. No lint exception was added.

CiCd `uv run --locked --script tools/render.py --check` passed. Its empty
registry produces no module renders or adoption changes. Both repositories'
whitespace checks passed. Pinned commitlint 21.2.4 accepted the exact
implementation message, with a breaking-change footer for caller permissions,
and the runtime repair message with zero problems or warnings.

Both complete locked local lanes passed 14/14 retained groups and 920 tests per
lane, including 504 shared-library tests and all 26 restore tests. Commands were
`uv run --no-cache --locked --script --python 3.12 Tools/ci/run_python_tests.py`
and the same command with `--python /usr/bin/python3`. Interpreters were Python
3.12.13 and 3.14.4; no covering restore test was skipped. Complete logs and
command/exit manifests are retained at `/tmp/bmad-5-3-python312.{log,json}` and
`/tmp/bmad-5-3-python-runner.{log,json}`. Expected failure output inside negative
controls does not represent a failing suite; both complete commands exited 0.

The committed public-API check passed for implementation revision
`5f8ae823fb74e116c7dec5289fb0c91805f092e6`, against stable `v5.2.2`
(`02ed624aa19781cac75d2c9112940e9bd830441a`): 2,590 entries and no removals.
The permission increase is separately declared as a breaking caller API
change, since the removal checker does not evaluate permission requirements.
The scoped CiCd-agent profile pushed that implementation to Builds `main`;
`git ls-remote` independently confirmed the complete revision.

CiCd callers derive permissions from their selected trusted workflow declaration:
the existing `v5.1.0` pin remains valid, while a restore-enabled tag requires the
Contents/Actions write union. No Builds tag, module adoption or release is
created by this story. Exact-source hosted CI passed as recorded below.

The locked CiCd renderer and validator suites passed 32 and 49 tests,
respectively. They render real disposable old-tag and restore-enabled tagged
contracts, ignore a newer mutable Builds HEAD/worktree, preserve Windows read
permissions, and reject insufficient or excess caller permissions. Production
render and `--check` produced zero generated changes; `builds-version` remains
`v5.1.0`.
Parent `uv run --locked --script tools/tests/test_render.py --lint` then passed
all 32 renderer tests and eight generated fixture workflows through pinned
actionlint and authenticated online zizmor. The complete log and command/exit
manifest are retained at `/tmp/bmad-5-3-cicd-render-lint.{log,json}`.

Normal push [run 43534137412](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43534137412)
uses implementation revision `5f8ae823fb74e116c7dec5289fb0c91805f092e6`.
Both hosted Python lanes passed 14/14 groups with 504 library tests and all
26 restore tests, under Python 3.12.16 and 3.14.4. Their complete job logs are
retained at `/tmp/bmad-5-3-hosted/python312.log` and
`/tmp/bmad-5-3-hosted/python-runner.log`. Hosted quality, sample CI and dependent
Windows also passed. Root .NET and the complete catalog gate then passed; the
normal push run completed successfully. Both self/sample restore jobs were
correctly skipped. These hosted skips do not claim production-module writes;
the executable disposable-repository controls provide that evidence.

| Hosted job | Job ID | Result |
| --- | --- | --- |
| Package catalog contracts | 132258879756 | success |
| Python contract tests (3.12) | 132258880074 | success |
| Python contract tests (runner) | 132258880078 | success |
| Root .NET build and tests | 132258880107 | success |
| Workflow lint and public API | 132258880119 | success |
| Sample module build and tests | 132258880125 | success |
| Sample Windows tests | 132259315233 | success |
| Sample restore | 132259317070 | skipped |
| Builds root restore | 132264692162 | skipped |


## Independent review corrections

All four review layers completed. Parent probes confirmed that standard-library
HTTP protocol errors escaped the original dispatch sanitizer, and the
verification reviewer demonstrated that removing the default redirect handler
was undetected by the earlier tests. The correction catches HTTPException with
the existing fixed transport diagnostic. New controls cover malformed status
lines and incomplete body reads, confirm a successful repair survives protocol
failure, and execute the actual default opener against a loopback HTTPS
connection transport while retaining the production URL and handler chain.
The redirect destination receives no request or authorization. The focused
runtime/HTTP suite passed all 25 tests after these corrections.

The parent separately reproduced the purported ancestor-replacement issue:
both full and eligible-only comparison refuse removal of the unowned `.github`
ancestor, as the contract's structural-failure rule requires. Shorthand
permission calculation also fails in the pre-existing global checker; that
inherited limitation is recorded as deferred, alongside unverified capacity for
very large future module repositories. All current tagged declarations use
explicit permission mappings. The canonical CiCd story records every review
verdict, including rejected diagnostics/retention suggestions and the intended
qualification/adoption boundary. Fresh post-review qualification follows.
