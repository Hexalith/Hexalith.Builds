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
NuGet discovers the HTTPS `api.nuget.org` package content resource and uses
exact-version content HEAD, including unlisted packages. Registry HEAD accepts
OCI and Docker manifests/indexes. Only authoritative 404 means absent;
uncertainty retains the draft. Explicit empty inventories require no requests.

| Boundary | Credentials / effect |
| --- | --- |
| GitHub recovery | Contents write and Actions read; no OIDC or attestation permission |
| NuGet existence | No credentials; no redirects |
| Registry existence | Existing endpoint-bound pull-only pair; no push credentials |
| Confirmed stale draft | Revalidate unchanged marked draft; DELETE only its release ID; require HTTP 204 |
| Valid original artifact | `publish=true`; reusable reporter uploads no outcome; existing publisher owns it |
| Confirmed stale deletion | `publish=false`, internal `stale-draft-deleted`; one reusable `nothing` outcome |
| Existing/uncertain destination | Preserve draft; fail `draft vX.Y.Z cannot be completed: its build artifact is gone` |
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

Final focused validation passes 180 release tests and 3 OCI publisher tests.
The [matrix audit](../evidence/cicd-3-5/matrix-audit.json) maps all 17 required
rows to executed passing tests. Controls execute the actual complete adapter,
actual reusable graph/reporter and existing publisher, with local HTTP and
real disposable Git histories. They cover all marker/producer mismatches,
expiry plus digest conflict, signed-download 404/206 refusal, metadata 202
refusal, unlisted NuGet presence, mixed/empty inventory, uncertain reads,
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
