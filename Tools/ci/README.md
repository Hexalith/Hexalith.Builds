# Builds quality checks

Run from the Builds repository root on Linux x86_64 with Python 3.12 or later,
the .NET SDK from `global.json`, Node 24 or later, PowerShell 7.6 or later
(`pwsh` on `PATH`), and the locked npm tools. The shared Python suite launches
PowerShell unconditionally for composite and tooling contract tests.

```bash
npm ci --ignore-scripts
npm audit signatures
python3 Tools/ci/lint_workflows.py --install-only
```

`tools.json` pins actionlint 1.7.12, zizmor 1.32.0 and uv 0.13.7 to official
release archives and committed SHA256 digests. The installer checks archive
integrity on every invocation, including cached downloads, and extracts fresh
executables without executing archive paths. Archive and executable replacements
are atomic, so reinstalling tools does not interrupt running checks. The default tool directory is
`/tmp/hexalith-builds-ci-tools`; `--cache PATH` selects another directory.
`--export-path` appends the verified directory to GitHub Actions' `GITHUB_PATH`.

Authenticate GitHub's read-only online audits, then run:

```bash
export GH_TOKEN="$(gh auth token)"
python3 Tools/ci/lint_workflows.py
/tmp/hexalith-builds-ci-tools/uv run --no-cache --locked --script --python 3.12 Tools/ci/check_public_api.py
```

Actionlint checks every `.github/workflows/*.yml` and `*.yaml`; its external
ShellCheck and pyflakes integrations are disabled so runner-installed tool
versions cannot alter results. Zizmor checks those workflows and every
`Github/**/action.{yml,yaml}` and root `.github/actions/**/action.{yml,yaml}`, with strict
collection, the committed config, all online
audits enabled and no severity filtering. Missing credentials, invalid output,
integrity errors and failed processes fail the lane. Offline environment
overrides are rejected. Hexalith actions require at least a ref; other actions
require full commit hashes. The sole disabled audit is `self-repository`, as
allowed by AD-11 during the runtime freeze.

AD-20 prevents repairing preexisting module runtime files during this story.
`.github/zizmor.yml` therefore ignores only exact rule/line/column findings in
legacy workflows: unpinned action refs, checkout credential persistence, and
existing action version-comment mismatches. New workflow findings still fail.
Actionlint's narrow exceptions cover newer GitHub `$/` action syntax, the
`job.workflow_*` context and native workflow `cache-mode` key schema gaps.
Unsupported action inputs remain errors.

[Zizmor's configuration documentation](https://docs.zizmor.sh/configuration/#rulesidignore)
confirms composite actions cannot be ignored through config. Their existing
findings are recorded in `frozen-composite-findings.json`, outside the frozen
files. Every entry names its rule, repository path, all start/end locations,
feature SHA256 hashes, starting revision and rationale. This baseline accepts
only composite findings with an exact match; moved, changed or new findings
fail. These temporary exceptions should be removed when the frozen runtime and
legacy release workflows are rewritten. Do not add blanket audit disables or
regenerate this baseline to make new findings disappear.

The API checker reads complete Git objects at `HEAD` (or `--ref REVISION`),
compares them with the highest semantic-version **stable** `vMAJOR.MINOR.PATCH`
tag reachable from that revision, and prints both full revisions. Prerelease
and unreachable tags do not qualify. Shallow history fails with instructions to
fetch complete history/tags; CI checks out with `fetch-depth: 0`. No reachable
stable tag is an explicit bootstrap: current documents are still parsed and
validated, and no removal comparison is attempted. Fetch tags before local use.

The public surface includes reusable `workflow_call` inputs, secrets and outputs;
composite inputs; schema files, named properties/definitions and enum values;
the imported build/package/catalog props and public property names; and catalog
`PackageVersion` names. Local imports are followed transitively. Test-only
workflow inputs `module-root` and `expected-source`, plus module-root/expected-source
schema fixtures, are excluded; those names remain public for composite inputs,
secrets and outputs. YAML, JSON and XML are
parsed with real parsers; malformed documents fail clearly. PyYAML 6.0.3 is a
CI-only PEP 723 dependency whose artifacts and hashes are recorded in both
script `.lock` files. The test runner also pins jsonschema 4.26.0 and its
transitive dependencies in its lock. Runtime scripts gain no third-party dependencies.

Additions pass. Removals print their entries and fail unless a commit in
`baseline..HEAD` has a Conventional Commit `!` header or a
`BREAKING CHANGE:`/`BREAKING-CHANGE:` footer; intentional removals print the
authorizing commit revisions. Versions/defaults/constraint changes are outside
this removal-only check and still require human compatibility assessment.

Run the same suite with Python 3.12 and the runner interpreter captured before
any setup changes its PATH:

```bash
RUNNER_PYTHON="$(command -v python3)"
/tmp/hexalith-builds-ci-tools/uv run --no-cache --locked --script --python 3.12 Tools/ci/run_python_tests.py
/tmp/hexalith-builds-ci-tools/uv run --no-cache --locked --script --python "$RUNNER_PYTHON" Tools/ci/run_python_tests.py
```

The policy regression tests require the verified actionlint/zizmor binaries and
archives installed by `--install-only` in the default tool directory, plus the
read-only GitHub token exported above. They invoke those binaries with the
committed policies and online audits enabled; missing prerequisites fail tests.

The runner invokes each `Tools/test-*.py` and `Tools/test_*.py` as a separate
script, then explicitly discovers each `Github/*/tests` group,
`test/qualification` and `Tools/ci/tests` in separate processes. This preserves
hyphenated and assertion-based `__main__` suites as well as local module imports.
`--no-cache` gives concurrent interpreter runs independent uv environments.
Every group uses the selected interpreter and all failures contribute to the
exit status. Unit tests use temporary Git repositories and synthetic API/tool
fixtures. Existing shell execution/output tests remain; workflow-source text
assertions have been removed. Legacy .NET, catalog, contract, qualification and
CodeQL jobs remain in CI; quality and both Python runs feed its aggregate verdict.

The CI configuration contract is `schemas/hexalith.ci.v1.json` (draft 2020-12).
`Github/lib/tests/fixtures/contract.json` contains complete positive and negative
documents. The automatically discovered `Github/lib/tests` group passes each
document through jsonschema and the independent stdlib loader, checks both
against the expected result, and fails any disagreement. Both interpreters run
that same corpus and validate Builds' hand-written `.github/hexalith-ci.json`.

Schema `default` values are renderer metadata. Every configuration must explicitly
contain `schema`, `solution`, `dapr`, `browsers`, `registry`, `codeql`, `tests`,
`hooks`, `jobs` and `submodules`; readers never insert values. The renderer's
`codeql` default is `["csharp"]` with a solution and `[]` with `solution: "none"`.
The other defaults are `solution: "none"`, false booleans, and empty collections
(including both `tests.integration` and `tests.exclude`). An explicit empty
`codeql` list skips analysis. Supported CodeQL identifiers follow the
[GitHub language identifiers](https://docs.github.com/en/code-security/reference/code-scanning/workflow-configuration-options),
with `actions` forbidden by the CI contract.

`tests.integration` lists repository-relative `.csproj` paths. `tests.exclude`
maps such paths to enabled hook or job names; it cannot skip a test without an
action responsible for it. Hooks are `after-unit-tests`,
`after-integration-tests` and `nightly`. Jobs map other kebab-case names (excluding
`ci` and every fixed hook name) to closed `{"runner": "windows-latest"}` objects.
`submodules` lists additional direct repository-relative submodule paths. Builds
checkout is implicit; this list never requests recursive initialization. Paths
reject absolute/drive paths, backslashes, dot/traversal segments, empty segments,
trailing slashes, control characters and `${{` expressions. A solution is a
`.slnx` path or `none`; with `none`, hooks/jobs/tests are empty and dapr/browsers
are false.

Use the single loader in `Github/lib/ci_config.py` from every runtime reader:

```python
from ci_config import load_config, validate_config

config = load_config(path, repository=repository, module_root=module_root)
config = validate_config(document, repository=repository, module_root=module_root)
```

Add the trusted Builds `Github/lib` directory to the importing application's
module search path. `repository` is a required trusted owner/name supplied by
the caller, and `module_root` defaults to an empty string. Neither is read from
the document. Only `repository="Hexalith/Hexalith.Builds"` with an empty module
root may omit `buildsTag`; samples require it. When present, a tag must be an
exact stable `vMAJOR.MINOR.PATCH` (no prerelease, build metadata or leading zeros).
The loader resolves its schema identity beside its own file, returns the same
values, raises `ConfigError` on invalid input, rejects duplicate JSON keys and
non-JSON constants, and uses no third-party modules. The CLI prints validated
JSON, or a source/location diagnostic on stderr with a nonzero exit status:

```bash
python3 -I Github/lib/ci_config.py .github/hexalith-ci.json --repository Hexalith/Hexalith.Builds
```

Schema validation cannot know the trusted repository identity or correlate
exclusion targets with arbitrary job property names. Separate loader tests cover
missing tags in module/sample contexts and references to disabled actions.
Delivery's later per-module checks verify that action paths and submodules
actually exist; this contract does not initialize repositories or run commands.

The shared CI build/test stages live in `Github/lib/ci_pipeline.py`. Its importable
`build_flags`, `prepare_config`, `discover_test_projects`, `classify_projects`,
`restore_and_build` and `execute_unit_projects` functions are also the building
blocks for the future local check. From an initialized module workspace, run
the workflow-owned script, supplying trusted repository context:

```bash
python3 -I /path/to/trusted/Builds/Github/lib/ci_pipeline.py --workspace . --repository OWNER/REPOSITORY
```

`--phase prepare`, `build` and `unit` run the corresponding composite phases;
the default `all` runs only stages 1–3, preserving the future local check's scope.
`--phase integration` runs classified integration projects without restoring,
building or setting up an environment. Preparation validates the complete document
through `ci_config.py`, then initializes Builds plus only the explicitly listed
root-declared gitlinks, with recursion disabled and literal Git pathspecs. It
uses `git submodule update --init --checkout`, so an already-initialized listed
submodule is detached at its recorded gitlink commit; run it in a disposable or
CI-like checkout rather than a working tree with advanced submodules. In
Builds' own repository the workspace is the Builds tree under test, so no Builds
gitlink is initialized, including under a test-only `module-root`; listed
submodule paths are prefixed with that root. It publishes `solution`,
`submodules` (a JSON array) and `global-json` through `GITHUB_OUTPUT` when set.
The solution output is workspace-relative, including a test-only `module-root`.
`module-root` and `expected-source` are accepted only in Builds' own repository.
`solution: none` validates configuration and invokes no SDK or submodule work.

The SDK is selected by the module's `global.json`, including `rollForward`.
The reusable workflow's optional `additional-sdk-version` installs another SDK
alongside that selection. Builds uses .NET SDK 10.0.404 to supply the .NET 10
reference packs for its isolated offline NuGet tests while `global.json` keeps
the executing SDK on 11.0.100. No extra SDK is installed when the input is empty.
Restore runs once; the Release build enables analyzers, treats warnings as
errors and uses package mode with audit off. Every MSBuild CLI invocation uses
`-noAutoResponse` and the trusted `NuGet.ci.Config` beside the executing Builds
code. That config clears sources and mappings, maps every package to nuget.org,
requires signatures and trusts only nuget.org's repository signing certificates.
The SHA-256 fingerprints were checked against the actual DER certificate bytes
advertised by the [nuget.org signatures endpoint](https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json)
on 2026-11-09; historical certificates support timestamped older packages.
Behavioral tests derive variants that relax exactly one control of the real
config: a locally packed unsigned package fails restore with NU3004 under its
signature policy and with NU1101 under its source mapping, while the control
variant with both relaxed restores. Add nuget.org's new certificate fingerprint
here when it rotates its repository signing certificate.

Discovery asks the selected SDK for actual solution membership, then evaluates
`IsTestProject` with the same Release/package properties. Cross-targeted projects
are evaluated for every target framework, trimming entries and dropping empty
ones as the SDK does; inconsistent test identity fails.
Only evaluated test projects enter AD-13 classification. Overrides must identify
known test projects, and exclusions must name an enabled hook or job. Unit
projects execute in ordinal path order, with no build or restore. Failure leaves
all tests within that project running to completion, preserves its exit status,
and prevents the next project from starting. Integration projects use the same
executor, runner selection and complete-project behavior, in ordinal path order.
`execute_unit_projects` and the existing stages 1–3 Python APIs remain compatible.

After successful unit execution, `ci-unit-tests` publishes literal `true`/`false`
outputs for `dapr`, `browsers`, `registry`, `after-unit-tests` and
`after-integration-tests`. The browser output is true when configured or when a
classified integration project's name contains `E2E`; an excluded E2E project
does not enable browsers. Failed units publish no stage gates. YAML consumes
these validated outputs and never interprets the configuration JSON.
CI also captures the validated configuration, runner and project classification
after unit tests. A hook that changes them fails before integration execution,
so it cannot silently remove the projects that stage 3 classified for stage 4.

Stage 4 runs once in the existing read-only CI job: `ci-environment`, the enabled
workspace-root `./.github/actions/after-unit-tests` composite, integration tests,
then the enabled `./.github/actions/after-integration-tests` composite. Hook
directories that are not enabled stay dormant, including `nightly` in CI. Hooks
resolve from the workspace root even with a test-only module root, and assume
the existing restored Release build and prepared environment. They never repeat
checkout, restore, build or setup. Any failed step stops every successor and
test failures retain their original status.

`Github/ci-environment` is independently reusable after a Linux prepare build.
Its `solution` input is the validated workspace-relative solution output, and its
optional `dapr`, `browsers` and `registry` inputs are literal validated booleans
(default `false`). `CI_MODULE_ROOT` selects the already validated test-only module
root when needed. Its stdlib script reads no CI configuration, restores nothing
and builds nothing; disabled tools make no calls. `solution: none` makes no SDK,
tool or hook calls.

Dapr setup reads `HexalithDaprRuntimeVersion` and `HexalithDaprCliVersion` from
the executing workflow's Builds `Props/Directory.Packages.props`, never the
module gitlink. On Linux x86_64 it downloads the official versioned CLI archive,
checks its committed SHA-256 before extracting the single regular executable,
adds that verified directory to `GITHUB_PATH`, and runs
`dapr init --runtime-version <pin>`. A CLI pin change requires adding the official
release asset's independently verified digest to `DAPR_CLI_DIGESTS`; an unknown
version or mismatched download fails before execution. Dapr manages its pinned
runtime downloads. Docker must be available for self-hosted initialization.

Browser setup asks MSBuild for the solution projects' evaluated Release output
paths, including inner target frameworks, then uses built `playwright.ps1` next
to `Microsoft.Playwright.dll` and `.playwright/package/package.json`. It runs
`pwsh -NoProfile -File <built-script> install --with-deps` once per distinct built
Playwright package version, so browser binaries match the module's packages,
including when they differ from Builds' central version. It never installs a
global Playwright tool or searches source directories for scripts. Requested
browsers without complete built tooling fail clearly. See the official
[Playwright browser installation contract](https://playwright.dev/dotnet/docs/browsers)
and [versioned Dapr initialization](https://docs.dapr.io/reference/cli/dapr-init/).

Registry setup logs into `registry.hexalith.com` with only the optional reusable
workflow secrets `registry-pull-username` and `registry-pull-password`. Builds'
caller passes the org secrets `HEXALITH_REGISTRY_PULL_USERNAME` and
`HEXALITH_REGISTRY_PULL_PASSWORD` by name. They enter only the setup step as
`REGISTRY_PULL_USERNAME`/`REGISTRY_PULL_PASSWORD`, and the password reaches
`docker login` through stdin. Enabling registry without both credentials fails
before any optional setup. No publisher credential enters this job.

The environment tests execute the actual shared job and composite metadata in a
disposable behavioral harness with SDK/tool executables and temporary hook
actions. They observe setup and hook order, dormant actions, workspace-root
resolution, one build/restore, and every failure boundary. Separate archive
tests verify Dapr integrity and safe extraction before executing a command
double; real SDK tests verify both integration runner paths and completion of
all tests in a failing project.

Ordinary runners use positional `dotnet test`; Microsoft.Testing.Platform uses
`--project`. SDK 10's MTP CLI forwards `-noAutoResponse` and `-warnaserror` to the
test application, causing handshake exit 5. The shared `test_flags` adapter
therefore retains all MSBuild properties and expresses warning enforcement as
properties for MTP. Its in-process MSBuild evaluation consumes no automatic
response files. The real SDK tests prove this with poisoned `MSBuild.rsp` and
`Directory.Build.rsp` in both the workspace and project directories; they also
prove that all three tests in a failing xUnit project finish exactly once while
its successor never starts. The MSBuild CLI paths retain both switches unchanged.

Own-code resolution was verified **before implementation** by a temporary hosted
`$/Github/ci-runtime-probe` action with no workspace checkout: [run 42615634829](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42615634829)
passed at `f2f0a08d8c38d34b390dc937659d2067c8a09cce` on runner 2.338.2.
`GITHUB_ACTION_PATH` resolved to that exact workflow commit's downloaded action.
The temporary probe files have been removed. Native `$/` works, so AD-9's pinned
own-code-checkout fallback is unnecessary. All new composites locate their
scripts through `GITHUB_ACTION_PATH`, independently of the module's Builds
gitlink. The first CI step reserves stage 1 for story 5-1's guard and commitlint;
Builds' own call runs no delivered-ref guard. A manual `workflow_dispatch` of
`builds-ci.yml` runs every lane but skips G-4 evidence archiving, whose carrier
is credited only for push and pull_request runs. Its concurrency group includes
the event name, so a dispatch never cancels the push run that release consumes.

The executor also overrides xUnit's within-project `stopOnFail` setting so
project-level failure never cancels the remaining tests in that project. xUnit
is identified by evaluated `XunitTestProject` metadata, read from each inner
build of a multi-targeted project because its outer evaluation has no
package-imported identity. MTP uses its supported `--stop-on-fail off` option;
VSTest uses `xUnit.StopOnFail=false`. The v3
VSTest adapter's separate executable can reload its JSON configuration, so only
evaluated build-output runner configs are temporarily merged with
`stopOnFail=false`, then restored byte-for-byte in `finally`. Multi-targeted
outputs are handled individually; source, tracked and escaping configurations
are rejected. Other runner settings are retained, and other test frameworks
receive no xUnit options. Real SDK tests exercise both runner paths.

`Github/prepare` prepares a separate custom job without running tests or hooks.
It always checks out without persisting credentials and without recursive submodules.
On Windows, pass shared CI's `solution`, `submodules` (the unchanged JSON-array
output), and `global-json` outputs. The SDK-path input defaults to `global.json`
for root modules; prefixed samples must pass the validated output explicitly.
Actions expressions decode the validated array to LF-delimited paths in an
environment variable. The PowerShell script reads no module configuration JSON
and invokes no Python or environment setup. An omitted Windows solution fails; explicit `none` skips preparation.
It rejects case-insensitive submodule destination collisions, unsafe paths, symlinks,
missing solutions/SDK files, undeclared submodules and non-gitlink paths before
SDK/product commands. It initializes only the exact requested root gitlinks,
with file transport and recursion disabled. The global.json directory controls
both SDK selection and cwd, including solutions nested below it or paths with
spaces. One restore and one no-restore Release build use the same mandatory
package/analyzer/warnings/audit/NuGet/response-file flags as shared Linux CI.
Native command failures preserve their status and stop every successor.

On Linux, prepare reads configuration through the same `ci_config.py` loader,
initializes direct dependencies, selects the module SDK, restores/builds once,
and calls the existing `ci-environment` composite once. It classifies projects
only to infer browsers from integration E2E names; it executes no test project
or hook. `module-root` is a Builds-only fixture input here too. Pull-only
registry credentials use the existing `REGISTRY_PULL_USERNAME` and
`REGISTRY_PULL_PASSWORD` environment contract. `solution: none` still validates
Linux configuration, while both OS paths skip SDK/build/environment/tests.
All preparation scripts resolve from the executing action's `GITHUB_ACTION_PATH`.

Builds' second shared-CI call targets `Samples/Hexalith.Sample`, a consumer with
its own SDK pin, a nested solution, central MSBuild imports, and four product
test projects. Shared CI runs units, the root `after-unit-tests` action, then
integration. The dependent Windows job has only prepare and the root
`sample-windows` action. Both custom actions invoke MTP projects with explicit
`--project`, `--no-build`, `--no-restore`, and Release selection; they reuse built
outputs and write no evidence. Only the sample configuration enables them and
delegates those projects through exclusions, avoiding duplicate test execution.
Both sample verdicts feed Builds' retained aggregate job.

The sample's expected-source fixture contains
`rendered/Hexalith.Sample/.github/hexalith-ci.json` and a sibling
`rendered/Hexalith.Sample.perimeter.json`. The latter is an empty placeholder;
story 5-1 owns its schema and enforcement integration. The retained first CI
step remains a reserved slot and provides no guard/commit-format enforcement.

Prepare tests execute real PowerShell and the actual composite metadata in a
temporary workspace. External checkout/SDK actions and command doubles are
labeled controls, covering their inputs and failure boundaries. They provide
local behavioral evidence; the dependent job on `windows-latest` supplies the
separate hosted Windows evidence.

## Reusable release gates

`.github/workflows/release.yml` is the incremental release entry point. Its
caller grants the permission union `contents: write`, `actions: read` and
`security-events: write`. The reusable workflow starts with `permissions: {}`
and grants each job only its own permissions: resolve has exactly contents
write/actions read; CodeQL has contents read/security-events write; the hook
and reporter have contents read. Version and build have contents read; draft
has contents write/actions read. Resolve executes trusted stdlib Python and
anonymous Git object reads only. Both workflow and caller use `cache-mode:
none`, and concurrency belongs to the top-level caller. The only optional
secrets are `registry-pull-username` and `registry-pull-password`, passed to
prepare in the hook job and to recovery in the draft job as
`REGISTRY_PULL_USERNAME` and `REGISTRY_PULL_PASSWORD`.

Resolve reads dispatch-commit configuration through `ci_config`, then freezes
the release SHA once. A valid marked draft selects complete mode; otherwise
the dispatch SHA selects create mode. The default qualifying CI is `ci.yml`;
only the Builds repository may pass `ci-workflow: builds-ci.yml`. The exact
switch `HEXALITH_RELEASE_PUBLISH_ENABLED=false` disables publishing while
current-dispatch CodeQL and nightly preparation/hook still run. Invalid switch
values and red exact-source CI fail without suppressing validated nightly
settings. Complete mode selects the older draft's SHA and known version but
never reads its old configuration or checks its CI again. Both nightly jobs
always use the dispatch SHA.

The final always-scheduled reporter writes only `outcome.json` in artifact
`hexalith-release-outcome`, with overwrite enabled for reruns. Its closed v1
schema is in `schemas/hexalith.release-outcome.v1.json`; runtime validation is
stdlib and the shared test corpus also uses the independent draft-2020-12
validator. Failed, cancelled, missing or unexpected job results dominate
successful disabled/no-op decisions. Explicit successful no-version produces
`nothing`. A validated successful draft hands publication and outcome ownership
to the publisher; the
reusable reporter then writes and uploads nothing. The publisher remains the
sole writer for published, ready and publishing-failure outcomes.

`Github/publish` now consumes that eight-field handoff in the caller-owned
`hexalith-release` job. Its permission union is `contents: write`, `actions:
read`, `id-token: write`; its only additional inputs are the NuGet user and
scoped registry push pair. It authenticates original files, preflights the Git
tag and every image before package writes, exchanges OIDC through pinned
NuGet/login, pushes original packages and complete OCI graphs, then creates
the GitHub tag last or reports `ready` when fresh workflow trees differ.
Its always-run finalizer owns the single outcome even when login or push fails.
See the [publisher interface](../../Github/publish/README.md) for retry, credential
and downstream caller boundaries. Production callers still use the
legacy release until their separate rewrite.

Create mode computes a version, produces an audited original artifact and
creates a marked draft. `release-rules` defaults to `module`; only the actual
Builds repository with no sample `module-root` may select `builds`. Version
calls the pinned commit-analyzer 13.0.1 and release-notes-generator 14.1.1
directly, from an isolated lockfile installation verified by `npm audit
signatures`. It reads no product npm/release configuration and runs no
semantic-release lifecycle. A nonreleasable range logs `nothing to release`,
skips build/draft and reports `nothing` with `publish=false`.

Version, build and draft consume resolve's frozen SHA. Build checks out without
stored credentials, selects that source's SDK and initializes only configured
direct submodules. Release/package/version evaluation selects packable projects
and non-test `HexalithPublishContainer=true` projects. Restore/build enable
full low-level NuGet auditing and promote NU1900, NU1903, NU1904 and NU1905
despite inherited warning exemptions. NU1901/2 remain advisories;
compiler/analyzer policy remains active. Blocking audit failures log
`vX.Y.Z blocked by a vulnerable package` and preserve the SDK exit status.
Packages are packed without another restore/build and stamped with the actual
version and commit. Each selected container RID is published once through the
SDK; only a new OCI index is assembled around unchanged child manifests,
configs and layers, preserving labels containing colons.

`Github/release-build` exposes only `artifact-id` and the `sha256:`-prefixed
`artifact-digest`. Its one 90-day artifact named `release` contains exactly
`release-manifest.json`, `packages/*.nupkg` and `containers/*.tar`. The internal
closed manifest format is:

```json
{
  "sha": "<full frozen SHA>",
  "version": "2.4.0",
  "packages": [{"id": "Package.Id", "path": "packages/Package.Id.2.4.0.nupkg"}],
  "containers": [{"name": "image-name", "path": "containers/container-1.tar"}]
}
```

Container archives are named `container-<n>.tar` by the project's position in
the solution, so distinct image names never collide as filenames; the
manifest's `name` carries the image identity. Both lists may be empty. `solution: none` uploads only the manifest and runs
no SDK or submodule command. Draft authenticates artifact ID, name, run, expiry
and API digest, verifies downloaded bytes, and derives inventory from the
manifest and actual package/OCI metadata as data without extraction. It
independently accepts exactly one stable major/minor/patch increment over the
shared highest reachable tag (or only `1.0.0` without a base). Same-run retries
reuse only an identical marked draft; competing or invalid markers fail. The
complete frozen workflow tree must match fresh `main` immediately before
creation. No product, npm or build code runs with the write token.

The persisted bot draft holds exactly five trailers: `hexalith-release`,
`hexalith-release-run`, `hexalith-release-artifact`,
`hexalith-release-packages`, and `hexalith-release-containers`. Notes cannot
inject reserved lines. The reusable workflow exposes validated `publish`,
`version`, `sha`, `last-tag`, `draft-id`, `artifact-id`, `artifact-digest` and
`artifact-run`; only a successful identity-matching handoff with successful
required jobs authorizes `publish=true`. Draft creation does not report
`published` or `ready` and creates no Git ref.

Complete mode skips version/build and authenticates the selected draft's saved
producer workflow path, manual dispatch on main, source and exact original
artifact ID/digest. The caller receives the saved run identity even after a
later dispatch or Builds bump. Recovery reads the source through bare Git,
never executes it, and never reloads its historical configuration, CI or audit.
Current-dispatch CodeQL and nightly hooks keep their existing contract.

When the original producer is missing or mismatched, or its artifact is
verifiably absent or expired, any listed NuGet package retains the draft and
fails with `draft vX.Y.Z cannot be completed: its build artifact is gone`.
NuGet content HEAD 404 can occur during validation/indexing, including after a
successful push; anonymous lookup results and elapsed time cannot establish
that a package was never published. Recovery makes no NuGet request and uses no
quiet period. This also applies to unlisted packages and mixed package/image
inventories. Manual disposition requires a separate human decision that
preserves the rule against reusing a version with different content. A usable
authenticated original artifact still permits normal completion from its bytes.

Automatic deletion requires an explicitly empty package trailer and definite
absence of every listed container version. Registry HEAD accepts OCI and Docker
manifest/index media types; only its endpoint's HTTP 404 establishes absence.
Recovery uses only the existing endpoint-bound pull pair. Missing credentials,
redirects, throttling, authorization errors, malformed responses and timeouts
retain the draft. Digest/identity disagreement, corrupt bytes and GitHub service
failures never authorize stale deletion. Explicit empty package and container
inventories make no destination requests.

After these conditions hold, recovery revalidates the unchanged bot draft and
DELETEs that release ID, requiring HTTP 204. It emits internal
`stale-draft-deleted` and `publish=false`; the reusable reporter uploads the
single `nothing` outcome. No tag, replacement draft or build is created. Any
existing or uncertain destination fails with
`draft vX.Y.Z cannot be completed: its build artifact is gone`. A successful
original-artifact handoff instead transfers outcome ownership to the publisher;
the reusable reporter uploads nothing. Failed/cancelled gates and unexpected
recovery skips remain failures. Caller integration remains downstream work.

Production callers remain on their existing workflows. `delivered-sha` is the
integration slot for story 5-1;
its presence currently provides no drift protection. The test-only
`module-root` is guarded to the Builds sample and excluded from the public
module API. Reusable build/draft jobs use no publisher secret, OIDC or
attestation permission; the separate caller publisher uses the permissions above.

CodeQL and nightly-hook run after resolve failures but honor run
cancellation; only the reporter is unconditionally scheduled. The security
gate first requires this run's uploaded SARIF to report complete processing,
because the analyze action only warns when its bounded wait times out, then
reads alerts.

Focused behavioral controls (real disposable Git histories, local HTTP
pagination/redirect/error endpoints and executed composite adapters) run with
the command below. They need the locked runner's PyYAML and jsonschema and
`node` on `PATH`; on a bare interpreter, use the locked
`Tools/ci/run_python_tests.py` command above.

```bash
python3 -m unittest discover -s Github/lib/tests -p 'test_release_*.py' -v
```

[Story 3-2 verification results](../../docs/cicd-3-2-release-gates-results.md)
record exact-source hosted observations separately from deterministic local
controls, alongside dual-interpreter, workflow-lint, public-API and frozen-file
checks.

[Story 3-3 verification results](../../docs/cicd-3-3-release-create-mode-results.md)
record create-mode artifact/draft observations separately from deterministic
controls. The focused suite also executes the two pinned npm plugins, imported
and multitarget SDK evaluations, real NuGet vulnerability blocking, independent
audit-code controls, package commit/version inspection and dual-RID SDK OCI
publication with unchanged child bytes. Disposable fixtures live under
`test/fixtures/release/`; their packages and container names are qualification
payloads and must never be published. Temporary hosted probe callers/helpers
are removed after evidence collection. Frozen legacy runtime and production
caller bytes are compared with the canonical story's implementation baseline.

[Story 3-4 verification results](../../docs/cicd-3-4-publish-composite-results.md)
retain the isolated caller's successful Trusted Publishing exchange, original
package/complete OCI publication, `ready` outcome, and duplicate-safe
`published` replay from the same original handoff. Publisher tests execute
both tag forms and conflict refusal through the actual composite, cover every
failure/finalizer boundary, and ensure package status logs never expose raw
tool output or keys. Retained full source, run/artifact/release/tag identities
and public package observations distinguish hosted effects from local controls.


[Story 3-5 verification results](../../docs/cicd-3-5-release-complete-mode-results.md)
record original-artifact recovery, conservative absence/deletion controls and
actual reusable workflow handoff separately from hosted publication evidence.
The probe has no publisher job and preserves the retained 3-4 draft/resources.
