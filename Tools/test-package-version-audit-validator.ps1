[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validatorPath = Join-Path $PSScriptRoot 'validate-package-version-audit.ps1'
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$repositoryRevision = ([string] @(& git --no-replace-objects -C $repositoryRoot rev-parse HEAD 2>$null)[-1]).Trim()
if ($LASTEXITCODE -ne 0 -or $repositoryRevision -cnotmatch '^[0-9a-f]{40}$') {
    throw "Could not resolve the validator fixture repository revision."
}
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

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-CatalogSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)

    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $offset = if (
        $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xEF -and
        $bytes[1] -eq 0xBB -and
        $bytes[2] -eq 0xBF
    ) { 3 } else { 0 }
    $text = $utf8.GetString($bytes, $offset, $bytes.Length - $offset)
    $canonicalText = [regex]::Replace($text, "`r`n|`r|`n", "`r`n")
    $canonicalBytes = [byte[]] @(0xEF, 0xBB, 0xBF) + $utf8.GetBytes($canonicalText)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($canonicalBytes)).ToLowerInvariant()
}

function Get-FamilySelectionFingerprint {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Packages)

    $material = @($Packages | Sort-Object id | ForEach-Object { "$($_.id)|$($_.selectedVersion)" })
    return Get-Sha256Text -Value ([string]::Join("`n", $material))
}

function Get-SourceScopeFingerprint {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Sources)

    $material = @($Sources | ForEach-Object {
            "$($_.uri)|$($_.resolution)|$($_.diagnostic)"
        } | Sort-Object -CaseSensitive)
    return Get-Sha256Text -Value ([string]::Join("`n", $material))
}

function Get-ConsumerRelationFingerprint {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Entries)

    $material = @($Entries | ForEach-Object {
            "$($_.family)|$($_.consumer)|$($_.packageId)|$($_.declarationPath)|$($_.declarationSha256)"
        } | Sort-Object -CaseSensitive)
    return Get-Sha256Text -Value ([string]::Join("`n", $material))
}

function Get-PackageMetadataFingerprint {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Packages)

    $material = foreach ($package in @($Packages | Sort-Object id)) {
        $sourceMaterial = [string]::Join(',', @($package.sourceResults | Sort-Object source | ForEach-Object {
                    "$($_.source)=$($_.listingState):$($_.latestStable):$($_.latestPrerelease):$($_.diagnostic)"
                }))
        "$($package.id)|$($package.auditedVersion)|$($package.selectedVersion)|$($package.latestStable)|" +
            "$($package.latestPrerelease)|$($package.listingState)|$sourceMaterial"
    }

    return Get-Sha256Text -Value ([string]::Join("`n", @($material)))
}

function Update-AuditProvenance {
    param(
        [Parameter(Mandatory = $true)] $Audit,
        [Parameter(Mandatory = $true)][string] $AuditCatalogPath,
        [Parameter(Mandatory = $true)][ValidateSet('preserved', 'refreshed')][string] $PreservationStatus
    )

    $catalogRelativePath = [IO.Path]::GetRelativePath($repositoryRoot, $AuditCatalogPath).Replace('\', '/')
    $catalogSha256 = Get-CatalogSha256 -Path $AuditCatalogPath
    $Audit.catalogPath = $catalogRelativePath
    $Audit.catalogSha256 = $catalogSha256
    $Audit.catalogRawSha256 = (Get-FileHash -LiteralPath $AuditCatalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $Audit.snapshot.auditedAtUtc = '2026-07-31T12:00:00.0000000+00:00'
    $Audit.consumerEvidence.sha256 = Get-ConsumerRelationFingerprint -Entries @($Audit.consumerEvidence.entries)

    foreach ($decision in $Audit.familyDecisions) {
        $family = [string] $decision.family
        $familyPackages = @($Audit.packages | Where-Object { [string] $_.family -ceq $family })
        $familyConsumers = @($Audit.consumerEvidence.entries | Where-Object { [string] $_.family -ceq $family })
        $null = $PreservationStatus
        $decision.origin.auditedAtUtc = ([DateTimeOffset] $Audit.snapshot.auditedAtUtc).ToString('O')
        $decision.origin.generatedFromRevision = [string] $Audit.generatedFromRevision
        $decision.origin.familySelectionSha256 = Get-FamilySelectionFingerprint -Packages $familyPackages
        $decision.origin.sourceScopeSha256 = Get-SourceScopeFingerprint -Sources @($Audit.sources)
        $decision.origin.packageMetadataSha256 = Get-PackageMetadataFingerprint -Packages $familyPackages
        $decision.origin.consumerEvidenceSha256 = Get-ConsumerRelationFingerprint -Entries $familyConsumers
    }
}

function New-AuditFixture {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [string] $PackagePrefix = 'Fixture'
    )

    $firstPackageId = "$PackagePrefix.One"
    $secondPackageId = "$PackagePrefix.Two"

    $path = Join-Path $temporaryRoot "$Name.json"
    $catalogSha256 = Get-CatalogSha256 -Path $catalogPath
    $sourceScopeSha256 = Get-Sha256Text -Value (
        'https://api.nuget.org/v3/index.json|resolved|Fixture source resolved.'
    )
    $fixturePath = Join-Path $temporaryRoot 'validator-fixture.json'
    $fixtureDocument = [ordered] @{
        entries = @(
            [ordered] @{ consumer = 'Fixture.Consumer'; packageId = $firstPackageId },
            [ordered] @{ consumer = 'Fixture.Consumer'; packageId = $secondPackageId }
        )
    }
    Write-Utf8File -Path $fixturePath -Content ($fixtureDocument | ConvertTo-Json -Depth 4)
    $declarationSha256 = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $consumerSha256 = Get-Sha256Text -Value (
        "fixture-family|Fixture.Consumer|$firstPackageId|validator-fixture.json|$declarationSha256`n" +
        "fixture-family|Fixture.Consumer|$secondPackageId|validator-fixture.json|$declarationSha256"
    )
    $packageMetadataSha256 = Get-Sha256Text -Value (
        "$firstPackageId|1.0.0|1.0.0|1.0.0||listed|" +
        'https://api.nuget.org/v3/index.json=listed:1.0.0::Registration metadata resolved.' + "`n" +
        "$secondPackageId|2.0.0|2.0.0|2.1.0|3.0.0-preview.1|unlisted|" +
        'https://api.nuget.org/v3/index.json=unlisted:2.1.0:3.0.0-preview.1:Registration metadata resolved.'
    )
    $audit = [ordered] @{
        schemaVersion = 2
        generatedFromRevision = $repositoryRevision
        catalogPath = [IO.Path]::GetRelativePath($repositoryRoot, $catalogPath).Replace('\', '/')
        catalogSha256 = $catalogSha256
        catalogRawSha256 = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
        snapshot = [ordered] @{
            mode = 'complete'
            auditedAtUtc = '2026-07-31T12:00:00.0000000+00:00'
            refreshedFamilies = @('fixture-family')
            preservedFamilies = @()
        }
        sources = @(
            [ordered] @{
                uri = 'https://api.nuget.org/v3/index.json'
                resolution = 'resolved'
                diagnostic = 'Fixture source resolved.'
            }
        )
        consumerEvidence = [ordered] @{
            schema = 'hexalith.package-consumer-evidence.v1'
            discovery = 'explicit-fixture'
            fixture = 'validator-fixture.json'
            fixtureSha256 = $declarationSha256
            fixtureMode = 'synthetic-explicit'
            repositoryRevision = $repositoryRevision
            sha256 = $consumerSha256
            entries = @(
                [ordered] @{
                    family = 'fixture-family'
                    consumer = 'Fixture.Consumer'
                    packageId = $firstPackageId
                    declarationPath = 'validator-fixture.json'
                    declarationSha256 = $declarationSha256
                },
                [ordered] @{
                    family = 'fixture-family'
                    consumer = 'Fixture.Consumer'
                    packageId = $secondPackageId
                    declarationPath = 'validator-fixture.json'
                    declarationSha256 = $declarationSha256
                }
            )
        }
        familyDecisions = @(
            [ordered] @{
                family = 'fixture-family'
                disposition = 'retained'
                rollbackGroup = 'fixture-family'
                packageIds = @($firstPackageId, $secondPackageId)
                rationale = 'Retained fixture family.'
                compatibilityEvidence = 'Fixture evidence.'
                removalTrigger = 'Re-run fixture validation.'
                representativeConsumers = @('Fixture.Consumer')
                origin = [ordered] @{
                    auditedAtUtc = '2026-07-31T12:00:00.0000000+00:00'
                    generatedFromRevision = $repositoryRevision
                    familySelectionSha256 = Get-Sha256Text -Value (
                        "$firstPackageId|1.0.0`n$secondPackageId|2.0.0"
                    )
                    sourceScopeSha256 = $sourceScopeSha256
                    packageMetadataSha256 = $packageMetadataSha256
                    consumerEvidenceSha256 = $consumerSha256
                }
                historicalContext = @()
            }
        )
        packages = @(
            [ordered] @{
                id = $firstPackageId
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
                historicalContext = @()
            },
            [ordered] @{
                id = $secondPackageId
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
                historicalContext = @()
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

function Add-CompleteHistoricalContexts {
    param([Parameter(Mandatory = $true)] $Audit)

    $package = $Audit.packages[0]
    $package.historicalContext = @([pscustomobject][ordered] @{
            schema = 'hexalith.package-audit-package-history.v1'
            label = 'Typed package history fixture.'
            auditedAtUtc = '2026-07-30T12:00:00.0000000+00:00'
            generatedFromRevision = ('b' * 40)
            id = $package.id
            auditedVersion = $package.auditedVersion
            selectedVersion = $package.selectedVersion
            latestStable = $package.latestStable
            latestPrerelease = $package.latestPrerelease
            listingState = $package.listingState
            family = $package.family
            disposition = $package.disposition
            rollbackGroup = $package.rollbackGroup
            rationale = $package.rationale
            evidence = $package.evidence
            removalTrigger = $package.removalTrigger
            sourceResults = @($package.sourceResults) + @([pscustomobject][ordered] @{
                    source = 'https://historical.example.test/v3/index.json'
                    listingState = 'unresolved'
                    latestStable = $null
                    latestPrerelease = $null
                    diagnostic = 'Historical source was unavailable.'
                })
            supersededBecause = 'Superseded by the current typed fixture audit.'
        })

    $family = $Audit.familyDecisions[0]
    $family.historicalContext = @([pscustomobject][ordered] @{
            schema = 'hexalith.package-audit-family-history.v1'
            label = 'Typed family history fixture.'
            auditedAtUtc = '2026-07-30T12:00:00.0000000+00:00'
            generatedFromRevision = ('b' * 40)
            family = $family.family
            disposition = $family.disposition
            rollbackGroup = $family.rollbackGroup
            packageIds = @($family.packageIds)
            rationale = $family.rationale
            compatibilityEvidence = $family.compatibilityEvidence
            removalTrigger = $family.removalTrigger
            representativeConsumers = @($family.representativeConsumers) + @('Fixture.Prior.Consumer')
            preservation = [pscustomobject][ordered] @{
                status = 'legacy-unbound'
                reason = 'Historical fixture intentionally predates byte-bound hashes.'
            }
            supersededBecause = 'Superseded by the current typed fixture audit.'
        })
}

function Add-V2HistoricalContexts {
    param([Parameter(Mandatory = $true)] $Audit)

    $historyTimestamp = '2026-07-30T12:00:00.0000000+00:00'
    $family = $Audit.familyDecisions[0]
    $origin = $family.origin | ConvertTo-Json -Depth 10 | ConvertFrom-Json -DateKind String
    $origin.auditedAtUtc = $historyTimestamp

    $package = $Audit.packages[0]
    $package.historicalContext = @([pscustomobject][ordered] @{
            schema = 'hexalith.package-audit-package-history.v2'
            label = 'Historical package observation fixture.'
            auditedAtUtc = $historyTimestamp
            generatedFromRevision = $repositoryRevision
            id = $package.id
            auditedVersion = $package.auditedVersion
            selectedVersion = $package.selectedVersion
            latestStable = $package.latestStable
            latestPrerelease = $package.latestPrerelease
            listingState = $package.listingState
            family = $package.family
            disposition = $package.disposition
            rollbackGroup = $package.rollbackGroup
            rationale = $package.rationale
            evidence = $package.evidence
            removalTrigger = $package.removalTrigger
            sourceResults = @($package.sourceResults)
            origin = $origin
            supersededBecause = 'Superseded by the current fixture observation.'
        })

    $family.historicalContext = @([pscustomobject][ordered] @{
            schema = 'hexalith.package-audit-family-history.v2'
            label = 'Historical family observation fixture.'
            auditedAtUtc = $historyTimestamp
            generatedFromRevision = $repositoryRevision
            family = $family.family
            disposition = $family.disposition
            rollbackGroup = $family.rollbackGroup
            packageIds = @($family.packageIds)
            rationale = $family.rationale
            compatibilityEvidence = $family.compatibilityEvidence
            removalTrigger = $family.removalTrigger
            representativeConsumers = @($family.representativeConsumers)
            origin = ($origin | ConvertTo-Json -Depth 10 | ConvertFrom-Json -DateKind String)
            supersededBecause = 'Superseded by the current fixture observation.'
        })
}

function Test-Scenario {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $AuditPath,
        [Parameter(Mandatory = $true)][int] $ExpectedExitCode,
        [Parameter(Mandatory = $true)][string] $ExpectedOutput,
        # Isolated by default so the PackageReference-rediscovery leg (task 6) only
        # ever sees this script's own fixture project files, never the real
        # repository tree, unless a scenario explicitly opts in.
        [string] $ConsumerScanRoot = $emptyConsumerScanRoot,
        [string] $RepositoryRootPath = $repositoryRoot
    )

    $script:scenarioCount++
    $output = @(& $pwshExecutable -NoLogo -NoProfile -File $validatorPath `
            -AuditPath $AuditPath -CatalogPath $catalogPath -EvaluatorScriptPath $evaluatorPath `
            -ConsumerScanRoot $ConsumerScanRoot -RepositoryRootPath $RepositoryRootPath 2>&1)
    $result = [string]::Join("`n", @($output | ForEach-Object { [string] $_ }))
    if ($LASTEXITCODE -ne $ExpectedExitCode) {
        $script:failures.Add("$Name expected exit code $ExpectedExitCode but received $LASTEXITCODE. Output: $result")
    }
    elseif ($result -notlike "*$ExpectedOutput*") {
        $script:failures.Add("$Name output did not contain '$ExpectedOutput'. Output: $result")
    }
}

function Test-RepositoryScenario {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $AuditPath,
        [Parameter(Mandatory = $true)][int] $ExpectedExitCode,
        [Parameter(Mandatory = $true)][string] $ExpectedOutput,
        [string] $RepositoryRootPath = '',
        [string] $CatalogPath = '',
        [string] $GitBlobReaderShimPath = '',
        [int] $GitBlobReadTimeoutSeconds = 10,
        [int] $GitBlobReadMaxBytes = 1048576
    )

    $script:scenarioCount++
    $arguments = @('-NoLogo', '-NoProfile', '-File', $validatorPath, '-AuditPath', $AuditPath)
    if (-not [string]::IsNullOrWhiteSpace($RepositoryRootPath)) {
        $arguments += @('-RepositoryRootPath', $RepositoryRootPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($CatalogPath)) {
        $arguments += @('-CatalogPath', $CatalogPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($GitBlobReaderShimPath)) {
        $arguments += @('-GitBlobReaderShimPath', $GitBlobReaderShimPath)
    }
    $arguments += @(
        '-GitBlobReadTimeoutSeconds', [string] $GitBlobReadTimeoutSeconds,
        '-GitBlobReadMaxBytes', [string] $GitBlobReadMaxBytes
    )

    $output = @(& $pwshExecutable @arguments 2>&1)
    $result = [string]::Join("`n", @($output | ForEach-Object { [string] $_ }))
    if ($LASTEXITCODE -ne $ExpectedExitCode) {
        $script:failures.Add("$Name expected exit code $ExpectedExitCode but received $LASTEXITCODE. Output: $result")
    }
    elseif ($result -notlike "*$ExpectedOutput*") {
        $script:failures.Add("$Name output did not contain '$ExpectedOutput'. Output: $result")
    }
}

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
        Diagnostic = ''
    }
}

function Test-ReadyRepositoryScenario {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $AuditPath,
        [Parameter(Mandatory = $true)][string] $RepositoryRootPath,
        [Parameter(Mandatory = $true)][string] $CatalogPath,
        [Parameter(Mandatory = $true)][string] $GitBlobReaderShimPath,
        [Parameter(Mandatory = $true)][string] $ReadyPath,
        [Parameter(Mandatory = $true)][int] $GitBlobReadTimeoutSeconds,
        [Parameter(Mandatory = $true)][string] $ExpectedOutput
    )

    $script:scenarioCount++
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwshExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @(
            '-NoLogo', '-NoProfile', '-File', $validatorPath,
            '-AuditPath', $AuditPath,
            '-RepositoryRootPath', $RepositoryRootPath,
            '-CatalogPath', $CatalogPath,
            '-GitBlobReaderShimPath', $GitBlobReaderShimPath,
            '-GitBlobReadTimeoutSeconds', [string] $GitBlobReadTimeoutSeconds,
            '-GitBlobReadMaxBytes', '1048576'
        )) {
        $null = $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $readyRecord = $null
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $process.Start()) {
            $failures.Add("$Name validator process could not start.")
            return
        }
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()

        $readyStopwatch = [Diagnostics.Stopwatch]::StartNew()
        while ($readyStopwatch.ElapsedMilliseconds -lt 15000) {
            $readyRecord = Read-ReadyProcessRecord -Path $ReadyPath
            if ($null -ne $readyRecord -or $process.HasExited) { break }
            Start-Sleep -Milliseconds 50
        }
        if ($null -eq $readyRecord) {
            $failures.Add("$Name did not observe the shim's atomic readiness handshake before validator exit/timeout.")
        }
        elseif (-not $readyRecord.Valid) {
            $failures.Add("$Name observed a malformed shim readiness record. $($readyRecord.Diagnostic)")
        }
        else {
            # The atomic record proves both owned processes exist. Give the shim a
            # small startup margin so the timeout is exercised while it is inside
            # its non-terminating phase rather than racing its launch path.
            Start-Sleep -Milliseconds 250
        }

        if (-not $process.WaitForExit(20000)) {
            $failures.Add("$Name validator process exceeded its bounded harness window.")
            try { $process.Kill($true) } catch { }
            try { $null = $process.WaitForExit(3000) } catch { }
        }
        else {
            $process.WaitForExit()
        }
        $result = [string]::Join("`n", @(
                $standardOutputTask.GetAwaiter().GetResult(),
                $standardErrorTask.GetAwaiter().GetResult()
            ))
        if ($process.HasExited -and $process.ExitCode -ne 1) {
            $failures.Add("$Name expected exit code 1 but received $($process.ExitCode). Output: $result")
        }
        elseif ($result -notlike "*$ExpectedOutput*") {
            $failures.Add("$Name output did not contain '$ExpectedOutput'. Output: $result")
        }
    }
    catch {
        $failures.Add("$Name harness failed closed. $($_.Exception.GetBaseException().Message)")
        try { if (-not $process.HasExited) { $process.Kill($true) } } catch { }
        try { $null = $process.WaitForExit(3000) } catch { }
    }
    finally {
        $stopwatch.Stop()
        if ($stopwatch.Elapsed.TotalSeconds -ge 20) {
            $failures.Add("$Name took $($stopwatch.Elapsed.TotalSeconds) seconds and exceeded its bounded harness window.")
        }
        if ($null -ne $readyRecord -and $readyRecord.Valid) {
            if (-not (Wait-ForProcessGone -ProcessId $readyRecord.ShimProcessId -TimeoutMilliseconds 3000)) {
                $failures.Add("$Name shim process $($readyRecord.ShimProcessId) survived process-tree cleanup.")
            }
            if (-not (Wait-ForProcessGone -ProcessId $readyRecord.ChildProcessId -TimeoutMilliseconds 3000)) {
                $failures.Add("$Name shim child process $($readyRecord.ChildProcessId) survived process-tree cleanup.")
            }
        }
        $process.Dispose()
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
[xml] $catalog = Get-Content -LiteralPath $CatalogPath -Raw
$items = @(
    $catalog.SelectNodes("//*[local-name()='PackageVersion']") |
        ForEach-Object {
            [ordered] @{
                Identity = [string] $_.Include
                Version = [string] $_.Version
            }
        }
)
[Console]::Out.WriteLine((@{ Items = @{ PackageVersion = $items } } | ConvertTo-Json -Depth 5 -Compress))
'@

    $emptyConsumerScanRoot = Join-Path $temporaryRoot 'empty-consumer-scan-root'
    New-Item -ItemType Directory -Path $emptyConsumerScanRoot -Force | Out-Null

    $validPath = New-AuditFixture -Name 'valid'
    Test-Scenario -Name 'Complete audit' -AuditPath $validPath -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 2 packages, 1 families, and 1 source'

    $stringSchemaPath = New-AuditFixture -Name 'string-schema-version'
    $stringSchemaAudit = Get-Content -LiteralPath $stringSchemaPath -Raw | ConvertFrom-Json -DateKind String
    $stringSchemaAudit.schemaVersion = '2'
    Save-Audit -Audit $stringSchemaAudit -Path $stringSchemaPath
    Test-Scenario -Name 'Schema version must be a JSON integer' -AuditPath $stringSchemaPath -ExpectedExitCode 1 `
        -ExpectedOutput 'schemaVersion must be the JSON integer 2'

    $unknownConfiguredSourcePath = New-AuditFixture -Name 'unknown-configured-source-field'
    $unknownConfiguredSourceAudit = Get-Content -LiteralPath $unknownConfiguredSourcePath -Raw | ConvertFrom-Json -DateKind String
    $unknownConfiguredSourceAudit.sources[0] | Add-Member -NotePropertyName mirror -NotePropertyValue $true
    Save-Audit -Audit $unknownConfiguredSourceAudit -Path $unknownConfiguredSourcePath
    Test-Scenario -Name 'Configured source has a closed shape' -AuditPath $unknownConfiguredSourcePath -ExpectedExitCode 1 `
        -ExpectedOutput "Source record declares field 'mirror'"

    $wrongConfiguredSourceTypePath = New-AuditFixture -Name 'wrong-configured-source-type'
    $wrongConfiguredSourceTypeAudit = Get-Content -LiteralPath $wrongConfiguredSourceTypePath -Raw | ConvertFrom-Json -DateKind String
    $wrongConfiguredSourceTypeAudit.sources[0].diagnostic = 42
    Save-Audit -Audit $wrongConfiguredSourceTypeAudit -Path $wrongConfiguredSourceTypePath
    Test-Scenario -Name 'Configured source fields retain JSON types' `
        -AuditPath $wrongConfiguredSourceTypePath -ExpectedExitCode 1 `
        -ExpectedOutput "Source record field 'diagnostic' must be a JSON string"

    $relativeConfiguredSourcePath = New-AuditFixture -Name 'relative-configured-source-uri'
    $relativeConfiguredSourceAudit = Get-Content -LiteralPath $relativeConfiguredSourcePath -Raw | ConvertFrom-Json -DateKind String
    $relativeConfiguredSourceAudit.sources[0].uri = '../feed/index.json'
    Save-Audit -Audit $relativeConfiguredSourceAudit -Path $relativeConfiguredSourcePath
    Test-Scenario -Name 'Configured source URI must be absolute' `
        -AuditPath $relativeConfiguredSourcePath -ExpectedExitCode 1 `
        -ExpectedOutput "Configured source '../feed/index.json' must be a valid absolute URI"

    $unknownConsumerEnvelopePath = New-AuditFixture -Name 'unknown-consumer-envelope-field'
    $unknownConsumerEnvelopeAudit = Get-Content -LiteralPath $unknownConsumerEnvelopePath -Raw | ConvertFrom-Json -DateKind String
    $unknownConsumerEnvelopeAudit.consumerEvidence | Add-Member -NotePropertyName reviewer -NotePropertyValue 'owner'
    Save-Audit -Audit $unknownConsumerEnvelopeAudit -Path $unknownConsumerEnvelopePath
    Test-Scenario -Name 'Consumer evidence envelope has a closed shape' `
        -AuditPath $unknownConsumerEnvelopePath -ExpectedExitCode 1 `
        -ExpectedOutput "Consumer evidence declares field 'reviewer'"

    $wrongConsumerEnvelopeTypePath = New-AuditFixture -Name 'wrong-consumer-envelope-type'
    $wrongConsumerEnvelopeTypeAudit = Get-Content -LiteralPath $wrongConsumerEnvelopeTypePath -Raw | ConvertFrom-Json -DateKind String
    $wrongConsumerEnvelopeTypeAudit.consumerEvidence.repositoryRevision = 42
    Save-Audit -Audit $wrongConsumerEnvelopeTypeAudit -Path $wrongConsumerEnvelopeTypePath
    Test-Scenario -Name 'Consumer evidence envelope fields retain JSON types' `
        -AuditPath $wrongConsumerEnvelopeTypePath -ExpectedExitCode 1 `
        -ExpectedOutput "Consumer evidence field 'repositoryRevision' must be a JSON string"

    $unknownConsumerEntryPath = New-AuditFixture -Name 'unknown-consumer-entry-field'
    $unknownConsumerEntryAudit = Get-Content -LiteralPath $unknownConsumerEntryPath -Raw | ConvertFrom-Json -DateKind String
    $unknownConsumerEntryAudit.consumerEvidence.entries[0] | Add-Member -NotePropertyName transitive -NotePropertyValue $false
    Save-Audit -Audit $unknownConsumerEntryAudit -Path $unknownConsumerEntryPath
    Test-Scenario -Name 'Consumer evidence entries have a closed shape' `
        -AuditPath $unknownConsumerEntryPath -ExpectedExitCode 1 `
        -ExpectedOutput "Consumer evidence entry declares field 'transitive'"

    $wrongConsumerEntryTypePath = New-AuditFixture -Name 'wrong-consumer-entry-type'
    $wrongConsumerEntryTypeAudit = Get-Content -LiteralPath $wrongConsumerEntryTypePath -Raw | ConvertFrom-Json -DateKind String
    $wrongConsumerEntryTypeAudit.consumerEvidence.entries[0].consumer = 42
    Save-Audit -Audit $wrongConsumerEntryTypeAudit -Path $wrongConsumerEntryTypePath
    Test-Scenario -Name 'Consumer evidence entry fields retain JSON types' `
        -AuditPath $wrongConsumerEntryTypePath -ExpectedExitCode 1 `
        -ExpectedOutput "Consumer evidence entry field 'consumer' must be a JSON string"

    $diagnosticDriftPath = New-AuditFixture -Name 'source-diagnostic-drift'
    $diagnosticDriftAudit = Get-Content -LiteralPath $diagnosticDriftPath -Raw | ConvertFrom-Json -DateKind String
    $diagnosticDriftAudit.packages[0].sourceResults[0].diagnostic = 'Different registration diagnostic.'
    Save-Audit -Audit $diagnosticDriftAudit -Path $diagnosticDriftPath
    Test-Scenario -Name 'Source diagnostic participates in metadata fingerprint' `
        -AuditPath $diagnosticDriftPath -ExpectedExitCode 1 `
        -ExpectedOutput 'origin packageMetadataSha256 does not match its package/source relations'

    $emptyIncrementalPath = New-AuditFixture -Name 'empty-incremental-refresh'
    $emptyIncrementalAudit = Get-Content -LiteralPath $emptyIncrementalPath -Raw | ConvertFrom-Json -DateKind String
    $emptyIncrementalAudit.snapshot.mode = 'incremental'
    $emptyIncrementalAudit.snapshot.refreshedFamilies = @()
    $emptyIncrementalAudit.snapshot.preservedFamilies = @('fixture-family')
    Save-Audit -Audit $emptyIncrementalAudit -Path $emptyIncrementalPath
    Test-Scenario -Name 'Incremental snapshot refresh is nonempty' `
        -AuditPath $emptyIncrementalPath -ExpectedExitCode 1 `
        -ExpectedOutput 'Incremental snapshot mode must refresh at least one family'

    $futureOriginPath = New-AuditFixture -Name 'future-current-origin'
    $futureOriginAudit = Get-Content -LiteralPath $futureOriginPath -Raw | ConvertFrom-Json -DateKind String
    $futureOriginAudit.familyDecisions[0].origin.auditedAtUtc = '2026-08-01T12:00:00.0000000+00:00'
    Save-Audit -Audit $futureOriginAudit -Path $futureOriginPath
    Test-Scenario -Name 'Current origin cannot postdate snapshot' `
        -AuditPath $futureOriginPath -ExpectedExitCode 1 `
        -ExpectedOutput "origin auditedAtUtc must not be later than the snapshot timestamp"

    $refreshedOriginPath = New-AuditFixture -Name 'refreshed-origin-mismatch'
    $refreshedOriginAudit = Get-Content -LiteralPath $refreshedOriginPath -Raw | ConvertFrom-Json -DateKind String
    $refreshedOriginAudit.familyDecisions[0].origin.auditedAtUtc = '2026-07-30T12:00:00.0000000+00:00'
    Save-Audit -Audit $refreshedOriginAudit -Path $refreshedOriginPath
    Test-Scenario -Name 'Refreshed origin equals current snapshot' `
        -AuditPath $refreshedOriginPath -ExpectedExitCode 1 `
        -ExpectedOutput "Refreshed family 'fixture-family' origin must equal the current snapshot revision and timestamp"

    $unavailableOriginPath = New-AuditFixture -Name 'unavailable-current-origin'
    $unavailableOriginAudit = Get-Content -LiteralPath $unavailableOriginPath -Raw | ConvertFrom-Json -DateKind String
    $unavailableOriginAudit.familyDecisions[0].origin.generatedFromRevision = ('0' * 40)
    Save-Audit -Audit $unavailableOriginAudit -Path $unavailableOriginPath
    Test-Scenario -Name 'Current origin revision must be available' `
        -AuditPath $unavailableOriginPath -ExpectedExitCode 1 `
        -ExpectedOutput "origin revision '$('0' * 40)' is not an available Git commit"

    $preservedCurrentOriginPath = New-AuditFixture -Name 'preserved-current-origin'
    $preservedCurrentOriginAudit = Get-Content -LiteralPath $preservedCurrentOriginPath -Raw | ConvertFrom-Json -DateKind String
    $preservedCurrentOriginAudit.snapshot.mode = 'incremental'
    $preservedCurrentOriginAudit.snapshot.refreshedFamilies = @()
    $preservedCurrentOriginAudit.snapshot.preservedFamilies = @('fixture-family')
    Save-Audit -Audit $preservedCurrentOriginAudit -Path $preservedCurrentOriginPath
    Test-Scenario -Name 'Preserved origin cannot claim current refresh' `
        -AuditPath $preservedCurrentOriginPath -ExpectedExitCode 1 `
        -ExpectedOutput "Preserved family 'fixture-family' origin must differ from the current refresh revision/timestamp pair"

    $originRepository = Join-Path $temporaryRoot 'origin-repository'
    New-Item -ItemType Directory -Path $originRepository -Force | Out-Null
    Write-Utf8File -Path (Join-Path $originRepository 'README.md') -Content "origin fixture`n"
    $null = & git -C $originRepository init --quiet
    $null = & git -C $originRepository config user.name 'Package Audit Validator Tests'
    $null = & git -C $originRepository config user.email 'package-audit-validator@example.invalid'
    $null = & git -C $originRepository add -- .
    $null = & git -C $originRepository commit --quiet -m 'test: origin base'
    $originRepositoryRevision = ([string] @(& git -C $originRepository rev-parse HEAD)[-1]).Trim()
    $originTree = ([string] @(& git -C $originRepository rev-parse 'HEAD^{tree}')[-1]).Trim()
    $nonAncestorOriginRevision = ([string] @(
            & git -C $originRepository commit-tree $originTree -m 'test: unrelated origin'
        )[-1]).Trim()

    $nonAncestorOriginPath = New-AuditFixture -Name 'non-ancestor-current-origin'
    $nonAncestorOriginAudit = Get-Content -LiteralPath $nonAncestorOriginPath -Raw | ConvertFrom-Json -DateKind String
    $nonAncestorOriginAudit.generatedFromRevision = $originRepositoryRevision
    $nonAncestorOriginAudit.consumerEvidence.repositoryRevision = $originRepositoryRevision
    $nonAncestorOriginAudit.catalogPath = [IO.Path]::GetRelativePath($originRepository, $catalogPath).Replace('\', '/')
    $nonAncestorOriginAudit.familyDecisions[0].origin.generatedFromRevision = $nonAncestorOriginRevision
    Save-Audit -Audit $nonAncestorOriginAudit -Path $nonAncestorOriginPath
    Test-Scenario -Name 'Current origin revision must be an ancestor' `
        -AuditPath $nonAncestorOriginPath -RepositoryRootPath $originRepository -ExpectedExitCode 1 `
        -ExpectedOutput "origin revision '$nonAncestorOriginRevision' is not an ancestor of generatedFromRevision '$originRepositoryRevision'"

    $nonAncestorHistoryPath = New-AuditFixture -Name 'non-ancestor-history-origin'
    $nonAncestorHistoryAudit = Get-Content -LiteralPath $nonAncestorHistoryPath -Raw | ConvertFrom-Json -DateKind String
    $nonAncestorHistoryAudit.generatedFromRevision = $originRepositoryRevision
    $nonAncestorHistoryAudit.consumerEvidence.repositoryRevision = $originRepositoryRevision
    $nonAncestorHistoryAudit.catalogPath = [IO.Path]::GetRelativePath($originRepository, $catalogPath).Replace('\', '/')
    $nonAncestorHistoryAudit.familyDecisions[0].origin.generatedFromRevision = $originRepositoryRevision
    Add-V2HistoricalContexts -Audit $nonAncestorHistoryAudit
    $nonAncestorHistoryAudit.packages[0].historicalContext[0].generatedFromRevision = $nonAncestorOriginRevision
    $nonAncestorHistoryAudit.packages[0].historicalContext[0].origin.generatedFromRevision = $nonAncestorOriginRevision
    $nonAncestorHistoryAudit.familyDecisions[0].historicalContext[0].generatedFromRevision = $originRepositoryRevision
    $nonAncestorHistoryAudit.familyDecisions[0].historicalContext[0].origin.generatedFromRevision = $originRepositoryRevision
    Save-Audit -Audit $nonAncestorHistoryAudit -Path $nonAncestorHistoryPath
    Test-Scenario -Name 'Historical origin revision must be an ancestor' `
        -AuditPath $nonAncestorHistoryPath -RepositoryRootPath $originRepository -ExpectedExitCode 1 `
        -ExpectedOutput "historical context origin revision '$nonAncestorOriginRevision' is not an ancestor of generatedFromRevision '$originRepositoryRevision'"

    $rawCatalogHashPath = New-AuditFixture -Name 'raw-catalog-hash-mismatch'
    $rawCatalogHashAudit = Get-Content -LiteralPath $rawCatalogHashPath -Raw | ConvertFrom-Json -DateKind String
    $rawCatalogHashAudit.catalogRawSha256 = ('0' * 64)
    Save-Audit -Audit $rawCatalogHashAudit -Path $rawCatalogHashPath
    Test-Scenario -Name 'Raw catalog byte hash mismatch' -AuditPath $rawCatalogHashPath -ExpectedExitCode 1 `
        -ExpectedOutput 'catalogRawSha256 does not match the exact evaluated catalog bytes'

    $snapshotGapPath = New-AuditFixture -Name 'snapshot-family-gap'
    $snapshotGapAudit = Get-Content -LiteralPath $snapshotGapPath -Raw | ConvertFrom-Json -DateKind String
    $snapshotGapAudit.snapshot.refreshedFamilies = @()
    Save-Audit -Audit $snapshotGapAudit -Path $snapshotGapPath
    Test-Scenario -Name 'Snapshot partition family gap' -AuditPath $snapshotGapPath -ExpectedExitCode 1 `
        -ExpectedOutput "Snapshot partition does not cover catalog family 'fixture-family'"

    $snapshotOverlapPath = New-AuditFixture -Name 'snapshot-family-overlap'
    $snapshotOverlapAudit = Get-Content -LiteralPath $snapshotOverlapPath -Raw | ConvertFrom-Json -DateKind String
    $snapshotOverlapAudit.snapshot.mode = 'incremental'
    $snapshotOverlapAudit.snapshot.preservedFamilies = @('fixture-family')
    Save-Audit -Audit $snapshotOverlapAudit -Path $snapshotOverlapPath
    Test-Scenario -Name 'Snapshot partition family overlap' -AuditPath $snapshotOverlapPath -ExpectedExitCode 1 `
        -ExpectedOutput "Snapshot family 'fixture-family' appears in both refreshed and preserved partitions"

    $unknownSnapshotFieldPath = New-AuditFixture -Name 'unknown-snapshot-field'
    $unknownSnapshotFieldAudit = Get-Content -LiteralPath $unknownSnapshotFieldPath -Raw | ConvertFrom-Json -DateKind String
    $unknownSnapshotFieldAudit.snapshot |
        Add-Member -NotePropertyName reviewer -NotePropertyValue 'untyped-reviewer'
    Save-Audit -Audit $unknownSnapshotFieldAudit -Path $unknownSnapshotFieldPath
    Test-Scenario -Name 'Unknown snapshot field would be silently dropped' `
        -AuditPath $unknownSnapshotFieldPath -ExpectedExitCode 1 `
        -ExpectedOutput "Snapshot declares field 'reviewer' that the typed round-trip model does not cover"

    $internalAdvancePath = New-AuditFixture -Name 'internal-family-advance' -PackagePrefix 'Hexalith.Fixture'
    $internalAdvanceCatalogPath = Join-Path $temporaryRoot 'internal-family-advance.props'
    Write-Utf8File -Path $internalAdvanceCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Fixture.One" Version="1.1.0" /><PackageVersion Include="Hexalith.Fixture.Two" Version="1.1.0" /></ItemGroup></Project>
'@
    $internalAdvanceAudit = Get-Content -LiteralPath $internalAdvancePath -Raw | ConvertFrom-Json
    $internalAdvanceAudit.snapshot.auditedAtUtc = '2026-07-31T12:00:00.0000000Z'
    foreach ($package in $internalAdvanceAudit.packages) {
        $package.auditedVersion = '1.0.0'
        $package.selectedVersion = '1.1.0'
        $package.latestStable = '1.1.0'
        $package.listingState = 'listed'
        $package.disposition = 'accepted'
        $package.sourceResults[0].listingState = 'listed'
        $package.sourceResults[0].latestStable = '1.1.0'
    }
    $internalAdvanceAudit.familyDecisions[0].disposition = 'accepted'
    Update-AuditProvenance -Audit $internalAdvanceAudit -AuditCatalogPath $internalAdvanceCatalogPath `
        -PreservationStatus preserved
    Save-Audit -Audit $internalAdvanceAudit -Path $internalAdvancePath
    $catalogPath = $internalAdvanceCatalogPath
    Test-Scenario -Name 'Aligned internal family advance' -AuditPath $internalAdvancePath -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 2 packages, 1 families, and 1 source'

    Write-Utf8File -Path $internalAdvanceCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Fixture.One" Version="0.9.0" /><PackageVersion Include="Hexalith.Fixture.Two" Version="0.9.0" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Internal family downgrade' -AuditPath $internalAdvancePath -ExpectedExitCode 1 `
        -ExpectedOutput "Internal package 'Hexalith.Fixture.One' cannot downgrade accepted version floor '1.1.0' to catalog version '0.9.0'"

    Write-Utf8File -Path $internalAdvanceCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Fixture.One" Version="1.0.5" /><PackageVersion Include="Hexalith.Fixture.Two" Version="1.0.5" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Internal regression above old audited floor' -AuditPath $internalAdvancePath -ExpectedExitCode 1 `
        -ExpectedOutput "Internal package 'Hexalith.Fixture.One' cannot downgrade accepted version floor '1.1.0' to catalog version '1.0.5'"

    $missingCandidatePath = Join-Path $temporaryRoot 'internal-missing-candidate.json'
    Copy-Item -LiteralPath $internalAdvancePath -Destination $missingCandidatePath
    $missingCandidateAudit = Get-Content -LiteralPath $missingCandidatePath -Raw | ConvertFrom-Json
    $missingCandidateAudit.packages[0].latestStable = '1.0.0'
    $missingCandidateAudit.packages[0].sourceResults[0].latestStable = '1.0.0'
    Save-Audit -Audit $missingCandidateAudit -Path $missingCandidatePath
    Write-Utf8File -Path $internalAdvanceCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Fixture.One" Version="1.1.0" /><PackageVersion Include="Hexalith.Fixture.Two" Version="1.1.0" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Internal actual selection missing source candidate' `
        -AuditPath $missingCandidatePath -ExpectedExitCode 1 `
        -ExpectedOutput "Accepted package 'Hexalith.Fixture.One' selected version '1.1.0' has no configured-source candidate evidence"

    $missingPublicationPath = Join-Path $temporaryRoot 'internal-missing-publication.json'
    Copy-Item -LiteralPath $internalAdvancePath -Destination $missingPublicationPath
    $missingPublicationAudit = Get-Content -LiteralPath $missingPublicationPath -Raw | ConvertFrom-Json
    $missingPublicationAudit.packages[0].listingState = 'missing'
    $missingPublicationAudit.packages[0].latestStable = $null
    $missingPublicationAudit.packages[0].sourceResults[0].listingState = 'missing'
    $missingPublicationAudit.packages[0].sourceResults[0].latestStable = $null
    Save-Audit -Audit $missingPublicationAudit -Path $missingPublicationPath
    Test-Scenario -Name 'Internal actual selection missing publication evidence' `
        -AuditPath $missingPublicationPath -ExpectedExitCode 1 `
        -ExpectedOutput "Accepted package 'Hexalith.Fixture.One' requires listed evidence from every configured source"

    $internalPrereleasePath = Join-Path $temporaryRoot 'internal-stable-to-prerelease.json'
    Copy-Item -LiteralPath $internalAdvancePath -Destination $internalPrereleasePath
    $internalPrereleaseAudit = Get-Content -LiteralPath $internalPrereleasePath -Raw | ConvertFrom-Json
    foreach ($package in $internalPrereleaseAudit.packages) {
        $package.selectedVersion = '1.1.0-preview.1'
        $package.latestStable = '1.0.0'
        $package.latestPrerelease = '1.1.0-preview.1'
        $package.sourceResults[0].latestStable = '1.0.0'
        $package.sourceResults[0].latestPrerelease = '1.1.0-preview.1'
    }
    Save-Audit -Audit $internalPrereleaseAudit -Path $internalPrereleasePath
    Write-Utf8File -Path $internalAdvanceCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Fixture.One" Version="1.1.0-preview.1" /><PackageVersion Include="Hexalith.Fixture.Two" Version="1.1.0-preview.1" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Internal stable package prerelease move' `
        -AuditPath $internalPrereleasePath -ExpectedExitCode 1 `
        -ExpectedOutput "Accepted stable package 'Hexalith.Fixture.One' cannot move to prerelease version '1.1.0-preview.1'"

    $metadataPath = Join-Path $temporaryRoot 'internal-stable-build-metadata.json'
    Copy-Item -LiteralPath $internalAdvancePath -Destination $metadataPath
    $metadataAudit = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    foreach ($package in $metadataAudit.packages) {
        $package.auditedVersion = '1.1.0+sha-feature-1'
        $package.selectedVersion = '1.1.0+sha-feature-1'
        $package.latestStable = '1.1.0+sha-feature-1'
        $package.disposition = 'retained'
        $package.sourceResults[0].latestStable = '1.1.0+sha-feature-1'
    }
    $metadataAudit.familyDecisions[0].disposition = 'retained'
    Write-Utf8File -Path $internalAdvanceCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Fixture.One" Version="1.1.0+sha-feature-1" /><PackageVersion Include="Hexalith.Fixture.Two" Version="1.1.0+sha-feature-1" /></ItemGroup></Project>
'@
    Update-AuditProvenance -Audit $metadataAudit -AuditCatalogPath $internalAdvanceCatalogPath `
        -PreservationStatus refreshed
    Save-Audit -Audit $metadataAudit -Path $metadataPath
    Test-Scenario -Name 'Internal stable build metadata containing hyphen' `
        -AuditPath $metadataPath -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 2 packages, 1 families, and 1 source'

    Write-Utf8File -Path $internalAdvanceCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Fixture.One" Version="999999999999999999.0.0" /><PackageVersion Include="Hexalith.Fixture.Two" Version="999999999999999999.0.0" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Internal overflowing catalog version' `
        -AuditPath $internalAdvancePath -ExpectedExitCode 1 `
        -ExpectedOutput "Internal package 'Hexalith.Fixture.One' has invalid catalog version NuGet version '999999999999999999.0.0'"

    $malformedVersionPath = Join-Path $temporaryRoot 'internal-malformed-audit-version.json'
    Copy-Item -LiteralPath $internalAdvancePath -Destination $malformedVersionPath
    $malformedVersionAudit = Get-Content -LiteralPath $malformedVersionPath -Raw | ConvertFrom-Json
    $malformedVersionAudit.packages[0].auditedVersion = '1..0'
    Save-Audit -Audit $malformedVersionAudit -Path $malformedVersionPath
    Write-Utf8File -Path $internalAdvanceCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Fixture.One" Version="1.1.0" /><PackageVersion Include="Hexalith.Fixture.Two" Version="1.1.0" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Internal malformed audit version' `
        -AuditPath $malformedVersionPath -ExpectedExitCode 1 `
        -ExpectedOutput "Internal package 'Hexalith.Fixture.One' has invalid auditedVersion NuGet version '1..0'"

    $tenantsDriftPath = New-AuditFixture -Name 'tenants-actual-drift' -PackagePrefix 'Hexalith.Tenants'
    $tenantsCatalogPath = Join-Path $temporaryRoot 'tenants-actual-drift.props'
    Write-Utf8File -Path $tenantsCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Tenants.One" Version="1.1.0" /><PackageVersion Include="Hexalith.Tenants.Two" Version="1.1.0" /></ItemGroup></Project>
'@
    $tenantsAudit = Get-Content -LiteralPath $tenantsDriftPath -Raw | ConvertFrom-Json
    foreach ($package in $tenantsAudit.packages) {
        $package.auditedVersion = '1.0.0'
        $package.selectedVersion = '1.0.0'
        $package.latestStable = '1.1.0'
        $package.listingState = 'listed'
        $package.sourceResults[0].latestStable = '1.1.0'
        $package.sourceResults[0].listingState = 'listed'
    }
    $tenantsAudit.catalogPath = [IO.Path]::GetRelativePath($repositoryRoot, $tenantsCatalogPath).Replace('\', '/')
    Save-Audit -Audit $tenantsAudit -Path $tenantsDriftPath
    $catalogPath = $tenantsCatalogPath
    Test-Scenario -Name 'Tenants actual selection drift' -AuditPath $tenantsDriftPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Hexalith.Tenants.One' changed without a separately validated Tenants release-owner contract"

    $catalogPath = $internalAdvanceCatalogPath

    Write-Utf8File -Path $internalAdvanceCatalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Hexalith.Fixture.One" Version="1.1.0" /><PackageVersion Include="Hexalith.Fixture.Two" Version="1.2.0" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Split internal family advance' -AuditPath $internalAdvancePath -ExpectedExitCode 1 `
        -ExpectedOutput "Internal package family 'fixture-family' must select one aligned catalog version"

    $catalogPath = Join-Path $temporaryRoot 'fixture.props'
    $catalogHashPath = New-AuditFixture -Name 'catalog-hash-mismatch'
    $catalogHashAudit = Get-Content -LiteralPath $catalogHashPath -Raw | ConvertFrom-Json
    $catalogHashAudit.catalogSha256 = ('0' * 64)
    Save-Audit -Audit $catalogHashAudit -Path $catalogHashPath
    Test-Scenario -Name 'Catalog declaration hash mismatch' -AuditPath $catalogHashPath -ExpectedExitCode 1 `
        -ExpectedOutput 'catalogSha256 does not match the evaluated catalog declaration bytes'

    $sourceHashPath = New-AuditFixture -Name 'source-hash-mismatch'
    $sourceHashAudit = Get-Content -LiteralPath $sourceHashPath -Raw | ConvertFrom-Json
    $sourceHashAudit.familyDecisions[0].origin.sourceScopeSha256 = ('0' * 64)
    Save-Audit -Audit $sourceHashAudit -Path $sourceHashPath
    Test-Scenario -Name 'Source provenance hash mismatch' -AuditPath $sourceHashPath -ExpectedExitCode 1 `
        -ExpectedOutput 'origin sourceScopeSha256 does not match the configured source records'

    $metadataHashPath = New-AuditFixture -Name 'metadata-hash-mismatch'
    $metadataHashAudit = Get-Content -LiteralPath $metadataHashPath -Raw | ConvertFrom-Json
    $metadataHashAudit.familyDecisions[0].origin.packageMetadataSha256 = ('0' * 64)
    Save-Audit -Audit $metadataHashAudit -Path $metadataHashPath
    Test-Scenario -Name 'Package/source relation hash mismatch' -AuditPath $metadataHashPath -ExpectedExitCode 1 `
        -ExpectedOutput 'origin packageMetadataSha256 does not match its package/source relations'

    $consumerHashPath = New-AuditFixture -Name 'consumer-hash-mismatch'
    $consumerHashAudit = Get-Content -LiteralPath $consumerHashPath -Raw | ConvertFrom-Json
    $consumerHashAudit.consumerEvidence.sha256 = ('0' * 64)
    Save-Audit -Audit $consumerHashAudit -Path $consumerHashPath
    Test-Scenario -Name 'Consumer relation hash mismatch' -AuditPath $consumerHashPath -ExpectedExitCode 1 `
        -ExpectedOutput 'Consumer evidence sha256 does not match its ordered direct-consumer relations and declaration bytes'

    $consumerDeclarationPath = New-AuditFixture -Name 'consumer-declaration-hash-mismatch'
    $consumerDeclarationAudit = Get-Content -LiteralPath $consumerDeclarationPath -Raw | ConvertFrom-Json
    $consumerDeclarationAudit.consumerEvidence.entries[0].declarationSha256 = 'invalid'
    Save-Audit -Audit $consumerDeclarationAudit -Path $consumerDeclarationPath
    Test-Scenario -Name 'Consumer declaration hash format' -AuditPath $consumerDeclarationPath -ExpectedExitCode 1 `
        -ExpectedOutput "Consumer evidence declaration 'validator-fixture.json' must have a lowercase SHA-256 value"

    $familyConsumerHashPath = New-AuditFixture -Name 'family-consumer-hash-mismatch'
    $familyConsumerHashAudit = Get-Content -LiteralPath $familyConsumerHashPath -Raw | ConvertFrom-Json
    $familyConsumerHashAudit.familyDecisions[0].origin.consumerEvidenceSha256 = ('0' * 64)
    Save-Audit -Audit $familyConsumerHashAudit -Path $familyConsumerHashPath
    Test-Scenario -Name 'Family consumer relation hash mismatch' -AuditPath $familyConsumerHashPath -ExpectedExitCode 1 `
        -ExpectedOutput 'origin consumerEvidenceSha256 does not match its consumer-package relations and declaration bytes'

    $fixtureBindingPath = New-AuditFixture -Name 'fixture-binding-missing'
    $fixtureBindingAudit = Get-Content -LiteralPath $fixtureBindingPath -Raw | ConvertFrom-Json
    $fixtureBindingAudit.consumerEvidence.fixtureSha256 = $null
    Save-Audit -Audit $fixtureBindingAudit -Path $fixtureBindingPath
    Test-Scenario -Name 'Explicit fixture binding missing' -AuditPath $fixtureBindingPath -ExpectedExitCode 1 `
        -ExpectedOutput 'Explicit consumer evidence must bind its fixture identity and lowercase SHA-256'

    $fixtureModePath = New-AuditFixture -Name 'fixture-mode-missing'
    $fixtureModeAudit = Get-Content -LiteralPath $fixtureModePath -Raw | ConvertFrom-Json
    $fixtureModeAudit.consumerEvidence.fixtureMode = $null
    Save-Audit -Audit $fixtureModeAudit -Path $fixtureModePath
    Test-Scenario -Name 'Explicit fixture mode missing' -AuditPath $fixtureModePath -ExpectedExitCode 1 `
        -ExpectedOutput "fixtureMode 'synthetic-explicit'"

    $fixtureTraversalPath = New-AuditFixture -Name 'fixture-path-traversal'
    $fixtureTraversalAudit = Get-Content -LiteralPath $fixtureTraversalPath -Raw | ConvertFrom-Json -DateKind String
    $fixtureTraversalAudit.consumerEvidence.fixture = '../escaped-validator-fixture.json'
    Save-Audit -Audit $fixtureTraversalAudit -Path $fixtureTraversalPath
    Test-Scenario -Name 'Explicit fixture path cannot escape audit directory' `
        -AuditPath $fixtureTraversalPath -ExpectedExitCode 1 `
        -ExpectedOutput "fixture '../escaped-validator-fixture.json' escapes the audit directory"

    $fixtureTamperPath = New-AuditFixture -Name 'fixture-byte-tamper'
    Write-Utf8File -Path (Join-Path $temporaryRoot 'validator-fixture.json') -Content '{"entries":[]}'
    Test-Scenario -Name 'Explicit fixture byte tamper' -AuditPath $fixtureTamperPath -ExpectedExitCode 1 `
        -ExpectedOutput 'fixtureSha256 does not match the fixture bytes'

    $fixtureSemanticPath = New-AuditFixture -Name 'fixture-semantic-mismatch'
    $fixtureSemanticAudit = Get-Content -LiteralPath $fixtureSemanticPath -Raw | ConvertFrom-Json
    $fixtureSemanticAudit.consumerEvidence.entries[0].consumer = 'Fixture.Other'
    Save-Audit -Audit $fixtureSemanticAudit -Path $fixtureSemanticPath
    Test-Scenario -Name 'Explicit fixture semantic mismatch' -AuditPath $fixtureSemanticPath -ExpectedExitCode 1 `
        -ExpectedOutput "relation 'Fixture.Other|Fixture.One' is not exactly bound"

    $familyHistoryPath = New-AuditFixture -Name 'incomplete-family-history'
    $familyHistoryAudit = Get-Content -LiteralPath $familyHistoryPath -Raw | ConvertFrom-Json
    $familyHistoryAudit.familyDecisions[0].historicalContext = @([pscustomobject] @{
            schema = 'hexalith.package-audit-family-history.v1'
            label = 'Incomplete history.'
        })
    Save-Audit -Audit $familyHistoryAudit -Path $familyHistoryPath
    Test-Scenario -Name 'Incomplete family history' -AuditPath $familyHistoryPath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'fixture-family' historical context has a blank or missing 'auditedAtUtc'"

    $untypedFamilyHistoryPath = New-AuditFixture -Name 'untyped-family-history'
    $untypedFamilyHistoryAudit = Get-Content -LiteralPath $untypedFamilyHistoryPath -Raw | ConvertFrom-Json
    $untypedFamilyHistoryAudit.familyDecisions[0].historicalContext = @([pscustomobject] @{
            label = 'Untyped history.'
        })
    Save-Audit -Audit $untypedFamilyHistoryAudit -Path $untypedFamilyHistoryPath
    Test-Scenario -Name 'Untyped family history' -AuditPath $untypedFamilyHistoryPath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'fixture-family' has historical context without a typed schema"

    $packageHistoryPath = New-AuditFixture -Name 'incomplete-package-history'
    $packageHistoryAudit = Get-Content -LiteralPath $packageHistoryPath -Raw | ConvertFrom-Json
    $packageHistoryAudit.packages[0] | Add-Member -NotePropertyName historicalContext -NotePropertyValue @([pscustomobject] @{
            schema = 'hexalith.package-audit-package-history.v1'
            label = 'Incomplete history.'
        }) -Force
    Save-Audit -Audit $packageHistoryAudit -Path $packageHistoryPath
    Test-Scenario -Name 'Incomplete package history' -AuditPath $packageHistoryPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' historical context has a blank or missing 'auditedAtUtc'"

    $untypedPackageHistoryPath = New-AuditFixture -Name 'untyped-package-history'
    $untypedPackageHistoryAudit = Get-Content -LiteralPath $untypedPackageHistoryPath -Raw | ConvertFrom-Json
    $untypedPackageHistoryAudit.packages[0] | Add-Member -NotePropertyName historicalContext `
        -NotePropertyValue @([pscustomobject] @{ label = 'Untyped history.' }) -Force
    Save-Audit -Audit $untypedPackageHistoryAudit -Path $untypedPackageHistoryPath
    Test-Scenario -Name 'Untyped package history' -AuditPath $untypedPackageHistoryPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' has historical context without a typed schema"

    $acceptedPreservationPath = New-AuditFixture -Name 'accepted-without-origin'
    $acceptedPreservationAudit = Get-Content -LiteralPath $acceptedPreservationPath -Raw | ConvertFrom-Json
    foreach ($package in $acceptedPreservationAudit.packages) {
        $package.disposition = 'accepted'
        if ($package.id -eq 'Fixture.Two') {
            $package.listingState = 'listed'
            $package.latestStable = '2.0.0'
            $package.sourceResults[0].listingState = 'listed'
            $package.sourceResults[0].latestStable = '2.0.0'
        }
    }
    $acceptedPreservationAudit.familyDecisions[0].disposition = 'accepted'
    $acceptedPreservationAudit.familyDecisions[0].PSObject.Properties.Remove('origin')
    Save-Audit -Audit $acceptedPreservationAudit -Path $acceptedPreservationPath
    Test-Scenario -Name 'Accepted decision origin is missing' -AuditPath $acceptedPreservationPath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'fixture-family' is missing observation origin provenance"

    $missingPath = New-AuditFixture -Name 'missing-package'
    $missingAudit = Get-Content -LiteralPath $missingPath -Raw | ConvertFrom-Json -DateKind String
    $missingAudit.packages = @($missingAudit.packages | Where-Object { $_.id -ne 'Fixture.Two' })
    $missingAudit.familyDecisions[0].packageIds = @('Fixture.One')
    Save-Audit -Audit $missingAudit -Path $missingPath
    Test-Scenario -Name 'Missing package evidence' -AuditPath $missingPath -ExpectedExitCode 1 `
        -ExpectedOutput "Evaluated catalog package 'Fixture.Two' has no audit evidence"

    $unsafePath = New-AuditFixture -Name 'unsafe-unlisted-update'
    $unsafeAudit = Get-Content -LiteralPath $unsafePath -Raw | ConvertFrom-Json -DateKind String
    $unsafeAudit.packages[0].disposition = 'accepted'
    $unsafeAudit.packages[1].disposition = 'accepted'
    $unsafeAudit.packages[1].selectedVersion = '2.1.0'
    $unsafeAudit.familyDecisions[0].disposition = 'accepted'
    Save-Audit -Audit $unsafeAudit -Path $unsafePath
    Test-Scenario -Name 'Unlisted package update' -AuditPath $unsafePath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.Two' is unlisted and must retain audited version '2.0.0'"

    $splitPath = New-AuditFixture -Name 'split-family'
    $splitAudit = Get-Content -LiteralPath $splitPath -Raw | ConvertFrom-Json -DateKind String
    $splitAudit.packages[0].disposition = 'accepted'
    Save-Audit -Audit $splitAudit -Path $splitPath
    Test-Scenario -Name 'Split family disposition' -AuditPath $splitPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' disposition does not match family 'fixture-family'"

    $exceptionPath = New-AuditFixture -Name 'incomplete-exception'
    $exceptionAudit = Get-Content -LiteralPath $exceptionPath -Raw | ConvertFrom-Json -DateKind String
    $exceptionAudit.packages[1].removalTrigger = ''
    Save-Audit -Audit $exceptionAudit -Path $exceptionPath
    Test-Scenario -Name 'Incomplete retained exception' -AuditPath $exceptionPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.Two' has a blank or missing 'removalTrigger'"

    $retainedUpdatePath = New-AuditFixture -Name 'retained-update'
    $retainedUpdateAudit = Get-Content -LiteralPath $retainedUpdatePath -Raw | ConvertFrom-Json -DateKind String
    $retainedUpdateAudit.packages[0].auditedVersion = '0.9.0'
    Save-Audit -Audit $retainedUpdateAudit -Path $retainedUpdatePath
    Test-Scenario -Name 'Retained package update' -AuditPath $retainedUpdatePath -ExpectedExitCode 1 `
        -ExpectedOutput "Retained package 'Fixture.One' must select audited version '0.9.0'"

    $downgradePath = New-AuditFixture -Name 'accepted-downgrade'
    $downgradeAudit = Get-Content -LiteralPath $downgradePath -Raw | ConvertFrom-Json -DateKind String
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
    $prereleaseAudit = Get-Content -LiteralPath $prereleasePath -Raw | ConvertFrom-Json -DateKind String
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

    $missingSourceResultPath = New-AuditFixture -Name 'missing-source-result'
    $missingSourceResultAudit = Get-Content -LiteralPath $missingSourceResultPath -Raw | ConvertFrom-Json -DateKind String
    $missingSourceResultAudit.sources += [pscustomobject] @{
        uri = 'https://packages.example.test/v3/index.json'
        resolution = 'resolved'
        diagnostic = 'Fixture source resolved.'
    }
    Save-Audit -Audit $missingSourceResultAudit -Path $missingSourceResultPath
    Test-Scenario -Name 'Missing configured source result' -AuditPath $missingSourceResultPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' must contain exactly one result for every configured source"

    $duplicateSourceResultPath = New-AuditFixture -Name 'duplicate-source-result'
    $duplicateSourceResultAudit = Get-Content -LiteralPath $duplicateSourceResultPath -Raw | ConvertFrom-Json -DateKind String
    $duplicateSourceResultAudit.packages[0].sourceResults += $duplicateSourceResultAudit.packages[0].sourceResults[0]
    Save-Audit -Audit $duplicateSourceResultAudit -Path $duplicateSourceResultPath
    Test-Scenario -Name 'Duplicate configured source result' -AuditPath $duplicateSourceResultPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' duplicates source result 'https://api.nuget.org/v3/index.json'"

    $undeclaredSourceResultPath = New-AuditFixture -Name 'undeclared-source-result'
    $undeclaredSourceResultAudit = Get-Content -LiteralPath $undeclaredSourceResultPath -Raw | ConvertFrom-Json -DateKind String
    $undeclaredSourceResultAudit.packages[0].sourceResults[0].source = 'https://packages.example.test/v3/index.json'
    Save-Audit -Audit $undeclaredSourceResultAudit -Path $undeclaredSourceResultPath
    Test-Scenario -Name 'Undeclared source result' -AuditPath $undeclaredSourceResultPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' references undeclared source 'https://packages.example.test/v3/index.json'"

    $aggregateMismatchPath = New-AuditFixture -Name 'aggregate-mismatch'
    $aggregateMismatchAudit = Get-Content -LiteralPath $aggregateMismatchPath -Raw | ConvertFrom-Json -DateKind String
    $aggregateMismatchAudit.packages[0].latestStable = '9.9.9'
    Save-Audit -Audit $aggregateMismatchAudit -Path $aggregateMismatchPath
    Test-Scenario -Name 'Aggregate candidate mismatch' -AuditPath $aggregateMismatchPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' latestStable '9.9.9' does not match source aggregate '1.0.0'"

    $sourceResolutionPath = New-AuditFixture -Name 'source-resolution-mismatch'
    $sourceResolutionAudit = Get-Content -LiteralPath $sourceResolutionPath -Raw | ConvertFrom-Json -DateKind String
    $sourceResolutionAudit.sources[0].resolution = 'unresolved'
    Save-Audit -Audit $sourceResolutionAudit -Path $sourceResolutionPath
    Test-Scenario -Name 'Source resolution mismatch' -AuditPath $sourceResolutionPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' source 'https://api.nuget.org/v3/index.json' must be unresolved"

    $partialSourcePath = New-AuditFixture -Name 'accepted-partial-source'
    $partialSourceAudit = Get-Content -LiteralPath $partialSourcePath -Raw | ConvertFrom-Json -DateKind String
    $partialSourceAudit.sources += [pscustomobject] @{
        uri = 'https://packages.example.test/v3/index.json'
        resolution = 'unresolved'
        diagnostic = 'Fixture source unavailable.'
    }
    foreach ($package in $partialSourceAudit.packages) {
        $package.sourceResults += [pscustomobject] @{
            source = 'https://packages.example.test/v3/index.json'
            listingState = 'unresolved'
            latestStable = $null
            latestPrerelease = $null
            diagnostic = 'Fixture source unavailable.'
        }
        $package.disposition = 'accepted'
    }
    $partialSourceAudit.familyDecisions[0].disposition = 'accepted'
    Save-Audit -Audit $partialSourceAudit -Path $partialSourcePath
    Test-Scenario -Name 'Accepted partial source visibility' -AuditPath $partialSourcePath -ExpectedExitCode 1 `
        -ExpectedOutput "Accepted package 'Fixture.One' requires listed evidence from every configured source"

    $orphanFamilyPath = New-AuditFixture -Name 'orphan-family'
    $orphanFamilyAudit = Get-Content -LiteralPath $orphanFamilyPath -Raw | ConvertFrom-Json -DateKind String
    $orphanFamilyAudit.familyDecisions += [pscustomobject] @{
        family = 'orphan-family'
        disposition = 'retained'
        rollbackGroup = 'orphan-family'
        packageIds = @()
        rationale = 'Fixture orphan.'
        compatibilityEvidence = 'Fixture evidence.'
        removalTrigger = 'Delete the orphan.'
        representativeConsumers = @('Fixture.Consumer')
    }
    Save-Audit -Audit $orphanFamilyAudit -Path $orphanFamilyPath
    Test-Scenario -Name 'Orphan family decision' -AuditPath $orphanFamilyPath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'orphan-family' has no package evidence rows"

    $nonUtcPath = New-AuditFixture -Name 'non-utc-timestamp'
    $nonUtcAudit = Get-Content -LiteralPath $nonUtcPath -Raw | ConvertFrom-Json -DateKind String
    $nonUtcAudit.snapshot.auditedAtUtc = '2026-07-31T14:00:00+02:00'
    Save-Audit -Audit $nonUtcAudit -Path $nonUtcPath
    Test-Scenario -Name 'Non-UTC timestamp' -AuditPath $nonUtcPath -ExpectedExitCode 1 `
        -ExpectedOutput 'auditedAtUtc must have a zero UTC offset'

    # --- PackageReference rediscovery vs. audit set equality (task 6) ---

    $coveredConsumerRoot = Join-Path $temporaryRoot 'consumer-covered'
    New-Item -ItemType Directory -Path $coveredConsumerRoot -Force | Out-Null
    Write-Utf8File -Path (Join-Path $coveredConsumerRoot 'Fixture.Consumer.csproj') -Content @'
<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Fixture.One" /></ItemGroup></Project>
'@
    $coveredPath = New-AuditFixture -Name 'reference-covered'
    Test-Scenario -Name 'Rediscovered PackageReference with audit evidence' -AuditPath $coveredPath -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 2 packages, 1 families, and 1 source' -ConsumerScanRoot $coveredConsumerRoot

    $missingRelationPath = Join-Path $temporaryRoot 'repository-consumer-relation-missing.json'
    $productionAuditPath = Join-Path $PSScriptRoot 'package-version-audit.json'
    $predatingCoveragePath = Join-Path $temporaryRoot 'repository-predating-coverage.json'
    $predatingCoverageAudit = Get-Content -LiteralPath $productionAuditPath -Raw | ConvertFrom-Json -DateKind String
    $predatingCoverageAudit.generatedFromRevision = '18742168b0bcdc40e5223f7573b1dcca441d781f'
    $predatingCoverageAudit.consumerEvidence.repositoryRevision = '18742168b0bcdc40e5223f7573b1dcca441d781f'
    Save-Audit -Audit $predatingCoverageAudit -Path $predatingCoveragePath
    Test-RepositoryScenario -Name 'Generated revision predates the audited coverage declaration' `
        -AuditPath $predatingCoveragePath -ExpectedExitCode 1 `
        -ExpectedOutput "Generated-from revision '18742168b0bcdc40e5223f7573b1dcca441d781f' does not contain the audited catalog declaration bytes"

    $historyRepository = Join-Path $temporaryRoot 'repository-history'
    $historyCatalogPath = Join-Path $historyRepository 'Props/Directory.Packages.props'
    New-Item -ItemType Directory -Path (Split-Path -Parent $historyCatalogPath) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Props/Directory.Packages.props') -Destination $historyCatalogPath
    $declarationPaths = @($predatingCoverageAudit.consumerEvidence.entries |
            ForEach-Object { [string] $_.declarationPath } |
            Sort-Object -Unique)
    foreach ($declarationPath in $declarationPaths) {
        $destination = Join-Path $historyRepository $declarationPath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $declarationPath) -Destination $destination
    }

    $changedDeclarationPath = $declarationPaths[0]
    $changedDeclaration = Join-Path $historyRepository $changedDeclarationPath
    [IO.File]::AppendAllText($changedDeclaration, "`n<!-- historical consumer bytes -->`n", [Text.UTF8Encoding]::new($false))
    $null = & git -C $historyRepository init --quiet
    $null = & git -C $historyRepository config user.name 'Package Audit Validator Tests'
    $null = & git -C $historyRepository config user.email 'package-audit-validator@example.invalid'
    $null = & git -C $historyRepository add -- .
    $null = & git -C $historyRepository commit --quiet -m 'test: historical consumer bytes'
    $historicalConsumerRevision = [string] @(& git -C $historyRepository rev-parse HEAD)[-1]

    Copy-Item -LiteralPath (Join-Path $repositoryRoot $changedDeclarationPath) -Destination $changedDeclaration -Force
    $null = & git -C $historyRepository add -- $changedDeclarationPath
    $null = & git -C $historyRepository commit --quiet -m 'test: current consumer bytes'

    $historicalConsumerAudit = Get-Content -LiteralPath $productionAuditPath -Raw | ConvertFrom-Json -DateKind String
    $historicalConsumerAudit.generatedFromRevision = $historicalConsumerRevision.Trim()
    $historicalConsumerAudit.consumerEvidence.repositoryRevision = $historicalConsumerRevision.Trim()
    $historicalConsumerPath = Join-Path $temporaryRoot 'repository-historical-consumer.json'
    Save-Audit -Audit $historicalConsumerAudit -Path $historicalConsumerPath
    Test-RepositoryScenario -Name 'Generated revision predates audited consumer declaration bytes' `
        -AuditPath $historicalConsumerPath -RepositoryRootPath $historyRepository -CatalogPath $historyCatalogPath `
        -ExpectedExitCode 1 `
        -ExpectedOutput "does not contain consumer declaration '$changedDeclarationPath' bytes"

    $currentTree = [string] @(& git -C $historyRepository rev-parse 'HEAD^{tree}')[-1]
    $unrelatedRevision = [string] @(& git -C $historyRepository commit-tree $currentTree.Trim() -m 'test: unrelated exact tree')[-1]
    $unrelatedAudit = Get-Content -LiteralPath $productionAuditPath -Raw | ConvertFrom-Json -DateKind String
    $unrelatedAudit.generatedFromRevision = $unrelatedRevision.Trim()
    $unrelatedAudit.consumerEvidence.repositoryRevision = $unrelatedRevision.Trim()
    $unrelatedPath = Join-Path $temporaryRoot 'repository-unrelated-revision.json'
    Save-Audit -Audit $unrelatedAudit -Path $unrelatedPath
    Test-RepositoryScenario -Name 'Generated revision is not from audited worktree ancestry' `
        -AuditPath $unrelatedPath -RepositoryRootPath $historyRepository -CatalogPath $historyCatalogPath `
        -ExpectedExitCode 1 `
        -ExpectedOutput "is not an ancestor of the audited worktree HEAD"

    $null = & git -C $historyRepository replace $historicalConsumerRevision.Trim() $unrelatedRevision.Trim()
    Test-RepositoryScenario -Name 'Historical Git reads ignore replacement refs' `
        -AuditPath $historicalConsumerPath -RepositoryRootPath $historyRepository -CatalogPath $historyCatalogPath `
        -ExpectedExitCode 1 `
        -ExpectedOutput "does not contain consumer declaration '$changedDeclarationPath' bytes"

    [IO.File]::AppendAllText(
        $historyCatalogPath,
        "`n<!--$('x' * 131072)-->`n",
        [Text.UTF8Encoding]::new($false)
    )
    $null = & git -C $historyRepository add -- 'Props/Directory.Packages.props'
    $null = & git -C $historyRepository commit --quiet -m 'test: oversized historical catalog'
    $oversizedRevision = [string] @(& git -C $historyRepository rev-parse HEAD)[-1]
    $oversizedAudit = Get-Content -LiteralPath $productionAuditPath -Raw | ConvertFrom-Json -DateKind String
    $oversizedAudit.generatedFromRevision = $oversizedRevision.Trim()
    $oversizedAudit.consumerEvidence.repositoryRevision = $oversizedRevision.Trim()
    $oversizedCatalogSha256 = Get-CatalogSha256 -Path $historyCatalogPath
    $oversizedAudit.catalogSha256 = $oversizedCatalogSha256
    $oversizedAudit.catalogRawSha256 = (Get-FileHash -LiteralPath $historyCatalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
    foreach ($decision in $oversizedAudit.familyDecisions) {
        $decision.origin.familySelectionSha256 = Get-FamilySelectionFingerprint -Packages @(
            $oversizedAudit.packages | Where-Object { [string] $_.family -ceq [string] $decision.family }
        )
    }
    $oversizedPath = Join-Path $temporaryRoot 'repository-oversized-historical-catalog.json'
    Save-Audit -Audit $oversizedAudit -Path $oversizedPath
    Test-RepositoryScenario -Name 'Oversized historical Git blob is rejected at the support-safe bound' `
        -AuditPath $oversizedPath -RepositoryRootPath $historyRepository -CatalogPath $historyCatalogPath `
        -GitBlobReadMaxBytes 65536 -ExpectedExitCode 1 `
        -ExpectedOutput "Historical Git blob read exceeded the 65536-byte limit for 'Props/Directory.Packages.props'."

    $malformedReadyPath = Join-Path $temporaryRoot 'malformed-git-shim.ready'
    Write-Utf8File -Path $malformedReadyPath -Content 'not-a-pid|still-not-a-pid'
    $scenarioCount++
    $malformedReadyRecord = Read-ReadyProcessRecord -Path $malformedReadyPath
    if ($null -eq $malformedReadyRecord -or $malformedReadyRecord.Valid -or
        $malformedReadyRecord.Diagnostic -notlike '*two positive integer process identifiers*') {
        $failures.Add('Malformed/non-numeric shim readiness records must be handled as bounded harness failures.')
    }

    foreach ($attempt in 1..3) {
        $gitShimPath = Join-Path $temporaryRoot "non-terminating-git-shim-$attempt.ps1"
        $gitShimReadyPath = Join-Path $temporaryRoot "non-terminating-git-$attempt.ready"
        New-NonTerminatingGitShim -ShimPath $gitShimPath -ReadyPath $gitShimReadyPath
        Test-ReadyRepositoryScenario `
            -Name "Non-terminating historical Git read attempt $attempt times out and terminates its process tree" `
            -AuditPath $oversizedPath -RepositoryRootPath $historyRepository -CatalogPath $historyCatalogPath `
            -GitBlobReaderShimPath $gitShimPath -ReadyPath $gitShimReadyPath `
            -GitBlobReadTimeoutSeconds 3 `
            -ExpectedOutput "Historical Git blob read timed out after 3 seconds for 'Props/Directory.Packages.props'."
    }

    $missingRelationAudit = Get-Content -LiteralPath $productionAuditPath -Raw | ConvertFrom-Json -DateKind String
    $removedRelation = @($missingRelationAudit.consumerEvidence.entries | Where-Object {
            [string] $_.packageId -ceq 'Microsoft.NET.Test.Sdk'
        })[0]
    $missingRelationAudit.consumerEvidence.entries = @($missingRelationAudit.consumerEvidence.entries | Where-Object {
            [string] $_.consumer -cne [string] $removedRelation.consumer -or
            [string] $_.packageId -cne [string] $removedRelation.packageId
        })
    $missingRelationAudit.consumerEvidence.sha256 = Get-ConsumerRelationFingerprint `
        -Entries @($missingRelationAudit.consumerEvidence.entries)
    $removedFamily = [string] $removedRelation.family
    $removedFamilyDecision = @($missingRelationAudit.familyDecisions | Where-Object {
            [string] $_.family -ceq $removedFamily
        })[0]
    $remainingFamilyEntries = @($missingRelationAudit.consumerEvidence.entries | Where-Object {
            [string] $_.family -ceq $removedFamily
        })
    $removedFamilyDecision.representativeConsumers = @(
        $remainingFamilyEntries | ForEach-Object { [string] $_.consumer } | Sort-Object -CaseSensitive -Unique
    )
    $removedFamilyDecision.origin.consumerEvidenceSha256 = Get-ConsumerRelationFingerprint `
        -Entries $remainingFamilyEntries
    Save-Audit -Audit $missingRelationAudit -Path $missingRelationPath
    $missingRelation = "$($removedRelation.consumer)|$($removedRelation.packageId)"
    Test-RepositoryScenario -Name 'Rediscovered consumer-package relation missing from evidence' `
        -AuditPath $missingRelationPath -ExpectedExitCode 1 `
        -ExpectedOutput "Rediscovered PackageReference relation '$missingRelation' has no exact consumer-evidence match"

    $uncoveredConsumerRoot = Join-Path $temporaryRoot 'consumer-uncovered'
    New-Item -ItemType Directory -Path $uncoveredConsumerRoot -Force | Out-Null
    Write-Utf8File -Path (Join-Path $uncoveredConsumerRoot 'Fixture.Consumer.csproj') -Content @'
<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Fixture.Missing" /></ItemGroup></Project>
'@
    $uncoveredPath = New-AuditFixture -Name 'reference-uncovered'
    Test-Scenario -Name 'Rediscovered PackageReference without audit evidence' -AuditPath $uncoveredPath -ExpectedExitCode 1 `
        -ExpectedOutput "PackageReference 'Fixture.Missing' rediscovered from Fixture.Consumer.csproj has no audit evidence" `
        -ConsumerScanRoot $uncoveredConsumerRoot

    $globalReferenceConsumerRoot = Join-Path $temporaryRoot 'consumer-global'
    New-Item -ItemType Directory -Path $globalReferenceConsumerRoot -Force | Out-Null
    Write-Utf8File -Path (Join-Path $globalReferenceConsumerRoot 'Directory.Build.props') -Content @'
<Project><ItemGroup><GlobalPackageReference Include="Fixture.GloballyMissing" /></ItemGroup></Project>
'@
    $globalReferencePath = New-AuditFixture -Name 'global-reference-uncovered'
    Test-Scenario -Name 'Rediscovered GlobalPackageReference without audit evidence' -AuditPath $globalReferencePath -ExpectedExitCode 1 `
        -ExpectedOutput "PackageReference 'Fixture.GloballyMissing' rediscovered from Directory.Build.props has no audit evidence" `
        -ConsumerScanRoot $globalReferenceConsumerRoot

    # --- Typed round-trip fidelity for collection-valued provenance (task 7) ---

    $multiCardinalityPath = New-AuditFixture -Name 'multi-cardinality-round-trip'
    $multiCardinalityAudit = Get-Content -LiteralPath $multiCardinalityPath -Raw | ConvertFrom-Json -DateKind String
    $multiCardinalityAudit.sources += [pscustomobject] @{
        uri = 'https://packages.example.test/v3/index.json'
        resolution = 'resolved'
        diagnostic = 'Fixture second source resolved.'
    }
    $multiCardinalityAudit.packages[0].sourceResults += [pscustomobject] @{
        source = 'https://packages.example.test/v3/index.json'
        listingState = 'listed'
        latestStable = '1.0.0'
        latestPrerelease = $null
        diagnostic = 'Registration metadata resolved.'
    }
    $multiCardinalityAudit.packages[1].sourceResults += [pscustomobject] @{
        source = 'https://packages.example.test/v3/index.json'
        listingState = 'unlisted'
        latestStable = '2.1.0'
        latestPrerelease = '3.0.0-preview.1'
        diagnostic = 'Registration metadata resolved.'
    }
    # Two distinct owning consumers make representativeConsumers genuinely
    # collection-valued. The explicit fixture, its consumer-evidence entries, and
    # every derived provenance hash must all move with it, so this scenario proves
    # a multi-element round trip against a fully coherent audit rather than one
    # that merely happens to fail a different check first.
    $multiCardinalityFixturePath = Join-Path $temporaryRoot 'validator-fixture.json'
    $multiCardinalityFixtureDocument = [ordered] @{
        entries = @(
            [ordered] @{ consumer = 'Fixture.Consumer.One'; packageId = 'Fixture.One' },
            [ordered] @{ consumer = 'Fixture.Consumer.Two'; packageId = 'Fixture.Two' }
        )
    }
    Write-Utf8File -Path $multiCardinalityFixturePath -Content ($multiCardinalityFixtureDocument | ConvertTo-Json -Depth 4)
    $multiCardinalityDeclarationSha256 = (Get-FileHash -LiteralPath $multiCardinalityFixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $multiCardinalityConsumerSha256 = Get-Sha256Text -Value (
        "fixture-family|Fixture.Consumer.One|Fixture.One|validator-fixture.json|$multiCardinalityDeclarationSha256`n" +
        "fixture-family|Fixture.Consumer.Two|Fixture.Two|validator-fixture.json|$multiCardinalityDeclarationSha256"
    )
    $multiCardinalityAudit.consumerEvidence.fixtureSha256 = $multiCardinalityDeclarationSha256
    $multiCardinalityAudit.consumerEvidence.sha256 = $multiCardinalityConsumerSha256
    $multiCardinalityAudit.consumerEvidence.entries = @(
        [pscustomobject] @{
            family = 'fixture-family'
            consumer = 'Fixture.Consumer.One'
            packageId = 'Fixture.One'
            declarationPath = 'validator-fixture.json'
            declarationSha256 = $multiCardinalityDeclarationSha256
        },
        [pscustomobject] @{
            family = 'fixture-family'
            consumer = 'Fixture.Consumer.Two'
            packageId = 'Fixture.Two'
            declarationPath = 'validator-fixture.json'
            declarationSha256 = $multiCardinalityDeclarationSha256
        }
    )
    $multiCardinalityAudit.familyDecisions[0].representativeConsumers = @('Fixture.Consumer.One', 'Fixture.Consumer.Two')
    $multiCardinalityAudit.familyDecisions[0].origin.sourceScopeSha256 = Get-Sha256Text -Value (
        "https://api.nuget.org/v3/index.json|resolved|Fixture source resolved.`n" +
        'https://packages.example.test/v3/index.json|resolved|Fixture second source resolved.'
    )
    $multiCardinalityAudit.familyDecisions[0].origin.packageMetadataSha256 = Get-Sha256Text -Value (
        'Fixture.One|1.0.0|1.0.0|1.0.0||listed|' +
        'https://api.nuget.org/v3/index.json=listed:1.0.0::Registration metadata resolved.,' +
        'https://packages.example.test/v3/index.json=listed:1.0.0::Registration metadata resolved.' + "`n" +
        'Fixture.Two|2.0.0|2.0.0|2.1.0|3.0.0-preview.1|unlisted|' +
        'https://api.nuget.org/v3/index.json=unlisted:2.1.0:3.0.0-preview.1:Registration metadata resolved.,' +
        'https://packages.example.test/v3/index.json=unlisted:2.1.0:3.0.0-preview.1:Registration metadata resolved.'
    )
    $multiCardinalityAudit.familyDecisions[0].origin.consumerEvidenceSha256 = $multiCardinalityConsumerSha256
    Save-Audit -Audit $multiCardinalityAudit -Path $multiCardinalityPath
    Test-Scenario -Name 'Multi-element collections round-trip' -AuditPath $multiCardinalityPath -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 2 packages, 1 families, and 2 source'

    $v2HistoryPath = New-AuditFixture -Name 'v2-history-round-trip'
    $v2HistoryAudit = Get-Content -LiteralPath $v2HistoryPath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $v2HistoryAudit
    Save-Audit -Audit $v2HistoryAudit -Path $v2HistoryPath
    Test-Scenario -Name 'Complete v2 package and family history round-trip' `
        -AuditPath $v2HistoryPath -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 2 packages, 1 families, and 1 source'

    $futureHistoryPath = New-AuditFixture -Name 'future-v2-history-origin'
    $futureHistoryAudit = Get-Content -LiteralPath $futureHistoryPath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $futureHistoryAudit
    $futureHistoryAudit.packages[0].historicalContext[0].auditedAtUtc = '2026-08-01T12:00:00.0000000+00:00'
    $futureHistoryAudit.packages[0].historicalContext[0].origin.auditedAtUtc = '2026-08-01T12:00:00.0000000+00:00'
    Save-Audit -Audit $futureHistoryAudit -Path $futureHistoryPath
    Test-Scenario -Name 'Historical origin cannot postdate snapshot' `
        -AuditPath $futureHistoryPath -ExpectedExitCode 1 `
        -ExpectedOutput 'historical context origin auditedAtUtc must not be later than the snapshot timestamp'

    $historyEnvelopeMismatchPath = New-AuditFixture -Name 'v2-history-envelope-mismatch'
    $historyEnvelopeMismatchAudit = Get-Content -LiteralPath $historyEnvelopeMismatchPath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $historyEnvelopeMismatchAudit
    $historyEnvelopeMismatchAudit.packages[0].historicalContext[0].origin.auditedAtUtc = '2026-07-29T12:00:00.0000000+00:00'
    Save-Audit -Audit $historyEnvelopeMismatchAudit -Path $historyEnvelopeMismatchPath
    Test-Scenario -Name 'Historical origin matches history envelope' `
        -AuditPath $historyEnvelopeMismatchPath -ExpectedExitCode 1 `
        -ExpectedOutput 'historical context envelope revision/timestamp must exactly match its origin'

    $historyHashPath = New-AuditFixture -Name 'v2-history-origin-hash'
    $historyHashAudit = Get-Content -LiteralPath $historyHashPath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $historyHashAudit
    $historyHashAudit.familyDecisions[0].historicalContext[0].origin.sourceScopeSha256 = 'invalid'
    Save-Audit -Audit $historyHashAudit -Path $historyHashPath
    Test-Scenario -Name 'Historical origin hashes have exact format' `
        -AuditPath $historyHashPath -ExpectedExitCode 1 `
        -ExpectedOutput 'historical context origin sourceScopeSha256 must be a lowercase SHA-256 value'

    $historyRevisionPath = New-AuditFixture -Name 'v2-history-unavailable-revision'
    $historyRevisionAudit = Get-Content -LiteralPath $historyRevisionPath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $historyRevisionAudit
    $historyRevisionAudit.packages[0].historicalContext[0].generatedFromRevision = ('0' * 40)
    $historyRevisionAudit.packages[0].historicalContext[0].origin.generatedFromRevision = ('0' * 40)
    Save-Audit -Audit $historyRevisionAudit -Path $historyRevisionPath
    Test-Scenario -Name 'Historical origin revision must be available' `
        -AuditPath $historyRevisionPath -ExpectedExitCode 1 `
        -ExpectedOutput "historical context origin revision '$('0' * 40)' is not an available Git commit"

    $historySourceUriPath = New-AuditFixture -Name 'v2-history-relative-source'
    $historySourceUriAudit = Get-Content -LiteralPath $historySourceUriPath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $historySourceUriAudit
    $historySourceUriAudit.packages[0].historicalContext[0].sourceResults[0].source = '../feed/index.json'
    Save-Audit -Audit $historySourceUriAudit -Path $historySourceUriPath
    Test-Scenario -Name 'Historical source URI must be absolute' `
        -AuditPath $historySourceUriPath -ExpectedExitCode 1 `
        -ExpectedOutput "historical context source '../feed/index.json' must be a valid absolute URI"

    $historySourceStatePath = New-AuditFixture -Name 'v2-history-source-state'
    $historySourceStateAudit = Get-Content -LiteralPath $historySourceStatePath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $historySourceStateAudit
    $historySourceStateAudit.packages[0].historicalContext[0].sourceResults[0].listingState = 'unknown'
    Save-Audit -Audit $historySourceStateAudit -Path $historySourceStatePath
    Test-Scenario -Name 'Historical source state has closed vocabulary' `
        -AuditPath $historySourceStatePath -ExpectedExitCode 1 `
        -ExpectedOutput "historical context source 'https://api.nuget.org/v3/index.json' has invalid listingState 'unknown'"

    $historySourceCoveragePath = New-AuditFixture -Name 'v2-history-source-coverage'
    $historySourceCoverageAudit = Get-Content -LiteralPath $historySourceCoveragePath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $historySourceCoverageAudit
    $historySourceCoverageAudit.packages[0].historicalContext[0].sourceResults = @()
    Save-Audit -Audit $historySourceCoverageAudit -Path $historySourceCoveragePath
    Test-Scenario -Name 'Historical source scope coverage is exact when embedded' `
        -AuditPath $historySourceCoveragePath -ExpectedExitCode 1 `
        -ExpectedOutput 'historical context does not exactly cover the configured sources bound by its origin'

    $duplicatePackageHistoryPath = New-AuditFixture -Name 'duplicate-v2-package-history'
    $duplicatePackageHistoryAudit = Get-Content -LiteralPath $duplicatePackageHistoryPath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $duplicatePackageHistoryAudit
    $duplicatePackageHistoryAudit.packages[0].historicalContext = @(
        $duplicatePackageHistoryAudit.packages[0].historicalContext[0],
        $duplicatePackageHistoryAudit.packages[0].historicalContext[0]
    )
    Save-Audit -Audit $duplicatePackageHistoryAudit -Path $duplicatePackageHistoryPath
    Test-Scenario -Name 'Exact duplicate package history is rejected' `
        -AuditPath $duplicatePackageHistoryPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' contains an exact duplicate historical context record"

    $duplicateFamilyHistoryPath = New-AuditFixture -Name 'duplicate-v2-family-history'
    $duplicateFamilyHistoryAudit = Get-Content -LiteralPath $duplicateFamilyHistoryPath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $duplicateFamilyHistoryAudit
    $duplicateFamilyHistoryAudit.familyDecisions[0].historicalContext = @(
        $duplicateFamilyHistoryAudit.familyDecisions[0].historicalContext[0],
        $duplicateFamilyHistoryAudit.familyDecisions[0].historicalContext[0]
    )
    Save-Audit -Audit $duplicateFamilyHistoryAudit -Path $duplicateFamilyHistoryPath
    Test-Scenario -Name 'Exact duplicate family history is rejected' `
        -AuditPath $duplicateFamilyHistoryPath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'fixture-family' contains an exact duplicate historical context record"

    $duplicateHistoryOriginPath = New-AuditFixture -Name 'duplicate-v2-history-origin'
    $duplicateHistoryOriginAudit = Get-Content -LiteralPath $duplicateHistoryOriginPath -Raw | ConvertFrom-Json -DateKind String
    Add-V2HistoricalContexts -Audit $duplicateHistoryOriginAudit
    $secondHistory = $duplicateHistoryOriginAudit.packages[0].historicalContext[0] |
        ConvertTo-Json -Depth 12 | ConvertFrom-Json -DateKind String
    $secondHistory.label = 'Different label but the same origin identity.'
    $duplicateHistoryOriginAudit.packages[0].historicalContext = @(
        $duplicateHistoryOriginAudit.packages[0].historicalContext[0], $secondHistory
    )
    Save-Audit -Audit $duplicateHistoryOriginAudit -Path $duplicateHistoryOriginPath
    Test-Scenario -Name 'Duplicate historical origin identity is rejected' `
        -AuditPath $duplicateHistoryOriginPath -ExpectedExitCode 1 `
        -ExpectedOutput 'historical context duplicates a historical origin identity'

    $completeHistoryPath = New-AuditFixture -Name 'complete-history-round-trip'
    $completeHistoryAudit = Get-Content -LiteralPath $completeHistoryPath -Raw | ConvertFrom-Json -DateKind String
    Add-CompleteHistoricalContexts -Audit $completeHistoryAudit
    Save-Audit -Audit $completeHistoryAudit -Path $completeHistoryPath
    Test-Scenario -Name 'Complete typed package and family history round-trip' -AuditPath $completeHistoryPath -ExpectedExitCode 0 `
        -ExpectedOutput 'validation passed for 2 packages, 1 families, and 1 source'

    $unknownPackageFieldPath = New-AuditFixture -Name 'unknown-package-field'
    $unknownPackageFieldAudit = Get-Content -LiteralPath $unknownPackageFieldPath -Raw | ConvertFrom-Json -DateKind String
    $unknownPackageFieldAudit.packages[0] | Add-Member -NotePropertyName internalReviewer -NotePropertyValue 'unreviewed-owner-note'
    Save-Audit -Audit $unknownPackageFieldAudit -Path $unknownPackageFieldPath
    Test-Scenario -Name 'Unknown package field would be silently dropped' -AuditPath $unknownPackageFieldPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' declares field 'internalReviewer' that the typed round-trip model does not cover"

    $unknownSourceFieldPath = New-AuditFixture -Name 'unknown-source-field'
    $unknownSourceFieldAudit = Get-Content -LiteralPath $unknownSourceFieldPath -Raw | ConvertFrom-Json -DateKind String
    $unknownSourceFieldAudit.packages[0].sourceResults[0] | Add-Member -NotePropertyName mirrorOf -NotePropertyValue 'https://mirror.example.test/'
    Save-Audit -Audit $unknownSourceFieldAudit -Path $unknownSourceFieldPath
    Test-Scenario -Name 'Unknown source result field would be silently dropped' -AuditPath $unknownSourceFieldPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' source result declares field 'mirrorOf' that the typed round-trip model does not cover"

    $unknownFamilyFieldPath = New-AuditFixture -Name 'unknown-family-field'
    $unknownFamilyFieldAudit = Get-Content -LiteralPath $unknownFamilyFieldPath -Raw | ConvertFrom-Json -DateKind String
    $unknownFamilyFieldAudit.familyDecisions[0] | Add-Member -NotePropertyName ownerSlackThread -NotePropertyValue 'https://example.test/thread/1'
    Save-Audit -Audit $unknownFamilyFieldAudit -Path $unknownFamilyFieldPath
    Test-Scenario -Name 'Unknown family field would be silently dropped' -AuditPath $unknownFamilyFieldPath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'fixture-family' declares field 'ownerSlackThread' that the typed round-trip model does not cover"

    $unknownPackageHistoryFieldPath = New-AuditFixture -Name 'unknown-package-history-field'
    $unknownPackageHistoryFieldAudit = Get-Content -LiteralPath $unknownPackageHistoryFieldPath -Raw | ConvertFrom-Json -DateKind String
    Add-CompleteHistoricalContexts -Audit $unknownPackageHistoryFieldAudit
    $unknownPackageHistoryFieldAudit.packages[0].historicalContext[0] |
        Add-Member -NotePropertyName priorReviewer -NotePropertyValue 'untyped-history-note'
    Save-Audit -Audit $unknownPackageHistoryFieldAudit -Path $unknownPackageHistoryFieldPath
    Test-Scenario -Name 'Unknown package history field would be silently dropped' `
        -AuditPath $unknownPackageHistoryFieldPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' historical context declares field 'priorReviewer' that the typed round-trip model does not cover"

    $unknownHistoricalSourceFieldPath = New-AuditFixture -Name 'unknown-historical-source-field'
    $unknownHistoricalSourceFieldAudit = Get-Content -LiteralPath $unknownHistoricalSourceFieldPath -Raw | ConvertFrom-Json -DateKind String
    Add-CompleteHistoricalContexts -Audit $unknownHistoricalSourceFieldAudit
    $unknownHistoricalSourceFieldAudit.packages[0].historicalContext[0].sourceResults[0] |
        Add-Member -NotePropertyName mirrorOf -NotePropertyValue 'https://mirror.example.test/'
    Save-Audit -Audit $unknownHistoricalSourceFieldAudit -Path $unknownHistoricalSourceFieldPath
    Test-Scenario -Name 'Unknown historical source field would be silently dropped' `
        -AuditPath $unknownHistoricalSourceFieldPath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' historical source result declares field 'mirrorOf' that the typed round-trip model does not cover"

    $unknownPreservationFieldPath = New-AuditFixture -Name 'unknown-origin-field'
    $unknownPreservationFieldAudit = Get-Content -LiteralPath $unknownPreservationFieldPath -Raw | ConvertFrom-Json -DateKind String
    $unknownPreservationFieldAudit.familyDecisions[0].origin |
        Add-Member -NotePropertyName ownerApproval -NotePropertyValue 'untyped-owner-note'
    Save-Audit -Audit $unknownPreservationFieldAudit -Path $unknownPreservationFieldPath
    Test-Scenario -Name 'Unknown origin field would be silently dropped' `
        -AuditPath $unknownPreservationFieldPath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'fixture-family' origin declares field 'ownerApproval' that the typed round-trip model does not cover"

    $unknownFamilyHistoryFieldPath = New-AuditFixture -Name 'unknown-family-history-field'
    $unknownFamilyHistoryFieldAudit = Get-Content -LiteralPath $unknownFamilyHistoryFieldPath -Raw | ConvertFrom-Json -DateKind String
    Add-CompleteHistoricalContexts -Audit $unknownFamilyHistoryFieldAudit
    $unknownFamilyHistoryFieldAudit.familyDecisions[0].historicalContext[0] |
        Add-Member -NotePropertyName previousOwner -NotePropertyValue 'untyped-history-owner'
    Save-Audit -Audit $unknownFamilyHistoryFieldAudit -Path $unknownFamilyHistoryFieldPath
    Test-Scenario -Name 'Unknown family history field would be silently dropped' `
        -AuditPath $unknownFamilyHistoryFieldPath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'fixture-family' historical context declares field 'previousOwner' that the typed round-trip model does not cover"

    $unknownHistoricalPreservationPath = New-AuditFixture -Name 'unknown-historical-preservation-field'
    $unknownHistoricalPreservationAudit = Get-Content -LiteralPath $unknownHistoricalPreservationPath -Raw | ConvertFrom-Json -DateKind String
    Add-CompleteHistoricalContexts -Audit $unknownHistoricalPreservationAudit
    $unknownHistoricalPreservationAudit.familyDecisions[0].historicalContext[0].preservation |
        Add-Member -NotePropertyName importedBy -NotePropertyValue 'untyped-importer'
    Save-Audit -Audit $unknownHistoricalPreservationAudit -Path $unknownHistoricalPreservationPath
    Test-Scenario -Name 'Unknown historical preservation field would be silently dropped' `
        -AuditPath $unknownHistoricalPreservationPath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'fixture-family' historical preservation declares field 'importedBy' that the typed round-trip model does not cover"

    $wrongOwnerTypePath = New-AuditFixture -Name 'wrong-owner-field-type'
    $wrongOwnerTypeAudit = Get-Content -LiteralPath $wrongOwnerTypePath -Raw | ConvertFrom-Json -DateKind String
    $wrongOwnerTypeAudit.packages[0].rationale = 123
    Save-Audit -Audit $wrongOwnerTypeAudit -Path $wrongOwnerTypePath
    Test-Scenario -Name 'Owner scalar JSON type is preserved exactly' -AuditPath $wrongOwnerTypePath -ExpectedExitCode 1 `
        -ExpectedOutput "Package 'Fixture.One' field 'rationale' must be a JSON string"

    $wrongCollectionTypePath = New-AuditFixture -Name 'wrong-owner-collection-type'
    $wrongCollectionTypeAudit = Get-Content -LiteralPath $wrongCollectionTypePath -Raw | ConvertFrom-Json -DateKind String
    $wrongCollectionTypeAudit.familyDecisions[0].packageIds = 'Fixture.One'
    Save-Audit -Audit $wrongCollectionTypeAudit -Path $wrongCollectionTypePath
    Test-Scenario -Name 'Owner collection JSON type is preserved exactly' -AuditPath $wrongCollectionTypePath -ExpectedExitCode 1 `
        -ExpectedOutput "Family 'fixture-family' field 'packageIds' must be a JSON array"

    foreach ($workflowRelativePath in @('../.github/workflows/ci.yml', '../.github/workflows/build-release.yml')) {
        $script:scenarioCount++
        $workflowPath = Join-Path $PSScriptRoot $workflowRelativePath
        $workflow = Get-Content -LiteralPath $workflowPath -Raw
        $validateIndex = $workflow.IndexOf('- name: Validate package version audit', [StringComparison]::Ordinal)
        $testIndex = $workflow.IndexOf('- name: Test package version audit validator', [StringComparison]::Ordinal)
        $consumerIndex = $workflow.IndexOf('- name: Validate Builds consumer package authority', [StringComparison]::Ordinal)
        $validateCommandIndex = $workflow.IndexOf(
            'run: pwsh -NoProfile -File ./Tools/validate-package-version-audit.ps1',
            [StringComparison]::Ordinal
        )
        $testCommandIndex = $workflow.IndexOf(
            'run: pwsh -NoProfile -File ./Tools/test-package-version-audit-validator.ps1',
            [StringComparison]::Ordinal
        )
        if (
            $validateIndex -lt 0 -or
            $validateCommandIndex -le $validateIndex -or
            $testIndex -le $validateCommandIndex -or
            $testCommandIndex -le $testIndex -or
            $consumerIndex -le $testCommandIndex
        ) {
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
