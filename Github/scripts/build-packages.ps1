#!/usr/bin/env pwsh
# Builds every .NET library under ./src/libraries stamped with the release version.
# Invoked by semantic-release (@semantic-release/exec prepareCmd) during a release.
# Stable versions build in Release, pre-releases (version contains '-') build in Debug.
# Packages are produced on build via GeneratePackageOnBuild (Hexalith.Package.props).
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$configuration = if ($Version -notlike '*-*') { 'Release' } else { 'Debug' }
Write-Host "Building .NET libraries version $Version ($configuration configuration)"

$projects = Get-ChildItem -Path './src/libraries' -Recurse -Filter '*.csproj'
foreach ($project in $projects) {
    Write-Host "Building $($project.FullName)..."
    dotnet build $project.FullName `
        --configuration $configuration `
        -p:Version=$Version `
        -p:FileVersion=$Version
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $($project.FullName) (exit code $LASTEXITCODE)."
    }
}
