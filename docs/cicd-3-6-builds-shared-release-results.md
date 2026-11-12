# Builds shared release caller results

Story 3-6 implements the shared release caller and retires Builds' legacy
qualification CI gates. Implementation starts from Builds
`aca0183fdeb4a7fcd02546ff8f082acbf73a0585`; the enclosing CiCd baseline is
`22387551f497bfef53af37c0c35d86b0d40c8d3f`. Production bootstrap remains a
separate operational step. No release dispatch, publication, retained-resource
mutation, setting change or credential change was performed for this story.

## Implemented behavior

[builds-release.yml](../.github/workflows/builds-release.yml) now calls the
local reusable release with `ci-workflow: builds-ci.yml`, `release-rules:
builds`, named pull-only secrets and exactly contents write/actions read/
security-events write. Top-level permissions are empty, cache mode is none,
and manual dispatch uses serialized `release` concurrency without cancellation.
Shared resolve enforces main. Builds has no setup/delivered state, `buildsTag`
or drift check.

The dependent publisher uses `hexalith-release` and runs only after a successful
`publish=true` handoff. Its only step is `$/Github/publish` from the caller's
revision. It forwards all eight original outputs unchanged: `publish`,
`version`, `sha`, `last-tag`, `draft-id`, `artifact-id`, `artifact-digest`,
`artifact-run`. Only this job has contents write/actions read/OIDC write,
`vars.HEXALITH_NUGET_USER` and the Builds-owned environment push pair
`HEXALITH_REGISTRY_USERNAME/PASSWORD`. There is no publisher checkout or source
build. Existing identity, security, audit, switch, duplicate-safe publication,
tag-last and outcome-ownership controls remain authoritative.

[builds-ci.yml](../.github/workflows/builds-ci.yml) retains the unchanged
`dotnet`, `sample`, dependent `sample-windows`, `catalog`, `quality` and both
Python interpreter lanes. Push/PR triggers target main; manual dispatch remains.
Only PR concurrency cancels by ref. Push/manual groups use SHA and do not cancel,
preserving exact-source CI evidence. The aggregator, entire legacy contracts job
and push/PR CodeQL call were removed. Each retained job now contributes directly
to the workflow conclusion. Release's current-dispatch CodeQL gate remains.

The [plan](cicd-3-6-builds-shared-release-plan.md) lists all fourteen retired
standalone G-4/G-6 qualification/evidence regressions. Their files are deleted,
and the empty mandatory `test/qualification` runner group and fixture
expectations are removed. Discovery still runs every retained Tools Python
script, every Github test group and the mandatory quality group with the
selected interpreter; failures remain nonzero. Existing process fixtures verify
those controls. New existing-suite graph controls evaluate the actual caller
and shared workflow mappings, execute the real reporter, and verify original
complete-mode identity, Builds rules, disabled/invalid/no-version behavior and
failed/cancelled/skipped publication admission. No YAML-source assertions were
added.

All fifteen catalog commands, their existing arguments/languages and SDK
prerequisites remain unchanged. Their complete KEEP classification is in the
[quality documentation](../Tools/ci/README.md). All three .NET product test
projects remain. SDK 11.0.100, offline-fixture SDK 10.0.404, the integrated
catalog data, root npm legacy release configuration and the frozen module
runtime are preserved relative to the integrated upstream base. Only obsolete
Builds-caller zizmor exceptions were removed.

Deferred legacy release remains executable: `verify-g4-tool-release.ps1` calls
`test-g4-tool-package-contracts.ps1`; `build-g4-tool-packages.ps1` calls
`test-g4-packaged-platform-loader.ps1`; contracts call
`test-g4-packaged-seed-subscriptions.ps1`. These helpers and callers remain.
The retained historical G-6 validator's canonical mutation artifact paths still
describe historical evidence identities; their retired regression scripts are
not current executable checks. Guard commitlint and `commitlint-caller.yml`
retirement remain story 5-1 work.

## Implementation source and verification

The initial implementation baseline is
`aca0183fdeb4a7fcd02546ff8f082acbf73a0585`. Eight concurrent upstream commits
were integrated through `6ad5854aa14a9950e1fbfc32d7e27953a1c4b82b` before final
verification, preserving their catalog, validator, runtime and product-fixture
changes. The only modify/delete conflict was the explicitly retired
`Tools/test_g6_current.py` regression; the upstream G-6 runtime/policy remains.
A concurrent Cursor `/pushall` session committed and pushed the exact staged
implementation as `d8b2be323528e624316d4049b7c86d03ba0f74e0`, whose parent is the
integrated revision. No history was rewritten.

- Real actionlint 1.7.12 and authenticated online zizmor 1.32.0 passed, with
  10 unchanged exact frozen composite findings and zero new findings.
- Both full locked Python suites passed, 14/14 retained groups each, on Python
  3.12.13 and the captured runner interpreter Python 3.14.4. All ten matrix rows
  have passing executed behavioral controls on both interpreters; read-only
  operational preflight observations are recorded separately below.
- All 35 focused release-outcome/caller-graph tests and all four real-process
  Python runner controls passed independently.
- The committed-object API check passed at implementation revision
  `d8b2be323528e624316d4049b7c86d03ba0f74e0`: 2,565 entries, no removals, baseline
  `v4.48.2` at `6ad5854aa14a9950e1fbfc32d7e27953a1c4b82b`.
- Structural comparison confirmed all six retained CI job definitions, including
  all fifteen catalog invocations, unchanged. All 118 frozen/pinned/dependency
  files match the integrated upstream revision under tracked checkout filters.
  Two differ from the original starting revision solely through upstream work:
  `Props/Directory.Packages.props` and
  `Tools/test-g4-packaged-platform-loader.ps1`. This story changes neither.
- Exact full implementation and follow-up evidence commit messages passed the
  pinned commitlint 21.2.4 policy with zero problems and zero warnings.

All fifteen exact catalog commands passed, including 113 audit-generator,
101 audit-validator, 101 Platform-catalog and 35 Dapr-validator scenarios.
Local catalog execution took 1,754.76 seconds under concurrent load; its retained
hosted timeout remains 30 minutes. Local full logs use
`/tmp/cicd-3-6-integrated-*.log`; durable summaries, matrix coverage and exact
command statuses are in [the evidence directory](../evidence/cicd-3-6/).

Concurrent upstream `c48c20d0fb2e39ef53888e5061ee27c7b8410344` then selected
Commons `2.30.11` and PolymorphicSerializations `1.19.9` without updating their
revision-bound audit. Eight source-sensitive validators were repeated at
`42cfc1413927b5c55d27835a40bc07c9079f50d9`; seven passed, and audit validation
reported fifteen errors for the stale hash and fourteen old audit selections.
The existing generator refreshed only those two families from committed
declarations and real listed NuGet metadata, preserving all 156 other families
and 304 package rows unchanged. The catalog selections and validator code were
unchanged. The repaired audit passes for all 318 packages and 158 families;
new observations retain the generator's `retained` disposition and make no new
compatibility-acceptance claim. The API check also passes at `42cfc1413927b5c55d27835a40bc07c9079f50d9`.

A subsequent fetch found the same repair already published in upstream
`cac8bf5ca50741f9ece4f75d9633292c78f58d6c`, generated from
`c48c20d0fb2e39ef53888e5061ee27c7b8410344`. Those upstream audit bytes were
retained; the independent temporary reproduction is identified separately in
[audit-refresh.json](../evidence/cicd-3-6/audit-refresh.json). Upstream
`25a6b5a613aca2ee37ebec582f69ad01a3b99e36` and
`1ecbcd6c6f61629086d019c37557d10d4247b0c3` also corrected consumer-validator
diagnostic separation and native discovery success status. All three upstream
commits are preserved. Audit validation and the two affected consumer commands
pass on that final integrated source: 318 packages/158 families, all 31 consumer
projects and all 36 consumer-validator scenarios. The source revision and six
relevant blobs remained unchanged before/after every command. All 118
protected/runtime files match it byte-for-byte under tracked checkout filters.
The release/CI callers, runner, caller tests and lint policy are byte-identical
to the implementation commit tested by hosted CI.

Exact-source [push run 43174705780](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43174705780)
at `d8b2be323528e624316d4049b7c86d03ba0f74e0` passed catalog, quality, both
Python lanes, Linux sample and Windows sample. The sample produced solution
`Samples/Hexalith.Sample/solutions/Hexalith.Sample.slnx`, SDK path
`Samples/Hexalith.Sample/global.json` and submodules `[]`; Windows consumed
those exact outputs. Linux passed four sample tests across unit/hook/integration,
and Windows passed one test, with no failures or skips.

The first .NET job failed in the unchanged
`ReviewDetachedNativeDescendantsAreVerifiedAsync(discoveryVerified: True)`
test at its process-stop observation. Its project builds locally, both focused
cases pass, and its full test class passes all 62 cases without failures or
skips. The failure correctly failed the workflow
without an aggregator; later .NET stages were skipped. Only the failed .NET
job was submitted for retry after the other jobs completed. The retry's final
conclusion is **success**, with all seven retained jobs green on attempt 2.
No runtime/test logic was changed or skipped to obtain that result. The cause
of the first cleanup observation remains unproven. The first attempt is
retained in [hosted-attempt-1.json](../evidence/cicd-3-6/hosted-attempt-1.json),
and the successful exact-source job identities, timestamps and sample outputs
are in [hosted-success.json](../evidence/cicd-3-6/hosted-success.json).

Hosted publication remains outside this implementation; these checks do not
establish production package, artifact, outcome or final-tag publication.

## AD-19 read-only preflight and bootstrap stops

Read-only checks at `2026-11-12T07:11:18Z` and inventory at
`2026-11-12T07:12:03Z` used the configured CiCd-agent profile
`/home/administrator/.config/gh-cicd-agent`, authenticated as `jpiquot`, targeting
`Hexalith/Hexalith.Builds`, with no `GH_TOKEN`/`GITHUB_TOKEN` override. Account
`admin: true` was not used as token-permission evidence. The maintainer owns the
nuget.org policy switch from `production` to `hexalith-release`, caller
`builds-release.yml`, scope `Hexalith.Builds*`; this implementation does not
claim that external change is complete.

Verified: `hexalith-release` has exactly one branch deployment policy, `main`,
with no required reviewer or wait timer. AD-21 rulesets are active with no
bypass actors: Protect main (19196298) forbids deletion/non-fast-forward on the
default branch; v tags (26812219) forbid update/deletion on `refs/tags/v*` and
permit tag creation.

The marked probe draft remains an explicit stop: release ID `441591623`,
`v4.47.1`, source `75e1d4a3809b30684e67c7e9b2c8043720b49c53`, package
`Hexalith.Builds.PublishProbe.20261111`, container `cicd-publish-spike-3-1`.
Its original [producer run 43010609603](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43010609603)
was a successful manual main dispatch of
`.github/workflows/release-publish-probe.yml`. Artifact `13850760915`, named
`release`, remains unexpired until `2027-02-09T11:16:45Z`, digest
`sha256:18db4ed7b07ffa3c5fd4496afce65dd50888705687d42695260e7573a7456d30`.
Its producer-path mismatch with the new caller and nonempty package inventory
require conservative refusal; no identity check was weakened.

The stable-tag inventory contained 203 tags. Two lack a published release:
`v4.47.1` at `75e1d4a3809b30684e67c7e9b2c8043720b49c53` and `v4.46.0` at
`a8a87160f2708322d5520742b9a0c3f93d23db78`. The maintainer must record separate
authorized disposition, reconcile probe-only policies (`publish-spike.yml`
wildcard and `release-publish-probe.yml`) and deferred spike Zot credentials,
then re-inventory drafts/tags. No resources were modified.

Every scoped request below returned HTTP 403, `Resource not accessible by
personal access token`. These settings remain **unverified**, not absent/false.
The advertised read permission is recorded so an authorized read or maintainer
confirmation can resolve each precondition without credential expansion here.
All endpoint paths are relative to the GitHub API.

| Unverified precondition | Endpoint | Required read permission |
| --- | --- | --- |
| Exact boolean switch | `repos/Hexalith/Hexalith.Builds/actions/variables/HEXALITH_RELEASE_PUBLISH_ENABLED` | `actions_variables=read` |
| Repository variables | `repos/Hexalith/Hexalith.Builds/actions/variables` | `actions_variables=read` |
| Builds-owned environment push secret names | `repos/Hexalith/Hexalith.Builds/environments/hexalith-release/secrets` | `environments=read` |
| Repository secret names | `repos/Hexalith/Hexalith.Builds/actions/secrets` | `secrets=read` |
| Immutable releases | `repos/Hexalith/Hexalith.Builds/immutable-releases` | `administration=read` |
| 90-day retention | `repos/Hexalith/Hexalith.Builds/actions/permissions/artifact-and-log-retention` | `administration=read` |
| GitHub App installation | `orgs/Hexalith/installations` | `organization_administration=read` |
| Organization variables and repository access | `orgs/Hexalith/actions/variables` | `organization_actions_variables=read` |
| Organization pull-secret names and repository access | `orgs/Hexalith/actions/secrets` | `organization_secrets=read` |

No production bootstrap is permitted while the retained draft/tags or any
unverified AD-19 precondition remains. A later explicitly authorized bootstrap
must repeat the complete preflight and record its exact source, original
artifact, package publication, sole outcome and final tag; `ready` requires
AD-18 checks and separate maintainer publication. No positive hosted production
handoff or publication claim is made by these local controls or earlier
invalid-switch probes.
