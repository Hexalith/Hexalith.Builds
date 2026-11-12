[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validatorPath = Join-Path $PSScriptRoot 'validate-central-package-versions.ps1'
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "central-package-validator-$([Guid]::NewGuid().ToString('N'))"
$failures = [System.Collections.Generic.List[string]]::new()
$scenarioCount = 0

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Content,

        [bool] $WithBom = $true
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($WithBom))
}

function ConvertTo-XmlAttributeValue {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Value
    )

    return $Value.Replace('&', '&amp;').Replace('"', '&quot;').Replace('<', '&lt;').Replace('>', '&gt;')
}

function New-CatalogFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Version
    )

    $path = Join-Path $temporaryRoot "$Name.props"
    $encodedVersion = ConvertTo-XmlAttributeValue -Value $Version
    $content = @"
<Project>
  <ItemGroup>
    <PackageVersion Include="Fixture.Package" Version="$encodedVersion" />
  </ItemGroup>
</Project>
"@
    Write-Utf8File -Path $path -Content $content
    return $path
}

function New-EvaluatorScript {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Output,

        [int] $ExitCode = 0,

        [string] $ErrorOutput = '',

        [int] $SleepSeconds = 0
    )

    $path = Join-Path $temporaryRoot "$Name-evaluator.ps1"
    $outputBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Output))
    $errorBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ErrorOutput))
    $content = @'
param([string] $CatalogPath)
$null = $CatalogPath
$output = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__OUTPUT_BASE64__'))
[Console]::Out.Write($output)
[Console]::Error.Write([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__ERROR_BASE64__')))
if (__SLEEP_SECONDS__ -gt 0) { Start-Sleep -Seconds __SLEEP_SECONDS__ }
exit __EXIT_CODE__
'@
    $content = $content.Replace('__OUTPUT_BASE64__', $outputBase64)
    $content = $content.Replace('__EXIT_CODE__', [string] $ExitCode)
    $content = $content.Replace('__ERROR_BASE64__', $errorBase64)
    $content = $content.Replace('__SLEEP_SECONDS__', [string] $SleepSeconds)
    Write-Utf8File -Path $path -Content "$content`n"
    return $path
}

function Invoke-Validator {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CatalogPath,

        [AllowEmptyString()]
        [string] $EvaluatorScriptPath = '',

        [int] $EvaluationTimeoutSeconds = 60,

        [switch] $EmitEvaluation
    )

    $arguments = @(
        '-NoLogo'
        '-NoProfile'
        '-File'
        $validatorPath
        '-CatalogPath'
        $CatalogPath
    )
    if (-not [string]::IsNullOrWhiteSpace($EvaluatorScriptPath)) {
        $arguments += @('-EvaluatorScriptPath', $EvaluatorScriptPath)
    }

    $arguments += @('-EvaluationTimeoutSeconds', [string] $EvaluationTimeoutSeconds)
    if ($EmitEvaluation) { $arguments += '-EmitEvaluation' }
    $output = @(& $pwshExecutable @arguments 2>&1)
    return [pscustomobject] @{
        ExitCode = $LASTEXITCODE
        Output = [string]::Join("`n", @($output | ForEach-Object { [string] $_ }))
    }
}

function Test-Scenario {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $CatalogPath,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedExitCode,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedOutput,

        [AllowEmptyString()]
        [string] $EvaluatorScriptPath = '',

        [int] $EvaluationTimeoutSeconds = 60,

        [switch] $EmitEvaluation
    )

    $script:scenarioCount++
    $result = Invoke-Validator -CatalogPath $CatalogPath -EvaluatorScriptPath $EvaluatorScriptPath `
        -EvaluationTimeoutSeconds $EvaluationTimeoutSeconds -EmitEvaluation:$EmitEvaluation
    if ($result.ExitCode -ne $ExpectedExitCode) {
        $script:failures.Add(
            "$Name expected exit code $ExpectedExitCode but received $($result.ExitCode). Output: $($result.Output)"
        )
        return
    }

    if ($result.Output -notlike "*$ExpectedOutput*") {
        $script:failures.Add("$Name output did not contain '$ExpectedOutput'. Output: $($result.Output)")
    }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    $stableCatalog = New-CatalogFixture -Name 'valid-stable' -Version '1.2.3'
    Test-Scenario -Name 'Valid stable version' -CatalogPath $stableCatalog -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 1 entries'

    $prereleaseCatalog = New-CatalogFixture -Name 'valid-prerelease' -Version '1.2.3-alpha.1+build.5'
    Test-Scenario -Name 'Valid prerelease version' -CatalogPath $prereleaseCatalog -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 1 entries'

    $fourPartCatalog = New-CatalogFixture -Name 'valid-four-part' -Version '10.28.0.143324'
    Test-Scenario -Name 'Valid four-part NuGet version' -CatalogPath $fourPartCatalog -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 1 entries'

    $bomFreeCatalog = Join-Path $temporaryRoot 'bom-free.props'
    Write-Utf8File -Path $bomFreeCatalog -Content (Get-Content -LiteralPath $stableCatalog -Raw) -WithBom $false
    Test-Scenario -Name 'BOM-free catalog' -CatalogPath $bomFreeCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'must begin with the UTF-8 BOM bytes EF BB BF'

    $truncatedCatalog = Join-Path $temporaryRoot 'truncated.props'
    [IO.File]::WriteAllBytes($truncatedCatalog, [byte[]] @(0xEF, 0xBB))
    Test-Scenario -Name 'Truncated BOM' -CatalogPath $truncatedCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'must begin with the UTF-8 BOM bytes EF BB BF'

    $invalidUtf8Catalog = Join-Path $temporaryRoot 'invalid-utf8.props'
    [IO.File]::WriteAllBytes($invalidUtf8Catalog, [byte[]] @(0xEF, 0xBB, 0xBF, 0xC3, 0x28))
    Test-Scenario -Name 'Invalid UTF-8 catalog' -CatalogPath $invalidUtf8Catalog -ExpectedExitCode 1 `
        -ExpectedOutput 'central catalog is not valid strict UTF-8'

    $lowerTagCatalog = New-CatalogFixture -Name 'lower-tag-prefix' -Version 'v1.16.3'
    Test-Scenario -Name 'Lowercase tag prefix' -CatalogPath $lowerTagCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'use the NuGet version without the v prefix'

    $upperTagCatalog = New-CatalogFixture -Name 'upper-tag-prefix' -Version 'V1.16.3'
    Test-Scenario -Name 'Uppercase tag prefix' -CatalogPath $upperTagCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'use the NuGet version without the v prefix'

    $blankCatalog = New-CatalogFixture -Name 'blank-version' -Version ''
    Test-Scenario -Name 'Blank version' -CatalogPath $blankCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'has a blank version'

    $blankIdentityCatalog = Join-Path $temporaryRoot 'blank-identity.props'
    Write-Utf8File -Path $blankIdentityCatalog -Content @'
<Project><ItemGroup><PackageVersion Include="" Version="1.2.3" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Blank identity' -CatalogPath $blankIdentityCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'must have a nonblank Include identity'

    $malformedCatalog = New-CatalogFixture -Name 'malformed-version' -Version '1..3'
    Test-Scenario -Name 'Malformed version' -CatalogPath $malformedCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'has malformed NuGet/SemVer version'

    $unresolvedJson = '{"Items":{"PackageVersion":[{"Identity":"Fixture.Package","Version":"$(MissingVersion)"}]}}'
    $unresolvedEvaluator = New-EvaluatorScript -Name 'unresolved-expression' -Output $unresolvedJson
    Test-Scenario -Name 'Unresolved MSBuild expression' -CatalogPath $stableCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'has unresolved MSBuild expression' -EvaluatorScriptPath $unresolvedEvaluator

    $malformedEvaluator = New-EvaluatorScript -Name 'malformed-json' -Output '{not-json'
    Test-Scenario -Name 'Malformed evaluator output' -CatalogPath $stableCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'returned malformed JSON' -EvaluatorScriptPath $malformedEvaluator

    $failedEvaluator = New-EvaluatorScript -Name 'failed-evaluation' -Output 'fixture evaluation failed' -ExitCode 17
    Test-Scenario -Name 'Failed evaluator' -CatalogPath $stableCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'catalog evaluation exited with code 17' -EvaluatorScriptPath $failedEvaluator

    $validJson = '{"Items":{"PackageVersion":[{"Identity":"Fixture.Package","Version":"1.2.3"}]}}'
    $warningEvaluator = New-EvaluatorScript -Name 'stderr-warning' -Output $validJson `
        -ErrorOutput 'MSBuild server fallback diagnostic'
    Test-Scenario -Name 'Zero-exit stderr cannot contaminate structured stdout' -CatalogPath $stableCatalog `
        -ExpectedExitCode 0 -ExpectedOutput 'Fixture.Package' -EvaluatorScriptPath $warningEvaluator -EmitEvaluation
    $structured = Invoke-Validator -CatalogPath $stableCatalog -EvaluatorScriptPath $warningEvaluator -EmitEvaluation
    try {
        $null = $structured.Output | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        $failures.Add('EmitEvaluation must remain JSON-only when a successful evaluator writes stderr.')
    }

    $failedValidEvaluator = New-EvaluatorScript -Name 'nonzero-valid-json' -Output $validJson `
        -ErrorOutput 'STDERR-FAILURE-MARKER' -ExitCode 17
    Test-Scenario -Name 'Valid stdout never hides nonzero exit' -CatalogPath $stableCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'catalog evaluation exited with code 17' -EvaluatorScriptPath $failedValidEvaluator
    Test-Scenario -Name 'Failed evaluator retains stderr diagnostics' -CatalogPath $stableCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'STDERR-FAILURE-MARKER' -EvaluatorScriptPath $failedValidEvaluator

    $misleadingEvaluator = New-EvaluatorScript -Name 'stderr-is-not-json' -Output '{not-json' -ErrorOutput $validJson
    Test-Scenario -Name 'Stderr cannot supply missing valid stdout' -CatalogPath $stableCatalog -ExpectedExitCode 1 `
        -ExpectedOutput 'returned malformed JSON' -EvaluatorScriptPath $misleadingEvaluator

    $largeErrorEvaluator = New-EvaluatorScript -Name 'bounded-stderr' -Output $validJson `
        -ErrorOutput (('x' * 65536) + 'STDERR-TAIL-MARKER') -ExitCode 19
    $largeError = Invoke-Validator -CatalogPath $stableCatalog -EvaluatorScriptPath $largeErrorEvaluator
    $scenarioCount++
    if ($largeError.ExitCode -ne 1 -or $largeError.Output.Length -gt 20000 `
        -or $largeError.Output -notlike '*STDERR-TAIL-MARKER*') {
        $failures.Add('Failed evaluator stderr diagnostics must retain their bounded tail.')
    }

    $largeOutputEvaluator = Join-Path $temporaryRoot 'stdout-overflow-evaluator.ps1'
    Write-Utf8File -Path $largeOutputEvaluator -Content "[Console]::Out.Write(('x' * (8 * 1024 * 1024 + 1)))"
    Test-Scenario -Name 'Oversized stdout fails instead of being silently truncated' -CatalogPath $stableCatalog `
        -ExpectedExitCode 1 -ExpectedOutput 'stdout exceeded the 8,388,608-character limit' -EvaluatorScriptPath $largeOutputEvaluator

    $slowEvaluator = New-EvaluatorScript -Name 'process-deadline' -Output $validJson -SleepSeconds 10
    $started = [Diagnostics.Stopwatch]::StartNew()
    Test-Scenario -Name 'Valid JSON before a hung evaluator is not success' -CatalogPath $stableCatalog `
        -ExpectedExitCode 1 -ExpectedOutput 'process deadline' -EvaluatorScriptPath $slowEvaluator -EvaluationTimeoutSeconds 1
    if ($started.Elapsed.TotalSeconds -gt 9) {
        $failures.Add('The evaluator deadline must terminate the process before its ten-second sleep completes.')
    }

    $duplicateJson = @'
{"Items":{"PackageVersion":[{"Identity":"Fixture.Package","Version":"1.2.3"},{"Identity":"fixture.package","Version":"1.2.3"}]}}
'@
    $duplicateEvaluator = New-EvaluatorScript -Name 'duplicate-package-id' -Output $duplicateJson
    Test-Scenario -Name 'Case-insensitive duplicate identity' -CatalogPath $stableCatalog -ExpectedExitCode 1 `
        -ExpectedOutput "duplicate package identity 'fixture.package'" -EvaluatorScriptPath $duplicateEvaluator

    $mismatchedJson = '{"Items":{"PackageVersion":[{"Identity":"Fixture.Package","Version":"9.9.9"}]}}'
    $mismatchedEvaluator = New-EvaluatorScript -Name 'mismatched-effective-version' -Output $mismatchedJson
    Test-Scenario -Name 'Mismatched effective catalog version' -CatalogPath $stableCatalog -ExpectedExitCode 1 `
        -ExpectedOutput "source version '1.2.3' evaluates to '9.9.9'" -EvaluatorScriptPath $mismatchedEvaluator
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Central package validator tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine("Central package validator tests passed: $scenarioCount scenarios.")
