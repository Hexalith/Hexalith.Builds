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

Because repository policy resolves the reusable workflow through mutable
`@main`, the caller supplies one maintainer-approved `builds-execution-sha`.
The workflow checks its resolved job SHA, checks out the nested action at that
exact commit, and invokes the action locally. The action then compares its
action/helper bytes with the same immutable Builds commit before installing
them. The caller repository's `references/Hexalith.Builds` submodule pin is not
treated as executed release-tool identity.

The protected GitHub environment on the reusable release job supplies human
publication approval. The installed `publication_preflight.py` supplies the
machine-verifiable contract without a separate comment or expiring record. It
freezes the exact repository, version, workflow source SHA, container
repository, platforms, environment, GitHub run identity, approved Builds
identity, and helper hashes. It also requires all 14 package versions and the
container tag to be absent.

`verifyRelease` freezes that identity and checks every destination before tag
creation. The pre-NuGet `publish` phase requires exact identity equality and
repeats every destination check. The publisher then requires the prior two
phases and repeats container-tag absence immediately before `dotnet publish`.
Each phase is single-use and fail-closed; duplicate skipping and overwrites are
forbidden. The workflow uploads the complete hidden release-evidence directory
on success or partial failure.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| `container-projects` | Yes | Newline-separated `project.csproj\|repository` mappings. Fails when blank. |
| `builds-execution-sha` | Yes | Exact approved Builds commit used for the reusable workflow, action, and helper bytes. |
