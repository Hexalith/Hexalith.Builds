[CmdletBinding()]
param(
    [string] $CatalogPath = (Join-Path $PSScriptRoot '../Props/Directory.Packages.props'),
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,
    [string] $NuspecPath = '',
    [string] $BuildSelectionsPath = '',
    [string] $Configuration = 'Debug',
    [string] $TargetFramework = 'net10.0',
    [ValidateRange(1, 300)]
    [int] $TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-CatalogProcess {
    param([string[]] $Arguments, [switch] $AllowInheritedSelections)
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    # MSBuild imports environment variables as initial properties. Only catalog declarations
    # may select controlled versions; caller/build observations are compared separately.
    foreach ($name in @($start.Environment.Keys)) {
        if (-not $AllowInheritedSelections -and ($name -like 'Hexalith*Version' -or $name -in @('HexalithRedisImage', 'HexalithRedisImageTag', 'HexalithRedisImageDigest', 'HexalithVersionsLoaded'))) {
            $null = $start.Environment.Remove($name)
        }
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'dotnet did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $null = $process.WaitForExit(5000)
            throw "dotnet catalog operation timed out after $TimeoutSeconds seconds."
        }
        if (-not [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]] @($stdout, $stderr)).Wait(5000)) {
            throw 'dotnet catalog output did not drain within five seconds.'
        }
        if ($process.ExitCode -ne 0) { throw "dotnet catalog operation failed ($($process.ExitCode)): $($stderr.Result) $($stdout.Result)" }
        return $stdout.Result.Trim()
    }
    finally { $process.Dispose() }
}

try {
    $CatalogPath = (Resolve-Path -LiteralPath $CatalogPath).ProviderPath
    $sdkVersion = Invoke-CatalogProcess @('--version')
    $sdkLines = Invoke-CatalogProcess @('--list-sdks')
    $sdkLine = @($sdkLines.Split("`n") | Where-Object { $_.StartsWith("$sdkVersion [", [StringComparison]::Ordinal) })
    if ($sdkLine.Count -ne 1) { throw "Cannot locate NuGet version semantics for .NET SDK $sdkVersion." }
    $sdkBase = [regex]::Match($sdkLine[0], '\[(.+)\]').Groups[1].Value
    Add-Type -Path (Join-Path (Join-Path $sdkBase $sdkVersion) 'NuGet.Versioning.dll')
    Add-Type -Path (Join-Path (Join-Path $sdkBase $sdkVersion) 'NuGet.Frameworks.dll')
    $target = [NuGet.Frameworks.NuGetFramework]::Parse($TargetFramework)
    if ($target.IsUnsupported -or $target.IsAny) { throw "TargetFramework '$TargetFramework' is invalid." }

    $propertyFields = [ordered] @{
        aspireAppHostSdkVersion = 'HexalithAspireAppHostSdkVersion'
        daprRuntimeVersion = 'HexalithDaprRuntimeVersion'
        daprCliVersion = 'HexalithDaprCliVersion'
        redisImage = 'HexalithRedisImage'
        redisImageTag = 'HexalithRedisImageTag'
        redisImageDigest = 'HexalithRedisImageDigest'
    }
    $packageFields = [ordered] @{
        eventStoreVersion = 'Hexalith.EventStore.Aspire'
        frontComposerVersion = 'Hexalith.FrontComposer.Shell'
        daprSdkVersion = 'Dapr.Client'
        aspireHostingVersion = 'Aspire.Hosting'
        aspireHostingDaprVersion = 'CommunityToolkit.Aspire.Hosting.Dapr'
    }
    $propertiesArgument = '-getProperty:' + ($propertyFields.Values -join ',')
    $contextArguments = @("-p:Configuration=$Configuration", "-p:TargetFramework=$TargetFramework")
    $inheritedSelections = @{}
    foreach ($name in [Environment]::GetEnvironmentVariables().Keys) {
        if ($name -like 'Hexalith*Version' -or $name -in @('HexalithRedisImage', 'HexalithRedisImageTag', 'HexalithRedisImageDigest')) {
            $inheritedSelections[$name] = [Environment]::GetEnvironmentVariable($name)
        }
    }
    # Preprocessing includes imported declarations while preserving their XML conditions.
    $preprocessedPath = Join-Path ([IO.Path]::GetTempPath()) "hexalith-catalog-$([Guid]::NewGuid().ToString('N')).xml"
    try {
        $null = Invoke-CatalogProcess (@('msbuild', $CatalogPath, '-nologo', "-preprocess:$preprocessedPath") + $contextArguments)
        [xml] $declarations = [IO.File]::ReadAllText($preprocessedPath)
        $requiredProperties = @($propertyFields.Values) + @('HexalithEventStoreVersion', 'HexalithFrontComposerVersion', 'HexalithAspireHostingDaprVersion')
        foreach ($property in $requiredProperties) {
            $unconditional = @($declarations.SelectNodes("//*[local-name()='PropertyGroup']/*[local-name()='$property']") | Where-Object {
                $conditionalAncestor = $_.SelectSingleNode("ancestor::*[@Condition and normalize-space(@Condition)!='']")
                if ($null -ne $conditionalAncestor) { return $false }
                $condition = ($_.GetAttribute('Condition') -replace '\s+', '')
                $singleQuotedDefault = "'" + '$(' + $property + ")'==''"
                $doubleQuotedDefault = '"' + '$(' + $property + ')"==""'
                [string]::IsNullOrEmpty($condition) -or $condition -ceq $singleQuotedDefault -or $condition -ceq $doubleQuotedDefault
            })
            if ($unconditional.Count -gt 1) { throw "Catalog field $property has duplicate unconditional declarations; found $($unconditional.Count)." }
        }
    }
    finally { Remove-Item -LiteralPath $preprocessedPath -Force -ErrorAction SilentlyContinue }
    # A neutral project name validates the Platform pair, preserving the Folders.Aspire exception.
    $evaluation = (Invoke-CatalogProcess (@('msbuild', $CatalogPath, '-nologo', '-getItem:PackageVersion', $propertiesArgument) + $contextArguments)) | ConvertFrom-Json -Depth 100
    $snapshot = [ordered] @{ schemaVersion = '1' }
    foreach ($field in $packageFields.Keys) {
        $package = $packageFields[$field]
        $items = @($evaluation.Items.PackageVersion | Where-Object { [string]::Equals($_.Identity, $package, [StringComparison]::OrdinalIgnoreCase) })
        if ($items.Count -ne 1) { throw "Catalog field $package requires exactly one evaluated PackageVersion; found $($items.Count)." }
        $snapshot[$field] = [string] $items[0].Version
    }
    foreach ($field in $propertyFields.Keys) {
        $property = $propertyFields[$field]
        $value = [string] $evaluation.Properties.$property
        if ([string]::IsNullOrWhiteSpace($value)) { throw "Catalog field $property is missing or empty." }
        $snapshot[$field] = $value
    }
    if ($inheritedSelections.Count -gt 0) {
        $observedEvaluation = (Invoke-CatalogProcess -Arguments (@('msbuild', $CatalogPath, '-nologo', '-getItem:PackageVersion', $propertiesArgument) + $contextArguments) -AllowInheritedSelections) | ConvertFrom-Json -Depth 100
        foreach ($field in @($packageFields.Keys) + @($propertyFields.Keys)) {
            if ($packageFields.Contains($field)) {
                $catalogField = $packageFields[$field]
                $observedItems = @($observedEvaluation.Items.PackageVersion | Where-Object { [string]::Equals($_.Identity, $catalogField, [StringComparison]::OrdinalIgnoreCase) })
                $observed = if ($observedItems.Count -eq 1) { [string] $observedItems[0].Version } else { '<missing or duplicate>' }
            }
            else { $catalogField = $propertyFields[$field]; $observed = [string] $observedEvaluation.Properties.$catalogField }
            if ($observed -cne $snapshot[$field]) {
                throw "Catalog field $catalogField has inconsistent build selection '$observed' inherited from environment; authoritative catalog selects '$($snapshot[$field])'. Change the catalog instead of overriding controlled selections."
            }
        }
    }
    if ($BuildSelectionsPath) {
        $buildSelections = @{}
        foreach ($line in [IO.File]::ReadAllLines($BuildSelectionsPath)) {
            $parts = $line.Split('=', 2)
            if ($parts.Count -ne 2 -or $buildSelections.ContainsKey($parts[0])) { throw "Malformed or duplicate build selection '$line'." }
            $buildSelections[$parts[0]] = $parts[1]
        }
        foreach ($field in @($packageFields.Keys) + @($propertyFields.Keys)) {
            $observed = $buildSelections[$field]
            $catalogField = if ($packageFields.Contains($field)) { $packageFields[$field] } else { $propertyFields[$field] }
            if ($null -eq $observed -or $observed -cne $snapshot[$field]) {
                if ($null -eq $observed) { $observed = '<missing>' }
                throw "Catalog field $catalogField has inconsistent build selection '$observed'; authoritative catalog selects '$($snapshot[$field])'. Change the catalog instead of overriding controlled selections."
            }
        }
    }
    foreach ($field in @($packageFields.Keys) + @($propertyFields.Keys)) {
        if ([string]::IsNullOrWhiteSpace($snapshot[$field]) -or $snapshot[$field] -cne $snapshot[$field].Trim()) { throw "Catalog field $field is missing or invalid." }
    }
    foreach ($field in @($packageFields.Keys) + @('aspireAppHostSdkVersion', 'daprRuntimeVersion', 'daprCliVersion')) {
        $parsed = $null
        if (-not [NuGet.Versioning.NuGetVersion]::TryParse($snapshot[$field], [ref] $parsed)) {
            $catalogField = if ($packageFields.Contains($field)) { $packageFields[$field] } else { $propertyFields[$field] }
            throw "Catalog field $catalogField has malformed version '$($snapshot[$field])'."
        }
    }
    if ($snapshot.redisImage -cnotmatch '^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*$') { throw 'Catalog field HexalithRedisImage is malformed.' }
    if ($snapshot.redisImageTag -notmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$') { throw 'Catalog field HexalithRedisImageTag is malformed.' }
    if ($snapshot.redisImageDigest -cnotmatch '^sha256:[0-9a-f]{64}$') { throw 'Catalog field HexalithRedisImageDigest is malformed.' }

    $selected = [NuGet.Versioning.NuGetVersion]::Parse($snapshot.eventStoreVersion)
    if ([string]::IsNullOrWhiteSpace($NuspecPath)) {
        $cacheOutput = Invoke-CatalogProcess @('nuget', 'locals', 'global-packages', '--list')
        $packageCache = $cacheOutput.Substring($cacheOutput.IndexOf(':') + 1).Trim()
        $NuspecPath = Join-Path $packageCache "hexalith.eventstore.aspire/$($selected.ToNormalizedString().ToLowerInvariant())/hexalith.eventstore.aspire.nuspec"
        if (-not (Test-Path -LiteralPath $NuspecPath -PathType Leaf)) {
            # Restore only the selected package, using the catalog directory's configured source hierarchy.
            $scratch = Join-Path (Split-Path -Parent $CatalogPath) ".platform-catalog-$([Guid]::NewGuid().ToString('N'))"
            $null = New-Item -ItemType Directory -Path $scratch
            try {
                $restoreProject = Join-Path $scratch 'metadata.csproj'
                $escapedVersion = [Security.SecurityElement]::Escape($snapshot.eventStoreVersion)
                $escapedFramework = [Security.SecurityElement]::Escape($TargetFramework)
                [IO.File]::WriteAllText($restoreProject, "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>$escapedFramework</TargetFramework><DirectoryBuildPropsPath/><DirectoryBuildTargetsPath/><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup><PackageDownload Include=`"Hexalith.EventStore.Aspire`" Version=`"[$escapedVersion]`" /></ItemGroup></Project>")
                $null = Invoke-CatalogProcess @('restore', $restoreProject, '--packages', $packageCache, '-p:DirectoryBuildPropsPath=', '-p:DirectoryBuildTargetsPath=')
            }
            finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
        }
    }
    if (-not (Test-Path -LiteralPath $NuspecPath -PathType Leaf)) { throw "Missing dependency metadata for Hexalith.EventStore.Aspire/$($snapshot.eventStoreVersion): $NuspecPath" }
    [xml] $metadata = [IO.File]::ReadAllText($NuspecPath)
    $metadataNodes = @($metadata.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']"))
    if ($metadataNodes.Count -ne 1) { throw 'Dependency metadata requires exactly one metadata element.' }
    $idNodes = @($metadataNodes[0].SelectNodes("*[local-name()='id']"))
    $versionNodes = @($metadataNodes[0].SelectNodes("*[local-name()='version']"))
    if ($idNodes.Count -ne 1 -or $versionNodes.Count -ne 1) { throw 'Dependency metadata requires exactly one ID and version element.' }
    $idNode = $idNodes[0]
    $versionNode = $versionNodes[0]
    $metadataVersion = $null
    if ($null -eq $idNode -or $null -eq $versionNode -or $idNode.InnerText -ine 'Hexalith.EventStore.Aspire' -or
        -not [NuGet.Versioning.NuGetVersion]::TryParse($versionNode.InnerText, [ref] $metadataVersion) -or
        -not [NuGet.Versioning.VersionComparer]::VersionRelease.Equals($metadataVersion, $selected)) {
        throw "Dependency metadata does not identify selected Hexalith.EventStore.Aspire/$($snapshot.eventStoreVersion)."
    }
    $dependencySets = @($metadata.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='dependencies']"))
    if ($dependencySets.Count -ne 1) { throw "Hexalith.EventStore.Aspire/$($snapshot.eventStoreVersion) metadata is missing or has duplicate CommunityToolkit.Aspire.Hosting.Dapr dependency requirements." }
    $dependencySet = $dependencySets[0]
    $groups = @($dependencySet.SelectNodes("*[local-name()='group']"))
    if ($groups.Count -gt 0) {
        if ($dependencySet.SelectNodes("*[local-name()='dependency']").Count -gt 0) { throw 'Dependency metadata cannot mix grouped and ungrouped dependencies.' }
        $frameworks = [Collections.Generic.List[NuGet.Frameworks.NuGetFramework]]::new()
        foreach ($group in $groups) {
            $frameworkText = $group.GetAttribute('targetFramework')
            $framework = if ([string]::IsNullOrWhiteSpace($frameworkText)) { [NuGet.Frameworks.NuGetFramework]::AnyFramework } else { [NuGet.Frameworks.NuGetFramework]::Parse($frameworkText) }
            if ($framework.IsUnsupported -or $frameworks.Contains($framework)) { throw "Invalid or duplicate dependency framework group '$frameworkText'." }
            $frameworks.Add($framework)
        }
        $nearest = [NuGet.Frameworks.FrameworkReducer]::new().GetNearest($target, $frameworks)
        if ($null -eq $nearest) { throw "Hexalith.EventStore.Aspire/$($snapshot.eventStoreVersion) has no dependency framework group applicable to $TargetFramework." }
        $dependencySet = $groups[$frameworks.IndexOf($nearest)]
    }
    $dependencies = @($dependencySet.SelectNodes("*[local-name()='dependency' and translate(@id,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz')='communitytoolkit.aspire.hosting.dapr']"))
    if ($dependencies.Count -eq 0) { throw "Hexalith.EventStore.Aspire/$($snapshot.eventStoreVersion) metadata is missing CommunityToolkit.Aspire.Hosting.Dapr dependency requirements." }
    $ranges = @($dependencies | ForEach-Object { $_.GetAttribute('version') } | Sort-Object -Unique)
    if ($dependencies.Count -ne 1 -or $ranges.Count -ne 1 -or [string]::IsNullOrWhiteSpace($ranges[0])) { throw "Hexalith.EventStore.Aspire/$($snapshot.eventStoreVersion) metadata has missing or conflicting CommunityToolkit.Aspire.Hosting.Dapr dependency ranges." }
    $range = $null
    if ($ranges[0] -cne $ranges[0].Trim() -or -not [NuGet.Versioning.VersionRange]::TryParse($ranges[0], [ref] $range)) { throw "Hexalith.EventStore.Aspire/$($snapshot.eventStoreVersion) has invalid CommunityToolkit.Aspire.Hosting.Dapr dependency range '$($ranges[0])'." }
    $snapshot.eventStoreHostingDaprRange = $ranges[0]
    if (-not $range.Satisfies([NuGet.Versioning.NuGetVersion]::Parse($snapshot.aspireHostingDaprVersion))) {
        throw "Hexalith.EventStore.Aspire/$($snapshot.eventStoreVersion) requires CommunityToolkit.Aspire.Hosting.Dapr range '$($ranges[0])' for $TargetFramework; selected CommunityToolkit.Aspire.Hosting.Dapr/$($snapshot.aspireHostingDaprVersion) is incompatible."
    }
    $content = ($snapshot | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n"
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))
    # Avoid touching the resource when unchanged, so incremental compilation remains deterministic.
    if (-not [IO.File]::Exists($OutputPath) -or [IO.File]::ReadAllText($OutputPath) -cne $content) {
        [IO.File]::WriteAllText($OutputPath, $content, [Text.UTF8Encoding]::new($false))
    }
    Write-Output "Platform version catalog validated: Hexalith.EventStore.Aspire/$($snapshot.eventStoreVersion), CommunityToolkit.Aspire.Hosting.Dapr/$($snapshot.aspireHostingDaprVersion), required range $($ranges[0]) for $TargetFramework."
}
catch {
    [Console]::Error.WriteLine("Platform version catalog validation failed: $($_.Exception.GetBaseException().Message)")
    exit 1
}
