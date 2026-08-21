# Prepare Container Publisher

Composite action used by the `domain-release.yml` reusable workflow. It copies
the checked-in `publish-containers.sh` helper into
`.hexalith/release/publish-containers.sh` in the caller workspace so the module
semantic-release `publishCmd` can invoke it with the released version:

```json
"publishCmd": "... && ./.hexalith/release/publish-containers.sh ${nextRelease.version}"
```

The helper:

- validates the release version is plain SemVer,
- logs in to the Hexalith Zot registry (`HEXALITH_ZOT_REGISTRY`, default
  `registry.hexalith.com`) with `HEXALITH_ZOT_USERNAME` / `HEXALITH_ZOT_API_KEY`,
- validates every project/repository mapping and proves the complete canonical
  container destination set is absent before the registry login or first write,
- publishes each `path/to/project.csproj|repository` mapping from
  `HEXALITH_CONTAINER_PROJECTS` via .NET SDK container support
  (`/t:PublishContainer`), tagging the image with the release version,
- passes `linux-musl-x64;linux-musl-arm64` through both multi-RID properties and
  requires an OCI index containing exactly `linux/amd64` and `linux/arm64`,
- rereads the tag and immutable index/children/configs from the registry and
  verifies content types, hashes, sizes, and descriptor/config platforms while
  retaining the exact raw index, child-manifest, and config bytes, and
- runs the same bounded loopback `/alive` smoke against both immutable child
  digests after explicit digest-pinned pulls and an executable arm64 runtime
  preflight, using an isolated non-secret symmetric JWT configuration required
  for startup validation and a 180-second per-platform default liveness bound
  that accommodates emulated arm64 startup, while retaining support-safe
  bounded diagnostics, cleanup results, classifications, and hashes. Callers
  publishing containers should allocate at least 30 minutes to the complete
  release job so preflight, pulls, both bounded smokes, and evidence upload have
  headroom after publication.

`dotnet publish` success is not sufficient. A mapping succeeds only after
immutable validation and both child-digest smokes pass. Emulation setup,
registry-pull, image-start, liveness-timeout, and cleanup failures are reported
separately. `/alive` accepts only an exact 2xx response and never follows a
redirect.

The caller pins the reusable workflow to a reviewed full commit SHA and passes
the identical literal as `builds-execution-sha`. The workflow checks its
resolved job SHA, checks out the nested action at that exact commit, and invokes
the action locally. The action then compares its
action/helper bytes with the same immutable Builds commit before installing
them. The caller repository's `references/Hexalith.Builds` submodule pin is not
treated as executed release-tool identity.

The protected GitHub environment on the reusable release job supplies the
environment approval, and is the only gate an ordinary release requires.

The `require-publication-authority` input adds an opt-in corrective-release gate
on top of it, for a release that must be individually authorized rather than
merely approved. It defaults to enabled. A caller that sets it to `false` must
leave `reserved-version`, `release-authority-issue-url` and
`release-authority-owner` empty; supplying a value that the disabled posture
would ignore fails closed, as does a declaration that is neither `true` nor
`false`. Disabling the gate changes nothing else: source proof, frozen identity,
version floor, and destination-absence checks all still run.

When the gate is enabled, container publication additionally requires one
machine-verifiable, expiring, single-use release-owner authority comment on the
configured repository issue. The comment must use the exact authority schema,
bind the frozen publication-identity SHA-256, name the `release-owner` role,
carry a canonical rationale and nonce, and expire no more than 24 hours after
creation. The publisher verifies the configured GitHub owner and repository
write role, rejects edited, expired, ambiguous, mismatched, or previously
consumed authority, and records one exact GitHub Actions consumption receipt
before the first publication write.

The installed `publication_preflight.py` freezes the exact repository, reserved
version, live current-main and successful push-CI proof, normalized package IDs
and canonical manifest hash, sorted unique container repository set, platforms,
environment, GitHub run identity, approved Builds identity, and helper hashes.
It also requires every package version and every container tag to be absent. A
single repository remains the same supported caller contract; multi-container
callers repeat `--container-repository` when invoking the preflight wrapper.

`verifyRelease` re-proves the source, freezes that identity, and checks every
destination before tag creation. The pre-NuGet `publish` phase requires exact
identity equality, re-proves the source, and repeats every destination check.
The publisher then requires the prior two phases, re-proves the source, and
repeats absence for the full frozen container set once immediately before the
first `dotnet publish`. It validates all mappings before that preflight, so a
malformed or duplicate later mapping cannot allow an earlier image write. Each
phase is single-use and fail-closed; redirects, ambiguous statuses, duplicate
skipping, and overwrites are forbidden. The workflow uploads the complete
hidden release-evidence directory on success or partial failure.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| `container-projects` | Yes | Newline-separated `project.csproj\|repository` mappings. Fails when blank. |
| `builds-execution-sha` | Yes | Exact approved Builds commit used for the reusable workflow, action, and helper bytes. |
