#!/usr/bin/env pwsh

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/build-release.yml') -Raw
if (-not $releaseWorkflow.Contains('id-token: write', [StringComparison]::Ordinal) -or
    -not $releaseWorkflow.Contains('uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0', [StringComparison]::Ordinal) -or
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
    [string]$hooks.prepareCmd -cne 'pwsh -NoProfile -File ./Tools/prepare-g4-tool-release.ps1 -Version ${nextRelease.version}') {
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

$preTag = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'verify-g4-tool-release.ps1') -Raw
$publication = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'publish-g4-tool-packages.ps1') -Raw
if (-not $preTag.TrimEnd().EndsWith('Assert-CurrentReleaseSource -Qualification $inventory.qualification', [StringComparison]::Ordinal) -or
    $preTag.IndexOf('Assert-CurrentReleaseSource', [StringComparison]::Ordinal) -lt
        $preTag.IndexOf("'archive-g4-qualification-evidence.py'", [StringComparison]::Ordinal) -or
    $publication.IndexOf('Assert-CurrentReleaseSource -Qualification $qualification', [StringComparison]::Ordinal) -lt
        $publication.IndexOf('--kind release --verify', [StringComparison]::Ordinal) -or
    $publication.IndexOf('Assert-CurrentReleaseSource -Qualification $qualification', [StringComparison]::Ordinal) -gt
        $publication.IndexOf("Invoke-DotNet -Arguments @('nuget', 'push'", [StringComparison]::Ordinal)) {
    throw 'Live source and exact CI must be rechecked after qualification and immediately before package publication.'
}

# Execute the real wrappers with only their expensive/external dependencies
# controlled in a private tree. No production wrapper implementation is copied
# into a test function, and no fixture inventory earns publication credit.
$fixture = Join-Path ([IO.Path]::GetTempPath()) "g4-release-wrapper-$([Guid]::NewGuid().ToString('N'))"
$fixtureTools = Join-Path $fixture 'Tools'
$null = New-Item -ItemType Directory -Path $fixtureTools -Force
foreach ($name in @('verify-g4-tool-release.ps1', 'prepare-g4-tool-release.ps1', 'G4PackageQualification.functions.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixtureTools $name)
}
[IO.File]::WriteAllText((Join-Path $fixtureTools 'test-g4-tool-package-contracts.ps1'), @'
param($Version, $PackageDirectory, [switch]$RetainPackageDirectory, [switch]$RequireControls, $SourceCiRunId)
if ($Version -cne '9.8.7' -or -not $RetainPackageDirectory -or -not $RequireControls -or $SourceCiRunId -cne '123') {
    throw 'Actual-version qualification arguments or SourceCiRunId forwarding changed.'
}
$global:G4WrapperPhases.Add('qualify')
$directory = Join-Path (Join-Path $PSScriptRoot '..') $PackageDirectory
$null = New-Item -ItemType Directory -Path $directory -Force
@{ schema = 'hexalith.g4-tool-package-inventory.v1'; version = $Version; configuration = 'Release'; qualification = @{
    packageBuild = @{mode='official';result='passed'}; controls = @{mode='executed';result='passed'}; releaseEligible = $true
    sourceTree = @{revision=$env:GITHUB_SHA;clean=$true}
    sourceValidation = @{mode='ci';result='passed';ciRun=@{id='123';runAttempt=1;repository=$env:GITHUB_REPOSITORY;workflow='.github/workflows/ci.yml';sha=$env:GITHUB_SHA;branch='main';url='https://github.com/Hexalith/Hexalith.Builds/actions/runs/123'}}
}} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directory 'g4-tool-package-inventory.json')
'@)
$names = @('GITHUB_TOKEN','NUGET_API_KEY','GITHUB_REPOSITORY','GITHUB_REF','GITHUB_SHA','HEXALITH_G4_SOURCE_CI_RUN_ID')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$global:G4WrapperPhases = [Collections.Generic.List[string]]::new()
$global:G4WrapperGuardStatus = 0
$wrapperCases = 0
function global:gh {
    param($Command,$Endpoint)
    if ($Command -cne 'api' -or $Endpoint -cne 'repos/Hexalith/Hexalith.Builds/actions/runs/123') { throw 'Unexpected external API dependency.' }
    $global:LASTEXITCODE = 0
    @{id=123;run_attempt=1;repository=@{full_name=$env:GITHUB_REPOSITORY};path='.github/workflows/ci.yml';head_sha=$env:GITHUB_SHA;head_branch='main';event='push';status='completed';conclusion='success';html_url='https://github.com/Hexalith/Hexalith.Builds/actions/runs/123'} | ConvertTo-Json -Depth 4
}
function global:python3 {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
    if ([IO.Path]::GetFileName($Arguments[0]) -cne 'archive-g4-qualification-evidence.py') { throw 'Unexpected archive dependency.' }
    $global:G4WrapperPhases.Add($(if ($Arguments -ccontains '--verify') {'verify-archive'} else {'archive'}))
    $global:LASTEXITCODE = 0
}
function global:bash {
    param($ScriptPath)
    if ([IO.Path]::GetFileName($ScriptPath) -cne 'verify-g4-release-source.sh' -or $env:EXPECTED_CI_RUN_ID -cne '123' -or $env:EXPECTED_CI_RUN_ATTEMPT -cne '1') { throw 'Late wrapper check lost qualified source scope.' }
    $global:G4WrapperPhases.Add('guard')
    $global:LASTEXITCODE = $global:G4WrapperGuardStatus
}
try {
    $env:NUGET_API_KEY = 'controlled-fixture-key'
    $env:GITHUB_REPOSITORY = 'Hexalith/Hexalith.Builds'
    $env:GITHUB_REF = 'refs/heads/main'
    $env:GITHUB_SHA = '0123456789abcdef0123456789abcdef01234567'
    $env:HEXALITH_G4_SOURCE_CI_RUN_ID = '123'
    Push-Location $fixture
    try {
        foreach ($wrapper in @('verify-g4-tool-release.ps1','prepare-g4-tool-release.ps1')) {
            foreach ($status in @(0,1)) {
                $global:G4WrapperPhases.Clear()
                $global:G4WrapperGuardStatus = $status
                $refused = $false
                try { & (Join-Path $fixtureTools $wrapper) -Version '9.8.7' }
                catch {
                    if ($status -eq 0 -or -not $_.Exception.Message.Contains('changed before publication', [StringComparison]::Ordinal)) { throw }
                    $refused = $true
                }
                if ($refused -ne ($status -ne 0)) { throw 'Real wrapper did not propagate its late refusal.' }
                $expected = if ($wrapper.StartsWith('verify-', [StringComparison]::Ordinal)) { 'qualify,archive,guard' } else { 'verify-archive,guard' }
                if (($global:G4WrapperPhases -join ',') -cne $expected) { throw "Real wrapper order changed: $($global:G4WrapperPhases -join ',')" }
                $wrapperCases++
            }
        }
    }
    finally { Pop-Location }
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
    foreach ($name in @('gh','python3','bash')) { Remove-Item "Function:$name" -ErrorAction SilentlyContinue }
    Remove-Variable G4WrapperPhases,G4WrapperGuardStatus -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
[Console]::Out.WriteLine("G-4 real release wrapper controls passed: $wrapperCases scenarios.")

[Console]::Out.WriteLine('G-4 release lifecycle and pre-tag credential contracts passed.')
