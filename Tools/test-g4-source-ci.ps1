#!/usr/bin/env pwsh

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1')

$revision = '0123456789abcdef0123456789abcdef01234567'
$originalRepository = $env:GITHUB_REPOSITORY
$originalRef = $env:GITHUB_REF
$originalSha = $env:GITHUB_SHA
$releaseEnvironmentNames = @('GITHUB_JOB', 'GITHUB_EVENT_NAME', 'GITHUB_WORKFLOW_REF', 'GITHUB_WORKFLOW_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT')
$originalReleaseEnvironment = @{}
foreach ($name in $releaseEnvironmentNames) {
    $originalReleaseEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$scenarioCount = 0
$apiExitCode = 0
$apiErrorOutput = 'unclassified failure'
$apiCalls = 0
$liveGuardCalls = 0
$liveGuardExitCode = 0
$expectedCiProof = $true
$inventoryPath = Join-Path ([IO.Path]::GetTempPath()) "g4-source-ci-$([Guid]::NewGuid().ToString('N')).json"

function gh {
    param([string] $Command, [string] $Endpoint)
    if ($Command -cne 'api' -or $Endpoint -cne 'repos/Hexalith/Hexalith.Builds/actions/runs/123') {
        throw "Unexpected GitHub request: $Command $Endpoint"
    }
    $script:apiCalls++
    $global:LASTEXITCODE = $script:apiExitCode
    if ($script:apiExitCode -ne 0) { $script:apiErrorOutput; return }
    $script:run | ConvertTo-Json -Depth 5 -Compress
}

function bash {
    param([string] $ScriptPath)
    $proofMatches = if ($script:expectedCiProof) {
        $env:EXPECTED_CI_RUN_ID -ceq '123' -and $env:EXPECTED_CI_RUN_ATTEMPT -ceq '1'
    } else {
        [string]::IsNullOrEmpty($env:EXPECTED_CI_RUN_ID) -and [string]::IsNullOrEmpty($env:EXPECTED_CI_RUN_ATTEMPT)
    }
    if ($ScriptPath -cne (Join-Path $PSScriptRoot 'verify-g4-release-source.sh') -or
        $env:REPOSITORY -cne $env:GITHUB_REPOSITORY -or $env:DISPATCH_REF -cne $env:GITHUB_REF -or
        $env:DISPATCH_SHA -cne $script:revision -or -not $proofMatches -or -not [string]::IsNullOrEmpty($env:GITHUB_OUTPUT)) {
        throw 'Late guard did not receive the exact qualified source scope.'
    }
    $script:liveGuardCalls++
    $global:LASTEXITCODE = $script:liveGuardExitCode
}

function Assert-Rejected {
    param([string] $Name, [scriptblock] $Action, [string] $Message)
    $script:scenarioCount++
    try {
        & $Action
    }
    catch {
        if (-not $_.Exception.Message.Contains($Message, [StringComparison]::Ordinal)) {
            throw "$Name failed unexpectedly: $($_.Exception.Message)"
        }
        return
    }
    throw "$Name was accepted."
}

try {
    $env:GITHUB_REPOSITORY = 'Hexalith/Hexalith.Builds'
    $env:GITHUB_SHA = $revision
    foreach ($branch in @('main', 'prerelease')) {
        $env:GITHUB_REF = "refs/heads/$branch"
        $run = [ordered] @{
            id = 123
            run_attempt = 1
            repository = @{ full_name = 'Hexalith/Hexalith.Builds' }
            path = '.github/workflows/ci.yml'
            head_sha = $revision
            head_branch = $branch
            event = 'push'
            status = 'completed'
            conclusion = 'success'
            html_url = 'https://github.com/Hexalith/Hexalith.Builds/actions/runs/123'
        }
        $proof = Get-VerifiedSourceCiRun -RunId '123' -Revision $revision
        $qualification = [pscustomobject] @{
            sourceValidation = [pscustomobject] @{ mode = 'ci'; result = 'passed'; ciRun = [pscustomobject]$proof }
            sourceTree = [pscustomobject] @{ clean = $true; revision = $revision }
        }
        $before = $apiCalls
        Assert-SourceValidation -Qualification $qualification
        if ($apiCalls -ne $before + 1) { throw 'Source CI evidence was accepted without querying GitHub again.' }
        $scenarioCount++

        $before = $liveGuardCalls
        $beforeApi = $apiCalls
        $previousOutput = $env:GITHUB_OUTPUT
        $lateEnvironment = @{}
        foreach ($name in @('REPOSITORY','DISPATCH_REF','DISPATCH_SHA','EXPECTED_CI_RUN_ID','EXPECTED_CI_RUN_ATTEMPT')) {
            $lateEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
        }
        Assert-CurrentReleaseSource -Qualification $qualification
        if ($liveGuardCalls -ne $before + 1 -or $apiCalls -ne $beforeApi + 1 -or $env:GITHUB_OUTPUT -cne $previousOutput) {
            throw 'Late release check must revalidate the recorded CI attempt and live source without changing workflow outputs.'
        }
        $scenarioCount++
        $liveGuardExitCode = 1
        Assert-Rejected -Name 'Source advanced during qualification' -Message 'changed before publication' -Action {
            Assert-CurrentReleaseSource -Qualification $qualification
        }
        $liveGuardExitCode = 0
        foreach ($name in $lateEnvironment.Keys) {
            if ([Environment]::GetEnvironmentVariable($name) -cne $lateEnvironment[$name]) { throw "Late guard changed caller environment '$name'." }
        }
        $env:GITHUB_SHA = '0000000000000000000000000000000000000000'
        Assert-Rejected -Name 'Qualified revision differs from workflow revision' -Message 'qualified workflow revision' -Action {
            Assert-CurrentReleaseSource -Qualification $qualification
        }
        $env:GITHUB_SHA = $revision

        $qualification | Add-Member -NotePropertyName packageBuild -NotePropertyValue @{ mode = 'official'; result = 'passed' }
        $qualification | Add-Member -NotePropertyName controls -NotePropertyValue @{ mode = 'executed'; result = 'passed' }
        $qualification | Add-Member -NotePropertyName releaseEligible -NotePropertyValue $true
        @{
            schema = 'hexalith.g4-tool-package-inventory.v1'; version = '9.8.7'
            configuration = 'Release'; qualification = $qualification
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $inventoryPath -Encoding utf8
        $null = Assert-ReleaseEligibleInventory -InventoryPath $inventoryPath -Version '9.8.7'
        $scenarioCount++

        foreach ($field in @('id', 'run_attempt', 'path', 'head_sha', 'head_branch', 'event', 'status', 'conclusion', 'html_url')) {
            $original = $run[$field]
            $run[$field] = switch ($field) {
                'id' { 456 }
                'run_attempt' { 0 }
                'path' { '.github/workflows/other.yml' }
                'head_sha' { '0000000000000000000000000000000000000000' }
                'head_branch' { 'feature' }
                'event' { 'pull_request' }
                'status' { 'in_progress' }
                'conclusion' { 'failure' }
                'html_url' { 'https://example.invalid/run/123' }
            }
            Assert-Rejected -Name "CI $field mismatch on $branch" -Message 'is not successful push CI' -Action {
                Assert-SourceValidation -Qualification $qualification
            }
            $run[$field] = $original
        }
        $run.repository.full_name = 'Other/Repository'
        Assert-Rejected -Name 'CI belongs to a different repository' -Message 'is not successful push CI' -Action {
            Assert-SourceValidation -Qualification $qualification
        }
        $run.repository.full_name = 'Hexalith/Hexalith.Builds'

        $run.run_attempt = 2
        Assert-Rejected -Name 'A rerun replaced the recorded attempt' -Message "field 'runAttempt'" -Action {
            $null = Assert-ReleaseEligibleInventory -InventoryPath $inventoryPath -Version '9.8.7'
        }
        $run.run_attempt = 1
        foreach ($field in @('repository', 'workflow', 'sha', 'branch', 'url')) {
            $original = $qualification.sourceValidation.ciRun.$field
            $qualification.sourceValidation.ciRun.$field = 'forged'
            Assert-Rejected -Name "Forged CI proof $field" -Message "field '$field'" -Action {
                Assert-SourceValidation -Qualification $qualification
            }
            $qualification.sourceValidation.ciRun.$field = $original
        }
        $apiExitCode = 1
        Assert-Rejected -Name 'GitHub verification is unavailable' -Message 'Could not verify source CI run' -Action {
            Assert-SourceValidation -Qualification $qualification
        }
        foreach ($case in @(
            @{ text = 'HTTP 403'; category = 'authorization' }, @{ text = 'HTTP 404'; category = 'not-found' },
            @{ text = 'HTTP 429'; category = 'rate-limit' }, @{ text = 'HTTP 503'; category = 'server' },
            @{ text = 'connection timed out'; category = 'transport' }, @{ text = 'opaque'; category = 'unclassified' }
        )) {
            $apiErrorOutput = "$($case.text) Authorization: Bearer fixture-sensitive-token"
            $scenarioCount++
            try {
                $null = Get-VerifiedSourceCiRun -RunId '123' -Revision $revision
                throw 'Failed API was accepted.'
            }
            catch {
                $message = $_.Exception.Message
                if (-not $message.Contains("exit 1; category $($case.category)", [StringComparison]::Ordinal) -or
                    $message.Contains('fixture-sensitive-token', [StringComparison]::Ordinal) -or
                    $message.Contains('Bearer', [StringComparison]::Ordinal) -or $message.Length -gt 256) { throw }
            }
        }
        $apiExitCode = 0

        foreach ($mode in @('skipped', 'unknown')) {
            $qualification.sourceValidation.mode = $mode
            Assert-Rejected -Name "Source validation $mode" -Message 'incomplete or bypassed' -Action {
                Assert-SourceValidation -Qualification $qualification
            }
        }
        $qualification.sourceValidation.mode = 'executed'
        $before = $apiCalls
        $beforeGuard = $liveGuardCalls
        Assert-CurrentReleaseSource -Qualification $qualification
        if ($apiCalls -ne $before -or $liveGuardCalls -ne $beforeGuard) { throw 'Locally executed source validation unexpectedly required GitHub.' }
        $scenarioCount++

        # Explicit release-context doubles test the production legacy path;
        # ordinary local executed qualification above remains fully offline.
        $expectedCiProof = $false
        $env:GITHUB_JOB = 'release'
        $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
        $env:GITHUB_WORKFLOW_REF = "Hexalith/Hexalith.Builds/.github/workflows/build-release.yml@refs/heads/$branch"
        $env:GITHUB_WORKFLOW_SHA = $revision
        $env:GITHUB_RUN_ID = '789'
        $env:GITHUB_RUN_ATTEMPT = '1'
        Assert-CurrentReleaseSource -Qualification $qualification
        if ($apiCalls -ne $before -or $liveGuardCalls -ne $beforeGuard + 1) {
            throw 'A production executed-mode release omitted its final live source check.'
        }
        $scenarioCount++
        $liveGuardExitCode = 1
        Assert-Rejected -Name 'Production legacy source advanced after qualification' -Message 'changed before publication' -Action {
            Assert-CurrentReleaseSource -Qualification $qualification
        }
        $liveGuardExitCode = 0
        foreach ($field in @('GITHUB_EVENT_NAME', 'GITHUB_WORKFLOW_REF', 'GITHUB_WORKFLOW_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT')) {
            $value = [Environment]::GetEnvironmentVariable($field)
            [Environment]::SetEnvironmentVariable($field, 'foreign')
            Assert-Rejected -Name "Foreign release scope $field" -Message 'complete protected release context' -Action {
                Assert-CurrentReleaseSource -Qualification $qualification
            }
            [Environment]::SetEnvironmentVariable($field, $value)
        }
        foreach ($name in $releaseEnvironmentNames) {
            [Environment]::SetEnvironmentVariable($name, $originalReleaseEnvironment[$name])
        }
        $expectedCiProof = $true
    }

    Assert-Rejected -Name 'Invalid CI run ID' -Message 'requires a valid run ID' -Action {
        $null = Get-VerifiedSourceCiRun -RunId '../other' -Revision $revision
    }
    $env:GITHUB_REF = 'refs/heads/feature'
    Assert-Rejected -Name 'Unsupported release branch' -Message 'requires a valid run ID' -Action {
        $null = Get-VerifiedSourceCiRun -RunId '123' -Revision $revision
    }
    Assert-Rejected -Name 'CI evidence cannot bypass skipped validation' -Message 'cannot be combined' -Action {
        & (Join-Path $PSScriptRoot 'test-g4-tool-package-contracts.ps1') -Version '9.8.7' -SourceCiRunId '123' -SkipSourceValidation
    }
}
finally {
    $env:GITHUB_REPOSITORY = $originalRepository
    $env:GITHUB_REF = $originalRef
    $env:GITHUB_SHA = $originalSha
    foreach ($name in $releaseEnvironmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalReleaseEnvironment[$name])
    }
    Remove-Item -LiteralPath $inventoryPath -Force -ErrorAction SilentlyContinue
}

Write-Host "G-4 source CI verification passed: $scenarioCount scenarios."
