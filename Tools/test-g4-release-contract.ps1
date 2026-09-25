#!/usr/bin/env pwsh

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/build-release.yml') -Raw
if (-not $releaseWorkflow.Contains('id-token: write', [StringComparison]::Ordinal) -or
    -not $releaseWorkflow.Contains('uses: NuGet/login@v1.2.0', [StringComparison]::Ordinal) -or
    -not $releaseWorkflow.Contains('user: ${{ vars.NUGET_USER }}', [StringComparison]::Ordinal) -or
    -not $releaseWorkflow.Contains('nuget-api-key: ${{ steps.nuget_login.outputs.NUGET_API_KEY }}', [StringComparison]::Ordinal) -or
    $releaseWorkflow.Contains('nuget-api-key: ${{ secrets.NUGET_API_KEY }}', [StringComparison]::Ordinal)) {
    throw 'Stable release must use NuGet.org Trusted Publishing through GitHub OIDC, not a stored API key.'
}

$release = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'package.json') -Raw | ConvertFrom-Json).release
$branches = @($release.branches)
if ($branches.Count -ne 2 -or $branches[0] -cne 'main' -or
    [string]$branches[1].name -cne 'prerelease' -or $branches[1].prerelease -ne $true) {
    throw 'Semantic-release must retain main stable and protected prerelease channels.'
}

$execPlugins = @($release.plugins | Where-Object { $_ -is [array] -and $_[0] -ceq '@semantic-release/exec' })
if ($execPlugins.Count -ne 1) {
    throw 'Semantic-release must configure exactly one exec lifecycle.'
}
$hooks = $execPlugins[0][1]
if ([string]$hooks.verifyReleaseCmd -cne 'pwsh -NoProfile -File ./Tools/verify-g4-tool-release.ps1 -Version ${nextRelease.version}' -or
    [string]$hooks.publishCmd -cne 'pwsh -NoProfile -File ./Tools/publish-g4-tool-packages.ps1 -Version ${nextRelease.version}' -or
    $null -ne $hooks.PSObject.Properties['prepareCmd']) {
    throw 'G-4 qualification must run before tagging, and the publisher must consume the same actual version.'
}

$pluginNames = @($release.plugins | ForEach-Object { if ($_ -is [array]) { [string]$_[0] } else { [string]$_ } })
if ($pluginNames -contains '@semantic-release/changelog' -or $pluginNames -contains '@semantic-release/git' -or
    $pluginNames -notcontains '@semantic-release/release-notes-generator' -or
    $pluginNames -notcontains '@semantic-release/github') {
    throw 'Protected branches require generated release notes and GitHub releases without generated commits.'
}

$oldGitHubToken = $env:GITHUB_TOKEN
$oldNuGetKey = $env:NUGET_API_KEY
try {
    $env:GITHUB_TOKEN = $null
    $env:NUGET_API_KEY = $null
    foreach ($version in @('9.8.7', '9.8.7-prerelease.1')) {
        try {
            & (Join-Path $PSScriptRoot 'verify-g4-tool-release.ps1') -Version $version
            throw "Pre-tag release verification accepted missing credentials for '$version'."
        }
        catch {
            if (-not $_.Exception.Message.Contains('before creating a release tag', [StringComparison]::Ordinal)) {
                throw
            }
        }
    }
}
finally {
    $env:GITHUB_TOKEN = $oldGitHubToken
    $env:NUGET_API_KEY = $oldNuGetKey
}

[Console]::Out.WriteLine('G-4 release lifecycle and pre-tag credential contracts passed.')
