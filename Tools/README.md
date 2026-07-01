# Hexalith.Builds Tools

Utility scripts for consuming repositories that use `Hexalith.Builds` as a
submodule.

## Available Tools

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
