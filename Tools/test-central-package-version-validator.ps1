[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validatorPath = Join-Path $PSScriptRoot 'validate-central-package-versions.ps1'
$workflowPath = Join-Path $PSScriptRoot '../.github/workflows/build-release.yml'
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
        [string] $Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
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

        [int] $ExitCode = 0
    )

    $path = Join-Path $temporaryRoot "$Name-evaluator.ps1"
    $outputBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Output))
    $content = @'
param([string] $CatalogPath)
$null = $CatalogPath
$output = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__OUTPUT_BASE64__'))
[Console]::Out.Write($output)
exit __EXIT_CODE__
'@
    $content = $content.Replace('__OUTPUT_BASE64__', $outputBase64)
    $content = $content.Replace('__EXIT_CODE__', [string] $ExitCode)
    Write-Utf8File -Path $path -Content "$content`n"
    return $path
}

function Invoke-Validator {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CatalogPath,

        [AllowEmptyString()]
        [string] $EvaluatorScriptPath = ''
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
        [string] $EvaluatorScriptPath = ''
    )

    $script:scenarioCount++
    $result = Invoke-Validator -CatalogPath $CatalogPath -EvaluatorScriptPath $EvaluatorScriptPath
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

    $script:scenarioCount++
    $workflow = Get-Content -LiteralPath $workflowPath -Raw
    $validateIndex = $workflow.IndexOf('- name: Validate central package versions', [StringComparison]::Ordinal)
    $catalogContractIndex = $workflow.IndexOf('- name: Test authoritative package catalog', [StringComparison]::Ordinal)
    $testIndex = $workflow.IndexOf('- name: Test central package version validator', [StringComparison]::Ordinal)
    $consumerValidationIndex = $workflow.IndexOf('- name: Validate Builds consumer package authority', [StringComparison]::Ordinal)
    $consumerTestIndex = $workflow.IndexOf('- name: Test consumer package authority validator', [StringComparison]::Ordinal)
    $exceptionValidationIndex = $workflow.IndexOf('- name: Validate package version exception inventory', [StringComparison]::Ordinal)
    $exceptionTestIndex = $workflow.IndexOf('- name: Test package version exception validator', [StringComparison]::Ordinal)
    $daprIndex = $workflow.IndexOf('- name: Validate Dapr package versions', [StringComparison]::Ordinal)
    $releaseIndex = $workflow.IndexOf('- name: Create Release', [StringComparison]::Ordinal)
    if (
        $validateIndex -lt 0 -or
        $catalogContractIndex -le $validateIndex -or
        $testIndex -le $catalogContractIndex -or
        $consumerValidationIndex -le $testIndex -or
        $consumerTestIndex -le $consumerValidationIndex -or
        $exceptionValidationIndex -le $consumerTestIndex -or
        $exceptionTestIndex -le $exceptionValidationIndex -or
        $daprIndex -le $exceptionTestIndex -or
        $releaseIndex -le $daprIndex
    ) {
        $failures.Add('Release workflow must run all package-authority validations and tests before Dapr validation and Create Release.')
    }
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
