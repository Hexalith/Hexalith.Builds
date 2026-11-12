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
projects remain. SDK 11.0.100, offline-fixture SDK 10.0.404, catalog data,
root npm legacy release configuration and the frozen module runtime are
preserved. Only obsolete Builds-caller zizmor exceptions were removed.

Deferred legacy release remains executable: `verify-g4-tool-release.ps1` calls
`test-g4-tool-package-contracts.ps1`; `build-g4-tool-packages.ps1` calls
`test-g4-packaged-platform-loader.ps1`; contracts call
`test-g4-packaged-seed-subscriptions.ps1`. These helpers and callers remain.
The retained historical G-6 validator's canonical mutation artifact paths still
describe historical evidence identities; their retired regression scripts are
not current executable checks. Guard commitlint and `commitlint-caller.yml`
retirement remain story 5-1 work.

## Local verification

- Real actionlint 1.7.12 and authenticated online zizmor 1.32.0 passed, with
  10 unchanged exact frozen composite findings and zero new findings.
- Focused real-process Python runner controls passed: 4 tests on Python 3.14.4.
- Full locked Python 3.12.13 and captured runner Python 3.14.4 suites started
  with 14 retained groups each. They were deliberately stopped during the
  Github/lib group before completion when upstream advancement was discovered.
  No full-suite pass or execution claim for the new caller-graph tests is made.
- The first six exact catalog invocations passed: central versions,
  authoritative catalog, logging selection, central validator, package audit,
  and audit generator (113 scenarios). Execution was stopped during the seventh,
  audit-validator command. The remaining nine command conclusions are unverified.
- Structural comparison confirmed all six retained CI job definitions unchanged,
  including every catalog invocation. A byte comparison against the baseline,
  applying tracked checkout filters, confirmed 118 frozen runtime/pinned-data/
  legacy-dependency files unchanged. No module repository was edited.
- Committed-object public API checking and exact-source hosted push CI are
  recorded after the implementation commit. Local controls do not establish
  hosted package/artifact/tag publication.

Local command logs are retained under `/tmp/cicd-3-6-*.log` in the implementation
workspace. Preservation comparison results are in
`/tmp/cicd-3-6-preservation.json`; completed catalog command/exit-status results
and the explicit interruption status are in `/tmp/cicd-3-6-catalog-results.json`.

During verification, a fetch revealed eight new upstream commits ending at
`6ad5854aa14a9950e1fbfc32d7e27953a1c4b82b`. They change catalog/validator/product
fixtures and the `Tools/test_g6_current.py` regression retired by this story.
The parent stopped all three broad verification process trees before combining
the implementation with upstream. No combined-tree check has passed yet.
Preserve upstream catalog/validator/product changes during integration, resolve
the retired regression deliberately, then rerun the affected full dual-Python,
all-fifteen catalog, lint, preservation and committed-API checks before the
exact-source hosted push observation.

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
