#!/usr/bin/env pwsh
# Builds and packs every .NET library under ./src/libraries with the release version.
# Invoked by semantic-release (@semantic-release/exec prepareCmd) during a release.
# Every version builds in Release: a prerelease is distributed to consumers exactly
# like a stable one, so shipping unoptimized Debug binaries under a prerelease
# version would mean testing something other than what is published
# (ci-cd-standards.md, Build Configuration). The prerelease suffix comes from
# -p:Version, not from the configuration.
# Pack explicitly: ordinary builds do not produce NuGet package archives.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$configuration = 'Release'
Write-Host "Packing .NET libraries version $Version ($configuration configuration)"

$projects = Get-ChildItem -Path './src/libraries' -Recurse -Filter '*.csproj'
foreach ($project in $projects) {
    Write-Host "Packing $($project.FullName)..."
    dotnet pack $project.FullName `
        --configuration $configuration `
        -p:Version=$Version `
        -p:FileVersion=$Version
    if ($LASTEXITCODE -ne 0) {
        throw "Pack failed for $($project.FullName) (exit code $LASTEXITCODE)."
    }
}
