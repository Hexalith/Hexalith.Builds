[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ciWorkflowPath = Join-Path $PSScriptRoot '../.github/workflows/domain-ci.yml'
$releaseWorkflowPath = Join-Path $PSScriptRoot '../.github/workflows/domain-release.yml'
$buildReleaseWorkflowPath = Join-Path $PSScriptRoot '../.github/workflows/build-release.yml'
$failures = [System.Collections.Generic.List[string]]::new()
$checkCount = 0

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Content,

        [Parameter(Mandatory = $true)]
        [string] $Expected
    )

    $script:checkCount++
    if (-not $Content.Contains($Expected, [StringComparison]::Ordinal)) {
        $script:failures.Add("$Name is missing '$Expected'.")
    }
}

function Assert-MtpBlocksExcludeVstestOptions {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Content
    )

    $script:checkCount++
    $blocks = [regex]::Matches(
        $Content,
        '(?ms)^\s{6}- name: [^\r\n]*Microsoft\.Testing\.Platform[^\r\n]*.*?(?=^\s{6}- name:|\z)'
    )
    if ($blocks.Count -eq 0) {
        $script:failures.Add("$Name has no Microsoft.Testing.Platform steps.")
        return
    }

    foreach ($block in $blocks) {
        if ($block.Value -match '(?m)^\s+--(?:logger|collect)') {
            $script:failures.Add("$Name passes a VSTest-only option inside a Microsoft.Testing.Platform step.")
            return
        }
    }
}

$ciWorkflow = Get-Content -LiteralPath $ciWorkflowPath -Raw
$releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw
$buildReleaseWorkflow = Get-Content -LiteralPath $buildReleaseWorkflowPath -Raw

foreach ($workflow in @(
    [pscustomobject] @{ Name = 'domain-ci.yml'; Content = $ciWorkflow },
    [pscustomobject] @{ Name = 'domain-release.yml'; Content = $releaseWorkflow }
)) {
    Assert-Contains -Name $workflow.Name -Content $workflow.Content -Expected "test-platform:"
    Assert-Contains -Name $workflow.Name -Content $workflow.Content -Expected "default: 'vstest'"
    Assert-Contains -Name $workflow.Name -Content $workflow.Content -Expected "inputs.test-platform == 'microsoft-testing-platform'"
    Assert-Contains -Name $workflow.Name -Content $workflow.Content -Expected '--report-xunit-trx'
    Assert-Contains -Name $workflow.Name -Content $workflow.Content -Expected '--report-xunit-trx-filename'
    Assert-Contains -Name $workflow.Name -Content $workflow.Content -Expected '--logger "trx;LogFileName='
    Assert-Contains -Name $workflow.Name -Content $workflow.Content -Expected '--collect:"XPlat Code Coverage"'
    Assert-MtpBlocksExcludeVstestOptions -Name $workflow.Name -Content $workflow.Content
}

Assert-Contains -Name 'domain-ci.yml' -Content $ciWorkflow -Expected 'run-coverage-gate is not supported with microsoft-testing-platform'
Assert-Contains -Name 'domain-ci.yml' -Content $ciWorkflow -Expected '--filter-not-trait'
Assert-Contains -Name 'domain-ci.yml' -Content $ciWorkflow -Expected '--filter-trait'
Assert-Contains -Name 'build-release.yml' -Content $buildReleaseWorkflow -Expected 'test-domain-workflow-test-platforms.ps1'

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Domain workflow test-platform checks failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine("Domain workflow test-platform checks passed: $checkCount assertions.")
