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
  (`/t:PublishContainer`), tagging the image with the release version.

Because the script ships inside this action, it always matches the
`domain-release.yml` revision that resolved `@main` — the caller repository's
`references/Hexalith.Builds` submodule pin is not involved.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| `container-projects` | Yes | Newline-separated `project.csproj\|repository` mappings. Fails when blank. |
