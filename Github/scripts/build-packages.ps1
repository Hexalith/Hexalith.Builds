#!/usr/bin/env pwsh
# Builds every .NET library under ./src/libraries stamped with the release version.
# Invoked by semantic-release (@semantic-release/exec prepareCmd) during a release.
# Every version builds in Release: a prerelease is distributed to consumers exactly
# like a stable one, so shipping unoptimized Debug binaries under a prerelease
# version would mean testing something other than what is published
# (ci-cd-standards.md, Build Configuration). The prerelease suffix comes from
# -p:Version, not from the configuration.
# Packages are produced on build via GeneratePackageOnBuild (Hexalith.Package.props).
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$configuration = 'Release'
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
