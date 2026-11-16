# Prepare a module build

This opt-in composite restores the caller's exact local module CLI manifest and
runs `prepare-build --mode package` without starting resources. Supply a
repository-relative v2 `manifest`. Install the pinned .NET SDK and initialize the
root-declared identity dependencies first. The helper needs Python 3.9 or later.
Before preparation, the helper fetches tags from the initialized direct Platform
checkout's `origin` with submodule recursion disabled. An unavailable checkout or
failed tag fetch blocks preparation and exports nothing; nested checkouts are
never initialized. A Platform-root caller without a direct Platform dependency
uses its own identity through the producer and requires no dependency tag fetch.

The tool version must be exact, `rollForward` must be false, and the observed
restored tool version must match. Only five verified generated MSBuild imports,
the selected mapping hash and Release configuration are exported to `GITHUB_ENV`.
The helper parses JSON and never evaluates producer output as shell commands.
Preparation failures export nothing and keep existing files intact.

The reusable domain CI workflow resolves this action from its executing Builds
revision. Direct callers use their qualified root Builds checkout or an approved
Builds execution checkout. Accepted CLI 4.35.2 lacks this command: separate
producer qualification/publication, accepted exact tool pin and a compatible
Platform/tag/catalog tuple are prerequisites. No version is selected here.

Run helper checks with `python3 -m unittest discover -s Github/prepare-module-build/tests`.


Shared CI, the local checker and release-build also accept the separate nullable
`publishedBuilds` CI configuration input. It selects a committed closed
`shared-release-handoff/v1` tuple instead of `moduleManifest`. Shared code verifies
the accepted active Builds tag/catalog separately from the historical official
producer tag/source/catalog, release body/run/manifest, archive SHA-256, NuGet
signature, Tooling digest, required host assembly and full executable identity.
It invokes no module preparation script and exports only
`HexalithBuildsPublishedToolRoot` and `Configuration=Release` for that producer.
Preparation refuses ambient imports and candidate authority, revalidates inputs
and output bytes on reuse, restores local caller environment, and removes failed
private scratch. Linux shared entry points are supported; platform jobs fail
closed. Absent/null preserves the existing managed and unprepared paths.

Optional `buildProjects: []` requires either authenticated preparation mode. Up to
32 committed non-test consumers have unique kebab-case IDs, safe `.csproj` paths
and nonempty `versions` maps containing only exact stable selections for
`HexalithEventStoreVersion`, `HexalithTenantsVersion` and
`HexalithMemoriesVersion`. Duplicate selections, symlinks, foreign references,
indirect imports, hidden execution and policy overrides are refused. Cases run in
declaration order after the root solution; restore/build/evaluation retain the
same mandatory flags, including release targets/audits when releasing. Each case
uses separate SDK artifacts under the existing private preparation lifetime.
The first failure stops successors and publication; root test classification is
unchanged and these consumers are excluded from package discovery.

`HexalithBuildProjects` exports a JSON object keyed by case ID. Each value contains
`project`, `versions` and absolute `artifactsPath`; the SDK creates its `bin` and
`obj` trees beneath that path. Product actions may read these outputs through the
job and must not invoke another setup, restore or build. Local/release scopes
remove case outputs on completion; CI keeps the private workspace through the
job, using the existing runner temporary-directory cleanup. Unsupported platform
jobs are rejected. These source additions do not qualify a published release or
adopt a module.
