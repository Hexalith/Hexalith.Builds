[CmdletBinding()]
param(
    [ValidateSet('legacy', 'v2')]
    [string] $Contract = 'legacy'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$isV2 = $Contract -eq 'v2'
$ciWorkflowName = if ($isV2) { 'domain-ci-v2.yml' } else { 'domain-ci.yml' }
$ciWorkflowPath = Join-Path $PSScriptRoot "../.github/workflows/$ciWorkflowName"
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
  # Simulate MTP/children that read stdin until EOF; the workflow must isolate them.
  cat >/dev/null
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
    case "$project" in
      *missing-report*) return 0 ;;
      *malformed-report*) printf '<TestRun' > "$result_directory/$trx_filename"; return 0 ;;
      *zero-report*) printf '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results/><ResultSummary><Counters total="0" executed="0"/></ResultSummary></TestRun>' > "$result_directory/$trx_filename"; return 0 ;;
    esac
    printf '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="Passed"/></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0"/></ResultSummary></TestRun>' > "$result_directory/$trx_filename"
    case "$project" in
      *stale-report*) touch -t 200001010000 "$result_directory/$trx_filename" ;;
      *failed-report*) sed -i 's/outcome="Passed"/outcome="Failed"/' "$result_directory/$trx_filename" ;;
      *inconsistent-report*) sed -i 's/total="1"/total="2"/' "$result_directory/$trx_filename" ;;
      *summary-failure*) sed -i 's/outcome="Completed"/outcome="Failed"/' "$result_directory/$trx_filename" ;;
      *failure-counter*) sed -i 's/failed="0"/failed="1"/' "$result_directory/$trx_filename" ;;
      *error-counter*) sed -i 's/error="0"/error="1"/' "$result_directory/$trx_filename" ;;
      *passed-counter*) sed -i 's/passed="1"/passed="0"/' "$result_directory/$trx_filename" ;;
    esac
  fi
  case "$project" in
    *infra-reported*) return 10 ;;
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
        $script:failures.Add("$ciWorkflowName is missing the $StepName step.")
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

    $consumingEnvironment = @{
        $ProjectsVariable = "tests/pass-first/One.csproj`ntests/pass-second/Two.csproj`ntests/pass-third/Three.csproj`ntests/pass-fourth/Four.csproj"
        RUN_COVERAGE_GATE = 'false'
    }
    $consumingResult = Invoke-WorkflowBashBody -Body $body -Environment $consumingEnvironment
    $consumingInvocations = @($consumingResult.Invocations -split '\r?\n' | Where-Object { $_ })
    Assert-Condition -Name "$StepName stdin-consuming children" `
        -Condition ($consumingResult.ExitCode -eq 0 -and $consumingInvocations.Count -eq 4 -and $consumingResult.StepOutput -match '\Afailure-count=0\r?\n\z') `
        -Failure 'did not execute all four configured projects with nonzero reports when children consume stdin.'

    foreach ($scenario in @('missing-report', 'stale-report', 'malformed-report', 'zero-report', 'failed-report', 'inconsistent-report', 'summary-failure', 'failure-counter', 'error-counter', 'passed-counter')) {
        $reportEnvironment = @{
            $ProjectsVariable = "tests/$scenario/Invalid.csproj`ntests/pass-later/Later.csproj"
            RUN_COVERAGE_GATE = 'false'
        }
        $reportResult = Invoke-WorkflowBashBody -Body $body -Environment $reportEnvironment
        Assert-Condition -Name "$StepName $scenario runtime" -Condition ($reportResult.ExitCode -eq 1) `
            -Failure "returned $($reportResult.ExitCode) instead of rejecting invalid test evidence."
        Assert-Contains -Name "$StepName $scenario diagnostic" -Content $reportResult.Output -Expected '::error title=Invalid test report::'
        Assert-NotContains -Name "$StepName $scenario output" -Content $reportResult.StepOutput -Unexpected 'failure-count=0'
    }

    # Deleting the prior same-basename report must also protect successful child exits.
    $staleSuccessEnvironment = @{
        $ProjectsVariable = "tests/pass-first/Same.csproj`ntests/missing-report/Same.csproj"
        RUN_COVERAGE_GATE = 'false'
    }
    $staleSuccessResult = Invoke-WorkflowBashBody -Body $body -Environment $staleSuccessEnvironment
    Assert-Condition -Name "$StepName stale successful report runtime" -Condition ($staleSuccessResult.ExitCode -eq 1) `
        -Failure 'accepted a prior project report after a successful child produced no report.'

    $infrastructureEnvironment = @{
        $ProjectsVariable = "tests/infra-first/Infra%0ADown.csproj`ntests/pass-later/Pass.Later.csproj"
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
        -Expected '::error title=Test shard infrastructure failure::tests/infra-first/Infra%250ADown.csproj exited with code 42 without producing TestResults/Infra%250ADown.csproj/Infra%250ADown.csproj.trx.'
    $infrastructureRows = @($infrastructureResult.Summary -split '\r?\n' | Where-Object { $_.StartsWith('| tests/', [StringComparison]::Ordinal) })
    Assert-Condition -Name "$StepName infrastructure summary" `
        -Condition (($infrastructureRows -join "`n") -ceq '| tests/infra-first/Infra%0ADown.csproj | FAIL | 42 |') `
        -Failure 'does not record the attempted infrastructure failure exactly once.'

    $collisionEnvironment = @{
        $ProjectsVariable = "tests/pass-first/Same.csproj`ntests/infra-first/Same.csproj`ntests/pass-later/Pass.Later.csproj"
    }
    if ($UsesCoverageSwitch) {
        $collisionEnvironment.RUN_COVERAGE_GATE = 'true'
    }
    $collisionResult = Invoke-WorkflowBashBody -Body $body -Environment $collisionEnvironment
    $collisionInvocations = @($collisionResult.Invocations -split '\r?\n' | Where-Object { $_ })
    Assert-Condition -Name "$StepName stale report runtime" -Condition ($collisionResult.ExitCode -eq 42) `
        -Failure "returned $($collisionResult.ExitCode) instead of rejecting the stale same-basename report."
    Assert-Condition -Name "$StepName stale report fail-fast runtime" -Condition ($collisionInvocations.Count -eq 2) `
        -Failure "ran $($collisionInvocations.Count) projects instead of stopping at the evidence-less second project."
    $collisionRows = @($collisionResult.Summary -split '\r?\n' | Where-Object { $_.StartsWith('| tests/', [StringComparison]::Ordinal) })
    $expectedCollisionRows = @(
        '| tests/pass-first/Same.csproj | PASS | 0 |',
        '| tests/infra-first/Same.csproj | FAIL | 42 |'
    )
    Assert-Condition -Name "$StepName stale report summary" `
        -Condition (($collisionRows -join "`n") -ceq ($expectedCollisionRows -join "`n")) `
        -Failure 'does not preserve both attempted outcomes in order.'
    Assert-Contains -Name "$StepName stale report annotation" -Content $collisionResult.Output `
        -Expected '::error title=Test shard infrastructure failure::tests/infra-first/Same.csproj exited with code 42 without producing TestResults/Same.csproj/Same.csproj.trx.'

    if ($UsesCoverageSwitch) {
        $reportedInfrastructureEnvironment = @{
            $ProjectsVariable = "tests/infra-reported/Infra.Reported.csproj`ntests/pass-later/Pass.Later.csproj"
            RUN_COVERAGE_GATE = 'true'
        }
        $reportedInfrastructureResult = Invoke-WorkflowBashBody -Body $body -Environment $reportedInfrastructureEnvironment
        Assert-Condition -Name "$StepName reported infrastructure runtime" -Condition ($reportedInfrastructureResult.ExitCode -eq 10) `
            -Failure "returned $($reportedInfrastructureResult.ExitCode) instead of preserving MTP infrastructure exit 10."
        $reportedInfrastructureInvocations = @($reportedInfrastructureResult.Invocations -split '\r?\n' | Where-Object { $_ })
        Assert-Condition -Name "$StepName reported infrastructure fail-fast runtime" -Condition ($reportedInfrastructureInvocations.Count -eq 1) `
            -Failure 'continued after an MTP infrastructure failure with a fresh report.'
        $reportedInfrastructureRows = @($reportedInfrastructureResult.Summary -split '\r?\n' | Where-Object { $_.StartsWith('| tests/', [StringComparison]::Ordinal) })
        Assert-Condition -Name "$StepName reported infrastructure summary" `
            -Condition (($reportedInfrastructureRows -join "`n") -ceq '| tests/infra-reported/Infra.Reported.csproj | FAIL | 10 |') `
            -Failure 'does not record the reported MTP infrastructure failure exactly once.'
        Assert-Contains -Name "$StepName reported infrastructure annotation" -Content $reportedInfrastructureResult.Output `
            -Expected '::error title=Test shard infrastructure failure::tests/infra-reported/Infra.Reported.csproj exited with infrastructure code 10.'
    }
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

function Assert-RestoreAndBuildUseReleaseRuntime {
    param([Parameter(Mandatory = $true)][string] $Workflow)

    $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-restore-build-$([guid]::NewGuid().ToString('N'))"
    try {
        $null = New-Item -ItemType Directory -Path $temporaryDirectory
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../global.json') -Destination (Join-Path $temporaryDirectory 'global.json')
        $projectPath = Join-Path $temporaryDirectory 'module graph with spaces.proj'
        $project = @'
<Project DefaultTargets="Build">
  <PropertyGroup>
    <Configuration Condition="'$(Configuration)' == ''">Debug</Configuration>
  </PropertyGroup>
  <Target Name="Restore">
    <Error Condition="'$(Configuration)' != 'Release'" Text="Restore must use Release." />
    <WriteLinesToFile File="restore-configuration.txt" Lines="$(Configuration)" Overwrite="true" />
  </Target>
  <Target Name="Build">
    <Error Condition="'$(Configuration)' != 'Release'" Text="Build must use Release." />
    <WriteLinesToFile File="build-configuration.txt" Lines="$(Configuration)" Overwrite="true" />
  </Target>
</Project>
'@
        Set-Content -LiteralPath $projectPath -Value $project -NoNewline

        foreach ($stepName in @('Restore', 'Build')) {
            $block = Get-NamedStepBlock -Content $Workflow -StepName $stepName
            $body = [regex]::Match($block, '(?m)^        run: (?<Body>dotnet [^\r\n]+)\r?$')
            Assert-Condition -Name "$stepName executable configuration body" -Condition $body.Success -Failure 'is missing its executable inline dotnet command.'
            if (-not $body.Success) { continue }

            $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
            $startInfo.FileName = 'bash'
            $startInfo.ArgumentList.Add('-c')
            $startInfo.ArgumentList.Add($body.Groups['Body'].Value)
            $startInfo.UseShellExecute = $false
            $startInfo.RedirectStandardInput = $true
            $startInfo.RedirectStandardOutput = $true
            $startInfo.RedirectStandardError = $true
            $startInfo.WorkingDirectory = $temporaryDirectory
            $startInfo.Environment['SOLUTION'] = $projectPath
            $process = [System.Diagnostics.Process]::new()
            try {
                $process.StartInfo = $startInfo
                $null = $process.Start()
                $process.StandardInput.Close()
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(30000)) {
                    $process.Kill($true)
                    $null = $process.WaitForExit(5000)
                    $script:failures.Add("$stepName configuration fixture exceeded 30 seconds.")
                    continue
                }
                $diagnostic = $stdout.Result + $stderr.Result
                if ($diagnostic.Length -gt 1000) { $diagnostic = $diagnostic.Substring(0, 1000) }
                Assert-Condition -Name "$stepName Release execution" -Condition ($process.ExitCode -eq 0) -Failure "did not restore/build the Release graph: $diagnostic"
                $configurationPath = Join-Path $temporaryDirectory "$($stepName.ToLowerInvariant())-configuration.txt"
                $configuration = if (Test-Path -LiteralPath $configurationPath) { (Get-Content -LiteralPath $configurationPath -Raw).Trim() } else { '' }
                Assert-Condition -Name "$stepName effective configuration" -Condition ($configuration -ceq 'Release') -Failure "used '$configuration' instead of Release."
            }
            finally {
                $process.Dispose()
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDirectory) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force }
    }
}

$ciWorkflow = Get-Content -LiteralPath $ciWorkflowPath -Raw
$releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw
$buildReleaseWorkflow = Get-Content -LiteralPath $buildReleaseWorkflowPath -Raw

$packagePreparation = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Prepare package dependencies'
$packageBody = Get-StepRunBody -Block $packagePreparation -StepName 'Prepare package dependencies'
Assert-Contains -Name 'Package preparation opt-in' -Content $packagePreparation -Expected "inputs.package-prepare-script != ''"
Assert-Contains -Name 'Package preparation deadline' -Content $packageBody -Expected 'timeout --signal=TERM --kill-after=30s 1800'
$expectedPreparationCount = if ($isV2) { 1 } else { 3 }
Assert-Condition -Name 'Package preparation in every job' -Condition (([regex]::Matches($ciWorkflow, '      - name: Prepare package dependencies')).Count -eq $expectedPreparationCount) -Failure 'must prepare before restore in every applicable job.'
foreach ($code in @(0, 23)) {
    $fixture = "mkdir -p hooks`nprintf 'exit $code\n' > hooks/packages.sh`n"
    $result = Invoke-WorkflowBashBody -Body ($fixture + $packageBody) -Environment @{ PACKAGE_PREPARE_SCRIPT = 'hooks/packages.sh' }
    Assert-Condition -Name "Package preparation exit $code" -Condition ($result.ExitCode -eq $code) -Failure "returned $($result.ExitCode)."
}
foreach ($path in @('../outside.sh', '/tmp/outside.sh', 'hooks/missing.sh', 'hooks/packages.sh;true', 'hooks/./packages.sh')) {
    $result = Invoke-WorkflowBashBody -Body ("mkdir -p hooks`nprintf 'exit 0\n' > hooks/packages.sh`n" + $packageBody) -Environment @{ PACKAGE_PREPARE_SCRIPT = $path }
    Assert-Condition -Name "Package preparation invalid path $path" -Condition ($result.ExitCode -ne 0) -Failure 'accepted an invalid path.'
}
foreach ($fixture in @(
    "mkdir -p hooks`nprintf 'exit 0\n' > hooks/real.sh`nln -s real.sh hooks/packages.sh`n",
    "mkdir -p real`nprintf 'exit 0\n' > real/packages.sh`nln -s real hooks`n"
)) {
    $result = Invoke-WorkflowBashBody -Body ($fixture + $packageBody) -Environment @{ PACKAGE_PREPARE_SCRIPT = 'hooks/packages.sh' }
    Assert-Condition -Name 'Package preparation linked path' -Condition ($result.ExitCode -ne 0) -Failure 'accepted a linked path.'
}
$result = Invoke-WorkflowBashBody -Body ("mkdir -p hooks`nprintf 'exit 0\n' > hooks/packages.sh`n" + 'timeout() { [ "$3" = 1800 ] || exit 99; return 124; }' + "`n" + $packageBody) -Environment @{ PACKAGE_PREPARE_SCRIPT = 'hooks/packages.sh' }
Assert-Condition -Name 'Package preparation timeout' -Condition ($result.ExitCode -eq 124) -Failure 'lost its deadline or timeout exit.'
# Exercise both representations in memory; never rewrite the runtime workflow.
$ciWorkflowLf = $ciWorkflow.Replace("`r`n", "`n")
foreach ($workflowInput in @($ciWorkflowLf, $ciWorkflowLf.Replace("`n", "`r`n"))) {
    Assert-RestoreAndBuildUseReleaseRuntime -Workflow $workflowInput
}

if (-not $isV2) {
    $hookValidation = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Validate Aspire hooks'
    $hookBody = Get-StepRunBody -Block $hookValidation -StepName 'Validate Aspire hooks'
    $hookEnvironment = @{
        ASPIRE_PREPARE_SCRIPT = 'hooks/prepare.sh'
        ASPIRE_RUNNER_SCRIPT = 'hooks/run.sh'
        ASPIRE_EVIDENCE_PATH = 'TestResults/aspire-hook-evidence'
        ASPIRE_PREPARE_TIMEOUT = '40'
        ASPIRE_RUNNER_TIMEOUT = '300'
        ASPIRE_JOB_TIMEOUT = '360'
    }
    $hookFixture = "mkdir -p hooks`nprintf 'exit 0\n' > hooks/prepare.sh`nprintf 'exit 0\n' > hooks/run.sh`n"
    $hookResult = Invoke-WorkflowBashBody -Body ($hookFixture + $hookBody) -Environment $hookEnvironment
    Assert-Condition -Name 'Aspire valid hooks' -Condition ($hookResult.ExitCode -eq 0) -Failure "rejected valid hooks: $($hookResult.Output)"
    foreach ($scenario in @(
        @{ Key = 'ASPIRE_PREPARE_SCRIPT'; Value = '../outside.sh' },
        @{ Key = 'ASPIRE_RUNNER_SCRIPT'; Value = '/tmp/run.sh' },
        @{ Key = 'ASPIRE_RUNNER_SCRIPT'; Value = 'hooks/missing.sh' },
        @{ Key = 'ASPIRE_RUNNER_SCRIPT'; Value = 'hooks/run.sh;true' },
        @{ Key = 'ASPIRE_EVIDENCE_PATH'; Value = '.' },
        @{ Key = 'ASPIRE_EVIDENCE_PATH'; Value = 'TestResults/../outside' },
        @{ Key = 'ASPIRE_PREPARE_TIMEOUT'; Value = '0' },
        @{ Key = 'ASPIRE_PREPARE_TIMEOUT'; Value = '61' },
        @{ Key = 'ASPIRE_RUNNER_TIMEOUT'; Value = '341' },
        @{ Key = 'ASPIRE_RUNNER_TIMEOUT'; Value = '0.5' },
        @{ Key = 'ASPIRE_JOB_TIMEOUT'; Value = '361' },
        @{ Key = 'ASPIRE_JOB_TIMEOUT'; Value = '340' }
    )) {
        $environment = $hookEnvironment.Clone()
        $environment[$scenario.Key] = $scenario.Value
        $result = Invoke-WorkflowBashBody -Body ($hookFixture + $hookBody) -Environment $environment
        Assert-Condition -Name "Aspire invalid $($scenario.Key)=$($scenario.Value)" -Condition ($result.ExitCode -ne 0) -Failure 'accepted invalid hook inputs.'
    }
    $symlinkResult = Invoke-WorkflowBashBody -Body ($hookFixture + "mv hooks/run.sh hooks/real.sh`nln -s real.sh hooks/run.sh`n" + $hookBody) -Environment $hookEnvironment
    Assert-Condition -Name 'Aspire linked script' -Condition ($symlinkResult.ExitCode -ne 0) -Failure 'accepted a symbolic link.'
    $seededEvidence = "mkdir -p TestResults/aspire-hook-evidence`nprintf 'private-key-sentinel\n' > TestResults/aspire-hook-evidence/issuer.key`n"
    $seededResult = Invoke-WorkflowBashBody -Body ($hookFixture + $seededEvidence + $hookBody) -Environment $hookEnvironment
    Assert-Condition -Name 'Aspire unowned evidence rejection' -Condition ($seededResult.ExitCode -ne 0) -Failure 'accepted pre-existing files for the always-run artifact upload.'
    Assert-Contains -Name 'Aspire unowned evidence diagnostic' -Content $seededResult.Output -Expected 'fresh empty directory'
    foreach ($step in @(
        @{ Name = 'Prepare Aspire tier'; Script = 'ASPIRE_PREPARE_SCRIPT'; Bound = 'ASPIRE_PREPARE_TIMEOUT' },
        @{ Name = 'Run Aspire hook'; Script = 'ASPIRE_RUNNER_SCRIPT'; Bound = 'ASPIRE_RUNNER_TIMEOUT' }
    )) {
        $block = Get-NamedStepBlock -Content $ciWorkflow -StepName $step.Name
        $body = Get-StepRunBody -Block $block -StepName $step.Name
        Assert-Contains -Name $step.Name -Content $block -Expected 'timeout-minutes: ${{ inputs.aspire-'
        Assert-Contains -Name $step.Name -Content $block -Expected 'TEST_PLATFORM: ${{ inputs.test-platform }}'
        Assert-Contains -Name $step.Name -Content $body -Expected '--signal=TERM --kill-after=30s'
        foreach ($code in @(0, 23, 124)) {
            $environment = @{ $step.Script = 'hooks/run.sh'; $step.Bound = '2' }
            $fixture = "mkdir -p hooks`nprintf 'exit $code\n' > hooks/run.sh`n"
            $result = Invoke-WorkflowBashBody -Body ($fixture + $body) -Environment $environment
            Assert-Condition -Name "$($step.Name) exit $code" -Condition ($result.ExitCode -eq $code) -Failure "returned $($result.ExitCode)."
        }
        $result = Invoke-WorkflowBashBody -Body ('timeout() { [ "$3" = 120 ] || exit 99; return 124; }' + "`n" + $body) -Environment @{ $step.Script = 'hooks/run.sh'; $step.Bound = '2' }
        Assert-Condition -Name "$($step.Name) timeout propagation" -Condition ($result.ExitCode -eq 124) -Failure 'lost the bound or deadline exit.'
    }
    $aspireJob = $ciWorkflow.Substring($ciWorkflow.IndexOf('  aspire-tests:')).Split('  performance-tests:')[0]
    $trustStepName = 'Trust HTTPS development certificates for live sockets'
    $trustBlock = Get-NamedStepBlock -Content $aspireJob -StepName $trustStepName
    $trustBody = Get-StepRunBody -Block $trustBlock -StepName $trustStepName
    $trustCondition = [regex]::Match($trustBlock, '(?m)^        if: \$\{\{ (?<Condition>[^\r\n]+) \}\}\r?$').Groups['Condition'].Value
    $trustClause = "inputs\.(?<Input>aspire-test-project|aspire-browser-project) != ''"
    Assert-Condition -Name 'Aspire HTTPS trust condition' `
        -Condition ([regex]::IsMatch($trustCondition, "\A$trustClause(?: \|\| $trustClause)*\z")) `
        -Failure 'must select the configured .NET or browser lane without an unmodelled condition.'
    $trustInputs = @([regex]::Matches($trustCondition, $trustClause) | ForEach-Object { $_.Groups['Input'].Value })
    foreach ($scenario in @(
        @{ Name = '.NET-only'; Project = 'tests/Module.Aspire.Tests'; Browser = ''; Expected = $true },
        @{ Name = 'browser'; Project = ''; Browser = 'tests/browser'; Expected = $true },
        @{ Name = 'combined'; Project = 'tests/Module.Aspire.Tests'; Browser = 'tests/browser'; Expected = $true },
        @{ Name = 'unconfigured'; Project = ''; Browser = ''; Expected = $false }
    )) {
        $inputs = @{ 'aspire-test-project' = $scenario.Project; 'aspire-browser-project' = $scenario.Browser }
        $applies = @($trustInputs | Where-Object { $inputs[$_] -ne '' }).Count -gt 0
        Assert-Condition -Name "Aspire HTTPS trust $($scenario.Name) applicability" `
            -Condition ($applies -eq $scenario.Expected) -Failure 'selects the wrong certificate trust behavior.'
    }
    $trustFixture = @'
set -euo pipefail
export GITHUB_ENV="${GITHUB_OUTPUT}.env"
# Exercise the workflow shell without changing host packages or certificate stores.
sudo() { :; }
mkdir() { :; }
certutil() { :; }
dotnet() {
  [ "$*" = 'dev-certs https --trust' ] || return 91
  [ "$(printenv SSL_CERT_DIR)" = "${HOME}/.aspnet/dev-certs/trust:/usr/lib/ssl/certs" ] || return 92
  IFS= read -r persisted_trust < "$GITHUB_ENV"
  [ "$persisted_trust" = "SSL_CERT_DIR=${SSL_CERT_DIR}" ] || return 93
  printf 'HTTPS trust receives exported and persisted OpenSSL configuration.\n'
}

'@
    $trustResult = Invoke-WorkflowBashBody -Body ($trustFixture + $trustBody) -Environment @{}
    Assert-Condition -Name 'Aspire HTTPS trust OpenSSL runtime' -Condition ($trustResult.ExitCode -eq 0) `
        -Failure "did not export and persist OpenSSL trust before dev-certs: $($trustResult.Output)"
    Assert-Contains -Name 'Aspire HTTPS trust invocation' -Content $trustResult.Output `
        -Expected 'HTTPS trust receives exported and persisted OpenSSL configuration.'
    Assert-Contains -Name 'Aspire HTTPS NSS stores' -Content $trustBody -Expected '"$HOME/.pki/nssdb" "$HOME/.local/share/pki/nssdb"'
    Assert-Contains -Name 'Aspire HTTPS NSS prerequisites' -Content $trustBody -Expected 'sudo apt-get install -y libnss3-tools'
    Assert-Contains -Name 'Aspire HTTPS browser peer trust' -Content $trustBody -Expected '-t ''P,,'' -i "$live_certificate"'
    Assert-Condition -Name 'Aspire HTTPS trust ordering' `
        -Condition ($aspireJob.IndexOf($trustStepName) -lt $aspireJob.IndexOf('Restore and Build')) `
        -Failure 'must configure trust before building or executing the Aspire lane.'
    $browserTools = Get-NamedStepBlock -Content $aspireJob -StepName 'Install pinned browser qualification tools'
    Assert-Contains -Name 'Aspire browser tooling opt-in' -Content $browserTools -Expected "inputs.aspire-browser-project != ''"
    $daprIndex = $aspireJob.IndexOf('Install and initialize Dapr')
    $prepareIndex = $aspireJob.IndexOf('Prepare Aspire tier')
    $runnerIndex = $aspireJob.IndexOf('Run Aspire hook')
    Assert-Condition -Name 'Aspire Dapr step exists' -Condition ($daprIndex -ge 0) -Failure 'omits Dapr initialization in the Aspire job.'
    Assert-Condition -Name 'Aspire preparation step exists' -Condition ($prepareIndex -ge 0) -Failure 'omits preparation in the Aspire job.'
    Assert-Condition -Name 'Aspire runner step exists' -Condition ($runnerIndex -ge 0) -Failure 'omits execution in the Aspire job.'
    Assert-Condition -Name 'Aspire preparation ordering' -Condition ($daprIndex -lt $prepareIndex -and $prepareIndex -lt $runnerIndex) -Failure 'does not initialize Dapr before preparation and execution.'
    foreach ($platform in @('VSTest', 'Microsoft.Testing.Platform')) {
        $block = Get-NamedStepBlock -Content $ciWorkflow -StepName "Aspire contract tests (Tier 3, non-Performance, $platform)"
        Assert-Contains -Name "Aspire default $platform" -Content $block -Expected "inputs.aspire-runner-script == '' && inputs.test-platform =="
    }
    $hookUpload = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Upload Aspire hook evidence'
    Assert-Contains -Name 'Aspire hook upload' -Content $hookUpload -Expected 'always()'
    Assert-Contains -Name 'Aspire hook upload validation gate' -Content $hookUpload -Expected "steps.validate-aspire-hooks.outcome == 'success'"
    Assert-Contains -Name 'Aspire hook upload path' -Content $hookUpload -Expected 'path: ${{ inputs.aspire-evidence-path }}'
}

foreach ($workflow in @(
    [pscustomobject] @{ Name = $ciWorkflowName; Content = $ciWorkflow },
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

Assert-NotContains -Name "$ciWorkflowName" -Content $ciWorkflow -Unexpected 'run-coverage-gate is not supported with microsoft-testing-platform'
$unitMtpBlock = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Unit tests (Tier 1, Microsoft.Testing.Platform)'
Assert-Condition -Name "$ciWorkflowName unit MTP coverage" -Condition (Test-MtpCoverageBlock -Block $unitMtpBlock) `
    -Failure 'does not preserve independent enabled and disabled coverage arguments.'
if (-not $isV2) {
    $integrationMtpBlock = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Integration tests (Tier 2, Microsoft.Testing.Platform)'
    Assert-Condition -Name "$ciWorkflowName integration MTP coverage" -Condition (Test-MtpCoverageBlock -Block $integrationMtpBlock) `
        -Failure 'does not preserve independent enabled and disabled coverage arguments.'
}

$coverageAppend = 'coverage_args+=(--coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml)'
$disabledSeed = 'coverage_args=()'
Assert-Condition -Name "$ciWorkflowName unit enabled-coverage mutation contract" `
    -Condition (-not (Test-MtpCoverageBlock -Block $unitMtpBlock.Replace($coverageAppend, '# removed'))) `
    -Failure 'did not reject removal of the unit enabled-coverage arguments.'
Assert-Condition -Name "$ciWorkflowName unit disabled-coverage mutation contract" `
    -Condition (-not (Test-MtpCoverageBlock -Block $unitMtpBlock.Replace($disabledSeed, 'coverage_args=(--coverage)'))) `
    -Failure 'did not reject pre-populated unit disabled-coverage arguments.'
if (-not $isV2) {
    Assert-Condition -Name "$ciWorkflowName integration enabled-coverage mutation contract" `
        -Condition (-not (Test-MtpCoverageBlock -Block $integrationMtpBlock.Replace($coverageAppend, '# removed'))) `
        -Failure 'did not reject removal of the integration enabled-coverage arguments.'
    Assert-Condition -Name "$ciWorkflowName integration disabled-coverage mutation contract" `
        -Condition (-not (Test-MtpCoverageBlock -Block $integrationMtpBlock.Replace($disabledSeed, 'coverage_args=(--coverage)'))) `
        -Failure 'did not reject pre-populated integration disabled-coverage arguments.'
    Assert-Condition -Name "$ciWorkflowName unit/integration isolation" `
        -Condition ((Test-MtpCoverageBlock -Block $unitMtpBlock) -and (Test-MtpCoverageBlock -Block $integrationMtpBlock)) `
        -Failure 'does not validate the unit and integration coverage blocks separately.'
}

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
if (-not $isV2) {
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
}

$failureOutputChannels = @(
    [pscustomobject] @{ Name = 'unit VSTest'; Platform = 'vstest'; Unit = 'configured'; Integration = ''; Variable = 'UNIT_VSTEST_FAILURE_COUNT'; StepId = 'unit-tests-vstest'; StepName = 'Unit tests (Tier 1, VSTest)' },
    [pscustomobject] @{ Name = 'unit MTP'; Platform = 'microsoft-testing-platform'; Unit = 'configured'; Integration = ''; Variable = 'UNIT_MTP_FAILURE_COUNT'; StepId = 'unit-tests-mtp'; StepName = 'Unit tests (Tier 1, Microsoft.Testing.Platform)' }
)
if (-not $isV2) {
    $failureOutputChannels += @(
        [pscustomobject] @{ Name = 'integration VSTest'; Platform = 'vstest'; Unit = ''; Integration = 'configured'; Variable = 'INTEGRATION_VSTEST_FAILURE_COUNT'; StepId = 'integration-tests-vstest'; StepName = 'Integration tests (Tier 2, VSTest)' },
        [pscustomobject] @{ Name = 'integration MTP'; Platform = 'microsoft-testing-platform'; Unit = ''; Integration = 'configured'; Variable = 'INTEGRATION_MTP_FAILURE_COUNT'; StepId = 'integration-tests-mtp'; StepName = 'Integration tests (Tier 2, Microsoft.Testing.Platform)' }
    )
}

$failureGateBlock = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Fail on blocking test failures'
if ([string]::IsNullOrWhiteSpace($failureGateBlock)) {
    $failures.Add('domain-ci.yml is missing the Fail on blocking test failures step.')
}
else {
    Assert-Contains -Name "$ciWorkflowName final failure gate" -Content $failureGateBlock -Expected 'if: always()'
    foreach ($channel in $failureOutputChannels) {
        $expectedMapping = '          ' + $channel.Variable + ': ${{ steps.' + $channel.StepId + '.outputs.failure-count }}'
        Assert-Condition -Name "$ciWorkflowName $($channel.Name) output mapping" `
            -Condition ([regex]::Matches($failureGateBlock, '(?m)^' + [regex]::Escape($expectedMapping) + '\r?$').Count -eq 1) `
            -Failure 'does not map its failure-count variable to exactly one matching shard output.'
    }

    $coverageGateIndex = $ciWorkflow.IndexOf('- name: Validate coverage gates', [StringComparison]::Ordinal)
    $failureGateIndex = $ciWorkflow.IndexOf('- name: Fail on blocking test failures', [StringComparison]::Ordinal)
    $evidenceUploadIndex = $ciWorkflow.IndexOf('- name: Upload test and coverage evidence', [StringComparison]::Ordinal)
    Assert-Condition -Name "$ciWorkflowName final failure gate order" `
        -Condition ($coverageGateIndex -ge 0 -and $failureGateIndex -gt $coverageGateIndex -and $evidenceUploadIndex -gt $failureGateIndex) `
        -Failure 'does not place the final gate after coverage validation and before always-run evidence upload.'

    if (-not $isV2) {
        $governedDaprIndex = $ciWorkflow.IndexOf('- name: Install and initialize Dapr (governed)', [StringComparison]::Ordinal)
        $legacyDaprIndex = $ciWorkflow.IndexOf("- name: Install and initialize Dapr`n", [StringComparison]::Ordinal)
        if ($legacyDaprIndex -lt 0) {
            $legacyDaprIndex = $ciWorkflow.IndexOf("- name: Install and initialize Dapr`r`n", [StringComparison]::Ordinal)
        }
        foreach ($platform in @('VSTest', 'Microsoft.Testing.Platform')) {
            $unitIndex = $ciWorkflow.IndexOf("- name: Unit tests (Tier 1, $platform)", [StringComparison]::Ordinal)
            $integrationIndex = $ciWorkflow.IndexOf("- name: Integration tests (Tier 2, $platform)", [StringComparison]::Ordinal)
            Assert-Condition -Name "$ciWorkflowName $platform prerequisite order" `
                -Condition ($unitIndex -ge 0 -and $governedDaprIndex -gt $unitIndex -and $legacyDaprIndex -gt $governedDaprIndex -and $integrationIndex -gt $legacyDaprIndex -and $coverageGateIndex -gt $integrationIndex -and $failureGateIndex -gt $coverageGateIndex) `
                -Failure 'does not run Tier 1, Dapr initialization, Tier 2, coverage, and the final gate in order.'
        }
    }
}

$failureGateBody = $(if ($failureGateBlock) {
    Get-StepRunBody -Block $failureGateBlock -StepName 'Fail on blocking test failures'
} else { '' })
if (-not [string]::IsNullOrWhiteSpace($failureGateBody)) {
    $inactiveGateEnvironment = New-FailureGateEnvironment -TestPlatform 'vstest'
    $inactiveGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $inactiveGateEnvironment
    Assert-Condition -Name "$ciWorkflowName inactive failure gate runtime" -Condition ($inactiveGateResult.ExitCode -eq 0) `
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
    Assert-Condition -Name "$ciWorkflowName passing failure gate runtime" -Condition ($passingGateResult.ExitCode -eq 0) `
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
    Assert-Condition -Name "$ciWorkflowName failing failure gate runtime" -Condition ($failingGateResult.ExitCode -eq 1) `
        -Failure "returned $($failingGateResult.ExitCode) instead of rejecting aggregate failures."
    $expectedFailureCount = if ($isV2) { 1 } else { 3 }
    Assert-Contains -Name "$ciWorkflowName failing failure gate annotation" -Content $failingGateResult.Output `
        -Expected "::error title=Blocking test failures::$expectedFailureCount test project(s) failed across the blocking tiers."

    $invalidGateEnvironment = New-FailureGateEnvironment `
        -TestPlatform 'vstest' `
        -UnitProjects 'configured-unit-project' `
        -FailureCounts @{ UNIT_VSTEST_FAILURE_COUNT = 'invalid' }
    $invalidGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $invalidGateEnvironment
    Assert-Condition -Name "$ciWorkflowName invalid failure gate runtime" -Condition ($invalidGateResult.ExitCode -eq 1) `
        -Failure "returned $($invalidGateResult.ExitCode) instead of rejecting a nonnumeric output."
    Assert-Contains -Name "$ciWorkflowName invalid failure gate annotation" -Content $invalidGateResult.Output `
        -Expected '::error title=Invalid test shard output::'

    $missingGateEnvironment = New-FailureGateEnvironment `
        -TestPlatform 'microsoft-testing-platform' `
        -UnitProjects 'configured-unit-project'
    $missingGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $missingGateEnvironment
    Assert-Condition -Name "$ciWorkflowName missing active failure output runtime" -Condition ($missingGateResult.ExitCode -eq 1) `
        -Failure "returned $($missingGateResult.ExitCode) instead of rejecting a missing active output."
    Assert-Contains -Name "$ciWorkflowName missing active failure output annotation" -Content $missingGateResult.Output `
        -Expected '::error title=Missing test shard output::Tier 1 Microsoft.Testing.Platform did not report a failure count.'

    $leadingZeroGateEnvironment = New-FailureGateEnvironment `
        -TestPlatform 'vstest' `
        -UnitProjects 'configured-unit-project' `
        -FailureCounts @{ UNIT_VSTEST_FAILURE_COUNT = '08' }
    $leadingZeroGateResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $leadingZeroGateEnvironment
    Assert-Condition -Name "$ciWorkflowName leading-zero failure output runtime" -Condition ($leadingZeroGateResult.ExitCode -eq 1) `
        -Failure "returned $($leadingZeroGateResult.ExitCode) instead of reporting the decimal failure total."
    Assert-Contains -Name "$ciWorkflowName leading-zero failure output annotation" -Content $leadingZeroGateResult.Output `
        -Expected '::error title=Blocking test failures::8 test project(s) failed across the blocking tiers.'

    foreach ($channel in $failureOutputChannels) {
        $channelEnvironment = New-FailureGateEnvironment `
            -TestPlatform $channel.Platform `
            -UnitProjects $channel.Unit `
            -IntegrationProjects $channel.Integration `
            -FailureCounts @{ $channel.Variable = '1' }
        $channelResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $channelEnvironment
        Assert-Condition -Name "$ciWorkflowName $($channel.Name) failure channel runtime" -Condition ($channelResult.ExitCode -eq 1) `
            -Failure "returned $($channelResult.ExitCode) instead of rejecting its isolated nonzero output."
        Assert-Contains -Name "$ciWorkflowName $($channel.Name) failure channel annotation" -Content $channelResult.Output `
            -Expected '::error title=Blocking test failures::1 test project(s) failed across the blocking tiers.'

        $projectsVariable = $(if ($channel.Unit) { 'UNIT_TEST_PROJECTS' } else { 'INTEGRATION_TEST_PROJECTS' })
        $passingShardEnvironment = @{ $projectsVariable = 'tests/pass-first/Pass.First.csproj'; RUN_COVERAGE_GATE = 'false' }
        $passingShardBody = Get-StepRunBody -Block (Get-NamedStepBlock -Content $ciWorkflow -StepName $channel.StepName) -StepName $channel.StepName
        $passingShardResult = Invoke-WorkflowBashBody -Body $passingShardBody -Environment $passingShardEnvironment
        $passingShardCount = [regex]::Match($passingShardResult.StepOutput, '\Afailure-count=([0-9]+)\r?\n\z').Groups[1].Value
        Assert-Condition -Name "$ciWorkflowName $($channel.Name) single-tier shard" `
            -Condition ($passingShardResult.ExitCode -eq 0 -and $passingShardCount -ceq '0') `
            -Failure 'does not emit a successful single-tier shard output.'
        $mappedEnvironment = New-FailureGateEnvironment -TestPlatform $channel.Platform -UnitProjects $channel.Unit -IntegrationProjects $channel.Integration
        $outputMappings = [regex]::Matches($failureGateBlock, '(?m)^          (?<Variable>[A-Z_]+_FAILURE_COUNT): \$\{\{ steps\.(?<StepId>[a-z-]+)\.outputs\.failure-count \}\}\r?$')
        foreach ($mapping in $outputMappings) {
            $mappedEnvironment[$mapping.Groups['Variable'].Value] = $(if ($mapping.Groups['StepId'].Value -ceq $channel.StepId) { $passingShardCount } else { '' })
        }
        $mappedResult = Invoke-WorkflowBashBody -Body $failureGateBody -Environment $mappedEnvironment
        Assert-Condition -Name "$ciWorkflowName $($channel.Name) single-tier mapped output" -Condition ($mappedResult.ExitCode -eq 0) `
            -Failure "returned $($mappedResult.ExitCode) for a passing single tier resolved through the actual workflow mappings."
    }

    if (-not $isV2) {
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
}

$evidenceUploadBlock = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Upload test and coverage evidence'
Assert-Contains -Name "$ciWorkflowName evidence upload" -Content $evidenceUploadBlock -Expected 'if: always()'
Assert-Contains -Name "$ciWorkflowName evidence upload" -Content $evidenceUploadBlock -Expected 'name: blocking-test-results'
Assert-Contains -Name "$ciWorkflowName evidence upload" -Content $evidenceUploadBlock -Expected 'TestResults/**/*.trx'
Assert-Contains -Name "$ciWorkflowName evidence upload" -Content $evidenceUploadBlock -Expected 'TestResults/**/coverage.cobertura.xml'

if (-not $isV2) {
    Assert-Contains -Name "$ciWorkflowName" -Content $ciWorkflow -Expected '--filter-not-trait'
    Assert-Contains -Name "$ciWorkflowName" -Content $ciWorkflow -Expected '--filter-trait'
}
Assert-Contains -Name 'build-release.yml' -Content $buildReleaseWorkflow -Expected 'test-domain-workflow-test-platforms.ps1'

if ($isV2) {
    $isolatedGate = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Require isolated success before integration'
    $isolatedBody = Get-StepRunBody -Block $isolatedGate -StepName 'Require isolated success before integration'
    foreach ($scenario in @('passed', 'failed', 'missing-count', 'missing-suite', 'missing-manifest', 'missing-units', 'legacy')) {
        $environment = @{ MODULE_MANIFEST = 'module/manifest.json'; INTEGRATION_PROFILES = 'package'; UNIT_PROJECTS = 'isolated'; UNIT_FAILURE_COUNT = '0'; LEGACY_INTEGRATION = ''; LEGACY_ASPIRE = '' }
        switch ($scenario) {
            'failed' { $environment.UNIT_FAILURE_COUNT = '1' }
            'missing-count' { $environment.UNIT_FAILURE_COUNT = '' }
            'missing-suite' { $environment.INTEGRATION_PROFILES = '   ' }
            'missing-manifest' { $environment.MODULE_MANIFEST = '' }
            'missing-units' { $environment.UNIT_PROJECTS = '' }
            'legacy' { $environment.LEGACY_INTEGRATION = 'tests/direct' }
        }
        $result = Invoke-WorkflowBashBody -Body $isolatedBody -Environment $environment
        Assert-Condition -Name "Isolated provisioning gate $scenario" -Condition (($result.ExitCode -eq 0) -eq ($scenario -eq 'passed')) -Failure "returned $($result.ExitCode)."
    }
    $ownerRun = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Runner-owned package integration'
    $finalize = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Finalize exact CI owner'
    $integrationUpload = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Upload classified integration diagnostics'
    Assert-Contains -Name 'Package integration gate' -Content $ownerRun -Expected "steps.isolated-gate.outcome == 'success'"
    Assert-Contains -Name 'Exact recovery receipt' -Content $ownerRun -Expected '--receipt "$RUNNER_TEMP/hexalith-ci-owner.json"'
    Assert-Contains -Name 'Unconditional exact finalizer' -Content $finalize -Expected 'always()'
    Assert-Contains -Name 'Actual cancellation status' -Content $finalize -Expected 'CI_JOB_STATUS: ${{ job.status }}'
    Assert-Contains -Name 'Cancellation status reaches helper' -Content $finalize -Expected '--job-status "$CI_JOB_STATUS"'
    Assert-Contains -Name 'Finite finalizer' -Content $finalize -Expected 'timeout-minutes: 4'
    Assert-Contains -Name 'Classified diagnostic upload' -Content $integrationUpload -Expected 'always()'
    Assert-NotContains -Name 'Retired direct integration' -Content $ciWorkflow -Unexpected '- name: Integration tests (Tier 2'
    Assert-NotContains -Name 'Retired advisory AppHost job' -Content $ciWorkflow -Unexpected '  aspire-tests:'
    Assert-NotContains -Name 'Integration registry write credentials' -Content $ciWorkflow -Unexpected 'packages: write'
    $secretUses = @([regex]::Matches($ciWorkflow, '\$\{\{\s*secrets\.([a-zA-Z0-9_-]+)\s*\}\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    Assert-Condition -Name 'Only explicit pull-only secrets' -Condition ($secretUses.Count -eq 2 -and 'registry-pull-username' -cin $secretUses -and 'registry-pull-password' -cin $secretUses) -Failure 'requires exactly the dedicated pull-only username/password interface.'
    Assert-NotContains -Name 'No inherited credential inventory' -Content $ciWorkflow -Unexpected 'secrets: inherit'
    Assert-NotContains -Name 'No privileged PR credential event' -Content $ciWorkflow -Unexpected 'pull_request_target'

    Assert-NotContains -Name 'Integration advisory acceptance' -Content $ownerRun -Unexpected 'continue-on-error'
    Assert-Condition -Name 'Disposable owner runner' -Condition ($ciWorkflow.Contains('runs-on: ubuntu-latest')) -Failure 'requires a Linux GitHub-hosted runner.'
    Assert-Condition -Name 'Withdrawal finalizer order' -Condition ($ciWorkflow.IndexOf('Finalize exact CI owner') -gt $ciWorkflow.IndexOf('Runner-owned package integration') -and $ciWorkflow.IndexOf('Upload classified integration diagnostics') -gt $ciWorkflow.IndexOf('Finalize exact CI owner')) -Failure 'must finalize before uploading retained diagnostics.'


    $integrationBinaries = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Prepare integration binaries'
    $downloadCommands = @($integrationBinaries -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('curl ') })
    Assert-Condition -Name 'Expected integration downloads' -Condition ($downloadCommands.Count -eq 2) -Failure 'requires the reviewed Dapr CLI and runtime download commands.'
    foreach ($command in $downloadCommands) {
        Assert-Contains -Name 'HTTPS-only integration download and redirects' -Content $command -Expected "--proto '=https' --proto-redir '=https'"
    }

    $registryLogin = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Authenticate pull-only integration registry'
    $registryCleanup = Get-NamedStepBlock -Content $ciWorkflow -StepName 'Remove integration registry credentials'
    $registryLoginBody = Get-StepRunBody -Block $registryLogin -StepName 'Authenticate pull-only integration registry'
    $registryCleanupBody = Get-StepRunBody -Block $registryCleanup -StepName 'Remove integration registry credentials'
    Assert-Contains -Name 'Registry waits for isolated success' -Content $registryLogin -Expected "steps.isolated-gate.outcome == 'success'"
    Assert-Contains -Name 'Integration waits for registry authentication' -Content $ownerRun -Expected "steps.registry-login.outcome == 'success'"
    Assert-Contains -Name 'Registry password is stdin only' -Content $registryLoginBody -Expected '--password-stdin'
    Assert-Contains -Name 'Registry host is fixed' -Content $registryLoginBody -Expected 'docker login registry.hexalith.com'
    Assert-Contains -Name 'Credential deletion runs on cancellation or failure' -Content $registryCleanup -Expected 'always()'
    Assert-Condition -Name 'Credential teardown ordering' -Condition ($ciWorkflow.IndexOf('Remove integration registry credentials') -gt $ciWorkflow.IndexOf('Finalize exact CI owner') -and $ciWorkflow.IndexOf('Upload classified integration diagnostics') -gt $ciWorkflow.IndexOf('Remove integration registry credentials')) -Failure 'credentials must be removed after owner teardown and before artifact upload.'
    foreach ($name in @('registry-pull-username', 'registry-pull-password')) {
        Assert-Condition -Name "Required $name interface" -Condition ([regex]::IsMatch($ciWorkflow, "(?ms)^      ${name}:\r?\n(?:(?!^      [a-z]).)*?^        required: true$")) -Failure 'must be an explicit required workflow secret.'
    }

    $registryHarness = @'
set +e
unset DOCKER_CONFIG HEXALITH_CI_REGISTRY_CONFIG
export RUNNER_TEMP="$PWD/runner temp"
mkdir -p "$RUNNER_TEMP"
export GITHUB_ENV="$PWD/github-env"
: > "$GITHUB_ENV"
docker() {
  printf '%s\n' "$*" >> "$DOTNET_INVOCATIONS"
  [[ "$*" == 'login registry.hexalith.com --username=ci-reader --password-stdin' ]] || return 91
  supplied="$(cat)"
  [[ "$supplied" == 'fixture-pull-password' ]] || return 92
  [[ "$(stat -c '%a' "$DOCKER_CONFIG")" == '700' ]] || return 93
  [[ "$(dirname -- "$DOCKER_CONFIG")" == "$(realpath -e -- "$RUNNER_TEMP")" ]] || return 94
  printf '%s' 'private-fixture-credential' > "$DOCKER_CONFIG/config.json"
  if [[ "$AUTH_SCENARIO" == 'login-failure' ]]; then return 23; fi
}
'@
    $registryBetween = @'
login_status=$?
while IFS='=' read -r name value; do
  [[ -n "$name" ]] && export "$name=$value"
done < "$GITHUB_ENV"
if [[ "$login_status" -eq 0 ]]; then
  [[ -s "$DOCKER_CONFIG/config.json" ]] || exit 81
elif [[ -n "${HEXALITH_CI_REGISTRY_CONFIG:-}" && -e "$HEXALITH_CI_REGISTRY_CONFIG" ]]; then
  exit 82
fi
real_config="${HEXALITH_CI_REGISTRY_CONFIG:-}"
if [[ "$AUTH_SCENARIO" == 'foreign-path' ]]; then
  mkdir -p "$RUNNER_TEMP/foreign"
  printf '%s' 'preserve' > "$RUNNER_TEMP/foreign/sentinel"
  export HEXALITH_CI_REGISTRY_CONFIG="$RUNNER_TEMP/foreign"
fi
'@
    $registryAfter = @'
cleanup_status=$?
printf 'login-exit=%s\ncleanup-exit=%s\n' "$login_status" "$cleanup_status" >> "$GITHUB_OUTPUT"
if [[ "$AUTH_SCENARIO" == 'foreign-path' ]]; then
  [[ "$(cat "$RUNNER_TEMP/foreign/sentinel")" == 'preserve' ]] || exit 83
  printf 'foreign-preserved=true\n' >> "$GITHUB_OUTPUT"
  rm -rf -- "$real_config"
else
  [[ -z "$real_config" || ! -e "$real_config" ]] || exit 84
fi
count="$(find "$RUNNER_TEMP" -maxdepth 1 -name 'hexalith-ci-registry.*' | wc -l)"
printf 'credential-directories=%s\n' "$count" >> "$GITHUB_OUTPUT"
'@
    foreach ($scenario in @('success', 'login-failure', 'missing-username', 'missing-password', 'foreign-path')) {
        $environment = @{ AUTH_SCENARIO = $scenario; REGISTRY_PULL_USERNAME = 'ci-reader'; REGISTRY_PULL_PASSWORD = 'fixture-pull-password' }
        if ($scenario -eq 'missing-username') { $environment.REGISTRY_PULL_USERNAME = '' }
        if ($scenario -eq 'missing-password') { $environment.REGISTRY_PULL_PASSWORD = '' }
        $body = $registryHarness + "`n(`n" + $registryLoginBody + "`n)`n" + $registryBetween + "`n(`n" + $registryCleanupBody + "`n)`n" + $registryAfter
        $result = Invoke-WorkflowBashBody -Body $body -Environment $environment
        $loginExit = if ($scenario -eq 'login-failure') { 23 } elseif ($scenario.StartsWith('missing-')) { 1 } else { 0 }
        $cleanupExit = if ($scenario -eq 'foreign-path') { 1 } else { 0 }
        Assert-Condition -Name "Registry executable control $scenario" -Condition ($result.ExitCode -eq 0 -and $result.StepOutput.Contains("login-exit=$loginExit`n") -and $result.StepOutput.Contains("cleanup-exit=$cleanupExit`n") -and $result.StepOutput.Contains("credential-directories=0`n")) -Failure "failed temporary-config/stdin/cleanup assertions: $($result.Output) $($result.StepOutput)"
        Assert-NotContains -Name "Registry no plaintext output $scenario" -Content $result.Output -Unexpected 'fixture-pull-password'
        Assert-NotContains -Name "Registry no plaintext argument $scenario" -Content $result.Invocations -Unexpected 'fixture-pull-password'
        if ($scenario.StartsWith('missing-')) {
            Assert-Condition -Name "No login for absent secret $scenario" -Condition ([string]::IsNullOrWhiteSpace($result.Invocations)) -Failure 'must refuse before invoking Docker.'
        }
        if ($scenario -eq 'foreign-path') {
            Assert-Contains -Name 'Foreign credential path is preserved' -Content $result.StepOutput -Expected 'foreign-preserved=true'
        }
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Domain workflow test-platform checks failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine("Domain workflow $Contract test-platform checks passed: $checkCount assertions.")
