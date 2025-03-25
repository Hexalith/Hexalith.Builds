# Initialize the Hexalith.Builds Git submodule from the specified repository
git submodule init https://github.com/Hexalith/Hexalith.Builds.git

# Update the Hexalith.Builds submodule to the latest commit referenced in the parent repo
git submodule update Hexalith.Builds

# Checkout the main branch in the Hexalith.Builds submodule
git submodule foreach git checkout main

# Create symbolic links from parent repo to submodule for development configuration files
Write-Host "Creating symbolic links to configuration files..." -ForegroundColor Cyan

# Ensure target directories exist
if (-not (Test-Path ".github")) {
    New-Item -ItemType Directory -Path ".github" -Force
}

# Create the files if they don't exist in Hexalith.Builds
$files = @(
    @{Source = "Hexalith.Builds\.clinerules"; Target = ".clinerules"},
    @{Source = "Hexalith.Builds\.cursorrules"; Target = ".cursorrules"},
    @{Source = "Hexalith.Builds\.github\copilot-instructions.md"; Target = ".github\copilot-instructions.md"}
)

foreach ($file in $files) {
    if (Test-Path $file.Source) {
        # Create symlink from root to Hexalith.Builds (opposite of before)
        if (Test-Path $file.Target) {
            Remove-Item $file.Target -Force
        }
        New-Item -ItemType SymbolicLink -Path $file.Target -Target $file.Source -Force
        Write-Host "Created symbolic link: $($file.Target) -> $($file.Source)" -ForegroundColor Green
    } else {
        Write-Host "Warning: Source file $($file.Source) does not exist" -ForegroundColor Yellow
    }
}

Write-Host "Symbolic link creation process completed." -ForegroundColor Green
