[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validatorPath = Join-Path $PSScriptRoot 'validate-dapr-package-versions.ps1'
$workflowPath = Join-Path $PSScriptRoot '../.github/workflows/build-release.yml'
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$requiredPackageIds = @(
    'Dapr.Client'
    'Dapr.AspNetCore'
    'Dapr.Actors.AspNetCore'
    'Dapr.Actors.Generators'
    'Dapr.Actors'
    'Dapr.Workflow'
)
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "dapr-package-validator-$([Guid]::NewGuid().ToString('N'))"
$failures = [System.Collections.Generic.List[string]]::new()
$scenarioCount = 0

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-RequiredPackageRows {
    param(
        [hashtable] $IdentityOverrides = @{},
        [hashtable] $VersionOverrides = @{},
        [string[]] $Omit = @(),
        [string[]] $AdditionalRows = @()
    )

    $rows = [System.Collections.Generic.List[string]]::new()
    foreach ($packageId in $requiredPackageIds) {
        if ($Omit -contains $packageId) {
            continue
        }

        $identity = if ($IdentityOverrides.ContainsKey($packageId)) { [string] $IdentityOverrides[$packageId] } else { $packageId }
        $version = if ($VersionOverrides.ContainsKey($packageId)) { [string] $VersionOverrides[$packageId] } else { '1.18.4' }
        $rows.Add("    <PackageVersion Include=`"$identity`" Version=`"$version`" />")
    }

    foreach ($row in $AdditionalRows) {
        $rows.Add($row)
    }

    return $rows.ToArray()
}

function New-CatalogFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string[]] $PackageRows,

        [string[]] $PropertyRows = @(),
        [string[]] $TrailingItemRows = @()
    )

    $path = Join-Path $temporaryRoot "$Name.props"
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<Project>')
    if ($PropertyRows.Count -gt 0) {
        $lines.Add('  <PropertyGroup>')
        foreach ($row in $PropertyRows) {
            $lines.Add($row)
        }

        $lines.Add('  </PropertyGroup>')
    }

    $lines.Add('  <ItemGroup>')
    foreach ($row in $PackageRows) {
        $lines.Add($row)
    }

    foreach ($row in $TrailingItemRows) {
        $lines.Add($row)
    }

    $lines.Add('  </ItemGroup>')
    $lines.Add('</Project>')
    Write-Utf8File -Path $path -Content "$([string]::Join("`n", $lines))`n"
    return $path
}

function Get-EvaluationJson {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Items
    )

    $packageItems = @(
        $Items | ForEach-Object {
            [ordered] @{
                Identity = $_.Identity
                Version = $_.Version
            }
        }
    )
    $model = [ordered] @{
        Items = [ordered] @{
            PackageVersion = [object[]] $packageItems
        }
    }
    return $model | ConvertTo-Json -Depth 10 -Compress
}

function Get-RequiredEvaluationItems {
    $items = [System.Collections.Generic.List[object]]::new()
    foreach ($packageId in $requiredPackageIds) {
        $items.Add([pscustomobject] @{
            Identity = $packageId
            Version = '1.18.4'
        })
    }

    return $items.ToArray()
}

function New-EvaluatorScript {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [AllowEmptyString()]
        [string] $StandardOutput = '',

        [AllowEmptyString()]
        [string] $StandardError = '',

        [int] $ExitCode = 0,
        [int] $SleepSeconds = 0,
        [switch] $SpawnStreamHolder
    )

    $path = Join-Path $temporaryRoot "$Name-evaluator.ps1"
    $stdoutBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($StandardOutput))
    $stderrBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($StandardError))
    $beforeOutput = ''
    if ($SleepSeconds -gt 0) {
        $beforeOutput = "Start-Sleep -Seconds $SleepSeconds"
    }
    elseif ($SpawnStreamHolder) {
        $beforeOutput = @'
$holderArguments = @('-NoLogo', '-NoProfile', '-Command', 'Start-Sleep -Seconds 4')
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$null = Start-Process -FilePath $pwshExecutable -ArgumentList $holderArguments
'@
    }

    $content = @'
param([string] $CatalogPath)
$null = $CatalogPath
__BEFORE_OUTPUT__
$stdout = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__STDOUT_BASE64__'))
$stderr = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__STDERR_BASE64__'))
if ($stdout.Length -gt 0) {
    [Console]::Out.Write($stdout)
}
if ($stderr.Length -gt 0) {
    [Console]::Error.Write($stderr)
}
exit __EXIT_CODE__
'@
    $content = $content.Replace('__BEFORE_OUTPUT__', $beforeOutput)
    $content = $content.Replace('__STDOUT_BASE64__', $stdoutBase64)
    $content = $content.Replace('__STDERR_BASE64__', $stderrBase64)
    $content = $content.Replace('__EXIT_CODE__', [string] $ExitCode)
    Write-Utf8File -Path $path -Content "$content`n"
    return $path
}

function New-ProcessTreeTimeoutEvaluatorScript {
    param(
        [Parameter(Mandatory = $true)]
        [string] $EvaluatorPidPath,

        [Parameter(Mandatory = $true)]
        [string] $ChildPidPath
    )

    $path = Join-Path $temporaryRoot 'process-tree-timeout-evaluator.ps1'
    $evaluatorPidPathBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($EvaluatorPidPath))
    $childPidPathBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ChildPidPath))
    $content = @'
param([string] $CatalogPath)
$null = $CatalogPath
$evaluatorPidPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__EVALUATOR_PID_PATH__'))
$childPidPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__CHILD_PID_PATH__'))
[IO.File]::WriteAllText($evaluatorPidPath, [string] $PID)
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$childArguments = @('-NoLogo', '-NoProfile', '-Command', 'Start-Sleep -Seconds 30')
$child = Start-Process -FilePath $pwshExecutable -ArgumentList $childArguments -PassThru
[IO.File]::WriteAllText($childPidPath, [string] $child.Id)
Start-Sleep -Seconds 30
'@
    $content = $content.Replace('__EVALUATOR_PID_PATH__', $evaluatorPidPathBase64)
    $content = $content.Replace('__CHILD_PID_PATH__', $childPidPathBase64)
    Write-Utf8File -Path $path -Content "$content`n"
    return $path
}

function Wait-ForProcessGone {
    param(
        [Parameter(Mandatory = $true)]
        [int] $ProcessId,

        [Parameter(Mandatory = $true)]
        [int] $TimeoutMilliseconds
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            return $true
        }

        Start-Sleep -Milliseconds 100
    }

    return $null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

function Get-CompletedTaskText {
    param(
        [Parameter(Mandatory = $true)]
        [System.Threading.Tasks.Task[string]] $Task
    )

    if ($Task.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
        return [string] $Task.Result
    }

    return ''
}

function Invoke-Validator {
    param(
        [AllowNull()]
        [string] $CatalogPath,

        [AllowNull()]
        [string] $EvaluatorScriptPath,

        [int] $EvaluationTimeoutSeconds = 30,
        [int] $StreamDrainTimeoutSeconds = 3,
        [int] $ParentTimeoutSeconds = 45
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwshExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add('-NoLogo')
    $startInfo.ArgumentList.Add('-NoProfile')
    $startInfo.ArgumentList.Add('-File')
    $startInfo.ArgumentList.Add($validatorPath)
    if (-not [string]::IsNullOrWhiteSpace($CatalogPath)) {
        $startInfo.ArgumentList.Add('-CatalogPath')
        $startInfo.ArgumentList.Add($CatalogPath)
    }

    $startInfo.ArgumentList.Add('-EvaluationTimeoutSeconds')
    $startInfo.ArgumentList.Add([string] $EvaluationTimeoutSeconds)
    $startInfo.ArgumentList.Add('-StreamDrainTimeoutSeconds')
    $startInfo.ArgumentList.Add([string] $StreamDrainTimeoutSeconds)
    if (-not [string]::IsNullOrWhiteSpace($EvaluatorScriptPath)) {
        $startInfo.ArgumentList.Add('-EvaluatorScriptPath')
        $startInfo.ArgumentList.Add($EvaluatorScriptPath)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdoutTask = $null
    $stderrTask = $null
    try {
        if (-not $process.Start()) {
            throw 'The validator process did not start.'
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($ParentTimeoutSeconds * 1000)) {
            try {
                $process.Kill($true)
            }
            catch {
                # A concurrent exit is harmless; cleanup remains bounded.
            }

            $null = $process.WaitForExit(3000)
            $combinedAfterKill = [Threading.Tasks.Task]::WhenAll(
                [Threading.Tasks.Task[]] @($stdoutTask, $stderrTask)
            )
            try {
                $null = $combinedAfterKill.Wait(3000)
            }
            catch {
                # The timeout result below remains the actionable failure.
            }

            throw "Validator fixture exceeded its $ParentTimeoutSeconds-second parent timeout."
        }

        $combinedTask = [Threading.Tasks.Task]::WhenAll(
            [Threading.Tasks.Task[]] @($stdoutTask, $stderrTask)
        )
        try {
            $drained = $combinedTask.Wait(5000)
        }
        catch {
            throw "Reading validator output failed: $($_.Exception.GetBaseException().Message)"
        }

        if (-not $drained) {
            throw 'Validator output did not drain within the bounded fixture deadline.'
        }

        return [pscustomobject] @{
            ExitCode = $process.ExitCode
            StandardOutput = Get-CompletedTaskText -Task $stdoutTask
            StandardError = Get-CompletedTaskText -Task $stderrTask
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Expected,

        [Parameter(Mandatory = $true)]
        [object] $Actual,

        [Parameter(Mandatory = $true)]
        [string] $Because
    )

    if (-not [object]::Equals($Expected, $Actual)) {
        throw "$Because Expected '$Expected', actual '$Actual'."
    }
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Actual,

        [Parameter(Mandatory = $true)]
        [string] $Expected,

        [Parameter(Mandatory = $true)]
        [string] $Because
    )

    if ($Actual.IndexOf($Expected, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "$Because Expected output to contain '$Expected'. Actual output:`n$Actual"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Actual,

        [Parameter(Mandatory = $true)]
        [string] $Unexpected,

        [Parameter(Mandatory = $true)]
        [string] $Because
    )

    if ($Actual.IndexOf($Unexpected, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "$Because Output unexpectedly contained '$Unexpected'."
    }
}

function Assert-ValidatorResult {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Result,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedExitCode,

        [string[]] $ExpectedOutput = @()
    )

    $combinedOutput = "$($Result.StandardOutput)`n$($Result.StandardError)"
    Assert-Equal -Expected $ExpectedExitCode -Actual $Result.ExitCode -Because 'Validator exit code differed.'
    foreach ($expectedText in $ExpectedOutput) {
        Assert-Contains -Actual $combinedOutput -Expected $expectedText -Because 'Validator diagnostic differed.'
    }
}

function Invoke-Scenario {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [scriptblock] $Body
    )

    $script:scenarioCount++
    try {
        & $Body
        Write-Host "[PASS] $Name"
    }
    catch {
        $message = $_.Exception.GetBaseException().Message
        $script:failures.Add("$Name -- $message")
        Write-Host "[FAIL] $Name -- $message"
    }
}

try {
    if (-not (Test-Path -LiteralPath $validatorPath -PathType Leaf)) {
        throw "Validator not found: $validatorPath"
    }

    $null = New-Item -ItemType Directory -Path $temporaryRoot

    Invoke-Scenario -Name 'canonical catalog uses the validator production default' -Body {
        $result = Invoke-Validator -CatalogPath $null -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 0 -ExpectedOutput @('validation passed')
    }

    Invoke-Scenario -Name 'imported PackageVersion items are evaluated' -Body {
        $importedPath = New-CatalogFixture -Name 'imported-items' -PackageRows (Get-RequiredPackageRows)
        $catalogPath = Join-Path $temporaryRoot 'imports.props'
        Write-Utf8File -Path $catalogPath -Content "<Project>`n  <Import Project=`"$([IO.Path]::GetFileName($importedPath))`" />`n</Project>`n"
        $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 0 -ExpectedOutput @('validation passed')
    }

    Invoke-Scenario -Name 'mixed-case required identity uses NuGet identity semantics' -Body {
        $rows = Get-RequiredPackageRows -IdentityOverrides @{ 'Dapr.Client' = 'dApR.cLiEnT' }
        $catalogPath = New-CatalogFixture -Name 'mixed-case-required' -PackageRows $rows
        $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 0 -ExpectedOutput @('validation passed')
    }

    Invoke-Scenario -Name 'aligned additional Dapr family item is allowed' -Body {
        $rows = Get-RequiredPackageRows -AdditionalRows @('    <PackageVersion Include="Dapr.Extensions.Configuration" Version="1.18.4" />')
        $catalogPath = New-CatalogFixture -Name 'aligned-extra' -PackageRows $rows
        $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 0 -ExpectedOutput @('7 unique Dapr.* package IDs')
    }

    Invoke-Scenario -Name 'mismatched additional Dapr family item is rejected' -Body {
        $rows = Get-RequiredPackageRows -AdditionalRows @('    <PackageVersion Include="Dapr.Extensions.Configuration" Version="1.17.9" />')
        $catalogPath = New-CatalogFixture -Name 'mismatched-extra' -PackageRows $rows
        $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('every evaluated Dapr.* package', 'Dapr.Extensions.Configuration=1.17.9')
    }

    Invoke-Scenario -Name 'case-insensitive duplicate SDK identity is rejected' -Body {
        $items = [System.Collections.Generic.List[object]]::new()
        foreach ($item in (Get-RequiredEvaluationItems)) {
            $items.Add($item)
        }

        $items.Add([pscustomobject] @{ Identity = 'dApR.cLiEnT'; Version = '1.18.4' })
        $json = Get-EvaluationJson -Items $items.ToArray()
        $evaluatorPath = New-EvaluatorScript -Name 'duplicate' -StandardOutput $json
        $result = Invoke-Validator -CatalogPath $validatorPath -EvaluatorScriptPath $evaluatorPath
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('duplicate Dapr.* PackageVersion identities', 'Dapr.Client / dApR.cLiEnT')
    }

    Invoke-Scenario -Name 'property-expanded versions are evaluated' -Body {
        $rows = Get-RequiredPackageRows -VersionOverrides @{
            'Dapr.Client' = '$(DaprSdkVersion)'
            'Dapr.AspNetCore' = '$(DaprSdkVersion)'
            'Dapr.Actors.AspNetCore' = '$(DaprSdkVersion)'
            'Dapr.Actors.Generators' = '$(DaprSdkVersion)'
            'Dapr.Actors' = '$(DaprSdkVersion)'
            'Dapr.Workflow' = '$(DaprSdkVersion)'
        }
        $catalogPath = New-CatalogFixture `
            -Name 'property-expanded' `
            -PackageRows $rows `
            -PropertyRows @('    <DaprSdkVersion>1.18.4</DaprSdkVersion>')
        $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 0 -ExpectedOutput @('validation passed')
    }

    Invoke-Scenario -Name 'inactive conditional items are ignored' -Body {
        $rows = Get-RequiredPackageRows -AdditionalRows @(
            '    <PackageVersion Include="Dapr" Version="1.17.9" Condition="''$(EnableInvalidItems)'' == ''true''" />',
            '    <PackageVersion Include="Dapr.Inactive" Version="1.17.9" Condition="''$(EnableInvalidItems)'' == ''true''" />'
        )
        $catalogPath = New-CatalogFixture -Name 'inactive-conditions' -PackageRows $rows
        $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 0 -ExpectedOutput @('validation passed')
    }

    Invoke-Scenario -Name 'case-variant bare Dapr ID is rejected' -Body {
        $rows = Get-RequiredPackageRows -AdditionalRows @('    <PackageVersion Include="dApR" Version="1.18.4" />')
        $catalogPath = New-CatalogFixture -Name 'bare-dapr' -PackageRows $rows
        $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('invalid bare package ID')
    }

    Invoke-Scenario -Name 'catalog with no Dapr family is rejected' -Body {
        $catalogPath = New-CatalogFixture -Name 'no-family' -PackageRows @('    <PackageVersion Include="Example.Package" Version="1.0.0" />')
        $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('no Dapr.* PackageVersion items')
    }

    Invoke-Scenario -Name 'Update-driven required package mismatch is rejected' -Body {
        $catalogPath = New-CatalogFixture `
            -Name 'update-mismatch' `
            -PackageRows (Get-RequiredPackageRows) `
            -TrailingItemRows @('    <PackageVersion Update="Dapr.Client" Version="1.17.9" />')
        $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('Dapr.Client=1.17.9')
    }

    foreach ($omittedPackageId in $requiredPackageIds) {
        $capturedPackageId = $omittedPackageId
        Invoke-Scenario -Name "missing required SDK ID $capturedPackageId is rejected" -Body {
            $safeName = $capturedPackageId.Replace('.', '-').ToLowerInvariant()
            $catalogPath = New-CatalogFixture -Name "missing-$safeName" -PackageRows (Get-RequiredPackageRows -Omit @($capturedPackageId))
            $result = Invoke-Validator -CatalogPath $catalogPath -EvaluatorScriptPath $null
            Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('required shared Dapr SDK package IDs are missing', $capturedPackageId)
        }.GetNewClosure()
    }

    $validJson = Get-EvaluationJson -Items (Get-RequiredEvaluationItems)

    Invoke-Scenario -Name 'benign stderr does not corrupt valid stdout JSON' -Body {
        $evaluatorPath = New-EvaluatorScript `
            -Name 'benign-stderr' `
            -StandardOutput $validJson `
            -StandardError 'benign evaluator warning'
        $result = Invoke-Validator -CatalogPath $validatorPath -EvaluatorScriptPath $evaluatorPath
        Assert-ValidatorResult -Result $result -ExpectedExitCode 0 -ExpectedOutput @('validation passed')
        Assert-NotContains `
            -Actual $result.StandardOutput `
            -Unexpected 'benign evaluator warning' `
            -Because 'Evaluator stderr must never be parsed or forwarded as validator stdout.'
    }

    Invoke-Scenario -Name 'evaluator timeout is controlled and bounded' -Body {
        $evaluatorPath = New-EvaluatorScript -Name 'timeout' -SleepSeconds 30
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-Validator `
            -CatalogPath $validatorPath `
            -EvaluatorScriptPath $evaluatorPath `
            -EvaluationTimeoutSeconds 1 `
            -StreamDrainTimeoutSeconds 1 `
            -ParentTimeoutSeconds 8
        $stopwatch.Stop()
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('timed out after 1 seconds', 'Process-tree termination was attempted', 'Evaluator exit was confirmed')
        if ($stopwatch.Elapsed.TotalSeconds -ge 6) {
            throw "The 1-second evaluator timeout took $($stopwatch.Elapsed.TotalSeconds) seconds, so it was not meaningfully bounded."
        }
    }

    Invoke-Scenario -Name 'evaluator timeout terminates its long-lived process tree' -Body {
        $evaluatorPidPath = Join-Path $temporaryRoot 'timeout-evaluator.pid'
        $childPidPath = Join-Path $temporaryRoot 'timeout-child.pid'
        $evaluatorPath = New-ProcessTreeTimeoutEvaluatorScript `
            -EvaluatorPidPath $evaluatorPidPath `
            -ChildPidPath $childPidPath
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-Validator `
            -CatalogPath $validatorPath `
            -EvaluatorScriptPath $evaluatorPath `
            -EvaluationTimeoutSeconds 1 `
            -StreamDrainTimeoutSeconds 1 `
            -ParentTimeoutSeconds 8
        $stopwatch.Stop()
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('timed out after 1 seconds', 'Process-tree termination was attempted', 'Evaluator exit was confirmed')
        if ($stopwatch.Elapsed.TotalSeconds -ge 6) {
            throw "The process-tree timeout took $($stopwatch.Elapsed.TotalSeconds) seconds, so it was not meaningfully bounded."
        }

        if (-not (Test-Path -LiteralPath $evaluatorPidPath -PathType Leaf)) {
            throw 'The timeout evaluator did not record its PID.'
        }

        if (-not (Test-Path -LiteralPath $childPidPath -PathType Leaf)) {
            throw 'The timeout evaluator did not record its child PID.'
        }

        $evaluatorProcessId = [int] (Get-Content -LiteralPath $evaluatorPidPath -Raw)
        $childProcessId = [int] (Get-Content -LiteralPath $childPidPath -Raw)
        if (-not (Wait-ForProcessGone -ProcessId $evaluatorProcessId -TimeoutMilliseconds 3000)) {
            throw "Evaluator process $evaluatorProcessId survived process-tree cleanup."
        }

        if (-not (Wait-ForProcessGone -ProcessId $childProcessId -TimeoutMilliseconds 3000)) {
            throw "Evaluator child process $childProcessId survived process-tree cleanup."
        }
    }

    Invoke-Scenario -Name 'nonzero evaluator exit preserves useful output tail' -Body {
        $tailDiagnostic = ('x' * 5000) + ' EVAL-TAIL-MARKER'
        $evaluatorPath = New-EvaluatorScript `
            -Name 'nonzero' `
            -StandardError $tailDiagnostic `
            -ExitCode 23
        $result = Invoke-Validator -CatalogPath $validatorPath -EvaluatorScriptPath $evaluatorPath
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('exited with code 23', 'stderr tail', 'EVAL-TAIL-MARKER')
    }

    Invoke-Scenario -Name 'nonzero evaluator drains large stdout and stderr without deadlock' -Body {
        $largeStandardOutput = ('o' * 262144) + ' LARGE-STDOUT-TAIL-MARKER'
        $largeStandardError = ('e' * 262144) + ' LARGE-STDERR-TAIL-MARKER'
        $evaluatorPath = New-EvaluatorScript `
            -Name 'large-dual-streams' `
            -StandardOutput $largeStandardOutput `
            -StandardError $largeStandardError `
            -ExitCode 29
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-Validator `
            -CatalogPath $validatorPath `
            -EvaluatorScriptPath $evaluatorPath `
            -ParentTimeoutSeconds 12
        $stopwatch.Stop()
        Assert-ValidatorResult `
            -Result $result `
            -ExpectedExitCode 1 `
            -ExpectedOutput @('exited with code 29', 'stdout tail', 'LARGE-STDOUT-TAIL-MARKER', 'stderr tail', 'LARGE-STDERR-TAIL-MARKER')
        if ($stopwatch.Elapsed.TotalSeconds -ge 10) {
            throw "Large dual-stream evaluation took $($stopwatch.Elapsed.TotalSeconds) seconds and may have deadlocked."
        }
    }

    Invoke-Scenario -Name 'empty evaluator stdout is rejected' -Body {
        $evaluatorPath = New-EvaluatorScript -Name 'empty-stdout' -StandardError 'EMPTY-STDERR-TAIL-MARKER'
        $result = Invoke-Validator -CatalogPath $validatorPath -EvaluatorScriptPath $evaluatorPath
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('produced no JSON on stdout', 'stderr tail', 'EMPTY-STDERR-TAIL-MARKER')
    }

    Invoke-Scenario -Name 'malformed evaluator JSON is rejected' -Body {
        $evaluatorPath = New-EvaluatorScript `
            -Name 'malformed-json' `
            -StandardOutput '{not-json MALFORMED-STDOUT-TAIL-MARKER' `
            -StandardError 'MALFORMED-STDERR-TAIL-MARKER'
        $result = Invoke-Validator -CatalogPath $validatorPath -EvaluatorScriptPath $evaluatorPath
        Assert-ValidatorResult `
            -Result $result `
            -ExpectedExitCode 1 `
            -ExpectedOutput @('produced malformed JSON on stdout', 'stdout tail', 'MALFORMED-STDOUT-TAIL-MARKER', 'stderr tail', 'MALFORMED-STDERR-TAIL-MARKER')
    }

    Invoke-Scenario -Name 'missing evaluator JSON schema object is rejected' -Body {
        $evaluatorPath = New-EvaluatorScript -Name 'missing-schema' -StandardOutput '{"unexpected":true}'
        $result = Invoke-Validator -CatalogPath $validatorPath -EvaluatorScriptPath $evaluatorPath
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @("requires an 'Items' object")
    }

    Invoke-Scenario -Name 'blank evaluator identity violates JSON schema' -Body {
        $json = '{"Items":{"PackageVersion":[{"Identity":" ","Version":"1.18.4"}]}}'
        $evaluatorPath = New-EvaluatorScript -Name 'blank-identity' -StandardOutput $json
        $result = Invoke-Validator -CatalogPath $validatorPath -EvaluatorScriptPath $evaluatorPath
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('Identity to be a nonblank string')
    }

    Invoke-Scenario -Name 'blank evaluator version violates JSON schema' -Body {
        $json = '{"Items":{"PackageVersion":[{"Identity":"Dapr.Client","Version":""}]}}'
        $evaluatorPath = New-EvaluatorScript -Name 'blank-version' -StandardOutput $json
        $result = Invoke-Validator -CatalogPath $validatorPath -EvaluatorScriptPath $evaluatorPath
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('Version to be a nonblank string')
    }

    Invoke-Scenario -Name 'redirected stream drain timeout is controlled and bounded' -Body {
        $evaluatorPath = New-EvaluatorScript `
            -Name 'stream-holder' `
            -StandardOutput $validJson `
            -SpawnStreamHolder
        $result = Invoke-Validator `
            -CatalogPath $validatorPath `
            -EvaluatorScriptPath $evaluatorPath `
            -StreamDrainTimeoutSeconds 1 `
            -ParentTimeoutSeconds 8
        Assert-ValidatorResult -Result $result -ExpectedExitCode 1 -ExpectedOutput @('redirected evaluator output did not drain within 1 seconds', 'No unbounded wait')
    }

    Invoke-Scenario -Name 'release workflow orders initializer and both gates before release' -Body {
        $workflow = Get-Content -LiteralPath $workflowPath -Raw
        Assert-NotContains -Actual $workflow -Unexpected 'timeout-minutes' -Because 'The release job must not gain a broad timeout.'
        Assert-Contains -Actual $workflow -Expected 'uses: ./Github/initialize-dotnet' -Because 'The repository-owned initializer must supply the SDK.'
        Assert-NotContains -Actual $workflow -Unexpected 'dotnet-version:' -Because 'The initializer default must remain the single SDK-version source.'

        $checkoutIndex = $workflow.IndexOf('uses: actions/checkout@', [StringComparison]::Ordinal)
        $initializerIndex = $workflow.IndexOf('uses: ./Github/initialize-dotnet', [StringComparison]::Ordinal)
        $validatorIndex = $workflow.IndexOf('pwsh -NoProfile -File ./Tools/validate-dapr-package-versions.ps1', [StringComparison]::Ordinal)
        $testsIndex = $workflow.IndexOf('pwsh -NoProfile -File ./Tools/test-dapr-package-version-validator.ps1', [StringComparison]::Ordinal)
        $releaseIndex = $workflow.IndexOf('uses: ./Github/create-release', [StringComparison]::Ordinal)
        if (
            $checkoutIndex -lt 0 -or
            $initializerIndex -le $checkoutIndex -or
            $validatorIndex -le $initializerIndex -or
            $testsIndex -le $validatorIndex -or
            $releaseIndex -le $testsIndex
        ) {
            throw 'Workflow steps are not ordered checkout -> initializer -> validator -> tests -> release.'
        }
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Dapr validator fixture suite failed: $($failures.Count) of $scenarioCount scenarios failed.")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

Write-Output "Dapr validator fixture suite passed: $scenarioCount scenarios."
