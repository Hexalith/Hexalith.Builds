[CmdletBinding()]
param(
    [string] $AuditPath = '',
    [string] $CatalogPath = '',
    [Parameter(DontShow = $true)][string] $EvaluatorScriptPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath

function Stop-Validation {
    param([Parameter(Mandatory = $true)][string] $Message)

    [Console]::Error.WriteLine("Package version audit validation failed: $Message")
    exit 1
}

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name
    )

    if ($null -eq $Object -or $Object.PSObject.Properties.Name -notcontains $Name) {
        return $null
    }

    return $Object.$Name
}

function Get-RequiredText {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[string]] $Failures
    )

    $value = Get-PropertyValue -Object $Object -Name $Name
    $text = if ($null -eq $value) { '' } else { [string] $value }
    if ([string]::IsNullOrWhiteSpace($text)) {
        $Failures.Add("$Description has a blank or missing '$Name'.")
    }

    return $text.Trim()
}

function ConvertTo-NuGetVersion {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Version
    )

    if ([string]::IsNullOrWhiteSpace($Version) -or $Version -cne $Version.Trim()) {
        return $null
    }

    $metadataParts = $Version.Split('+')
    if ($metadataParts.Count -gt 2 -or [string]::IsNullOrEmpty($metadataParts[0])) {
        return $null
    }

    if ($metadataParts.Count -eq 2) {
        $metadataIdentifiers = $metadataParts[1].Split('.')
        if (
            [string]::IsNullOrEmpty($metadataParts[1]) -or
            @($metadataIdentifiers | Where-Object { $_ -cnotmatch '^[0-9A-Za-z-]+$' }).Count -gt 0
        ) {
            return $null
        }
    }

    $versionWithoutMetadata = $metadataParts[0]
    $prereleaseSeparator = $versionWithoutMetadata.IndexOf('-', [StringComparison]::Ordinal)
    $coreText = if ($prereleaseSeparator -ge 0) {
        $versionWithoutMetadata.Substring(0, $prereleaseSeparator)
    }
    else {
        $versionWithoutMetadata
    }
    $prereleaseText = if ($prereleaseSeparator -ge 0) {
        $versionWithoutMetadata.Substring($prereleaseSeparator + 1)
    }
    else {
        ''
    }

    $coreIdentifiers = $coreText.Split('.')
    if ($coreIdentifiers.Count -lt 1 -or $coreIdentifiers.Count -gt 4) {
        return $null
    }

    $core = [System.Collections.Generic.List[int]]::new()
    foreach ($identifier in $coreIdentifiers) {
        $component = 0
        if (
            $identifier -cnotmatch '^(0|[1-9][0-9]*)$' -or
            -not [int]::TryParse($identifier, [ref] $component)
        ) {
            return $null
        }
        $core.Add($component)
    }

    $prereleaseIdentifiers = @()
    if ($prereleaseSeparator -ge 0) {
        if ([string]::IsNullOrEmpty($prereleaseText)) {
            return $null
        }
        $prereleaseIdentifiers = @($prereleaseText.Split('.'))
        foreach ($identifier in $prereleaseIdentifiers) {
            if ($identifier -cnotmatch '^[0-9A-Za-z-]+$') {
                return $null
            }
            if ($identifier -cmatch '^[0-9]+$' -and $identifier.Length -gt 1 -and $identifier.StartsWith('0')) {
                return $null
            }
        }
    }

    return [pscustomobject] @{
        Core = @($core)
        Prerelease = @($prereleaseIdentifiers)
        IsPrerelease = $prereleaseSeparator -ge 0
    }
}

function Compare-NuGetVersion {
    param(
        [Parameter(Mandatory = $true)][string] $Left,
        [Parameter(Mandatory = $true)][string] $Right
    )

    $leftVersion = ConvertTo-NuGetVersion -Version $Left
    $rightVersion = ConvertTo-NuGetVersion -Version $Right
    if ($null -eq $leftVersion -or $null -eq $rightVersion) {
        return $null
    }

    for ($index = 0; $index -lt 4; $index++) {
        $leftNumber = if ($index -lt $leftVersion.Core.Count) { $leftVersion.Core[$index] } else { 0 }
        $rightNumber = if ($index -lt $rightVersion.Core.Count) { $rightVersion.Core[$index] } else { 0 }
        if ($leftNumber -ne $rightNumber) {
            return $leftNumber.CompareTo($rightNumber)
        }
    }

    if (-not $leftVersion.IsPrerelease) {
        return $(if (-not $rightVersion.IsPrerelease) { 0 } else { 1 })
    }

    if (-not $rightVersion.IsPrerelease) {
        return -1
    }

    $leftIdentifiers = $leftVersion.Prerelease
    $rightIdentifiers = $rightVersion.Prerelease
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
    foreach ($version in @($Versions | Where-Object { -not [string]::IsNullOrWhiteSpace([string] $_) })) {
        if ($null -eq $latest -or (Compare-NuGetVersion -Left $version -Right $latest) -gt 0) {
            $latest = $version
        }
    }

    return $latest
}

function Invoke-CatalogEvaluation {
    param(
        [Parameter(Mandatory = $true)][string] $ResolvedCatalogPath,
        [AllowEmptyString()][string] $ResolvedEvaluatorScriptPath
    )

    $output = if ([string]::IsNullOrWhiteSpace($ResolvedEvaluatorScriptPath)) {
        @(& dotnet msbuild $ResolvedCatalogPath -nologo -getItem:PackageVersion 2>&1)
    }
    else {
        @(& $pwshExecutable -NoLogo -NoProfile -File $ResolvedEvaluatorScriptPath $ResolvedCatalogPath 2>&1)
    }

    if ($LASTEXITCODE -ne 0) {
        Stop-Validation "catalog evaluation exited with code $LASTEXITCODE. $([string]::Join("`n", $output))"
    }

    try {
        return ([string]::Join("`n", $output) | ConvertFrom-Json -ErrorAction Stop)
    }
    catch {
        Stop-Validation "catalog evaluation returned malformed JSON. $($_.Exception.GetBaseException().Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($AuditPath)) {
    $AuditPath = Join-Path $PSScriptRoot 'package-version-audit.json'
}

if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $CatalogPath = Join-Path $PSScriptRoot '../Props/Directory.Packages.props'
}

try {
    $resolvedAuditPath = (Resolve-Path -LiteralPath $AuditPath -ErrorAction Stop).ProviderPath
    $resolvedCatalogPath = (Resolve-Path -LiteralPath $CatalogPath -ErrorAction Stop).ProviderPath
    $resolvedEvaluatorScriptPath = if ([string]::IsNullOrWhiteSpace($EvaluatorScriptPath)) {
        ''
    }
    else {
        (Resolve-Path -LiteralPath $EvaluatorScriptPath -ErrorAction Stop).ProviderPath
    }
    $audit = Get-Content -LiteralPath $resolvedAuditPath -Raw | ConvertFrom-Json -DateKind String -ErrorAction Stop
}
catch {
    Stop-Validation "audit, catalog, or evaluator input could not be loaded. $($_.Exception.GetBaseException().Message)"
}

$evaluation = Invoke-CatalogEvaluation `
    -ResolvedCatalogPath $resolvedCatalogPath `
    -ResolvedEvaluatorScriptPath $resolvedEvaluatorScriptPath
$failures = [System.Collections.Generic.List[string]]::new()

if ((Get-PropertyValue -Object $audit -Name 'schemaVersion') -ne 1) {
    $failures.Add('schemaVersion must equal 1.')
}

$auditedAtValue = Get-PropertyValue -Object $audit -Name 'auditedAtUtc'
$auditedAtUtc = if ($null -eq $auditedAtValue) { '' } else { [string] $auditedAtValue }
$parsedTimestamp = [DateTimeOffset]::MinValue
$timestampParsed = [DateTimeOffset]::TryParse($auditedAtUtc, [ref] $parsedTimestamp)
if ([string]::IsNullOrWhiteSpace($auditedAtUtc) -or -not $timestampParsed) {
    $failures.Add('auditedAtUtc must be a valid UTC timestamp.')
}
elseif ($parsedTimestamp.Offset -ne [TimeSpan]::Zero) {
    $failures.Add('auditedAtUtc must have a zero UTC offset.')
}

$generatedFromRevision = Get-RequiredText `
    -Object $audit -Name 'generatedFromRevision' -Description 'Audit' -Failures $failures
if ($generatedFromRevision -cnotmatch '^[0-9a-f]{40}$') {
    $failures.Add('generatedFromRevision must be a full lowercase 40-character Git revision.')
}

$catalogPathValue = Get-RequiredText -Object $audit -Name 'catalogPath' -Description 'Audit' -Failures $failures
$expectedCatalogPath = [IO.Path]::GetRelativePath($repositoryRoot, $resolvedCatalogPath).Replace('\', '/')
if ($catalogPathValue -cne $expectedCatalogPath) {
    $failures.Add("Audit catalogPath '$catalogPathValue' does not match evaluated catalog '$expectedCatalogPath'.")
}

$sources = @((Get-PropertyValue -Object $audit -Name 'sources'))
if ($sources.Count -eq 0 -or $null -eq $sources[0]) {
    $failures.Add('Audit must declare at least one configured source.')
    $sources = @()
}

$sourceUris = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$sourceResolutions = @{}
foreach ($source in $sources) {
    $uri = Get-RequiredText -Object $source -Name 'uri' -Description 'Source' -Failures $failures
    $resolution = Get-RequiredText -Object $source -Name 'resolution' -Description "Source '$uri'" -Failures $failures
    $null = Get-RequiredText -Object $source -Name 'diagnostic' -Description "Source '$uri'" -Failures $failures
    if (-not $sourceUris.Add($uri)) {
        $failures.Add("Configured source '$uri' is duplicated.")
    }

    if ($resolution -notin @('resolved', 'unresolved')) {
        $failures.Add("Source '$uri' has invalid resolution '$resolution'.")
    }
    else {
        $sourceResolutions[$uri] = $resolution
    }
}

$catalogVersions = @{}
foreach ($item in @($evaluation.Items.PackageVersion)) {
    $identity = [string] $item.Identity
    if ($catalogVersions.ContainsKey($identity)) {
        $failures.Add("Evaluated catalog contains duplicate package '$identity'.")
    }
    else {
        $catalogVersions[$identity] = [string] $item.Version
    }
}

if ($catalogVersions.Count -eq 0) {
    $failures.Add('Evaluated catalog must contain at least one package row.')
}

$packages = @((Get-PropertyValue -Object $audit -Name 'packages'))
$auditPackages = @{}
foreach ($package in $packages) {
    $id = Get-RequiredText -Object $package -Name 'id' -Description 'Package evidence' -Failures $failures
    if ($auditPackages.ContainsKey($id)) {
        $failures.Add("Package evidence contains duplicate package '$id'.")
        continue
    }

    $auditPackages[$id] = $package
    $auditedVersion = Get-RequiredText -Object $package -Name 'auditedVersion' -Description "Package '$id'" -Failures $failures
    $selectedVersion = Get-RequiredText -Object $package -Name 'selectedVersion' -Description "Package '$id'" -Failures $failures
    $listingState = Get-RequiredText -Object $package -Name 'listingState' -Description "Package '$id'" -Failures $failures
    $family = Get-RequiredText -Object $package -Name 'family' -Description "Package '$id'" -Failures $failures
    $disposition = Get-RequiredText -Object $package -Name 'disposition' -Description "Package '$id'" -Failures $failures
    $rollbackGroup = Get-RequiredText -Object $package -Name 'rollbackGroup' -Description "Package '$id'" -Failures $failures
    $null = Get-RequiredText -Object $package -Name 'rationale' -Description "Package '$id'" -Failures $failures
    $null = Get-RequiredText -Object $package -Name 'evidence' -Description "Package '$id'" -Failures $failures
    $removalTrigger = Get-RequiredText -Object $package -Name 'removalTrigger' -Description "Package '$id'" -Failures $failures
    $latestStableValue = Get-PropertyValue -Object $package -Name 'latestStable'
    $latestPrereleaseValue = Get-PropertyValue -Object $package -Name 'latestPrerelease'
    if ($package.PSObject.Properties.Name -notcontains 'latestStable') {
        $failures.Add("Package '$id' is missing 'latestStable'.")
    }
    if ($package.PSObject.Properties.Name -notcontains 'latestPrerelease') {
        $failures.Add("Package '$id' is missing 'latestPrerelease'.")
    }
    $latestStable = if ($null -eq $latestStableValue) { '' } else { [string] $latestStableValue }
    $latestPrerelease = if ($null -eq $latestPrereleaseValue) { '' } else { [string] $latestPrereleaseValue }
    $isInternalPackage = $id.StartsWith('Hexalith.', [StringComparison]::OrdinalIgnoreCase)
    $catalogSelectedVersion = if ($catalogVersions.ContainsKey($id)) { [string] $catalogVersions[$id] } else { '' }
    $parsedInternalVersions = @{}

    if ($isInternalPackage) {
        foreach ($versionEntry in @(
                @{ Name = 'auditedVersion'; Value = $auditedVersion },
                @{ Name = 'selectedVersion'; Value = $selectedVersion },
                @{ Name = 'catalog version'; Value = $catalogSelectedVersion },
                @{ Name = 'latestStable'; Value = $latestStable },
                @{ Name = 'latestPrerelease'; Value = $latestPrerelease }
            )) {
            if ([string]::IsNullOrWhiteSpace($versionEntry.Value)) {
                continue
            }

            $parsedVersion = ConvertTo-NuGetVersion -Version $versionEntry.Value
            if ($null -eq $parsedVersion) {
                $failures.Add(
                    "Internal package '$id' has invalid $($versionEntry.Name) NuGet version '$($versionEntry.Value)'."
                )
            }
            else {
                $parsedInternalVersions[$versionEntry.Name] = $parsedVersion
            }
        }
    }

    if ($listingState -notin @('listed', 'unlisted', 'missing', 'unresolved')) {
        $failures.Add("Package '$id' has invalid listingState '$listingState'.")
    }

    if ($disposition -notin @('accepted', 'retained')) {
        $failures.Add("Package '$id' has invalid disposition '$disposition'.")
    }

    if (-not $catalogVersions.ContainsKey($id)) {
        $failures.Add("Package evidence '$id' is not present in the evaluated catalog.")
    }
    elseif (-not $isInternalPackage -and $catalogSelectedVersion -cne $selectedVersion) {
        $failures.Add("Package '$id' selects '$selectedVersion' but the evaluated catalog resolves '$($catalogVersions[$id])'.")
    }

    if (
        $isInternalPackage -and
        $parsedInternalVersions.ContainsKey('catalog version') -and
        $parsedInternalVersions.ContainsKey('selectedVersion')
    ) {
        $acceptedFloorComparison = Compare-NuGetVersion -Left $catalogSelectedVersion -Right $selectedVersion
        if ($acceptedFloorComparison -lt 0) {
            $failures.Add(
                "Internal package '$id' cannot downgrade accepted version floor '$selectedVersion' to catalog version '$catalogSelectedVersion'."
            )
        }
        elseif ($acceptedFloorComparison -gt 0) {
            $failures.Add(
                "Internal package '$id' catalog version '$catalogSelectedVersion' has no matching accepted audit selection."
            )
        }

        if (
            -not $parsedInternalVersions['selectedVersion'].IsPrerelease -and
            $parsedInternalVersions['catalog version'].IsPrerelease
        ) {
            $failures.Add(
                "Internal stable package '$id' cannot move accepted version '$selectedVersion' to prerelease catalog version '$catalogSelectedVersion'."
            )
        }
    }

    if ($listingState -ne 'listed' -and ($disposition -ne 'retained' -or $selectedVersion -cne $auditedVersion)) {
        $failures.Add("Package '$id' is $listingState and must retain audited version '$auditedVersion'.")
    }

    if ($disposition -eq 'retained' -and $selectedVersion -cne $auditedVersion) {
        $failures.Add("Retained package '$id' must select audited version '$auditedVersion'.")
    }

    if ($disposition -eq 'retained' -and [string]::IsNullOrWhiteSpace($removalTrigger)) {
        $failures.Add("Retained package '$id' must declare a removal trigger.")
    }

    if (
        $disposition -eq 'accepted' -and
        $selectedVersion -cne $latestStable -and
        $selectedVersion -cne $latestPrerelease
    ) {
        $failures.Add("Accepted package '$id' must select its audited latest stable or prerelease candidate.")
    }

    $selectedAuditComparison = Compare-NuGetVersion -Left $selectedVersion -Right $auditedVersion
    if ($disposition -eq 'accepted' -and $null -ne $selectedAuditComparison -and $selectedAuditComparison -lt 0) {
        $failures.Add("Accepted package '$id' cannot downgrade audited version '$auditedVersion' to '$selectedVersion'.")
    }

    $parsedAuditedVersion = ConvertTo-NuGetVersion -Version $auditedVersion
    $parsedSelectedVersion = ConvertTo-NuGetVersion -Version $selectedVersion
    if (
        $disposition -eq 'accepted' -and
        $null -ne $parsedAuditedVersion -and
        $null -ne $parsedSelectedVersion -and
        -not $parsedAuditedVersion.IsPrerelease -and
        $parsedSelectedVersion.IsPrerelease
    ) {
        $failures.Add("Accepted stable package '$id' cannot move to prerelease version '$selectedVersion'.")
    }

    $sourceResults = @((Get-PropertyValue -Object $package -Name 'sourceResults'))
    $packageSources = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sourceStates = [System.Collections.Generic.List[string]]::new()
    $sourceStableCandidates = [System.Collections.Generic.List[string]]::new()
    $sourcePrereleaseCandidates = [System.Collections.Generic.List[string]]::new()
    foreach ($sourceResult in $sourceResults) {
        $sourceUri = Get-RequiredText `
            -Object $sourceResult -Name 'source' -Description "Package '$id' source result" -Failures $failures
        $sourceState = Get-RequiredText `
            -Object $sourceResult -Name 'listingState' -Description "Package '$id' source '$sourceUri'" -Failures $failures
        $null = Get-RequiredText `
            -Object $sourceResult -Name 'diagnostic' -Description "Package '$id' source '$sourceUri'" -Failures $failures
        if ($sourceResult.PSObject.Properties.Name -notcontains 'latestStable') {
            $failures.Add("Package '$id' source '$sourceUri' is missing 'latestStable'.")
        }
        if ($sourceResult.PSObject.Properties.Name -notcontains 'latestPrerelease') {
            $failures.Add("Package '$id' source '$sourceUri' is missing 'latestPrerelease'.")
        }
        if (-not $packageSources.Add($sourceUri)) {
            $failures.Add("Package '$id' duplicates source result '$sourceUri'.")
        }

        if (-not $sourceUris.Contains($sourceUri)) {
            $failures.Add("Package '$id' references undeclared source '$sourceUri'.")
        }

        if ($sourceState -notin @('listed', 'unlisted', 'missing', 'unresolved')) {
            $failures.Add("Package '$id' source '$sourceUri' has invalid listingState '$sourceState'.")
        }
        else {
            $sourceStates.Add($sourceState)
        }

        if ($sourceResolutions.ContainsKey($sourceUri) -and $sourceResolutions[$sourceUri] -eq 'unresolved' -and $sourceState -ne 'unresolved') {
            $failures.Add("Package '$id' source '$sourceUri' must be unresolved because the configured source is unresolved.")
        }

        $sourceStable = Get-PropertyValue -Object $sourceResult -Name 'latestStable'
        $sourcePrerelease = Get-PropertyValue -Object $sourceResult -Name 'latestPrerelease'
        if ($isInternalPackage) {
            foreach ($sourceVersionEntry in @(
                    @{ Name = "source '$sourceUri' latestStable"; Value = [string] $sourceStable },
                    @{ Name = "source '$sourceUri' latestPrerelease"; Value = [string] $sourcePrerelease }
                )) {
                if (
                    -not [string]::IsNullOrWhiteSpace($sourceVersionEntry.Value) -and
                    $null -eq (ConvertTo-NuGetVersion -Version $sourceVersionEntry.Value)
                ) {
                    $failures.Add(
                        "Internal package '$id' has invalid $($sourceVersionEntry.Name) NuGet version '$($sourceVersionEntry.Value)'."
                    )
                }
            }
        }
        if (-not [string]::IsNullOrWhiteSpace([string] $sourceStable)) {
            $sourceStableCandidates.Add([string] $sourceStable)
        }
        if (-not [string]::IsNullOrWhiteSpace([string] $sourcePrerelease)) {
            $sourcePrereleaseCandidates.Add([string] $sourcePrerelease)
        }
    }

    if ($packageSources.Count -ne $sourceUris.Count) {
        $failures.Add("Package '$id' must contain exactly one result for every configured source.")
    }

    $expectedListingState = if ($sourceStates -contains 'listed') { 'listed' }
    elseif ($sourceStates -contains 'unlisted') { 'unlisted' }
    elseif ($sourceStates.Count -gt 0 -and @($sourceStates | Where-Object { $_ -ne 'unresolved' }).Count -eq 0) { 'unresolved' }
    else { 'missing' }
    if ($listingState -cne $expectedListingState) {
        $failures.Add("Package '$id' listingState '$listingState' does not match source aggregate '$expectedListingState'.")
    }

    $expectedLatestStable = Select-LatestVersion -Versions @($sourceStableCandidates)
    $expectedLatestPrerelease = Select-LatestVersion -Versions @($sourcePrereleaseCandidates)
    if ([string] $latestStable -cne [string] $expectedLatestStable) {
        $failures.Add("Package '$id' latestStable '$latestStable' does not match source aggregate '$expectedLatestStable'.")
    }
    if ([string] $latestPrerelease -cne [string] $expectedLatestPrerelease) {
        $failures.Add("Package '$id' latestPrerelease '$latestPrerelease' does not match source aggregate '$expectedLatestPrerelease'.")
    }

    if ($disposition -eq 'accepted' -and @($sourceStates | Where-Object { $_ -ne 'listed' }).Count -gt 0) {
        $failures.Add("Accepted package '$id' requires listed evidence from every configured source.")
    }

    $sourceCandidateVersions = @($sourceStableCandidates) + @($sourcePrereleaseCandidates)
    if ($disposition -eq 'accepted' -and @($sourceCandidateVersions | Where-Object { $_ -ieq $selectedVersion }).Count -eq 0) {
        $failures.Add("Accepted package '$id' selected version '$selectedVersion' has no configured-source candidate evidence.")
    }

    if ($id -eq 'Microsoft.OpenApi' -and $selectedVersion -notmatch '^2\.') {
        $failures.Add('Microsoft.OpenApi must remain on the proven 2.x line until compatibility evidence changes the contract.')
    }

    if (
        ($id -eq 'Hexalith.Tenants' -or $id -like 'Hexalith.Tenants.*') -and
        $catalogSelectedVersion -cne $selectedVersion
    ) {
        $failures.Add("Package '$id' changed without a separately validated Tenants release-owner contract.")
    }

    $package | Add-Member -NotePropertyName _validatedFamily -NotePropertyValue $family -Force
    $package | Add-Member -NotePropertyName _validatedDisposition -NotePropertyValue $disposition -Force
    $package | Add-Member -NotePropertyName _validatedRollbackGroup -NotePropertyValue $rollbackGroup -Force
}

foreach ($catalogEntry in $catalogVersions.GetEnumerator()) {
    if (-not $auditPackages.ContainsKey($catalogEntry.Key)) {
        $failures.Add("Evaluated catalog package '$($catalogEntry.Key)' has no audit evidence.")
    }
}

$familyDecisions = @((Get-PropertyValue -Object $audit -Name 'familyDecisions'))
$decisionsByFamily = @{}
foreach ($decision in $familyDecisions) {
    $family = Get-RequiredText -Object $decision -Name 'family' -Description 'Family decision' -Failures $failures
    if ($decisionsByFamily.ContainsKey($family)) {
        $failures.Add("Family decision '$family' is duplicated.")
        continue
    }

    $decisionsByFamily[$family] = $decision
    $disposition = Get-RequiredText -Object $decision -Name 'disposition' -Description "Family '$family'" -Failures $failures
    $rollbackGroup = Get-RequiredText -Object $decision -Name 'rollbackGroup' -Description "Family '$family'" -Failures $failures
    $null = Get-RequiredText -Object $decision -Name 'rationale' -Description "Family '$family'" -Failures $failures
    $null = Get-RequiredText -Object $decision -Name 'compatibilityEvidence' -Description "Family '$family'" -Failures $failures
    $removalTrigger = Get-RequiredText -Object $decision -Name 'removalTrigger' -Description "Family '$family'" -Failures $failures
    $representativeConsumers = @((Get-PropertyValue -Object $decision -Name 'representativeConsumers') | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string] $_)
        })
    if ($disposition -notin @('accepted', 'retained')) {
        $failures.Add("Family '$family' has invalid disposition '$disposition'.")
    }

    if ($disposition -eq 'accepted' -and $representativeConsumers.Count -eq 0) {
        $failures.Add("Accepted family '$family' must name at least one representative consumer.")
    }

    if ($disposition -eq 'retained' -and [string]::IsNullOrWhiteSpace($removalTrigger)) {
        $failures.Add("Retained family '$family' must declare a removal trigger.")
    }

    $declaredIds = @((Get-PropertyValue -Object $decision -Name 'packageIds') | ForEach-Object { [string] $_ })
    $actualIds = @($packages | Where-Object { $_._validatedFamily -ceq $family } | ForEach-Object { [string] $_.id })
    if ($actualIds.Count -eq 0) {
        $failures.Add("Family '$family' has no package evidence rows.")
    }
    $declaredSignature = [string]::Join('|', @($declaredIds | Sort-Object))
    $actualSignature = [string]::Join('|', @($actualIds | Sort-Object))
    if ($declaredSignature -cne $actualSignature) {
        $failures.Add("Family '$family' packageIds do not exactly match its package evidence rows.")
    }

    foreach ($familyPackage in @($packages | Where-Object { $_._validatedFamily -ceq $family })) {
        if ($familyPackage._validatedDisposition -cne $disposition) {
            $failures.Add("Package '$($familyPackage.id)' disposition does not match family '$family'.")
        }

        if ($familyPackage._validatedRollbackGroup -cne $rollbackGroup) {
            $failures.Add("Package '$($familyPackage.id)' rollback group does not match family '$family'.")
        }
    }

    $internalFamilyPackages = @($packages | Where-Object {
            $_._validatedFamily -ceq $family -and
            ([string] $_.id).StartsWith('Hexalith.', [StringComparison]::OrdinalIgnoreCase)
        })
    if ($internalFamilyPackages.Count -gt 0) {
        $selectedFamilyVersions = @(
            $internalFamilyPackages |
                ForEach-Object { [string] $catalogVersions[[string] $_.id] } |
                Sort-Object -Unique
        )
        if ($selectedFamilyVersions.Count -ne 1) {
            $coordinates = @(
                $internalFamilyPackages |
                    ForEach-Object { "$($_.id)='$($catalogVersions[[string] $_.id])'" }
            )
            $failures.Add(
                "Internal package family '$family' must select one aligned catalog version; found $([string]::Join(', ', $coordinates))."
            )
        }
    }
}

foreach ($rollbackGroup in @($familyDecisions | Group-Object rollbackGroup)) {
    $groupDispositions = @($rollbackGroup.Group | ForEach-Object { [string] $_.disposition } | Sort-Object -Unique)
    if ($groupDispositions.Count -gt 1) {
        $failures.Add("Rollback group '$($rollbackGroup.Name)' contains inconsistent family dispositions.")
    }
}

foreach ($package in $packages) {
    if (-not $decisionsByFamily.ContainsKey($package._validatedFamily)) {
        $failures.Add("Package '$($package.id)' has no family decision for '$($package._validatedFamily)'.")
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Package version audit validation failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine(
    "Package version audit validation passed for $($packages.Count) packages, $($familyDecisions.Count) families, and $($sources.Count) source(s)."
)
