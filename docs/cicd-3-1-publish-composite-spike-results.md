---
story: '3-1'
created: '2026-11-10'
status: complete
implementation_baseline_revision: f81486cb532e2e166f5c363fa8e9f75c1313c2a1
---

# Publish composite spike results

## Prerequisite evidence

The renewed prerequisite gate passed on 2026-11-10 **before restoring any
executable spike files**. The configured profile was
`GH_CONFIG_DIR=/home/administrator/.config/gh-cicd-agent`, authenticated account
`jpiquot`, repository `Hexalith/Hexalith.Builds`, clean `main` at
`3f47d25b98260ddcbdcfae68402ef86fa0ca58bf`; `GH_TOKEN` and `GITHUB_TOKEN`
overrides were absent. Read-only environment metadata confirmed exactly one
allowed deployment policy (`main`, type `branch`) and no reviewer or timer.
Protected variable endpoints returned HTTP 403, an access limitation rather
than absence. No maintainer credentials or secret values were read.

The canonical story supplies renewed protected setup confirmation for active
NuGet policy `Hexalith.Builds CiCd`, creator `jpiquot`, package owner `Hexalith`,
GitHub owner/repository IDs `80614290` / `952945989`, workflow
`publish-spike.yml`, environment `hexalith-release`, glob `*`, and operations
push-new-package/version and unlist/relist. Runtime exchanges below now confirm
matching authentication. No NuGet package was published or modified. Existing
release policies, `HEXALITH_NUGET_USER` and `production` were preserved.

| Supplied input | Confirmed configuration/reference |
| --- | --- |
| Isolated destination | `registry.hexalith.com/cicd-publish-spike-3-1` |
| Version tag | `0.0.0-spike.20261110` |
| Principal | `cicd-publish-spike-3-1`; read/create/update only on this repository, no delete grant |
| Explicit publish-job secret inputs | `HEXALITH_REGISTRY_PUSH_USERNAME`, `HEXALITH_REGISTRY_PUSH_PASSWORD` in `hexalith-release` |
| Destination/tag variable references | `HEXALITH_REGISTRY_SPIKE_REPOSITORY`, `HEXALITH_REGISTRY_SPIKE_TAG` |
| NuGet user variable | Organization variable `HEXALITH_NUGET_USER`, selected for Builds, confirmed as `jpiquot` |

Dated maintainer provisioning in the canonical story supplies protected
credential evidence. The retained exact workflow/composite source rechecked
all configured input references. Before restoration, authenticated metadata
and an independent local download revalidated the original artifact's
ID/run/source/ZIP digest, inventory and complete OCI graph. Its expiry remains
`2027-02-08T08:50:41Z`.

## Hosted outcome

**OCI publication and fresh-job replay passed, and nested remote NuGet/login
passed. Signed build provenance is excluded for the source/builder identity
reason below.** Story 3-2 can use these settled interfaces.

| Run | Exact caller revision | Executed outcome |
| --- | --- | --- |
| [42785806980](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42785806980), attempt 1 | `5780b026ebfb194392c93b2546126a960aa0b30a` | Original build passed; both login placements returned HTTP 401 for workflow-policy mismatches. No registry copy ran. |
| [42791562146](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42791562146), attempt 1 | `a35cb83be57cff4e26fe593910a1f16ef6a6a873` | Build skipped; nested and caller login passed; exact artifact validated. Skopeo version guard rejected the upstream commit suffix before copy. |
| [42791957667](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42791957667), attempt 1 | `515aaa3b0c5b668d86bdfed7065286a378052d62` | Build skipped; both login placements, original-artifact publication, complete remote graph verification and fresh-job replay passed. |

The successful caller resolved the remote composite and trusted helpers at
`0705088e92d6703232e62dcbe44b3bf0a0980ee3`, via its own `GITHUB_ACTION_PATH`.
Neither publish nor replay checked out the repository, restored a build cache,
compiled, packed or rebuilt an image. They used runner 2.338.2 and
`ubuntu-26.04` image `20261101.186.1`. Exact Git blobs and SHA-256 hashes are
in [exact-source.json](../evidence/cicd-3-1/exact-source.json); final job/step
outcomes are in [resumed-run-42791957667.json](../evidence/cicd-3-1/resumed-run-42791957667.json).

## Original build and archive identity

The separate read-only build used Python 3.12 and SDK `10.0.403` from
`global.json`, shared `build_flags`, Release/package mode and `NuGet.ci.Config`.
It restored once, built each platform once through the SDK, assembled an OCI
index from unchanged platform manifests, then packed one local-only fixture
with `--no-build --no-restore`.

| Identity | Value |
| --- | --- |
| Producing run / source | `42785806980`, attempt `1` / `5780b026ebfb194392c93b2546126a960aa0b30a` |
| Artifact ID / name | `13749161812` / `publish-spike-original` |
| Authenticated ZIP digest | `sha256:a49aa577b8807fbdb1e42bf6bcbf12e071c6f66c9054e0aed793fd8ddc2083d1` |
| Archive / digest | `containers/publish-spike.tar` / `sha256:aba1ada7bc66ffa300f4a4f8b5c6f8923fd1b618c550c8610e14c5f31ed93f8e` |
| Root index | `sha256:c86808e5f83c4ce68fb1b5fc5dee7217fe8aa951cf290b07899a3a349dddf897` |
| linux/amd64 manifest | `sha256:7fb2788092e7b7bdee2bcdb59613a425b848d097b5acb7e2d66ece2caed7b304` |
| linux/arm64 manifest | `sha256:7728b01f94766956b2410f3bcbef4d1fd40e45867df1ce778f7600f3e4e32e67` |
| Local-only package SHA-256 | `3b64bdb0f6549852d2db4271eb06c6d6a86222b0609edf4075d27927bf7df7b0` |

[original-artifact.json](../evidence/cicd-3-1/original-artifact.json) retains
all manifest/config/layer digests and sizes, producer inventory, platform set,
package name/hash and authenticated metadata. Both resumed runs consumed only
that original ID, producing run, source and ZIP digest; no substitute artifact
or new build was accepted.

SDK `ContainerArchiveOutputPath` emitted **uncompressed OCI tar streams** even
with requested `.tar.gz` names. The selected handoff is uncompressed
`containers/*.tar`. With SDK 10.0.403, multi-RID label transport truncates
colon-containing values: source URL `https://...` became `https` because the
SDK serializes `identity:value` then reads `Split(':')[1]`. The fixture publishes
each platform once via single-RID SDK publication, then assembles packaging
only. Child manifests, configs and layers retain their original bytes. Both
platforms and source/revision/version labels passed hosted archive inspection.

## Proven tool, registry graph and retry

The adopted distribution is official `quay.io/skopeo/stable:v1.22.3` pinned to
image digest
`sha256:2681eccb8b02c495b013c2669cbf7f41d5c0b0b0e644e7c69bd3ea9bcb20641f`,
with source commit `92397d5ba399784c6e2f9f229a4244b724743dba`.
[The tool pin](../Tools/spikes/publish-tools.json) and helper require the exact
reported version `skopeo version 1.22.3 commit: 92397d5ba399784c6e2f9f229a4244b724743dba`.
The first resumed run exposed the overly strict bare-version expectation;
that pre-copy failure is retained in
[resumed-login-and-version-guard.txt](../evidence/cicd-3-1/resumed-login-and-version-guard.txt).
The correction pins the observed commit suffix rather than accepting arbitrary
version output.

The helper mounts the artifact and a mode-0600 authfile read-only in the pinned
distribution, checks its version, and executes the equivalent of:

```text
skopeo copy --all --preserve-digests --authfile /spike/auth.json oci-archive:/spike/containers/publish-spike.tar docker://registry.hexalith.com/cicd-publish-spike-3-1:0.0.0-spike.20261110
```

Credentials never enter command arguments or evidence; the authfile is removed
in `finally`. A pre-copy registry read rejects an existing conflicting root.
After each copy, authenticated registry reads verified the tag's root and all
17 manifest/config/layer digest-size pairs against the archive. The archive's
exact two-platform index and source/revision/version labels were validated
before registry writes. First publication recorded `previously_present: false`;
fresh-job replay recorded `true`. Direct comparison confirms the original,
[publish](../evidence/cicd-3-1/publish.json) and
[replay](../evidence/cicd-3-1/replay.json) graphs and archive hashes are identical.
[Evidence artifact metadata](../evidence/cicd-3-1/resumed-artifacts.json) and
[sanitized command effects](../evidence/cicd-3-1/successful-hosted-effects.txt)
retain the hosted observation. Only the isolated spike tag was published.

## NuGet login placement and production input

SHA-pinned `NuGet/login@8d196754b4036150537f80ac539e15c2f1028841`
(v1.2.0) succeeded inside the remotely resolved composite. The same-job caller
control succeeded under the identical workflow, environment, username and
`id-token: write` permission. Publish and fresh replay also independently
exchanged a token inside that remote composite. Helpers observed only masked,
nonempty temporary keys; no package push or key/token retention occurred.

Production keeps login **inside `Github/publish`**. The caller passes the
NuGet user and explicit registry push pair in its own `hexalith-release` job;
no caller-login fallback or public `nuget-api-key` input is required. The
conditional experimental fallback was not exercised or adopted because no
discriminating nested failure occurred. The earlier shared HTTP 401 was a
policy prerequisite failure, not evidence against composition.

## Signed build provenance: excluded

The decision is based on identity semantics, not service availability.
[provenance-evaluation.json](../evidence/cicd-3-1/provenance-evaluation.json)
records the package bytes, immutable root/child image subjects, exact upstream
revisions and source hashes, and permission delta `[]`.

The [v4.2.2 wrapper](https://github.com/actions/attest-build-provenance/blob/4d101475d8b20a2381f78447822ac1eab6504dd8/action.yml)
delegates to `actions/attest@508db95dd578ae2727ebd6217d5ba78e4fbda05d`.
Its [default provenance generator](https://github.com/actions/attest/blob/508db95dd578ae2727ebd6217d5ba78e4fbda05d/src/provenance.ts)
uses the bundled library's current OIDC `claims.sha`, `claims.job_workflow_ref`
and `claims.run_id`/`run_attempt` for source, builder and invocation. On this
replay, source `515aaa3b...` and run `42791957667` would describe the publisher,
while both subject classes were built at `5780b026...` in run `42785806980`.
Signing the downloaded bytes would authenticate that current claim, not prove
the original build. This mismatch also applies when production complete mode
runs after a Builds bump.

The original read-only build emitted no producer-signed statement to verify.
Custom predicates can describe a publish receipt; they do not retroactively
create producer-signed build evidence. Therefore the publisher adds neither
`actions/attest-build-provenance` nor `attestations: write`; no attestation was
created and no inclusion claim is made. Its permission set remains
`contents: write`, `actions: read`, `id-token: write` in production (the spike
needed only `contents: read`). OCI labels remain metadata, not attestations.

## Cleanup and verification

The temporary `.github/workflows/publish-spike.yml` and
`Github/publish-spike/action.yml` are removed after retaining exact source and
hosted evidence. The fixture, archive driver, helper, tool pin and behavioral
controls remain reproducible evidence. No Builds release was dispatched while
experimental action metadata existed; no release state machine, production
`Github/publish`, Git tag, module pin or frozen workflow/action was changed.
The dedicated registry tag remains as the scoped experiment result; its
principal has no delete grant.

Focused tests passed on Python 3.12 and runner Python 3.14.4: 9 behavioral tests,
including real archive/ZIP corruption and identity controls, graph loss,
conflicting destination, unchanged-byte replay, failed login, private authfile
lifetime and wrong version/source/unexpected-output rejection before copy.
No workflow-text assertions were added. Both locked complete suites passed
20/20 groups on Python 3.12.13 and 3.14.4. Final online cleanup lint passed:
12 exact frozen findings and zero new findings. All 104 pre-existing runtime
Git blobs match the implementation baseline. JSON, local document links and
`git diff --check` pass. [Resumed verification](../evidence/cicd-3-1/resumed-verification.json)
records commands, interpreter versions, source/log hashes and final cleanup/API evidence.
Exact created commit messages were validated with pinned commitlint 21.2.4.

The [plan](cicd-3-1-publish-composite-spike-plan.md) remains the execution
companion; the canonical CiCd story and AD-17/spec companions receive the same
settled tool, archive, login and provenance decisions before story 3-2.
