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

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Content,

        [Parameter(Mandatory = $true)]
        [string] $Unexpected
    )

    $script:checkCount++
    if ($Content.Contains($Unexpected, [StringComparison]::Ordinal)) {
        $script:failures.Add("$Name unexpectedly contains '$Unexpected'.")
    }
}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][bool] $Condition,
        [Parameter(Mandatory = $true)][string] $Failure
    )

    $script:checkCount++
    if (-not $Condition) {
        $script:failures.Add("$Name $Failure")
    }
}

function Get-NamedStepBlock {
    param(
        [Parameter(Mandatory = $true)][string] $Content,
        [Parameter(Mandatory = $true)][string] $StepName
    )

    $escapedName = [regex]::Escape($StepName)
    $match = [regex]::Match(
        $Content,
        "(?ms)^      - name: $escapedName\r?\n.*?(?=^      - name:|\z)"
    )
    return $(if ($match.Success) { $match.Value } else { '' })
}

function Test-MtpCoverageBlock {
    param([Parameter(Mandatory = $true)][string] $Block)

    if ([string]::IsNullOrWhiteSpace($Block)) {
        return $false
    }

    $emptyIndex = $Block.IndexOf('coverage_args=()', [StringComparison]::Ordinal)
    $guardIndex = $Block.IndexOf('if [ "$RUN_COVERAGE_GATE" = "true" ]; then', [StringComparison]::Ordinal)
    $coverageIndex = $Block.IndexOf(
        'coverage_args+=(--coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml)',
        [StringComparison]::Ordinal
    )
    $guardEndIndex = $Block.IndexOf("`n            fi", [StringComparison]::Ordinal)
    $invocationIndex = $Block.IndexOf('"${coverage_args[@]}"', [StringComparison]::Ordinal)

    return $Block.Contains('RUN_COVERAGE_GATE: ${{ inputs.run-coverage-gate }}', [StringComparison]::Ordinal) -and
        $emptyIndex -ge 0 -and
        $guardIndex -gt $emptyIndex -and
        $coverageIndex -gt $guardIndex -and
        $guardEndIndex -gt $coverageIndex -and
        $invocationIndex -gt $guardEndIndex -and
        ([regex]::Matches($Block, [regex]::Escape('coverage_args+=('))).Count -eq 1
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

Assert-NotContains -Name 'domain-ci.yml' -Content $ciWorkflow -Unexpected 'run-coverage-gate is not supported with microsoft-testing-platform'
$unitMtpBlock = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Unit tests (Tier 1, Microsoft.Testing.Platform)'
$integrationMtpBlock = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Integration tests (Tier 2, Microsoft.Testing.Platform)'
Assert-Condition -Name 'domain-ci.yml unit MTP coverage' -Condition (Test-MtpCoverageBlock -Block $unitMtpBlock) `
    -Failure 'does not preserve independent enabled and disabled coverage arguments.'
Assert-Condition -Name 'domain-ci.yml integration MTP coverage' -Condition (Test-MtpCoverageBlock -Block $integrationMtpBlock) `
    -Failure 'does not preserve independent enabled and disabled coverage arguments.'

$coverageAppend = 'coverage_args+=(--coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml)'
$disabledSeed = 'coverage_args=()'
Assert-Condition -Name 'domain-ci.yml unit enabled-coverage mutation contract' `
    -Condition (-not (Test-MtpCoverageBlock -Block $unitMtpBlock.Replace($coverageAppend, '# removed'))) `
    -Failure 'did not reject removal of the unit enabled-coverage arguments.'
Assert-Condition -Name 'domain-ci.yml unit disabled-coverage mutation contract' `
    -Condition (-not (Test-MtpCoverageBlock -Block $unitMtpBlock.Replace($disabledSeed, 'coverage_args=(--coverage)'))) `
    -Failure 'did not reject pre-populated unit disabled-coverage arguments.'
Assert-Condition -Name 'domain-ci.yml integration enabled-coverage mutation contract' `
    -Condition (-not (Test-MtpCoverageBlock -Block $integrationMtpBlock.Replace($coverageAppend, '# removed'))) `
    -Failure 'did not reject removal of the integration enabled-coverage arguments.'
Assert-Condition -Name 'domain-ci.yml integration disabled-coverage mutation contract' `
    -Condition (-not (Test-MtpCoverageBlock -Block $integrationMtpBlock.Replace($disabledSeed, 'coverage_args=(--coverage)'))) `
    -Failure 'did not reject pre-populated integration disabled-coverage arguments.'
Assert-Condition -Name 'domain-ci.yml unit/integration isolation' `
    -Condition ((Test-MtpCoverageBlock -Block $unitMtpBlock) -and (Test-MtpCoverageBlock -Block $integrationMtpBlock)) `
    -Failure 'does not validate the unit and integration coverage blocks separately.'
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
