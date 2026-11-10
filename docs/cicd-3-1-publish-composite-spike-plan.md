---
story: '3-1'
created: '2026-11-10'
status: complete
planning_only: false
implementation_baseline_revision: b99c0d9a5b9b2eeebacc19ea1aebf1b0f656afba
---

# Publish composite spike plan

Story 3-1 will establish whether containers built once as OCI archives can be
pushed by a remotely resolved composite, prove the placement of NuGet/login,
and decide whether the new publisher adds signed build provenance. These
decisions must reach the CiCd architecture spine and spec before story 3-2.
Planning created this execution companion. After the maintainer repaired the
matching policy, hosted run `42791957667` published and replayed the original
artifact from run `42785806980`, with build skipped. Nested NuGet/login and the
same-context caller control succeeded. The results document settles OCI
tooling/naming, login placement and reasoned provenance exclusion; it retains
the historical blocked attempt and exact resumed evidence.

The canonical workflow record is in the enclosing Hexalith.CiCd repository at
`_bmad-output/specs/spec-cicd/stories/3-1-spike-oci-containers-and-nuget-login-in-the-publish-composite.md`.
Its intent contract and the architecture's AD-7, AD-9, AD-10, AD-11, AD-17 and
AD-20 govern execution. This document is the Builds-local execution companion.

## Starting evidence

- [The existing release](../.github/workflows/builds-release.yml) authenticates
  in a caller job under `production`, using SHA-pinned NuGet/login v1.2.0 and
  passing its temporary key to another composite. This proves neither the
  remote nested-login case nor a policy for a different workflow/environment.
- [The legacy container publisher](../Github/publish-containers/publish-containers.sh)
  logs in and runs SDK container publication directly against the registry. It
  does not transfer an archive between jobs. Preserve it and all other
  AD-20-frozen runtime files byte for byte.
- [The existing registry validator](../Github/publish-containers/oci_registry_validator.py)
  has graph, platform, digest and label checks useful as design references.
  Its legacy workspace assumptions are not the new publisher's contract.
- [ContainerProvenance.targets](../Props/ContainerProvenance.targets) supplies
  image labels. Signed subject attestations are a separate mechanism; the
  frozen domain release has an example of the latter.
- [Shared build helpers](../Github/lib/ci_pipeline.py),
  [restore trust](../NuGet.ci.Config), [global.json](../global.json) and the
  [quality-check commands](../Tools/ci/README.md) provide existing conventions.
  Preserve the completed shared CI and sample-module behavior.

## Execution prerequisites

Before deploying or dispatching probe files, record the following in the future
`docs/cicd-3-1-publish-composite-spike-results.md`:

1. A caller-owned `hexalith-release` environment restricted to `main`, with no
   reviewer or timer, and explicit registry push credentials scoped to an
   isolated spike repository in Zot. The pull-only CI identity is insufficient.
2. A nuget.org Trusted Publishing policy matching
   `Hexalith/Hexalith.Builds`, `publish-spike.yml` and `hexalith-release`, with
   the permitted package scope, plus access to `HEXALITH_NUGET_USER`.
3. The destination repository and one version-shaped spike tag chosen and
   recorded before the run. Use that same destination for replay. Do not write
   a production image tag or publish a NuGet package.

Existing story-1 evidence covers `builds-release.yml` with `production` and
left the production `hexalith-release` transition to later work. It does not
establish these new prerequisites. The maintainer must provide missing policy
or credentials. If unavailable, record the exact missing prerequisite and stop
implementation as blocked. Retain existing policies, legacy credentials and
the current release workflow; do not change them to make a probe pass.

The hosted jobs use `permissions: {}` at workflow level and explicit per-job
permissions. Build has read access only. Publish gets `id-token: write` and
only the read permissions needed for its original artifact; grant
`attestations: write` only for the provenance experiment. Pass secrets by name.
Runtime code is standard-library Python >=3.12. No expression enters `run:`;
scripts receive values through environment variables.

## Ordered implementation work

1. Add `Samples/PublishSpike/PublishSpike.csproj`, `Program.cs`, and
   `Tools/spikes/publish_spike.py`. Build a tiny container through SDK support,
   without a Dockerfile, in Release/package mode using the repository SDK and
   restore-trust conventions. Build both linux amd64 and arm64 once and emit
   an OCI archive. Pack a local-only sample package if evaluating package
   provenance. Do not add the fixture to existing production solutions.
2. Add `Tools/spikes/publish-tools.json` with a selected Skopeo distribution,
   exact version and integrity identity before executing it. Prefer
   `skopeo copy --all --preserve-digests` from the OCI archive to the isolated
   registry destination. Prove this candidate; do not infer support from help
   text. SDK documentation describes compressed archives, while AD-17 names
   `containers/*.tar`: inspect actual bytes/layout and explicitly choose
   compatible naming or lossless packaging normalization. Never rebuild images
   to fix packaging.
3. Add `Github/lib/publish_spike.py`, temporary
   `Github/publish-spike/action.yml` and
   `.github/workflows/publish-spike.yml`. Keep the workflow dispatch-only on
   main. Build/upload in one job; download and publish in a fresh job with no
   repository checkout, source, build, pack or restored build cache. Resolve
   helper code from `GITHUB_ACTION_PATH`. Commit the action first, obtain its
   full revision directly from Git, then commit the harness with that literal
   remote action reference: `Hexalith/Hexalith.Builds/Github/publish-spike@`
   followed by that full revision.
   This experiment does not change the exact-release-tag rule for modules.
4. Verify artifact ID, producing run and digest before consuming it. Push the
   archive; query the registry to verify the root digest, exact platform set,
   child manifests/configs/layers and source/version labels. Repeat publish
   from the original build job's artifact without running that build again.
   Existing different content at the selected version is a conflict, not
   permission to overwrite it.
5. Run SHA-pinned NuGet/login inside the remote composite. Record only that its
   masked temporary API key is nonempty, never the key or raw OIDC token.
   Exercise caller-job login under the same policy/environment as a control.
   If nesting fails while that control succeeds, make caller login the first
   publish step and exercise explicit `nuget-api-key` handoff into the remote
   composite. Record the new production input required by AD-10. If both fail,
   diagnose policy, permissions or service availability; leave execution
   blocked instead of declaring fallback proven.
6. Evaluate signed provenance over the local-only package bytes and published
   container digests. Include it only if both verify, source/builder identity
   truthfully relates to the original build, and it needs no checkout, source
   execution, rebuild or credential exposure. Keep any added permission in
   publish. Otherwise record exclusion and its substantive rationale. A service
   outage is an incomplete experiment, not an exclusion decision. Do not equate
   an attestation made after download with proof that its job built the bytes.
7. Add `Github/lib/tests/test_publish_spike.py` for meaningful helper/command
   behavior: wrong artifact identity/digest, corrupt archive, absent platform
   or blob, conflicting version, failed login and successful original-artifact
   replay. These controls run in the existing dual-Python discovery group.
   Assert observable effects and failures, never workflow YAML text.
8. Write `docs/cicd-3-1-publish-composite-spike-results.md`, then update the
   affected AD-17 and Story 3 Deferred clauses, spec constraints and companion
   release steps/permissions in CiCd. Append a spec memlog event. Remove the
   temporary workflow and action after retaining hosted evidence; keep the
   fixture, helpers and behavioral tests as reproducible evidence. Do not
   publish a Builds release while experimental action metadata exists.

Reserve `Github/publish` and the production release state machine for later
stories. Existing `builds-release.yml`, domain workflows and module-used
composites stay unchanged throughout this spike.

## Acceptance and evidence

| Check | Required observation |
| --- | --- |
| Build once | One producing job/artifact; no compile or pack in publish or replay |
| Real OCI destination | Registry tag and complete two-platform graph match archive digests |
| Remote login | Hosted action resolved at its recorded full source revision; nested login succeeds, or caller control plus key handoff proves fallback |
| Failure controls | Invalid artifact and authentication errors prevent writes; graph loss and tag conflicts fail |
| Provenance decision | Explicit inclusion/exclusion, rationale, subject identities and exact permission delta |
| Consistent handoff | Spine and spec contain final tool, archive, login and provenance decisions before 3-2 |

The results document must retain the full caller/action source revisions, SDK
and tool versions/integrity identities, run URL/ID/attempt, original artifact
run/ID/digest, archive hash, root/child digests and platforms, sanitized commands
and outcomes, control failures, replay evidence and cleanup revisions. If
provenance is included, retain bundle/subject identities and independent
verification. Separate executed proof, documentation, inference and blockers.

Run focused tests with Python 3.12 and runner Python, then the locked complete
Python suite, actionlint/online zizmor, and committed-revision public API checks
using the existing quality commands. Require hosted evidence for remote action,
OIDC and registry behavior. Compare frozen blobs against the starting revision,
validate each exact commit message with pinned commitlint, and preserve green
Builds CI. Review both the Builds implementation delta and CiCd decision delta.

This planning delivery checks frontmatter, local links, whitespace and its
documentation-only scope, validates its commit message, and commits/pushes the
plan in Builds. The canonical story and updated Builds gitlink are staged
together in CiCd. All implementation work remains unchecked there.

## Primary-source feasibility references

- [.NET SDK container configuration](https://learn.microsoft.com/en-us/dotnet/core/containers/publish-configuration)
  describes archive output and OCI configuration. Actual SDK archive layout
  remains an experimental observation.
- [Skopeo copy options](https://github.com/podman-container-tools/skopeo/blob/main/docs/skopeo-copy.1.md)
  describe complete-index copying and digest preservation. This supports the
  candidate choice, not a claim that it passed against Zot.
- [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
  describes policy matching and short-lived credentials;
  [NuGet/login](https://github.com/NuGet/login/blob/main/README.md) documents
  the temporary-key output. Neither proves this remote-composite boundary.
- [The attestation action](https://github.com/actions/attest-build-provenance/blob/main/README.md)
  documents signed subjects. The spike must establish whether and how that
  mechanism belongs in this publisher.
