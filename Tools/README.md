# Hexalith.Builds Tools

Utility scripts for consuming repositories that use `Hexalith.Builds` as a
submodule.

## Available Tools

### G-4 tool package qualification

`build-g4-tool-packages.ps1` builds both authorized .NET tools in Release,
packs exactly `Hexalith.Builds.Module.Cli` and
`Hexalith.Builds.Evidence.Cli`, and writes a SHA-256 inventory. It does not
reuse the shared consumer `Github/scripts/build-packages.ps1` behavior.

```powershell
.\Tools\build-g4-tool-packages.ps1 -Version 0.0.0-ci.1
```

`test-g4-tool-package-contracts.ps1` performs clean source and package-mode
qualification. It generates a temporary local-tool manifest pinned to the
provided version, restores only from the temporary local feed, and invokes the
positive and blocking-negative module/evidence controls.

```powershell
.\Tools\test-g4-tool-package-contracts.ps1 -Version 0.0.0-ci.1 -RequireControls
```

`publish-g4-tool-packages.ps1` verifies the Release inventory before
publication. A prerelease version (one containing `-`) targets
repository-configured GitHub Packages using `GITHUB_TOKEN`; a stable version
targets NuGet.org using `NUGET_API_KEY`. The script fails closed if the token,
package inventory, version, NuGet package, symbol package, or SHA-256 record
is missing.

```powershell
.\Tools\publish-g4-tool-packages.ps1 -Version 4.20.0
```

The version shown above is illustrative. Semantic-release supplies the actual
version; never create a consumer tool manifest with an unpublished version.

### validate-central-package-versions.ps1

Evaluates `Props/Directory.Packages.props` with MSBuild and rejects blank,
unresolved, tag-prefixed, or malformed package versions before release.

```powershell
.\Tools\validate-central-package-versions.ps1
```

Run the focused fixture suite with:

```powershell
.\Tools\test-central-package-version-validator.ps1
```

### test-domain-workflow-test-platforms.ps1

Checks that reusable domain CI/release workflows retain their backward-compatible
VSTest default and route Microsoft.Testing.Platform callers to MTP-native TRX and
trait-filter options without leaking VSTest-only arguments.

### builds-submodule-init.ps1

Adds or initializes the `Hexalith.Builds` Git submodule in a parent repository
under `references/Hexalith.Builds`.

#### Purpose

The script automates:

1. Checking that PowerShell is running with administrator privileges.
2. Adding the `references/Hexalith.Builds` submodule when it is not already
   declared.
3. Initializing the existing `references/Hexalith.Builds` submodule when it is
   declared.
4. Updating the submodule.
5. Checking out the `main` branch in the initialized build submodule.

#### Requirements

- Administrator privileges on Windows.
- Git available in `PATH`.
- PowerShell 5.0 or later.

#### Usage

Run the script from the root directory of your repository:

```powershell
.\references\Hexalith.Builds\Tools\builds-submodule-init.ps1
```

If the submodule has not been added yet, you can download the script and run it:

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/Hexalith/Hexalith.Builds/main/Tools/builds-submodule-init.ps1" -OutFile "builds-submodule-init.ps1"
.\builds-submodule-init.ps1
```

#### Notes

- The script does not use recursive or remote submodule updates.
- When adding a new submodule, it adds the repository root and
  `./references/Hexalith.Builds` to Git's global safe directory list.
- The script does not create symlinks. Use `editorconfig-symlink.ps1` for the
  shared `.editorconfig` link.

### editorconfig-symlink.ps1

Creates a `.editorconfig` symbolic link in the parent repository that points to
`references/Hexalith.Builds/.editorconfig`.

#### Purpose

The script lets a consuming repository reuse the shared editor and analyzer
style settings from the `Hexalith.Builds` submodule.

#### Requirements

- Administrator privileges on Windows.
- `references/Hexalith.Builds/.editorconfig` must exist.

#### Usage

Run the script from the `references/Hexalith.Builds` submodule:

```powershell
.\Tools\editorconfig-symlink.ps1
```

#### What the Script Does

1. Resolves the `references/Hexalith.Builds` directory and its parent
   repository.
2. Verifies that `references/Hexalith.Builds/.editorconfig` exists.
3. Removes any existing parent `.editorconfig` path.
4. Creates a symbolic link from the parent `.editorconfig` to
   `references\Hexalith.Builds\.editorconfig`.

## Troubleshooting

- `Error: This script requires administrator privileges`: Run PowerShell as
  Administrator and try again.
- Symbolic link creation fails: Confirm administrator privileges and Windows
  symlink support.
- Git submodule commands fail: Confirm Git is installed and that the repository
  root is the current directory.
