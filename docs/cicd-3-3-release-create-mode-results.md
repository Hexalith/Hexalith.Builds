# Story 3-3 release create-mode verification

Implementation baseline: `4dbf9b80ea6875c8f66a9f6cb5861f4fbba2d0a1`.
Verification dates: 2026-11-10–11 (Europe/Paris); hosted timestamps are UTC. The enclosing CiCd story is canonical workflow state.

Version computes the frozen commit range through the two pinned plugins. Build
produces one audited original payload, and the stdlib-only draft stage independently
validates the version, authenticated artifact, inventories, marked drafts and fresh
workflow tree before creating the bot draft and exposing its eight-field handoff.
Complete-mode recovery and the publisher remain downstream work.

## Executed local controls

`python3 -m unittest discover -s Github/lib/tests -p 'test_release_*.py' -v`
passed **122 tests** on runner Python 3.14.4 before review hardening; the
post-review count is recorded below. The following mapping audits every
intent-matrix row against executed tests. SDK controls use real restore/build/pack
and container publication; API controls use disposable Git histories and local
HTTP/HTTPS servers. They are deterministic controls, not hosted observations.

| Matrix row | Executed controls in `Github/lib/tests` |
| --- | --- |
| Module rules | `test_release_version.test_actual_module_plugin_rules_and_breaking_precedence` |
| Builds rules | `test_release_version.test_actual_builds_plugin_rules_exclude_exact_nonrelease_types`, `test_builds_rules_and_sample_context_cannot_be_selected_by_modules` |
| No version | `test_release_version.test_no_version_tagless_or_at_base_has_explicit_noop`, `test_release_outcome.test_no_version_skips_build_and_draft_but_failure_or_cancellation_wins` |
| Tagless release | `test_release_version.test_first_release_analyzes_full_history_and_is_exactly_one`, `test_release_draft.test_tagless_accepts_only_one_zero_zero` |
| Independent draft gate | `test_release_draft.test_exact_successors_and_every_rejected_shape`, `test_reachable_annotated_tag_wins_over_prerelease_and_unreachable` |
| Audit | `test_release_build.test_real_sdk_audit_codes_and_inherited_exemptions`, `test_real_nuget_vulnerability_blocks_inherited_exemptions`, `test_audit_failure_suppresses_payload_manifest`, `test_non_audit_failure_keeps_status_without_vulnerability_diagnostic` |
| Payload | `test_release_build.test_real_multitarget_import_discovery_and_pack_stamp`, `test_duplicate_and_conditional_framework_identity_rejected`, `test_container_opt_in_rids_and_test_exclusion`, `test_real_sdk_complete_container_graph`; all four `test_release_oci` tests |
| GitHub-only/empty payload | `test_release_build.test_none_payload_does_not_execute_sdk_or_submodules`, `test_no_publishable_project_still_writes_empty_manifest`, `test_release_draft.test_empty_inventory_persists_empty_list_trailers` |
| Artifact identity | `test_release_draft.test_artifact_identity_failures_never_write`, `test_tampered_digest_and_package_metadata`, `test_manifest_closed_identity_and_duplicate_json_rules`, `test_unsafe_duplicate_and_unlisted_members`, `test_authenticated_container_inventory_runs_production_oci_graph_validation` |
| Retry/competition | `test_release_draft.test_create_has_five_trailers_and_exact_eight_outputs_then_retry_reuses`, `test_competing_and_conflicting_same_run_drafts_never_write_again`, `test_invalid_bot_marker_and_multiple_marked_drafts_block_creation` |
| Workflow race | `test_release_draft.test_complete_workflow_tree_race_blocks_api_write`, `test_main_product_change_preserves_frozen_source_and_allows_creation` |
| Success | `test_release_draft.test_subprocess_action_uses_only_trusted_python_git_api_and_never_checks_out`, `test_create_has_five_trailers_and_exact_eight_outputs_then_retry_reuses`, `test_release_outcome.test_only_validated_successful_drafts_can_transfer_outcome_ownership`, `test_incomplete_or_mismatched_artifact_handoffs_retain_reporter_ownership` |

The draft accepts only `2.3.5`, `2.4.0`, or `3.0.0` over `v2.3.4`, and only
`1.0.0` without a base. Tests reject jumps, incorrect resets, leading zeros,
prereleases, metadata, equality and lower versions. The artifact is parsed as data
without extracting or executing product files. The actual composite subprocess
proves credential-free Git, credential-separated signed downloads, five persisted
trailers, eight outputs and no Git tag.

The isolated npm installation uses `npm ci --ignore-scripts --legacy-peer-deps`
and `npm audit signatures`. The peer flag prevents installation of semantic-release
core; both direct plugins and their transitive dependencies are locked. The build
normalizes the upload action's hexadecimal digest to the API's `sha256:` form;
its executed adapter control validates that public handoff.

## Quality and frozen-file evidence

Both required locked commands pass **20/20 groups**, on Python 3.12.13 and
runner Python 3.14.4. Each shared-library group passes **230 tests**, including
all 122 release tests. The nested failure/empty-suite output in the runner
self-tests is intentional and those parent tests pass.

- `uv run --no-cache --locked --script --python 3.12 Tools/ci/run_python_tests.py`
- `uv run --no-cache --locked --script --python python3 Tools/ci/run_python_tests.py`


Pinned online actionlint 1.7.12 and zizmor 1.32.0 pass with 12 exact frozen
composite findings and zero new findings. Committed public API at
`4dd100c62ee8d5dd2952cfaa5c2516245d5e1c1c` passes against reachable `v4.47.0`
(`5ec1779040606be89306129a7d2b245258c9acd2`): **2542 entries, no removals**.

Concurrent external delivery during implementation merged lifecycle work at
`9f238f42fdb4fe0da40945663b92d9561a8acfd7` and swept partial story work into
`79eeccfd2f9482bb1f17729f880f03c25a1da2b3`; a separate Cursor session committed
and pushed the remaining implementation at
`4dd100c62ee8d5dd2952cfaa5c2516245d5e1c1c`. That history is preserved.
The frozen-file comparison explicitly records the unrelated lifecycle paths.
Outside those paths, only release.yml, the CI property-evaluation seam, outcome
reduction/tests, .gitignore and this story's quality documentation modify existing
files. Root npm release configuration/lock, legacy builds-release.yml, shared
Build props, trusted NuGet source configuration, and publication spike/evidence
sources retain their original bytes.

Local logs for this workspace are under `/tmp/cicd-3-3/`: `focused.log`,
`python312.log`, `runner.log`, `runtime-lint.log`, `runtime-api.log`,
`frozen-files.json` and exact-message `*-commitlint.log` evidence. These local
logs are not retained in the repository. Broad-suite and hosted observations are
recorded below.

## Hosted probe scope

The temporary manual caller invokes the actual reusable release workflow with
`test/fixtures/release/PackageOnly.slnx` and no publisher. Production callers and
the repository publishing variable are unchanged. The intended CiCd-agent profile
returns HTTP 403 reading the variable endpoint; its value is unverified. No
maintainer profile is used and no repository-variable mutation is made.

For this temporary probe only, the reusable resolve step receives literal `true`
as a labelled switch input control, retaining the real resolve and exact-source
CI check. CodeQL and hook are intentionally disabled by the disposable root
configuration. A labelled no-version output control exercises real Actions skip
scheduling; actual plugin no-version computation is proved by the local Git/plugin
tests above. A real SDK warning control requests NU1903 for the audit scenario.
Those injected inputs are distinguished from hosted API, artifact, SDK and graph
observations. Temporary caller/helper and all temporary bindings/config changes
are removed after collection; only identified probe drafts are cleaned up.

## Hosted observations

All observations below use full source
`c244eb55729947d293cfbc8837166086eb83df37` on `main`, attempt 1.
The selected exact-source CI is the real shared `ci.yml` job, temporarily made
manually dispatchable for the disposable fixture.
[CI run 42918909311](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42918909311)
passed. The ordinary Builds CI remains separate; its retained Python suites
have also passed locally above. The temporary manual trigger is restored during
cleanup. The earlier waiting probe
[42917082574](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42917082574)
at `443e3b7161b371d4845524583554d78fc23fc1b6` was cancelled before downstream
stages ran and supplies no positive branch coverage.

| Scenario / run | Hosted behavior | Retained identity |
| --- | --- | --- |
| [No version: 42918914780](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42918914780) | Resolve/version/reporter succeed; CodeQL/hook/build/draft skip; `publish=false`, outcome `nothing` | Outcome artifact `13809898352`, digest `sha256:5b92d8140a0ec5182ad1c751975c1b0065e56a343d5fc3ccb23f17c130af982a` |
| [Success: 42919132649](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42919132649) | Resolve/version/build/draft/reporter succeed; `publish=true`; reusable reporter uploads no outcome | Release artifact `13810653539`, draft `441030264`, version `4.48.0`, base `v4.47.0` |

The success artifact digest is
`sha256:941ff9943e9429201c9f2b4dceede13630c1d3cb2d1e9f3fefa7373e9d1e46c8`.
Downloaded bytes match the authenticated API digest. It contains exactly the
manifest and `packages/Hexalith.Release.CreateMode.Disposable.4.48.0.nupkg`,
with no container. Independent ZIP/XML inspection confirms its real version
and full repository commit. This first probe did not exercise OCI; the hardened probe below adds hosted
container coverage. Both SDK platforms and original graph bytes also have local controls.

The downloaded handoff exposes all eight fields with the exact source, version,
base, draft ID, artifact ID/digest and originating run above. The API draft is
authored by `github-actions[bot]`, `draft=true`, targets that full source and
contains exactly these persisted reserved trailers:

```text
hexalith-release: c244eb55729947d293cfbc8837166086eb83df37
hexalith-release-run: 42919132649
hexalith-release-artifact: 13810653539 sha256:941ff9943e9429201c9f2b4dceede13630c1d3cb2d1e9f3fefa7373e9d1e46c8
hexalith-release-packages: Hexalith.Release.CreateMode.Disposable
hexalith-release-containers:
```

The identified probe draft was deleted only after checking its bot author,
source, version and every trailer against that exact run/artifact/inventory.
The version tag was independently checked absent after draft creation/deletion.
No package, container, tag or published release was produced. Sanitized API,
job/step timestamps, digest and handoff evidence is retained in
[evidence/3-3/no-version.json](../evidence/3-3/no-version.json) and
[evidence/3-3/success.json](../evidence/3-3/success.json).

The build/upload composite completed at `2026-11-10T22:58:14Z`; the independent draft job started at `2026-11-10T22:58:20Z`. The package bytes have SHA-256 `7b9ffd79a41aaf39c63fceb3bd16040e0b17ab389fa995e27943848afc169839`.

[Audit run 42919434703](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42919434703),
at the same full source and attempt 1, fails as expected. Resolve and real
versioning succeed; the real SDK restore emits NU1903 despite the fixture's
inherited exemptions, then `v4.48.0 blocked by a vulnerable package`. Build
fails before upload, draft skips, reporter fails and writes the sole failed
outcome with known version `4.48.0`; `publish=false`. The run has only the
outcome and handoff observation artifacts, with no `release` artifact.
[Sanitized audit evidence](../evidence/3-3/audit.json) retains job results,
API artifact identities, output/outcome documents and exact diagnostic lines.

The repository variable was never changed, and no maintainer profile was used.
The temporary no-version injection and true-switch binding are test controls,
not claims about production switch configuration. The ordinary Builds CI is
separate from the selected green shared fixture CI and is not claimed as a
completed green run by these probes.

## Cleanup and final source

Cleanup commit `a5908ec9f4016afe309231855826fa6f2b20b3fa` was pushed and read
back from `origin/main`. The reusable release, shared CI trigger and root
configuration are byte-for-byte identical to production implementation commit
`4dd100c62ee8d5dd2952cfaa5c2516245d5e1c1c`. Both temporary probe files are
absent. Final source retains the strict repository-variable binding and no
no-version injection. Exact complete probe/fixture-CI/cleanup messages passed
lock-pinned commitlint 21.2.4 before use. Each push followed fetch/divergence
inspection and its full revision was read back from the remote.

## Review hardening and re-verification

Four independent review lenses returned 15 defect/gap findings, grouped into
12 patch entries: one high, nine medium and two low. All were fixed; none
were deferred. The high issue was comma-separated audit exemptions bypassing
NU1903. Corrections also cover SDK-derived normalized container names and RID
precedence/restore assets, collision-free archive paths, retained platform
metadata, mandatory supported media types, pre-consumption PAX/GNU metadata
bounds, UTF-16 DTD rejection, scalar SDK properties, final-fetch force-push
rejection and persisted multiline notes.

Hardening commit: `a5829e85f5d1a68aba6d7b33c63f241cea44fc90`, pushed with
lock-pinned commitlint evidence. The patched focused suite passes **137 tests**.
Both required Python commands pass **20/20 groups**, including **246 library
tests** each, on Python 3.12.13 and runner 3.14.4. Pinned online lint still
reports 12 exact frozen findings and zero new findings. Full logs are
`post-review-focused.log`, `post-review-python312.log`,
`post-review-runner.log` and `post-review-lint.log` under `/tmp/cicd-3-3/`.

The hardened temporary caller uses `Release.slnx`, producing one real package
and one uncompressed dual-platform container archive. Its fixture inherits
comma-separated NoWarn values. The repository switch remains unchanged and
its enabled input remains a labelled temporary control.

[Selected fixture CI 42923489011](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42923489011)
passed at full source `11672763af22972a40a8ba7d7531f10684197939`.
[Hardened success run 42923493860](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42923493860),
at that exact source, attempt 1, passed resolve/version/build/draft/reporter
and returned all eight validated fields. Its release artifact is `13812224374`,
digest `sha256:0f0fadfdc130e4e8795754840462d531d8553cd20840964c76daf89062fceeff`.
Draft `441052673` persisted all five trailers, including the container name
`hexalith-release-create-disposable`, version `4.48.0`, and base `v4.47.0`.
Only the verified identified draft was deleted afterward.

The downloaded archive passes byte-digest and complete graph checks for
`linux/amd64` and `linux/arm64`, with 17 blobs and root
`sha256:ac1776719615c0ffaea0a612e548a6c827e8139eba992eaa18bb1c4c11cb7243`.
[Sanitized hardened evidence](../evidence/3-3/post-review-success.json) retains
the persisted body, exact handoff, artifact inventory, timestamps and graph.

A concurrent external lifecycle merge then advanced main to
`b1681870d0b08a5fe103133214c67fda7a449eb1` (lifecycle parent
`7461fb05ad170375fc0df66e6a39a4d2432e6247`). It changes no release implementation,
workflow or fixture path. The first subsequent audit dispatch
[42924100861](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42924100861)
failed resolve because the new SHA had no qualifying CI; build/draft skipped.
That failure is not counted as audit-code coverage. The new exact-source fixture
[CI 42924269443](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42924269443)
then passed before the corrected audit dispatch. External lifecycle changes
are preserved and excluded from this story's frozen-runtime comparison.

[Corrected hardened audit run 42924447082](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42924447082),
at `b1681870d0b08a5fe103133214c67fda7a449eb1`, attempt 1, reaches the real
SDK restore after successful resolve/version. Despite inherited
`NoWarn=NU1903,NU1904`, it reports NU1903 and
`v4.48.0 blocked by a vulnerable package`, fails build before upload, skips
draft, writes a failed outcome and returns `publish=false`. No `release`
artifact exists. [Retained hardened audit evidence](../evidence/3-3/post-review-audit.json)
contains the exact SDK diagnostics and authenticated outcome/artifact metadata.

Final probe cleanup `2de148778026c5ace8153a388596836afc6cf0a4` was pushed and
read back from main. It restores the strict reusable switch binding, shared CI
trigger, root configuration and fixture props byte-for-byte to hardening commit
`a5829e85f5d1a68aba6d7b33c63f241cea44fc90`. Both temporary caller/helper files
are absent. No probe draft remains and no version tag/package/container/release
was published. Temporary controls do not remain in the final runtime.

A follow-up independent review is recommended because this pass repaired a
high audit bypass and added archive/parser and SDK metadata handling. All known
findings have executed regression controls and post-fix hosted coverage; the
new repair code itself has not yet had a separate independent review pass.

## Follow-up review (2026-11-11)

A separate four-lens review covered the story-scoped range
`4dbf9b80ea6875c8f66a9f6cb5861f4fbba2d0a1..9b34565cb746d6a9c978d8dcf436830a31ceaefc`,
excluding the merged lifecycle work. It found no in-scope high defect.
Nine patches add executed controls and direct corrections:

- `Github/lib/tests/actions_expressions.py` is a test-only GitHub expression
  evaluator. It runs the real `release.yml` gates, `with:` handoffs, job
  outputs, reporter `env:`, upload condition and workflow outputs for
  no-version, publisher handoff, build failure, disabled, complete-mode and
  CodeQL-failure states, then runs the real reporter. Mutating the
  release-rules, version, no-op or notes mapping fails these controls.
- The version, build and draft composites now run through their declared
  inputs, step `env:`, `if:`, external-action `with:` and output expressions.
  No test presets `RELEASE_*` variables.
- Real SDK package and dual-platform container payloads are zipped as the
  upload stores them and read through the draft's `artifact_inventory`.
  A multi-target container project is rejected.
- Draft package inspection selects only the root nuspec, as build does.
  Oversized `Content-Length` values are reported as size violations.
  Raw non-UTF-8 legacy commit messages are analyzed instead of aborting
  version computation.

Verification after these patches:

- The focused release suite passes 143 tests.
- Both locked full Python commands pass 20/20 groups, including 252 library
  tests each, on Python 3.12.13 and runner Python 3.13.13.
- Pinned online lint reports 12 exact frozen findings and zero new findings.

Deferred, not this story's control: module-file audit waivers
(`Directory.Build.targets` `NoWarn` and `PackageReference` `NoWarn` metadata)
still pass the release build. SPEC and AD-8 assign their rejection to the
story 5-2 guard audit lint. `NuGetAuditSuppress` in Builds `Props/` remains
the sanctioned waiver.
