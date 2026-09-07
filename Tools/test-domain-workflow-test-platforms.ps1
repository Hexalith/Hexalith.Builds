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
        [AllowEmptyString()]
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
        [AllowEmptyString()]
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

function Get-StepRunBody {
    param(
        [Parameter(Mandatory = $true)][string] $Block,
        [Parameter(Mandatory = $true)][string] $StepName
    )

    $match = [regex]::Match($Block, '(?ms)^        run: \|\r?\n(?<Body>.*)$')
    if (-not $match.Success) {
        $script:failures.Add("$StepName does not have an extractable literal run block.")
        return ''
    }

    return [regex]::Replace($match.Groups['Body'].Value, '(?m)^          ', '')
}

function Invoke-WorkflowBashBody {
    param(
        [Parameter(Mandatory = $true)][string] $Body,
        [Parameter(Mandatory = $true)][hashtable] $Environment
    )

    $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-domain-ci-$([guid]::NewGuid().ToString('N'))"
    $scriptPath = Join-Path $temporaryDirectory 'step.sh'
    $summaryPath = Join-Path $temporaryDirectory 'summary.md'
    $outputPath = Join-Path $temporaryDirectory 'output.txt'
    $invocationsPath = Join-Path $temporaryDirectory 'dotnet-invocations.txt'

    try {
        $null = New-Item -ItemType Directory -Path $temporaryDirectory
        $shim = @'
dotnet() {
  printf '%s\n' "$*" >> "$DOTNET_INVOCATIONS"
  project="$2"
  result_directory=''
  trx_filename=''
  arguments=("$@")
  for ((index = 0; index < ${#arguments[@]}; index++)); do
    case "${arguments[index]}" in
      --results-directory) result_directory="${arguments[index + 1]}" ;;
      --logger) trx_filename="${arguments[index + 1]#trx;LogFileName=}" ;;
      --report-xunit-trx-filename) trx_filename="${arguments[index + 1]}" ;;
    esac
  done
  case "$project" in
    *infra-first*) return 42 ;;
  esac
  if [ -n "$result_directory" ] && [ -n "$trx_filename" ]; then
    mkdir -p "$result_directory"
    : > "$result_directory/$trx_filename"
  fi
  case "$project" in
    *fail-first*) return 17 ;;
    *fail-second*) return 23 ;;
    *) return 0 ;;
  esac
}

'@
        Set-Content -LiteralPath $scriptPath -Value ($shim + $Body) -NoNewline

        $processStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $processStartInfo.FileName = 'bash'
        $processStartInfo.ArgumentList.Add($scriptPath)
        $processStartInfo.UseShellExecute = $false
        $processStartInfo.RedirectStandardOutput = $true
        $processStartInfo.RedirectStandardError = $true
        $processStartInfo.WorkingDirectory = $temporaryDirectory
        $processStartInfo.Environment['GITHUB_STEP_SUMMARY'] = $summaryPath
        $processStartInfo.Environment['GITHUB_OUTPUT'] = $outputPath
        $processStartInfo.Environment['DOTNET_INVOCATIONS'] = $invocationsPath
        foreach ($entry in $Environment.GetEnumerator()) {
            $processStartInfo.Environment[$entry.Key] = [string] $entry.Value
        }

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $processStartInfo
        $null = $process.Start()
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()

        return [pscustomobject] @{
            ExitCode    = $process.ExitCode
            Output      = $standardOutputTask.Result + $standardErrorTask.Result
            Summary     = $(if (Test-Path -LiteralPath $summaryPath) { Get-Content -LiteralPath $summaryPath -Raw } else { '' })
            StepOutput  = $(if (Test-Path -LiteralPath $outputPath) { Get-Content -LiteralPath $outputPath -Raw } else { '' })
            Invocations = $(if (Test-Path -LiteralPath $invocationsPath) { Get-Content -LiteralPath $invocationsPath -Raw } else { '' })
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
        }
    }
}

function Assert-ShardAggregationRuntime {
    param(
        [Parameter(Mandatory = $true)][string] $Workflow,
        [Parameter(Mandatory = $true)][string] $StepName,
        [Parameter(Mandatory = $true)][string] $StepId,
        [Parameter(Mandatory = $true)][string] $ProjectsVariable,
        [Parameter(Mandatory = $true)][string] $ExpectedCondition,
        [Parameter(Mandatory = $true)][string] $ExpectedEnvironmentMapping,
        [Parameter(Mandatory = $true)][string] $SummaryHeading,
        [Parameter(Mandatory = $true)][string[]] $ExpectedInvocationFragments,
        [Parameter(Mandatory = $true)][bool] $UsesCoverageSwitch
    )

    $block = Get-NamedStepBlock -Content $Workflow -StepName $StepName
    if ([string]::IsNullOrWhiteSpace($block)) {
        $script:failures.Add("domain-ci.yml is missing the $StepName step.")
        return
    }

    Assert-Contains -Name $StepName -Content $block -Expected "id: $StepId"
    Assert-Contains -Name $StepName -Content $block -Expected $ExpectedCondition
    Assert-Contains -Name $StepName -Content $block -Expected $ExpectedEnvironmentMapping
    Assert-Contains -Name $StepName -Content $block -Expected 'if dotnet test'
    Assert-Contains -Name $StepName -Content $block -Expected 'if [ ! -f "$result_file" ]; then'
    Assert-Contains -Name $StepName -Content $block -Expected '::error title=Test shard failed::'
    Assert-Contains -Name $StepName -Content $block -Expected '>> "$GITHUB_STEP_SUMMARY"'
    Assert-Contains -Name $StepName -Content $block -Expected 'printf ''failure-count=%d\n'' "$failures"'

    $body = Get-StepRunBody -Block $block -StepName $StepName
    if ([string]::IsNullOrWhiteSpace($body)) {
        return
    }

    $failureEnvironment = @{
        $ProjectsVariable = "tests/fail-first/Fail.First.csproj`ntests/fail-second/Fail.Second.csproj`ntests/pass-later/Pass.Later.csproj"
    }
    if ($UsesCoverageSwitch) {
        $failureEnvironment.RUN_COVERAGE_GATE = 'true'
    }

    $failureResult = Invoke-WorkflowBashBody -Body $body -Environment $failureEnvironment
    $failureInvocations = @($failureResult.Invocations -split '\r?\n' | Where-Object { $_ })
    Assert-Condition -Name "$StepName failure runtime" -Condition ($failureResult.ExitCode -eq 0) `
        -Failure "returned $($failureResult.ExitCode) instead of deferring the failure."
    Assert-Condition -Name "$StepName continuation runtime" -Condition ($failureInvocations.Count -eq 3) `
        -Failure "ran $($failureInvocations.Count) projects instead of all three configured projects."
    Assert-Contains -Name "$StepName failure summary" -Content $failureResult.Summary -Expected $SummaryHeading
    $failureRows = @($failureResult.Summary -split '\r?\n' | Where-Object { $_.StartsWith('| tests/', [StringComparison]::Ordinal) })
    $expectedFailureRows = @(
        '| tests/fail-first/Fail.First.csproj | FAIL | 17 |',
        '| tests/fail-second/Fail.Second.csproj | FAIL | 23 |',
        '| tests/pass-later/Pass.Later.csproj | PASS | 0 |'
    )
    Assert-Condition -Name "$StepName failure summary rows" `
        -Condition (($failureRows -join "`n") -ceq ($expectedFailureRows -join "`n")) `
        -Failure 'does not contain one exact, ordered result row per configured project.'
    Assert-Condition -Name "$StepName failure output" `
        -Condition ([regex]::IsMatch($failureResult.StepOutput, '\Afailure-count=2\r?\n\z')) `
        -Failure 'does not contain exactly one failure-count=2 assignment.'
    Assert-Contains -Name "$StepName failure annotation" -Content $failureResult.Output -Expected '::error title=Test shard failed::tests/fail-first/Fail.First.csproj exited with code 17.'
    Assert-Contains -Name "$StepName failure annotation" -Content $failureResult.Output -Expected '::error title=Test shard failed::tests/fail-second/Fail.Second.csproj exited with code 23.'
    foreach ($fragment in $ExpectedInvocationFragments) {
        Assert-Contains -Name "$StepName invocation" -Content $failureResult.Invocations -Expected $fragment
    }

    $passingEnvironment = @{
        $ProjectsVariable = "tests/pass-first/Pass.First.csproj`ntests/pass-later/Pass.Later.csproj`ntests/pass-special/Pass | 50% ``Special``.csproj"
    }
    if ($UsesCoverageSwitch) {
        $passingEnvironment.RUN_COVERAGE_GATE = 'false'
    }

    $passingResult = Invoke-WorkflowBashBody -Body $body -Environment $passingEnvironment
    Assert-Condition -Name "$StepName passing runtime" -Condition ($passingResult.ExitCode -eq 0) `
        -Failure "returned $($passingResult.ExitCode) for passing projects."
    Assert-Condition -Name "$StepName passing output" `
        -Condition ([regex]::IsMatch($passingResult.StepOutput, '\Afailure-count=0\r?\n\z')) `
        -Failure 'does not contain exactly one failure-count=0 assignment.'
    $passingRows = @($passingResult.Summary -split '\r?\n' | Where-Object { $_.StartsWith('| tests/', [StringComparison]::Ordinal) })
    $expectedPassingRows = @(
        '| tests/pass-first/Pass.First.csproj | PASS | 0 |',
        '| tests/pass-later/Pass.Later.csproj | PASS | 0 |',
        '| tests/pass-special/Pass \| 50% \`Special\`.csproj | PASS | 0 |'
    )
    Assert-Condition -Name "$StepName passing summary rows" `
        -Condition (($passingRows -join "`n") -ceq ($expectedPassingRows -join "`n")) `
        -Failure 'does not contain one exact, ordered PASS row per configured project.'
    Assert-NotContains -Name "$StepName passing summary" -Content $passingResult.Summary -Unexpected '| FAIL |'
    if ($UsesCoverageSwitch) {
        Assert-NotContains -Name "$StepName disabled coverage runtime" -Content $passingResult.Invocations -Unexpected '--coverage'
    }

    $infrastructureEnvironment = @{
        $ProjectsVariable = "tests/infra-first/Infra%Down.csproj`ntests/pass-later/Pass.Later.csproj"
    }
    if ($UsesCoverageSwitch) {
        $infrastructureEnvironment.RUN_COVERAGE_GATE = 'true'
    }

    $infrastructureResult = Invoke-WorkflowBashBody -Body $body -Environment $infrastructureEnvironment
    $infrastructureInvocations = @($infrastructureResult.Invocations -split '\r?\n' | Where-Object { $_ })
    Assert-Condition -Name "$StepName infrastructure runtime" -Condition ($infrastructureResult.ExitCode -eq 42) `
        -Failure "returned $($infrastructureResult.ExitCode) instead of failing its own step."
    Assert-Condition -Name "$StepName infrastructure fail-fast runtime" -Condition ($infrastructureInvocations.Count -eq 1) `
        -Failure "ran $($infrastructureInvocations.Count) projects after an infrastructure failure."
    Assert-Contains -Name "$StepName infrastructure annotation" -Content $infrastructureResult.Output `
        -Expected '::error title=Test shard infrastructure failure::tests/infra-first/Infra%25Down.csproj exited with code 42 without producing'
}

function New-FailureGateEnvironment {
    param(
        [Parameter(Mandatory = $true)][string] $TestPlatform,
        [string] $UnitProjects = '',
        [string] $IntegrationProjects = '',
        [hashtable] $FailureCounts = @{}
    )

    $environment = @{
        TEST_PLATFORM                     = $TestPlatform
        UNIT_TEST_PROJECTS                = $UnitProjects
        INTEGRATION_TEST_PROJECTS         = $IntegrationProjects
        UNIT_VSTEST_FAILURE_COUNT         = ''
        UNIT_MTP_FAILURE_COUNT            = ''
        INTEGRATION_VSTEST_FAILURE_COUNT  = ''
        INTEGRATION_MTP_FAILURE_COUNT     = ''
    }
    foreach ($entry in $FailureCounts.GetEnumerator()) {
        $environment[$entry.Key] = [string] $entry.Value
    }

    return $environment
}

function Assert-PlatformAggregationSequence {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $TestPlatform,
        [Parameter(Mandatory = $true)][string] $UnitBody,
        [Parameter(Mandatory = $true)][string] $IntegrationBody,
        [Parameter(Mandatory = $true)][string] $FailureGateBody,
        [Parameter(Mandatory = $true)][string] $UnitFailureVariable,
        [Parameter(Mandatory = $true)][string] $IntegrationFailureVariable,
        [Parameter(Mandatory = $true)][bool] $UsesCoverageSwitch
    )

    $unitEnvironment = @{
        UNIT_TEST_PROJECTS = "tests/fail-first/Fail.First.csproj`ntests/pass-later/Pass.Later.csproj"
    }
    $integrationEnvironment = @{
        INTEGRATION_TEST_PROJECTS = "tests/pass-first/Pass.First.csproj`ntests/pass-later/Pass.Later.csproj"
    }
    if ($UsesCoverageSwitch) {
        $unitEnvironment.RUN_COVERAGE_GATE = 'true'
        $integrationEnvironment.RUN_COVERAGE_GATE = 'true'
    }

    $unitResult = Invoke-WorkflowBashBody -Body $UnitBody -Environment $unitEnvironment
    $integrationResult = Invoke-WorkflowBashBody -Body $IntegrationBody -Environment $integrationEnvironment
    Assert-Condition -Name "$Name sequence unit shard" -Condition ($unitResult.ExitCode -eq 0) `
        -Failure "returned $($unitResult.ExitCode) instead of deferring its test failure."
    Assert-Condition -Name "$Name sequence integration shard" -Condition ($integrationResult.ExitCode -eq 0) `
        -Failure "did not execute successfully after the Tier 1 test failure."
    $integrationInvocations = @($integrationResult.Invocations -split '\r?\n' | Where-Object { $_ })
    Assert-Condition -Name "$Name sequence integration projects" -Condition ($integrationInvocations.Count -eq 2) `
        -Failure "ran $($integrationInvocations.Count) Tier 2 projects instead of both configured projects."

    $unitCount = [regex]::Match($unitResult.StepOutput, '\Afailure-count=([0-9]+)\r?\n\z').Groups[1].Value
    $integrationCount = [regex]::Match($integrationResult.StepOutput, '\Afailure-count=([0-9]+)\r?\n\z').Groups[1].Value
    $failureCounts = @{
        $UnitFailureVariable        = $unitCount
        $IntegrationFailureVariable = $integrationCount
    }
    $gateEnvironment = New-FailureGateEnvironment `
        -TestPlatform $TestPlatform `
        -UnitProjects 'configured-unit-project' `
        -IntegrationProjects 'configured-integration-project' `
        -FailureCounts $failureCounts
    $gateResult = Invoke-WorkflowBashBody -Body $FailureGateBody -Environment $gateEnvironment
    Assert-Condition -Name "$Name sequence final gate" -Condition ($gateResult.ExitCode -eq 1) `
        -Failure "returned $($gateResult.ExitCode) instead of rejecting the Tier 1 failure after Tier 2 ran."
    Assert-Contains -Name "$Name sequence final gate" -Content $gateResult.Output `
        -Expected '::error title=Blocking test failures::1 test project(s) failed across the blocking tiers.'
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

Assert-ShardAggregationRuntime -Workflow $ciWorkflow `
    -StepName 'Unit tests (Tier 1, VSTest)' `
    -StepId 'unit-tests-vstest' `
    -ProjectsVariable 'UNIT_TEST_PROJECTS' `
    -ExpectedCondition "if: `${{ inputs.unit-test-projects != '' && inputs.test-platform == 'vstest' }}" `
    -ExpectedEnvironmentMapping "UNIT_TEST_PROJECTS: `${{ inputs.unit-test-projects }}" `
    -SummaryHeading '## Tier 1 Unit Tests (VSTest)' `
    -ExpectedInvocationFragments @(
        '--logger trx;LogFileName=Fail.First.csproj.trx',
        '--results-directory TestResults/Fail.First.csproj',
        '--collect:XPlat Code Coverage'
    ) `
    -UsesCoverageSwitch $false
Assert-ShardAggregationRuntime -Workflow $ciWorkflow `
    -StepName 'Unit tests (Tier 1, Microsoft.Testing.Platform)' `
    -StepId 'unit-tests-mtp' `
    -ProjectsVariable 'UNIT_TEST_PROJECTS' `
    -ExpectedCondition "if: `${{ inputs.unit-test-projects != '' && inputs.test-platform == 'microsoft-testing-platform' }}" `
    -ExpectedEnvironmentMapping "UNIT_TEST_PROJECTS: `${{ inputs.unit-test-projects }}" `
    -SummaryHeading '## Tier 1 Unit Tests (Microsoft.Testing.Platform)' `
    -ExpectedInvocationFragments @(
        '--results-directory TestResults/Fail.First.csproj',
        '--report-xunit-trx',
        '--report-xunit-trx-filename Fail.First.csproj.trx',
        '--coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml'
    ) `
    -UsesCoverageSwitch $true
Assert-ShardAggregationRuntime -Workflow $ciWorkflow `
    -StepName 'Integration tests (Tier 2, VSTest)' `
    -StepId 'integration-tests-vstest' `
    -ProjectsVariable 'INTEGRATION_TEST_PROJECTS' `
    -ExpectedCondition "if: `${{ inputs.integration-test-projects != '' && inputs.test-platform == 'vstest' }}" `
    -ExpectedEnvironmentMapping "INTEGRATION_TEST_PROJECTS: `${{ inputs.integration-test-projects }}" `
    -SummaryHeading '## Tier 2 Integration Tests (VSTest)' `
    -ExpectedInvocationFragments @(
        '--logger trx;LogFileName=Fail.First.csproj.trx',
        '--results-directory TestResults/Fail.First.csproj',
        '--collect:XPlat Code Coverage'
    ) `
    -UsesCoverageSwitch $false
Assert-ShardAggregationRuntime -Workflow $ciWorkflow `
    -StepName 'Integration tests (Tier 2, Microsoft.Testing.Platform)' `
    -StepId 'integration-tests-mtp' `
    -ProjectsVariable 'INTEGRATION_TEST_PROJECTS' `
    -ExpectedCondition "if: `${{ inputs.integration-test-projects != '' && inputs.test-platform == 'microsoft-testing-platform' }}" `
    -ExpectedEnvironmentMapping "INTEGRATION_TEST_PROJECTS: `${{ inputs.integration-test-projects }}" `
    -SummaryHeading '## Tier 2 Integration Tests (Microsoft.Testing.Platform)' `
    -ExpectedInvocationFragments @(
        '--results-directory TestResults/Fail.First.csproj',
        '--report-xunit-trx',
        '--report-xunit-trx-filename Fail.First.csproj.trx',
        '--coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml'
    ) `
    -UsesCoverageSwitch $true

$failureGateBlock = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Fail on blocking test failures'
if ([string]::IsNullOrWhiteSpace($failureGateBlock)) {
    $failures.Add('domain-ci.yml is missing the Fail on blocking test failures step.')
}
else {
    Assert-Contains -Name 'domain-ci.yml final failure gate' -Content $failureGateBlock -Expected 'if: always()'
    foreach ($outputReference in @(
        'steps.unit-tests-vstest.outputs.failure-count',
        'steps.unit-tests-mtp.outputs.failure-count',
        'steps.integration-tests-vstest.outputs.failure-count',
        'steps.integration-tests-mtp.outputs.failure-count'
    )) {
        Assert-Contains -Name 'domain-ci.yml final failure gate' -Content $failureGateBlock -Expected $outputReference
    }

    $coverageGateIndex = $ciWorkflow.IndexOf('- name: Validate coverage gates', [StringComparison]::Ordinal)
    $failureGateIndex = $ciWorkflow.IndexOf('- name: Fail on blocking test failures', [StringComparison]::Ordinal)
    $evidenceUploadIndex = $ciWorkflow.IndexOf('- name: Upload test and coverage evidence', [StringComparison]::Ordinal)
    Assert-Condition -Name 'domain-ci.yml final failure gate order' `
        -Condition ($coverageGateIndex -ge 0 -and $failureGateIndex -gt $coverageGateIndex -and $evidenceUploadIndex -gt $failureGateIndex) `
        -Failure 'does not place the final gate after coverage validation and before always-run evidence upload.'
}

$failureGateBody = $(if ($failureGateBlock) {
    Get-StepRunBody -Block $failureGateBlock -StepName 'Fail on blocking test failures'
} else { '' })
if (-not [string]::IsNullOrWhiteSpace($failureGateBody)) {
    $inactiveGateEnvironment = New-FailureGateEnvironment -TestPlatform 'vstest'
    $inactiveGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $inactiveGateEnvironment
    Assert-Condition -Name 'domain-ci.yml inactive failure gate runtime' -Condition ($inactiveGateResult.ExitCode -eq 0) `
        -Failure "returned $($inactiveGateResult.ExitCode) with no active shard outputs."

    $passingGateEnvironment = New-FailureGateEnvironment `
        -TestPlatform 'vstest' `
        -UnitProjects 'configured-unit-project' `
        -IntegrationProjects 'configured-integration-project' `
        -FailureCounts @{
            UNIT_VSTEST_FAILURE_COUNT        = '0'
            INTEGRATION_VSTEST_FAILURE_COUNT = '0'
        }
    $passingGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $passingGateEnvironment
    Assert-Condition -Name 'domain-ci.yml passing failure gate runtime' -Condition ($passingGateResult.ExitCode -eq 0) `
        -Failure "returned $($passingGateResult.ExitCode) for zero active failures."

    $failingGateEnvironment = New-FailureGateEnvironment `
        -TestPlatform 'vstest' `
        -UnitProjects 'configured-unit-project' `
        -IntegrationProjects 'configured-integration-project' `
        -FailureCounts @{
            UNIT_VSTEST_FAILURE_COUNT        = '1'
            INTEGRATION_VSTEST_FAILURE_COUNT = '2'
        }
    $failingGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $failingGateEnvironment
    Assert-Condition -Name 'domain-ci.yml failing failure gate runtime' -Condition ($failingGateResult.ExitCode -eq 1) `
        -Failure "returned $($failingGateResult.ExitCode) instead of rejecting aggregate failures."
    Assert-Contains -Name 'domain-ci.yml failing failure gate annotation' -Content $failingGateResult.Output `
        -Expected '::error title=Blocking test failures::3 test project(s) failed across the blocking tiers.'

    $invalidGateEnvironment = New-FailureGateEnvironment `
        -TestPlatform 'vstest' `
        -UnitProjects 'configured-unit-project' `
        -FailureCounts @{ UNIT_VSTEST_FAILURE_COUNT = 'invalid' }
    $invalidGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $invalidGateEnvironment
    Assert-Condition -Name 'domain-ci.yml invalid failure gate runtime' -Condition ($invalidGateResult.ExitCode -eq 1) `
        -Failure "returned $($invalidGateResult.ExitCode) instead of rejecting a nonnumeric output."
    Assert-Contains -Name 'domain-ci.yml invalid failure gate annotation' -Content $invalidGateResult.Output `
        -Expected '::error title=Invalid test shard output::'

    $missingGateEnvironment = New-FailureGateEnvironment `
        -TestPlatform 'microsoft-testing-platform' `
        -UnitProjects 'configured-unit-project'
    $missingGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $missingGateEnvironment
    Assert-Condition -Name 'domain-ci.yml missing active failure output runtime' -Condition ($missingGateResult.ExitCode -eq 1) `
        -Failure "returned $($missingGateResult.ExitCode) instead of rejecting a missing active output."
    Assert-Contains -Name 'domain-ci.yml missing active failure output annotation' -Content $missingGateResult.Output `
        -Expected '::error title=Missing test shard output::Tier 1 Microsoft.Testing.Platform did not report a failure count.'

    $leadingZeroGateEnvironment = New-FailureGateEnvironment `
        -TestPlatform 'vstest' `
        -UnitProjects 'configured-unit-project' `
        -FailureCounts @{ UNIT_VSTEST_FAILURE_COUNT = '08' }
    $leadingZeroGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $leadingZeroGateEnvironment
    Assert-Condition -Name 'domain-ci.yml leading-zero failure output runtime' -Condition ($leadingZeroGateResult.ExitCode -eq 1) `
        -Failure "returned $($leadingZeroGateResult.ExitCode) instead of reporting the decimal failure total."
    Assert-Contains -Name 'domain-ci.yml leading-zero failure output annotation' -Content $leadingZeroGateResult.Output `
        -Expected '::error title=Blocking test failures::8 test project(s) failed across the blocking tiers.'

    foreach ($channel in @(
        [pscustomobject] @{ Name = 'unit VSTest'; Platform = 'vstest'; Unit = 'configured'; Integration = ''; Variable = 'UNIT_VSTEST_FAILURE_COUNT' },
        [pscustomobject] @{ Name = 'unit MTP'; Platform = 'microsoft-testing-platform'; Unit = 'configured'; Integration = ''; Variable = 'UNIT_MTP_FAILURE_COUNT' },
        [pscustomobject] @{ Name = 'integration VSTest'; Platform = 'vstest'; Unit = ''; Integration = 'configured'; Variable = 'INTEGRATION_VSTEST_FAILURE_COUNT' },
        [pscustomobject] @{ Name = 'integration MTP'; Platform = 'microsoft-testing-platform'; Unit = ''; Integration = 'configured'; Variable = 'INTEGRATION_MTP_FAILURE_COUNT' }
    )) {
        $channelEnvironment = New-FailureGateEnvironment `
            -TestPlatform $channel.Platform `
            -UnitProjects $channel.Unit `
            -IntegrationProjects $channel.Integration `
            -FailureCounts @{ $channel.Variable = '1' }
        $channelResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $channelEnvironment
        Assert-Condition -Name "domain-ci.yml $($channel.Name) failure channel runtime" -Condition ($channelResult.ExitCode -eq 1) `
            -Failure "returned $($channelResult.ExitCode) instead of rejecting its isolated nonzero output."
        Assert-Contains -Name "domain-ci.yml $($channel.Name) failure channel annotation" -Content $channelResult.Output `
            -Expected '::error title=Blocking test failures::1 test project(s) failed across the blocking tiers.'
    }

    $unitVstestBody = Get-StepRunBody `
        -Block (Get-NamedStepBlock -Content $ciWorkflow -StepName 'Unit tests (Tier 1, VSTest)') `
        -StepName 'Unit tests (Tier 1, VSTest)'
    $integrationVstestBody = Get-StepRunBody `
        -Block (Get-NamedStepBlock -Content $ciWorkflow -StepName 'Integration tests (Tier 2, VSTest)') `
        -StepName 'Integration tests (Tier 2, VSTest)'
    Assert-PlatformAggregationSequence `
        -Name 'VSTest' `
        -TestPlatform 'vstest' `
        -UnitBody $unitVstestBody `
        -IntegrationBody $integrationVstestBody `
        -FailureGateBody $failureGateBody `
        -UnitFailureVariable 'UNIT_VSTEST_FAILURE_COUNT' `
        -IntegrationFailureVariable 'INTEGRATION_VSTEST_FAILURE_COUNT' `
        -UsesCoverageSwitch $false
    Assert-PlatformAggregationSequence `
        -Name 'Microsoft.Testing.Platform' `
        -TestPlatform 'microsoft-testing-platform' `
        -UnitBody (Get-StepRunBody -Block $unitMtpBlock -StepName 'Unit tests (Tier 1, Microsoft.Testing.Platform)') `
        -IntegrationBody (Get-StepRunBody -Block $integrationMtpBlock -StepName 'Integration tests (Tier 2, Microsoft.Testing.Platform)') `
        -FailureGateBody $failureGateBody `
        -UnitFailureVariable 'UNIT_MTP_FAILURE_COUNT' `
        -IntegrationFailureVariable 'INTEGRATION_MTP_FAILURE_COUNT' `
        -UsesCoverageSwitch $true
}

$evidenceUploadBlock = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Upload test and coverage evidence'
Assert-Contains -Name 'domain-ci.yml evidence upload' -Content $evidenceUploadBlock -Expected 'if: always()'
Assert-Contains -Name 'domain-ci.yml evidence upload' -Content $evidenceUploadBlock -Expected 'name: blocking-test-results'
Assert-Contains -Name 'domain-ci.yml evidence upload' -Content $evidenceUploadBlock -Expected 'TestResults/**/*.trx'
Assert-Contains -Name 'domain-ci.yml evidence upload' -Content $evidenceUploadBlock -Expected 'TestResults/**/coverage.cobertura.xml'

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
