[CmdletBinding()]
param(
    [string] $CatalogPath = '',
    [string] $OutputPath = '',
    [string[]] $Source = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Invoke-JsonRequest {
    param([Parameter(Mandatory = $true)][string] $Uri)

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

        $leftNumeric = 0L
        $rightNumeric = 0L
        $leftIsNumeric = [long]::TryParse($leftIdentifiers[$index], [ref] $leftNumeric)
        $rightIsNumeric = [long]::TryParse($rightIdentifiers[$index], [ref] $rightNumeric)
        if ($leftIsNumeric -and $rightIsNumeric -and $leftNumeric -ne $rightNumeric) {
            return [Math]::Sign($leftNumeric - $rightNumeric)
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
    param(
        [Parameter(Mandatory = $true)][string] $Id,
        [AllowEmptyString()][string] $SourceVersion
    )

    $propertyMatch = [regex]::Match($SourceVersion, '^\$\(Hexalith([A-Za-z0-9]+)Version\)$')
    if ($propertyMatch.Success) {
        return "hexalith-$($propertyMatch.Groups[1].Value.ToLowerInvariant())"
    }

    if ($Id -match '^Hexalith\.([^.]+)') { return "hexalith-$($Matches[1].ToLowerInvariant())" }
    if ($Id -match '^Aspire\.') { return 'aspire' }
    if ($Id -match '^Dapr\.') { return 'dapr' }
    if ($Id -match '^Microsoft\.CodeAnalysis') { return 'roslyn' }
    if ($Id -match '^(Microsoft\.IdentityModel\.|System\.IdentityModel\.Tokens\.Jwt$)') { return 'identity-model' }
    if ($Id -match '^OpenTelemetry') { return 'opentelemetry' }
    if ($Id -match '^Microsoft\.FluentUI\.') { return 'fluent-ui' }
    if ($Id -match '^xunit\.') { return 'xunit' }
    if ($Id -match '^Verify(?:\.|$)') { return 'verify' }
    if ($Id -match '^(Microsoft\.AspNetCore\.|Microsoft\.Extensions\.|System\.Text\.Json$|System\.Collections\.Immutable$)') {
        return 'dotnet-10'
    }

    return "package:$($Id.ToLowerInvariant())"
}

function Get-ConfiguredSources {
    if ($Source.Count -gt 0) {
        return @($Source)
    }

    $output = @(& dotnet nuget list source --format short 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Stop-Audit "configured NuGet sources could not be listed. $([string]::Join("`n", $output))"
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
    [xml] $catalogXml = Get-Content -LiteralPath $resolvedCatalogPath -Raw -ErrorAction Stop
}
catch {
    Stop-Audit "catalog could not be loaded from '$CatalogPath'. $($_.Exception.GetBaseException().Message)"
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

$sourceVersions = @{}
foreach ($node in @($catalogXml.SelectNodes("//*[local-name()='PackageVersion']"))) {
    $sourceVersions[[string] $node.GetAttribute('Include')] = [string] $node.GetAttribute('Version')
}

$configuredSources = @(Get-ConfiguredSources)
if ($configuredSources.Count -eq 0) {
    Stop-Audit 'no enabled NuGet source was discovered.'
}

$sourceContracts = [System.Collections.Generic.List[object]]::new()
foreach ($sourceUri in $configuredSources) {
    try {
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

        $sourceContracts.Add([pscustomobject] @{
                Uri = $sourceUri
                Registration = ([string] $registrationResource[0].'@id').TrimEnd('/')
                FlatContainer = ([string] $flatResource[0].'@id').TrimEnd('/')
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
    $family = Get-PackageFamily -Id $id -SourceVersion ([string] $sourceVersions[$id])

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

$familyDecisions = [System.Collections.Generic.List[object]]::new()
foreach ($group in @($packages | Group-Object family | Sort-Object Name)) {
    $familyDecisions.Add([pscustomobject] @{
            family = $group.Name
            disposition = 'retained'
            rollbackGroup = $group.Name
            packageIds = @($group.Group | ForEach-Object { $_.id } | Sort-Object)
            rationale = 'Retain the audited family until candidate compatibility is accepted as one rollback-safe group.'
            compatibilityEvidence = 'Live NuGet metadata is recorded per package; no candidate is accepted without repository validation.'
            removalTrigger = 'Re-run the audit and validate every affected representative consumer before accepting the family.'
            representativeConsumers = @('Hexalith.EventStore')
        })
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$revisionOutput = @(& git -C $repositoryRoot rev-parse HEAD 2>&1)
$generatedFromRevision = if ($LASTEXITCODE -eq 0) { ([string] $revisionOutput[0]).Trim() } else { 'NO_VCS' }
$audit = [ordered] @{
    schemaVersion = 1
    auditedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    generatedFromRevision = $generatedFromRevision
    catalogPath = 'Props/Directory.Packages.props'
    sources = @($sourceContracts | ForEach-Object {
            [ordered] @{
                uri = $_.Uri
                resolution = $_.Resolution
                diagnostic = $_.Diagnostic
            }
        })
    familyDecisions = @($familyDecisions)
    packages = @($packages)
}

$outputDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$audit | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
[Console]::Out.WriteLine(
    "Central package freshness audit wrote $($packages.Count) packages from $($sourceContracts.Count) configured source(s) to '$OutputPath'."
)
