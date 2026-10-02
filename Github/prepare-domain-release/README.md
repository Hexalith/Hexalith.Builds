# Prepare domain release

This composite prepares ordinary releases in a caller-owned protected job. The
caller proves current `main` and successful exact-source push CI before entering
the environment; this action repeats that proof after its Release build and
immediately before the caller authenticates and publishes. Exact-source CI is
the test authority, so the action does not rerun tests or add governed or
container publication behavior. Existing `domain-release.yml` callers retain
their existing contract.

Pin `Hexalith/Hexalith.Builds/Github/prepare-domain-release` to a reviewed full
40-character commit and pass the identical `builds-execution-sha`. The action
checks `github.action_repository` and `github.action_ref` through composite
step environment values before any preparation, then checks out that approved
Builds commit for its local `Github/initialize-build` action. The caller's Builds
gitlink is an independent development dependency. Bootstrap initializes only
root-declared submodules without recursion.

Required inputs are `solution` (an existing relative `.slnx`),
`builds-execution-sha`, and `expected-package-count` (a positive integer declared
by the caller). Optional inputs default to `source-branch: main`,
`source-ci-workflow: ci.yml`, `package-manifest: tools/release-packages.json`,
`dotnet-global-json: global.json`, `packages-lock-file: Directory.Packages.props`,
and `node-version: '24'`. The manifest must contain exactly the declared unique
package IDs and unique existing project paths.

The caller checks out `${{ github.sha }}` with full history, `submodules: false`,
and `persist-credentials: false` before calling the action. Its job owns
`environment: production`, the usual semantic-release write permissions,
`actions: read`, and `id-token: write` when using NuGet trusted publishing.
NuGet must see the package repository's workflow identity, so authentication and
semantic-release both remain in that caller job.

After npm installation and signature verification, SDK setup, restore, and the
Release build, the action compares the repository variable
`HEXALITH_RELEASE_PUBLISH_ENABLED` using a case-sensitive, untrimmed shell
comparison. Only `true` permits publication. All other values return
`publish-enabled: false` with a notice; the caller skips login and publication.
Set this variable explicitly at repository scope to avoid inheriting an
organization value unintentionally.

When enabled, `nuget-user` must contain the individual NuGet account that created
the registered trusted publishing policy. Pass `${{ vars.NUGET_USER }}` and
never derive it from the GitHub actor or package owner. Empty or whitespace-only
values fail before authentication. The checked-out and dispatch SHAs must be
the same exact lowercase commit, still be the live `main` tip, and have a
completed successful `push` run of the configured CI workflow.

Call the SHA-pinned `NuGet/login` immediately after successful preparation and
conditionally on `steps.prepare.outputs.publish-enabled == 'true'`. Then run
semantic-release under that same condition with `NUGET_API_KEY` taken exclusively
from the login output. No key is minted, accepted, transferred, or logged by
this action. Caller-owned final semantic-release hooks must still revalidate
source, package inventory, immutable execution identity, and destination
absence at each publication boundary.

Run `python3 Tools/test-prepare-domain-release.py` for hermetic behavior tests of
identity, input/manifest, freeze, creator, source and CI rejection branches.
