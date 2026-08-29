[CmdletBinding()]
param(
    [string] $AuditPath = '',
    [string] $CatalogPath = '',
    [Parameter(DontShow = $true)][string] $EvaluatorScriptPath = '',
    # Root scanned for tracked *.csproj/*.props/*.targets files during independent
    # PackageReference rediscovery (see Get-TrackedProjectFiles). Defaults to the
    # repository root. Overridable so tests can point rediscovery at an isolated
    # fixture tree without touching the real repository.
    [string] $ConsumerScanRoot = ''
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

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-Sha256File {
    param([Parameter(Mandatory = $true)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
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

function Get-IdentitySignature {
    param([AllowEmptyCollection()][object[]] $Values)

    return [string]::Join('|', @($Values | ForEach-Object { [string] $_ } | Sort-Object -CaseSensitive))
}

function Get-SourceScopeFingerprint {
    param([AllowEmptyCollection()][object[]] $Sources)

    $material = @($Sources | Where-Object { $null -ne $_ } | ForEach-Object {
            "$(Get-PropertyValue -Object $_ -Name 'uri')|" +
            "$(Get-PropertyValue -Object $_ -Name 'resolution')|" +
            "$(Get-PropertyValue -Object $_ -Name 'diagnostic')"
        } | Sort-Object -CaseSensitive)
    return Get-Sha256Text -Value ([string]::Join("`n", $material))
}

function Get-ConsumerRelationFingerprint {
    param([AllowEmptyCollection()][object[]] $Entries)

    $material = @($Entries | Where-Object { $null -ne $_ } | ForEach-Object {
            "$(Get-PropertyValue -Object $_ -Name 'family')|" +
            "$(Get-PropertyValue -Object $_ -Name 'consumer')|" +
            "$(Get-PropertyValue -Object $_ -Name 'packageId')|" +
            "$(Get-PropertyValue -Object $_ -Name 'declarationPath')|" +
            "$(Get-PropertyValue -Object $_ -Name 'declarationSha256')"
        } | Sort-Object -CaseSensitive)
    return Get-Sha256Text -Value ([string]::Join("`n", $material))
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

function Get-TrackedProjectFiles {
    # Independently rediscovers every *.csproj/*.props/*.targets file the repository
    # actually tracks, using Git as the source of truth (falling back to a filesystem
    # walk only when Git is unavailable). This must not reuse the audit's own notion
    # of "the catalog" or any generator/validator caching: it is the independent
    # rediscovery leg of the audit-vs-rediscovery set-equality proof.
    param([Parameter(Mandatory = $true)][string] $Root)

    if ($null -ne (Get-Command git -ErrorAction SilentlyContinue)) {
        $tracked = @(& git -C $Root ls-files -- '*.csproj' '*.props' '*.targets' 2>$null)
        if ($LASTEXITCODE -eq 0) {
            return @(
                $tracked |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                    ForEach-Object { Join-Path $Root $_ }
            )
        }
    }

    return @(
        Get-ChildItem -LiteralPath $Root -Recurse -File |
            Where-Object {
                $_.Extension -in @('.csproj', '.props', '.targets') -and
                $_.FullName -notmatch '[\\/](?:bin|obj|\.git)[\\/]'
            } |
            Select-Object -ExpandProperty FullName
    )
}

function Get-PackageReferenceConsumers {
    # Rediscovers the distinct set of package identities every tracked project file
    # actually consumes through <PackageReference>/<GlobalPackageReference Include="...">,
    # independently of MSBuild evaluation and independently of the audit itself. Returns
    # an ordered map of identity -> repository-relative consumer file paths, so a missing
    # audit row can be reported against every file that references it.
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]] $ProjectFiles,
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][string] $ExcludePath
    )

    $consumers = [ordered] @{}
    foreach ($projectFile in $ProjectFiles) {
        $resolvedProjectFile = [IO.Path]::GetFullPath($projectFile)
        if ([StringComparer]::OrdinalIgnoreCase.Equals($resolvedProjectFile, $ExcludePath)) {
            # The central catalog declares PackageVersion rows, not PackageReference
            # consumption; excluding it keeps rediscovery scoped to actual consumers.
            continue
        }

        if (-not (Test-Path -LiteralPath $resolvedProjectFile -PathType Leaf)) {
            continue
        }

        try {
            [xml] $xml = Get-Content -LiteralPath $resolvedProjectFile -Raw -ErrorAction Stop
        }
        catch {
            continue
        }

        $relativePath = [IO.Path]::GetRelativePath($RepositoryRoot, $resolvedProjectFile).Replace('\', '/')
        foreach ($itemName in @('PackageReference', 'GlobalPackageReference')) {
            foreach ($item in @($xml.SelectNodes("//*[local-name()='$itemName']"))) {
                $include = [string] $item.GetAttribute('Include')
                if ([string]::IsNullOrWhiteSpace($include)) {
                    continue
                }

                if (-not $consumers.Contains($include)) {
                    $consumers[$include] = [System.Collections.Generic.List[string]]::new()
                }

                if (-not $consumers[$include].Contains($relativePath)) {
                    $consumers[$include].Add($relativePath)
                }
            }
        }
    }

    return $consumers
}

function ConvertTo-OrderedSourceResultRecord {
    # Projects one per-source package result row (used both at package top level
    # and, identically shaped, inside a package's historicalContext entries).
    param([Parameter(Mandatory = $true)] $SourceResult)

    return [ordered] @{
        source = [string] $SourceResult.source
        listingState = [string] $SourceResult.listingState
        latestStable = if ($null -eq $SourceResult.latestStable) { $null } else { [string] $SourceResult.latestStable }
        latestPrerelease = if ($null -eq $SourceResult.latestPrerelease) { $null } else { [string] $SourceResult.latestPrerelease }
        diagnostic = [string] $SourceResult.diagnostic
    }
}

function ConvertTo-OrderedPreservationRecord {
    # Projects a preservation-provenance object. Used both at family top level and,
    # identically shaped but with some fields legitimately absent on older
    # generations, inside a family's historicalContext entries -- so every field is
    # read defensively via Get-PropertyValue rather than direct property access.
    param($Preservation)

    if ($null -eq $Preservation) {
        return $null
    }

    $record = [ordered] @{}
    foreach ($field in @(
            'status', 'reason', 'catalogPath', 'catalogSha256', 'sourceScopeSha256',
            'packageMetadataSha256', 'consumerEvidenceSha256'
        )) {
        # Historical legacy-unbound records legitimately predate the hash fields.
        # Preserve property absence exactly rather than silently manufacturing empty
        # strings during the supposedly lossless typed round trip.
        if ($Preservation.PSObject.Properties.Name -contains $field) {
            $record[$field] = [string] (Get-PropertyValue -Object $Preservation -Name $field)
        }
    }

    return $record
}

function ConvertTo-OrderedHistoricalPackageRecord {
    # Projects one hexalith.package-audit-package-history.v1 entry retained on a
    # package row: a frozen snapshot of a prior audit's package-shaped fields plus
    # its own schema/label/provenance envelope and collection-valued sourceResults.
    param([Parameter(Mandatory = $true)] $Entry)

    return [ordered] @{
        schema = [string] (Get-PropertyValue -Object $Entry -Name 'schema')
        label = [string] (Get-PropertyValue -Object $Entry -Name 'label')
        auditedAtUtc = [string] (Get-PropertyValue -Object $Entry -Name 'auditedAtUtc')
        generatedFromRevision = [string] (Get-PropertyValue -Object $Entry -Name 'generatedFromRevision')
        id = [string] (Get-PropertyValue -Object $Entry -Name 'id')
        auditedVersion = [string] (Get-PropertyValue -Object $Entry -Name 'auditedVersion')
        selectedVersion = [string] (Get-PropertyValue -Object $Entry -Name 'selectedVersion')
        latestStable = $(if ($null -eq (Get-PropertyValue -Object $Entry -Name 'latestStable')) { $null } else { [string] (Get-PropertyValue -Object $Entry -Name 'latestStable') })
        latestPrerelease = $(if ($null -eq (Get-PropertyValue -Object $Entry -Name 'latestPrerelease')) { $null } else { [string] (Get-PropertyValue -Object $Entry -Name 'latestPrerelease') })
        listingState = [string] (Get-PropertyValue -Object $Entry -Name 'listingState')
        family = [string] (Get-PropertyValue -Object $Entry -Name 'family')
        disposition = [string] (Get-PropertyValue -Object $Entry -Name 'disposition')
        rollbackGroup = [string] (Get-PropertyValue -Object $Entry -Name 'rollbackGroup')
        rationale = [string] (Get-PropertyValue -Object $Entry -Name 'rationale')
        evidence = [string] (Get-PropertyValue -Object $Entry -Name 'evidence')
        removalTrigger = [string] (Get-PropertyValue -Object $Entry -Name 'removalTrigger')
        sourceResults = @(
            foreach ($sourceResult in @((Get-PropertyValue -Object $Entry -Name 'sourceResults') | Where-Object { $null -ne $_ })) {
                ConvertTo-OrderedSourceResultRecord -SourceResult $sourceResult
            }
        )
        supersededBecause = [string] (Get-PropertyValue -Object $Entry -Name 'supersededBecause')
    }
}

function ConvertTo-OrderedHistoricalFamilyRecord {
    # Projects one hexalith.package-audit-family-history.v1 entry retained on a
    # family decision: a frozen snapshot of a prior audit's family-shaped fields
    # plus its own schema/label/provenance envelope, nested preservation, and
    # collection-valued packageIds/representativeConsumers.
    param([Parameter(Mandatory = $true)] $Entry)

    return [ordered] @{
        schema = [string] (Get-PropertyValue -Object $Entry -Name 'schema')
        label = [string] (Get-PropertyValue -Object $Entry -Name 'label')
        auditedAtUtc = [string] (Get-PropertyValue -Object $Entry -Name 'auditedAtUtc')
        generatedFromRevision = [string] (Get-PropertyValue -Object $Entry -Name 'generatedFromRevision')
        family = [string] (Get-PropertyValue -Object $Entry -Name 'family')
        disposition = [string] (Get-PropertyValue -Object $Entry -Name 'disposition')
        rollbackGroup = [string] (Get-PropertyValue -Object $Entry -Name 'rollbackGroup')
        packageIds = @(@(Get-PropertyValue -Object $Entry -Name 'packageIds') | ForEach-Object { [string] $_ })
        rationale = [string] (Get-PropertyValue -Object $Entry -Name 'rationale')
        compatibilityEvidence = [string] (Get-PropertyValue -Object $Entry -Name 'compatibilityEvidence')
        removalTrigger = [string] (Get-PropertyValue -Object $Entry -Name 'removalTrigger')
        representativeConsumers = @(@(Get-PropertyValue -Object $Entry -Name 'representativeConsumers') | ForEach-Object { [string] $_ })
        preservation = ConvertTo-OrderedPreservationRecord -Preservation (Get-PropertyValue -Object $Entry -Name 'preservation')
        supersededBecause = [string] (Get-PropertyValue -Object $Entry -Name 'supersededBecause')
    }
}

function ConvertTo-OrderedPackageRecord {
    # Projects a parsed audit package row onto the complete typed field set the
    # validator understands. Used both to prove a full read/write round trip and to
    # detect owner-authored fields the typed model does not yet cover (see
    # Get-UnknownFieldNames), so drift cannot hide behind a structural/shape check.
    param([Parameter(Mandatory = $true)] $Package)

    return [ordered] @{
        id = [string] $Package.id
        auditedVersion = [string] $Package.auditedVersion
        selectedVersion = [string] $Package.selectedVersion
        latestStable = if ($null -eq $Package.latestStable) { $null } else { [string] $Package.latestStable }
        latestPrerelease = if ($null -eq $Package.latestPrerelease) { $null } else { [string] $Package.latestPrerelease }
        listingState = [string] $Package.listingState
        family = [string] $Package.family
        disposition = [string] $Package.disposition
        rollbackGroup = [string] $Package.rollbackGroup
        rationale = [string] $Package.rationale
        evidence = [string] $Package.evidence
        removalTrigger = [string] $Package.removalTrigger
        sourceResults = @(
            foreach ($sourceResult in @(Get-PropertyValue -Object $Package -Name 'sourceResults')) {
                ConvertTo-OrderedSourceResultRecord -SourceResult $sourceResult
            }
        )
        historicalContext = @(
            foreach ($historicalEntry in @((Get-PropertyValue -Object $Package -Name 'historicalContext') | Where-Object { $null -ne $_ })) {
                ConvertTo-OrderedHistoricalPackageRecord -Entry $historicalEntry
            }
        )
    }
}

function ConvertTo-OrderedFamilyRecord {
    # Projects a parsed audit family decision onto the complete typed field set,
    # mirroring ConvertTo-OrderedPackageRecord for the family-level owner fields,
    # including its collection-valued provenance fields.
    param([Parameter(Mandatory = $true)] $Decision)

    return [ordered] @{
        family = [string] $Decision.family
        disposition = [string] $Decision.disposition
        rollbackGroup = [string] $Decision.rollbackGroup
        packageIds = @(@(Get-PropertyValue -Object $Decision -Name 'packageIds') | ForEach-Object { [string] $_ })
        rationale = [string] $Decision.rationale
        compatibilityEvidence = [string] $Decision.compatibilityEvidence
        removalTrigger = [string] $Decision.removalTrigger
        representativeConsumers = @(@(Get-PropertyValue -Object $Decision -Name 'representativeConsumers') | ForEach-Object { [string] $_ })
        preservation = ConvertTo-OrderedPreservationRecord -Preservation (Get-PropertyValue -Object $Decision -Name 'preservation')
        historicalContext = @(
            foreach ($historicalEntry in @((Get-PropertyValue -Object $Decision -Name 'historicalContext') | Where-Object { $null -ne $_ })) {
                ConvertTo-OrderedHistoricalFamilyRecord -Entry $historicalEntry
            }
        )
    }
}

function Get-UnknownFieldNames {
    # Reports any JSON property present on $Object that the typed model in
    # $KnownFields does not project. An owner field the schema does not yet know
    # about would otherwise be silently dropped by a "round trip" that only
    # re-serializes the fields it already recognizes; this makes that omission
    # loud instead of silent.
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string[]] $KnownFields
    )

    return @(
        $Object.PSObject.Properties.Name |
            Where-Object { $_ -notin $KnownFields -and -not $_.StartsWith('_validated', [System.StringComparison]::Ordinal) }
    )
}

function Assert-JsonStringFields {
    # PowerShell casts numbers and booleans to strings very readily. A typed audit
    # round trip must reject that coercion rather than serialize a different JSON
    # type on the next write.
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string[]] $Fields,
        [AllowEmptyCollection()][string[]] $NullableFields = @(),
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[string]] $Failures
    )

    foreach ($field in $Fields) {
        $property = $Object.PSObject.Properties[$field]
        if ($null -eq $property) {
            $Failures.Add("$Description is missing typed string field '$field'.")
            continue
        }

        if ($null -eq $property.Value -and $field -in $NullableFields) {
            continue
        }
        if ($property.Value -isnot [string]) {
            $Failures.Add("$Description field '$field' must be a JSON string$(if ($field -in $NullableFields) { ' or null' } else { '' }); actual type is '$($property.Value?.GetType().Name)'.")
        }
    }
}

function Assert-JsonStringArrayField {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Field,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[string]] $Failures
    )

    $property = $Object.PSObject.Properties[$Field]
    if ($null -eq $property -or $property.Value -isnot [array]) {
        $Failures.Add("$Description field '$Field' must be a JSON array.")
        return
    }

    for ($index = 0; $index -lt $property.Value.Count; $index++) {
        if ($property.Value[$index] -isnot [string]) {
            $Failures.Add("$Description field '$Field' element $index must be a JSON string.")
        }
    }
}

function Assert-JsonObjectArrayField {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string] $Field,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[string]] $Failures
    )

    $property = $Object.PSObject.Properties[$Field]
    if ($null -eq $property -or $property.Value -isnot [array]) {
        $Failures.Add("$Description field '$Field' must be a JSON array.")
        return
    }

    for ($index = 0; $index -lt $property.Value.Count; $index++) {
        $item = $property.Value[$index]
        if ($null -eq $item -or $item -is [string] -or $item.GetType().IsPrimitive) {
            $Failures.Add("$Description field '$Field' element $index must be a JSON object.")
        }
    }
}

function Add-UnknownFieldFailures {
    param(
        [Parameter(Mandatory = $true)] $Object,
        [Parameter(Mandatory = $true)][string[]] $KnownFields,
        [Parameter(Mandatory = $true)][string] $Description,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[string]] $Failures
    )

    foreach ($unknownField in @(Get-UnknownFieldNames -Object $Object -KnownFields $KnownFields)) {
        $Failures.Add("$Description declares field '$unknownField' that the typed round-trip model does not cover; it would be silently dropped.")
    }
}

function Assert-PackageRoundTrip {
    # Proves a package record survives a full typed read -> JSON write -> JSON read
    # cycle byte-for-byte, including every collection-valued field's exact element
    # count. A structural/shape check (are the required fields present?) cannot
    # catch a serializer that silently truncates nested depth or collapses a
    # single-element array to a scalar; this does.
    param(
        [Parameter(Mandatory = $true)] $Package,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[string]] $Failures
    )

    $id = [string] (Get-PropertyValue -Object $Package -Name 'id')
    Add-UnknownFieldFailures -Object $Package -KnownFields @(
            'id', 'auditedVersion', 'selectedVersion', 'latestStable', 'latestPrerelease',
            'listingState', 'family', 'disposition', 'rollbackGroup', 'rationale',
            'evidence', 'removalTrigger', 'sourceResults', 'historicalContext'
        ) -Description "Package '$id'" -Failures $Failures
    Assert-JsonStringFields -Object $Package -Fields @(
        'id', 'auditedVersion', 'selectedVersion', 'latestStable', 'latestPrerelease',
        'listingState', 'family', 'disposition', 'rollbackGroup', 'rationale', 'evidence', 'removalTrigger'
    ) -NullableFields @('latestStable', 'latestPrerelease') -Description "Package '$id'" -Failures $Failures
    Assert-JsonObjectArrayField -Object $Package -Field 'sourceResults' -Description "Package '$id'" -Failures $Failures
    Assert-JsonObjectArrayField -Object $Package -Field 'historicalContext' -Description "Package '$id'" -Failures $Failures

    foreach ($sourceResult in @((Get-PropertyValue -Object $Package -Name 'sourceResults') | Where-Object { $null -ne $_ })) {
        Add-UnknownFieldFailures -Object $sourceResult -KnownFields @(
                'source', 'listingState', 'latestStable', 'latestPrerelease', 'diagnostic'
            ) -Description "Package '$id' source result" -Failures $Failures
        Assert-JsonStringFields -Object $sourceResult -Fields @(
            'source', 'listingState', 'latestStable', 'latestPrerelease', 'diagnostic'
        ) -NullableFields @('latestStable', 'latestPrerelease') -Description "Package '$id' source result" -Failures $Failures
    }

    foreach ($history in @((Get-PropertyValue -Object $Package -Name 'historicalContext') | Where-Object { $null -ne $_ })) {
        Add-UnknownFieldFailures -Object $history -KnownFields @(
            'schema', 'label', 'auditedAtUtc', 'generatedFromRevision', 'id', 'auditedVersion',
            'selectedVersion', 'latestStable', 'latestPrerelease', 'listingState', 'family',
            'disposition', 'rollbackGroup', 'rationale', 'evidence', 'removalTrigger',
            'sourceResults', 'supersededBecause'
        ) -Description "Package '$id' historical context" -Failures $Failures
        Assert-JsonStringFields -Object $history -Fields @(
            'schema', 'label', 'auditedAtUtc', 'generatedFromRevision', 'id', 'auditedVersion',
            'selectedVersion', 'latestStable', 'latestPrerelease', 'listingState', 'family',
            'disposition', 'rollbackGroup', 'rationale', 'evidence', 'removalTrigger', 'supersededBecause'
        ) -NullableFields @('latestStable', 'latestPrerelease') -Description "Package '$id' historical context" -Failures $Failures
        Assert-JsonObjectArrayField -Object $history -Field 'sourceResults' -Description "Package '$id' historical context" -Failures $Failures
        foreach ($historicalSource in @((Get-PropertyValue -Object $history -Name 'sourceResults') | Where-Object { $null -ne $_ })) {
            Add-UnknownFieldFailures -Object $historicalSource -KnownFields @(
                'source', 'listingState', 'latestStable', 'latestPrerelease', 'diagnostic'
            ) -Description "Package '$id' historical source result" -Failures $Failures
            Assert-JsonStringFields -Object $historicalSource -Fields @(
                'source', 'listingState', 'latestStable', 'latestPrerelease', 'diagnostic'
            ) -NullableFields @('latestStable', 'latestPrerelease') `
                -Description "Package '$id' historical source result" -Failures $Failures
        }
    }

    $originalSourceCount = @(Get-PropertyValue -Object $Package -Name 'sourceResults').Count
    $originalHistoryCount = @((Get-PropertyValue -Object $Package -Name 'historicalContext') | Where-Object { $null -ne $_ }).Count
    $canonical = ConvertTo-OrderedPackageRecord -Package $Package
    if (@($canonical.sourceResults).Count -ne $originalSourceCount) {
        $Failures.Add("Package '$id' sourceResults changed cardinality from $originalSourceCount to $(@($canonical.sourceResults).Count) while building the typed record.")
    }
    if (@($canonical.historicalContext).Count -ne $originalHistoryCount) {
        $Failures.Add("Package '$id' historicalContext changed cardinality from $originalHistoryCount to $(@($canonical.historicalContext).Count) while building the typed record.")
    }

    $firstPassJson = $canonical | ConvertTo-Json -Depth 12
    try {
        $reread = $firstPassJson | ConvertFrom-Json -DateKind String -ErrorAction Stop
    }
    catch {
        $Failures.Add("Package '$id' typed record did not survive re-parsing after serialization. $($_.Exception.GetBaseException().Message)")
        return
    }

    $secondPassJson = (ConvertTo-OrderedPackageRecord -Package $reread) | ConvertTo-Json -Depth 12
    if ($firstPassJson -cne $secondPassJson) {
        $Failures.Add("Package '$id' typed record did not round-trip identically through a second JSON write/read cycle.")
    }

    if (@($reread.sourceResults).Count -ne $originalSourceCount) {
        $Failures.Add("Package '$id' sourceResults changed cardinality from $originalSourceCount to $(@($reread.sourceResults).Count) across a JSON round trip.")
    }
    if (@($reread.historicalContext).Count -ne $originalHistoryCount) {
        $Failures.Add("Package '$id' historicalContext changed cardinality from $originalHistoryCount to $(@($reread.historicalContext).Count) across a JSON round trip.")
    }
}

function Assert-FamilyRoundTrip {
    # Family-level counterpart to Assert-PackageRoundTrip: proves packageIds and
    # representativeConsumers -- the family's collection-valued provenance -- and
    # every scalar owner field survive a full read/write cycle unchanged.
    param(
        [Parameter(Mandatory = $true)] $Decision,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.Collections.Generic.List[string]] $Failures
    )

    $family = [string] (Get-PropertyValue -Object $Decision -Name 'family')
    Add-UnknownFieldFailures -Object $Decision -KnownFields @(
            'family', 'disposition', 'rollbackGroup', 'packageIds', 'rationale',
            'compatibilityEvidence', 'removalTrigger', 'representativeConsumers',
            'preservation', 'historicalContext'
        ) -Description "Family '$family'" -Failures $Failures
    Assert-JsonStringFields -Object $Decision -Fields @(
        'family', 'disposition', 'rollbackGroup', 'rationale', 'compatibilityEvidence', 'removalTrigger'
    ) -Description "Family '$family'" -Failures $Failures
    Assert-JsonStringArrayField -Object $Decision -Field 'packageIds' -Description "Family '$family'" -Failures $Failures
    Assert-JsonStringArrayField -Object $Decision -Field 'representativeConsumers' -Description "Family '$family'" -Failures $Failures
    Assert-JsonObjectArrayField -Object $Decision -Field 'historicalContext' -Description "Family '$family'" -Failures $Failures

    $preservation = Get-PropertyValue -Object $Decision -Name 'preservation'
    if ($null -ne $preservation) {
        Add-UnknownFieldFailures -Object $preservation -KnownFields @(
            'status', 'reason', 'catalogPath', 'catalogSha256', 'sourceScopeSha256',
            'packageMetadataSha256', 'consumerEvidenceSha256'
        ) -Description "Family '$family' preservation" -Failures $Failures
        Assert-JsonStringFields -Object $preservation -Fields @(
            'status', 'reason', 'catalogPath', 'catalogSha256', 'sourceScopeSha256',
            'packageMetadataSha256', 'consumerEvidenceSha256'
        ) -Description "Family '$family' preservation" -Failures $Failures
    }

    foreach ($history in @((Get-PropertyValue -Object $Decision -Name 'historicalContext') | Where-Object { $null -ne $_ })) {
        Add-UnknownFieldFailures -Object $history -KnownFields @(
            'schema', 'label', 'auditedAtUtc', 'generatedFromRevision', 'family', 'disposition',
            'rollbackGroup', 'packageIds', 'rationale', 'compatibilityEvidence', 'removalTrigger',
            'representativeConsumers', 'preservation', 'supersededBecause'
        ) -Description "Family '$family' historical context" -Failures $Failures
        Assert-JsonStringFields -Object $history -Fields @(
            'schema', 'label', 'auditedAtUtc', 'generatedFromRevision', 'family', 'disposition',
            'rollbackGroup', 'rationale', 'compatibilityEvidence', 'removalTrigger', 'supersededBecause'
        ) -Description "Family '$family' historical context" -Failures $Failures
        Assert-JsonStringArrayField -Object $history -Field 'packageIds' `
            -Description "Family '$family' historical context" -Failures $Failures
        Assert-JsonStringArrayField -Object $history -Field 'representativeConsumers' `
            -Description "Family '$family' historical context" -Failures $Failures

        $historicalPreservation = Get-PropertyValue -Object $history -Name 'preservation'
        if ($null -ne $historicalPreservation) {
            Add-UnknownFieldFailures -Object $historicalPreservation -KnownFields @(
                'status', 'reason', 'catalogPath', 'catalogSha256', 'sourceScopeSha256',
                'packageMetadataSha256', 'consumerEvidenceSha256'
            ) -Description "Family '$family' historical preservation" -Failures $Failures
            Assert-JsonStringFields -Object $historicalPreservation `
                -Fields @($historicalPreservation.PSObject.Properties.Name) `
                -Description "Family '$family' historical preservation" -Failures $Failures
        }
    }

    $originalPackageIdCount = @(Get-PropertyValue -Object $Decision -Name 'packageIds').Count
    $originalConsumerCount = @(Get-PropertyValue -Object $Decision -Name 'representativeConsumers').Count
    $originalHistoryCount = @((Get-PropertyValue -Object $Decision -Name 'historicalContext') | Where-Object { $null -ne $_ }).Count
    $canonical = ConvertTo-OrderedFamilyRecord -Decision $Decision
    if (@($canonical.packageIds).Count -ne $originalPackageIdCount) {
        $Failures.Add("Family '$family' packageIds changed cardinality from $originalPackageIdCount to $(@($canonical.packageIds).Count) while building the typed record.")
    }
    if (@($canonical.representativeConsumers).Count -ne $originalConsumerCount) {
        $Failures.Add("Family '$family' representativeConsumers changed cardinality from $originalConsumerCount to $(@($canonical.representativeConsumers).Count) while building the typed record.")
    }
    if (@($canonical.historicalContext).Count -ne $originalHistoryCount) {
        $Failures.Add("Family '$family' historicalContext changed cardinality from $originalHistoryCount to $(@($canonical.historicalContext).Count) while building the typed record.")
    }

    $firstPassJson = $canonical | ConvertTo-Json -Depth 12
    try {
        $reread = $firstPassJson | ConvertFrom-Json -DateKind String -ErrorAction Stop
    }
    catch {
        $Failures.Add("Family '$family' typed record did not survive re-parsing after serialization. $($_.Exception.GetBaseException().Message)")
        return
    }

    $secondPassJson = (ConvertTo-OrderedFamilyRecord -Decision $reread) | ConvertTo-Json -Depth 12
    if ($firstPassJson -cne $secondPassJson) {
        $Failures.Add("Family '$family' typed record did not round-trip identically through a second JSON write/read cycle.")
    }

    if (@($reread.packageIds).Count -ne $originalPackageIdCount) {
        $Failures.Add("Family '$family' packageIds changed cardinality from $originalPackageIdCount to $(@($reread.packageIds).Count) across a JSON round trip.")
    }

    if (@($reread.representativeConsumers).Count -ne $originalConsumerCount) {
        $Failures.Add("Family '$family' representativeConsumers changed cardinality from $originalConsumerCount to $(@($reread.representativeConsumers).Count) across a JSON round trip.")
    }

    if (@($reread.historicalContext).Count -ne $originalHistoryCount) {
        $Failures.Add("Family '$family' historicalContext changed cardinality from $originalHistoryCount to $(@($reread.historicalContext).Count) across a JSON round trip.")
    }
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
$catalogSha256 = Get-RequiredText -Object $audit -Name 'catalogSha256' -Description 'Audit' -Failures $failures
$actualCatalogSha256 = Get-CatalogSha256 -Path $resolvedCatalogPath
if ($catalogSha256 -cne $actualCatalogSha256) {
    $failures.Add('Audit catalogSha256 does not match the evaluated catalog declaration bytes.')
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
$sourceScopeSha256 = Get-SourceScopeFingerprint -Sources $sources

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

    foreach ($historicalEntry in @((Get-PropertyValue -Object $package -Name 'historicalContext') | Where-Object { $null -ne $_ })) {
        $historicalSchema = [string](Get-PropertyValue -Object $historicalEntry -Name 'schema')
        if ([string]::IsNullOrWhiteSpace($historicalSchema)) {
            $failures.Add("Package '$id' has historical context without a typed schema.")
            continue
        }
        if ($historicalSchema -cne 'hexalith.package-audit-package-history.v1') {
            $failures.Add("Package '$id' has unsupported historical context schema '$historicalSchema'.")
            continue
        }
        foreach ($field in @(
                'label', 'auditedAtUtc', 'generatedFromRevision', 'id', 'auditedVersion', 'selectedVersion',
                'listingState', 'family', 'disposition', 'rollbackGroup', 'rationale', 'evidence',
                'removalTrigger', 'supersededBecause'
            )) {
            $null = Get-RequiredText -Object $historicalEntry -Name $field `
                -Description "Package '$id' historical context" -Failures $failures
        }
        if ($historicalEntry.PSObject.Properties.Name -notcontains 'latestStable' -or
            $historicalEntry.PSObject.Properties.Name -notcontains 'latestPrerelease' -or
            $historicalEntry.PSObject.Properties.Name -notcontains 'sourceResults') {
            $failures.Add("Package '$id' historical context must label latestStable, latestPrerelease, and sourceResults.")
        }
        $historyTimestamp = [DateTimeOffset]::MinValue
        $historyTimestampText = [string](Get-PropertyValue -Object $historicalEntry -Name 'auditedAtUtc')
        if (-not [DateTimeOffset]::TryParse($historyTimestampText, [ref] $historyTimestamp) -or
            $historyTimestamp.Offset -ne [TimeSpan]::Zero) {
            $failures.Add("Package '$id' historical context auditedAtUtc must be a valid UTC timestamp.")
        }
        if ([string](Get-PropertyValue -Object $historicalEntry -Name 'generatedFromRevision') -cnotmatch '^[0-9a-f]{40}$') {
            $failures.Add("Package '$id' historical context generatedFromRevision must be a full lowercase Git revision.")
        }
        if ([string](Get-PropertyValue -Object $historicalEntry -Name 'id') -cne $id) {
            $failures.Add("Package '$id' historical context has a mismatched package identity.")
        }
        if ([string](Get-PropertyValue -Object $historicalEntry -Name 'family') -cne $family) {
            $failures.Add("Package '$id' historical context has a mismatched family identity.")
        }
        if ([string](Get-PropertyValue -Object $historicalEntry -Name 'listingState') -notin
            @('listed', 'unlisted', 'missing', 'unresolved', 'unrecorded')) {
            $failures.Add("Package '$id' historical context has an invalid listingState.")
        }
        if ([string](Get-PropertyValue -Object $historicalEntry -Name 'disposition') -notin @('accepted', 'retained')) {
            $failures.Add("Package '$id' historical context has an invalid disposition.")
        }
    }

    $package | Add-Member -NotePropertyName _validatedFamily -NotePropertyValue $family -Force
    $package | Add-Member -NotePropertyName _validatedDisposition -NotePropertyValue $disposition -Force
    $package | Add-Member -NotePropertyName _validatedRollbackGroup -NotePropertyValue $rollbackGroup -Force

    Assert-PackageRoundTrip -Package $package -Failures $failures
}

foreach ($catalogEntry in $catalogVersions.GetEnumerator()) {
    if (-not $auditPackages.ContainsKey($catalogEntry.Key)) {
        $failures.Add("Evaluated catalog package '$($catalogEntry.Key)' has no audit evidence.")
    }
}

$consumerEvidence = Get-PropertyValue -Object $audit -Name 'consumerEvidence'
$consumerEvidenceByFamily = @{}
$consumerEntries = @()
$consumerDiscovery = ''
$consumerPackageRelations = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if ($null -eq $consumerEvidence) {
    $failures.Add('Audit must declare owned direct-consumer evidence provenance.')
}
else {
    $consumerSchema = Get-RequiredText `
        -Object $consumerEvidence -Name 'schema' -Description 'Consumer evidence' -Failures $failures
    if ($consumerSchema -cne 'hexalith.package-consumer-evidence.v1') {
        $failures.Add("Consumer evidence schema '$consumerSchema' is unsupported.")
    }
    $consumerDiscovery = Get-RequiredText `
        -Object $consumerEvidence -Name 'discovery' -Description 'Consumer evidence' -Failures $failures
    if ($consumerDiscovery -notin @('git-ls-files', 'explicit-fixture')) {
        $failures.Add("Consumer evidence discovery '$consumerDiscovery' is unsupported.")
    }
    $consumerFixture = Get-PropertyValue -Object $consumerEvidence -Name 'fixture'
    $consumerFixtureSha256 = Get-PropertyValue -Object $consumerEvidence -Name 'fixtureSha256'
    $consumerFixtureMode = Get-PropertyValue -Object $consumerEvidence -Name 'fixtureMode'
    $explicitFixtureRelations = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($consumerDiscovery -ceq 'explicit-fixture') {
        if ([string]::IsNullOrWhiteSpace([string] $consumerFixture) -or
            [string] $consumerFixtureSha256 -cnotmatch '^[0-9a-f]{64}$') {
            $failures.Add('Explicit consumer evidence must bind its fixture identity and lowercase SHA-256.')
        }
        if ([string] $consumerFixtureMode -cne 'synthetic-explicit') {
            $failures.Add("Explicit consumer evidence must be labeled with fixtureMode 'synthetic-explicit'.")
        }
        if (-not [string]::IsNullOrWhiteSpace([string] $consumerFixture)) {
            $fixtureFullPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $resolvedAuditPath) ([string] $consumerFixture)))
            if (-not (Test-Path -LiteralPath $fixtureFullPath -PathType Leaf)) {
                $failures.Add("Explicit consumer evidence fixture '$consumerFixture' could not be resolved beside the audit.")
            }
            elseif ((Get-Sha256File -Path $fixtureFullPath) -cne [string] $consumerFixtureSha256) {
                $failures.Add('Explicit consumer evidence fixtureSha256 does not match the fixture bytes.')
            }
            else {
                try {
                    $fixtureDocument = Get-Content -LiteralPath $fixtureFullPath -Raw -ErrorAction Stop |
                        ConvertFrom-Json -ErrorAction Stop
                    foreach ($fixtureEntry in @((Get-PropertyValue -Object $fixtureDocument -Name 'entries'))) {
                        $fixtureConsumer = [string](Get-PropertyValue -Object $fixtureEntry -Name 'consumer')
                        $fixturePackageId = [string](Get-PropertyValue -Object $fixtureEntry -Name 'packageId')
                        if ([string]::IsNullOrWhiteSpace($fixtureConsumer) -or
                            [string]::IsNullOrWhiteSpace($fixturePackageId) -or
                            -not $explicitFixtureRelations.Add("$fixtureConsumer|$fixturePackageId")) {
                            $failures.Add('Explicit consumer evidence fixture contains a blank or duplicate consumer/package relation.')
                        }
                    }
                }
                catch {
                    $failures.Add("Explicit consumer evidence fixture is not valid semantic JSON. $($_.Exception.GetBaseException().Message)")
                }
            }
        }
    }
    elseif ($null -ne $consumerFixture -or $null -ne $consumerFixtureSha256 -or $null -ne $consumerFixtureMode) {
        $failures.Add('Git-owned consumer evidence must not declare an external or synthetic fixture binding.')
    }
    $consumerRevision = Get-RequiredText `
        -Object $consumerEvidence -Name 'repositoryRevision' -Description 'Consumer evidence' -Failures $failures
    if ($consumerRevision -cne $generatedFromRevision) {
        $failures.Add('Consumer evidence repositoryRevision must match generatedFromRevision.')
    }
    $declaredConsumerHash = Get-RequiredText `
        -Object $consumerEvidence -Name 'sha256' -Description 'Consumer evidence' -Failures $failures
    $consumerRelations = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $consumerEntries = @((Get-PropertyValue -Object $consumerEvidence -Name 'entries'))
    foreach ($entry in $consumerEntries) {
        $family = Get-RequiredText -Object $entry -Name 'family' -Description 'Consumer evidence entry' -Failures $failures
        $consumer = Get-RequiredText -Object $entry -Name 'consumer' -Description 'Consumer evidence entry' -Failures $failures
        $packageId = Get-RequiredText -Object $entry -Name 'packageId' -Description 'Consumer evidence entry' -Failures $failures
        $declarationPath = Get-RequiredText `
            -Object $entry -Name 'declarationPath' -Description 'Consumer evidence entry' -Failures $failures
        $declarationSha256 = Get-RequiredText `
            -Object $entry -Name 'declarationSha256' -Description 'Consumer evidence entry' -Failures $failures
        if ($declarationSha256 -cnotmatch '^[0-9a-f]{64}$') {
            $failures.Add("Consumer evidence declaration '$declarationPath' must have a lowercase SHA-256 value.")
        }
        if ($consumerDiscovery -ceq 'git-ls-files') {
            $declarationFullPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $declarationPath))
            if (-not $declarationFullPath.StartsWith("$repositoryRoot$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal) -or
                -not (Test-Path -LiteralPath $declarationFullPath -PathType Leaf)) {
                $failures.Add("Consumer declaration '$declarationPath' is not a repository-owned file.")
            }
            else {
                $trackedOutput = @(& git -C $repositoryRoot ls-files --error-unmatch -- $declarationPath 2>&1)
                if ($LASTEXITCODE -ne 0) {
                    $failures.Add("Consumer declaration '$declarationPath' is not tracked by Git.")
                }
                elseif ((Get-Sha256File -Path $declarationFullPath) -cne $declarationSha256) {
                    $failures.Add("Consumer declaration '$declarationPath' SHA-256 does not match its tracked bytes.")
                }
                else {
                    try {
                        [xml] $declarationDocument = Get-Content -LiteralPath $declarationFullPath -Raw -ErrorAction Stop
                        $declaredPackageIds = @(
                            $declarationDocument.SelectNodes(
                                "//*[local-name()='PackageReference' or local-name()='GlobalPackageReference']"
                            ) |
                                ForEach-Object { [string] $_.GetAttribute('Include') })
                        if ($consumer -cne $declarationPath -or $declaredPackageIds -cnotcontains $packageId) {
                            $failures.Add("Consumer evidence relation '$consumer|$packageId' is not declared by '$declarationPath'.")
                        }
                    }
                    catch {
                        $failures.Add("Consumer declaration '$declarationPath' could not be semantically parsed. $($_.Exception.GetBaseException().Message)")
                    }
                }
            }
        }
        elseif ($consumerDiscovery -ceq 'explicit-fixture') {
            if ($declarationPath -cne [string] $consumerFixture -or
                $declarationSha256 -cne [string] $consumerFixtureSha256 -or
                -not $explicitFixtureRelations.Contains("$consumer|$packageId")) {
                $failures.Add("Explicit consumer evidence relation '$consumer|$packageId' is not exactly bound to its fixture record and bytes.")
            }
        }
        $relation = "$family|$consumer|$packageId"
        if (-not $consumerRelations.Add($relation)) {
            $failures.Add("Consumer evidence relation '$relation' is duplicated.")
        }
        $null = $consumerPackageRelations.Add("$consumer|$packageId")
        if (-not $auditPackages.ContainsKey($packageId)) {
            $failures.Add("Consumer evidence references unknown package '$packageId'.")
            continue
        }
        if ($auditPackages[$packageId]._validatedFamily -cne $family) {
            $failures.Add("Consumer evidence package '$packageId' does not belong to family '$family'.")
        }
        if (-not $consumerEvidenceByFamily.ContainsKey($family)) {
            $consumerEvidenceByFamily[$family] = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        }
        $null = $consumerEvidenceByFamily[$family].Add($consumer)
    }
    $computedConsumerHash = Get-ConsumerRelationFingerprint -Entries $consumerEntries
    if ($declaredConsumerHash -cne $computedConsumerHash) {
        $failures.Add('Consumer evidence sha256 does not match its ordered direct-consumer relations and declaration bytes.')
    }
    if ($consumerDiscovery -ceq 'explicit-fixture' -and $consumerRelations.Count -ne $explicitFixtureRelations.Count) {
        $failures.Add('Explicit consumer evidence entries do not exactly cover the fixture relations.')
    }
}

# Independent PackageReference rediscovery vs. audit relation equality (owned csproj/
# props/targets only). The catalog can contain unconsumed rows for external repositories,
# but the locally rediscovered (consumer path, package ID) relations must exactly match
# the current git-owned consumer-evidence relations.
$resolvedConsumerScanRoot = if ([string]::IsNullOrWhiteSpace($ConsumerScanRoot)) {
    $repositoryRoot
}
else {
    (Resolve-Path -LiteralPath $ConsumerScanRoot -ErrorAction Stop).ProviderPath
}
$rediscoveredProjectFiles = @(Get-TrackedProjectFiles -Root $resolvedConsumerScanRoot)
$rediscoveredReferences = Get-PackageReferenceConsumers `
    -ProjectFiles $rediscoveredProjectFiles -RepositoryRoot $resolvedConsumerScanRoot -ExcludePath $resolvedCatalogPath
$rediscoveredRelations = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($identity in $rediscoveredReferences.Keys) {
    if (-not $auditPackages.ContainsKey($identity)) {
        $consumerList = [string]::Join(', ', @($rediscoveredReferences[$identity]))
        $failures.Add(
            "PackageReference '$identity' rediscovered from $consumerList has no audit evidence; rediscovery and audit output must not drift."
        )
    }
    foreach ($consumerPath in @($rediscoveredReferences[$identity])) {
        $null = $rediscoveredRelations.Add("$consumerPath|$identity")
    }
}
if ($consumerDiscovery -ceq 'git-ls-files') {
    foreach ($relation in $rediscoveredRelations) {
        if (-not $consumerPackageRelations.Contains($relation)) {
            $failures.Add(
                "Rediscovered PackageReference relation '$relation' has no exact consumer-evidence match."
            )
        }
    }
    foreach ($relation in $consumerPackageRelations) {
        if (-not $rediscoveredRelations.Contains($relation)) {
            $failures.Add(
                "Git-owned consumer-evidence relation '$relation' was not rediscovered from tracked project files."
            )
        }
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
    $expectedConsumers = if ($consumerEvidenceByFamily.ContainsKey($family)) {
        @($consumerEvidenceByFamily[$family])
    }
    else {
        @()
    }
    if ((Get-IdentitySignature -Values $representativeConsumers) -cne (Get-IdentitySignature -Values $expectedConsumers)) {
        $failures.Add("Family '$family' representativeConsumers do not exactly match owned direct-consumer evidence.")
    }
    foreach ($historicalEntry in @((Get-PropertyValue -Object $decision -Name 'historicalContext') | Where-Object { $null -ne $_ })) {
        $historicalSchema = [string](Get-PropertyValue -Object $historicalEntry -Name 'schema')
        if ([string]::IsNullOrWhiteSpace($historicalSchema)) {
            $failures.Add("Family '$family' has historical context without a typed schema.")
            continue
        }
        if ($historicalSchema -cne 'hexalith.package-audit-family-history.v1') {
            $failures.Add("Family '$family' has unsupported historical context schema '$historicalSchema'.")
            continue
        }
        foreach ($field in @(
                'label', 'auditedAtUtc', 'generatedFromRevision', 'family', 'disposition', 'rollbackGroup',
                'rationale', 'compatibilityEvidence', 'removalTrigger', 'supersededBecause'
            )) {
            $null = Get-RequiredText -Object $historicalEntry -Name $field `
                -Description "Family '$family' historical context" -Failures $failures
        }
        if ($historicalEntry.PSObject.Properties.Name -notcontains 'packageIds' -or
            $historicalEntry.PSObject.Properties.Name -notcontains 'representativeConsumers' -or
            $historicalEntry.PSObject.Properties.Name -notcontains 'preservation') {
            $failures.Add("Family '$family' historical context must label packageIds, representativeConsumers, and preservation.")
        }
        $historyTimestamp = [DateTimeOffset]::MinValue
        $historyTimestampText = [string](Get-PropertyValue -Object $historicalEntry -Name 'auditedAtUtc')
        if (-not [DateTimeOffset]::TryParse($historyTimestampText, [ref] $historyTimestamp) -or
            $historyTimestamp.Offset -ne [TimeSpan]::Zero) {
            $failures.Add("Family '$family' historical context auditedAtUtc must be a valid UTC timestamp.")
        }
        if ([string](Get-PropertyValue -Object $historicalEntry -Name 'generatedFromRevision') -cnotmatch '^[0-9a-f]{40}$') {
            $failures.Add("Family '$family' historical context generatedFromRevision must be a full lowercase Git revision.")
        }
        if ([string](Get-PropertyValue -Object $historicalEntry -Name 'family') -cne $family) {
            $failures.Add("Family '$family' historical context has a mismatched family identity.")
        }
        if ([string](Get-PropertyValue -Object $historicalEntry -Name 'disposition') -notin @('accepted', 'retained')) {
            $failures.Add("Family '$family' historical context has an invalid disposition.")
        }
        $historicalPreservation = Get-PropertyValue -Object $historicalEntry -Name 'preservation'
        if ($null -ne $historicalPreservation) {
            $historicalPreservationStatus = Get-RequiredText -Object $historicalPreservation -Name 'status' `
                -Description "Family '$family' historical preservation" -Failures $failures
            if ($historicalPreservationStatus -notin @('preserved', 'migrated', 'refreshed', 'legacy-unbound')) {
                $failures.Add("Family '$family' historical context has an invalid preservation status '$historicalPreservationStatus'.")
            }
            $null = Get-RequiredText -Object $historicalPreservation -Name 'reason' `
                -Description "Family '$family' historical preservation" -Failures $failures
        }
    }
    $preservation = Get-PropertyValue -Object $decision -Name 'preservation'
    if ($null -eq $preservation) {
        $failures.Add("Family '$family' is missing preservation provenance.")
    }
    else {
        $preservationStatus = Get-RequiredText `
            -Object $preservation -Name 'status' -Description "Family '$family' preservation" -Failures $failures
        if ($preservationStatus -notin @('preserved', 'migrated', 'refreshed')) {
            $failures.Add("Family '$family' has invalid preservation status '$preservationStatus'.")
        }
        if ($disposition -eq 'accepted' -and $preservationStatus -cne 'preserved') {
            $failures.Add("Accepted family '$family' must have preservation status 'preserved'.")
        }
        $null = Get-RequiredText `
            -Object $preservation -Name 'reason' -Description "Family '$family' preservation" -Failures $failures
        $preservationCatalogPath = Get-RequiredText `
            -Object $preservation -Name 'catalogPath' -Description "Family '$family' preservation" -Failures $failures
        if ($preservationCatalogPath -cne $catalogPathValue) {
            $failures.Add("Family '$family' preservation catalogPath does not match the audit catalogPath.")
        }
        foreach ($hashName in @('catalogSha256', 'sourceScopeSha256', 'packageMetadataSha256', 'consumerEvidenceSha256')) {
            $hash = Get-RequiredText `
                -Object $preservation -Name $hashName -Description "Family '$family' preservation" -Failures $failures
            if ($hash -cnotmatch '^[0-9a-f]{64}$') {
                $failures.Add("Family '$family' preservation $hashName must be a lowercase SHA-256 value.")
            }
        }
        if ((Get-PropertyValue -Object $preservation -Name 'catalogSha256') -cne $catalogSha256) {
            $failures.Add("Family '$family' preservation catalogSha256 does not match the audit catalog binding.")
        }
        if ((Get-PropertyValue -Object $preservation -Name 'sourceScopeSha256') -cne $sourceScopeSha256) {
            $failures.Add("Family '$family' preservation sourceScopeSha256 does not match the configured source records.")
        }
        $expectedPackageMetadataHash = Get-PackageMetadataFingerprint -PackageRows @(
            $packages | Where-Object { $_._validatedFamily -ceq $family }
        )
        if ((Get-PropertyValue -Object $preservation -Name 'packageMetadataSha256') -cne $expectedPackageMetadataHash) {
            $failures.Add("Family '$family' preservation packageMetadataSha256 does not match its package/source relations.")
        }
        $expectedConsumerHash = Get-ConsumerRelationFingerprint -Entries @(
            $consumerEntries | Where-Object { (Get-PropertyValue -Object $_ -Name 'family') -ceq $family }
        )
        if ((Get-PropertyValue -Object $preservation -Name 'consumerEvidenceSha256') -cne $expectedConsumerHash) {
            $failures.Add("Family '$family' preservation consumerEvidenceSha256 does not match its consumer-package relations and declaration bytes.")
        }
    }
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

    Assert-FamilyRoundTrip -Decision $decision -Failures $failures
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
