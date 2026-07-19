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
  verifies content types, hashes, sizes, and descriptor/config platforms, and
- runs the same bounded loopback `/alive` smoke against both immutable child
  digests, retaining support-safe logs, classifications, and hashes.

`dotnet publish` success is not sufficient. A mapping succeeds only after
immutable validation and both child-digest smokes pass. Emulation setup,
image-start, and liveness-timeout failures are reported separately.

Because repository policy resolves the reusable workflow through mutable
`@main`, the caller supplies one maintainer-approved `builds-execution-sha`.
The workflow checks its resolved job SHA, checks out the nested action at that
exact commit, and invokes the action locally. The action then compares its
action/helper bytes with the same immutable Builds commit before installing
them. The caller repository's `references/Hexalith.Builds` submodule pin is not
treated as executed release-tool identity.

The installed `publication_authority.py` validates a separate durable
release-owner record immediately before publication. It binds repository,
version, workflow source SHA, container repository, exact platforms, owner,
validity window, rationale, durable source, approved Builds identity, and helper
hashes. It also requires all 14 package versions and the container tag to be
absent. The validator records frozen authority/source/check-time evidence but
does not create human authority.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| `container-projects` | Yes | Newline-separated `project.csproj\|repository` mappings. Fails when blank. |
| `builds-execution-sha` | Yes | Exact approved Builds commit used for the reusable workflow, action, and helper bytes. |
