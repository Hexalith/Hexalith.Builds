# Story 2.7 evidence notes

This implements the frozen Platform specification `spec-2-7-withdraw-attached-runs-and-keep-failure-retention.md`, building on Builds `2980efdf51f305dc5c39bb854261c252d13465f5` and policy `hexalith.platform.candidate-and-attachment-policy.v1`.

Attachment records now carry first outcomes. `attach --end` requires `--outcome passed|failed|cancelled`; the first record survives later ends, cancellation, withdrawal and expiry. An end at or after expiry records `withdrawn`/`expired` at the immutable original expiry. Legacy terminal records without an outcome read as `unknown`. The v1 descriptor/result hold shapes and contract reader invariants are unchanged.

Owner cancellation is recorded before `attachments.lock` is taken to withdraw active holds. Cancellation during the success hold wait uses a fresh finite stopping allowance, withdraws with `owner-cancelled`, and then cleans up. The owner's first terminal outcome remains immutable. Exact down records cleanup in progress, withdraws with `developer-stop`, and then stops the session AppHost before acquiring the provisioning lock to stop other resources. Both paths report `HXI011` with attached run ids and actual reasons; unrecorded expiries become `expired` in the same pass. Unrecordable withdrawal prevents teardown (`HXR037` for down, pending cleanup with `HXT008` for owner cancellation).

A recorded local attached failure interrupts the owner's success hold wait and establishes failure retention. The owner records resource disposition `retained`; listing includes the failed attached run id and each attachment outcome. Later cancellation withdraws peers and preserves the failed attachment and resources. Ready failure-retained local environments remain attachable until explicit down. Listing projects unrecorded expiries without changing attachment metadata.

Validation uses the Debug Module.Tests project and its built xUnit v3 assembly directly. The focused classes cover the matrix rows, prior admission/hold rules, listing honesty, outcome persistence, exact-owner bounded cleanup, cancellation and cleanup retries. Build and test logs alongside this note record the exact commands and results.

The runner does not own or terminate attached processes/tasks. Stopping attached work and ending access at expiry is explicitly deferred to the Platform follow-up `attached-test-execution-and-expiry-enforcement` in the Platform deferred-work ledger. CI withdrawal/job cleanup, fixture migration, attached-run result documents, candidate acceptance and publication changes remain outside this story. These deterministic tests do not claim live composition or candidate-evidence qualification.

Initial validation of implementation commit `a13d7972395f989955cf00f1047f1ba50b1a6b73`: Debug build passed with 0 warnings and 0 errors; all 182 focused tests passed with 0 skipped. The 9 cases isolated while correcting fixture readiness, exhausted metadata allowances and cleanup recording contention also pass. See `build.log`, `tests.log`, `regression-tests.log` and `verification.json` for exact commands, results, source binding and pinned commitlint validation. The implementation commit was recorded externally while this task was active; the agent performed no remote operation.

Review correction: cleanup refusal now precedes the attached-failure retention exception. A failed attachment cannot admit a fresh run when its owner is pending cleanup, cleaning, incompletely cleaned, or completely cleaned; ready retained admission remains available when cleanup is not requested. `AttachedFailureCannotOverrideCleanupRefusalAsync` reproduced the three incorrect admissions before the correction (`cleanup-admission-before.log` and `.xml`). After correction, the Debug build passed with 0 warnings/errors and all 187 focused cases passed with 0 skipped. `tests.xml` retains each named test and its pass result, including all five cleanup-state cases. `verification.json` binds the correction's source bytes, exact commands and pinned commitlint validation. The xUnit `-xml` option emitted its deprecation notice; XML was successfully written and verified.

Independent review fixes normalize legacy expiry records before validation, retain cumulative HXI011 diagnostics in internal `attachment-withdrawals.json` before teardown, report committed outcomes rather than stale projections, and persist pending cleanup after attachment inspection/report failure. Report persistence is limited to observation/withdrawal, preserving admission semantics. Existing owner-result retention fields now include local attached failures and their explicit release; the owner first outcome, v1 schemas and reader invariants are unchanged.

Final coordinator verification after those fixes: Debug build passed with 0 warnings/errors; all 203 cases in the five focused classes passed with 0 skipped. `tests.xml` records every named result and `verification.json` records the exact command, complete tested source hashes and matrix audit. The implementation agent also ran only the edited attachment/result-writer classes: 118/118 passed (`review-focused-tests.log` and `.xml`). Added coverage includes both admission/withdrawal lock orderings, live AppHost survival and retry for both session entry points, report failure, legacy upgrade, and attached failure through explicit down and active tracking removal.

Review follow-up fixes restore the 2.5 cleanup continuation for CI, context-less and
environment-only owners, while local selected-test owners retain strict durable cleanup
state and withdrawal. HXR037 names cleanup state or withdrawal and preserves earlier
bookkeeping diagnostics. Passed owners no longer treat a retained resource disposition
alone as proof of failure retention. Cancellation metadata uses a separate five-second
allowance; an exhausted resource budget still kills the session without waiting after
withdrawal. Session AppHosts are stopped and disposed immediately after durable
withdrawal, before provisioning-lock acquisition, so later errors cannot leave them alive.
Pending-state write failures are explicit HXT008 diagnostics, and cumulative HXI011
reports name each withdrawn run once. Outcome consistency has four negative cases, the
cancellation refusal test asserts durable pending with the immutable passed outcome, and
the cleanup test double counts every attempt before assertions. Hold wait tests use longer
holds to leave room for negative completion assertions.

The current verification expands the original five-class scope with
`ModuleRunnerReviewRegressionTests` and `LocalRunSourceReviewTests`. The manifest records
each current command and repository-relative XML path; earlier observations remain
identified as historical evidence and are not claimed as rerun commands.

Final review-patch verification: the seven-class run passed all 291 cases with zero
errors, failures, skipped or not run. The focused Debug test build passed with zero
warnings/errors after the current Tooling and Module.Cli outputs compiled. The ordinary
project build remains blocked by three unrelated EventStoreHost routing API errors;
`review-patch-build-blocked.log` retains that failure. The focused build uses existing
AppHost reference artifacts and does not claim to rebuild the blocked host. SHA-256
hashes confirm the test directory loaded the freshly built Tooling/CLI outputs.
`verification.json` records all commands and subset scopes; the prior manifest is
preserved as `verification-before-review-patches.json`. The exhausted-budget regression
also passed independently after synchronizing cancellation with native startup.

Final coordinator review verification: all 299 cases in the seven-class suite pass, including live exhausted-budget cancellation, publication-refusal AppHost survival, cleanup-lock expiry, both locality bookkeeping branches, passed-owner admission/listing and environment-only cleanup progress. The focused Debug build has zero warnings/errors. The normal build still fails on seven unrelated EventStoreHost API diagnostics in the concurrent checkout; final-build-blocked.log preserves them. Story source and executed artifact hashes were stable throughout the run; unrelated concurrent source edits are identified in the manifest. Final command stopping follows both durable withdrawal and valid terminal publication.

Fifth-pass corrections add informational `HXI012` for a passed owner retained by attached failures, naming the failed runs and the explicit `down` release. `attach --end` recovers an already-ended record read-only after complete owner cleanup; active and unknown targets still refuse when the owner is absent. Recorded expiry keeps the `attachment-expired` retry reason, and result snapshots project every hold against one clock reading. CLI help and README explain the required outcome and HXR032 refusal.

Fifth-pass regressions cover the cancellation recheck after the last hold poll, withdrawal before acceptance, HXR037 outcome classification, cancelled/passed attachment listing honesty, immutable post-down ends through both issued identities, and refused absent-owner writes. Ordering tests use native barriers, the blocked expiry report is installed before native release, and provisioning-cancellation tests allow four seconds for pre-lock bookkeeping.

Fifth-pass verification passed all 316 cases in the seven Story 2.7 classes, with zero errors, failures, skipped or not-run cases (`fifth-pass-tests.xml`). The run uses `-parallelMode none` to avoid the recorded cross-class fixed-run-id interference. Focused Debug AppHost and Module.Tests builds each passed with zero warnings/errors against freshly compiled Tooling/CLI outputs. The ordinary full project build still fails on seven unrelated EventStoreHost API diagnostics (`fifth-pass-build.txt`). `fifth-pass-verification.json` records exact commands, the named matrix/patch audit, source hashes and matching executed binary hashes. No commits or remote operations were performed during this fifth-pass implementation.

Review-patch evidence preserves the original 316-case run and its XML. Every fifth-pass log referenced by the manifest now has a verbatim retained `.txt` copy. The manifest also hashes currently available prebuilt EventStoreHost outputs/reference artifacts; their originating source provenance is unknown, and those observations do not establish historical byte stability or a source revision. Patch validation is recorded separately from that earlier passing run.

Review-patch verification passed 24 targeted attachment cases in 4.837 seconds, including the directory-at-owner-state refusal and a scanner-barrier race where explicit down completes before the owner retention update returns. CLI/Tooling and focused Module.Tests Debug builds passed with zero warnings/errors. Recorded expiry refuses both issued identities even at expiry minus one second; HXI012 requires a still-retained, not-requested returned record; mixed CLI options assert HXC001. See `fifth-pass-review-patch-verification.json` and its separate `.txt`/XML artifacts. Coordinator verification remains separate.

Final fifth-pass coordinator verification: all 318 cases across the seven story/regression classes passed, with zero errors, failures, skips or not-run cases. XML parsing confirms all six matrix rows and the new clock-rollback, strict-absence and concurrent-down regressions. Source and artifact hashes stayed unchanged. See `fifth-pass-coordinator-verification.json` and `fifth-pass-coordinator-tests.xml`; broad EventStoreHost build limitations remain explicit. One pre-existing native-barrier fixture-disposal follow-up is deferred in the Platform ledger.
