# Hexalith.Builds Tools

This directory contains utility scripts and tools for managing and working with the Hexalith.Builds repository.

## Available Tools

### builds-submodule-init.ps1

A PowerShell script that initializes and configures the Hexalith.Builds Git submodule in a parent repository.

#### Purpose

This script automates the process of:
1. Adding or initializing the Hexalith.Builds Git submodule
2. Updating the submodule to the latest commit
3. Checking out the main branch in the submodule
4. Creating symbolic links from the parent repository to configuration files in the submodule

#### Requirements

- **Administrator Privileges**: Required for creating symbolic links
- **Git**: Must be installed and available in the PATH
- **PowerShell**: Version 5.0 or higher recommended

#### Usage

Run the script from the root directory of your repository:

```powershell
# From your repository root:
.\Hexalith.Builds\Tools\builds-submodule-init.ps1
```

If you haven't yet added the submodule, you can download the script directly and run it:

```powershell
# Download and run the script:
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/Hexalith/Hexalith.Builds/main/Tools/builds-submodule-init.ps1" -OutFile "builds-submodule-init.ps1"
.\builds-submodule-init.ps1
```

#### What the Script Does

1. **Checks for Administrator Privileges**
   - Verifies if the script is running with administrator privileges
   - Exits with an error message if not running as administrator

2. **Manages the Git Submodule**
   - Checks if the Hexalith.Builds submodule already exists
   - If it exists, initializes it
   - If it doesn't exist, adds it from the GitHub repository
   - Adds the submodule directory to Git's list of safe directories

3. **Updates the Submodule**
   - Updates the Hexalith.Builds submodule to the latest commit referenced in the parent repository
   - Checks out the main branch in the submodule

4. **Creates Symbolic Links**
   - Creates symbolic links from the parent repository to configuration files in the submodule
   - Links the following files:
     - `.clinerules`: Rules for the Cline AI assistant
     - `.cursorrules`: Rules for the Cursor AI assistant
     - `.github\copilot-instructions.md`: Instructions for GitHub Copilot

#### Expected Outcome

After running the script successfully:
- The Hexalith.Builds submodule will be properly initialized and updated
- Symbolic links will be created to the configuration files
- The parent repository will be configured to use the standardized build properties and settings from Hexalith.Builds

#### Troubleshooting

- **"Error: This script requires administrator privileges"**: Run PowerShell as Administrator and try again
- **Symbolic link creation fails**: Ensure you're running as Administrator and that Windows has symbolic link creation enabled
- **Git submodule commands fail**: Ensure Git is installed and properly configured