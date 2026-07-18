[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory = $true, Position = 1)]
    [string] $CatalogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Stop-Validation {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[string]] $Failures
    )

    [Console]::Error.WriteLine("Consumer package authority validation failed with $($Failures.Count) error(s):")
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

function Read-XmlFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    try {
        [xml] $xml = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        return $xml
    }
    catch {
        throw "'$Path' is not valid XML. $($_.Exception.GetBaseException().Message)"
    }
}

function Get-ConsumerXmlFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $trackedPaths = @(& git -C $Root ls-files -- '*.csproj' '*.props' '*.targets' 2>$null)
    if ($LASTEXITCODE -eq 0) {
        return @(
            $trackedPaths |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                ForEach-Object { Join-Path $Root $_ }
        )
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

function Invoke-ProjectEvaluation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectPath,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $output = @(& dotnet msbuild $ProjectPath -nologo @Arguments 2>&1)
    $outputText = [string]::Join("`n", @($output | ForEach-Object { [string] $_ }))
    if ($LASTEXITCODE -ne 0) {
        throw "Evaluation of '$ProjectPath' exited with code $LASTEXITCODE. $outputText"
    }

    try {
        return $outputText | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Evaluation of '$ProjectPath' returned malformed JSON. $($_.Exception.GetBaseException().Message)"
    }
}

try {
    $resolvedRepositoryRoot = Resolve-ExistingPath `
        -Path $RepositoryRoot -Description 'Repository root' -PathType Container
    $resolvedCatalogPath = Resolve-ExistingPath `
        -Path $CatalogPath -Description 'Authoritative catalog' -PathType Leaf
}
catch {
    [Console]::Error.WriteLine("Consumer package authority validation failed: $($_.Exception.Message)")
    exit 1
}

$failures = [System.Collections.Generic.List[string]]::new()
$catalogXml = Read-XmlFile -Path $resolvedCatalogPath
$authoritativeVersionProperties = @{}
foreach ($packageVersion in @($catalogXml.SelectNodes("//*[local-name()='PackageVersion']"))) {
    $versionText = [string] $packageVersion.GetAttribute('Version')
    if ([string]::IsNullOrWhiteSpace($versionText)) {
        $versionNode = $packageVersion.SelectSingleNode("*[local-name()='Version']")
        if ($null -ne $versionNode) {
            $versionText = [string] $versionNode.InnerText
        }
    }

    foreach ($match in [regex]::Matches($versionText, '\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)')) {
        $authoritativeVersionProperties[$match.Groups[1].Value] = $true
    }
}

$consumerXmlFiles = @(Get-ConsumerXmlFiles -Root $resolvedRepositoryRoot)
$projectFiles = @($consumerXmlFiles | Where-Object { [IO.Path]::GetExtension($_) -ieq '.csproj' })
if ($projectFiles.Count -eq 0) {
    $failures.Add("Repository '$resolvedRepositoryRoot' contains no tracked .NET project files.")
}

$wrapperPath = Join-Path $resolvedRepositoryRoot 'Directory.Packages.props'
if (-not (Test-Path -LiteralPath $wrapperPath -PathType Leaf)) {
    $failures.Add("Repository root has no Directory.Packages.props wrapper.")
}
else {
    $wrapperXml = Read-XmlFile -Path $wrapperPath
    if (@($wrapperXml.SelectNodes("//*[local-name()='Import']")).Count -eq 0) {
        $failures.Add("Repository wrapper '$wrapperPath' does not import the authoritative catalog.")
    }
}

foreach ($xmlPath in $consumerXmlFiles) {
    $resolvedXmlPath = [IO.Path]::GetFullPath($xmlPath)
    if ([StringComparer]::OrdinalIgnoreCase.Equals($resolvedXmlPath, $resolvedCatalogPath)) {
        continue
    }

    $relativePath = [IO.Path]::GetRelativePath($resolvedRepositoryRoot, $resolvedXmlPath)
    $xml = Read-XmlFile -Path $resolvedXmlPath
    foreach ($packageVersion in @($xml.SelectNodes("//*[local-name()='PackageVersion']"))) {
        $include = [string] $packageVersion.GetAttribute('Include')
        $update = [string] $packageVersion.GetAttribute('Update')
        if (-not [string]::IsNullOrWhiteSpace($include)) {
            $failures.Add("$relativePath contains consumer PackageVersion Include '$include'.")
        }
        elseif (-not [string]::IsNullOrWhiteSpace($update)) {
            $failures.Add("$relativePath contains consumer PackageVersion Update '$update'.")
        }
        elseif ($packageVersion.ParentNode.LocalName -eq 'ItemGroup') {
            $failures.Add("$relativePath contains a consumer PackageVersion declaration.")
        }
    }

    foreach ($itemName in @('PackageReference', 'GlobalPackageReference')) {
        foreach ($item in @($xml.SelectNodes("//*[local-name()='$itemName']"))) {
            foreach ($metadataName in @('Version', 'VersionOverride')) {
                if ($item.HasAttribute($metadataName)) {
                    $metadataValue = [string] $item.GetAttribute($metadataName)
                    $failures.Add(
                        "$relativePath contains $itemName $metadataName metadata '$metadataValue'."
                    )
                }

                foreach ($metadataNode in @($item.SelectNodes("*[local-name()='$metadataName']"))) {
                    $failures.Add(
                        "$relativePath contains $itemName nested $metadataName metadata '$($metadataNode.InnerText)'."
                    )
                }
            }
        }
    }

    foreach ($cpmProperty in @($xml.SelectNodes("//*[local-name()='ManagePackageVersionsCentrally']"))) {
        if ([string] $cpmProperty.InnerText -match '^\s*false\s*$') {
            $failures.Add("$relativePath sets ManagePackageVersionsCentrally=false.")
        }
    }

    foreach ($overrideProperty in @($xml.SelectNodes("//*[local-name()='CentralPackageVersionOverrideEnabled']"))) {
        if ([string] $overrideProperty.InnerText -notmatch '^\s*false\s*$') {
            $failures.Add("$relativePath enables CentralPackageVersionOverrideEnabled.")
        }
    }

    foreach ($propertyName in $authoritativeVersionProperties.Keys) {
        foreach ($propertyNode in @($xml.SelectNodes("//*[local-name()='$propertyName']"))) {
            $failures.Add("$relativePath overrides authoritative version property '$propertyName'.")
        }
    }
}

if ($failures.Count -gt 0) {
    Stop-Validation -Failures $failures
}

try {
    $catalogEvaluation = Invoke-ProjectEvaluation -ProjectPath $resolvedCatalogPath `
        -Arguments @('-getItem:PackageVersion')
}
catch {
    $failures.Add($_.Exception.Message)
    Stop-Validation -Failures $failures
}

$authoritativePackages = @{}
foreach ($packageVersion in @($catalogEvaluation.Items.PackageVersion)) {
    $identity = [string] $packageVersion.Identity
    $version = [string] $packageVersion.Version
    if (-not [string]::IsNullOrWhiteSpace($identity)) {
        $authoritativePackages[$identity] = $version
    }
}

foreach ($projectPath in $projectFiles) {
    $relativeProjectPath = [IO.Path]::GetRelativePath($resolvedRepositoryRoot, $projectPath)
    try {
        $evaluation = Invoke-ProjectEvaluation -ProjectPath $projectPath -Arguments @(
            '-getProperty:ManagePackageVersionsCentrally'
            '-getProperty:CentralPackageVersionOverrideEnabled'
            '-getProperty:HexalithVersionsLoaded'
            '-getItem:PackageReference'
            '-getItem:PackageVersion'
        )
    }
    catch {
        $failures.Add($_.Exception.Message)
        continue
    }

    if ([string] $evaluation.Properties.ManagePackageVersionsCentrally -cne 'true') {
        $failures.Add("$relativeProjectPath does not evaluate ManagePackageVersionsCentrally=true.")
    }

    if ([string] $evaluation.Properties.CentralPackageVersionOverrideEnabled -cne 'false') {
        $failures.Add("$relativeProjectPath does not evaluate CentralPackageVersionOverrideEnabled=false.")
    }

    if ([string] $evaluation.Properties.HexalithVersionsLoaded -cne 'true') {
        $failures.Add("$relativeProjectPath does not import the authoritative catalog marker.")
    }

    $effectivePackages = @{}
    foreach ($packageVersion in @($evaluation.Items.PackageVersion)) {
        $effectivePackages[[string] $packageVersion.Identity] = [string] $packageVersion.Version
    }

    foreach ($authoritativePackage in $authoritativePackages.GetEnumerator()) {
        if (-not $effectivePackages.ContainsKey($authoritativePackage.Key)) {
            $failures.Add(
                "$relativeProjectPath is missing authoritative PackageVersion '$($authoritativePackage.Key)'."
            )
            continue
        }

        if ([string] $effectivePackages[$authoritativePackage.Key] -cne [string] $authoritativePackage.Value) {
            $failures.Add(
                "$relativeProjectPath resolves '$($authoritativePackage.Key)' to '$($effectivePackages[$authoritativePackage.Key])' instead of '$($authoritativePackage.Value)'."
            )
        }
    }

    foreach ($effectivePackage in $effectivePackages.GetEnumerator()) {
        if (-not $authoritativePackages.ContainsKey($effectivePackage.Key)) {
            $failures.Add(
                "$relativeProjectPath evaluates non-authoritative PackageVersion '$($effectivePackage.Key)'."
            )
        }
    }

    foreach ($packageReference in @($evaluation.Items.PackageReference)) {
        $isImplicitlyDefined = if (
            $null -ne $packageReference -and
            $packageReference.PSObject.Properties.Name -contains 'IsImplicitlyDefined'
        ) {
            [string] $packageReference.IsImplicitlyDefined
        }
        else {
            ''
        }

        if ($isImplicitlyDefined -ieq 'true') {
            continue
        }

        $identity = [string] $packageReference.Identity
        if (-not [string]::IsNullOrWhiteSpace($identity) -and -not $authoritativePackages.ContainsKey($identity)) {
            $failures.Add(
                "$relativeProjectPath PackageReference '$identity' has no authoritative catalog row."
            )
        }
    }
}

if ($failures.Count -gt 0) {
    Stop-Validation -Failures $failures
}

[Console]::Out.WriteLine(
    "Consumer package authority validation passed for $($projectFiles.Count) projects in '$resolvedRepositoryRoot'."
)
