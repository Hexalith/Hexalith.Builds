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
all redirect codes, and schema/stdlib agreement including ECMAScript regex
end-of-string behavior. The isolated outcome adapter also rejects importing
module-owned Python.

## Hosted observations

The temporary manual Builds-only caller, root nightly hook and probe script
are installed only for these observations. Its sample and matching expected
configuration temporarily enable the nightly hook; both will be restored and
all three probe files removed after evidence collection. The repository
publishing switch is observed unchanged. No vulnerable finding or registry
artifact is created. Exact source/run/attempt, job conclusions and downloaded
outcome will be appended after the dispatched runs finish.

## Quality checks

The focused release suite and pinned actionlint/online zizmor pass. Zizmor
reports 12 exact frozen composite findings and zero new findings. Full locked
Python 3.12 and captured runner Python 3.14 suites are in progress at the
initial probe-source commit. The committed-source public API check, final
frozen-file comparison and remote read-back follow the source commit.

Caller permission union, current/release SHA separation, outcome ownership,
safe incremental behavior and the unimplemented drift slot are documented in
[the quality README](../Tools/ci/README.md#reusable-release-gates).
