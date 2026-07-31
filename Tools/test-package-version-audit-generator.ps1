[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$generatorPath = Join-Path $PSScriptRoot 'audit-central-package-versions.ps1'
$validatorPath = Join-Path $PSScriptRoot 'validate-package-version-audit.ps1'
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "package-audit-generator-$([Guid]::NewGuid().ToString('N'))"
$failures = [System.Collections.Generic.List[string]]::new()
$scenarioCount = 0

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Content
    )

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Add-FixtureResponse {
    param(
        [Parameter(Mandatory = $true)] $Responses,
        [Parameter(Mandatory = $true)][string] $Uri,
        [Parameter(Mandatory = $true)] $Response
    )

    $Responses[$Uri] = [ordered] @{ response = $Response }
}

function Add-FixtureError {
    param(
        [Parameter(Mandatory = $true)] $Responses,
        [Parameter(Mandatory = $true)][string] $Uri,
        [Parameter(Mandatory = $true)][string] $ErrorMessage
    )

    $Responses[$Uri] = [ordered] @{ error = $ErrorMessage }
}

function New-RegistrationLeaf {
    param(
        [Parameter(Mandatory = $true)][string] $Version,
        [Parameter(Mandatory = $true)][bool] $Listed
    )

    return [ordered] @{
        catalogEntry = [ordered] @{
            version = $Version
            listed = $Listed
        }
    }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)][string] $Scenario,
        [AllowNull()] $Expected,
        [AllowNull()] $Actual
    )

    $script:scenarioCount++
    if ([string] $Expected -cne [string] $Actual) {
        $script:failures.Add("$Scenario expected '$Expected' but received '$Actual'.")
    }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    $catalogPath = Join-Path $temporaryRoot 'fixture.props'
    Write-Utf8File -Path $catalogPath -Content @'
<Project>
  <ItemGroup>
    <PackageVersion Include="Fixture.Listed" Version="1.0.0" />
    <PackageVersion Include="Fixture.Missing" Version="3.0.0" />
    <PackageVersion Include="Fixture.Unlisted" Version="2.0.0" />
  </ItemGroup>
</Project>
'@

    $sourceOne = 'https://fixture.test/v3/index.json'
    $sourceTwo = 'https://unavailable.test/v3/index.json'
    $registrationBase = 'https://fixture.test/registration'
    $flatBase = 'https://fixture.test/flat'
    $responses = [ordered] @{}
    Add-FixtureResponse -Responses $responses -Uri $sourceOne -Response ([ordered] @{
            resources = @(
                [ordered] @{ '@id' = $registrationBase; '@type' = 'RegistrationsBaseUrl/3.6.0' },
                [ordered] @{ '@id' = $flatBase; '@type' = 'PackageBaseAddress/3.0.0' }
            )
        })
    Add-FixtureError -Responses $responses -Uri $sourceTwo -ErrorMessage 'Fixture source is unavailable.'

    $listedPageUri = "$registrationBase/fixture.listed/page.json"
    Add-FixtureResponse -Responses $responses -Uri "$registrationBase/fixture.listed/index.json" -Response ([ordered] @{
            items = @([ordered] @{ '@id' = $listedPageUri })
        })
    Add-FixtureResponse -Responses $responses -Uri $listedPageUri -Response ([ordered] @{
            items = @(
                (New-RegistrationLeaf -Version '1.0.0' -Listed $true),
                (New-RegistrationLeaf -Version '1.9.0' -Listed $true),
                (New-RegistrationLeaf -Version '1.10.0' -Listed $true),
                (New-RegistrationLeaf -Version '2.0.0-beta.999999999999999999999999999999' -Listed $true),
                (New-RegistrationLeaf -Version '2.0.0-beta.1000000000000000000000000000000' -Listed $true)
            )
        })

    Add-FixtureResponse -Responses $responses -Uri "$registrationBase/fixture.unlisted/index.json" -Response ([ordered] @{
            items = @([ordered] @{
                    '@id' = "$registrationBase/fixture.unlisted/page.json"
                    items = @(
                        (New-RegistrationLeaf -Version '1.9.0' -Listed $true),
                        (New-RegistrationLeaf -Version '2.0.0' -Listed $false),
                        (New-RegistrationLeaf -Version '2.1.0' -Listed $true)
                    )
                })
        })

    Add-FixtureResponse -Responses $responses -Uri "$registrationBase/fixture.missing/index.json" -Response ([ordered] @{
            items = @([ordered] @{
                    '@id' = "$registrationBase/fixture.missing/page.json"
                    items = @((New-RegistrationLeaf -Version '2.9.0' -Listed $true))
                })
        })
    Add-FixtureResponse -Responses $responses -Uri "$flatBase/fixture.missing/index.json" -Response ([ordered] @{
            versions = @('2.9.0')
        })

    $fixturePath = Join-Path $temporaryRoot 'requests.json'
    Write-Utf8File -Path $fixturePath -Content (
        [ordered] @{ responses = $responses } | ConvertTo-Json -Depth 20
    )
    $auditPath = Join-Path $temporaryRoot 'audit.json'
    $generatorOutput = @(& $generatorPath `
            -CatalogPath $catalogPath `
            -OutputPath $auditPath `
            -Source @($sourceOne, $sourceTwo) `
            -RequestFixturePath $fixturePath 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("Generator fixture exited with code $LASTEXITCODE. $([string]::Join("`n", $generatorOutput))")
    }
    else {
        $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
        $listed = @($audit.packages | Where-Object id -eq 'Fixture.Listed')[0]
        $missing = @($audit.packages | Where-Object id -eq 'Fixture.Missing')[0]
        $unlisted = @($audit.packages | Where-Object id -eq 'Fixture.Unlisted')[0]
        if ($listed.listingState -eq 'unresolved') {
            $failures.Add(
                "Fixture listed package was unresolved. Sources: $($audit.sources | ConvertTo-Json -Compress); " +
                "results: $($listed.sourceResults | ConvertTo-Json -Compress)"
            )
        }
        Assert-Equal -Scenario 'Complete package count' -Expected 3 -Actual $audit.packages.Count
        Assert-Equal -Scenario 'Configured source count' -Expected 2 -Actual $audit.sources.Count
        Assert-Equal -Scenario 'Unresolved source preserved' -Expected 'unresolved' -Actual $audit.sources[1].resolution
        Assert-Equal -Scenario 'Paged registration latest stable' -Expected '1.10.0' -Actual $listed.latestStable
        Assert-Equal -Scenario 'Arbitrary-size prerelease ordering' `
            -Expected '2.0.0-beta.1000000000000000000000000000000' -Actual $listed.latestPrerelease
        Assert-Equal -Scenario 'Listed aggregate state' -Expected 'listed' -Actual $listed.listingState
        Assert-Equal -Scenario 'Unlisted current version state' -Expected 'unlisted' -Actual $unlisted.listingState
        Assert-Equal -Scenario 'Missing current version state' -Expected 'missing' -Actual $missing.listingState
        Assert-Equal -Scenario 'Per-source result completeness' -Expected 2 -Actual $listed.sourceResults.Count

        $validatorOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $validatorPath `
                -AuditPath $auditPath -CatalogPath $catalogPath 2>&1)
        $scenarioCount++
        if ($LASTEXITCODE -ne 0) {
            $failures.Add("Generated audit did not pass deterministic validation. $([string]::Join("`n", $validatorOutput))")
        }
    }

    $catalogBefore = Get-Content -LiteralPath $catalogPath -Raw
    $collisionOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $generatorPath `
            -CatalogPath $catalogPath `
            -OutputPath $catalogPath `
            -Source $sourceOne `
            -RequestFixturePath $fixturePath 2>&1)
    $scenarioCount++
    if ($LASTEXITCODE -eq 0 -or [string]::Join("`n", $collisionOutput) -notlike '*output path must differ*') {
        $failures.Add('Catalog/output collision was not rejected.')
    }
    Assert-Equal -Scenario 'Catalog collision preserves bytes' `
        -Expected $catalogBefore -Actual (Get-Content -LiteralPath $catalogPath -Raw)

    foreach ($workflowRelativePath in @('../.github/workflows/ci.yml', '../.github/workflows/build-release.yml')) {
        $scenarioCount++
        $workflowPath = Join-Path $PSScriptRoot $workflowRelativePath
        $workflow = Get-Content -LiteralPath $workflowPath -Raw
        $generatorStepIndex = $workflow.IndexOf('- name: Test package version audit generator', [StringComparison]::Ordinal)
        $generatorCommandIndex = $workflow.IndexOf(
            'run: pwsh -NoProfile -File ./Tools/test-package-version-audit-generator.ps1',
            [StringComparison]::Ordinal
        )
        $validatorStepIndex = $workflow.IndexOf('- name: Test package version audit validator', [StringComparison]::Ordinal)
        if (
            $generatorStepIndex -lt 0 -or
            $generatorCommandIndex -le $generatorStepIndex -or
            $validatorStepIndex -le $generatorCommandIndex
        ) {
            $failures.Add("Workflow '$workflowRelativePath' must run generator fixtures before validator fixtures.")
        }
    }
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Package version audit generator tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine("Package version audit generator tests passed: $scenarioCount scenarios.")
