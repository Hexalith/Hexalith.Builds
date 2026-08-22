[CmdletBinding()]
param(
    [string] $CatalogPath = '',
    [string] $OutputPath = '',
    [string[]] $Source = @(),
    [Parameter(DontShow = $true)][string] $RequestFixturePath = '',
    [Parameter(DontShow = $true)][string] $PriorAuditPath = '',
    [Parameter(DontShow = $true)][string] $ConsumerEvidencePath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$requestFixtures = @{}

if (-not [string]::IsNullOrWhiteSpace($RequestFixturePath)) {
    try {
        $fixtureDocument = Get-Content -LiteralPath $RequestFixturePath -Raw -ErrorAction Stop |
            ConvertFrom-Json -ErrorAction Stop
        foreach ($property in @($fixtureDocument.responses.PSObject.Properties)) {
            $requestFixtures[$property.Name] = $property.Value
        }
    }
    catch {
        [Console]::Error.WriteLine(
            "Central package freshness audit failed: request fixture could not be loaded. $($_.Exception.GetBaseException().Message)"
        )
        exit 1
    }
}

function Stop-Audit {
    param([Parameter(Mandatory = $true)][string] $Message)

    [Console]::Error.WriteLine("Central package freshness audit failed: $Message")
    exit 1
}

function Get-PropertyText {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($null -eq $Object -or $Object.PSObject.Properties.Name -notcontains $Name) {
        return ''
    }

    return [string] $Object.$Name
}

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-Sha256File {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ArrayProperty {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($null -eq $Object -or $Object.PSObject.Properties.Name -notcontains $Name) {
        return @()
    }

    return @($Object.$Name)
}

function Get-IdentitySignature {
    param([AllowEmptyCollection()][object[]] $Values)

    return [string]::Join('|', @($Values | ForEach-Object { [string] $_ } | Sort-Object -CaseSensitive))
}

function Get-SourceScopeFingerprint {
    param([AllowEmptyCollection()][object[]] $Sources)

    $material = @($Sources | Where-Object { $null -ne $_ } | ForEach-Object {
            "$(Get-PropertyText -Object $_ -Name 'uri')|" +
            "$(Get-PropertyText -Object $_ -Name 'resolution')|" +
            "$(Get-PropertyText -Object $_ -Name 'diagnostic')"
        } | Sort-Object -CaseSensitive)
    return Get-Sha256Text -Value ([string]::Join("`n", $material))
}

function Get-ConsumerRelationFingerprint {
    param([AllowEmptyCollection()][object[]] $Entries)

    $material = @($Entries | Where-Object { $null -ne $_ } | ForEach-Object {
            "$(Get-PropertyText -Object $_ -Name 'family')|" +
            "$(Get-PropertyText -Object $_ -Name 'consumer')|" +
            "$(Get-PropertyText -Object $_ -Name 'packageId')|" +
            "$(Get-PropertyText -Object $_ -Name 'declarationPath')|" +
            "$(Get-PropertyText -Object $_ -Name 'declarationSha256')"
        } | Sort-Object -CaseSensitive)
    return Get-Sha256Text -Value ([string]::Join("`n", $material))
}

function Invoke-JsonRequest {
    param([Parameter(Mandatory = $true)][string] $Uri)

    if ($requestFixtures.Count -gt 0) {
        if (-not $requestFixtures.ContainsKey($Uri)) {
            throw "Request fixture contains no response for '$Uri'."
        }

        $fixture = $requestFixtures[$Uri]
        if ($fixture.PSObject.Properties.Name -contains 'error') {
            throw [string] $fixture.error
        }

        if ($fixture.PSObject.Properties.Name -notcontains 'response') {
            throw "Request fixture response for '$Uri' is malformed."
        }

        return $fixture.response
    }

    $lastError = ''
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            return Invoke-RestMethod -Uri $Uri -Method Get -TimeoutSec 45 -ErrorAction Stop
        }
        catch {
            $lastError = $_.Exception.GetBaseException().Message
            if ($attempt -lt 3) {
                Start-Sleep -Milliseconds (250 * $attempt)
            }
        }
    }

    throw "GET $Uri failed after 3 attempts. $lastError"
}

function Compare-NuGetVersion {
    param(
        [Parameter(Mandatory = $true)][string] $Left,
        [Parameter(Mandatory = $true)][string] $Right
    )

    $leftWithoutMetadata = $Left.Split('+', 2)[0]
    $rightWithoutMetadata = $Right.Split('+', 2)[0]
    $leftParts = $leftWithoutMetadata.Split('-', 2)
    $rightParts = $rightWithoutMetadata.Split('-', 2)
    $leftCore = $leftParts[0].Split('.')
    $rightCore = $rightParts[0].Split('.')
    for ($index = 0; $index -lt 4; $index++) {
        $leftNumber = if ($index -lt $leftCore.Count) { [long] $leftCore[$index] } else { 0 }
        $rightNumber = if ($index -lt $rightCore.Count) { [long] $rightCore[$index] } else { 0 }
        if ($leftNumber -ne $rightNumber) {
            return [Math]::Sign($leftNumber - $rightNumber)
        }
    }

    $leftPrerelease = if ($leftParts.Count -gt 1) { $leftParts[1] } else { '' }
    $rightPrerelease = if ($rightParts.Count -gt 1) { $rightParts[1] } else { '' }
    if ([string]::IsNullOrEmpty($leftPrerelease)) {
        return $(if ([string]::IsNullOrEmpty($rightPrerelease)) { 0 } else { 1 })
    }

    if ([string]::IsNullOrEmpty($rightPrerelease)) {
        return -1
    }

    $leftIdentifiers = $leftPrerelease.Split('.')
    $rightIdentifiers = $rightPrerelease.Split('.')
    $identifierCount = [Math]::Max($leftIdentifiers.Count, $rightIdentifiers.Count)
    for ($index = 0; $index -lt $identifierCount; $index++) {
        if ($index -ge $leftIdentifiers.Count) { return -1 }
        if ($index -ge $rightIdentifiers.Count) { return 1 }

        $leftNumeric = [System.Numerics.BigInteger]::Zero
        $rightNumeric = [System.Numerics.BigInteger]::Zero
        $leftIsNumeric = [System.Numerics.BigInteger]::TryParse($leftIdentifiers[$index], [ref] $leftNumeric)
        $rightIsNumeric = [System.Numerics.BigInteger]::TryParse($rightIdentifiers[$index], [ref] $rightNumeric)
        if ($leftIsNumeric -and $rightIsNumeric -and $leftNumeric -ne $rightNumeric) {
            return $leftNumeric.CompareTo($rightNumeric)
        }

        if ($leftIsNumeric -ne $rightIsNumeric) {
            return $(if ($leftIsNumeric) { -1 } else { 1 })
        }

        $comparison = [StringComparer]::OrdinalIgnoreCase.Compare($leftIdentifiers[$index], $rightIdentifiers[$index])
        if ($comparison -ne 0) {
            return [Math]::Sign($comparison)
        }
    }

    return 0
}

function Select-LatestVersion {
    param([string[]] $Versions)

    $latest = $null
    foreach ($version in @($Versions)) {
        if ($null -eq $latest -or (Compare-NuGetVersion -Left $version -Right $latest) -gt 0) {
            $latest = $version
        }
    }

    return $latest
}

function Get-PackageFamily {
    param([Parameter(Mandatory = $true)][string] $Id)

    if ($Id -match '^Hexalith\.([^.]+)') { return "hexalith-$($Matches[1].ToLowerInvariant())" }
    if ($Id -match '^Aspire\.') { return 'aspire' }
    if ($Id -match '^Dapr\.') { return 'dapr' }
    if ($Id -match '^Microsoft\.CodeAnalysis') { return 'roslyn' }
    if ($Id -match '^(Microsoft\.IdentityModel\.|Microsoft\.Identity\.Web$|System\.IdentityModel\.Tokens\.Jwt$)') {
        return 'identity-model'
    }
    if ($Id -match '^OpenTelemetry') { return 'opentelemetry' }
    if ($Id -match '^Microsoft\.FluentUI\.') { return 'fluent-ui' }
    if ($Id -match '^(AngleSharp|bunit)$') { return 'bunit-html' }
    if ($Id -match '^xunit\.') { return 'xunit' }
    if ($Id -match '^Verify(?:\.|$)') { return 'verify' }
    if ($Id -match '^(Microsoft\.AspNetCore\.|Microsoft\.Extensions\.|System\.Text\.Json$|System\.Collections\.Immutable$)') {
        return 'dotnet-10'
    }

    return "package:$($Id.ToLowerInvariant())"
}

function Assert-PriorAuditIdentityRelations {
    param([Parameter(Mandatory = $true)] $PriorAudit)

    $sourceUris = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($sourceRow in @(Get-ArrayProperty -Object $PriorAudit -Name 'sources')) {
        $sourceUri = Get-PropertyText -Object $sourceRow -Name 'uri'
        if ([string]::IsNullOrWhiteSpace($sourceUri) -or -not $sourceUris.Add($sourceUri)) {
            Stop-Audit "prior audit contains a blank or duplicate source identity '$sourceUri'."
        }
    }

    $packagesById = @{}
    $packagesByFamily = @{}
    foreach ($package in @(Get-ArrayProperty -Object $PriorAudit -Name 'packages')) {
        $id = Get-PropertyText -Object $package -Name 'id'
        $family = Get-PropertyText -Object $package -Name 'family'
        if ([string]::IsNullOrWhiteSpace($id) -or $packagesById.ContainsKey($id)) {
            Stop-Audit "prior audit contains a blank or duplicate package identity '$id'."
        }
        if ([string]::IsNullOrWhiteSpace($family)) {
            Stop-Audit "prior audit package '$id' has no family identity."
        }

        $packagesById[$id] = $package
        if (-not $packagesByFamily.ContainsKey($family)) {
            $packagesByFamily[$family] = [Collections.Generic.List[object]]::new()
        }
        $packagesByFamily[$family].Add($package)

        $resultSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($sourceResult in @(Get-ArrayProperty -Object $package -Name 'sourceResults')) {
            $resultSource = Get-PropertyText -Object $sourceResult -Name 'source'
            if (
                [string]::IsNullOrWhiteSpace($resultSource) -or
                -not $sourceUris.Contains($resultSource) -or
                -not $resultSources.Add($resultSource)
            ) {
                Stop-Audit "prior audit package '$id' contains a blank, undeclared, or duplicate source-result identity '$resultSource'."
            }
        }
        if ($resultSources.Count -ne $sourceUris.Count) {
            Stop-Audit "prior audit package '$id' does not contain exactly one result for every source identity."
        }
    }

    $decisionsByFamily = @{}
    foreach ($decision in @(Get-ArrayProperty -Object $PriorAudit -Name 'familyDecisions')) {
        $family = Get-PropertyText -Object $decision -Name 'family'
        if ([string]::IsNullOrWhiteSpace($family) -or $decisionsByFamily.ContainsKey($family)) {
            Stop-Audit "prior audit contains a blank or duplicate family identity '$family'."
        }
        if (-not $packagesByFamily.ContainsKey($family)) {
            Stop-Audit "prior audit family '$family' has no package identities."
        }

        $decisionsByFamily[$family] = $decision
        $declaredIds = @(Get-ArrayProperty -Object $decision -Name 'packageIds')
        $actualIds = @($packagesByFamily[$family] | ForEach-Object { Get-PropertyText -Object $_ -Name 'id' })
        if ((Get-IdentitySignature -Values $declaredIds) -cne (Get-IdentitySignature -Values $actualIds)) {
            Stop-Audit "prior audit family '$family' package identities do not exactly match its package rows."
        }

        $decisionDisposition = Get-PropertyText -Object $decision -Name 'disposition'
        $decisionRollbackGroup = Get-PropertyText -Object $decision -Name 'rollbackGroup'
        foreach ($package in $packagesByFamily[$family]) {
            $id = Get-PropertyText -Object $package -Name 'id'
            if ((Get-PropertyText -Object $package -Name 'disposition') -cne $decisionDisposition) {
                Stop-Audit "prior audit package '$id' disposition does not match family '$family'."
            }
            if ((Get-PropertyText -Object $package -Name 'rollbackGroup') -cne $decisionRollbackGroup) {
                Stop-Audit "prior audit package '$id' rollback group does not match family '$family'."
            }
        }
    }

    foreach ($family in $packagesByFamily.Keys) {
        if (-not $decisionsByFamily.ContainsKey($family)) {
            Stop-Audit "prior audit package family '$family' has no family decision identity."
        }
    }

    if ($PriorAudit.PSObject.Properties.Name -contains 'consumerEvidence') {
        $consumerRelations = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $consumerEntries = @(Get-ArrayProperty -Object $PriorAudit.consumerEvidence -Name 'entries')
        foreach ($entry in $consumerEntries) {
            $family = Get-PropertyText -Object $entry -Name 'family'
            $consumer = Get-PropertyText -Object $entry -Name 'consumer'
            $packageId = Get-PropertyText -Object $entry -Name 'packageId'
            if (
                [string]::IsNullOrWhiteSpace($consumer) -or
                -not $packagesById.ContainsKey($packageId) -or
                -not $consumerRelations.Add("$consumer|$packageId")
            ) {
                Stop-Audit "prior audit contains a blank, unknown-package, or duplicate consumer identity '$consumer|$packageId'."
            }
            if ($family -cne (Get-PropertyText -Object $packagesById[$packageId] -Name 'family')) {
                Stop-Audit "prior audit consumer '$consumer|$packageId' has an inconsistent family identity '$family'."
            }
        }

        $declaredHash = Get-PropertyText -Object $PriorAudit.consumerEvidence -Name 'sha256'
        $hasDeclarationBinding = @($consumerEntries | Where-Object {
                $_.PSObject.Properties.Name -contains 'declarationPath' -or
                $_.PSObject.Properties.Name -contains 'declarationSha256'
            }).Count -gt 0
        if ($hasDeclarationBinding) {
            foreach ($entry in $consumerEntries) {
                if (
                    [string]::IsNullOrWhiteSpace((Get-PropertyText -Object $entry -Name 'declarationPath')) -or
                    (Get-PropertyText -Object $entry -Name 'declarationSha256') -cnotmatch '^[0-9a-f]{64}$'
                ) {
                    Stop-Audit 'prior audit consumer provenance has an incomplete declaration-byte binding.'
                }
            }
            if ($declaredHash -cne (Get-ConsumerRelationFingerprint -Entries $consumerEntries)) {
                Stop-Audit 'prior audit consumer evidence SHA-256 does not match its relations and declaration bytes.'
            }
        }
    }
}

function Get-OwnedConsumerEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $Root,
        [Parameter(Mandatory = $true)][Collections.Generic.HashSet[string]] $CatalogPackageIds,
        [AllowEmptyString()][string] $FixturePath
    )

    $entries = [Collections.Generic.List[object]]::new()
    $discovery = 'git-ls-files'
    $fixtureIdentity = $null
    $fixtureSha256 = $null
    $fixtureMode = $null
    if (-not [string]::IsNullOrWhiteSpace($FixturePath)) {
        try {
            $resolvedFixturePath = (Resolve-Path -LiteralPath $FixturePath -ErrorAction Stop).ProviderPath
            $fixtureDocument = Get-Content -LiteralPath $resolvedFixturePath -Raw -ErrorAction Stop |
                ConvertFrom-Json -ErrorAction Stop
            $fixtureSha256 = Get-Sha256File -Path $resolvedFixturePath
            foreach ($entry in @(Get-ArrayProperty -Object $fixtureDocument -Name 'entries')) {
                $entries.Add([pscustomobject] @{
                        consumer = Get-PropertyText -Object $entry -Name 'consumer'
                        packageId = Get-PropertyText -Object $entry -Name 'packageId'
                        declarationPath = [IO.Path]::GetFileName($resolvedFixturePath)
                        declarationSha256 = $fixtureSha256
                    })
            }
            $discovery = 'explicit-fixture'
            $fixtureIdentity = [IO.Path]::GetFileName($resolvedFixturePath)
            $fixtureMode = 'synthetic-explicit'
        }
        catch {
            Stop-Audit "consumer evidence fixture could not be loaded. $($_.Exception.GetBaseException().Message)"
        }
    }
    else {
        $trackedProjectOutput = @(& git -C $Root ls-files -- '*.csproj' '*.props' '*.targets' 2>&1)
        if ($LASTEXITCODE -ne 0) {
            Stop-Audit "owned direct-consumer discovery failed. $([string]::Join("`n", $trackedProjectOutput))"
        }

        foreach ($relativeProjectPath in @($trackedProjectOutput | ForEach-Object { [string] $_ } | Sort-Object -CaseSensitive)) {
            $projectPath = Join-Path $Root $relativeProjectPath
            try {
                [xml] $projectDocument = Get-Content -LiteralPath $projectPath -Raw -ErrorAction Stop
            }
            catch {
                Stop-Audit "owned direct-consumer project '$relativeProjectPath' could not be parsed. $($_.Exception.GetBaseException().Message)"
            }

            $declarationSha256 = Get-Sha256File -Path $projectPath
            foreach ($reference in @($projectDocument.SelectNodes("//*[local-name()='PackageReference']"))) {
                $packageId = [string] $reference.GetAttribute('Include')
                if (-not [string]::IsNullOrWhiteSpace($packageId) -and $CatalogPackageIds.Contains($packageId)) {
                    $entries.Add([pscustomobject] @{
                            consumer = ([string] $relativeProjectPath).Replace('\', '/')
                            packageId = $packageId
                            declarationPath = ([string] $relativeProjectPath).Replace('\', '/')
                            declarationSha256 = $declarationSha256
                        })
                }
            }
        }
    }

    $relations = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        if (
            [string]::IsNullOrWhiteSpace($entry.consumer) -or
            -not $CatalogPackageIds.Contains($entry.packageId) -or
            [string]::IsNullOrWhiteSpace($entry.declarationPath) -or
            $entry.declarationSha256 -cnotmatch '^[0-9a-f]{64}$' -or
            -not $relations.Add("$($entry.consumer)|$($entry.packageId)")
        ) {
            Stop-Audit "consumer evidence contains a blank, unknown-package, or duplicate identity '$($entry.consumer)|$($entry.packageId)'."
        }

        $entry | Add-Member -NotePropertyName family -NotePropertyValue (Get-PackageFamily -Id $entry.packageId) -Force
    }

    $sortedEntries = @($entries | Sort-Object family, consumer, packageId)
    return [pscustomobject] @{
        discovery = $discovery
        fixture = $fixtureIdentity
        fixtureSha256 = $fixtureSha256
        fixtureMode = $fixtureMode
        sha256 = Get-ConsumerRelationFingerprint -Entries $sortedEntries
        entries = $sortedEntries
    }
}

function Get-PackageMetadataFingerprint {
    param([AllowEmptyCollection()][object[]] $PackageRows)

    $material = foreach ($package in @($PackageRows | Sort-Object id)) {
        $sourceMaterial = [string]::Join(',', @($package.sourceResults | Sort-Object source | ForEach-Object {
                    "$($_.source)=$($_.listingState):$($_.latestStable):$($_.latestPrerelease)"
                }))
        "$($package.id)|$($package.auditedVersion)|$($package.selectedVersion)|$($package.latestStable)|" +
            "$($package.latestPrerelease)|$($package.listingState)|$sourceMaterial"
    }

    return Get-Sha256Text -Value ([string]::Join("`n", @($material)))
}

function New-LegacyHistoryPreservation {
    return [ordered] @{
        status = 'legacy-unbound'
        reason = 'The legacy history record predated byte-bound preservation provenance.'
    }
}

function ConvertTo-TypedFamilyHistory {
    param(
        [Parameter(Mandatory = $true)] $Entry,
        [Parameter(Mandatory = $true)][string] $Family,
        [Parameter(Mandatory = $true)][object[]] $PackageIds
    )

    $schema = Get-PropertyText -Object $Entry -Name 'schema'
    if ($schema -ceq 'hexalith.package-audit-family-history.v1') {
        return $Entry
    }
    if (-not [string]::IsNullOrWhiteSpace($schema)) {
        Stop-Audit "prior family history for '$Family' uses unsupported schema '$schema'."
    }

    return [ordered] @{
        schema = 'hexalith.package-audit-family-history.v1'
        label = Get-PropertyText -Object $Entry -Name 'label'
        auditedAtUtc = Get-PropertyText -Object $Entry -Name 'auditedAtUtc'
        generatedFromRevision = Get-PropertyText -Object $Entry -Name 'generatedFromRevision'
        family = $Family
        disposition = Get-PropertyText -Object $Entry -Name 'disposition'
        rollbackGroup = '<unrecorded>'
        packageIds = @($PackageIds)
        rationale = Get-PropertyText -Object $Entry -Name 'rationale'
        compatibilityEvidence = Get-PropertyText -Object $Entry -Name 'compatibilityEvidence'
        removalTrigger = Get-PropertyText -Object $Entry -Name 'removalTrigger'
        representativeConsumers = @(Get-ArrayProperty -Object $Entry -Name 'representativeConsumers')
        preservation = New-LegacyHistoryPreservation
        supersededBecause = Get-PropertyText -Object $Entry -Name 'supersededBecause'
    }
}

function ConvertTo-TypedPackageHistory {
    param(
        [Parameter(Mandatory = $true)] $Entry,
        [Parameter(Mandatory = $true)][string] $Id,
        [Parameter(Mandatory = $true)][string] $Family
    )

    $schema = Get-PropertyText -Object $Entry -Name 'schema'
    if ($schema -ceq 'hexalith.package-audit-package-history.v1') {
        return $Entry
    }
    if (-not [string]::IsNullOrWhiteSpace($schema)) {
        Stop-Audit "prior package history for '$Id' uses unsupported schema '$schema'."
    }

    return [ordered] @{
        schema = 'hexalith.package-audit-package-history.v1'
        label = Get-PropertyText -Object $Entry -Name 'label'
        auditedAtUtc = Get-PropertyText -Object $Entry -Name 'auditedAtUtc'
        generatedFromRevision = Get-PropertyText -Object $Entry -Name 'generatedFromRevision'
        id = $Id
        auditedVersion = Get-PropertyText -Object $Entry -Name 'auditedVersion'
        selectedVersion = Get-PropertyText -Object $Entry -Name 'selectedVersion'
        latestStable = $null
        latestPrerelease = $null
        listingState = 'unrecorded'
        family = $Family
        disposition = Get-PropertyText -Object $Entry -Name 'disposition'
        rollbackGroup = '<unrecorded>'
        rationale = Get-PropertyText -Object $Entry -Name 'rationale'
        evidence = '<unrecorded>'
        removalTrigger = Get-PropertyText -Object $Entry -Name 'removalTrigger'
        sourceResults = @()
        supersededBecause = Get-PropertyText -Object $Entry -Name 'supersededBecause'
    }
}

function Get-ConfiguredSources {
    param([Parameter(Mandatory = $true)][string] $WorkingDirectory)

    if ($Source.Count -gt 0) {
        return @($Source)
    }

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = @(& dotnet nuget list source --format short 2>&1)
        if ($LASTEXITCODE -ne 0) {
            Stop-Audit "configured NuGet sources could not be listed. $([string]::Join("`n", $output))"
        }
    }
    finally {
        Pop-Location
    }

    return @(
        $output |
            ForEach-Object { [string] $_ } |
            Where-Object { $_ -match '^\s*E\s+(.+?)\s*$' } |
            ForEach-Object { $Matches[1] }
    )
}

if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $CatalogPath = Join-Path $PSScriptRoot '../Props/Directory.Packages.props'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot 'package-version-audit.json'
}

try {
    $resolvedCatalogPath = (Resolve-Path -LiteralPath $CatalogPath -ErrorAction Stop).ProviderPath
}
catch {
    Stop-Audit "catalog could not be loaded from '$CatalogPath'. $($_.Exception.GetBaseException().Message)"
}

$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
if ([StringComparer]::OrdinalIgnoreCase.Equals($resolvedOutputPath, $resolvedCatalogPath)) {
    Stop-Audit 'output path must differ from the package catalog path.'
}

$priorAudit = $null
$candidatePriorAuditPath = if (-not [string]::IsNullOrWhiteSpace($PriorAuditPath)) {
    $PriorAuditPath
}
elseif (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf) {
    $resolvedOutputPath
}
else {
    ''
}
if (-not [string]::IsNullOrWhiteSpace($candidatePriorAuditPath)) {
    try {
        $resolvedPriorAuditPath = (Resolve-Path -LiteralPath $candidatePriorAuditPath -ErrorAction Stop).ProviderPath
        $priorAudit = Get-Content -LiteralPath $resolvedPriorAuditPath -Raw -ErrorAction Stop |
            ConvertFrom-Json -DateKind String -ErrorAction Stop
        Assert-PriorAuditIdentityRelations -PriorAudit $priorAudit
    }
    catch {
        Stop-Audit "prior audit could not be loaded or validated. $($_.Exception.GetBaseException().Message)"
    }
}

$evaluationOutput = @(& dotnet msbuild $resolvedCatalogPath -nologo -getItem:PackageVersion 2>&1)
if ($LASTEXITCODE -ne 0) {
    Stop-Audit "catalog evaluation failed. $([string]::Join("`n", $evaluationOutput))"
}

try {
    $evaluation = ([string]::Join("`n", $evaluationOutput) | ConvertFrom-Json -ErrorAction Stop)
}
catch {
    Stop-Audit "catalog evaluation returned malformed JSON. $($_.Exception.GetBaseException().Message)"
}

$configuredSources = @(Get-ConfiguredSources -WorkingDirectory $repositoryRoot)
if ($configuredSources.Count -eq 0) {
    Stop-Audit 'no enabled NuGet source was discovered.'
}

$sourceContracts = [System.Collections.Generic.List[object]]::new()
foreach ($sourceUri in $configuredSources) {
    try {
        $parsedSourceUri = $null
        if (-not [Uri]::TryCreate($sourceUri, [UriKind]::Absolute, [ref] $parsedSourceUri)) {
            throw 'configured source is not an absolute URI'
        }

        $serviceIndex = Invoke-JsonRequest -Uri $sourceUri
        $registrationResources = @($serviceIndex.resources | Where-Object {
                @($_.'@type') | Where-Object { $_ -match '^RegistrationsBaseUrl/' }
            })
        $registrationResource = @(
            $registrationResources | Where-Object { @($_.'@type') -contains 'RegistrationsBaseUrl/3.6.0' } |
                Select-Object -First 1
        )
        if ($registrationResource.Count -eq 0) {
            $registrationResource = @(
                $registrationResources | Where-Object { @($_.'@type') -contains 'RegistrationsBaseUrl/3.4.0' } |
                    Select-Object -First 1
            )
        }
        if ($registrationResource.Count -eq 0) {
            $registrationResource = @($registrationResources | Select-Object -First 1)
        }
        $flatResource = @($serviceIndex.resources | Where-Object {
                @($_.'@type') | Where-Object { $_ -eq 'PackageBaseAddress/3.0.0' }
            } | Select-Object -First 1)
        if ($registrationResource.Count -ne 1 -or $flatResource.Count -ne 1) {
            throw 'required V3 registration or flat-container resource is missing'
        }

        $registrationUri = $null
        $flatContainerUri = $null
        if (
            -not [Uri]::TryCreate([string] $registrationResource[0].'@id', [UriKind]::Absolute, [ref] $registrationUri) -or
            -not [Uri]::TryCreate([string] $flatResource[0].'@id', [UriKind]::Absolute, [ref] $flatContainerUri)
        ) {
            throw 'required V3 resource URI is blank or relative'
        }

        $sourceContracts.Add([pscustomobject] @{
                Uri = $sourceUri
                Registration = $registrationUri.AbsoluteUri.TrimEnd('/')
                FlatContainer = $flatContainerUri.AbsoluteUri.TrimEnd('/')
                Resolution = 'resolved'
                Diagnostic = 'NuGet V3 registration and flat-container resources resolved.'
            })
    }
    catch {
        $sourceContracts.Add([pscustomobject] @{
                Uri = $sourceUri
                Registration = ''
                FlatContainer = ''
                Resolution = 'unresolved'
                Diagnostic = $_.Exception.GetBaseException().Message
            })
    }
}

$packages = [System.Collections.Generic.List[object]]::new()
$evaluatedItems = @($evaluation.Items.PackageVersion)
$catalogPackageIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($evaluatedItem in $evaluatedItems) {
    if (-not $catalogPackageIds.Add([string] $evaluatedItem.Identity)) {
        Stop-Audit "evaluated catalog contains duplicate package identity '$($evaluatedItem.Identity)'."
    }
}
$consumerEvidence = Get-OwnedConsumerEvidence `
    -Root $repositoryRoot `
    -CatalogPackageIds $catalogPackageIds `
    -FixturePath $ConsumerEvidencePath
foreach ($item in @($evaluatedItems | Sort-Object Identity)) {
    $id = [string] $item.Identity
    $currentVersion = [string] $item.Version
    $sourceResults = [System.Collections.Generic.List[object]]::new()
    $allStable = [System.Collections.Generic.List[string]]::new()
    $allPrerelease = [System.Collections.Generic.List[string]]::new()

    foreach ($sourceContract in $sourceContracts) {
        if ($sourceContract.Resolution -ne 'resolved') {
            $sourceResults.Add([pscustomobject] @{
                    source = $sourceContract.Uri
                    listingState = 'unresolved'
                    latestStable = $null
                    latestPrerelease = $null
                    diagnostic = $sourceContract.Diagnostic
                })
            continue
        }

        try {
            $lowerId = $id.ToLowerInvariant()
            $registration = Invoke-JsonRequest -Uri "$($sourceContract.Registration)/$lowerId/index.json"
            $entries = [System.Collections.Generic.List[object]]::new()
            foreach ($page in @($registration.items)) {
                $pageItems = if ($page.PSObject.Properties.Name -contains 'items') { @($page.items) } else { @() }
                if (@($pageItems).Count -eq 0) {
                    $pageDocument = Invoke-JsonRequest -Uri ([string] $page.'@id')
                    if ($pageDocument.PSObject.Properties.Name -notcontains 'items') {
                        throw "Registration page '$($page.'@id')' contains no items."
                    }

                    $pageItems = @($pageDocument.items)
                }

                foreach ($leaf in $pageItems) {
                    if ($leaf.PSObject.Properties.Name -contains 'catalogEntry' -and $null -ne $leaf.catalogEntry) {
                        $entries.Add($leaf.catalogEntry)
                    }
                }
            }

            $listedVersions = @($entries | Where-Object {
                    $_.PSObject.Properties.Name -notcontains 'listed' -or [bool] $_.listed
                } | ForEach-Object { [string] $_.version })
            $stableVersions = @($listedVersions | Where-Object { $_ -notmatch '-' })
            $prereleaseVersions = @($listedVersions | Where-Object { $_ -match '-' })
            $latestStable = Select-LatestVersion -Versions $stableVersions
            $latestPrerelease = Select-LatestVersion -Versions $prereleaseVersions
            if ($null -ne $latestStable) { $allStable.Add($latestStable) }
            if ($null -ne $latestPrerelease) { $allPrerelease.Add($latestPrerelease) }

            $currentEntry = @($entries | Where-Object { [string] $_.version -ieq $currentVersion } | Select-Object -First 1)
            if ($currentEntry.Count -eq 1) {
                $listingState = if (
                    $currentEntry[0].PSObject.Properties.Name -contains 'listed' -and
                    -not [bool] $currentEntry[0].listed
                ) { 'unlisted' } else { 'listed' }
            }
            else {
                $flatIndex = Invoke-JsonRequest -Uri "$($sourceContract.FlatContainer)/$lowerId/index.json"
                $flatVersions = @($flatIndex.versions | ForEach-Object { [string] $_ })
                $listingState = if (@($flatVersions | Where-Object { $_ -ieq $currentVersion }).Count -gt 0) {
                    'unlisted'
                }
                else {
                    'missing'
                }
            }

            $sourceResults.Add([pscustomobject] @{
                    source = $sourceContract.Uri
                    listingState = $listingState
                    latestStable = $latestStable
                    latestPrerelease = $latestPrerelease
                    diagnostic = 'Registration metadata resolved.'
                })
        }
        catch {
            $diagnostic = $_.Exception.GetBaseException().Message
            $failureState = if ($diagnostic -match '\b404\s*\(Not Found\)') { 'missing' } else { 'unresolved' }
            $sourceResults.Add([pscustomobject] @{
                    source = $sourceContract.Uri
                    listingState = $failureState
                    latestStable = $null
                    latestPrerelease = $null
                    diagnostic = $diagnostic
                })
        }
    }

    $latestStable = Select-LatestVersion -Versions @($allStable)
    $latestPrerelease = Select-LatestVersion -Versions @($allPrerelease)
    $states = @($sourceResults | ForEach-Object { $_.listingState })
    $listingState = if ($states -contains 'listed') { 'listed' }
    elseif ($states -contains 'unlisted') { 'unlisted' }
    elseif (@($states | Where-Object { $_ -ne 'unresolved' }).Count -eq 0) { 'unresolved' }
    else { 'missing' }
    $family = Get-PackageFamily -Id $id

    if ($listingState -eq 'listed' -and $latestStable -ieq $currentVersion) {
        $rationale = 'Current pin is the latest listed stable release on the configured sources.'
    }
    elseif ($listingState -eq 'listed' -and $currentVersion -match '-') {
        $rationale = 'Retain the intentional prerelease channel until its coupled family passes compatibility review.'
    }
    elseif ($listingState -eq 'listed' -and $null -ne $latestStable) {
        $rationale = "Retain $currentVersion pending compatibility validation of listed stable candidate $latestStable."
    }
    else {
        $rationale = "Retain $currentVersion because its configured-source state is $listingState; incomplete source results must not cause a downgrade."
    }

    $packages.Add([pscustomobject] @{
            id = $id
            auditedVersion = $currentVersion
            selectedVersion = $currentVersion
            latestStable = $latestStable
            latestPrerelease = $latestPrerelease
            listingState = $listingState
            family = $family
            disposition = 'retained'
            rollbackGroup = $family
            rationale = $rationale
            evidence = [string]::Join('; ', @($sourceResults | ForEach-Object {
                        "$($_.source): state=$($_.listingState), stable=$($_.latestStable), prerelease=$($_.latestPrerelease)"
                    }))
            removalTrigger = 'Re-run the live audit and accept a source-resolved candidate only after the family and representative consumers pass compatibility validation.'
            sourceResults = @($sourceResults)
        })
}

$revisionOutput = @(& git -C $repositoryRoot rev-parse HEAD 2>&1)
$generatedFromRevision = if ($LASTEXITCODE -eq 0) { ([string] $revisionOutput[0]).Trim() } else { '' }
if ($generatedFromRevision -cnotmatch '^[0-9a-f]{40}$') {
    Stop-Audit "Git revision discovery did not return a full lowercase revision. $([string]::Join("`n", $revisionOutput))"
}

$catalogRelativePath = [IO.Path]::GetRelativePath($repositoryRoot, $resolvedCatalogPath).Replace('\', '/')
$catalogSha256 = Get-Sha256File -Path $resolvedCatalogPath
$currentSourceFingerprint = Get-SourceScopeFingerprint -Sources @($sourceContracts)
$priorCatalogPath = if ($null -eq $priorAudit) { '' } else { Get-PropertyText -Object $priorAudit -Name 'catalogPath' }
$priorCatalogSha256 = if ($null -eq $priorAudit) { '' } else { Get-PropertyText -Object $priorAudit -Name 'catalogSha256' }
$priorSourceFingerprint = if ($null -eq $priorAudit) {
    ''
}
else {
    Get-SourceScopeFingerprint -Sources @(Get-ArrayProperty -Object $priorAudit -Name 'sources')
}
$priorPackagesByFamily = @{}
$priorPackagesById = @{}
$priorDecisionsByFamily = @{}
if ($null -ne $priorAudit) {
    foreach ($priorPackage in @(Get-ArrayProperty -Object $priorAudit -Name 'packages')) {
        $priorPackageId = Get-PropertyText -Object $priorPackage -Name 'id'
        $priorFamily = Get-PropertyText -Object $priorPackage -Name 'family'
        $priorPackagesById[$priorPackageId] = $priorPackage
        if (-not $priorPackagesByFamily.ContainsKey($priorFamily)) {
            $priorPackagesByFamily[$priorFamily] = [System.Collections.Generic.List[object]]::new()
        }
        $priorPackagesByFamily[$priorFamily].Add($priorPackage)
    }
    foreach ($priorDecision in @(Get-ArrayProperty -Object $priorAudit -Name 'familyDecisions')) {
        $priorDecisionsByFamily[(Get-PropertyText -Object $priorDecision -Name 'family')] = $priorDecision
    }
}

$priorConsumerEvidenceEntries = if ($null -ne $priorAudit -and
    $priorAudit.PSObject.Properties.Name -contains 'consumerEvidence') {
    @(Get-ArrayProperty -Object $priorAudit.consumerEvidence -Name 'entries')
}
else {
    @()
}
$priorConsumerEvidenceTrusted = $null -ne $priorAudit -and
    $priorAudit.PSObject.Properties.Name -contains 'consumerEvidence' -and
    (Get-PropertyText -Object $priorAudit.consumerEvidence -Name 'schema') -ceq 'hexalith.package-consumer-evidence.v1' -and
    (Get-PropertyText -Object $priorAudit.consumerEvidence -Name 'sha256') -cmatch '^[0-9a-f]{64}$' -and
    @($priorConsumerEvidenceEntries | Where-Object {
            (Get-PropertyText -Object $_ -Name 'declarationPath') -eq '' -or
            (Get-PropertyText -Object $_ -Name 'declarationSha256') -cnotmatch '^[0-9a-f]{64}$'
        }).Count -eq 0
$familyDecisions = [System.Collections.Generic.List[object]]::new()
foreach ($group in @($packages | Group-Object family | Sort-Object Name)) {
    $family = $group.Name
    $packageIds = @($group.Group | ForEach-Object { $_.id } | Sort-Object)
    $currentConsumers = @($consumerEvidence.entries | Where-Object { $_.family -ceq $family } |
            ForEach-Object { $_.consumer } | Sort-Object -CaseSensitive -Unique)
    $currentConsumerRelations = @($consumerEvidence.entries | Where-Object { $_.family -ceq $family })
    $currentConsumerFingerprint = Get-ConsumerRelationFingerprint -Entries $currentConsumerRelations
    $currentMetadataFingerprint = Get-PackageMetadataFingerprint -PackageRows @($group.Group)
    $priorDecision = if ($priorDecisionsByFamily.ContainsKey($family)) { $priorDecisionsByFamily[$family] } else { $null }
    $priorFamilyPackages = @()
    if ($priorPackagesByFamily.ContainsKey($family)) {
        $priorFamilyPackages = @($priorPackagesByFamily[$family])
    }
    $priorConsumerRelations = if ($priorConsumerEvidenceTrusted) {
        @($priorConsumerEvidenceEntries | Where-Object {
                (Get-PropertyText -Object $_ -Name 'packageId') -in $packageIds
            })
    }
    else {
        @()
    }
    $priorConsumerFingerprint = Get-ConsumerRelationFingerprint -Entries $priorConsumerRelations
    $priorMetadataFingerprint = if ($priorFamilyPackages.Count -gt 0) {
        Get-PackageMetadataFingerprint -PackageRows $priorFamilyPackages
    }
    else {
        ''
    }

    $preserve = $true
    $preservationReason = 'prior decision and current provenance are identical'
    if ($null -eq $priorDecision) {
        $preserve = $false
        $preservationReason = 'no prior family decision exists'
    }
    elseif ($priorCatalogPath -cne $catalogRelativePath) {
        $preserve = $false
        $preservationReason = 'catalog scope changed'
    }
    elseif ($priorCatalogSha256 -cne $catalogSha256) {
        $preserve = $false
        $preservationReason = 'tracked catalog declaration bytes changed or were not previously bound'
    }
    elseif ($priorSourceFingerprint -cne $currentSourceFingerprint) {
        $preserve = $false
        $preservationReason = 'configured source scope changed'
    }
    elseif ((Get-IdentitySignature -Values @($priorFamilyPackages | ForEach-Object { $_.id })) -cne
        (Get-IdentitySignature -Values $packageIds)) {
        $preserve = $false
        $preservationReason = 'family package identities changed'
    }
    elseif ($priorMetadataFingerprint -cne $currentMetadataFingerprint) {
        $preserve = $false
        $preservationReason = 'package or source metadata changed'
    }
    elseif ($priorConsumerEvidenceTrusted -and $priorConsumerFingerprint -cne $currentConsumerFingerprint) {
        $preserve = $false
        $preservationReason = 'owned direct-consumer relations or tracked declaration bytes changed'
    }
    elseif (-not $priorConsumerEvidenceTrusted -and
        (Get-PropertyText -Object $priorDecision -Name 'disposition') -ceq 'accepted') {
        $preserve = $false
        $preservationReason = 'accepted legacy decision lacks trusted consumer declaration provenance'
    }
    elseif ((Get-PropertyText -Object $priorDecision -Name 'disposition') -ceq 'accepted' -and $currentConsumers.Count -eq 0) {
        $preserve = $false
        $preservationReason = 'accepted decision has no current owned direct-consumer evidence'
    }

    $historicalContext = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $priorDecision) {
        foreach ($historicalEntry in @(Get-ArrayProperty -Object $priorDecision -Name 'historicalContext')) {
            $historicalContext.Add((ConvertTo-TypedFamilyHistory -Entry $historicalEntry -Family $family -PackageIds $packageIds))
        }
        if (-not $preserve) {
            $historicalContext.Add([ordered] @{
                    schema = 'hexalith.package-audit-family-history.v1'
                    label = 'Historical prior-audit owner/runtime context; not current metadata or acceptance evidence.'
                    auditedAtUtc = Get-PropertyText -Object $priorAudit -Name 'auditedAtUtc'
                    generatedFromRevision = Get-PropertyText -Object $priorAudit -Name 'generatedFromRevision'
                    family = Get-PropertyText -Object $priorDecision -Name 'family'
                    disposition = Get-PropertyText -Object $priorDecision -Name 'disposition'
                    rollbackGroup = Get-PropertyText -Object $priorDecision -Name 'rollbackGroup'
                    packageIds = @(Get-ArrayProperty -Object $priorDecision -Name 'packageIds')
                    rationale = Get-PropertyText -Object $priorDecision -Name 'rationale'
                    compatibilityEvidence = Get-PropertyText -Object $priorDecision -Name 'compatibilityEvidence'
                    removalTrigger = Get-PropertyText -Object $priorDecision -Name 'removalTrigger'
                    representativeConsumers = @(Get-ArrayProperty -Object $priorDecision -Name 'representativeConsumers')
                    preservation = if ($priorDecision.PSObject.Properties.Name -contains 'preservation') {
                        $priorDecision.preservation
                    }
                    else {
                        New-LegacyHistoryPreservation
                    }
                    supersededBecause = $preservationReason
                })
        }
    }

    $disposition = if ($preserve) { Get-PropertyText -Object $priorDecision -Name 'disposition' } else { 'retained' }
    $rollbackGroup = if ($preserve) { Get-PropertyText -Object $priorDecision -Name 'rollbackGroup' } else { $family }
    $rationale = if ($preserve) {
        Get-PropertyText -Object $priorDecision -Name 'rationale'
    }
    else {
        'Retain the current audited family until current metadata and owned direct consumers complete compatibility acceptance.'
    }
    $compatibilityEvidence = if ($preserve) {
        Get-PropertyText -Object $priorDecision -Name 'compatibilityEvidence'
    }
    else {
        'Current NuGet metadata and owned direct-consumer discovery are recorded; historical claims are not current acceptance evidence.'
    }
    $removalTrigger = if ($preserve) {
        Get-PropertyText -Object $priorDecision -Name 'removalTrigger'
    }
    else {
        'Accept only after current family compatibility, consumer evidence, and owner review pass against this exact audit provenance.'
    }

    foreach ($package in $group.Group) {
        $priorPackage = if ($priorPackagesById.ContainsKey($package.id)) { $priorPackagesById[$package.id] } else { $null }
        $packageHistory = [System.Collections.Generic.List[object]]::new()
        if ($null -ne $priorPackage) {
            foreach ($historicalEntry in @(Get-ArrayProperty -Object $priorPackage -Name 'historicalContext')) {
                $packageHistory.Add((ConvertTo-TypedPackageHistory -Entry $historicalEntry -Id $package.id -Family $family))
            }
            if (-not $preserve) {
                $packageHistory.Add([ordered] @{
                        schema = 'hexalith.package-audit-package-history.v1'
                        label = 'Historical prior-audit owner/runtime context; not current package metadata or acceptance evidence.'
                        auditedAtUtc = Get-PropertyText -Object $priorAudit -Name 'auditedAtUtc'
                        generatedFromRevision = Get-PropertyText -Object $priorAudit -Name 'generatedFromRevision'
                        id = Get-PropertyText -Object $priorPackage -Name 'id'
                        auditedVersion = Get-PropertyText -Object $priorPackage -Name 'auditedVersion'
                        selectedVersion = Get-PropertyText -Object $priorPackage -Name 'selectedVersion'
                        latestStable = if ($priorPackage.PSObject.Properties.Name -contains 'latestStable') { $priorPackage.latestStable } else { $null }
                        latestPrerelease = if ($priorPackage.PSObject.Properties.Name -contains 'latestPrerelease') { $priorPackage.latestPrerelease } else { $null }
                        listingState = Get-PropertyText -Object $priorPackage -Name 'listingState'
                        family = Get-PropertyText -Object $priorPackage -Name 'family'
                        disposition = Get-PropertyText -Object $priorPackage -Name 'disposition'
                        rollbackGroup = Get-PropertyText -Object $priorPackage -Name 'rollbackGroup'
                        rationale = Get-PropertyText -Object $priorPackage -Name 'rationale'
                        evidence = Get-PropertyText -Object $priorPackage -Name 'evidence'
                        removalTrigger = Get-PropertyText -Object $priorPackage -Name 'removalTrigger'
                        sourceResults = @(Get-ArrayProperty -Object $priorPackage -Name 'sourceResults')
                        supersededBecause = $preservationReason
                    })
            }
        }

        if ($preserve -and $null -ne $priorPackage) {
            $package.disposition = Get-PropertyText -Object $priorPackage -Name 'disposition'
            $package.rollbackGroup = Get-PropertyText -Object $priorPackage -Name 'rollbackGroup'
            $package.rationale = Get-PropertyText -Object $priorPackage -Name 'rationale'
            $package.removalTrigger = Get-PropertyText -Object $priorPackage -Name 'removalTrigger'
        }
        else {
            $package.disposition = 'retained'
            $package.rollbackGroup = $family
        }
        $package | Add-Member -NotePropertyName historicalContext -NotePropertyValue @($packageHistory) -Force
    }

    $familyDecisions.Add([pscustomobject] @{
            family = $family
            disposition = $disposition
            rollbackGroup = $rollbackGroup
            packageIds = $packageIds
            rationale = $rationale
            compatibilityEvidence = $compatibilityEvidence
            removalTrigger = $removalTrigger
            representativeConsumers = $currentConsumers
            preservation = [ordered] @{
                status = $(if ($preserve) { $(if ($priorConsumerEvidenceTrusted) { 'preserved' } else { 'migrated' }) } else { 'refreshed' })
                reason = $preservationReason
                catalogPath = $catalogRelativePath
                catalogSha256 = $catalogSha256
                sourceScopeSha256 = $currentSourceFingerprint
                packageMetadataSha256 = $currentMetadataFingerprint
                consumerEvidenceSha256 = $currentConsumerFingerprint
            }
            historicalContext = @($historicalContext)
        })
}

$audit = [ordered] @{
    schemaVersion = 1
    auditedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    generatedFromRevision = $generatedFromRevision
    catalogPath = $catalogRelativePath
    catalogSha256 = $catalogSha256
    sources = @($sourceContracts | ForEach-Object {
            [ordered] @{
                uri = $_.Uri
                resolution = $_.Resolution
                diagnostic = $_.Diagnostic
            }
        })
    consumerEvidence = [ordered] @{
        schema = 'hexalith.package-consumer-evidence.v1'
        discovery = $consumerEvidence.discovery
        fixture = $consumerEvidence.fixture
        fixtureSha256 = $consumerEvidence.fixtureSha256
        fixtureMode = $consumerEvidence.fixtureMode
        repositoryRevision = $generatedFromRevision
        sha256 = $consumerEvidence.sha256
        entries = @($consumerEvidence.entries | ForEach-Object {
                [ordered] @{
                    family = $_.family
                    consumer = $_.consumer
                    packageId = $_.packageId
                    declarationPath = $_.declarationPath
                    declarationSha256 = $_.declarationSha256
                }
            })
    }
    familyDecisions = @($familyDecisions)
    packages = @($packages)
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$audit | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resolvedOutputPath -Encoding utf8
[Console]::Out.WriteLine(
    "Central package freshness audit wrote $($packages.Count) packages from $($sourceContracts.Count) configured source(s) to '$OutputPath'."
)
