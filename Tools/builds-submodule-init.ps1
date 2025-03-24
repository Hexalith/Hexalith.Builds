# Initialize the Hexalith.Builds Git submodule from the specified repository
git submodule init https://github.com/Hexalith/Hexalith.Builds.git

# Update the Hexalith.Builds submodule to the latest commit referenced in the parent repo
git submodule update Hexalith.Builds

# Checkout the main branch in the Hexalith.Builds submodule
git submodule foreach git checkout main

# Create symbolic links from parent repo to submodule for development configuration files
Write-Host "Creating symbolic links to configuration files..." -ForegroundColor Cyan

# Create symlinks for rules files
New-Item -ItemType SymbolicLink -Path "Hexalith.Builds\.clinerules" -Target ".clinerules" -Force
New-Item -ItemType SymbolicLink -Path "Hexalith.Builds\.cursorrules" -Target ".cursorrules" -Force
New-Item -ItemType SymbolicLink -Path "Hexalith.Builds\.github\copilot-instructions.md" -Target ".github\copilot-instructions.md" -Force

Write-Host "Symbolic links created successfully." -ForegroundColor Green
