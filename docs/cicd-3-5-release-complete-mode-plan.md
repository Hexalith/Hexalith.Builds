---
story: '3-5'
created: '2026-11-11'
status: ready-for-dev
planning_only: true
baseline_revision: cbdfdd5443a4d2315b64d47bdc907cae7edf8d66
implementation_repository: references/Hexalith.Builds
implementation_baseline_revision: fb027a87afcdf7661c8f80ade89231b7730b60d1
---

# Release complete-mode plan

Story 3-5 completes a marked draft from the original artifact for CAP-12,
AD-16/17. This delivery contains planning only. The canonical workflow state,
full matrix, unchecked implementation tasks and acceptance contract are in
the enclosing CiCd repository at
`_bmad-output/specs/spec-cicd/stories/3-5-complete-mode-reruns-finish-from-the-original-artifact.md`.

## Recovery contract

Extend the existing draft stage to support complete mode. Resolve already
selects the marked draft's frozen SHA/version, reads only dispatch-SHA config
and skips historical CI. Version and build remain create-only. Preserve
current-dispatch CodeQL/nightly jobs, the exact publishing switch, create-mode
behavior and the publisher's independent checks. No historical checkout,
configuration loading, drift/CI/audit check or rebuild belongs in recovery.

Read exactly the five persisted trailers, including explicit empty package
and container lists. Validate bot ownership, full source/target agreement,
stable version, base ancestry, unique identifiers, positive run/artifact IDs
and SHA-256 digest. Use that saved run and exact artifact ID, never a name
search or current-run replacement. The producer must have the current caller
workflow path from `github.workflow_ref`, event `workflow_dispatch`, branch
`main` and the draft SHA. Validate original artifact name/run/source/expiry/API
digest, downloaded ZIP bytes, manifest and package/OCI identities against the
draft before returning the existing eight-field publisher handoff.

Missing or identity-mismatching producer runs count as no artifact. Definitive
artifact disappearance or validated expiry also enters the stale branch.
API digest disagreement is always fatal, including when expiry is reported.
Wrong artifact identity, corrupt payload, malformed expiry, authorization,
transport, unexpected redirects and signed-storage errors fail without
deletion. Add structured HTTP classification; do not parse exception messages
to infer absence. Keep create/publisher authentication strict.

When the artifact is gone, delete the draft only if every listed package at
that exact version and every listed container version tag is definitely absent.
For NuGet, discover `PackageBaseAddress/3.0.0` from
`https://api.nuget.org/v3/index.json`, require an HTTPS `api.nuget.org`
resource and HEAD the lowercase ID/version content URL, including unlisted
packages, without credentials or redirects. For the fixed registry endpoint,
use bounded HEAD with OCI and
Docker manifest/index media types. Only authoritative 404 means absent;
existence, missing credentials, redirects, errors, malformed data and timeouts
all count as existing for deletion. Explicit empty inventories require no
destination requests.

If any destination exists or is uncertain, retain the draft and fail exactly:

```text
draft vX.Y.Z cannot be completed: its build artifact is gone
```

After all absence checks pass, re-fetch and revalidate the unchanged live
marked draft, DELETE only its release ID and require HTTP 204. Emit
`stale-draft-deleted`, `publish=false` and one `nothing` outcome. Do not start
create mode, create another draft/version or delete/move a tag in that run.
Never delete published package/image content or touch unmarked drafts.

## Ordered implementation and reuse

1. [Draft helpers](../Github/lib/release_draft.py),
   [release state](../Github/lib/release_state.py) and
   [publisher](../Github/lib/release_publish.py): share strict trailer and pure
   producer identity validation, minimally add status-aware missing-resource
   handling, and preserve existing transport/size/security guarantees.
2. New `Github/lib/release_complete.py`: authenticate the original handoff,
   implement NuGet exact-version existence and stale-deletion orchestration.
   Reuse artifact metadata/inventory validators and source/base ancestry checks;
   do not apply create-time strict-successor rules to an existing AD-16 draft.
3. [OCI publisher](../Github/lib/oci_publish.py): add a bounded read-only
   existence method at `registry.hexalith.com`, preserving publisher behavior.
   Recovery receives the existing pull-only pair, independent of current
   `config.registry`; it never receives push credentials.
4. [Draft adapter](../Github/release-draft/action.yml) and
   [reusable release](../.github/workflows/release.yml): add compatible
   default-create mode and selected draft/base inputs, conditionally validate
   create-only inputs, retain all eight outputs and add an internal no-op
   output. Admit complete draft execution despite skipped version/build after
   successful resolve/security. Keep draft permissions exactly Contents write
   and Actions read, trusted action-path stdlib execution and no cache.
5. [Outcome reducer](../Github/lib/release_outcome.py): remove its temporary
   missing-recovery exemption; accept stale no-op only from a successful
   complete-mode draft's matching output. Preserve failure/cancellation
   precedence and exclusive publisher ownership. No schema change is needed.
6. Add `Github/lib/tests/test_release_complete.py` and extend existing
   draft/state/publisher/adapter/OCI/outcome tests. Use real Git and local HTTP
   effects, actual composite mappings and the workflow expression/reporter
   harness. Cover the full canonical matrix, trap historical build/CI/config
   calls, prove original run/artifact/digest outputs and assert actual API
   writes/outcomes. Existing publisher retry controls prove duplicate-safe
   completion from the same bytes. No workflow-source assertions.
7. Update [quality guide](../Tools/ci/README.md),
   [publisher guide](../Github/publish/README.md) and new
   `docs/cicd-3-5-release-complete-mode-results.md` with interfaces, evidence and
   limits. During implementation only, use temporary
   `.github/workflows/release-publish-probe.yml` and
   `Tools/ci/probe_release_complete.py` for hosted handoff verification without
   a publisher job, then remove the temporary controls.

## Hosted verification and preservation

Read-only planning checks on 2026-11-11 confirmed retained bot draft
`441591623` (`v4.47.1`) names original run `43010609603` and artifact
`13850760915`, named `release`, unexpired until `2027-02-09T11:16:45Z`, with
digest `sha256:18db4ed7b07ffa3c5fd4496afce65dd50888705687d42695260e7573a7456d30`.
The producer caller path is `.github/workflows/release-publish-probe.yml`.
Metadata checks do not establish original-byte validation or recovery success.
[Story 3-4 results](cicd-3-4-publish-composite-results.md) retain its full
publication identities and the resources that must be preserved.

A later hosted probe must use that same caller path and revalidate all saved
identities; a different path correctly fails producer authentication. Do not
introduce a second marked draft, mutate/delete the retained draft, change its
credentials/policy or publish again. Invoke the actual reusable graph and
record skipped version/build and all original handoff outputs in a probe
artifact. The reusable reporter must transfer ownership without uploading an
outcome on successful handoff. Destructive stale and uncertain-read branches
use local HTTP controls. The restricted token's REST read of the publishing
variable returned HTTP 403 during planning, so that setting remains unverified;
the unsupported `gh variable get` command supplied no evidence. Future hosted
positive verification requires enabled publishing. If it cannot run, record
the exact verification blocker without changing settings or claiming success.

Preserve AD-20 frozen runtime, production callers/`builds-release.yml`, root
package/release configuration, spike evidence and earlier unrelated deferrals.
Story 5-1 owns drift checking; caller rewrite and nightly/App publication stay
downstream. Completion uses the existing publisher; a workflow-tree change can
still end `ready`, for the App's later publication. No provenance or new OIDC
permission is added.

Protocol references: [NuGet package content](https://learn.microsoft.com/en-us/nuget/api/package-base-address-resource),
[Distribution manifest existence](https://distribution.github.io/distribution/spec/api/#existing-manifests)
and [GitHub release deletion](https://docs.github.com/en/rest/releases/releases#delete-a-release).

## Validation and planning delivery

Future implementation runs focused release/OCI tests, the retained locked
Python 3.12 and runner suites, pinned online workflow lint and committed-source
API checks, plus frozen-file comparison and the hosted handoff probe. Full
commands and Given/When/Then acceptance are in the canonical story. Distinguish
local simulated branches from hosted observations.

This planning invocation checks both frontmatters, current date, full canonical
Git baselines, source links, unchecked implementation tasks, document-only scope
and whitespace. Validate the exact full message with pinned commitlint 21.2.4,
fetch and inspect divergence, commit/push this companion only and read back the
full remote revision. Stage the canonical ready-for-dev story and updated Builds
gitlink in CiCd without a CiCd commit/push. Halt after planning; no runtime
implementation, live probe, release or publication is performed now.
