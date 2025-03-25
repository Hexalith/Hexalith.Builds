# Check if running with administrator privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Error: This script requires administrator privileges to create symbolic links." -ForegroundColor Red
    Write-Host "Please run PowerShell as Administrator and try again." -ForegroundColor Red
    exit 1
}

Write-Host "Running with administrator privileges - proceeding with initialization..." -ForegroundColor Green

# Check if the submodule already exists
$submodulePath = "Hexalith.Builds"
$gitModulesPath = ".gitmodules"

if (Test-Path $gitModulesPath) {
    $modulesContent = Get-Content $gitModulesPath -Raw
    if ($modulesContent -match "Hexalith\.Builds") {
        Write-Host "Submodule already exists. Initializing..." -ForegroundColor Cyan
        git submodule init $submodulePath
    } else {
        Write-Host "Adding new submodule..." -ForegroundColor Cyan
        git submodule add https://github.com/Hexalith/Hexalith.Builds.git
    }
} else {
    Write-Host "Adding new submodule..." -ForegroundColor Cyan
    git submodule add https://github.com/Hexalith/Hexalith.Builds.git
    # Add the submodule directory to the list of safe directories
    git config --global --add safe.directory ./Hexalith.Builds
    # Add the directory to the list of safe directories
    git config --global --add safe.directory .
}

# Update the Hexalith.Builds submodule to the latest commit referenced in the parent repo
git submodule update $submodulePath

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
