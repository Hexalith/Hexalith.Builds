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
