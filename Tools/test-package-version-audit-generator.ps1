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
function New-NonTerminatingGitShim {
    param(
        [Parameter(Mandatory = $true)][string] $ShimPath,
        [Parameter(Mandatory = $true)][string] $ReadyPath
    )

    $readyPathBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ReadyPath))
    $content = @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $IgnoredArguments)
$null = $IgnoredArguments
$readyPath = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__READY_PATH__'))
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$child = Start-Process -FilePath $pwshExecutable `
    -ArgumentList @('-NoLogo', '-NoProfile', '-Command', 'Start-Sleep -Seconds 30') `
    -PassThru
$readyTemporaryPath = "$readyPath.$PID.tmp"
[IO.File]::WriteAllText($readyTemporaryPath, "$PID|$($child.Id)")
[IO.File]::Move($readyTemporaryPath, $readyPath, $true)
Start-Sleep -Seconds 30
'@
    $content = $content.Replace('__READY_PATH__', $readyPathBase64)
    Write-Utf8File -Path $ShimPath -Content "$content`n"
}

function Wait-ForProcessGone {
    param(
        [Parameter(Mandatory = $true)][int] $ProcessId,
        [Parameter(Mandatory = $true)][int] $TimeoutMilliseconds
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            return $true
        }
        Start-Sleep -Milliseconds 100
    }

    return $null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

function Read-ReadyProcessRecord {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    # Mirrors the validator harness: a malformed record is a controlled fixture failure
    # with its own diagnostic, never $null, because $null means "not ready yet" and
    # would silently degrade a corrupt handshake into a readiness-poll timeout.
    try {
        $parts = (Get-Content -LiteralPath $Path -Raw -ErrorAction Stop).Trim().Split('|')
    }
    catch {
        return [pscustomobject] @{
            Valid = $false
            Diagnostic = "Ready/PID record could not be read: $($_.Exception.GetBaseException().Message)"
        }
    }
    $shimProcessId = 0
    $childProcessId = 0
    if ($parts.Count -ne 2 -or
        -not [int]::TryParse($parts[0], [ref] $shimProcessId) -or
        -not [int]::TryParse($parts[1], [ref] $childProcessId) -or
        $shimProcessId -le 0 -or $childProcessId -le 0) {
        return [pscustomobject] @{
            Valid = $false
            Diagnostic = 'Ready/PID record must contain two positive integer process identifiers.'
        }
    }

    return [pscustomobject] @{
        Valid = $true
        ShimProcessId = $shimProcessId
        ChildProcessId = $childProcessId
    }
}

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
            $acceptedPrior = Get-Content -LiteralPath $acceptedPriorPath -Raw | ConvertFrom-Json -DateKind String
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

            # Almost all history in the shipped artifact is v1, so the v1 branch of the
            # prior contract is the one every real incremental refresh traverses. A
            # bootstrapped prior carries no history at all, which left that branch -- and
            # the verbatim copy-forward of legacy records -- entirely unproven. Stamping
            # legacy records onto the preserved family makes the deep-equality assertions
            # below cover them.
            $legacyFamilyDecision = @($acceptedPrior.familyDecisions | Where-Object family -eq 'package:fixture.missing')[0]
            $legacyPackage = @($acceptedPrior.packages | Where-Object id -eq 'Fixture.Missing')[0]
            $legacyRevision = 'a' * 40
            $legacyTimestamp = '2026-01-02T03:04:05Z'
            $legacyFamilyDecision.historicalContext = @([pscustomobject] [ordered] @{
                    schema = 'hexalith.package-audit-family-history.v1'
                    label = 'Legacy v1 family snapshot.'
                    auditedAtUtc = $legacyTimestamp
                    generatedFromRevision = $legacyRevision
                    family = 'package:fixture.missing'
                    disposition = $legacyFamilyDecision.disposition
                    rollbackGroup = $legacyFamilyDecision.rollbackGroup
                    packageIds = @($legacyFamilyDecision.packageIds)
                    rationale = $legacyFamilyDecision.rationale
                    compatibilityEvidence = $legacyFamilyDecision.compatibilityEvidence
                    removalTrigger = $legacyFamilyDecision.removalTrigger
                    representativeConsumers = @($legacyFamilyDecision.representativeConsumers)
                    preservation = [pscustomobject] [ordered] @{
                        status = 'preserved'
                        reason = 'Legacy v1 preservation envelope.'
                    }
                    supersededBecause = 'Superseded by the v2 family origin contract.'
                })
            $legacyPackage.historicalContext = @([pscustomobject] [ordered] @{
                    schema = 'hexalith.package-audit-package-history.v1'
                    label = 'Legacy v1 package snapshot.'
                    auditedAtUtc = $legacyTimestamp
                    generatedFromRevision = $legacyRevision
                    id = 'Fixture.Missing'
                    auditedVersion = $legacyPackage.auditedVersion
                    selectedVersion = $legacyPackage.selectedVersion
                    latestStable = $null
                    latestPrerelease = $null
                    listingState = 'missing'
                    family = 'package:fixture.missing'
                    disposition = $legacyPackage.disposition
                    rollbackGroup = $legacyPackage.rollbackGroup
                    rationale = $legacyPackage.rationale
                    evidence = $legacyPackage.evidence
                    removalTrigger = $legacyPackage.removalTrigger
                    sourceResults = @([pscustomobject] [ordered] @{
                            source = $sourceOne
                            listingState = 'missing'
                            latestStable = $null
                            latestPrerelease = $null
                            diagnostic = ''
                        })
                    supersededBecause = 'Superseded by the v2 package origin contract.'
                })
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
                Assert-Equal -Scenario 'Accepted family origin stays stable for identical evidence' `
                    -Expected $acceptedDecision.origin.generatedFromRevision -Actual $preservedDecision.origin.generatedFromRevision
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

            $incrementalOriginalCatalog = Get-Content -LiteralPath $catalogPath -Raw
            Write-Utf8File -Path $catalogPath -Content (
                $incrementalOriginalCatalog.Replace(
                    'Fixture.Listed" Version="1.0.0"',
                    'Fixture.Listed" Version="1.1.0"'
                )
            )
            $responses[$listedPageUri].response.items = @(
                (New-RegistrationLeaf -Version '1.0.0' -Listed $true),
                (New-RegistrationLeaf -Version '1.1.0' -Listed $true)
            )
            $incrementalResponses = [ordered] @{}
            $incrementalResponses[$sourceOne] = $responses[$sourceOne]
            $incrementalResponses["$registrationBase/fixture.listed/index.json"] = `
                $responses["$registrationBase/fixture.listed/index.json"]
            $incrementalResponses[$listedPageUri] = $responses[$listedPageUri]
            $incrementalFixturePath = Join-Path $temporaryRoot 'incremental-requests.json'
            Write-Utf8File -Path $incrementalFixturePath -Content (
                [ordered] @{ responses = $incrementalResponses } | ConvertTo-Json -Depth 20
            )
            $incrementalPath = Join-Path $temporaryRoot 'incremental.json'
            $incrementalOutput = @(& $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $incrementalPath `
                    -PriorAuditPath $acceptedPriorPath `
                    -Family 'package:fixture.listed' `
                    -Source @($sourceOne) `
                    -RequestFixturePath $incrementalFixturePath `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $failures.Add("Incremental family refresh failed. $([string]::Join("`n", $incrementalOutput))")
            }
            else {
                $incrementalAudit = Get-Content -LiteralPath $incrementalPath -Raw | ConvertFrom-Json -DateKind String
                $incrementalDecision = @($incrementalAudit.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                $incrementalPackage = @($incrementalAudit.packages | Where-Object id -eq 'Fixture.Listed')[0]
                $untouchedPriorDecision = @($acceptedPrior.familyDecisions | Where-Object family -eq 'package:fixture.missing')[0]
                $untouchedDecision = @($incrementalAudit.familyDecisions | Where-Object family -eq 'package:fixture.missing')[0]
                $untouchedPriorPackage = @($acceptedPrior.packages | Where-Object id -eq 'Fixture.Missing')[0]
                $untouchedPackage = @($incrementalAudit.packages | Where-Object id -eq 'Fixture.Missing')[0]
                Assert-Equal -Scenario 'Incremental mode is explicit' -Expected 'incremental' -Actual $incrementalAudit.snapshot.mode
                Assert-Equal -Scenario 'Incremental refreshed partition contains one family' `
                    -Expected 'package:fixture.listed' -Actual $incrementalAudit.snapshot.refreshedFamilies[0]
                Assert-Equal -Scenario 'Incremental preserved partition covers untouched families' `
                    -Expected 2 -Actual $incrementalAudit.snapshot.preservedFamilies.Count
                Assert-Equal -Scenario 'Changed family gains one family snapshot' `
                    -Expected ($acceptedDecision.historicalContext.Count + 1) -Actual $incrementalDecision.historicalContext.Count
                Assert-Equal -Scenario 'Changed package gains one package snapshot' `
                    -Expected ($acceptedPackage.historicalContext.Count + 1) -Actual $incrementalPackage.historicalContext.Count
                $scenarioCount++
                if ($null -ne $incrementalPackage.historicalContext[-1].latestPrerelease) {
                    $failures.Add('Historical null candidate was coerced away from JSON null.')
                }
                Assert-Equal -Scenario 'Untouched family remains deep-equal' `
                    -Expected ($untouchedPriorDecision | ConvertTo-Json -Depth 30 -Compress) `
                    -Actual ($untouchedDecision | ConvertTo-Json -Depth 30 -Compress)
                Assert-Equal -Scenario 'Untouched package remains deep-equal' `
                    -Expected ($untouchedPriorPackage | ConvertTo-Json -Depth 30 -Compress) `
                    -Actual ($untouchedPackage | ConvertTo-Json -Depth 30 -Compress)

                $incrementalValidatorOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $validatorPath `
                        -AuditPath $incrementalPath -CatalogPath $catalogPath -ConsumerScanRoot $temporaryRoot 2>&1)
                $scenarioCount++
                if ($LASTEXITCODE -ne 0) {
                    $failures.Add("Incremental audit did not pass validation. $([string]::Join("`n", $incrementalValidatorOutput))")
                }

                $deduplicatedPath = Join-Path $temporaryRoot 'incremental-deduplicated.json'
                $deduplicatedOutput = @(& $generatorPath `
                        -CatalogPath $catalogPath `
                        -OutputPath $deduplicatedPath `
                        -PriorAuditPath $incrementalPath `
                        -Family 'package:fixture.listed' `
                        -Source @($sourceOne) `
                        -RequestFixturePath $incrementalFixturePath `
                        -ConsumerEvidencePath $consumerEvidencePath 2>&1)
                if ($LASTEXITCODE -ne 0) {
                    $failures.Add("Repeated incremental refresh failed. $([string]::Join("`n", $deduplicatedOutput))")
                }
                else {
                    $deduplicatedAudit = Get-Content -LiteralPath $deduplicatedPath -Raw | ConvertFrom-Json -DateKind String
                    $deduplicatedDecision = @($deduplicatedAudit.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                    $deduplicatedPackage = @($deduplicatedAudit.packages | Where-Object id -eq 'Fixture.Listed')[0]
                    Assert-Equal -Scenario 'Repeated identical refresh deduplicates family history' `
                        -Expected $incrementalDecision.historicalContext.Count -Actual $deduplicatedDecision.historicalContext.Count
                    Assert-Equal -Scenario 'Repeated identical refresh deduplicates package history' `
                        -Expected $incrementalPackage.historicalContext.Count -Actual $deduplicatedPackage.historicalContext.Count
                    Assert-Equal -Scenario 'Repeated package history remains deep-equal' `
                        -Expected ($incrementalPackage.historicalContext | ConvertTo-Json -Depth 30 -Compress) `
                        -Actual ($deduplicatedPackage.historicalContext | ConvertTo-Json -Depth 30 -Compress)
                }
            }

            foreach ($invalidSelection in @(
                    @{ Name = 'unknown'; Values = @('package:unknown') },
                    @{ Name = 'duplicate'; Values = @('package:fixture.listed', 'package:fixture.listed') },
                    @{ Name = 'case-variant'; Values = @('PACKAGE:FIXTURE.LISTED') }
                )) {
                $invalidSelectionOutput = @(& $generatorPath `
                        -CatalogPath $catalogPath `
                        -OutputPath (Join-Path $temporaryRoot "invalid-$($invalidSelection.Name).json") `
                        -PriorAuditPath $acceptedPriorPath `
                        -Family $invalidSelection.Values `
                        -Source @($sourceOne) `
                        -RequestFixturePath $incrementalFixturePath `
                        -ConsumerEvidencePath $consumerEvidencePath 2>&1)
                $scenarioCount++
                if ($LASTEXITCODE -eq 0) {
                    $failures.Add("Incremental $($invalidSelection.Name) family selection was not rejected.")
                }
            }

            foreach ($preservedTamper in @(
                    'fingerprint', 'unknown-field', 'wrong-type', 'representative-consumers',
                    'package-history-unknown', 'package-history-wrong-type', 'family-history-unknown'
                )) {
                $tamperedPrior = Get-Content -LiteralPath $acceptedPriorPath -Raw | ConvertFrom-Json -DateKind String
                $tamperedDecision = @($tamperedPrior.familyDecisions | Where-Object family -eq 'package:fixture.missing')[0]
                $tamperedPackage = @($tamperedPrior.packages | Where-Object family -eq 'package:fixture.missing')[0]
                if ($preservedTamper -like 'package-history-*') {
                    $historyOrigin = $tamperedDecision.origin | ConvertTo-Json -Depth 10 |
                        ConvertFrom-Json -DateKind String
                    $tamperedPackage.historicalContext = @([pscustomobject][ordered] @{
                            schema = 'hexalith.package-audit-package-history.v2'
                            label = 'Preserved historical package fixture.'
                            auditedAtUtc = $historyOrigin.auditedAtUtc
                            generatedFromRevision = $historyOrigin.generatedFromRevision
                            id = $tamperedPackage.id
                            auditedVersion = $tamperedPackage.auditedVersion
                            selectedVersion = $tamperedPackage.selectedVersion
                            latestStable = $tamperedPackage.latestStable
                            latestPrerelease = $tamperedPackage.latestPrerelease
                            listingState = $tamperedPackage.listingState
                            family = $tamperedPackage.family
                            disposition = $tamperedPackage.disposition
                            rollbackGroup = $tamperedPackage.rollbackGroup
                            rationale = $tamperedPackage.rationale
                            evidence = $tamperedPackage.evidence
                            removalTrigger = $tamperedPackage.removalTrigger
                            sourceResults = @($tamperedPackage.sourceResults)
                            origin = $historyOrigin
                            supersededBecause = 'Preserved history fixture.'
                        })
                }
                elseif ($preservedTamper -ceq 'family-history-unknown') {
                    $historyOrigin = $tamperedDecision.origin | ConvertTo-Json -Depth 10 |
                        ConvertFrom-Json -DateKind String
                    $tamperedDecision.historicalContext = @([pscustomobject][ordered] @{
                            schema = 'hexalith.package-audit-family-history.v2'
                            label = 'Preserved historical family fixture.'
                            auditedAtUtc = $historyOrigin.auditedAtUtc
                            generatedFromRevision = $historyOrigin.generatedFromRevision
                            family = $tamperedDecision.family
                            disposition = $tamperedDecision.disposition
                            rollbackGroup = $tamperedDecision.rollbackGroup
                            packageIds = @($tamperedDecision.packageIds)
                            rationale = $tamperedDecision.rationale
                            compatibilityEvidence = $tamperedDecision.compatibilityEvidence
                            removalTrigger = $tamperedDecision.removalTrigger
                            representativeConsumers = @($tamperedDecision.representativeConsumers)
                            origin = $historyOrigin
                            supersededBecause = 'Preserved history fixture.'
                        })
                }
                switch ($preservedTamper) {
                    'fingerprint' { $tamperedDecision.origin.packageMetadataSha256 = ('0' * 64) }
                    'unknown-field' {
                        $tamperedPackage | Add-Member -NotePropertyName reviewer -NotePropertyValue 'untyped'
                    }
                    'wrong-type' { $tamperedDecision.origin.sourceScopeSha256 = 42 }
                    'representative-consumers' {
                        $tamperedDecision.representativeConsumers = @('Fixture.Unrelated.Consumer')
                    }
                    'package-history-unknown' {
                        $tamperedPackage.historicalContext[0] |
                            Add-Member -NotePropertyName reviewer -NotePropertyValue 'untyped'
                    }
                    'package-history-wrong-type' {
                        $tamperedPackage.historicalContext[0].sourceResults[0].diagnostic = 42
                    }
                    'family-history-unknown' {
                        $tamperedDecision.historicalContext[0] |
                            Add-Member -NotePropertyName reviewer -NotePropertyValue 'untyped'
                    }
                }
                $tamperedPriorPath = Join-Path $temporaryRoot "preserved-$preservedTamper-prior.json"
                Write-Utf8File -Path $tamperedPriorPath -Content ($tamperedPrior | ConvertTo-Json -Depth 30)
                $tamperedOutputPath = Join-Path $temporaryRoot "preserved-$preservedTamper-output.json"
                $sentinel = "{`"sentinel`":`"$preservedTamper`"}`n"
                Write-Utf8File -Path $tamperedOutputPath -Content $sentinel
                $tamperedOutput = @(& $generatorPath `
                        -CatalogPath $catalogPath `
                        -OutputPath $tamperedOutputPath `
                        -PriorAuditPath $tamperedPriorPath `
                        -Family 'package:fixture.listed' `
                        -Source @($sourceOne) `
                        -RequestFixturePath $incrementalFixturePath `
                        -ConsumerEvidencePath $consumerEvidencePath 2>&1)
                $scenarioCount++
                if ($LASTEXITCODE -eq 0) {
                    $failures.Add("Tampered preserved v2 evidence '$preservedTamper' was not rejected.")
                }
                Assert-Equal -Scenario "Failed preserved evidence '$preservedTamper' keeps prior output bytes" `
                    -Expected $sentinel -Actual (Get-Content -LiteralPath $tamperedOutputPath -Raw)
            }

            Write-Utf8File -Path $catalogPath -Content (
                (Get-Content -LiteralPath $catalogPath -Raw).Replace(
                    'Fixture.Missing" Version="3.0.0"',
                    'Fixture.Missing" Version="3.1.0"'
                )
            )
            $unrequestedOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath (Join-Path $temporaryRoot 'unrequested-drift.json') `
                    -PriorAuditPath $acceptedPriorPath `
                    -Family 'package:fixture.listed' `
                    -Source @($sourceOne) `
                    -RequestFixturePath $incrementalFixturePath `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            $scenarioCount++
            if ($LASTEXITCODE -eq 0) {
                $failures.Add('Unrequested changed family was not rejected before querying.')
            }
            elseif ((($unrequestedOutput | ForEach-Object { [string] $_ }) -join "`n") -notmatch
                "unrequested family 'package:fixture\.missing' changed") {
                $failures.Add('Unrequested catalog drift did not fail before any package request was attempted.')
            }
            Write-Utf8File -Path $catalogPath -Content $incrementalOriginalCatalog
            $responses[$listedPageUri].response.items = @((New-RegistrationLeaf -Version '1.0.0' -Listed $true))

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
                    -Expected 1 -Actual $declarationDriftDecision.historicalContext.Count
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
                Assert-Equal -Scenario 'Unrelated catalog byte drift preserves family-local decision' `
                    -Expected 'accepted' -Actual $catalogByteDriftDecision.disposition
                Assert-Equal -Scenario 'Unrelated catalog byte drift does not grow family history' `
                    -Expected $acceptedDecision.historicalContext.Count -Actual $catalogByteDriftDecision.historicalContext.Count
            }
            Write-Utf8File -Path $catalogPath -Content $originalCatalog

            $legacyPriorPath = Join-Path $temporaryRoot 'legacy-prior.json'
            $legacyPrior = Get-Content -LiteralPath $acceptedPriorPath -Raw | ConvertFrom-Json -DateKind String
            $legacyPrior.schemaVersion = 1
            $legacyPrior | Add-Member -NotePropertyName auditedAtUtc `
                -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O'))
            $legacyPrior.PSObject.Properties.Remove('snapshot')
            $legacyPrior.PSObject.Properties.Remove('catalogRawSha256')
            $legacyPrior.PSObject.Properties.Remove('consumerEvidence')
            foreach ($legacyDecision in $legacyPrior.familyDecisions) {
                $legacyDecision.PSObject.Properties.Remove('origin')
                $legacyDecision | Add-Member -NotePropertyName preservation -NotePropertyValue ([pscustomobject] @{
                        status = 'legacy-unbound'
                        reason = 'Legacy fixture predates family-local origin provenance.'
                    })
            }
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
                    -Expected 'hexalith.package-audit-family-history.v2' -Actual $legacyRefreshDecision.historicalContext[-1].schema
                $legacyRefreshPackage = @($legacyRefreshAudit.packages | Where-Object id -eq 'Fixture.Listed')[0]
                Assert-Equal -Scenario 'Accepted legacy package provenance keeps typed history' `
                    -Expected 'hexalith.package-audit-package-history.v2' -Actual $legacyRefreshPackage.historicalContext[-1].schema
            }

            # A complete refresh migrates a pre-v2 prior (above); an incremental refresh
            # would instead copy its unvalidated rows verbatim into a v2 document, so it
            # must fail closed rather than skip the closed-shape prior contract.
            $legacyIncrementalPath = Join-Path $temporaryRoot 'legacy-incremental.json'
            $legacyIncrementalOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $legacyIncrementalPath `
                    -PriorAuditPath $legacyPriorPath `
                    -Family 'package:fixture.listed' `
                    -Source @($sourceOne) `
                    -RequestFixturePath $preservationFixturePath `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            $scenarioCount++
            $legacyIncrementalText = ($legacyIncrementalOutput | ForEach-Object { [string] $_ }) -join "`n"
            if ($LASTEXITCODE -eq 0 -or
                $legacyIncrementalText -notmatch 'incremental refresh requires a schemaVersion 2 prior audit') {
                $failures.Add(
                    "An incremental refresh over a pre-v2 prior audit was not rejected. $legacyIncrementalText"
                )
            }
            if (Test-Path -LiteralPath $legacyIncrementalPath -PathType Leaf) {
                $failures.Add('A rejected incremental refresh over a pre-v2 prior audit still wrote output.')
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

            $diagnosticResponsesA = $responses | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable
            $diagnosticResponsesA["$registrationBase/fixture.listed/index.json"] = @{
                error = 'Diagnostic fixture A.'
            }
            $diagnosticFixtureA = Join-Path $temporaryRoot 'diagnostic-a-requests.json'
            Write-Utf8File -Path $diagnosticFixtureA -Content (
                [ordered] @{ responses = $diagnosticResponsesA } | ConvertTo-Json -Depth 20
            )
            $diagnosticPriorPath = Join-Path $temporaryRoot 'diagnostic-prior.json'
            $diagnosticPriorOutput = @(& $generatorPath `
                    -CatalogPath $catalogPath `
                    -OutputPath $diagnosticPriorPath `
                    -Source @($sourceOne) `
                    -RequestFixturePath $diagnosticFixtureA `
                    -ConsumerEvidencePath $consumerEvidencePath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $failures.Add("Diagnostic prior fixture failed. $([string]::Join("`n", $diagnosticPriorOutput))")
            }
            else {
                $diagnosticResponsesB = $diagnosticResponsesA | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable
                $diagnosticResponsesB["$registrationBase/fixture.listed/index.json"].error = 'Diagnostic fixture B.'
                $diagnosticFixtureB = Join-Path $temporaryRoot 'diagnostic-b-requests.json'
                Write-Utf8File -Path $diagnosticFixtureB -Content (
                    [ordered] @{ responses = $diagnosticResponsesB } | ConvertTo-Json -Depth 20
                )
                $diagnosticDriftPath = Join-Path $temporaryRoot 'diagnostic-drift.json'
                $diagnosticDriftOutput = @(& $generatorPath `
                        -CatalogPath $catalogPath `
                        -OutputPath $diagnosticDriftPath `
                        -PriorAuditPath $diagnosticPriorPath `
                        -Source @($sourceOne) `
                        -RequestFixturePath $diagnosticFixtureB `
                        -ConsumerEvidencePath $consumerEvidencePath 2>&1)
                if ($LASTEXITCODE -ne 0) {
                    $failures.Add("Diagnostic drift fixture failed. $([string]::Join("`n", $diagnosticDriftOutput))")
                }
                else {
                    $diagnosticPrior = Get-Content -LiteralPath $diagnosticPriorPath -Raw | ConvertFrom-Json -DateKind String
                    $diagnosticDrift = Get-Content -LiteralPath $diagnosticDriftPath -Raw | ConvertFrom-Json -DateKind String
                    $diagnosticPriorDecision = @($diagnosticPrior.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                    $diagnosticDriftDecision = @($diagnosticDrift.familyDecisions | Where-Object family -eq 'package:fixture.listed')[0]
                    $scenarioCount++
                    if ($diagnosticPriorDecision.origin.packageMetadataSha256 -ceq `
                        $diagnosticDriftDecision.origin.packageMetadataSha256) {
                        $failures.Add('Diagnostic-only source-result drift did not change packageMetadataSha256.')
                    }
                    Assert-Equal -Scenario 'Diagnostic-only drift gains one family history record' `
                        -Expected ($diagnosticPriorDecision.historicalContext.Count + 1) `
                        -Actual $diagnosticDriftDecision.historicalContext.Count
                }
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
                    -Expected 'retained' -Actual $sourceDriftDecision.disposition
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
                $malformedPrior = Get-Content -LiteralPath $acceptedPriorPath -Raw | ConvertFrom-Json -DateKind String
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

    $repositoryFixtureRoot = Join-Path $temporaryRoot 'repository-owned'
    $repositoryFixtureTools = Join-Path $repositoryFixtureRoot 'Tools'
    $repositoryFixtureProps = Join-Path $repositoryFixtureRoot 'Props'
    New-Item -ItemType Directory -Path $repositoryFixtureTools, $repositoryFixtureProps -Force | Out-Null
    Copy-Item -LiteralPath $generatorPath -Destination (Join-Path $repositoryFixtureTools 'audit-central-package-versions.ps1')
    Write-Utf8File -Path (Join-Path $repositoryFixtureRoot '.gitattributes') -Content @'
*.props text eol=crlf
*.csproj text eol=crlf
'@
    # These two fixture files are the ones .gitattributes normalizes, so they are written
    # with a trailing line ending: without one there is nothing for eol=crlf to convert
    # and the checkout below cannot diverge from the committed blob.
    $repositoryCatalogPath = Join-Path $repositoryFixtureProps 'Directory.Packages.props'
    Write-Utf8File -Path $repositoryCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Fixture.Repository" Version="1.0.0" /></ItemGroup></Project>

'@
    $repositoryConsumerPath = Join-Path $repositoryFixtureRoot 'Consumer.csproj'
    Write-Utf8File -Path $repositoryConsumerPath -Content @'
<Project><ItemGroup><PackageReference Include="Fixture.Repository" /></ItemGroup></Project>

'@
    $repositoryResponses = [ordered] @{}
    Add-FixtureResponse -Responses $repositoryResponses -Uri $sourceOne -Response ([ordered] @{
            resources = @(
                [ordered] @{ '@id' = $registrationBase; '@type' = 'RegistrationsBaseUrl/3.6.0' },
                [ordered] @{ '@id' = $flatBase; '@type' = 'PackageBaseAddress/3.0.0' }
            )
        })
    Add-FixtureResponse -Responses $repositoryResponses `
        -Uri "$registrationBase/fixture.repository/index.json" -Response ([ordered] @{
            items = @([ordered] @{
                    '@id' = "$registrationBase/fixture.repository/page.json"
                    items = @((New-RegistrationLeaf -Version '1.0.0' -Listed $true))
                })
        })
    $repositoryRequestPath = Join-Path $repositoryFixtureRoot 'requests.json'
    Write-Utf8File -Path $repositoryRequestPath -Content (
        [ordered] @{ responses = $repositoryResponses } | ConvertTo-Json -Depth 20
    )
    $null = & git -C $repositoryFixtureRoot init --quiet
    $null = & git -C $repositoryFixtureRoot config user.name 'Package Audit Generator Tests'
    $null = & git -C $repositoryFixtureRoot config user.email 'package-audit-generator@example.invalid'
    $null = & git -C $repositoryFixtureRoot add -- .
    $null = & git -C $repositoryFixtureRoot commit --quiet -m 'test: repository provenance fixture'
    # The .gitattributes eol=crlf rule above only takes effect when Git materializes the
    # files again, so the worktree copies are removed and then re-checked out. Without
    # that re-materialization the whole fixture runs with worktree bytes == blob bytes
    # and proves nothing about normalizing checkouts.
    Remove-Item -LiteralPath $repositoryCatalogPath, $repositoryConsumerPath -Force
    $null = & git -C $repositoryFixtureRoot checkout-index --force --all
    foreach ($normalized in @($repositoryCatalogPath, $repositoryConsumerPath)) {
        $scenarioCount++
        if ([IO.File]::ReadAllBytes($normalized) -notcontains 13) {
            $failures.Add("Repository fixture '$normalized' was not checked out with normalized CRLF bytes.")
        }
    }

    $repositoryGeneratorPath = Join-Path $repositoryFixtureTools 'audit-central-package-versions.ps1'
    $repositoryAuditPath = Join-Path $repositoryFixtureTools 'audit.json'
    $repositoryGeneratorOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $repositoryGeneratorPath `
            -CatalogPath $repositoryCatalogPath -OutputPath $repositoryAuditPath `
            -Source @($sourceOne) -RequestFixturePath $repositoryRequestPath 2>&1)
    $scenarioCount++
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("Clean repository-owned fixture failed. $([string]::Join("`n", $repositoryGeneratorOutput))")
    }
    else {
        # The generator binds catalog and consumer provenance to committed blob bytes, so
        # the audit it produces must still validate on a checkout whose worktree bytes are
        # EOL-normalized away from those blobs.
        $repositoryValidatorOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $validatorPath `
                -AuditPath $repositoryAuditPath -RepositoryRootPath $repositoryFixtureRoot `
                -CatalogPath $repositoryCatalogPath -ConsumerScanRoot $repositoryFixtureRoot 2>&1)
        $scenarioCount++
        if ($LASTEXITCODE -ne 0) {
            $failures.Add(
                "Repository-owned audit did not pass deterministic validation on a normalizing checkout. $([string]::Join("`n", $repositoryValidatorOutput))"
            )
        }

        $repositoryAuditHash = (Get-FileHash -LiteralPath $repositoryAuditPath -Algorithm SHA256).Hash
        Write-Utf8File -Path $repositoryCatalogPath -Content (
            (Get-Content -LiteralPath $repositoryCatalogPath -Raw).Replace('Version="1.0.0"', 'Version="1.1.0"')
        )
        $null = & git -C $repositoryFixtureRoot add -- 'Props/Directory.Packages.props'
        $stagedCatalogOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $repositoryGeneratorPath `
                -CatalogPath $repositoryCatalogPath -OutputPath $repositoryAuditPath `
                -Source @($sourceOne) -RequestFixturePath $repositoryRequestPath 2>&1)
        $scenarioCount++
        $stagedCatalogExitCode = $LASTEXITCODE
        $stagedCatalogText = ($stagedCatalogOutput | ForEach-Object { [string] $_ }) -join "`n"
        if ($stagedCatalogExitCode -eq 0 -or $stagedCatalogText -notmatch "catalog 'Props/Directory\.Packages\.props' is dirty") {
            $failures.Add('A staged repository-owned catalog edit was not rejected against HEAD.')
        }
        Assert-Equal -Scenario 'Staged catalog failure preserves prior output atomically' `
            -Expected $repositoryAuditHash `
            -Actual (Get-FileHash -LiteralPath $repositoryAuditPath -Algorithm SHA256).Hash
        $null = & git -C $repositoryFixtureRoot restore --staged --worktree -- 'Props/Directory.Packages.props'

        [IO.File]::AppendAllText($repositoryConsumerPath, "`n<!-- staged consumer edit -->`n", [Text.UTF8Encoding]::new($false))
        $null = & git -C $repositoryFixtureRoot add -- 'Consumer.csproj'
        $stagedConsumerOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $repositoryGeneratorPath `
                -CatalogPath $repositoryCatalogPath -OutputPath $repositoryAuditPath `
                -Source @($sourceOne) -RequestFixturePath $repositoryRequestPath 2>&1)
        $scenarioCount++
        $stagedConsumerExitCode = $LASTEXITCODE
        $stagedConsumerText = ($stagedConsumerOutput | ForEach-Object { [string] $_ }) -join "`n"
        if ($stagedConsumerExitCode -eq 0 -or $stagedConsumerText -notmatch "consumer declaration 'Consumer\.csproj' is dirty") {
            $failures.Add('A staged repository-owned consumer edit was not rejected against HEAD.')
        }
        Assert-Equal -Scenario 'Staged consumer failure preserves prior output atomically' `
            -Expected $repositoryAuditHash `
            -Actual (Get-FileHash -LiteralPath $repositoryAuditPath -Algorithm SHA256).Hash
        $null = & git -C $repositoryFixtureRoot restore --staged --worktree -- 'Consumer.csproj'

        [IO.File]::AppendAllText(
            $repositoryConsumerPath,
            "`n<!--$('x' * 4096)-->`n",
            [Text.UTF8Encoding]::new($false)
        )
        $null = & git -C $repositoryFixtureRoot add -- 'Consumer.csproj'
        $null = & git -C $repositoryFixtureRoot commit --quiet -m 'test: oversized consumer blob'
        $oversizedConsumerOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $repositoryGeneratorPath `
                -CatalogPath $repositoryCatalogPath -OutputPath $repositoryAuditPath `
                -Source @($sourceOne) -RequestFixturePath $repositoryRequestPath -GitBlobReadMaxBytes 1024 2>&1)
        $scenarioCount++
        $oversizedConsumerExitCode = $LASTEXITCODE
        $oversizedConsumerText = ($oversizedConsumerOutput | ForEach-Object { [string] $_ }) -join "`n"
        if ($oversizedConsumerExitCode -eq 0 -or $oversizedConsumerText -notmatch "exceeded the 1024-byte limit for 'Consumer\.csproj'") {
            $failures.Add('An oversized committed consumer declaration blob was not rejected at the generator bound.')
        }

        $null = & git -C $repositoryFixtureRoot checkout 'HEAD^' -- 'Consumer.csproj'
        $null = & git -C $repositoryFixtureRoot commit --quiet -m 'test: restore consumer fixture'
        [IO.File]::AppendAllText(
            $repositoryCatalogPath,
            "`n<!--$('x' * 4096)-->`n",
            [Text.UTF8Encoding]::new($false)
        )
        $null = & git -C $repositoryFixtureRoot add -- 'Props/Directory.Packages.props'
        $null = & git -C $repositoryFixtureRoot commit --quiet -m 'test: oversized catalog blob'
        $oversizedCatalogOutput = @(& $pwshExecutable -NoLogo -NoProfile -File $repositoryGeneratorPath `
                -CatalogPath $repositoryCatalogPath -OutputPath $repositoryAuditPath `
                -Source @($sourceOne) -RequestFixturePath $repositoryRequestPath -GitBlobReadMaxBytes 1024 2>&1)
        $scenarioCount++
        $oversizedCatalogExitCode = $LASTEXITCODE
        $oversizedCatalogText = ($oversizedCatalogOutput | ForEach-Object { [string] $_ }) -join "`n"
        if ($oversizedCatalogExitCode -eq 0 -or $oversizedCatalogText -notmatch "exceeded the 1024-byte limit for 'Props/Directory\.Packages\.props'") {
            $failures.Add('An oversized committed catalog blob was not rejected at the generator bound.')
        }
        Assert-Equal -Scenario 'Oversized blob failures preserve prior output atomically' `
            -Expected $repositoryAuditHash `
            -Actual (Get-FileHash -LiteralPath $repositoryAuditPath -Algorithm SHA256).Hash

        # The generator's bounded Git reader is the validator's counterpart and must be
        # proved the same way: only its byte bound was previously exercised, so its
        # timeout and process-tree termination had no test surface at all.
        $generatorShimPath = Join-Path $repositoryFixtureRoot 'non-terminating-git-shim.ps1'
        $generatorShimReadyPath = Join-Path $repositoryFixtureRoot 'non-terminating-git.ready'
        New-NonTerminatingGitShim -ShimPath $generatorShimPath -ReadyPath $generatorShimReadyPath
        $timeoutStartInfo = [Diagnostics.ProcessStartInfo]::new()
        $timeoutStartInfo.FileName = $pwshExecutable
        $timeoutStartInfo.UseShellExecute = $false
        $timeoutStartInfo.RedirectStandardOutput = $true
        $timeoutStartInfo.RedirectStandardError = $true
        $timeoutStartInfo.CreateNoWindow = $true
        foreach ($argument in @(
                '-NoLogo', '-NoProfile', '-File', $repositoryGeneratorPath,
                '-CatalogPath', $repositoryCatalogPath, '-OutputPath', $repositoryAuditPath,
                '-Source', $sourceOne, '-RequestFixturePath', $repositoryRequestPath,
                '-GitBlobReaderShimPath', $generatorShimPath,
                '-GitBlobReadTimeoutSeconds', '3'
            )) {
            $null = $timeoutStartInfo.ArgumentList.Add($argument)
        }
        $timeoutProcess = [Diagnostics.Process]::new()
        $timeoutProcess.StartInfo = $timeoutStartInfo
        $generatorShimRecord = $null
        $scenarioCount++
        try {
            if (-not $timeoutProcess.Start()) {
                $failures.Add('The generator timeout scenario could not start.')
            }
            else {
                $timeoutStandardOutput = $timeoutProcess.StandardOutput.ReadToEndAsync()
                $timeoutStandardError = $timeoutProcess.StandardError.ReadToEndAsync()
                $readyStopwatch = [Diagnostics.Stopwatch]::StartNew()
                while ($readyStopwatch.ElapsedMilliseconds -lt 30000) {
                    $generatorShimRecord = Read-ReadyProcessRecord -Path $generatorShimReadyPath
                    if ($null -ne $generatorShimRecord -or $timeoutProcess.HasExited) { break }
                    Start-Sleep -Milliseconds 50
                }
                if ($null -eq $generatorShimRecord) {
                    $failures.Add('The generator timeout scenario never observed the shim readiness handshake.')
                }
                elseif (-not $generatorShimRecord.Valid) {
                    $failures.Add(
                        "The generator timeout scenario observed a malformed shim readiness record. $($generatorShimRecord.Diagnostic)"
                    )
                }
                if (-not $timeoutProcess.WaitForExit(30000)) {
                    $failures.Add('The generator did not abandon a non-terminating Git blob read within its bound.')
                    try { $timeoutProcess.Kill($true) } catch { }
                    try { $null = $timeoutProcess.WaitForExit(3000) } catch { }
                }
                $timeoutText = [string]::Join("`n", @(
                        $timeoutStandardOutput.GetAwaiter().GetResult(),
                        $timeoutStandardError.GetAwaiter().GetResult()
                    ))
                if ($timeoutProcess.HasExited -and $timeoutProcess.ExitCode -eq 0) {
                    $failures.Add("A non-terminating generator Git blob read was not rejected. $timeoutText")
                }
                elseif ($timeoutText -notmatch 'Git blob read timed out after 3 seconds') {
                    $failures.Add("The generator timeout diagnostic was not reported. $timeoutText")
                }
            }
        }
        finally {
            if ($null -ne $generatorShimRecord -and $generatorShimRecord.Valid) {
                foreach ($ownedProcessId in @($generatorShimRecord.ShimProcessId, $generatorShimRecord.ChildProcessId)) {
                    if (-not (Wait-ForProcessGone -ProcessId $ownedProcessId -TimeoutMilliseconds 5000)) {
                        $failures.Add("Generator-owned process $ownedProcessId survived process-tree cleanup.")
                        try { Stop-Process -Id $ownedProcessId -Force -ErrorAction SilentlyContinue } catch { }
                    }
                }
            }
            $timeoutProcess.Dispose()
        }
        Assert-Equal -Scenario 'Timed-out Git blob read preserves prior output atomically' `
            -Expected $repositoryAuditHash `
            -Actual (Get-FileHash -LiteralPath $repositoryAuditPath -Algorithm SHA256).Hash
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
