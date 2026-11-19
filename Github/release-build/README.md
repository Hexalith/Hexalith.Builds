# Build frozen release

The reusable release's `build` job runs this composite once per create-mode
release. It checks out the frozen release commit without credentials, validates
the module configuration, sets up the frozen source SDK, then audits, builds,
packs and publishes containers once into the original `release` artifact. It
never publishes packages or images and holds no secret.

| Input | Meaning |
| --- | --- |
| `sha` | Frozen full release commit |
| `version` | Stable release version without the `v` prefix |
| `accepted-delivered-sha` | Frozen accepted delivery output from release resolve |
| `module-root` | Test-only Builds sample root |

| Output | Meaning |
| --- | --- |
| `artifact-id` | Uploaded `release` artifact identity |
| `artifact-digest` | `sha256:` digest of the uploaded original archive |

The artifact holds `release-manifest.json` (`sha`, `version`, `packages`,
`containers`), `packages/*.nupkg` and uncompressed OCI `containers/*.tar`.
Each image is published once per runtime identifier and assembled into one
index without rewriting child manifests, configs or layers. `release.targets`
keeps release audit policy and rebinds the source, revision, version and
created labels of every image.

## Publisher created instant

| Aspect | Rule |
| --- | --- |
| Property | `ContainerProvenanceCreated`, the generic Builds property that `Props/ContainerProvenance.targets` and the legacy container publisher already use |
| Time authority | The publisher's UTC clock, read once at the start of the build phase (`release_build.py --phase build`) |
| Format | `YYYY-MM-DDTHH:MM:SSZ`: an exact UTC RFC 3339 second with no fraction or offset and a real calendar date |
| Scope | Every SDK command of the build phase: project discovery, audited restore and build, declared builds, pack and each runtime identifier's container publish |

The build phase passes the instant as `-p:ContainerProvenanceCreated=<instant>`
together with `-p:ContainerGenerateLabelsImageCreated=false`. Every image's
`org.opencontainers.image.created` label carries the instant, so all projects,
images and runtime identifiers of one release share it even when the build
spans several clock seconds. There is no workflow or composite input,
environment override or other time source: commit time and per-project or
per-runtime clocks are never used. An instant that is not an exact real UTC
second fails the build before any SDK command and writes no payload.

Containers need no configuration to receive the label. A product that requires
the publisher instant reads `$(ContainerProvenanceCreated)` directly. Because
the SDK no longer generates its created labels, an
`org.opencontainers.artifact.created` label appears only when the product
declares one, and it must carry the same instant. Before the manifest is
written, the assembled archive must show the instant as every child's image
created label, and as its artifact created label when present. A mismatch fails
with `OCI created label mismatch` and writes no manifest.

The scope is labels. The SDK's own config `created` field and history stay as
written, so the child digests remain those the SDK produced. Package-only
releases keep their packages and manifest, and `solution: none` still runs no
SDK command. Complete mode publishes the stored original payload with its
original labels; it never rebuilds or reads a new instant.
