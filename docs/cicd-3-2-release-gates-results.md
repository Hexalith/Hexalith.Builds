# Story 3-2 release gate verification

Implementation baseline: `11f8534fc89a2c4a18a984307e927abb6d6c7ff3`.
Verification date: 2026-11-10. The earlier planning delivery remains recorded
in `cicd-3-2-release-gates-plan.md` and the canonical CiCd story.

The implementation adds trusted release resolution, current-dispatch CodeQL
and prepared nightly jobs, and failure-first outcome reporting. Publication
stages and the story 5-1 perimeter comparison remain later integration work.
The enabled all-green gate-only path deliberately fails with string
`publish=false` and `release publication stages are not installed`.

## Intent matrix and executed local controls

Tests named below are behavioral controls over disposable Git histories,
loopback HTTP endpoints, independent schema validation and executed composite
adapters. They never assert workflow source text. Hosted observations are
recorded separately; a local branch is not claimed as a hosted branch.

| Intent row | Executed control in `Github/lib/tests` | Evidence |
| --- | --- | --- |
| Wrong ref | `test_release_adapters.test_invalid_switch_and_wrong_ref_retain_valid_current_settings` | Wrong ref fails, retains validated dispatch settings, emits no selected SHA. |
| Create | `test_release_state.test_disabled_and_invalid_switches_retain_settings_and_selected_identity` | No marked draft freezes dispatch SHA/create mode. |
| Complete | `test_release_adapters.test_complete_draft_uses_dispatch_config_without_historical_ci_or_checkout` | Old draft SHA selected; current CodeQL/hook settings emitted; no CI read or checkout for old source. |
| Invalid drafts | `test_release_state.test_malformed_bot_drafts_are_named_and_all_remain_unchanged`, `test_multiple_valid_drafts_fail_naming_both_identities`, `test_unmarked_and_nonbot_drafts_cannot_select_or_mutate_releases` | Invalid/ambiguous drafts named, objects unchanged, unmarked drafts ignored. |
| Disabled | `test_release_adapters.test_disabled_adapter_keeps_dispatch_settings_and_uses_only_git_and_stdlib`, `test_release_outcome.test_successful_gates_under_the_false_switch_are_disabled` | Exact false logs publishing disabled; only successful gates authorize disabled. |
| Invalid switch | `test_release_state.test_disabled_and_invalid_switches_retain_settings_and_selected_identity` | Unset, nonliteral and malformed switches fail with the exact diagnostic while valid nightly settings survive. |
| Red CI | `test_release_state.test_no_ci_and_each_non_success_conclusion_fail_without_waiting`, `test_unfinished_ci_waits_once_per_minute_until_success_or_fixed_deadline`, `test_api_time_counts_toward_fixed_deadline_even_when_the_run_is_green` | No/red CI fails; 60-second polling has one 1800-second deadline, including API time. |
| CI selection | `test_release_state.test_latest_exact_sha_push_or_manual_ci_governs`, `test_fixture_and_workflow_overrides_are_restricted` | Latest exact-source push/manual run wins; arbitrary workflow override rejected; only Builds accepts builds-ci.yml. |
| Security | `test_release_security.test_only_current_open_high_and_critical_release_categories_block`, `test_real_pagination_finds_later_blockers_and_names_each_number`, `test_every_page_must_be_available_and_well_formed` | Open high/critical current release categories block; closed/lower/unrelated pass; API/JSON failure fails. |
| Hook | Hosted success/failure observations below; `test_release_state.test_complete_uses_dispatch_configuration_and_never_checks_old_sha_ci` | Current settings remain independent of old selected source; hosted run proves actual prepare/hook scheduling. |
| Empty languages | `test_release_outcome.test_intentionally_disabled_gates_can_skip` | Empty CodeQL/dormant hook allow only their intentional skips; hosted empty-matrix coverage is not claimed. |
| No publication | `test_release_outcome.test_only_explicit_successful_downstream_noops_are_nothing`, `test_missing_unknown_and_unexpected_skips_cannot_be_success` | Nothing requires an explicit successful no-version/stale-draft-deletion owner; missing stages never imply a no-op. |
| Publication handoff | `test_release_outcome.test_only_validated_successful_drafts_can_transfer_outcome_ownership`, `test_actual_adapter_does_no_file_or_upload_work_for_publisher_handoff` | Failure wins; validated handoff writes no file/directory and sets upload=false. |
| Early error/cancellation | `test_release_outcome.test_failure_and_cancellation_win_over_all_other_decisions`, `test_actual_adapter_falls_back_to_dispatch_on_early_or_malformed_metadata`, `test_failed_decision_still_exposes_upload_path_and_retains_failure` | Failed artifact uses dispatch until selected SHA is known; failed decision still exposes upload path and exits nonzero. |
| Intermediate enabled path | `test_release_outcome.test_enabled_gates_without_publication_stages_fail_explicitly` | All gates green and true switch remains failed with incomplete-pipeline diagnostic and publish=false. |

Additional controls cover annotated/numeric/unreachable tags, excluding the
draft's own tag, no-tag `1.0.0` bootstrap, malicious filters/hooks/working-tree
config and submodule nonexecution, API pagination credential boundaries and
all redirect codes, verified numeric GitHub repository pagination aliases,
and schema/stdlib agreement including ECMAScript regex
end-of-string behavior. The isolated outcome adapter also rejects importing
module-owned Python.

## Hosted observations

The temporary manual Builds-only caller, root nightly hook and probe script
use `Samples/Hexalith.Sample`; its matching sample and expected-source configs
temporarily enable nightly. The hook checks the workspace HEAD against the
dispatch SHA, checks the existing Release DLL, then optionally exits 23.
Only the probe reads that dispatch input. No registry artifact or vulnerable
finding is created. The probe files and both config changes are removed after
collection.

The repository's publishing switch is **unset**, observed unchanged in hosted
resolve logs. The scoped CiCd-agent profile cannot read the repository variable
endpoint (HTTP 403); that failure alone does not establish absence. Hosted
runner `RELEASE_SWITCH` is empty and corrected resolve reports
`HEXALITH_RELEASE_PUBLISH_ENABLED must be exactly true or false`. Successful
`disabled`, true-switch CI/pipeline and complete-mode paths therefore remain
local executable controls; they are not claimed as hosted branches.

The first run at `796e5dbc4e4554a90aa6a07d20c096a6c5c94298`,
[42867343043, attempt 1](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42867343043),
revealed GitHub pagination using `/repositories/<id>/...` aliases. Resolve
failed at the named-repository credential boundary before selecting a SHA.
CodeQL and preparation/hook still succeeded, final reporting failed and
uploaded a valid failed outcome using the dispatch SHA. The implementation now
fetches authenticated named-repository metadata, validates ID/full name and
rewrites only the verified numeric alias back to the named endpoint; no numeric
or cross-repository destination receives the credential. A live read traversed
all 198 release records after that fix; four additional HTTP regression cases
cover matching aliases, cross-ID rejection, metadata mismatch and API base paths.

The corrected source is `feef50a50870efb288002471293f76ab461f08a3`.
Both corrected-source observations completed as follows:

| Run / attempt | Resolve | CodeQL | Prepare + hook | Reporter / run | Outcome artifact |
| --- | --- | --- | --- | --- | --- |
| [42867966372 / 1](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42867966372) | failed, strict unset switch | success, processed alert check | prepare success, hook exit 23 | failed / failed | ID 13788287997, only outcome.json, failed |
| [42868589774 / 1](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42868589774) | failed, strict unset switch | success, processed alert check | prepare and hook success | failed / failed | ID 13789700286, only outcome.json, failed |

The downloaded outcome is exactly:

```json
{"outcome":"failed","schema":"hexalith.release-outcome.v1","sha":"feef50a50870efb288002471293f76ab461f08a3"}
```

Both the independent draft-2020-12 schema validator and the stdlib validator
accept it. The file SHA-256 is
`7a41a275106fbcea7fd160da47ef5db9f4023cd14f0127983cd018a3323b6e8f`.
The artifact API ZIP digest is
`sha256:109ab9f9db304390f0183d64520bdf1160264b9d7d0fc0c7cfdd1b7380fd54db`.
These are distinct byte identities.

The success-hook run downloads the identical schema-valid outcome document
and file SHA-256. Its artifact ZIP digest is
`sha256:5ee54acbae16c3f2027e151442fd9a4d05e793a3fa81842f8edca894d00fd03c`.

Sanitized exact-source log observations:

- Failure-hook run: prepare built the sample successfully once, then at
  `17:46:03Z` the hook logged the full corrected SHA and `release_dlls: 1`,
  followed by exit 23. At `17:49:23Z` CodeQL reported analysis upload status
  complete, then checked zero alerts at `17:49:24Z`. Reporter decision failed
  at `17:49:35Z`; its artifact upload succeeded at `17:49:36Z`.
- Success-hook run: at `17:50:19Z` the hook logged the full corrected SHA and
  `release_dlls: 1`. CodeQL reported processing complete at `17:53:45Z`, then
  checked zero alerts at `17:53:46Z`. Reporter decision failed at `17:53:55Z`
  and still uploaded the sole outcome at `17:53:57Z`.

Both CodeQL jobs used build-mode none and category
`hexalith-release/csharp`. The annotated upstream v4.39.3 tag was dereferenced
and verified to full commit `33d7b85da1a04171e211befe2c739303b9a4c066` before
pinning init/analyze. The corrected resolve jobs emitted selected SHA/mode and
valid current settings despite failure. This proves actual always scheduling
after failed resolve, preparation, hook success/failure, real processed
analysis/alert reads and upload after failed reporting. Cancellation of a
running prerequisite, empty matrix and complete mode are local controls only.

All three probe files are removed. Both sample config blobs are restored
byte-for-byte to the implementation baseline; existing sample behavior is
preserved. No release, tag, draft, package or registry publication was made.
Queued runs 42867410394 and 42867960321 were cancelled before execution;
they provide no branch coverage. The former was an obsolete pre-fix probe;
the latter was superseded by GitHub's one-pending-run concurrency behavior.

## Quality checks

- Focused controls: `python3 -m unittest discover -s Github/lib/tests -p
  'test_release_*.py' -v` passes 55 tests on runner Python 3.14.4. Locked
  Python 3.12.13 runs the same 55 cases in the full library group.
- Full commands: `/tmp/hexalith-builds-ci-tools/uv run --no-cache --locked
  --script --python 3.12 Tools/ci/run_python_tests.py` and the same command
  with `--python /usr/bin/python3` (captured runner 3.14.4) each pass **20/20
  groups**. Each shared library group passes 163 tests. These final runs use
  command-local read-only `GH_TOKEN` captured without logging its value.
  Initial runs passed 19/20 groups because that online-policy prerequisite
  was omitted; authenticated full reruns resolved the environment failure.
- Final `python3 Tools/ci/lint_workflows.py` passes after probe removal;
  integrity-pinned actionlint 1.7.12 and online zizmor 1.32.0 report 12 exact
  frozen composite findings, zero new findings. No audit baseline changed.
- Committed-source public API at the probe commit passes against reachable
  `v4.47.0` (`5ec1779040606be89306129a7d2b245258c9acd2`): 2521 entries,
  no removals. The same check is repeated at the final cleanup commit.
- `git diff --check` passes. Final comparison against implementation baseline
  `11f8534fc89a2c4a18a984307e927abb6d6c7ff3` confirms every preexisting
  runtime/config/package/release blob remains unchanged. The only modified
  preexisting file is this story's documentation addition in Tools/ci/README.md;
  new release files, schema, tests and this results document are additions.
- Exact complete implementation, pagination-fix and cleanup commit messages
  pass lock-pinned commitlint 21.2.4 before use. Each push follows fetch and
  divergence inspection, and remote refs are read back after delivery.

Local evidence logs are retained for this workspace at
`/tmp/cicd-3-2-release-tests.log`,
`/tmp/cicd-3-2-python312-authenticated.log`,
`/tmp/cicd-3-2-runner-authenticated.log`,
`/tmp/cicd-3-2-final-lint.log` and `/tmp/cicd-3-2-probes/`.
Hosted run/job links and artifact identities above are the durable evidence;
no credential-bearing raw log is committed.

Caller permission union, current/release SHA separation, outcome ownership,
safe incremental behavior and the unimplemented drift slot are documented in
[the quality README](../Tools/ci/README.md#reusable-release-gates).
