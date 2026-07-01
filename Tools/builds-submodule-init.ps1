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
$submodulePath = "references/Hexalith.Builds"
$legacySubmodulePath = "Hexalith.Builds"
$submoduleUrl = "https://github.com/Hexalith/Hexalith.Builds.git"
$gitModulesPath = ".gitmodules"

function Add-BuildsSubmodule {
    $submoduleParentPath = Split-Path -Parent $submodulePath
    if (-not (Test-Path $submoduleParentPath)) {
        New-Item -ItemType Directory -Path $submoduleParentPath | Out-Null
    }

    git submodule add $submoduleUrl $submodulePath
    # Add the submodule directory to the list of safe directories
    git config --global --add safe.directory ./$submodulePath
    # Add the directory to the list of safe directories
    git config --global --add safe.directory .
}

function Test-SubmodulePath {
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $gitModulesPath)) {
        return $false
    }

    $submodulePaths = git config -f $gitModulesPath --get-regexp "submodule\..*\.path" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $submodulePaths) {
        return $false
    }

    foreach ($line in $submodulePaths) {
        $parts = $line -split "\s+", 2
        if ($parts.Count -eq 2 -and $parts[1] -eq $Path) {
            return $true
        }
    }

    return $false
}

if (Test-Path $gitModulesPath) {
    if (Test-SubmodulePath $submodulePath) {
        Write-Host "Submodule already exists. Initializing..." -ForegroundColor Cyan
        git submodule init $submodulePath
    } elseif (Test-SubmodulePath $legacySubmodulePath) {
        Write-Host "Error: Hexalith.Builds is declared at the repository root." -ForegroundColor Red
        Write-Host "Move the submodule to '$submodulePath' before running this script." -ForegroundColor Red
        exit 1
    } else {
        Write-Host "Adding new submodule..." -ForegroundColor Cyan
        Add-BuildsSubmodule
    }
} else {
    Write-Host "Adding new submodule..." -ForegroundColor Cyan
    Add-BuildsSubmodule
}

# Update the Hexalith.Builds submodule to the latest commit referenced in the parent repo
git submodule update $submodulePath

# Checkout the main branch in the Hexalith.Builds submodule
git -C $submodulePath checkout main

