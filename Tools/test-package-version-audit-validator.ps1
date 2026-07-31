[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validatorPath = Join-Path $PSScriptRoot 'validate-package-version-audit.ps1'
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "package-audit-validator-$([Guid]::NewGuid().ToString('N'))"
$failures = [System.Collections.Generic.List[string]]::new()
$scenarioCount = 0

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-AuditFixture {
    param([Parameter(Mandatory = $true)][string] $Name)

    $path = Join-Path $temporaryRoot "$Name.json"
    $audit = [ordered] @{
        schemaVersion = 1
        auditedAtUtc = '2026-07-31T12:00:00.0000000+00:00'
        generatedFromRevision = ('a' * 40)
        catalogPath = 'fixture.props'
        sources = @(
            [ordered] @{
                uri = 'https://api.nuget.org/v3/index.json'
                resolution = 'resolved'
                diagnostic = 'Fixture source resolved.'
            }
        )
        familyDecisions = @(
            [ordered] @{
                family = 'fixture-family'
                disposition = 'retained'
                rollbackGroup = 'fixture-family'
                packageIds = @('Fixture.One', 'Fixture.Two')
                rationale = 'Retained fixture family.'
                compatibilityEvidence = 'Fixture evidence.'
                removalTrigger = 'Re-run fixture validation.'
                representativeConsumers = @('Fixture.Consumer')
            }
        )
        packages = @(
            [ordered] @{
                id = 'Fixture.One'
                auditedVersion = '1.0.0'
                selectedVersion = '1.0.0'
                latestStable = '1.0.0'
                latestPrerelease = $null
                listingState = 'listed'
                family = 'fixture-family'
                disposition = 'retained'
                rollbackGroup = 'fixture-family'
                rationale = 'Current stable is retained.'
                evidence = 'Fixture evidence.'
                removalTrigger = 'Re-run fixture validation.'
                sourceResults = @(
                    [ordered] @{
                        source = 'https://api.nuget.org/v3/index.json'
                        listingState = 'listed'
                        latestStable = '1.0.0'
                        latestPrerelease = $null
                        diagnostic = 'Registration metadata resolved.'
                    }
                )
            },
            [ordered] @{
                id = 'Fixture.Two'
                auditedVersion = '2.0.0'
                selectedVersion = '2.0.0'
                latestStable = '2.1.0'
                latestPrerelease = '3.0.0-preview.1'
                listingState = 'unlisted'
                family = 'fixture-family'
                disposition = 'retained'
                rollbackGroup = 'fixture-family'
                rationale = 'Unlisted current version is retained without downgrade.'
                evidence = 'Fixture evidence.'
                removalTrigger = 'Re-run fixture validation.'
                sourceResults = @(
                    [ordered] @{
                        source = 'https://api.nuget.org/v3/index.json'
                        listingState = 'unlisted'
                        latestStable = '2.1.0'
                        latestPrerelease = '3.0.0-preview.1'
                        diagnostic = 'Registration metadata resolved.'
                    }
                )
            }
        )
    }

    Write-Utf8File -Path $path -Content ($audit | ConvertTo-Json -Depth 10)
    return $path
}

function Save-Audit {
    param(
        [Parameter(Mandatory = $true)] $Audit,
        [Parameter(Mandatory = $true)][string] $Path
    )

    Write-Utf8File -Path $Path -Content ($Audit | ConvertTo-Json -Depth 10)
}

function Test-Scenario {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $AuditPath,
        [Parameter(Mandatory = $true)][int] $ExpectedExitCode,
        [Parameter(Mandatory = $true)][string] $ExpectedOutput
    )

    $script:scenarioCount++
    $output = @(& $pwshExecutable -NoLogo -NoProfile -File $validatorPath `
            -AuditPath $AuditPath -CatalogPath $catalogPath -EvaluatorScriptPath $evaluatorPath 2>&1)
    $result = [string]::Join("`n", @($output | ForEach-Object { [string] $_ }))
    if ($LASTEXITCODE -ne $ExpectedExitCode) {
        $script:failures.Add("$Name expected exit code $ExpectedExitCode but received $LASTEXITCODE. Output: $result")
    }
    elseif ($result -notlike "*$ExpectedOutput*") {
        $script:failures.Add("$Name output did not contain '$ExpectedOutput'. Output: $result")
    }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    $catalogPath = Join-Path $temporaryRoot 'fixture.props'
    Write-Utf8File -Path $catalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Fixture.One" Version="1.0.0" /><PackageVersion Include="Fixture.Two" Version="2.0.0" /></ItemGroup></Project>
'@
    $evaluatorPath = Join-Path $temporaryRoot 'evaluate.ps1'
    Write-Utf8File -Path $evaluatorPath -Content @'
param([string] $CatalogPath)
$null = $CatalogPath
[Console]::Out.WriteLine('{"Items":{"PackageVersion":[{"Identity":"Fixture.One","Version":"1.0.0"},{"Identity":"Fixture.Two","Version":"2.0.0"}]}}')
'@

    $validPath = New-AuditFixture -Name 'valid'
    Test-Scenario -Name 'Complete audit' -AuditPath $validPath -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 2 packages, 1 families, and 1 source'

    $missingPath = New-AuditFixture -Name 'missing-package'
    $missingAudit = Get-Content -LiteralPath $missingPath -Raw | ConvertFrom-Json
    $missingAudit.packages = @($missingAudit.packages | Where-Object { $_.id -ne 'Fixture.Two' })
    $missingAudit.familyDecisions[0].packageIds = @('Fixture.One')
    Save-Audit -Audit $missingAudit -Path $missingPath
    Test-Scenario -Name 'Missing package evidence' -AuditPath $missingPath -ExpectedExitCode 1 `
        -ExpectedOutput "Evaluated catalog package 'Fixture.Two' has no audit evidence"

    $unsafePath = New-AuditFixture -Name 'unsafe-unlisted-update'
    $unsafeAudit = Get-Content -LiteralPath $unsafePath -Raw | ConvertFrom-Json
    $unsafeAudit.packages[0].disposition = 'accepted'
    $unsafeAudit.packages[1].disposition = 'accepted'
    $unsafeAudit.packages[1].selectedVersion = '2.1.0'
    $unsafeAudit.familyDecisions[0].disposition = 'accepted'
    Save-Audit -Audit $unsafeAudit -Path $unsafePath
    Test-Scenario -Name 'Unlisted package update' -AuditPath $unsafePath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.Two' is unlisted and must retain audited version '2.0.0'"

    $splitPath = New-AuditFixture -Name 'split-family'
    $splitAudit = Get-Content -LiteralPath $splitPath -Raw | ConvertFrom-Json
    $splitAudit.packages[0].disposition = 'accepted'
    Save-Audit -Audit $splitAudit -Path $splitPath
    Test-Scenario -Name 'Split family disposition' -AuditPath $splitPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' disposition does not match family 'fixture-family'"

    $exceptionPath = New-AuditFixture -Name 'incomplete-exception'
    $exceptionAudit = Get-Content -LiteralPath $exceptionPath -Raw | ConvertFrom-Json
    $exceptionAudit.packages[1].removalTrigger = ''
    Save-Audit -Audit $exceptionAudit -Path $exceptionPath
    Test-Scenario -Name 'Incomplete retained exception' -AuditPath $exceptionPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.Two' has a blank or missing 'removalTrigger'"

    $retainedUpdatePath = New-AuditFixture -Name 'retained-update'
    $retainedUpdateAudit = Get-Content -LiteralPath $retainedUpdatePath -Raw | ConvertFrom-Json
    $retainedUpdateAudit.packages[0].auditedVersion = '0.9.0'
    Save-Audit -Audit $retainedUpdateAudit -Path $retainedUpdatePath
    Test-Scenario -Name 'Retained package update' -AuditPath $retainedUpdatePath -ExpectedExitCode 1 `
        -ExpectedOutput "Retained package 'Fixture.One' must select audited version '0.9.0'"

    $downgradePath = New-AuditFixture -Name 'accepted-downgrade'
    $downgradeAudit = Get-Content -LiteralPath $downgradePath -Raw | ConvertFrom-Json
    $downgradeAudit.packages[0].auditedVersion = '1.1.0'
    $downgradeAudit.packages[0].disposition = 'accepted'
    $downgradeAudit.packages[1].listingState = 'listed'
    $downgradeAudit.packages[1].latestStable = '2.0.0'
    $downgradeAudit.packages[1].sourceResults[0].listingState = 'listed'
    $downgradeAudit.packages[1].sourceResults[0].latestStable = '2.0.0'
    $downgradeAudit.packages[1].disposition = 'accepted'
    $downgradeAudit.familyDecisions[0].disposition = 'accepted'
    Save-Audit -Audit $downgradeAudit -Path $downgradePath
    Test-Scenario -Name 'Accepted package downgrade' -AuditPath $downgradePath -ExpectedExitCode 1 `
        -ExpectedOutput "Accepted package 'Fixture.One' cannot downgrade audited version '1.1.0' to '1.0.0'"

    $prereleasePath = New-AuditFixture -Name 'stable-to-prerelease'
    $prereleaseAudit = Get-Content -LiteralPath $prereleasePath -Raw | ConvertFrom-Json
    $prereleaseAudit.packages[0].selectedVersion = '1.1.0-preview.1'
    $prereleaseAudit.packages[0].latestPrerelease = '1.1.0-preview.1'
    $prereleaseAudit.packages[0].disposition = 'accepted'
    $prereleaseAudit.packages[1].listingState = 'listed'
    $prereleaseAudit.packages[1].latestStable = '2.0.0'
    $prereleaseAudit.packages[1].sourceResults[0].listingState = 'listed'
    $prereleaseAudit.packages[1].sourceResults[0].latestStable = '2.0.0'
    $prereleaseAudit.packages[1].disposition = 'accepted'
    $prereleaseAudit.familyDecisions[0].disposition = 'accepted'
    Save-Audit -Audit $prereleaseAudit -Path $prereleasePath
    Test-Scenario -Name 'Stable package prerelease move' -AuditPath $prereleasePath -ExpectedExitCode 1 `
        -ExpectedOutput "Accepted stable package 'Fixture.One' cannot move to prerelease version '1.1.0-preview.1'"

    foreach ($workflowRelativePath in @('../.github/workflows/ci.yml', '../.github/workflows/build-release.yml')) {
        $script:scenarioCount++
        $workflowPath = Join-Path $PSScriptRoot $workflowRelativePath
        $workflow = Get-Content -LiteralPath $workflowPath -Raw
        $validateIndex = $workflow.IndexOf('- name: Validate package version audit', [StringComparison]::Ordinal)
        $testIndex = $workflow.IndexOf('- name: Test package version audit validator', [StringComparison]::Ordinal)
        $consumerIndex = $workflow.IndexOf('- name: Validate Builds consumer package authority', [StringComparison]::Ordinal)
        if ($validateIndex -lt 0 -or $testIndex -le $validateIndex -or $consumerIndex -le $testIndex) {
            $failures.Add("Workflow '$workflowRelativePath' must validate and test the package audit before consumer authority validation.")
        }
    }
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Package version audit validator tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine("Package version audit validator tests passed: $scenarioCount scenarios.")
