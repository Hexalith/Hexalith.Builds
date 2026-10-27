# Prepare a module build

This opt-in composite restores the caller's exact local module CLI manifest and
runs `prepare-build --mode package` without starting resources. Supply a
repository-relative v2 `manifest`. Install the pinned .NET SDK and initialize the
root-declared identity dependencies first. The helper needs Python 3.9 or later.

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
