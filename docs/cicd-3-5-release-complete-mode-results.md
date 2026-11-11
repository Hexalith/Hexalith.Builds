# Story 3-5: complete a release from its original artifact

Complete mode extends the existing draft stage. Resolve's saved draft SHA,
version and base remain fixed; version and build skip. Recovery authenticates
the bot draft and its five trailers, the original manual-dispatch caller/run,
exact artifact ID/API digest, ZIP digest, inventory and package/OCI identities.
It returns the original eight-field publisher handoff. The publisher continues
to authenticate independently and retains its duplicate-safe behavior.

Recovery uses trusted stdlib Python and bare Git data reads. It executes no
source, historical configuration, CI, audit, SDK, checkout, login or push.
CodeQL and the nightly hook still use the current dispatch configuration/SHA.
Create-mode selection and publisher conflict/tag-last checks remain unchanged.

An unavailable or identity-mismatching producer, definite artifact loss, or
validated expiry permits only the stale decision. Malformed responses,
authorization/transport errors, API digest disagreement (including expired
artifacts), invalid expiry and corrupt downloaded content fail without deletion.
Any authenticated listed NuGet package now retains the draft and fails when
the original artifact is unavailable. NuGet content 404 during validation or
indexing cannot prove a package was never pushed; unlisted packages, elapsed
time and mixed package/image inventories do not relax this rule. Recovery makes
no NuGet request and has no quiet period. Manual disposition requires a separate
human decision that preserves the prohibition on reusing a version with
different content. Usable original bytes still permit normal completion.

Automatic deletion requires an explicitly empty package inventory and definite
HTTP 404 for every listed container version. Registry HEAD accepts OCI and
Docker manifests/indexes and uses only the existing endpoint-bound pull pair.
Published images, missing credentials, redirects, throttling, authorization
errors, malformed responses and timeouts retain the draft. Explicit empty
package and container inventories require no destination requests.

| Boundary | Credentials / effect |
| --- | --- |
| GitHub recovery | Contents write and Actions read; no OIDC or attestation permission |
| Any saved NuGet package with unavailable artifact | Retain and fail; no NuGet lookup, credentials or quiet period |
| Registry existence | Existing endpoint-bound pull-only pair; no push credentials |
| Confirmed stale draft | Revalidate unchanged marked draft; DELETE only its release ID; require HTTP 204 |
| Valid original artifact | `publish=true`; reusable reporter uploads no outcome; existing publisher owns it |
| Confirmed stale deletion | `publish=false`, internal `stale-draft-deleted`; one reusable `nothing` outcome |
| Package-bearing inventory or existing/uncertain image | Preserve draft; fail `draft vX.Y.Z cannot be completed: its build artifact is gone` |
| Gate failure, cancellation, invalid switch or unexpected skip | Fail; no publisher authorization |

Stale recovery creates no replacement draft/build/version and never changes a
Git tag or published package/image. Frozen legacy runtime, production callers,
`builds-release.yml`, npm release configuration and retained story 3-4 resources
are preserved. Drift integration, caller rewrite and nightly/App publication
remain downstream work.

Read-only hosted preflight is retained in
[hosted-preflight.json](../evidence/cicd-3-5/hosted-preflight.json).
The scoped CiCd-agent profile is authenticated as `jpiquot` for
`Hexalith/Hexalith.Builds`, with no token environment override. The REST read of
`HEXALITH_RELEASE_PUBLISH_ENABLED` returned HTTP 403, `Resource not accessible
by personal access token`; the response requires `actions_variables=read`.
That setting remains unverified by the external session. No settings or
credential changes were attempted.

The original producer is run `43010609603`, manual dispatch on main at
`75e1d4a3809b30684e67c7e9b2c8043720b49c53`, through
`.github/workflows/release-publish-probe.yml`. Retained bot draft `441591623`
is `v4.47.1` and names original artifact `13850760915`. The read-only
[original-byte preflight](../evidence/cicd-3-5/original-byte-preflight.json)
downloaded and validated all 83,256,215 bytes with digest
`sha256:18db4ed7b07ffa3c5fd4496afce65dd50888705687d42695260e7573a7456d30`,
including package `Hexalith.Builds.PublishProbe.20261111` and container
`cicd-publish-spike-3-1`. The artifact is unexpired until
`2027-02-09T11:16:45Z`. This observation verifies original bytes locally; it
alone makes no reusable-workflow or publication claim.

The initial implementation's focused validation passed 180 release tests and
3 OCI publisher tests.
The [matrix audit](../evidence/cicd-3-5/matrix-audit.json) maps all 17 required
rows to executed passing tests. Controls execute the actual complete adapter,
actual reusable graph/reporter and existing publisher, with local HTTP and
real disposable Git histories. They cover all marker/producer mismatches,
expiry plus digest conflict, signed-download 404/206 refusal, metadata 202
refusal, the former NuGet lookup policy, mixed/empty inventory, uncertain reads,
changed draft and exact DELETE status. The publisher retry observes identical
package bytes across an interrupted push and duplicate-safe replay; existing
OCI controls verify the complete graph and retry. These are simulated
publication/deletion effects, not hosted writes.

Pinned actionlint and online zizmor pass with 12 exact frozen findings and
zero new findings. The committed-object API candidate
`6760ca2182c98bae6a30bb7d0a7fd7a6c2ea09ba` passes with 2,558 entries and no
removals versus the highest reachable stable tag `v4.47.1` at
`75e1d4a3809b30684e67c7e9b2c8043720b49c53`. That retained probe tag affects
baseline selection as already documented in story 3-4. Both whitespace checks
and the [scope comparison](../evidence/cicd-3-5/scope.json) pass. The exact
implementation message passed lock-pinned commitlint 21.2.4 with zero problems
or warnings.

The original implementation baseline is
`329a4a23483fe1aa4710ebf82768b5a52b30fcd9`. Three independently pushed upstream
commits advanced Builds to `a3a3cf2959cc8020c37fc980a05d4cf6eb7a265b` during
implementation. They change only the catalog audit/props and SDK test controls;
release runtime, guidance and commit tooling were unchanged. A task-only
stash, fast-forward and clean restore preserved them before full validation.
Their four files are upstream changes, outside this story's delivery delta.
Compact verification logs and command/result records are retained in
[evidence/cicd-3-5](../evidence/cicd-3-5/); full raw diagnostic logs are local
`/tmp` artifacts identified by their hashes in those records.


Review added explicit refusal of enabled-but-skipped CodeQL/nightly gates
before any draft processing, and a second unchanged-draft validation after
artifact authentication before the publication handoff. Regressions cover both
security skips, draft edits/publication during download, and a real original
multi-platform OCI artifact plus container-trailer disagreement. Final check
records bind the tested HEAD and runtime/test source hashes to an immutable
source manifest, and verify those files stayed unchanged throughout each run.

A second clean integration preserved three additional upstream commits ending
at `e35dd39872baf071aec6a7be1b252b16f460e08c`. They add offline SDK packs and
host diagnostics to Builds/shared CI, update the catalog/audit and package
probe, and add a separate quality-guide paragraph. Their files are upstream
changes; this story preserves them. Final validation runs against that combined
base plus the reviewed release changes.

After review fixes, both locked complete suites pass 20/20 groups on Python
3.12.13 and runner Python 3.14.4. The final focused runs pass 180 release and
3 OCI tests; all 17 matrix rows retain executed passing coverage. Final pinned
online lint has zero new findings. Committed-object candidate
`7c8325104f2826421adf69d35d07fbf6dd1bd03d` passes the public API check with
2,560 entries and no removals; the additional entries are preserved upstream
CI setup inputs. Final source-bound records use the `final-` prefix in the
evidence directory and attest unchanged runtime/test bytes throughout each run.

The reviewed runtime was committed/pushed as
`1713722053b0c1944ee2f9dad5c09c56a08951a6`; the
[source binding](../evidence/cicd-3-5/runtime-source-binding.json) proves all
33 tested runtime/test files match that pushed commit. Temporary caller/helper
revision `962440af99c1f12d91eec192715210c83a728344` pins that runtime literally
and was dispatched through the original producer's exact workflow path.
[Hosted run 43035532396](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43035532396)
reached complete mode with original source
`75e1d4a3809b30684e67c7e9b2c8043720b49c53`, but resolve failed with
`HEXALITH_RELEASE_PUBLISH_ENABLED must be exactly true or false`.
This is the exact enabled-positive verification blocker; the effective value
fails the strict switch contract. The external variable-read HTTP 403 does not
establish whether the setting exists. No setting or credential was changed,
and no temporary true-switch injection or bypass was used.

CodeQL succeeded at dispatch revision
`962440af99c1f12d91eec192715210c83a728344` despite resolve failure; the disabled
nightly hook and version/build/draft jobs skipped. The reporter uploaded exactly
one schema-valid `failed` outcome at the original SHA/version. The observer's
positive expectation intentionally failed and retained the actual
`publish=false` handoff with empty draft/artifact fields. There is no publisher
job. [Run](../evidence/cicd-3-5/hosted-run.json),
[jobs](../evidence/cicd-3-5/hosted-jobs.json),
[artifacts](../evidence/cicd-3-5/hosted-artifacts.json),
[observed handoff](../evidence/cicd-3-5/hosted-handoff/complete-handoff.json),
[outcome](../evidence/cicd-3-5/hosted-outcome/outcome.json), and
[exact blocker](../evidence/cicd-3-5/hosted-switch-blocker.json) are retained.
The [resource readback](../evidence/cicd-3-5/hosted-resource-readback.json)
confirms unchanged draft/body/source and tag. Positive hosted original-artifact
handoff is **not verified**; the passing local controls establish that branch.
No release/package/image was published or deleted by this probe.

Temporary controls are removed after preserving their exact pinned
[patch](../evidence/cicd-3-5/probe-controls.patch). Normal retention and production
callers remain unchanged. Review patched two runtime boundaries, added an OCI
regression and source-bound evidence, and rejected synthetic/out-of-contract
claims individually in the canonical story. One external concurrency question
is deferred: mutation by an out-of-band editor/publisher after the final GET and
before DELETE was not destructively qualified. Immediate revalidation and
standard release concurrency remain the implemented safeguards; an authorized
disposable two-actor experiment and supported serialization/atomicity decision
would settle that residual question. The [GitHub release API](https://docs.github.com/en/rest/releases/releases#delete-a-release)
defines the release-ID deletion/204 contract; no additional atomic guarantee
is claimed here. Follow-up review is recommended for that unverified boundary
and for enabled positive hosted qualification once the switch contract is met.

The human-approved package-retention re-drive amends Builds main at
`a9f443f0bd482359731dcd54711e44fbd23d58fe`, preserving delivered recovery
`dd68a5efbfaec13ef22ec06ad151b212eb16ea4f` and all subsequent commits. It
replaces the former NuGet absence decision with unconditional retention for
any authenticated nonempty package inventory when the original bytes are
unavailable. The removed NuGet lookup cannot authorize deletion. Container-only
and explicitly empty-inventory cleanup retain the existing revalidation and
HTTP 204 requirement; registry HEAD and publisher authentication are unchanged.

Current focused verification passes 182 release tests and 3 OCI publisher
tests. The [policy controls](../evidence/cicd-3-5/package-retention-controls.json)
map package-only and mixed refusals across missing/mismatched producers,
metadata/archive disappearance, expiry and years-old expiry; permitted
container-only deletion; present/uncertain images; exact pull credential
forwarding through the real registry and completion entry point; the actual
adapter/graph/reporter's sole failed outcome; and identical original package
retry through the existing publisher. Failure controls trap accidental stale
routing, use empty inventories where deletion must remain observable, and
block outbound HTTPS in adapter subprocesses. Valid JSON served with HTTP 202
is rejected for both producer and artifact metadata. Non-dispatch/non-main and
invalid-mode controls perform no API request. The historical matrix above
records the earlier policy; these controls supersede its NuGet absence cases.
[Release](../evidence/cicd-3-5/package-retention-focused-release.json) and
[OCI](../evidence/cicd-3-5/package-retention-focused-oci.json) records bind the
starting HEAD and both changed runtime/test file hashes, verify unchanged
source throughout each check, and retain compact logs plus raw-log hashes.

On the original starting baseline, the pinned workflow lint command failed on four findings in
frozen `.github/workflows/domain-ci.yml`: `artipacked` at `1254:9` and
`ref-version-mismatch` at `1129:100`, `1267:107` and `1319:100`. A disposable
extraction of the starting commit produces the identical four findings under
the same online pinned check. The [comparison](../evidence/cicd-3-5/package-retention-lint-comparison.json)
records both failures; the [scope manifest](../evidence/cicd-3-5/package-retention-scope.json)
confirms that all 47 lint inputs/configuration files are byte-identical to the
starting baseline and frozen paths are preserved. This is a failed required
gate, not a passing lint claim. No frozen workflow or audit exception was
changed to bypass it.

Before upstream integration, both prescribed locked complete-suite commands finished with 18/20 groups
passing, under Python 3.12.13 and uv-selected Python 3.13.13 respectively
(`--python python3` selects the latter in this workspace; bare `python3` is
3.14.4 and ran the focused release/OCI checks). Each complete suite passed the
shared release library group. Their original failures were the unchanged
governed-provenance group and the Tools/ci online-policy test, whose initial
environment omitted its required read-only token. Focused retries of all 46
Tools/ci tests under both selected interpreters pass with the existing scoped
token supplied privately; the runner uses the same committed dependency lock. The original
full-run failures and corrected focused results are retained separately in the
[verification summary](../evidence/cicd-3-5/package-retention-verification.json).

The eight governed-provenance errors also reproduce on a disposable extraction
of the starting baseline: its evaluator rejects frozen
`domain-ci.yml:1070`'s `$/Github/initialize-apt` reference because that legacy
closure requires a literal lowercase 40-hex commit. The
[provenance comparison](../evidence/cicd-3-5/package-retention-provenance-comparison.json)
records all eight matching errors and identical workflow/evaluator/test hashes.
Those original provenance and workflow-lint failures were required-gate
blockers; neither original complete suite is reported as passing.

This re-drive performs no hosted probe, setting change, release mutation or
publication. The previously recorded positive-hosted switch blocker and
unverified external final-GET/DELETE race remain unchanged. The
[current matrix audit](../evidence/cicd-3-5/package-retention-matrix-audit.json)
confirms all 18 current rows have executed passing coverage. The canonical story
records the final delivery revision and remote readback.

The pre-push fetch found 23 upstream commits through
`163f3d0deac6b0db5ec4bdfcc0934b12e6adb46a`, including existing repairs to the
workflow lint locations and the governed action reference. A task-only stash,
fast-forward and clean restore preserved these commits without changing this
story's tested runtime or tests. On the integrated tree, pinned online lint
passes with 10 exact frozen findings and zero new findings, and all 90
governed-provenance tests pass. These results supersede the historical
baseline failures above; the
[integration scope](../evidence/cicd-3-5/package-retention-integrated-scope.json)
separates upstream paths and this story's five tracked paths. Fresh complete
suite verification is recorded separately for the integrated tree.

Both fresh integrated complete suites pass **22/22 groups**, under Python
3.12.13 and 3.13.13. The [3.12 record](../evidence/cicd-3-5/package-retention-integrated-full-python312.json)
and [3.13 record](../evidence/cicd-3-5/package-retention-integrated-full-runner.json)
bind full integration HEAD and unchanged runtime/test hashes. The required
scoped token was supplied privately to online-policy tests. Integrated
committed-source [API validation](../evidence/cicd-3-5/package-retention-integrated-public-api.json)
passes with 2,565 entries and no removals. All previously failing required
gates are resolved on the integrated tree.

The four-layer review found no runtime-policy defect or new intent gap. Nine
low findings were corrected in eight groups: proxy-independent test isolation,
timestamp-only expiry coverage, baseline/archive source binding, explicit scope,
durable test details with group/negative-fixture context, reproducible wrapper
records and the current lint count. One optional composed cleanup fixture was
rejected because its existing execution seams already have passing controls
and adding the transport adds substantial test machinery. Historical planning,
hosted qualification and the already-recorded external race remain explicitly
distinguished. The [triage record](../evidence/cicd-3-5/package-retention-review-triage.json)
retains every finding and disposition. Production recovery bytes did not change
during these review corrections. Final verification is recorded separately.

After review corrections, both final complete suites again pass **22/22 groups**
on Python 3.12.13 and 3.13.13. Final focused verification passes **182 release
tests and 3 OCI publisher tests**; pinned online lint passes with **10 exact
frozen findings and zero new findings**, and committed-source API validation
passes with **2,565 entries and no removals**. The
[verification summary](../evidence/cicd-3-5/package-retention-verification.json)
links every final source-bound command and complete sanitized durable output.
The [final matrix audit](../evidence/cicd-3-5/package-retention-matrix-audit.json)
confirms all 18 rows ran and passed against the corrected tree. Whitespace and
tracked-path preservation checks pass. All new patch groups are low; no new
deferral or follow-up review is recommended. The existing hosted-positive
blocker and external GET/DELETE qualification limit remain documented above.
