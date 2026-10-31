---
title: Observe retryable startup cleanup without weakening withdrawal
status: in-review
created: 2026-10-31
baseline_commit: 57255b502fdd546bb09851d04fc1830175d897f3
context:
  - AGENTS.md
  - src/libraries/Hexalith.Builds.Tooling/Runtime/CompositionEngine.cs
  - test/Hexalith.Builds.Module.Tests/LocalRunLifecycleTests.cs
  - test/Hexalith.Builds.Module.Tests/LocalRunLifecycleFixture.cs
  - test/Hexalith.Builds.Module.Tests/CiRunLifecycleTests.cs
---

## Intent

Exact-main CI 41238944762 failed only
`ConcurrentDownCancelsStartingOwnerAsync(declared: true, beforeLaunch: false)`.
The original 2,668-case run recorded 2,662 passes, one failure and five existing
skips. Down refused with HXR037/HXT008 before cleanup because mandatory owner
metadata could not be recorded. The tracked HXR037 diagnostic explicitly directs
an operator to retry down for the same owner run identity.

Down deliberately acquires the outcome lock without waiting. Concurrent startup
cancellation can acquire that same lock after observing `cleanup-request.json`.
The test observes this existing refusal and explicitly invokes the same-owner
command after startup finalization. Production code, stopping and metadata
allowances, withdrawal, retention and first-outcome policy remain unchanged.

## File Scope

- test/Hexalith.Builds.Module.Tests/LocalRunLifecycleTests.cs
- _bmad-output/implementation-artifacts/spec-observe-retryable-startup-cleanup.md

## Acceptance and implementation

- Retain all four ordinary/declared and before/after-launch combinations and
  every cancellation, final cleanup, state/workspace removal and no-resurrection
  assertion. Retry only HXR037 with its exact HXT008 and documented same-owner
  hint, after startup cancellation has durably finished. Preserve the committed
  first cancellation outcome, timestamp and both selection expiry values.
- Two controlled cases cover the run-id and session Down entry points. Hold the
  actual outcome lock on a ready AppHost process with a previously recorded
  failure. Require HXR037/HXT008, the exact retained process identity, a still-live
  process, surviving state/workspace and no withdrawal report. Release the lock,
  explicitly retry the same owner, require successful cleanup and verify the
  first failure outcome, timestamp and expiry values remain unchanged.
- No production retry or allowance renewal is introduced. No resource, privacy,
  ownership, retention or withdrawal gate is weakened.

## Verification

All commands run in an isolated worktree at the baseline above, without nested
submodule initialization or original Root producer writes. The complete local
hosted-context simulation sets CI/GITHUB_ACTIONS, repository, run, job, attempt,
Linux and github-hosted variables; it is supporting local evidence, not a hosted
run claim. CI=true alone lacks mandatory CI identity and fails before startup;
that separately retained exploratory result is not the reproduced cleanup bug.

- Normal Release Module.Tests build with package dependencies, `-m:1` and
  `-warnaserror`: zero warnings/errors. Final build 22.09 seconds.
- Original four cases: 4/4 pass in the complete simulated hosted context.
- Controlled red: the two held-lock cases fail the old unconditional Success
  expectation with the exact HXR037/HXT008 refusal, zero skips, 5.069 seconds.
- Focused green: original four cases plus both held-lock cases, 6/6 passed,
  zero errors/skips/not-run, 13.523 seconds.
- Entire affected LocalRunLifecycleTests plus existing CiRunLifecycleTests:
  74/74 passed, zero errors/skips/not-run, 67.079 seconds. These retain the
  expired stopping-budget and blocked-withdrawal negative controls.
- Direct xUnit uses single-dash `-method`/`-class` and `-result-xml`. Build logs:
  `/tmp/builds572-lifecycle-baseline-build.log`,
  `/tmp/builds572-cleanup-green-build.log`.
- Exact full candidate message validated with the owning pinned commitlint
  21.2.4 on Node 24.22.1; no hook bypass.
- Source production diff is empty; copied root guidance is temporary/untracked
  and excluded from the explicit two-path stage.

The original main Python/CodeQL, package catalog and build/package contract
jobs all succeeded; only the .NET case and its aggregate failed. Normal protected
PR checks and exact new-main CI remain pending before Root pointer adoption.

### Receipt hashes

- `/tmp/builds572-main-dotnet-failure.log`: SHA-256 `c6188c5fe0d8df88ee5805311e37495c4a61058bf053f2c4c5970827235a9176`.
- `/tmp/builds572-held-lock-red.xml`: SHA-256 `58f740a20e7eb497df74586e0a97a71eb97a02f37feca993593b67eec0be30d4`.
- `/tmp/builds572-cleanup-green-methods.xml`: SHA-256 `bd0880f526c91e125822e249e1d0d1b710989abc7bcb0b487c911b5859eb2ffd`.
- `/tmp/builds572-cleanup-affected-classes.xml`: SHA-256 `04eb56155ecdb320e5ed013285546c0e7e7503b34ade415af454e4e1031b83f3`.
- `/tmp/builds572-cleanup-commit-message.txt`: SHA-256 `4e9c2f98564e732acf0a679abfe967555d0092fa60ac89021ec61578a37d7ef9`.
