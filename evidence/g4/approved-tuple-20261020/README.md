# Approved 6.1-P0 continuation — 2026-10-20

Current state: the exact G-6 source result is owner accepted. The complete first
3.126.0 P1R campaign was audited with three retained restore-fixture failures;
the corrected frozen verifier is executing a fresh complete campaign. Published
4.35.0 tools are separately proposed and await the user's source/version choice.
The approved local target remains active. P0 acceptance and Projects adoption
remain open.

The user approved the proposed current-catalog preparation target with “I approve”.
The approval authorizes preparation and prerequisite qualification. It does not
accept unseen G-6/P1R/P0 results or authorize fabricated owner decisions.
`approval.json` binds the unchanged proposal and its original tuple snapshot.
`continuation-state.json` records subsequent execution; pending fields in the
nested proposal snapshot are historical, not a revocation of approval.

Both tools were built locally at `4.33.2-p0.20261020.1`, from Builds
`e40ed9c7110d56a2f38c9291627728aea2f205cb`, embedding EventStore `3.126.0`
and FrontComposer `4.7.0`. `local-tool-package-inventory.json` records the clean
source and actual archive hashes. The source/package contract gate passed;
its `releaseEligible` field applies to that gate, not full P0 acceptance.
No tool package was published. The archives remain in
`/var/tmp/hexalith-p0-packages-20261020/`; `local-package-controls/` retains
55 hash-bound contract artifacts.

The frozen qualification workspace is
`/home/administrator/projects/hexalith/p0-approved-qualification-20261020`.
The immutable published-runtime source is separately checked out at
`/home/administrator/projects/hexalith/p0-runtime-3.126.0-20261020`, revision
`fcf78d6cafb6c0c9f52dc350a9a09aff54ded288`, with tag-construction Builds
`1dfc39e3a3e6ab1c8ce95138490b39d76e7feb2c`. Source-mode repairs never
rewrite that tag or the signed published archives. Local qualification commits
are confined to the disposable frozen workspace; shared checkouts have concurrent
owner changes. Nothing was pushed.

Observed host product revisions are also retained: SDK 10.0.402 commit
`a8dd0577c5c13f43a2f57fe33b4ff11b90f6371e`, .NET/AspNetCore 10.0.13
commit `4099f83977f4d29604a9aeb971fb0a7bc1d5f699`, and Aspire CLI 13.6.1
informational revision `3751e615ad660621f2a7aa4c390ef48e08a0eed8`.
These observations do not substitute for the private SDK image/executable hashes.

The exact 3.125.1-to-3.126.0 source comparison changes five production files,
including the payload-protection completion call's I/O wrapper. The public query
API comparison remains unchanged. `runtime-31251-to-31260-source-delta.json`
records the exact revisions and changed files; neither unchanged query APIs nor
a newer catalog version is evidence that recovery/source behavior is identical
or repaired. The live campaign measures that separately.

Fresh primary release evidence closes the earlier unavailable-log observation:
the 3.126.0 release job checked out construction Builds
`1dfc39e3a3e6ab1c8ce95138490b39d76e7feb2c` and executed release actions from
`22a578b576a515d2af214fe81859447fffc97981`. The exact-head CI run used reusable
Builds workflow revision `5c04dab754e269f52306b9582cacd7cf1dc1540f`.
The [release run](https://github.com/Hexalith/Hexalith.EventStore/actions/runs/39165347868)
and [CI run](https://github.com/Hexalith/Hexalith.EventStore/actions/runs/39159607831)
both bind runtime source `fcf78d6cafb6c0c9f52dc350a9a09aff54ded288`.
The downloaded release-evidence ZIP matched the GitHub artifact digest
`15ba6bbbcfbdb83f3b025ed40897f0712b50c3813e5af1f59402dc71cc81293e`.
Selected publication-identity fields and the original entry hash are retained in
`eventstore-31260-publication-identity-observation.json`; filtered actual job-log
identity lines are in `eventstore-31260-release-job-log-identities.json`.
These producer/construction/execution roles stay separate from the current local
tool source and qualifier. The 3.125.1 CI revision is not reused for 3.126.0.

G-6 attempt 1 is retained unchanged. Its real PostgreSQL restart case passed
1/1, deterministic support 33/33 and fixture controls 33/33. Strict capture passed;
all cleanup booleans were true. The overall result failed because Folders
compiled against packages lacking its projection helpers, and the Platform
file host used a shared intermediate directory without enforcing package mode.
The repaired qualifier explicitly names direct workspace source roots and uses
per-project artifacts plus explicit package mode for the file host. Folders and
Platform build probes pass. The projection helper regression suite passed 92/92.

The Folders failure also establishes a package compatibility gap:
`FoldersServerHostComposition` at `eed43a86c94b49e030b233beb37fe6128456f649`
calls `MapEventStoreDomainProjectionEndpoints` and
`MapEventStoreDomainProjectionMetadataEndpoint`. Those helpers are absent from
published DomainService `3.126.0`; the isolated source repair was taken from the
owner's additive `5a38f36cab94e013183ed264b583da61e6eb91d4` refactor and tested
without importing its unrelated changes. Passing the explicitly sourced G-6
build does not establish a passing Folders package build. A released capable
SDK and affected package qualification remain delivery work for that consumer,
alongside the original D1/D4 origin-context/aggregate-bound and D5/D6 owner API gaps.

G-6 attempt 2 passed on frozen Projects
`83c4802be3626560e53d4ef88aa78647e89df589`, with EventStore verifier/source
`47def45665312410b73fa12cb047c6bbaa431cf2` and Builds execution
`e40ed9c7110d56a2f38c9291627728aea2f205cb`. All ten AppHosts and the
McpCli transitive consumer compiled. The live proof passed 1/1, support 33/33,
fixture controls 33/33, strict capture passed, and every cleanup check is true.
Independent `g6_current.py validate` returned zero. Result file SHA-256:
`4660559dc8095d5257814b9d50af9aaacdefa7f3f6f4f9f84aeda53d8be1c8bb`;
canonical artifact SHA-256:
`287663d2a1d5ae25acf64e27a9984780062f26aaa4b8e71a972c73ead4488a08`.
`g6-result-review.json` records the user’s exact-result acceptance (“Accept this
G-6 result”) in Builds, Platform and FrontComposer/Web roles at `2026-10-20T14:40:32Z`.
The previous pending assessment is preserved separately. This was local source qualification
using the CI validation mode, not a remote GitHub Actions run or P0 acceptance.

The existing G-6 pub/sub helper declares `redis:7.4`; its retained launch record
has that tag, not an immutable Redis launch digest. The read-only post-run image
observation is explicitly weaker evidence. It does not replace P0's separately
approved Redis digest or its required persisted proof. P1R uses its existing
immutable pub/sub Redis digest, recorded separately in the observation. No Redis
version or image was silently changed to erase this scope difference.

P1R observation attempts 1–3 are also retained: output outside the mounted
workspace; planning input outside the mount; then SDK mismatch (private image
10.0.401, approved runtime source requires 10.0.402). The new exact source profile
uses the measured Linux/amd64 SDK image in `sdk-100402-bound-profile.json`.
It contains .NET/AspNetCore 10.0.13 and Ubuntu 24.04.5/glibc 2.39; the outer
qualification host is Ubuntu 26.04. Historical source paths retain their original
10.0.401 image/version/executable checks. Cross-profile proof substitution is
rejected. These controls are verifier evidence, not a live P1R pass.

The first full P1R invocation was cancelled after an invocation-path mistake was
identified: output at the umbrella root would fail the canonical final ownership
handoff, which requires output inside the verifier repository. Its passing partial
receipts are diagnostic only. The public CLI exited 2 and the controller retained
exit 130 (`IsolationInterrupted`); two outer cleanup rounds prove all three owned
containers, the volume and scratch absent. No runtime-product failure is inferred
from this operator cancellation. The corrected full invocation uses
`references/Hexalith.EventStore/.p0-qualification/p1r-31260-execution-attempt2`,
with explicit preflight evidence for verifier/workspace containment and absent
canonical output. Versions and frozen source revisions are unchanged.

That corrected invocation completed all 19 lanes and 163 cases: 4,805 of 4,808
scenario assertions passed. The three failures are retained in post-upgrade
restore (append 13, replay 13 and durable head 13); the separate restore receipt
is 10/12 and cleanup 7/7. Executor errors are empty and final private isolation
passed. `p1r-execution-attempt2-measurement.json` indexes all closed receipts.
The independent technical audit validated all 21 closed receipts (19 scenario lanes plus restore and cleanup); all measured checks are not passing. Strict packet preparation correctly refused pending owner disposition. None of this is P1R acceptance.

`p1r-restore-fixture-diagnosis.json` proves the existing retention mutation set
metadata floor 5 while leaving its actor-created source proof at floor 1. The
reconstructed metadata hash exactly matches the restored provider inventory.
The published persister checks those coordinates together before append. A
correction is isolated in `/home/administrator/projects/hexalith/p1r-retention-qualification-20261020`:
it advances both sampled floors, preserves the completed prefix and digest,
refuses an already contradictory proof, and leaves the invalid-floor negative
control invalid. Fresh complete qualification is required. Approved runtime
archives and accepted G-6 source revisions remain unchanged.

Host storage exhaustion blocked two regression-suite attempts and truncated
their local status writes; the raw observations are preserved. Only ignored,
reproducible build outputs in the task-owned G-6 source workspace were removed
(`reproducible-build-cache-cleanup.json`). All retained evidence, P1R consumers,
shared checkouts and Docker resources were preserved. The progress observation
was reconstructed from the original receipts, and now writes atomically.

Local source tests passed: Module 1,116 total with one existing Platform-checkout
skip, Evidence 107/107, G-6 catalog/policy 7 tests (47 catalog controls), and the
repaired Projects G-6 runner 5/5. Package-gate integration had one pass and 12
conditional live skips; none is presented as persisted qualification.
The SDK isolation controls passed 40/40, hardening 30/30 executor 211/211 and published qualification validator 68/68.

P0 live execution requires usable matching P1R and accepted matching G-6.
Final P0 acceptance requires the actual persisted/native, negative, cleanup,
publication/remote-restore and exercised rollback evidence plus the named owner
assessments. Projects consumer adoption follows accepted P0. Missing released
resolution APIs/provider/feed work remains independent delivery work under the
existing D1–D6 designs. No new redesign vote is requested.

Rollback remains unchanged: no accepted prior tool package and no qualified data
downgrade target. Keep the greenfield Builds boundary
`8e0e2da5e1eff07468b41d85d97979c96c2ac975` and AD-17 drain/freeze/forward
recovery. Historical 3.110.0/3.70.1 are comparison versions.

Fresh signing-transformation evidence closes the earlier historical archive
comparison gap without rewriting historical files. For both remote 4.29.1 tools,
removing only NuGet's signature entry reconstructs the exact qualified unsigned
ZIP hash in the original release inventory. `historical-tools-4291-signing-proof.json`
uses the same retained inspection procedure as the 4.35.0 proof. The signed
archive hashes also match the original October 1 observations. This supplies
archive equivalence only: historical tool 4.29.1 still has no full P0 acceptance
and is not promoted to an accepted rollback pin. `historical-tuple-reconciliation.json`
binds the exact historical P1R acceptance separately.

A later catalog refresh observes published tools 4.35.0 at
`fff4a212180d8cd1c5c0759655a9bc4fb6963c45`. Its signed archives, signatures,
embedded catalogs, exact-source CI/release, and producer verification are retained.
Independent signature removal also reconstructs both qualified unsigned archives
exactly (`published-tools-4350-signing-proof-reproduction.json`). A separate
immutable published-tool addendum is pending the user's tool-source decision;
the already approved target is unchanged until that response. Proposed G-6 source
`3f5ab317d1d1dbfafd6102308258818a84e0a322` is prepared with eleven restored
graphs and a zero-issue static audit. No new G-6 live qualification has started.
