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
skipped. The source uses stdlib Python only.

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

Full locked local and committed-source hosted qualification are the remaining
gates before final acceptance; their results will be retained with the source
and run identities.
