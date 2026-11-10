---
story: '3-1'
created: '2026-11-10'
status: in-progress
implementation_baseline_revision: 15c108131c7127dbff10dbbb53d1701f123dfe29
---

# Publish composite spike results

## Resumed prerequisite gate — 2026-11-10

The earlier blocked check below is historical and is superseded by the
maintainer's provisioning recorded in the canonical story. A fresh read-only
check using the configured CiCd-agent profile confirmed Builds' environment
`hexalith-release`, exactly one allowed deployment branch `main`, and no
reviewer or timer. Token override variables were absent; repository identity
and authenticated account were checked. Protected variable metadata returned
HTTP 403, an access limit which does not negate the dated provisioning.

The supplied destination is `registry.hexalith.com/cicd-publish-spike-3-1`,
tag `0.0.0-spike.20261110`; dedicated principal `cicd-publish-spike-3-1` has
read/create/update only on that repository, with no delete grant. The caller
environment supplies `HEXALITH_REGISTRY_PUSH_USERNAME` and
`HEXALITH_REGISTRY_PUSH_PASSWORD` explicitly, and records destination/tag in
`HEXALITH_REGISTRY_SPIKE_REPOSITORY` and `HEXALITH_REGISTRY_SPIKE_TAG`.
`HEXALITH_NUGET_USER` is the maintainer-confirmed organization variable selected
for Builds; matching `publish-spike.yml` / `hexalith-release` NuGet policy setup
is maintainer-confirmed. This gate passed before executable file creation.
Runtime authentication remains experimental and must succeed independently.

## Historical blocked attempt

Execution stopped at the prerequisite gate on 2026-11-10. The required
`hexalith-release` environment does not exist in `Hexalith/Hexalith.Builds`.
No executable probe files were created, no workflow was dispatched, and no
NuGet login exchange, package publication, registry write or provenance
experiment was attempted. OCI transport, login placement and signed provenance
remain unproven; this result makes no composite-failure or fallback verdict.

The [execution plan](cicd-3-1-publish-composite-spike-plan.md) describes the
prerequisites and subsequent experiment. The canonical workflow record remains
in the enclosing CiCd repository at
`_bmad-output/specs/spec-cicd/stories/3-1-spike-oci-containers-and-nuget-login-in-the-publish-composite.md`.

## Prerequisite evidence

Read-only GitHub REST checks ran with the existing CiCd-agent credential on
2026-11-10, at approximately `01:34 UTC`. Only non-secret response fields and
diagnostics are retained below. HTTP authorization failures establish the
limits of this session's access; they do not establish that a secret, variable
or policy is absent.

| Required prerequisite | Observed evidence | Result |
| --- | --- | --- |
| Caller-owned `hexalith-release`, restricted to `main`, with no reviewers or timer | Listing Builds environments returned exactly one environment, `production`. Direct lookup of `hexalith-release` returned HTTP 404. | **Missing.** A publish job cannot use the required environment and its scoped credentials. |
| nuget.org policy for `Hexalith/Hexalith.Builds`, `publish-spike.yml`, `hexalith-release`, and the permitted package scope | No matching policy evidence is available. Prior story-1 evidence covers `builds-release.yml` / `production`; its attended nuget.org browser session was closed after planning. | **Unverified.** An existing release policy does not authorize the proposed caller. |
| Accessible `HEXALITH_NUGET_USER` | Reading the organization variable returned HTTP 403. Story 1 previously recorded selected visibility for Builds, but current access was not independently verified. | **Unverified.** Retain the existing variable; provide current policy and availability evidence before execution. |
| Isolated Zot repository and its scoped push username/password, available only to the publish job | No isolated destination, version-shaped tag, push principal or scoped credential pair was supplied. The required environment is absent; repository secret metadata also returned HTTP 403. Prior story-1 evidence proves a separate pull-only identity, which cannot satisfy this prerequisite. | **Unavailable to this session.** Do not reuse legacy or pull-only credentials. |

The decisive read-only commands and sanitized results were:

```console
$ gh api repos/Hexalith/Hexalith.Builds/environments --jq '{total_count, environments: [.environments[] | {name,protection_rules,deployment_branch_policy}]}'
HTTP 200; total_count: 1; environment names: ["production"]

$ gh api repos/Hexalith/Hexalith.Builds/environments/hexalith-release --jq '{name,protection_rules,deployment_branch_policy}'
HTTP 404; Not Found

$ gh api orgs/Hexalith/actions/variables/HEXALITH_NUGET_USER --jq '{name,value,visibility,selected_repositories_url}'
HTTP 403; Resource not accessible by personal access token

$ gh api repos/Hexalith/Hexalith.Builds/actions/variables --jq '{total_count,variables:[.variables[]|{name,value}]}'
HTTP 403; Resource not accessible by personal access token

$ gh api repos/Hexalith/Hexalith.Builds/actions/secrets --jq '{total_count, names: [.secrets[].name]}'
HTTP 403; Resource not accessible by personal access token
```

These endpoints were queried with GET only. The existing `production`
environment, NuGet policies, legacy credentials and release workflow were
preserved. Missing configuration was not replaced or weakened to run a probe.

## Execution identity and unperformed proof

- Builds was on `main`, initially clean, at full revision
  `15c108131c7127dbff10dbbb53d1701f123dfe29`, the story's implementation baseline.
- The canonical CiCd story's baseline remains
  `7eb1fd990bcee6075c6a8729e6ab3cf934482ca6`.
- No experimental caller/action source revision, hosted run URL, run ID or
  attempt exists. No original artifact run/ID/digest, archive hash, root/child
  manifest digest or platform observation exists.
- No Skopeo distribution or integrity pin was selected, no SDK archive format
  was observed, and no original-artifact replay occurred.
- Neither nested nor caller NuGet login was attempted. A missing prerequisite
  cannot distinguish their behavior and does not justify a key-input fallback.
- Signed package/container provenance was not evaluated. Neither inclusion nor
  exclusion is settled, and no new permissions were granted.
- The temporary `.github/workflows/publish-spike.yml` and
  `Github/publish-spike/action.yml` do not exist; there is no deployed probe to
  clean up. The fixture, helper and behavioral-test tasks were not entered.

Architecture/spec decision clauses and their OCI/login/provenance Deferred
entries remain open. Story 3-2 still depends on executed evidence from this
spike. Existing release success and local mocks cannot substitute for it.

## Conditions for resuming

The maintainer must supply the missing environment and publishing
prerequisites described in the [plan](cicd-3-1-publish-composite-spike-plan.md):

1. Create `hexalith-release` for Builds, allow deployments from `main` only,
   and use no reviewer or timer.
2. Provide evidence of a nuget.org Trusted Publishing policy matching
   `Hexalith/Hexalith.Builds`, `publish-spike.yml` and `hexalith-release`, with
   the permitted package scope, and confirm `HEXALITH_NUGET_USER` is available
   to the caller.
3. Supply an isolated Zot destination, one version-shaped spike tag and an
   explicit push credential pair whose access is confined to that destination.
   Place the pair in the caller's environment for the publish job.

After those checks pass, resume the ordered implementation tasks. Preserve
existing policies, the legacy environment and every frozen runtime file.

## Verification of this delivery

This delivery adds only this prerequisite-results document. Document checks
cover frontmatter, local links, final newline, whitespace and preservation of
all pre-existing Builds files relative to the implementation baseline.
Runtime test suites and hosted experiments were not run because the execution
gate stopped implementation before any executable change.
