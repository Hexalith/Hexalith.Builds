# Creates a symbolic link to .editorconfig in the parent directory of Hexalith.Builds
# This script must be run with administrator privileges on Windows

# Get the script's directory and navigate to the target locations
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$hexalithBuildsDir = Split-Path -Parent $scriptDir
$parentDir = Split-Path -Parent $hexalithBuildsDir

$sourceEditorConfig = Join-Path $hexalithBuildsDir ".editorconfig"
$targetSymlink = Join-Path $parentDir ".editorconfig"
$relativeTarget = "Hexalith.Builds\.editorconfig"

# Verify source file exists
if (-not (Test-Path $sourceEditorConfig)) {
    Write-Error "Source .editorconfig not found at: $sourceEditorConfig"
    exit 1
}

# Remove existing symlink if it exists
if (Test-Path $targetSymlink) {
    Write-Host "Removing existing .editorconfig at: $targetSymlink" -ForegroundColor Yellow
    Remove-Item $targetSymlink -Force
}

# Create the symbolic link with relative path
try {
    New-Item -ItemType SymbolicLink -Path $targetSymlink -Target $relativeTarget -ErrorAction Stop | Out-Null
    Write-Host "Successfully created symlink:" -ForegroundColor Green
    Write-Host "  Link: $targetSymlink" -ForegroundColor Cyan
    Write-Host "  Target: $relativeTarget" -ForegroundColor Cyan
}
catch {
    Write-Error "Failed to create symlink. Ensure you're running as Administrator. Error: $_"
    exit 1
}