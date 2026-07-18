[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InventoryPath,

    [Parameter(Mandatory = $true)]
    [string] $CatalogPath,

    [string] $WorkspaceRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$nugetVersionPattern = '^(0|[1-9]\d*)(?:\.(0|[1-9]\d*)){0,3}(?:-(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'

function Stop-Validation {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]] $Failures
    )

    [Console]::Error.WriteLine("Package version exception validation failed with $($Failures.Count) error(s):")
    foreach ($failure in $Failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

function Resolve-ExistingPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Description,

        [Parameter(Mandatory = $true)]
        [ValidateSet('Container', 'Leaf')]
        [string] $PathType
    )

    try {
        $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    }
    catch {
        throw "$Description was not found: $Path"
    }

    if (-not (Test-Path -LiteralPath $resolved.ProviderPath -PathType $PathType)) {
        throw "$Description has the wrong path type: $Path"
    }

    return $resolved.ProviderPath
}

function Get-PropertyText {
    param(
        [AllowNull()]
        [object] $Object,

        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    if ($null -eq $Object -or $Object.PSObject.Properties.Name -notcontains $Name) {
        return ''
    }

    return ([string] $Object.$Name).Trim()
}

function Get-RepositoryFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string[]] $Patterns,

        [switch] $ExcludeReferences
    )

    $extensions = @($Patterns | ForEach-Object { $_ -replace '^\*', '' })
    $tracked = @(& git -C $Root ls-files 2>$null)
    if ($LASTEXITCODE -eq 0) {
        return @(
            $tracked |
                Where-Object {
                    -not [string]::IsNullOrWhiteSpace($_) -and
                    $extensions -contains [IO.Path]::GetExtension($_)
                } |
                ForEach-Object { Join-Path $Root $_ }
        )
    }

    return @(
        Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
            Where-Object {
                $_.FullName -notmatch '[\\/](?:bin|obj|\.git)[\\/]' -and
                (-not $ExcludeReferences -or $_.FullName -notmatch '[\\/]references[\\/]') -and
                $extensions -contains $_.Extension
            } |
            Select-Object -ExpandProperty FullName
    )
}

try {
    $resolvedInventoryPath = Resolve-ExistingPath `
        -Path $InventoryPath -Description 'Exception inventory' -PathType Leaf
    $resolvedCatalogPath = Resolve-ExistingPath `
        -Path $CatalogPath -Description 'Authoritative catalog' -PathType Leaf
    $inventory = Get-Content -LiteralPath $resolvedInventoryPath -Raw -ErrorAction Stop |
        ConvertFrom-Json -ErrorAction Stop
}
catch {
    [Console]::Error.WriteLine("Package version exception validation failed: $($_.Exception.Message)")
    exit 1
}

$failures = [System.Collections.Generic.List[string]]::new()
if ((Get-PropertyText -Object $inventory -Name 'schemaVersion') -cne '1') {
    $failures.Add('Inventory schemaVersion must be 1.')
}

if ([string]::IsNullOrWhiteSpace((Get-PropertyText -Object $inventory -Name 'architectureDecision'))) {
    $failures.Add('Inventory architectureDecision must name the governing decision.')
}

$inventoryEntries = if ($inventory.PSObject.Properties.Name -contains 'exceptions') {
    @($inventory.exceptions)
}
else {
    @()
}
if ($inventoryEntries.Count -eq 0) {
    $failures.Add('Inventory must contain at least one exception.')
}

$catalogOutput = @(& dotnet msbuild $resolvedCatalogPath -nologo -getItem:PackageVersion 2>&1)
$catalogOutputText = [string]::Join("`n", @($catalogOutput | ForEach-Object { [string] $_ }))
if ($LASTEXITCODE -ne 0) {
    $failures.Add("Catalog evaluation exited with code $LASTEXITCODE. $catalogOutputText")
    Stop-Validation -Failures $failures
}

try {
    $catalogEvaluation = $catalogOutputText | ConvertFrom-Json -ErrorAction Stop
}
catch {
    $failures.Add("Catalog evaluation returned malformed JSON. $($_.Exception.GetBaseException().Message)")
    Stop-Validation -Failures $failures
}

$catalogPackages = @{}
foreach ($packageVersion in @($catalogEvaluation.Items.PackageVersion)) {
    $catalogPackages[[string] $packageVersion.Identity] = [string] $packageVersion.Version
}

$allowlist = @{}
foreach ($entry in $inventoryEntries) {
    $kind = Get-PropertyText -Object $entry -Name 'kind'
    $owner = Get-PropertyText -Object $entry -Name 'owner'
    $path = (Get-PropertyText -Object $entry -Name 'path').Replace('\', '/')
    $id = Get-PropertyText -Object $entry -Name 'id'
    $version = Get-PropertyText -Object $entry -Name 'version'
    $rationale = Get-PropertyText -Object $entry -Name 'rationale'
    $alignmentRule = Get-PropertyText -Object $entry -Name 'alignmentRule'
    $key = "$kind|$owner|$path|$id"

    if ($kind -notin @('apphost-sdk', 'dotnet-tool')) {
        $failures.Add("Exception '$key' has unsupported kind '$kind'.")
    }

    foreach ($requiredValue in @{
        owner = $owner
        path = $path
        id = $id
        version = $version
        rationale = $rationale
        alignmentRule = $alignmentRule
    }.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string] $requiredValue.Value)) {
            $failures.Add("Exception '$key' has blank $($requiredValue.Key).")
        }
    }

    if ($version -cnotmatch $nugetVersionPattern) {
        $failures.Add("Exception '$key' has malformed version '$version'.")
    }

    if ($allowlist.ContainsKey($key)) {
        $failures.Add("Inventory contains duplicate exception key '$key'.")
        continue
    }

    if ($kind -eq 'apphost-sdk') {
        $catalogPackage = Get-PropertyText -Object $entry -Name 'catalogPackage'
        if ($id -cne 'Aspire.AppHost.Sdk') {
            $failures.Add("AppHost SDK exception '$key' must use ID 'Aspire.AppHost.Sdk'.")
        }

        if ($alignmentRule -cne 'exact-catalog-package' -or [string]::IsNullOrWhiteSpace($catalogPackage)) {
            $failures.Add("AppHost SDK exception '$key' must use exact-catalog-package alignment.")
        }
        elseif (-not $catalogPackages.ContainsKey($catalogPackage)) {
            $failures.Add("AppHost SDK exception '$key' references missing catalog package '$catalogPackage'.")
        }
        elseif ([string] $catalogPackages[$catalogPackage] -cne $version) {
            $failures.Add(
                "$id/$version is not aligned with $catalogPackage/$($catalogPackages[$catalogPackage]) for '$owner'."
            )
        }
    }
    elseif ($kind -eq 'dotnet-tool' -and $alignmentRule -cne 'exact-manifest') {
        $failures.Add("Tool exception '$key' must use exact-manifest alignment.")
    }

    $allowlist[$key] = $entry
}

if ($failures.Count -gt 0) {
    Stop-Validation -Failures $failures
}

if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    [Console]::Out.WriteLine("Package version exception inventory validated $($allowlist.Count) allowlisted exceptions.")
    exit 0
}

try {
    $resolvedWorkspaceRoot = Resolve-ExistingPath `
        -Path $WorkspaceRoot -Description 'Workspace root' -PathType Container
}
catch {
    $failures.Add($_.Exception.Message)
    Stop-Validation -Failures $failures
}

$repositoryRoots = [System.Collections.Generic.List[object]]::new()
$submoduleOwners = @{}
$gitmodulesPath = Join-Path $resolvedWorkspaceRoot '.gitmodules'
if (Test-Path -LiteralPath $gitmodulesPath -PathType Leaf) {
    $gitmodulesText = Get-Content -LiteralPath $gitmodulesPath -Raw
    foreach ($match in [regex]::Matches($gitmodulesText, '(?m)^\s*path\s*=\s*(.+?)\s*$')) {
        $relativeRoot = $match.Groups[1].Value.Trim().Replace('\', '/')
        $repositoryRoot = Join-Path $resolvedWorkspaceRoot $relativeRoot
        if (Test-Path -LiteralPath $repositoryRoot -PathType Container) {
            $owner = Split-Path -Leaf $relativeRoot
            $submoduleOwners[$owner] = $true
            $repositoryRoots.Add([pscustomobject] @{ Owner = $owner; Root = $repositoryRoot; IsWorkspace = $false })
        }
    }
}

$workspaceOwner = ''
foreach ($inventoryEntry in $inventoryEntries) {
    $candidateOwner = Get-PropertyText -Object $inventoryEntry -Name 'owner'
    $candidatePath = Get-PropertyText -Object $inventoryEntry -Name 'path'
    if (
        -not $submoduleOwners.ContainsKey($candidateOwner) -and
        (Test-Path -LiteralPath (Join-Path $resolvedWorkspaceRoot $candidatePath))
    ) {
        $workspaceOwner = $candidateOwner
        break
    }
}
if ([string]::IsNullOrWhiteSpace($workspaceOwner)) {
    $workspaceOwner = Split-Path -Leaf $resolvedWorkspaceRoot
}
$repositoryRoots.Add(
    [pscustomobject] @{ Owner = $workspaceOwner; Root = $resolvedWorkspaceRoot; IsWorkspace = $true }
)

$actualExceptions = @{}
foreach ($repository in $repositoryRoots) {
    $projectFiles = @(Get-RepositoryFiles -Root $repository.Root -Patterns @('*.csproj') `
        -ExcludeReferences:$repository.IsWorkspace)
    foreach ($projectPath in $projectFiles) {
        $projectText = Get-Content -LiteralPath $projectPath -Raw
        $sdkMatch = [regex]::Match($projectText, 'Aspire\.AppHost\.Sdk/([^";\s<]+)')
        if (-not $sdkMatch.Success) {
            continue
        }

        $relativePath = [IO.Path]::GetRelativePath($repository.Root, $projectPath).Replace('\', '/')
        $version = $sdkMatch.Groups[1].Value
        $key = "apphost-sdk|$($repository.Owner)|$relativePath|Aspire.AppHost.Sdk"
        $actualExceptions[$key] = [pscustomobject] @{
            Kind = 'apphost-sdk'
            Owner = $repository.Owner
            Path = $relativePath
            Id = 'Aspire.AppHost.Sdk'
            Version = $version
        }

        if ($catalogPackages.ContainsKey('Aspire.Hosting') -and $version -cne $catalogPackages['Aspire.Hosting']) {
            $failures.Add(
                "Aspire.AppHost.Sdk/$version is not aligned with Aspire.Hosting/$($catalogPackages['Aspire.Hosting']) for '$($repository.Owner)'."
            )
        }
    }

    $toolManifestFiles = @(Get-RepositoryFiles -Root $repository.Root -Patterns @('*.json') `
        -ExcludeReferences:$repository.IsWorkspace | Where-Object {
            $_.Replace('\', '/') -like '*/.config/dotnet-tools.json'
        })
    foreach ($manifestPath in $toolManifestFiles) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            $failures.Add("Tool manifest '$manifestPath' is malformed JSON.")
            continue
        }

        $relativePath = [IO.Path]::GetRelativePath($repository.Root, $manifestPath).Replace('\', '/')
        if ($manifest.PSObject.Properties.Name -notcontains 'tools') {
            continue
        }

        foreach ($toolProperty in $manifest.tools.PSObject.Properties) {
            $toolId = [string] $toolProperty.Name
            $toolVersion = Get-PropertyText -Object $toolProperty.Value -Name 'version'
            $key = "dotnet-tool|$($repository.Owner)|$relativePath|$toolId"
            $actualExceptions[$key] = [pscustomobject] @{
                Kind = 'dotnet-tool'
                Owner = $repository.Owner
                Path = $relativePath
                Id = $toolId
                Version = $toolVersion
            }
        }
    }
}

foreach ($actualEntry in $actualExceptions.GetEnumerator()) {
    if (-not $allowlist.ContainsKey($actualEntry.Key)) {
        $failures.Add("Workspace contains unlisted version exception '$($actualEntry.Key)'.")
        continue
    }

    $expectedVersion = Get-PropertyText -Object $allowlist[$actualEntry.Key] -Name 'version'
    if ([string] $actualEntry.Value.Version -cne $expectedVersion) {
        $failures.Add(
            "Exception '$($actualEntry.Key)' has version '$($actualEntry.Value.Version)'; allowlist requires '$expectedVersion'."
        )
    }
}

foreach ($allowlistEntry in $allowlist.GetEnumerator()) {
    if (-not $actualExceptions.ContainsKey($allowlistEntry.Key)) {
        $failures.Add("Allowlisted exception '$($allowlistEntry.Key)' was not found in the workspace.")
    }
}

if ($failures.Count -gt 0) {
    Stop-Validation -Failures $failures
}

[Console]::Out.WriteLine(
    "Package version exception workspace evidence matches the allowlist for $($actualExceptions.Count) exceptions."
)
