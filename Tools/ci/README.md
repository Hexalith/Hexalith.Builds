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
script `.lock` files. Runtime scripts gain no third-party dependencies.

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
