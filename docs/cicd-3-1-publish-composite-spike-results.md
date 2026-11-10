---
story: '3-1'
created: '2026-11-10'
status: blocked
implementation_baseline_revision: f81486cb532e2e166f5c363fa8e9f75c1313c2a1
---

# Publish composite spike results

The hosted build passed, but **execution is blocked by a NuGet Trusted
Publishing policy mismatch**. Both the nested composite login and the caller
control returned HTTP 401 naming workflow-policy mismatches. Neither returned
a temporary key. Registry publication/replay and signed provenance were not
attempted. No NuGet package or container was published. No OCI transport,
login placement, fallback or provenance inclusion/exclusion decision is made.

The [execution plan](cicd-3-1-publish-composite-spike-plan.md) remains the
resumption companion. The canonical CiCd story is
`_bmad-output/specs/spec-cicd/stories/3-1-spike-oci-containers-and-nuget-login-in-the-publish-composite.md`.
The earlier absence of `hexalith-release` was resolved by maintainer
provisioning; it is not the blocker in this attempt.

## Prerequisite gate repeated before executable creation

On 2026-11-10 the configured CiCd-agent profile was selected at
`/home/administrator/.config/gh-cicd-agent`; `GH_TOKEN` and `GITHUB_TOKEN`
overrides were absent. Authenticated account `jpiquot`, target repository
`Hexalith/Hexalith.Builds`, branch `main`, and baseline revision
`f81486cb532e2e166f5c363fa8e9f75c1313c2a1` were checked. No credentials or
credential-file contents were retained.

Read-only GitHub environment metadata confirmed `hexalith-release`, exactly
one permitted deployment policy (`main`, type `branch`), and no reviewer or
timer. Protected environment-variable metadata returned HTTP 403, an access
limitation that does not invalidate the canonical story's dated provisioning.
The maintainer had confirmed the matching NuGet policy and organization
variable selection; the story explicitly authorized using that confirmation.
This preflight passed before any executable spike file was created.

| Supplied input | Recorded configuration |
| --- | --- |
| Isolated destination | `registry.hexalith.com/cicd-publish-spike-3-1` |
| Version tag | `0.0.0-spike.20261110` |
| Principal | `cicd-publish-spike-3-1`; read/create/update only on the exact repository, no delete grant |
| Explicit publish-job secret inputs | Environment secrets `HEXALITH_REGISTRY_PUSH_USERNAME`, `HEXALITH_REGISTRY_PUSH_PASSWORD` |
| Destination/tag references | Environment variables `HEXALITH_REGISTRY_SPIKE_REPOSITORY`, `HEXALITH_REGISTRY_SPIKE_TAG` |
| NuGet username reference | Organization variable `HEXALITH_NUGET_USER`, selected for Builds |
| Required matching NuGet policy | Owner `Hexalith`, repository `Hexalith.Builds`, workflow `publish-spike.yml`, environment `hexalith-release`, permitted package scope |

The exact workflow source proves these configured input references. Protected
values were not enumerated. The failed exchange below supersedes preflight's
policy assumption: runtime policy matching remains unsatisfied.

## Exact hosted execution and original artifact

[Hosted run 42785806980, attempt 1](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42785806980)
ran from caller revision `5780b026ebfb194392c93b2546126a960aa0b30a` on `main`.
The remotely resolved composite and helper source was
`b260185ebe0d0dd1ae9507b16f2b49b366ccd47c`; the runner downloaded that exact
revision and resolved scripts through its own `GITHUB_ACTION_PATH`.
The publish job contained no checkout, source build, pack or cache restore.
Runner version was 2.338.2, image `ubuntu-26.04` / `20261101.186.1`.
The source blobs and hashes are retained in
[exact-source.json](../evidence/cicd-3-1/exact-source.json); job/step outcomes
are in [hosted-run.json](../evidence/cicd-3-1/hosted-run.json).

The separate read-only build job succeeded using SDK `10.0.403` selected from
`global.json`, Python 3.12, Release/package mode, the shared `build_flags`, and
`NuGet.ci.Config`. It restored once, built each platform once through the SDK,
assembled an OCI index from the unchanged platform manifests, and packed a
local-only fixture with `--no-build --no-restore`. It executed no package push.

| Original build artifact identity | Value |
| --- | --- |
| Producing run | `42785806980`, attempt `1` |
| Artifact ID | `13749161812` |
| Name | `publish-spike-original` |
| Uploaded ZIP digest | `sha256:a49aa577b8807fbdb1e42bf6bcbf12e071c6f66c9054e0aed793fd8ddc2083d1` |
| Source revision | `5780b026ebfb194392c93b2546126a960aa0b30a` |
| Retention requested | 90 days; exact expiry is in the artifact evidence |

[original-artifact.json](../evidence/cicd-3-1/original-artifact.json) retains
the authenticated artifact metadata and complete producer inventory: archive
hash, root/child/config/layer digests and sizes, platforms, local package hash,
SDK version and actual archive compression. After the hosted failure, the
retained helper independently downloaded the original ZIP locally, validated
its ID/run/source/digest against GitHub metadata and explicit upload identity,
then validated its inventory and complete OCI graph. That local download
accessed no registry; it does not substitute for hosted archive transport.

## Observed archive behavior and candidate tool

SDK single-platform `ContainerArchiveOutputPath` emitted **uncompressed OCI
tar streams**, despite the requested `.tar.gz` filenames. The handoff uses
`containers/publish-spike.tar`. The driver packages both original SDK manifest
graphs into a new two-platform OCI index without rewriting any child manifest,
config or layer bytes. Both expected platforms (`linux/amd64`, `linux/arm64`)
and source/revision/version labels passed archive inspection.

A prior local feasibility build exposed SDK 10.0.403's multi-RID label
transport truncating values at `:`: the source URL became `https`. The
installed SDK's `Microsoft.NET.Build.Containers.targets` serializes labels as
`identity:value` then reads `Split(':')[1]`. The fixture therefore builds each
platform once via single-RID SDK publication and assembles only packaging.
This workaround passed both locally and in the hosted build. No image rebuild
occurs in the publisher or any original-artifact retry.

[Tool pin](../Tools/spikes/publish-tools.json): official
`quay.io/skopeo/stable:v1.22.3` at immutable image digest
`sha256:2681eccb8b02c495b013c2669cbf7f41d5c0b0b0e644e7c69bd3ea9bcb20641f`.
This distribution was selected by querying its registry identity before use.
The helper will verify `skopeo version 1.22.3`, then invoke
`copy --all --preserve-digests` with an isolated private authfile and the
original `oci-archive:` input. **This remains a candidate:** no hosted Skopeo
execution or Zot archive-copy success was observed.

## Runtime authentication blocker

The nested SHA-pinned `NuGet/login` v1.2.0 exchange and the immediately
following caller-job control used the same username variable, workflow,
environment and `id-token: write` permission. Both failed at
`https://www.nuget.org/api/v2/token` with HTTP 401:

```text
Workflow mismatch for policy 'Hexalith.Builds': expected 'build-release.yml', actual 'publish-spike.yml'.
Workflow mismatch for policy 'Hexalith.Builds builds-release': expected 'builds-release.yml', actual 'publish-spike.yml'.
```

The nested helper rejected the empty key. The caller failure stopped every
successor, so no artifact-consuming publish step, registry write or replay
ran. GitHub's parent step conclusion for the nested call was softened by
`continue-on-error`; the actual nested login and helper failures are retained
in [login-failures.txt](../evidence/cicd-3-1/login-failures.txt). This is an
**environment/policy blocker**, not evidence against remote composition and
not evidence for the caller-login fallback. The existing release policies
and the production workflow were preserved.

The maintainer must make a policy accessible to the selected policy-creator
username match **`Hexalith/Hexalith.Builds` / `publish-spike.yml` /
`hexalith-release`**, including its permitted package scope. The service's
error names workflow mismatches; it does not prove that another username has
no such policy. No policy was weakened or replaced during this attempt.

## Provenance and production-interface handoff remain open

Signed package/container provenance was not executed, and no attestation
permission was granted. The standard action's documented subject/signature
behavior was inspected for feasibility, but no subject/source/builder
verification was completed. Neither inclusion nor reasoned exclusion is
settled by this authentication failure. OCI labels are not attestations.

The artifact helper, graph checks and archive naming above are retained
implementation evidence, not final AD-17 production decisions. The original
OCI/login and provenance Deferred entries remain open. Story 3-2 cannot treat
this attempt as a completed spike or adopt the candidate tool/login interface
as proven. The release state machine and `Github/publish` were not implemented.

## Re-drive without rebuilding

The temporary workflow/action were removed in cleanup revision
`30c07862eb655703467433aeaff5a01de9782d48`, after recording exact source
revisions and blob hashes. No Builds release was dispatched while they existed.
The fixture, Python helpers, tool pin, behavioral tests and sanitized evidence
remain. Restore the exact harness/action from the retained Git objects only
after repeating preflight and correcting the matching policy. Preserve all
frozen runtime files and the existing `builds-release.yml`.

After restoration, dispatch `publish-spike.yml` on `main` with these inputs:

```text
artifact-id=13749161812
artifact-digest=sha256:a49aa577b8807fbdb1e42bf6bcbf12e071c6f66c9054e0aed793fd8ddc2083d1
artifact-run-id=42785806980
source-revision=5780b026ebfb194392c93b2546126a960aa0b30a
```

A nonempty `artifact-id` skips the build job; publish/replay consume only this
original artifact. Its authenticated metadata and every content digest must
still match. An expired original artifact is a blocker, not permission to
replace it silently. Retry the same destination and version tag; conflicting
content must fail before copy. The unexercised fallback path must be tested
only if nested login fails while caller login succeeds. Both failing remains
blocked. Provenance needs a truthful independent subject/source verification
before any adoption decision.

## Verification

- Focused behavioral suite: 8 tests passed on Python 3.12 and runner Python
  3.14.4. Controls exercise real ZIP/tar bytes, wrong artifact identity/digest,
  missing graph content/platforms, conflicting destination, full graph replay,
  nonzero empty-login command behavior and the pinned Skopeo command/authfile
  lifetime through an executable command double. No workflow-text assertions.
- Local fixture build/pack and local verification of the hosted original
  artifact passed; these are explicitly separate from unperformed OCI proof.
- Actionlint passed for the exact temporary caller. Initial full workflow
  lint passed with the temporary action and no caller yet. The caller audit
  started before its action commit was pushed reported `impostor-commit`
  for that not-yet-visible revision. Final cleanup lint passed with all
  online audits: 12 exact frozen findings and 0 new findings. Both locked
  full suites both passed: **20/20 groups on Python 3.12.13** and
  **20/20 groups on runner Python 3.14.4**. The hosted caller CI also passed
  its workflow-lint/public-API job at the exact caller revision.
- Committed-source public API check at caller revision passed: 2507 entries,
  no removals, against stable `v4.47.0`. The post-cleanup committed-source
  API check also passed at `30c07862eb655703467433aeaff5a01de9782d48`: 2494
  entries and no removals.
- Every created commit message was validated with the installed pinned
  commitlint 21.2.4. Frozen-file comparison and cleanup checks accompany the
  final cleanup revision. All 104 pre-existing `Github/`, workflow and
  `Props/` Git blobs match the implementation baseline under the tracked EOL
  rules. Temporary action/workflow files are absent; evidence JSON and local
  document links validate, and `git diff --check` passes.
