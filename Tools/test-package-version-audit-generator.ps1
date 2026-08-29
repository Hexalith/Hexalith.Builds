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
    $consumerEvidencePath = Join-Path $temporaryRoot 'consumer-evidence.json'
    Write-Utf8File -Path $consumerEvidencePath -Content (
        [ordered] @{
            entries = @(
                [ordered] @{ consumer = 'Fixture.Consumer'; packageId = 'Fixture.Listed' }
            )
        } | ConvertTo-Json -Depth 5
    )
    $auditPath = Join-Path $temporaryRoot 'audit.json'
    $generatorOutput = @(& $generatorPath `
            -CatalogPath $catalogPath `
            -OutputPath $auditPath `
            -Source @($sourceOne, $sourceTwo) `
            -RequestFixturePath $fixturePath `
            -ConsumerEvidencePath $consumerEvidencePath 2>&1)
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

        # Isolate PackageReference rediscovery (task 6) from this repository's own
        # tracked projects: this scenario's tiny 3-package fixture catalog/audit have
        # nothing to do with what Hexalith.Builds itself consumes, and $temporaryRoot
        # contains no .csproj/.props/.targets PackageReference elements of its own.
        $validatorOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $validatorPath `
                -AuditPath $auditPath -CatalogPath $catalogPath -ConsumerScanRoot $temporaryRoot 2>&1)
        $scenarioCount++
        if ($LASTEXITCODE -ne 0) {
            $failures.Add("Generated audit did not pass deterministic validation. $([string]::Join("`n", $validatorOutput))")
        }

        if ($audit.consumerEvidence.discovery -cne 'explicit-fixture') {
            $failures.Add("Synthetic consumer discovery was not recorded explicitly.")
        }

        $responses[$listedPageUri].response.items = @((New-RegistrationLeaf -Version '1.0.0' -Listed $true))
        $preservationFixturePath = Join-Path $temporaryRoot 'preservation-requests.json'
        Write-Utf8File -Path $preservationFixturePath -Content (
            [ordered] @{ responses = $responses } | ConvertTo-Json -Depth 20
        )
        $acceptedPriorPath = Join-Path $temporaryRoot 'accepted-prior.json'
        $acceptedBootstrapOutput = @(& $generatorPath `
                -CatalogPath $catalogPath `
                -OutputPath $acceptedPriorPath `
                -Source @($sourceOne) `
                -RequestFixturePath $preservationFixturePath `
                -ConsumerEvidencePath $consumerEvidencePath 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $failures.Add("Accepted-prior bootstrap failed. $([string]::Join("`n", $acceptedBootstrapOutput))")
        }
        else {
            $acceptedPrior = Get-Content -LiteralPath $acceptedPriorPath -Raw | ConvertFrom-Json
            $acceptedDecision = @($acceptedPrior.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
            $acceptedPackage = @($acceptedPrior.packages | Where-Object id -eq 'Fixture.Listed')[0]
            $acceptedDecision.disposition = 'accepted'
            $acceptedDecision.rollbackGroup = 'owner-approved-rollback-group'
            $acceptedDecision.rationale = 'Owner accepted the exact fixture metadata and consumer evidence.'
            $acceptedDecision.compatibilityEvidence = 'Fixture.Consumer passed the exact current compatibility lane.'
            $acceptedDecision.removalTrigger = 'Re-open when package metadata or direct consumers change.'
            $acceptedPackage.disposition = 'accepted'
            $acceptedPackage.rollbackGroup = 'owner-approved-rollback-group'
            $acceptedPackage.rationale = 'Package owner accepted this exact version and provenance.'
            $acceptedPackage.removalTrigger = 'Re-open this package decision when its exact provenance changes.'
            Write-Utf8File -Path $acceptedPriorPath -Content ($acceptedPrior | ConvertTo-Json -Depth 20)

            $preservedPath = Join-Path $temporaryRoot 'preserved.json'
            $preservedOutput = @(& $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $preservedPath `
                    -PriorAuditPath $acceptedPriorPath `
                    -Source @($sourceOne) `
                    -RequestFixturePath $preservationFixturePath `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $failures.Add("Provenance-preservation fixture failed. $([string]::Join("`n", $preservedOutput))")
            }
            else {
                $preservedAudit = Get-Content -LiteralPath $preservedPath -Raw | ConvertFrom-Json
                $preservedDecision = @($preservedAudit.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                Assert-Equal -Scenario 'Accepted decision is preserved only under identical provenance' `
                    -Expected 'accepted' -Actual $preservedDecision.disposition
                Assert-Equal -Scenario 'Accepted family rollback group round-trips exactly' `
                    -Expected $acceptedDecision.rollbackGroup -Actual $preservedDecision.rollbackGroup
                Assert-Equal -Scenario 'Accepted family rationale round-trips exactly' `
                    -Expected $acceptedDecision.rationale -Actual $preservedDecision.rationale
                Assert-Equal -Scenario 'Accepted family compatibility evidence round-trips exactly' `
                    -Expected $acceptedDecision.compatibilityEvidence -Actual $preservedDecision.compatibilityEvidence
                Assert-Equal -Scenario 'Accepted family removal trigger round-trips exactly' `
                    -Expected $acceptedDecision.removalTrigger -Actual $preservedDecision.removalTrigger
                Assert-Equal -Scenario 'Preserved consumers come from current direct evidence' `
                    -Expected 'Fixture.Consumer' -Actual $preservedDecision.representativeConsumers[0]
                Assert-Equal -Scenario 'Accepted family preservation status stays preserved' `
                    -Expected 'preserved' -Actual $preservedDecision.preservation.status
                $preservedPackage = @($preservedAudit.packages | Where-Object id -eq 'Fixture.Listed')[0]
                Assert-Equal -Scenario 'Accepted package disposition round-trips exactly' `
                    -Expected $acceptedPackage.disposition -Actual $preservedPackage.disposition
                Assert-Equal -Scenario 'Accepted package rollback group round-trips exactly' `
                    -Expected $acceptedPackage.rollbackGroup -Actual $preservedPackage.rollbackGroup
                Assert-Equal -Scenario 'Accepted package rationale round-trips exactly' `
                    -Expected $acceptedPackage.rationale -Actual $preservedPackage.rationale
                Assert-Equal -Scenario 'Accepted package removal trigger round-trips exactly' `
                    -Expected $acceptedPackage.removalTrigger -Actual $preservedPackage.removalTrigger

                $preservedValidatorOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $validatorPath `
                        -AuditPath $preservedPath -CatalogPath $catalogPath -ConsumerScanRoot $temporaryRoot 2>&1)
                $scenarioCount++
                if ($LASTEXITCODE -ne 0) {
                    $failures.Add("Preserved owner audit did not pass validation. $([string]::Join("`n", $preservedValidatorOutput))")
                }
            }

            $originalConsumerEvidence = Get-Content -LiteralPath $consumerEvidencePath -Raw
            Write-Utf8File -Path $consumerEvidencePath -Content "$originalConsumerEvidence`n"
            $declarationDriftPath = Join-Path $temporaryRoot 'declaration-drift.json'
            $declarationDriftOutput = @(& $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $declarationDriftPath `
                    -PriorAuditPath $acceptedPriorPath `
                    -Source @($sourceOne) `
                    -RequestFixturePath $preservationFixturePath `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $failures.Add("Declaration-byte drift fixture failed. $([string]::Join("`n", $declarationDriftOutput))")
            }
            else {
                $declarationDriftAudit = Get-Content -LiteralPath $declarationDriftPath -Raw | ConvertFrom-Json
                $declarationDriftDecision = @($declarationDriftAudit.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                Assert-Equal -Scenario 'Tracked declaration byte drift fails closed' -Expected 'retained' -Actual $declarationDriftDecision.disposition
                Assert-Equal -Scenario 'Tracked declaration byte drift is labeled' `
                    -Expected 'owned direct-consumer relations or tracked declaration bytes changed' `
                    -Actual $declarationDriftDecision.preservation.reason
            }
            Write-Utf8File -Path $consumerEvidencePath -Content $originalConsumerEvidence

            $originalCatalog = Get-Content -LiteralPath $catalogPath -Raw
            Write-Utf8File -Path $catalogPath -Content "$originalCatalog<!-- declaration drift -->`n"
            $catalogByteDriftPath = Join-Path $temporaryRoot 'catalog-byte-drift.json'
            $catalogByteDriftOutput = @(& $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $catalogByteDriftPath `
                    -PriorAuditPath $acceptedPriorPath `
                    -Source @($sourceOne) `
                    -RequestFixturePath $preservationFixturePath `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $failures.Add("Catalog-byte drift fixture failed. $([string]::Join("`n", $catalogByteDriftOutput))")
            }
            else {
                $catalogByteDriftAudit = Get-Content -LiteralPath $catalogByteDriftPath -Raw | ConvertFrom-Json
                $catalogByteDriftDecision = @($catalogByteDriftAudit.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                Assert-Equal -Scenario 'Catalog declaration byte drift fails closed' `
                    -Expected 'tracked catalog declaration bytes changed or were not previously bound' `
                    -Actual $catalogByteDriftDecision.preservation.reason
            }
            Write-Utf8File -Path $catalogPath -Content $originalCatalog

            $legacyPriorPath = Join-Path $temporaryRoot 'legacy-prior.json'
            $legacyPrior = Get-Content -LiteralPath $acceptedPriorPath -Raw | ConvertFrom-Json
            $legacyPrior.PSObject.Properties.Remove('consumerEvidence')
            Write-Utf8File -Path $legacyPriorPath -Content ($legacyPrior | ConvertTo-Json -Depth 20)
            $legacyRefreshPath = Join-Path $temporaryRoot 'legacy-refresh.json'
            $legacyRefreshOutput = @(& $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $legacyRefreshPath `
                    -PriorAuditPath $legacyPriorPath `
                    -Source @($sourceOne) `
                    -RequestFixturePath $preservationFixturePath `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $failures.Add("Legacy-provenance refresh fixture failed. $([string]::Join("`n", $legacyRefreshOutput))")
            }
            else {
                $legacyRefreshAudit = Get-Content -LiteralPath $legacyRefreshPath -Raw | ConvertFrom-Json
                $legacyRefreshDecision = @($legacyRefreshAudit.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                Assert-Equal -Scenario 'Accepted legacy provenance refreshes retained' -Expected 'retained' -Actual $legacyRefreshDecision.disposition
                Assert-Equal -Scenario 'Accepted legacy provenance keeps complete typed history' `
                    -Expected 'hexalith.package-audit-family-history.v1' -Actual $legacyRefreshDecision.historicalContext[-1].schema
                $legacyRefreshPackage = @($legacyRefreshAudit.packages | Where-Object id -eq 'Fixture.Listed')[0]
                Assert-Equal -Scenario 'Accepted legacy package provenance keeps typed history' `
                    -Expected 'hexalith.package-audit-package-history.v1' -Actual $legacyRefreshPackage.historicalContext[-1].schema
            }

            $driftConsumerEvidencePath = Join-Path $temporaryRoot 'consumer-evidence-drift.json'
            Write-Utf8File -Path $driftConsumerEvidencePath -Content (
                [ordered] @{
                    entries = @(
                        [ordered] @{ consumer = 'Fixture.OtherConsumer'; packageId = 'Fixture.Listed' }
                    )
                } | ConvertTo-Json -Depth 5
            )
            $consumerDriftPath = Join-Path $temporaryRoot 'consumer-drift.json'
            $consumerDriftOutput = @(& $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $consumerDriftPath `
                    -PriorAuditPath $acceptedPriorPath `
                    -Source @($sourceOne) `
                    -RequestFixturePath $preservationFixturePath `
                    -ConsumerEvidencePath $driftConsumerEvidencePath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $failures.Add("Consumer-drift fixture failed. $([string]::Join("`n", $consumerDriftOutput))")
            }
            else {
                $consumerDriftAudit = Get-Content -LiteralPath $consumerDriftPath -Raw | ConvertFrom-Json
                $consumerDriftDecision = @($consumerDriftAudit.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                Assert-Equal -Scenario 'Consumer drift fails closed' -Expected 'retained' -Actual $consumerDriftDecision.disposition
                Assert-Equal -Scenario 'Consumer drift retains labeled owner history' `
                    -Expected 'accepted' -Actual $consumerDriftDecision.historicalContext[-1].disposition
            }

            $metadataResponses = $responses | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable
            $metadataResponses[$listedPageUri].response.items = @(
                (New-RegistrationLeaf -Version '1.0.0' -Listed $true),
                (New-RegistrationLeaf -Version '1.1.0' -Listed $true)
            )
            $metadataDriftFixturePath = Join-Path $temporaryRoot 'metadata-drift-requests.json'
            Write-Utf8File -Path $metadataDriftFixturePath -Content (
                [ordered] @{ responses = $metadataResponses } | ConvertTo-Json -Depth 20
            )
            $metadataDriftPath = Join-Path $temporaryRoot 'metadata-drift.json'
            $metadataDriftOutput = @(& $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $metadataDriftPath `
                    -PriorAuditPath $acceptedPriorPath `
                    -Source @($sourceOne) `
                    -RequestFixturePath $metadataDriftFixturePath `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $failures.Add("Metadata-drift fixture failed. $([string]::Join("`n", $metadataDriftOutput))")
            }
            else {
                $metadataDriftAudit = Get-Content -LiteralPath $metadataDriftPath -Raw | ConvertFrom-Json
                $metadataDriftDecision = @($metadataDriftAudit.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                Assert-Equal -Scenario 'Metadata drift fails closed' -Expected 'retained' -Actual $metadataDriftDecision.disposition
            }

            $sourceDriftPath = Join-Path $temporaryRoot 'source-drift.json'
            $sourceDriftOutput = @(& $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $sourceDriftPath `
                    -PriorAuditPath $acceptedPriorPath `
                    -Source @($sourceOne, $sourceTwo) `
                    -RequestFixturePath $preservationFixturePath `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $failures.Add("Source-drift fixture failed. $([string]::Join("`n", $sourceDriftOutput))")
            }
            else {
                $sourceDriftAudit = Get-Content -LiteralPath $sourceDriftPath -Raw | ConvertFrom-Json
                $sourceDriftDecision = @($sourceDriftAudit.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                Assert-Equal -Scenario 'Source drift fails closed' `
                    -Expected 'configured source scope changed' -Actual $sourceDriftDecision.preservation.reason
            }

            foreach ($malformation in @(
                    'duplicate-package',
                    'missing-package-family',
                    'family-package-mismatch',
                    'family-disposition-mismatch',
                    'family-rollback-mismatch',
                    'missing-family-decision',
                    'duplicate-source-result',
                    'unknown-consumer-package',
                    'duplicate-source',
                    'missing-source-result',
                    'duplicate-family-decision',
                    'orphan-family-decision',
                    'duplicate-consumer-relation',
                    'consumer-family-mismatch',
                    'consumer-hash-mismatch',
                    'consumer-declaration-missing',
                    'unknown-package-history-schema',
                    'unknown-family-history-schema'
                )) {
                $malformedPrior = Get-Content -LiteralPath $acceptedPriorPath -Raw | ConvertFrom-Json
                switch ($malformation) {
                    'duplicate-package' { $malformedPrior.packages += $malformedPrior.packages[0] }
                    'missing-package-family' { $malformedPrior.packages[0].family = '' }
                    'family-package-mismatch' { $malformedPrior.familyDecisions[0].packageIds = @('Fixture.Unknown') }
                    'family-disposition-mismatch' { $malformedPrior.packages[0].disposition = 'retained' }
                    'family-rollback-mismatch' { $malformedPrior.packages[0].rollbackGroup = 'wrong-group' }
                    'missing-family-decision' { $malformedPrior.familyDecisions = @($malformedPrior.familyDecisions | Select-Object -Skip 1) }
                    'duplicate-source-result' { $malformedPrior.packages[0].sourceResults += $malformedPrior.packages[0].sourceResults[0] }
                    'unknown-consumer-package' { $malformedPrior.consumerEvidence.entries[0].packageId = 'Fixture.Unknown' }
                    'duplicate-source' { $malformedPrior.sources += $malformedPrior.sources[0] }
                    'missing-source-result' { $malformedPrior.packages[0].sourceResults = @($malformedPrior.packages[0].sourceResults | Select-Object -Skip 1) }
                    'duplicate-family-decision' { $malformedPrior.familyDecisions += $malformedPrior.familyDecisions[0] }
                    'orphan-family-decision' {
                        $orphan = $malformedPrior.familyDecisions[0].PSObject.Copy()
                        $orphan.family = 'orphan-family'
                        $orphan.packageIds = @()
                        $malformedPrior.familyDecisions += $orphan
                    }
                    'duplicate-consumer-relation' { $malformedPrior.consumerEvidence.entries += $malformedPrior.consumerEvidence.entries[0] }
                    'consumer-family-mismatch' { $malformedPrior.consumerEvidence.entries[0].family = 'wrong-family' }
                    'consumer-hash-mismatch' { $malformedPrior.consumerEvidence.sha256 = ('0' * 64) }
                    'consumer-declaration-missing' { $malformedPrior.consumerEvidence.entries[0].declarationSha256 = '' }
                    'unknown-package-history-schema' {
                        $malformedPrior.packages[0] | Add-Member -NotePropertyName historicalContext `
                            -NotePropertyValue @([pscustomobject] @{ schema = 'unknown.package-history.v1' }) -Force
                    }
                    'unknown-family-history-schema' {
                        $malformedPrior.familyDecisions[0].historicalContext = @(
                            [pscustomobject] @{ schema = 'unknown.family-history.v1' }
                        )
                    }
                }
                $malformedPriorPath = Join-Path $temporaryRoot "$malformation.json"
                Write-Utf8File -Path $malformedPriorPath -Content ($malformedPrior | ConvertTo-Json -Depth 20)
                $malformedOutputPath = Join-Path $temporaryRoot "$malformation-output.json"
                $malformedOutput = @(& $generatorPath `
                        -CatalogPath $catalogPath `
                        -OutputPath $malformedOutputPath `
                        -PriorAuditPath $malformedPriorPath `
                        -Source @($sourceOne) `
                        -RequestFixturePath $preservationFixturePath `
                        -ConsumerEvidencePath $consumerEvidencePath 2>&1)
                $scenarioCount++
                if ($LASTEXITCODE -eq 0) {
                    $failures.Add("Malformed prior relation '$malformation' was not rejected. $([string]::Join("`n", $malformedOutput))")
                }
            }
        }
    }

    $productionAuditPath = Join-Path $PSScriptRoot 'package-version-audit.json'
    $productionAudit = Get-Content -LiteralPath $productionAuditPath -Raw | ConvertFrom-Json
    $propsConsumerEntries = @($productionAudit.consumerEvidence.entries | Where-Object {
            [string] $_.declarationPath -like '*.props'
        })
    $scenarioCount++
    if ($propsConsumerEntries.Count -eq 0) {
        $failures.Add('Production audit must prove direct PackageReference discovery from at least one tracked .props declaration.')
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
