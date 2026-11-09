# Builds quality checks

Run from the Builds repository root on Linux x86_64 with Python 3.12 or later,
the .NET SDK from `global.json`, Node 24 or later, and the locked npm tools:

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
`Github/**/action.yml`, with strict collection, the committed config, all online
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

The shared CI stages 2–3 live in `Github/lib/ci_pipeline.py`. Its importable
`build_flags`, `prepare_config`, `discover_test_projects`, `classify_projects`,
`restore_and_build` and `execute_unit_projects` functions are also the building
blocks for the future local check. From an initialized module workspace, run
the workflow-owned script, supplying trusted repository context:

```bash
python3 -I /path/to/trusted/Builds/Github/lib/ci_pipeline.py --workspace . --repository OWNER/REPOSITORY
```

`--phase prepare`, `build` and `unit` run the corresponding composite phases;
the default `all` runs them in order. Preparation validates the complete document
through `ci_config.py`, then initializes Builds plus only the explicitly listed
root-declared gitlinks, with recursion disabled. It publishes `solution`,
`submodules` (a JSON array) and `global-json` through `GITHUB_OUTPUT` when set.
The solution output is workspace-relative, including a test-only `module-root`.
`module-root` and `expected-source` are accepted only in Builds' own repository.
`solution: none` validates configuration and invokes no SDK or submodule work.

The SDK is selected by the module's `global.json`, including `rollForward`.
Restore runs once; the Release build enables analyzers, treats warnings as
errors and uses package mode with audit off. Every MSBuild CLI invocation uses
`-noAutoResponse` and the trusted `NuGet.ci.Config` beside the executing Builds
code. That config clears sources and mappings, maps every package to nuget.org,
requires signatures and trusts only nuget.org's repository signing certificates.
The SHA-256 fingerprints were checked against the actual DER certificate bytes
advertised by the [nuget.org signatures endpoint](https://api.nuget.org/v3-index/repository-signatures/5.0.0/index.json)
on 2026-11-09; historical certificates support timestamped older packages.

Discovery asks the selected SDK for actual solution membership, then evaluates
`IsTestProject` with the same Release/package properties. Cross-targeted projects
are evaluated for every target framework; inconsistent test identity fails.
Only evaluated test projects enter AD-13 classification. Overrides must identify
known test projects, and exclusions must name an enabled hook or job. Unit
projects execute in ordinal path order, with no build or restore. Failure leaves
all tests within that project running to completion, preserves its exit status,
and prevents the next project from starting. Integration projects are classified
but stage 4 and hooks are not implemented by these stages.

Ordinary runners use positional `dotnet test`; Microsoft.Testing.Platform uses
`--project`. SDK 10's MTP CLI forwards `-noAutoResponse` and `-warnaserror` to the
test application, causing handshake exit 5. The shared `test_flags` adapter
therefore retains all MSBuild properties and expresses warning enforcement as
properties for MTP. Its in-process MSBuild evaluation consumes no automatic
response files. The real SDK tests prove this with poisoned `MSBuild.rsp` and
`Directory.Build.rsp` in both the workspace and project directories; they also
prove that both tests in a failing xUnit project finish while its successor
never starts. The MSBuild CLI paths retain both switches unchanged.

Own-code resolution was verified **before implementation** by a temporary hosted
`$/Github/ci-runtime-probe` action with no workspace checkout: [run 42615634829](https://github.com/Hexalith/Hexalith.Builds/actions/runs/42615634829)
passed at `f2f0a08d8c38d34b390dc937659d2067c8a09cce` on runner 2.338.2.
`GITHUB_ACTION_PATH` resolved to that exact workflow commit's downloaded action.
The temporary probe files have been removed. Native `$/` works, so AD-9's pinned
own-code-checkout fallback is unnecessary. All new composites locate their
scripts through `GITHUB_ACTION_PATH`, independently of the module's Builds
gitlink. The first CI step reserves stage 1 for story 5-1's guard and commitlint;
Builds' own call runs no delivered-ref guard.

The executor also overrides xUnit's within-project `stopOnFail` setting so
project-level failure never cancels the remaining tests in that project. xUnit
is identified by evaluated `XunitTestProject` metadata. MTP uses its supported
`--stop-on-fail off` option; VSTest uses `xUnit.StopOnFail=false`. The v3
VSTest adapter's separate executable can reload its JSON configuration, so only
evaluated build-output runner configs are temporarily merged with
`stopOnFail=false`, then restored byte-for-byte in `finally`. Multi-targeted
outputs are handled individually; source, tracked and escaping configurations
are rejected. Other runner settings are retained, and other test frameworks
receive no xUnit options. Real SDK tests exercise both runner paths.
