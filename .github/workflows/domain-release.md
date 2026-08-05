# Hexalith Domain Release reusable workflow

`domain-release.yml` is a reusable (`workflow_call`) release pipeline for
Hexalith domain modules. It checks out the caller repository, initializes .NET,
installs Node.js dependencies, restores and builds the solution, optionally runs
Dapr-backed tests, and then runs semantic-release.

## Inputs

| Input | Required | Default | Description |
|-------|----------|---------|-------------|
| `solution` | Yes | - | Path to the `.slnx` or `.sln` file to restore and build. |
| `dotnet-global-json` | No | `global.json` | Path to the SDK-pinning `global.json` file. |
| `packages-lock-file` | No | `Directory.Packages.props` | File used to build the NuGet cache key. |
| `dapr-version` | No | `1.18.0` | Dapr version used when tests are enabled. |
| `test-platform` | No | `vstest` | Test command contract. Set `microsoft-testing-platform` for xUnit v3 MTP-native TRX reporting. |
| `test-projects` | No | `''` | Newline-separated test project paths to run before release. Leave empty when the caller already proved exact-source CI success. |
| `node-version` | No | `24` | Node.js major line passed to `actions/setup-node`. Pinned to the Active LTS major that satisfies semantic-release 25 (`>=24.10.0`) and ships the npm major `package-lock.json` was built with; override to move ahead deliberately. |
| `timeout-minutes` | No | `20` | Timeout for the release job. |
| `environment-name` | No | `production` | Protected caller-repository environment that supplies human release approval. |
| `publish-containers` | No | `false` | Whether to prepare semantic-release container publishing for .NET SDK container projects. |
| `container-projects` | No | `''` | Newline-separated container mappings in `path/to/project.csproj|repository-name` format. Required when `publish-containers` is `true`. |
| `builds-execution-sha` | Yes | - | Exact maintainer-approved Builds commit. It must equal the reusable workflow's full-SHA `uses` revision and is checked against nested action/helper bytes. |
| `source-branch` | No | `main` | Protected source branch revalidated at every publication boundary. |
| `source-ci-workflow` | No | `ci.yml` | Workflow filename whose successful exact-source `push` run authorizes the source. |
| `package-manifest` | No | `tools/release-packages.json` | Caller package manifest frozen into publication identity. |
| `expected-package-count` | No\* | `0` | Number of NuGet package IDs the caller publishes. \*Effectively required when `publish-containers` is `true`: a dedicated workflow step rejects the default `0` and any non-positive-integer value immediately after checkout, before `npm ci` or any publication step. Declared by the caller rather than derived from the manifest, so an accidental inventory change fails closed. |

### Governed mode inputs (BUILD-REL-1)

Every input below is optional and defaults to the pre-BUILD-REL-1 behavior. They
are read only when `governed-release` is `true`; see
[Governed release mode](#governed-release-mode-build-rel-1).

| Input | Required | Default | Description |
|-------|----------|---------|-------------|
| `governed-release` | No | `false` | Opt into the governed contract: signing, a pre-publication candidate phase, build-provenance attestation over those exact packages, and GOV-1 handoff I/O. |
| `release-commit` | Governed | `''` | Exact authenticated CI-handoff candidate. In governed mode every checkout, prepare, seal, verify, classify, and publish operation consumes this commit. |
| `candidate-command` | Governed | `''` | Caller-owned command that packs, signs, and timestamps the candidate packages. |
| `candidate-directory` | No | `.hexalith/release/candidate` | Relative workspace directory the candidate `.nupkg` files are produced in. |
| `nuget-signing-timestamper` | No | `http://timestamp.digicert.com` | Absolute RFC 3161 timestamp-authority URL the candidate phase signs against. |
| `ci-run-id` / `ci-run-attempt` | Governed | `''` | Run identity of the authenticated CI run that produced the candidate handoff. |
| `ci-handoff-artifact` | No | `dependency-release-handoff` | Artifact name carrying the authenticated `hexalith.dependency-release-handoff.v1` evidence. |
| `dependency-policy-repository` | Governed | `''` | Normalized `github.com/owner/repository` identity owning the active dependency policy. |
| `dependency-policy-path` | Governed | `''` | Repository-relative path of the active dependency policy. |
| `dependency-policy-commit` | Governed | `''` | Exact 40-hex commit of the active dependency policy. |
| `dependency-policy-sha256` | Governed | `''` | Exact 64-hex SHA-256 of the active dependency-policy bytes. |
| `expected-release-evaluator-digest` | Governed | `''` | Expected Release evaluator-authorization digest recorded into the governed provenance. |

## Protected environment and caller secrets

| Caller secret | Required | Description |
|--------|----------|-------------|
| `NUGET_API_KEY` | Yes | API key used by semantic-release package publishing. |
| `HEXALITH_ZOT_USERNAME` | No | Hexalith Zot username used when `publish-containers` is `true`. |
| `HEXALITH_ZOT_API_KEY` | No | Hexalith Zot API key used when `publish-containers` is `true`. |
| `NUGET_SIGNING_CERTIFICATE_BASE64` | Governed | Base64 PKCS#12 signing certificate. Read only by the governed candidate phase. |
| `NUGET_SIGNING_CERTIFICATE_PASSWORD` | Governed | Password of that certificate. Read only by the governed candidate phase. |

Store these credentials at caller repository or organization scope and map each
declared name explicitly. Do not use `secrets: inherit`. Legacy callers map
`NUGET_API_KEY` plus, when `publish-containers` is `true`,
`HEXALITH_ZOT_USERNAME` and `HEXALITH_ZOT_API_KEY`. Governed callers
(`governed-release: true`) must additionally map
`NUGET_SIGNING_CERTIFICATE_BASE64` and `NUGET_SIGNING_CERTIFICATE_PASSWORD`; the
governed contract gate refuses to run the candidate phase without both, because
it will not publish unsigned packages. The one exception is a frozen caller: when
`vars.HEXALITH_RELEASE_PUBLISH_ENABLED` is not exactly `true`, the candidate phase
never runs and the signing secrets are not required for that run. The reusable
publication job references the protected environment named by
`environment-name`, so GitHub does not start the job or expose its explicitly
passed credentials until environment protection passes. The environment does
not need duplicate credential values.

The workflow also uses the caller repository `GITHUB_TOKEN` for semantic-release
GitHub operations. Container publishing uses the organization variable
`HEXALITH_ZOT_REGISTRY`; when it is not set, the workflow defaults to
`registry.hexalith.com`.

## Release publication freeze

> **Rollout is fail-closed for every module.** This gate is common to all
> Hexalith modules, governed or not. The moment a caller re-pins to a Builds
> commit containing it, that module stops publishing until its owner sets
> `HEXALITH_RELEASE_PUBLISH_ENABLED` to `true` at repository scope. Set the
> variable on every module that should keep publishing **before** re-pinning it.
> A module that is silently frozen still concludes green, so the omission shows
> up as a release that produced nothing rather than as a failed run.

Both the legacy and the governed job evaluate one common gate and skip Semantic
Release unless the caller variable `HEXALITH_RELEASE_PUBLISH_ENABLED` is set. A
frozen module concludes **green** with an explicit notice: freezing publication
is a deliberate state, not a failure, so a frozen module must not produce a red
run every time someone dispatches Release.

The two jobs evaluate the gate at different points on purpose:

- The **legacy** job evaluates it immediately before the source revalidation, so
  its build and test steps still run and still report.
- The **governed** job evaluates it as its **first step**, before the contract
  gate. A frozen governed caller therefore skips the signing-secret requirement,
  the whole build and test phase, the candidate phase, the attestation, and
  Semantic Release. Without this ordering, a deliberately frozen module would
  fail for signing secrets it has no use for and pay a full build for a
  publication that never happens.

```text
vars.HEXALITH_RELEASE_PUBLISH_ENABLED = true
```

Two properties of this gate are easy to get wrong:

- **The comparison is an exact, case-sensitive, untrimmed shell string.** Only
  the four characters `true` authorize publication. `TRUE`, `True`, and `" true"`
  all leave the module frozen. This is why the gate is a shell step and not a
  workflow expression: GitHub's `==` folds case, so `TRUE` would silently
  unfreeze a module whose owner never unfroze it.
- **A repository value shadows the organization value.** If your organization
  sets `HEXALITH_RELEASE_PUBLISH_ENABLED = true`, every repository that never
  set its own value inherits `true`. Set the variable explicitly at repository
  scope on every module, including the ones you intend to keep frozen.

Reusable workflows resolve `vars` from the **caller's** repository and
organization, not from Hexalith.Builds, so the variable belongs on the module
repository. This was verified against GitHub's documented behavior; no
`publish-enabled` input fallback is required.

When the gate is frozen the governed job still runs its candidate-free evidence
collection, so `release-verification-data.json` is uploaded with closed null
fields rather than omitted.

## Governed release mode (BUILD-REL-1)

Setting `governed-release: true` selects a second job, `governed-release`, and
disables the legacy `release` job. The two jobs are mutually exclusive by
design: GitHub resolves job permissions statically, so `id-token: write` and
`attestations: write` cannot be granted conditionally inside a single job
without demanding them from every legacy caller. Only the governed job carries
them.

The governed job mirrors the legacy step sequence and adds:

1. **Freeze gate first.** The governed job resolves
   `HEXALITH_RELEASE_PUBLISH_ENABLED` before anything else, so a frozen module
   skips the signing-secret requirement, the build, the candidate phase, the
   attestation, and Semantic Release.
2. **Contract validation.** Every governed coordinate is checked before
   anything is checked out, including `expected-package-count` and a
   `candidate-directory` that must be relative and free of empty, `.`, and `..`
   segments, because that value is later passed to `rm -rf`. Signing secrets are
   required here whenever the freeze gate authorized publication, so they are
   rejected with an explicit diagnostic rather than discovered mid-publication.
3. **Exact-candidate checkout.** The workspace is checked out at
   `release-commit`. The event head only authenticates the triggering CI run;
   it is never used as the release source.
4. **Identity validation.** `job.workflow_repository`, `job.workflow_sha`, and
   the `job.workflow_ref` path must all name
   `Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml` at the
   approved `builds-execution-sha`. Repository and commit alone would also be
   satisfied by a different reusable workflow in the same commit.
5. **Workflow provenance.** The `Github/governed-provenance` composite, loaded
   from the approved Builds checkout, validates `job.workflow_ref` /
   `job.workflow_sha` and evaluates the bounded static `uses:` closure of
   `domain-release.yml`, hashing every Builds-owned source and recording every
   external action as a pinned 40-hex coordinate. When
   `expected-release-evaluator-digest` is supplied, the evaluated closure digest
   must equal it exactly or the job fails closed.
6. **Candidate phase.** `semantic-release --dry-run` resolves the next version
   with no publication side effect; the caller's `candidate-command` then packs,
   signs, and timestamps exactly that version into `candidate-directory`. The
   command receives `HEXALITH_RELEASE_CANDIDATE_VERSION`,
   `HEXALITH_RELEASE_CANDIDATE_DIRECTORY`,
   `HEXALITH_NUGET_SIGNING_TIMESTAMPER`, and the two signing secrets. Candidate
   packages must be regular files: a symlinked `.nupkg` is refused, because its
   target could differ from the hashed and attested bytes. When
   `expected-package-count` is a positive declaration, a candidate inventory of a
   different size is refused. When the dry run resolves no version, the job
   records that no release was warranted and produces no candidate.
7. **Attestation.** `actions/attest-build-provenance` runs over those exact
   `.nupkg` bytes, and a following step fails closed if the bundle is missing or
   empty. Attestation therefore always precedes the first publication side
   effect.
8. **Publication.** `npx semantic-release` runs only when the freeze gate
   authorized publication **and** the candidate phase produced an attested
   version, with the candidate version,
   inventory, attestation bundle/id/url, provenance path, and the GOV-1
   coordinates exported as `HEXALITH_RELEASE_*` environment variables, so the
   caller's `prepareCmd` / `verifyReleaseCmd` can build its own evidence.
9. **Verification data.** Under `if: always()`, the job writes
   `.hexalith/release/governed/release-verification-data.json`
   (`hexalith.builds-release-verification-data.v1`) and uploads the governed
   evidence directory with `if-no-files-found: error`. Unavailable release data
   is represented as explicit `null`, never by omitting a field or the artifact.

Signing secrets are referenced in exactly two places: the presence probes in the
contract gate (which compare without exposing the value) and the candidate
phase. They are never passed to Semantic Release, the container publisher, or an
uploaded artifact.

### Governed outputs

| Output | Description |
|--------|-------------|
| `governed-candidate` | Exact candidate commit every governed phase consumed. |
| `governed-release-version` | Version semantic-release resolved, when a release was warranted. |
| `governed-publish-enabled` | Whether the freeze gate authorized Semantic Release. |
| `governed-attestation-bundle` | Path of the attestation bundle minted over the candidate packages. |
| `governed-verification-data-path` | Path of the durable release-verification handoff data. |
| `governed-provenance-path` / `governed-provenance-sha256` | Governed provenance document and the SHA-256 of its bytes. |
| `governed-closure-digest` | Canonical digest of the evaluated reusable/composite closure. |
| `governed-reusable-blob-sha256` | SHA-256 of the `domain-release.yml` blob that executed. |
| `governed-actions-json` | Canonical JSON array of the Builds-owned composite sources in the closure. |

The caller composes these into its own
`hexalith.release-verification-handoff.v1` artifact. Builds provides the
governed execution context; callers still own their evidence logic.

## Steps

1. Wait for required protection on the configured environment, check out the
   caller repository with full history and no persisted credentials, verify the resolved
   reusable-workflow identity, check out that exact Builds commit, then
   initialize root-declared submodules through its local `initialize-build`
   action.
2. Initialize .NET with `actions/setup-dotnet` from `global.json`.
3. Set up Node.js.
4. Cache NuGet packages.
5. Run `npm ci` and `npm audit signatures`.
6. Restore and build the solution in Release configuration with warnings as
   errors.
7. If `test-projects` is not empty, initialize Dapr, run each test project, and
   upload TRX/coverage evidence (`release-test-results`, `if: always()`).
8. If `publish-containers` is `true`, run the SHA-pinned arm64 emulation setup.
9. Invoke the nested
   `Github/publish-containers` action from the immutable local checkout, and
   install the publisher, immutable OCI validator, publication preflight,
   and child-digest smoke helpers. The action also compares its action/helper
   bytes with the same approved Builds commit.
10. Evaluate the `HEXALITH_RELEASE_PUBLISH_ENABLED` freeze gate. When the module
    is frozen, the remaining publication steps are skipped and the job concludes
    green.
11. Resolve the actual checked-out `HEAD`, require it to equal the exact
    lowercase caller SHA, then re-resolve live `main` and require the same
    identity immediately before Semantic Release. This late gate catches a
    mismatched or stale source while the protected job is setting up or building,
    before any Semantic Release lifecycle or publication effect can run.
12. Run `npx semantic-release`, passing the approved Builds identity, source
    proof inputs, package manifest, and protected environment name to the
    caller's publication preflight; always
    upload the complete hidden release-evidence directory afterward.

The generated publish helper accepts the semantic-release version as its first
argument. After environment approval, the caller's `verifyReleaseCmd` must
re-prove exact current `main` and successful exact-SHA push CI, freeze exact
repository, version, source proof, Builds, environment, run, helper, and
canonical package-manifest identity, and prove destination absence before
semantic-release creates a Git tag. The `publishCmd` must require exact
frozen-identity equality, repeat the live source proof, and revalidate
immediately before the first NuGet push, then call the container helper with the
same version. The helper requires both earlier phases and repeats the live
source proof and container absence immediately before publication. Existing
package/tag identities are collisions; duplicate skipping is forbidden. The
helper logs in to Hexalith Zot only when semantic-release reaches `publishCmd`:

```json
"verifyReleaseCmd": "bash scripts/validate-publication-preflight.sh ${nextRelease.version} verify >&2",
"publishCmd": "bash scripts/validate-publication-preflight.sh ${nextRelease.version} publish >&2 && dotnet nuget push ./nupkgs/*.nupkg --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json && ./.hexalith/release/publish-containers.sh ${nextRelease.version}"
```

The caller wrapper must pass `--expected-package-count` with the module's own
package count, and the caller must set the matching `expected-package-count`
input so the container phase receives it through
`HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT`. The count is caller-declared rather
than derived from the manifest: an inventory that silently gains or loses a
package must fail closed instead of redefining the gate it is checked against.

If `main` advances after the caller preflight or environment approval, the late
source gate exits nonzero before Semantic Release. The failed run is not safe to
retry against its old dispatch SHA: wait for exact-source push CI to succeed on
the new `main` tip, then manually dispatch Release from that tip. A `main`
advance after a completed publication does not invalidate the release; callers
should verify the published tag resolves to the originally dispatched SHA.

## Usage

The standard caller is manually dispatched. A caller-owned preflight must prove
the selected SHA is the current `main` tip with successful exact-SHA push CI
before the reusable protected-environment job is called (see
`ci-cd-standards.md`, "Release Gates"):

```yaml
on:
  workflow_dispatch:

concurrency:
  group: release-production
  cancel-in-progress: false

permissions:
  contents: read

jobs:
  verify-source:
    runs-on: ubuntu-latest
    # Fail unless this is the current main SHA with successful exact-SHA push CI.

  release:
    needs: verify-source
    permissions:
      actions: read
      contents: write
      issues: write
      pull-requests: write
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@0123456789abcdef0123456789abcdef01234567
    with:
      solution: Hexalith.<Module>.slnx
      environment-name: production
      publish-containers: true
      builds-execution-sha: 0123456789abcdef0123456789abcdef01234567
      expected-package-count: 5
      container-projects: |
        src/Hexalith.<Module>/Hexalith.<Module>.csproj|module-name
    secrets:
      NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
      HEXALITH_ZOT_USERNAME: ${{ secrets.HEXALITH_ZOT_USERNAME }}
      HEXALITH_ZOT_API_KEY: ${{ secrets.HEXALITH_ZOT_API_KEY }}
```

The recommended organization-level values are:

```text
vars.HEXALITH_ZOT_REGISTRY = registry.hexalith.com
vars.HEXALITH_BUILDS_RELEASE_SHA
caller repository/organization: secrets.HEXALITH_ZOT_USERNAME
caller repository/organization: secrets.HEXALITH_ZOT_API_KEY
caller repository/organization: secrets.NUGET_API_KEY
governed callers only: secrets.NUGET_SIGNING_CERTIFICATE_BASE64
governed callers only: secrets.NUGET_SIGNING_CERTIFICATE_PASSWORD
```

Pass exactly these declared names. The job-level protected environment gates
their use without requiring a second credential copy.

The SHA above is a placeholder: replace both occurrences with the same reviewed
40-character Builds commit. Do not substitute a branch, tag, expression, or
repository variable. The caller's `references/Hexalith.Builds` gitlink remains
an independent development dependency and need not equal the executed release
tool SHA. See `ci-cd-standards.md` for shared CI/CD policy.

### Governed usage

A governed caller adds the governed inputs, the two signing secrets, and the
`attestations` / `id-token` permissions. The candidate commit comes from the
authenticated CI handoff, not from `github.sha`:

```yaml
  release:
    needs: verify-source
    permissions:
      actions: read
      attestations: write
      contents: write
      id-token: write
      issues: write
      pull-requests: write
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-release.yml@0123456789abcdef0123456789abcdef01234567
    with:
      solution: Hexalith.<Module>.slnx
      environment-name: production
      builds-execution-sha: 0123456789abcdef0123456789abcdef01234567
      expected-package-count: 5
      governed-release: true
      release-commit: ${{ needs.verify-source.outputs.candidate }}
      candidate-command: bash scripts/pack-governed-candidate.sh
      ci-run-id: ${{ needs.verify-source.outputs.ci-run-id }}
      ci-run-attempt: ${{ needs.verify-source.outputs.ci-run-attempt }}
      dependency-policy-repository: github.com/hexalith/hexalith.<module>
      dependency-policy-path: eng/dependency-graph-policy.json
      dependency-policy-commit: ${{ needs.verify-source.outputs.policy-commit }}
      dependency-policy-sha256: ${{ needs.verify-source.outputs.policy-sha256 }}
      expected-release-evaluator-digest: ${{ vars.HEXALITH_RELEASE_EVALUATOR_DIGEST }}
    secrets:
      NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}
      NUGET_SIGNING_CERTIFICATE_BASE64: ${{ secrets.NUGET_SIGNING_CERTIFICATE_BASE64 }}
      NUGET_SIGNING_CERTIFICATE_PASSWORD: ${{ secrets.NUGET_SIGNING_CERTIFICATE_PASSWORD }}
```

The caller's permissions block must include everything the governed job
requests: GitHub rejects a called workflow that asks for more than its caller
was granted.
