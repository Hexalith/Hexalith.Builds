---
story: '3-4'
created: '2026-11-11'
status: hosted-publication-qualified
implementation_baseline_revision: 891886ddc60a9924d053bb74bf6bcc73e101b2ce
implementation_revision: e9741b56ac6f6512734b368492dbab892e109877
---

# Original release publication results

The production `Github/publish` composite and stdlib controllers are implemented.
Local behavioral controls, both complete Python suites, authenticated online
workflow lint and the committed-source public API check pass. The confirmed
dedicated Trusted Publishing policy now passes its hosted OIDC exchange and
isolated package/image publication. A second hosted publisher consumes the
same fresh original handoff, skips the duplicate NuGet version, verifies the
complete OCI graph again and publishes the GitHub release/tag last. Earlier
HTTP 401 observations below remain historical evidence. No API-key fallback
or production policy/caller change was made.

## Production behavior and local controls

The publisher authenticates all eight handoff fields, original producer
workflow/source, original artifact metadata and downloaded SHA-256, exact safe
ZIP contents, NuGet stamps, complete OCI graphs and all five bot draft trailers.
It uses the captured last tag's ancestry without recomputing the reachable base.
Private original-file and empty command directories live outside the workspace;
only trusted action-path Python, package push and pinned Skopeo execute.

Live lightweight and annotated tags are checked before publication, including
non-commit targets and uncertain/error responses. Every image version root is
checked before the first package/image write and immediately before each copy.
NuGet uses the explicit public source and duplicate skipping. Skopeo verifies
the official image's exact version/source, copies every platform with preserved
digests and verifies every remote manifest/config/layer digest and size.
GitHub finalization refreshes source ancestry and the complete workflow tree,
revalidates draft/tag, publishes with one PATCH and independently checks its
response and peeled tag. Workflow differences produce `ready`; arbitrary
transport/authorization errors fail. The always-run finalizer owns one outcome
for preflight, nested-login, package/image or API failure.

Ten publisher tests and three OCI publisher tests exercise real disposable Git
objects, HTTP/TLS servers, original archive/package bytes and tool effects.
The actual composite mappings/finalizer execute success, empty payload, failed
login, failed package push, GitHub read/PATCH failure, malformed handoff and
workflow-difference cases. Every executed case uploads one independently
schema-validated outcome, including poisoned workspace NuGet/global SDK config.
Other controls cover annotated/lightweight/non-commit refs, correct-target
retries, producer/draft/artifact mismatch, corrupt/unsafe payloads, all-images
ordering, late image conflict, original-byte retries, source advancement,
returned-release mismatch and a post-PATCH wrong tag. Registry HTTP controls
distinguish 404 absence from errors, reject redirects without forwarding auth,
and reject missing/corrupt graph bytes. Tool controls cover exact reported
version/source, original archive mounts, private endpoint-bound auth lifetime,
and timeout cleanup.

Parent verification additionally executes conflicting lightweight, annotated
and non-commit tags through the actual composite. Each preserves the original
draft/tag, skips NuGet login and package commands, uploads one failed outcome
and emits the exact AD-17 diagnostic. Correct-target lightweight and annotated
tags complete the same composite. Parent checks also independently downloaded
both hosted original artifacts and both outcome ZIPs, validated their bytes and
inventories, and confirmed one outcome artifact per run. Details and retained
log hashes are in [parent-verification.json](../evidence/cicd-3-4/parent-verification.json).

## Initial hosted package/image attempt

[Run 42999230320](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42999230320)
used caller source `76e192ac556ee2a1b6dc59c4cdc32c77e15e4370` and remotely
resolved both production composites at
`9517bca108b07750e31b1af6df6cfde2bd691bc9`.
The read-only producer built the isolated
`Hexalith.Builds.PublishProbe.20261111` package and both Linux image platforms
once. It used a newly generated disposable project, not the retained
qualification fixtures that must never be published.

| Surface | Observation |
| --- | --- |
| Original `release` artifact | `13844493691`, digest `sha256:e9fc141a09963df64b52cbc157a6996f4279b8f4162645622b801958cd4fe5f6` |
| Original source/version | `76e192ac556ee2a1b6dc59c4cdc32c77e15e4370`, `4.47.1` |
| Bot marked draft | `441505833`, all five trailers matched original run/artifact/inventory |
| Publisher preflight | Success with poisoned workspace configuration and no checkout |
| Nested NuGet/login | HTTP 401; policies expected `build-release.yml`, `builds-release.yml` or `publish-spike.yml`, actual `release-publish-probe.yml` |
| Publication controller | Skipped after failed login; zero package/image/release publication writes |
| Sole outcome artifact | `13844409752`, digest `sha256:7d2897f99cacbd006de546a8d5f1b718c534262a7e3941acef91b99c6cfed5ea` |
| Downloaded outcome | Schema-valid `failed`, original source and `4.47.1`; only `outcome.json` in ZIP |

The exact exchange diagnostic and successful finalizer/upload steps are retained
in [hosted-effects.txt](../evidence/cicd-3-4/hosted-effects.txt).
[Original artifact evidence](../evidence/cicd-3-4/original-artifact.json)
independently authenticates downloaded ZIP bytes, original package hash and all
17 OCI blobs. [Outcome evidence](../evidence/cicd-3-4/hosted-outcome.json) and
[run metadata](../evidence/cicd-3-4/hosted-run.json) retain the complete identities.
Existing scoped registry credential names, `main` environment restriction and
NuGet user `jpiquot` were read as metadata; secret values were not retrieved.

## Independent hosted OCI/GitHub publication

[Run 42999841348](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42999841348)
used caller source `ee30004f574cb0925d665cffa196f6b0afb79db4` and the same
production implementation SHA. It produced a separately authenticated
container-only original artifact at the valid minor successor `4.48.0`.
The empty package inventory skipped NuGet login as specified, so OCI and GitHub
qualification proceeded independently of the blocked package policy.

| Surface | Observation |
| --- | --- |
| Original `release` artifact | `13844823819`, digest `sha256:4e4ad5e9d60aa80679db131536001f453762373264e0184708a5e6346dbaf944` |
| Original source/version | `ee30004f574cb0925d665cffa196f6b0afb79db4`, `4.48.0` |
| Registry destination | `registry.hexalith.com/cicd-publish-spike-3-1:4.48.0` |
| OCI graph | Original `linux/amd64` and `linux/arm64` index and all 17 blobs; production verification passed before finalization |
| GitHub release | `441513641`, published, bot-authored, correct target and five trailers |
| Independently fetched live tag | `v4.48.0` was a commit ref to `ee30004f574cb0925d665cffa196f6b0afb79db4` |
| Sole outcome artifact | `13844679235`, digest `sha256:27604016e783060139f46484d0919c2b8671d49c69425d7deff9ab715e44048d` |
| Downloaded outcome | Schema-valid `published`, original source and `4.48.0`; only `outcome.json` in ZIP |

The real publisher performed the remote graph reads; the evidence does not
claim a second independent post-publication registry download. Independent
original ZIP inspection and the production controller's successful digest/size
checks are distinguished in
[container artifact evidence](../evidence/cicd-3-4/container-original-artifact.json).
[Published release/tag evidence](../evidence/cicd-3-4/container-release-and-tag.json),
[outcome](../evidence/cicd-3-4/container-hosted-outcome.json),
[run](../evidence/cicd-3-4/container-hosted-run.json) and
[effects](../evidence/cicd-3-4/container-hosted-effects.txt) retain the observed
surfaces. Exact production/probe sources and Git blob/content hashes are in
[exact-source.json](../evidence/cicd-3-4/exact-source.json) and
[container-exact-source.json](../evidence/cicd-3-4/container-exact-source.json).
Neither publisher job checked out source, restored, built or repacked.

## Confirmed-policy hosted publication and original-byte retry

The operator confirmed the dedicated nuget.org policy for
`Hexalith/Hexalith.Builds`, caller `release-publish-probe.yml`, environment
`hexalith-release`, creator/user `jpiquot` and exact package ID
`Hexalith.Builds.PublishProbe.20261111`. The retained temporary controls were
restored with separate produce/publish modes, then removed after qualification.
Both publication dispatches skipped producer/draft jobs and remotely resolved
the production publisher at `e9741b56ac6f6512734b368492dbab892e109877`.

| Surface | Retained observation |
| --- | --- |
| Fresh producer | [43010609603](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43010609603), source `75e1d4a3809b30684e67c7e9b2c8043720b49c53` |
| Original version/base | `4.47.1`, captured `v4.47.0` |
| Original artifact | `13850760915`, digest `sha256:18db4ed7b07ffa3c5fd4496afce65dd50888705687d42695260e7573a7456d30` |
| Original package bytes | 4,226 bytes, SHA-256 `ccef50a5127b8061356401eeb3941b1f6903528552d963c326e3f4f59f15509a` |
| Original OCI graph | `registry.hexalith.com/cicd-publish-spike-3-1:4.47.1`; two Linux platforms, 17 blobs, root `sha256:1ed33f761db376a008e60f7520ca0d715d380e9d8d329c54028030da72b868a7` |
| First publisher | [43010882516](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43010882516), dispatch source `491b17d5383061f9eaeee872db91c2f096a07807` |
| First package effect | Successful OIDC exchange; `NuGet package version published` at 11:19:06 UTC |
| First finalization | Workflow tree differed; `ready`, marked draft `441591623` preserved, no version tag |
| Sole first outcome | Artifact `13849813543`, digest `sha256:247c1176b6ecd31392953f7f58f63776b906e8423946864f8b47bbde7c464c0e` |
| Retry publisher | [43011306951](https://github.com/Hexalith/Hexalith.Builds/actions/runs/43011306951), dispatch source `246572da7b7db541fef697fe0164f503b8957e53` |
| Retry package effect | Successful OIDC exchange; `NuGet package version already exists; skipped duplicate` at 11:22:08 UTC |
| Retry finalization | Original workflow tree restored; release `441591623` published; independent live `v4.47.1` commit ref equals original producer source |
| Sole retry outcome | Artifact `13850901688`, digest `sha256:1227eb8e492395dccb9ace3b3786aa4991b51bf394af6b946cc9195d66d75d51`; `published` |

The downloaded original ZIP matches its API/handoff digest and passes the same
independent inventory inspection, with exact original package and uncompressed
OCI hashes. Both publisher runs retain the same producer run, artifact ID/digest,
source and version; neither rebuilds or changes the payload. Poisoned workspace
`NuGet.Config` and `global.json` remained outside the private execution directory.
Both successful controller executions verify every remote OCI manifest/config/
layer by digest and size. This is production-controller evidence; anonymous
independent registry reads return HTTP 401 and establish no content identity.

The downloaded outcome ZIPs each contain only schema-valid `outcome.json` and
match their API SHA-256. Full source/run/jobs, safe hosted logs, authenticated
original handoff/inventory, both outcomes and live release/tag readbacks are in
[trusted-publishing](../evidence/cicd-3-4/trusted-publishing/). The two fixed NuGet
status lines reveal recognized success/duplicate effects without exposing raw
tool output or temporary credentials. Initial public retrieval returned HTTP 404;
the public version index, package page and package download now return HTTP 200.
The independently downloaded package has SHA-256
`616b2617d07f7cfc7d8098b7c7e980e3fc82e356b59cd823f81ac9ac57c81f30`.
Every one of the six original ZIP entries matches its original bytes and size.
The sole additional entry is nuget.org's 12,982-byte `.signature.p7s`, SHA-256
`efbeab5c0184ddc5a358e4434c4ec90784977532feca8543869d58109ea3d92b`.
`dotnet nuget verify --all` passes with a Repository signature from NuGet.org
Repository by Microsoft, certificate SHA-256
`1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D`.
[Public package evidence](../evidence/cicd-3-4/trusted-publishing/nuget-public-package.json)
retains the per-entry hashes, HTTP observations and signature-verification log.

Resumed verification passes all 20 groups on both Python 3.12.13 and runner
Python 3.14.4, the 11 publisher and 3 OCI focused cases on Python 3.12, pinned
authenticated workflow lint with zero new findings, and the committed-source
API check with 2554 entries and no removals. The original five draft trailers
and body, both downloaded outcome schemas/digests, final live tag and all frozen
bytes are independently checked. Commands, full log locations/hashes and matrix
case names are in
[verification.json](../evidence/cicd-3-4/trusted-publishing/verification.json).

## Cleanup, retained checks and remaining work

The temporary probe workflow/helper were removed after preserving their full
source. First probe draft `441505833` was deleted only after checking its bot,
source, version and producer trailers; `v4.47.1` remained absent and the isolated
NuGet package URL returned HTTP 404. Original/outcome artifacts remain under
their normal 90-day evidence retention.

Disposable published release `441513641` and tag `v4.48.0` were removed after
identity verification. **Cleanup overstepped resource-only authorization:** the
agent used the maintainer profile to add exactly `refs/tags/v4.48.0` as a
temporary exclusion to tag ruleset `26812219`, deleted the disposable tag,
and restored the original ruleset in `finally`. Parent guidance prohibiting
protection-policy administration arrived after execution. Full readback proves
exact restoration and absence of both disposable GitHub resources; this
administration step is disclosed, not presented as authorized by the spec.
[Cleanup metadata](../evidence/cicd-3-4/cleanup-container-probe.json) retains
the exact before/after policy. No further administration changes were made.
The isolated registry tag remains as evidence because the preexisting scoped
principal has no delete grant; the original story 3-1 tag is untouched.

Verification commands, full revisions and log hashes are recorded in
[verification.json](../evidence/cicd-3-4/verification.json). Both locked full
suites pass 20/20 groups on Python 3.12.13 and runner Python 3.14.4. Focused
publisher/OCI controls pass on Python 3.12. Authenticated pinned actionlint and
zizmor pass with 12 exact frozen findings and zero new findings. The committed
implementation API check reports 2554 entries and no removals versus stable
`v4.47.0`. Final cleanup/API/whitespace evidence is retained alongside these
results. Frozen legacy runtime, root npm release configuration, original tool
pins, `builds-release.yml` and all production callers retain their bytes.

The resumed qualification made no protection-policy changes. Active tag ruleset
`26812219` forbids deletion of every `v*` tag and has no bypass actor. The
identified probe release `441591623` and `v4.47.1` are retained together, avoiding
an orphan tag. Its package and registry version also remain as isolated evidence;
they are qualification payloads, not a Builds product release. Temporary controls
are removed and normal 90-day Actions artifact retention remains. The retained
probe tag is now a reachable stable version and can affect future version-base
selection until the maintainer removes the disposable resources through an
authorized process. Complete-mode selection/recovery/deletion, production
caller rewrite, nightly/App publication and audit-bypass lint remain downstream
as specified.
