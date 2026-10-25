---
title: 'Release and verify the corrected module CLI'
type: 'bugfix'
created: '2026-10-25'
status: 'in-progress'
route: 'dispatch'
baseline_commit: '957df69d50f4ab56c6c924b7a61237338413c21c'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — authorized release scope in the invocation">

## Intent

Publish the corrected Hexalith.Builds Module CLI through the existing governed
release workflow and verify the actual feed artifact against the unchanged
Conversations declaration. Prepare the exact artifact handoff for Platform.

## Boundaries & Constraints

**Always:** Preserve concurrent `/pushall` work and all previous observations.
Validate exact commit messages with Builds' pinned commitlint. Require successful
push CI at the exact current release SHA and complete release qualification;
honor protected-environment approval. Let semantic-release choose the version.
Restore from the publication feed into a fresh isolated cache and record all
artifact/source/schema identities. Retain every adoption failure additively.

**Never:** Deploy, edit Platform or Conversations pins/gitlinks, claim new
Platform qualification, weaken release gates, or lift Story 16.1's runtime hold.
Historical local packages cannot substitute for published-artifact measurements.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Correct declaration | Unchanged Conversations manifest | Ordinary human/JSON validation succeeds | Retain streams and exits |
| Invalid keys | Both configuration-key fields, malformed variants | Exact-field HXP002 rejection | A false acceptance blocks completion |
| Adoption | Current tracked Conversations snapshot | Run human/JSON and preserve all failures | Expected incomplete adoption remains explicit |
| Release gate | Current main and exact-SHA CI | Protected full qualification and hashed publisher | Stop on any failed gate |
| Publication identity | Actual signed feed package | NuSpec and informational source match release SHA; loose/embedded/source schema equality | Mismatch blocks handoff |

</frozen-after-approval>

## Code Map

- Builds `docs/module-cli-configuration-key-compatibility.md` and linked
  `evidence/g4/module-configuration-keys-20261025/`: immutable predecessor facts.
- Builds `schemas/hexalith.module-manifest.v2.json`: correction already committed.
- Builds `test/Hexalith.Builds.Module.Tests/Workspace/WorkspaceCommandModeTests.cs`:
  stabilize the finite startup allowance for the actual warning-success CI failure,
  retaining both format/result assertions. `CompositionEngineTests.cs` and
  `StartupCoordinationTests.cs` retain independent readiness/deadline refusal controls.
- Builds `Tools/test-g4-tool-package-contracts.ps1`: official source, installed
  tool, SDK and malformed-key qualification; preserve its gates.
- Builds `.github/workflows/build-release.yml`, `Tools/verify-g4-release-source.sh`,
  `Tools/verify-g4-tool-release.ps1`, `Tools/publish-g4-tool-packages.ps1`:
  existing governed manual release, protected approval and hashed publication.
- Builds `package.json`, `package-lock.json`, `commitlint.config.mjs`:
  semantic release effects and pinned message validation.
- Platform `.config/dotnet-tools.json`, `hexalith-platform.json`, `README.md`:
  read-only handoff constraints; accepted pin remains 4.35.2.
- Conversations `module/hexalith.module-manifest.v2.json` and prior adoption
  evidence: unchanged inputs and comparison only.

## Tasks & Acceptance

**Execution:**

- [x] Builds `docs/module-cli-configuration-key-release-plan.md`: record authorized
  release scope and create the necessary validated semantic fix commit.
- [x] Stabilize the warning-success test's process-fixture startup bound for the
  actual CI failure; retain all warning/exit assertions and verify separate
  readiness/deadline refusal controls without changing production gates.
- [x] Builds release workflow: push, obtain successful exact-SHA CI, dispatch
  protected release and require full qualification and published inventory.
- [x] Builds new `evidence/g4/module-configuration-key-release-20261025/`:
  retain baseline, commands, CI/release identities and published package probes.
- [x] Builds new released-artifact handoff document: record exact version,
  content/catalog identities, validation results and all remaining blockers.
- [ ] This spec: record verification and independent review without changing
  historical story records or protected input bytes.

**Acceptance Criteria:**

- Given committed corrected source, when governed release completes, then the
  exact SHA has successful push CI and release-eligible full qualification.
- Given its actual published successor, when restored in isolation, then package,
  source, DLL and schemas are recorded and ordinary validation passes in both modes.
- Given malformed keys and adoption probes, when both formats execute, then
  malformed keys remain rejected and every outstanding adoption failure survives.
- Given the final workspace, when compared with entry inventories, then existing
  observations, manifests, pins, indexed gitlinks and Story 16.1 hold are preserved.

## Implementation Notes

- No unresolved intent questions. Release/push side effects are explicitly
  authorized by the user. The spec records that scope without another approval.
- Entry: Conversations `957df69d50f4ab56c6c924b7a61237338413c21c`, Builds
  `8eb5646fac69c5d3f42257c6bfdcd04158ac50ea`, Platform
  `1b7a00381794bb7ecf40caf643c63c50758a81eb`; all three main trees were clean.
- Scope is a freeform Builds release continuation, not Story 16.1 execution.
  Historical runtime evidence-boundary/final-record gates are not-applicable;
  the governed Builds release inventory is the applicable release gate.
- Entry inventories are retained at
  `/tmp/module-cli-release-20261025-2gbedle5/baseline.json`; copy them into the
  new Builds evidence directory. The new release-plan document is already
  prepared in Builds and not committed. `npm ci --ignore-scripts` installs the
  pinned validator only; do not update dependencies. No commit/push/release has
  occurred in this continuation yet. The caller's explicit release authorization
  overrides the skill's generic no-push/no-remote-operation restriction.

- Progress through 2026-10-25 09:59 UTC: the entry inventory was copied to the
  new Builds evidence directory. Exact initial and successor commit messages
  passed the pinned validator. The initial direct main push at `c81e691` was
  classified as bypass by rule suite 4709439980; this was a deviation and is
  retained explicitly. No release was dispatched from it.
- PR 134 passed required exact-head Codacy, SonarCloud and commitlint checks,
  then normally squash-merged to `36e7e485`; rule suite 4709465424 passed.
  Its exact-SHA push CI 40181805558 was cancelled after separate work advanced
  main. Separate changes to runtime retention/cleanup were preserved, and 372
  release-bound inputs were verified byte-identical.
- PR 135 passed its required exact-head app checks, but concurrent `/pushall`
  merged its branch at `80a810de` before this continuation's normal merge.
  The main-tip precheck failed before the merge API call. Rule suite 4709538966
  reports bypass because merge commits are disallowed; required statuses passed.
- Latest observed Builds remote/local main is
  `a9013c541a3cb8e127d84a3723966dfe11eeab82`; push CI 40182696227 is queued.
  The previous `c81e691`, `36e7e485`, `0ade5ec` and `80a810de` push CI runs
  are cancelled, not successful full runs. Await confirmation that concurrent
  `/pushall` has stopped before further remote mutations or source stabilization.
- Conversations was externally committed to
  `3fa50842227ecbbe68a77f10ce68743ac000e26a`, changing its indexed Builds
  gitlink from entry to `657bcfaa0addd4f8ca94448bc3d8b9eed5193a00`.
  Platform checkout is externally advanced to
  `b9cf0e78c42ca8bfe1aa5488cc4340c238887acb`, with an externally changed
  indexed gitlink. These changes were not made by this continuation and are
  preserved rather than reset. The protected file bytes and old observations
  still match entry. The final indexed-gitlink preservation criterion is not
  claimed against the entry snapshot.
- Local-only progress document:
  `references/Hexalith.Builds/docs/module-cli-configuration-key-release-progress.md`.
  Timestamped progress/preservation observations and prepared feed harness are
  under `references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/`.
  The harness is syntax checked only. No published-artifact measurement,
  release-eligible inventory or actual Platform artifact handoff is claimed.

- Resumed after the user's instruction to continue. Fresh entry inventories at
  `references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/baseline.json.gz`
  bind Conversations `8444207001d6ad3cd197c2ffba519317effa8b37`, Builds
  `142b06e6d05c94b55ac35e587450c35fcac87590`, and Platform
  `b9cf0e78c42ca8bfe1aa5488cc4340c238887acb`, all initially clean.
- PR 136 exact-head required app checks passed after preserving an inventory
  representation correction: Sonar first reported 509 `json:S6418` findings on
  hash values under auth/token-shaped path keys; original bytes remain in gzip,
  with equivalent labelled path/sha256 rows and no gate change. Normal squash
  source `7144b8031b95a65982d78b3eb381e10c00b55cbe` passed rule suite
  4709709500. Exact-source push CI 40184369318 was later cancelled after 34
  completed steps; no complete push CI success or release credit is claimed.
- At 10:44:01 UTC, separate `/pushall` work advanced remote main to
  `6e40ea5f6243391bea80084b8ddd695df58fb1ff` (19 runtime/test/README files);
  rule suite 4709861214 reports bypass and push CI 40185980339 started.
  Complete Git-tree comparison found all 372 release-bound input blobs unchanged.
  Old CI logs retain only the cancellation error, with no recorded test failure.
  Remote writes are held pending explicit confirmation that other Builds writers
  are paused through CI and protected release. No release/feed step has run.
- Latest resumed progress and preservation observations:
  `references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/progress-at-supersession-20261025T104747Z.json` and
  `references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/preservation-at-supersession-20261025T104747Z.json`.
  Local Builds checkout remains 7144b803; incoming remote work is not reset.

- The latest 372-input equality proof covers only the module-key schema and
  workflow/qualification/publisher/fixture/catalog/lock/SDK/attribute inputs.
  External source 6e40ea5 also changes the compiled
  `ModuleRunEvidenceArtifactValidator.cs` policy by dropping removed-disposition
  and empty-remaining-resource requirements for completed selected-test evidence.
  Its exact patch is retained at
  `references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/external-runtime-validator-delta-20261025T105102Z.json`.
  This continuation does not edit or independently qualify that runtime policy;
  no Platform or runtime qualification credit is claimed.

- The user explicitly confirmed other Builds writes are paused and instructed
  continuation. Rechecked current main at
  `6e40ea5f6243391bea80084b8ddd695df58fb1ff` with push CI 40185980339
  running. Safely fast-forwarded local Builds without source edits. The existing
  governed source guard requires exact current allowed branch and successful
  exact-SHA push CI, not a new source-only PR after every incoming baseline.
  Existing committed fix/feature work provides the release effect, so no new
  semantic trigger or CI-resetting commit is needed. Preserve and disclose
  external rule suite 4709861214 as bypass without claiming historical branch
  compliance; this continuation changes or bypasses no rule. Full current-source
  qualification remains mandatory. Decision evidence is
  `references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/stable-source-decision-20261025T105248Z.json`.

- Source advanced through normal PR137 at 11:13:56 UTC to
  `68e65f2023eca117a2aa08a022ee33d8f4c34a44`; rule suite 4710053318 passed.
  It changes only CompositionEngine.cs to release the provisioning lock and
  remove pre-start state/diagnostics after an HXW006 refusal. Earlier current
  CI40185980339 was cancelled by supersession; its full metadata and logs are
  retained. Local Builds safely fast-forwarded and current push CI40187992999
  is running. Existing writer-pause authorization continues; no extra source-only
  PR, trigger commit or permission request is required by the governed route.
- Old PR135/136 full CI logs were retained: each had one failure in
  `WorkspaceCommandModeTests.DescriptorModuleProjectOutsideMappingStopsEngineAsync`
  at line1151 (2097 total,2093 succeeded,1 failed,3 Platform-context skips).
  Incoming PR137 addresses the same refusal-state defect, subject to successful
  current-source CI and full qualification. No obsolete release PR CI remained
  active, so routine cleanup required no cancellation or gate change.

- Current source68e65f2 CI40187992999 completed with failure, not cancellation:
  `PublicSourceWarningAppearsOnSuccessAsync(format: json)` expected success but
  received HXR021 after its test-only five-second total startup deadline. Summary:
  2116 total,2112 succeeded,1 failed,3 Platform-context skips. The earlier
  refusal-state regression passed. Full native logs/jobs/conclusion are retained.
  Parent-authorized narrow stabilization covers this success fixture's finite
  allowance, preserving both formats, all warning/result assertions, separate
  readiness-failure controls, production deadlines and every release gate.
  A focused baseline/reproduction and post-change check precede normal pinned-
  validated PR/check gates and fresh exact-current-SHA CI. Release remains pending.

- Another session merged the same warning-success30-second fixture fix through
  normal PR138 at `e171c5a788eab183b32683431b084198c9b8d373`; rule suite
  4710425344 passed. Our pre-push tip assertion stopped before any duplicate
  branch/stage/commit/push. Preserved the broader local two-fixture proposal's
  exact source bytes/patch and18-test measurement, then reverted only its
  hash-verified owned test delta and safely consumed PR138. The imported full
  commit message passed pinned commitlint. Its native-handoff fixture stays at
  the original allowance; no second fixture edit or source-confirmation commit
  was introduced.
- Exact imported sourcee171c5a Debug build passed with zero warnings/errors;
  focused warning formats, prior refusal regression and unchanged0.5-second
  mapping-deadline control passed4 tests with zero failed/skipped/not-run.
  Proposal18-test results remain separate and are not exact released-source
  proof. Current-source push CI40192040716 is required and in progress; release
  dispatch, protected approval, full qualification and feed measurements remain
  pending. Exact-source proof is recorded in
  `references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/imported-pr138-verification.json`.

- Exact current e171c5a push CI40192040716 succeeded, including installed tool
  qualification. Native counts remain explicit: Module2113 succeeded/3 skipped
  of2116, Evidence178/0 skipped, Integration1/12 skipped of13, all zero failed.
  Three skips require Platform checkout context;12 opt-in live composition tests
  were not run and earn no live/Platform qualification credit. Full native counts,
  skip reasons, jobs and logs are retained.
- Active governed Release40193969210 was dispatched by another session21 seconds
  before our redundant queued40193995250. Cancelled only our queued duplicate,
  with zero jobs/publication, and adopted the active exact-e171 run. Both dispatch
  identities and cancellation remain explicit; the cancelled duplicate earns no
  release credit. Active verify-source passed. Protected production history
  records approved byjpiquot, with a comment stating no separate interactive
  review; current required-reviewer policy remains unchanged and admin bypass
  disabled. Do not infer independent human review. Full release qualification,
  inventory/publication, actual feed probes and artifact handoff remain pending.

- Final 2026-10-25 release observation: protected Release40193969210 succeeded
  at exact e171c5a; semantic-release selected4.38.0 and its tag resolves exactly
  to that source. Both full source lanes ran distinct Evidence178/178,
  Module2113/2116(3 Platform-context skips) and Integration1/13(12 opt-in skips),
  all zero failed. Final inventory SHA-256
  `1405abc0e1476e8b41d275f46369d2034a0e761154b42d39c85acc33ae595786`
  records executed/passed sourceValidation/controls, official build, clean exact
  source and releaseEligibletrue. Qualified visible-fixture scope289 is proven;
  one hidden tracked `.config` file is separately identified, not silently counted.
- Actual isolated feed measurements are retained under resumed evidence
  `published-artifact-03/`: signed Module nupkg SHA-256
  `f352d3e0f03f21dbfe42c89adb7a8765cb073a2351281acaa3cf304235426ab1`
  matches the publisher's signed remote hash; qualified unsigned hash is distinct.
  NuSpec/public/Tooling source identities and source/loose/embedded schema hashes
  match. Raw embedded catalog and source Props bytes/hashes are retained.
  Ordinary validation0/0 and all36 malformed exact-field HXP002 probes passed.
- Adoption is actually6PASS/18FAIL, identical human/JSON rows. Every17 historical
  failure remains unchanged; one new gitlink failure records released checkout
  e171 versus indexed Builds68e65f2. Conversations externally committed
  ed53473ea4c67aa10ffc46c9b951d9864fe263d4 at11:24:40UTC; its index and
  concurrent user documentation are preserved. No pin/gitlink repair is made.
  Fresh03's final comparison-assertion exit1 remains native evidence; the additive
  triage ledger records completed required probes and classifies the real new row.
  Attempts01/02 retain supplemental BOM/path presentation assumption failures.
- Exact released-artifact handoff is prepared locally at Builds
  `docs/module-cli-configuration-key-released-artifact-handoff.md`. Platform
  remains b9cf0e78 with pin4.35.2/rollForwardfalse and indexed Builds0ade5ec2,
  a preserved pre-existing mismatch with historical4.35.2 source4248b8c7. Future
  separately authorized adoption targets actual tag e171 and exact tool4.38.0,
  with its own Platform qualification. Story16.1 hold stays ACTIVE. Final evidence
  and docs need no extra main push; independent implementation review is pending.

## Spec Change Log

- 2026-10-25: recorded implementation progress and the concurrent-source blocker;
  frozen intent, protected input bytes and historical story records are unchanged.

## Review Triage Log

## Verification

- Pinned commitlint accepted every exact candidate used by this continuation and
  the exact imported PR138 message. Exact-source Debug build passed with zero
  warnings/errors; four focused warning/refusal/deadline checks passed without
  failure/skip/not-run. Archived18-test proposal proof remains separately labelled.
- Exact-current-SHA push CI40192040716 and protected Release40193969210 succeeded
  at `e171c5a788eab183b32683431b084198c9b8d373`. Initial and actual4.38.0 full
  lanes each observed Evidence178 passed/0 skipped, Module2113 passed/3 skipped,
  Integration1 passed/12 skipped, all zero failed. No Platform/live credit from skips.
- Actual tagv4.38.0 and final inventory bind that clean exact source. Inventory
  sourceValidation/controls executed/passed, package build official/passed and
  releaseEligibletrue. All289 governed visible fixture hashes/sizes and manifest
  digest match source-bound checkout filters; the extra hidden tracked fixture and
  raw-blob/all-tree assumption failures remain explicitly scoped observations.
- Production review history records jpiquot approval without separate interactive
  review, with unchanged required-reviewer policy/admin bypass disabled. Both source
  guards and hashed publisher passed. Initial direct-push deviation/external bypasses
  remain disclosed. Our redundant queued40193995250 remains cancelled with zero jobs.
- Fresh isolated actual feed restore/signature/catalog/NuSpec/public version/DLL/
  three-schema/raw embedded catalog/source Props identities are recorded in resumed
  `published-artifact-03/identity.json`. Signed package hash matches the publisher;
  qualified unsigned ZIP and signed ZIP hashes remain distinct. Raw NuSpec bytes
  differ only by the archive's single leading UTF-8 BOM, retained explicitly.
- Unchanged Conversations manifest hash0bed1c29… validated ordinary human/JSON0/0
  with no diagnostics.36 malformed controls each exited1 with one exact-field
  HXP002, canonical input-file identity and unchanged input bytes. Adoption human/
  JSON1/1 retains24 identical rows:6PASS/18FAIL, all17 old failures unchanged plus
  the released-checkout/index mismatch. The fresh03 wrapper's final comparison
  assertion exit1 is retained and classified in actual-feed-measurement-triage.json;
  it does not mean a declaration or malformed-key gate failed. No adoption pass claimed.
- Protected input and historical observation bytes remain compared against original/
  resumed inventories. External Conversations ed53473e indexed Builds68e65f2 is
  preserved; unchanged indexed links against earlier baselines are not falsely
  claimed. This continuation makes no Conversations/Platform pin or indexed-link edit.
- Released-artifact Platform handoff is prepared with exact4.38.0/e171 target and
  current Platformb9cf0e78/pin4.35.2/index0ade5ec2 preserved. No new Platform
  acceptance or application deployment; Story16.1 runtime hold remains ACTIVE.
- Independent review: pending; implementation status remains in-progress until
  the parent completes that workflow step. Final evidence/docs remain local.
