[CmdletBinding()]
param([switch] $SkipRebuild)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "hexalith-catalog-tests-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $temporaryRoot
$writer = Join-Path $PSScriptRoot 'write-platform-version-catalog.ps1'
$authoritativeCatalogText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Props/Directory.Packages.props'))
# Fixed synthetic input keeps historical regressions independent of future catalog-only changes.
$catalogText = @'
<Project><PropertyGroup>
<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
<HexalithEventStoreVersion>3.117.1</HexalithEventStoreVersion>
<HexalithFrontComposerVersion>4.6.0</HexalithFrontComposerVersion>
<HexalithAspireHostingDaprVersion>13.6.0-preview.1.261001-0243</HexalithAspireHostingDaprVersion>
<HexalithAspireHostingDaprVersion Condition="'$(MSBuildProjectName)' == 'Hexalith.Folders.Aspire'">13.0.0</HexalithAspireHostingDaprVersion>
<HexalithAspireAppHostSdkVersion>13.6.0</HexalithAspireAppHostSdkVersion>
<HexalithDaprRuntimeVersion>1.18.2</HexalithDaprRuntimeVersion>
<HexalithDaprCliVersion>1.18.0</HexalithDaprCliVersion>
<HexalithRedisImage>docker.io/library/redis</HexalithRedisImage>
<HexalithRedisImageTag>7.4-alpine</HexalithRedisImageTag>
<HexalithRedisImageDigest>sha256:ff02b58f971e7d7d156a1267e283fcbbeee91773b6aa36c49dac28ecfe28eadf</HexalithRedisImageDigest>
</PropertyGroup><ItemGroup>
<PackageVersion Include="Hexalith.EventStore.Aspire" Version="$(HexalithEventStoreVersion)" />
<PackageVersion Include="Hexalith.FrontComposer.Shell" Version="$(HexalithFrontComposerVersion)" />
<PackageVersion Include="Dapr.Client" Version="1.18.10" />
<PackageVersion Include="Aspire.Hosting" Version="13.6.1" />
<PackageVersion Include="CommunityToolkit.Aspire.Hosting.Dapr" Version="$(HexalithAspireHostingDaprVersion)" />
</ItemGroup></Project>
'@
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$count = 0

function Test-Catalog {
    param([string] $Name, [string] $Catalog, [string] $EventStore, [string] $Range, [int] $ExpectedExit, [string[]] $ExpectedText = @(), [string] $Metadata = '', [string] $Configuration = 'Debug', [string] $TargetFramework = 'net10.0')
    $directory = Join-Path $temporaryRoot $Name
    $null = New-Item -ItemType Directory -Path $directory
    $catalogPath = Join-Path $directory 'Directory.Packages.props'
    $metadataPath = Join-Path $directory 'eventstore.nuspec'
    $outputPath = Join-Path $directory 'catalog.json'
    [IO.File]::WriteAllText($catalogPath, $Catalog)
    if (-not $Metadata) { $Metadata = "<package><metadata><id>Hexalith.EventStore.Aspire</id><version>$EventStore</version><dependencies><group targetFramework=`"net10.0`"><dependency id=`"CommunityToolkit.Aspire.Hosting.Dapr`" version=`"$Range`" /></group></dependencies></metadata></package>" }
    [IO.File]::WriteAllText($metadataPath, $Metadata)
    $result = @(& $pwshExecutable -NoProfile -File $writer -CatalogPath $catalogPath -NuspecPath $metadataPath -OutputPath $outputPath -Configuration $Configuration -TargetFramework $TargetFramework 2>&1)
    $exitCode = $LASTEXITCODE
    $text = $result -join "`n"
    if ($exitCode -ne $ExpectedExit) { throw "$Name exited $exitCode, expected $ExpectedExit. $text" }
    foreach ($expected in $ExpectedText) { if (-not $text.Contains($expected, [StringComparison]::Ordinal)) { throw "$Name omitted '$expected': $text" } }
    if ($ExpectedExit -eq 0) {
        $snapshot = [IO.File]::ReadAllText($outputPath)
        $timestamp = [IO.File]::GetLastWriteTimeUtc($outputPath)
        $null = & $pwshExecutable -NoProfile -File $writer -CatalogPath $catalogPath -NuspecPath $metadataPath -OutputPath $outputPath -Configuration $Configuration -TargetFramework $TargetFramework
        if ($LASTEXITCODE -ne 0 -or [IO.File]::ReadAllText($outputPath) -cne $snapshot -or [IO.File]::GetLastWriteTimeUtc($outputPath) -ne $timestamp) { throw "$Name is not deterministic/incremental." }
    }
    elseif (Test-Path -LiteralPath $outputPath) { throw "$Name produced a snapshot on failure." }
    $script:count++
    Write-Output "PASS: $Name"
}

try {
    $actual = @(& $pwshExecutable -NoProfile -File (Join-Path $PSScriptRoot 'validate-platform-version-catalog.ps1') 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Current authoritative metadata validation failed: $actual" }
    $count++
    Write-Output 'PASS: current authoritative catalog with real selected package metadata'
    Test-Catalog 'current-pair' $catalogText '3.117.1' '13.6.0-preview.1.261001-0243' 0
    $historical = $catalogText.Replace('3.117.1', '3.109.0').Replace('13.6.0-preview.1.261001-0243', '13.5.1-beta.767')
    Test-Catalog 'historical-3-109-beta-767' $historical '3.109.0' '[13.5.1-beta.767]' 0
    Test-Catalog 'historical-preview-too-old' ($historical.Replace('13.5.1-beta.767', '13.5.1-beta.766')) '3.109.0' '[13.5.1-beta.767,13.6.0)' 1 @('Hexalith.EventStore.Aspire/3.109.0', 'CommunityToolkit.Aspire.Hosting.Dapr/13.5.1-beta.766', '[13.5.1-beta.767,13.6.0)')
    Test-Catalog 'eventstore-pin-changes-required-range' ($catalogText.Replace('3.117.1', '3.118.0')) '3.118.0' '[14.0.0,15.0.0)' 1 @('Hexalith.EventStore.Aspire/3.118.0', 'CommunityToolkit.Aspire.Hosting.Dapr/13.6.0-preview.1.261001-0243', '[14.0.0,15.0.0)')
    Test-Catalog 'toolkit-pin-outside-selected-range' ($catalogText.Replace('13.6.0-preview.1.261001-0243', '14.0.0')) '3.117.1' '[13.6.0-preview.1.261001-0243,14.0.0)' 1 @('Hexalith.EventStore.Aspire/3.117.1', 'CommunityToolkit.Aspire.Hosting.Dapr/14.0.0', '[13.6.0-preview.1.261001-0243,14.0.0)')
    Test-Catalog 'both-hosting-pins-compatible' ($catalogText.Replace('3.117.1', '3.118.0').Replace('13.6.0-preview.1.261001-0243', '14.0.0')) '3.118.0' '[14.0.0,15.0.0)' 0
    Test-Catalog 'nuget-beta-numeric-ordering' ($historical.Replace('13.5.1-beta.767', '13.5.1-beta.1000')) '3.109.0' '[13.5.1-beta.767,13.6.0)' 0
    Test-Catalog 'nuget-metadata-is-ignored' ($historical.Replace('13.5.1-beta.767', '13.5.1-beta.767+hash')) '3.109.0' '[13.5.1-beta.767]' 0
    foreach ($id in @('Hexalith.EventStore.Aspire', 'Hexalith.FrontComposer.Shell', 'Dapr.Client', 'Aspire.Hosting', 'CommunityToolkit.Aspire.Hosting.Dapr')) {
        $missing = [regex]::Replace($catalogText, '<PackageVersion Include="' + [regex]::Escape($id) + '"[^>]*/>', '')
        Test-Catalog "missing-$id" $missing '3.117.1' '13.6.0-preview.1.261001-0243' 1 @($id)
        $duplicate = $catalogText.Replace('</ItemGroup>', "<PackageVersion Include=`"$($id.ToLowerInvariant())`" Version=`"1.0.0`" /></ItemGroup>")
        Test-Catalog "duplicate-$id" $duplicate '3.117.1' '13.6.0-preview.1.261001-0243' 1 @($id)
    }
    foreach ($property in @('HexalithAspireAppHostSdkVersion', 'HexalithDaprRuntimeVersion', 'HexalithDaprCliVersion', 'HexalithRedisImage', 'HexalithRedisImageTag', 'HexalithRedisImageDigest')) {
        $missing = [regex]::Replace($catalogText, '<' + $property + '>[^<]*</' + $property + '>', '')
        Test-Catalog "missing-$property" $missing '3.117.1' '13.6.0-preview.1.261001-0243' 1 @($property)
    }
    foreach ($property in @('HexalithAspireAppHostSdkVersion', 'HexalithDaprRuntimeVersion', 'HexalithDaprCliVersion', 'HexalithRedisImage', 'HexalithRedisImageTag', 'HexalithRedisImageDigest', 'HexalithEventStoreVersion', 'HexalithFrontComposerVersion', 'HexalithAspireHostingDaprVersion')) {
        Test-Catalog "duplicate-property-$property" ($catalogText.Replace('</PropertyGroup>', "<$property>0.0.1</$property></PropertyGroup>")) '3.117.1' '13.*' 1 @($property, 'duplicate unconditional')
    }
    foreach ($property in @('HexalithEventStoreVersion', 'HexalithFrontComposerVersion')) {
        $conditionalDefault = "<$property Condition=`"'`$($property)' == ''`">0.0.1</$property>"
        Test-Catalog "duplicate-self-default-$property" ($catalogText.Replace('</PropertyGroup>', "$conditionalDefault</PropertyGroup>")) '3.117.1' '13.*' 1 @($property, 'duplicate unconditional')
        $original = "<$property>" + $(if ($property -eq 'HexalithEventStoreVersion') { '3.117.1' } else { '4.6.0' }) + "</$property>"
        $realForm = $catalogText.Replace($original, "<$property Condition=`"'`$($property)' == ''`">" + $(if ($property -eq 'HexalithEventStoreVersion') { '3.117.1' } else { '4.6.0' }) + "</$property>")
        Test-Catalog "default-then-unconditional-$property" ($realForm.Replace('</PropertyGroup>', "<$property>0.0.1</$property></PropertyGroup>")) '3.117.1' '13.*' 1 @($property, 'duplicate unconditional')
    }
    Test-Catalog 'intentional-conditional-property' ($catalogText.Replace('</PropertyGroup>', '<HexalithAspireAppHostSdkVersion Condition="false">99.0.0</HexalithAspireAppHostSdkVersion></PropertyGroup>')) '3.117.1' '13.*' 0
    foreach ($image in @('Docker.io/library/redis', 'docker.io//library/redis', '/redis', 'docker.io/library/', 'docker.io/library/redis..bad', 'docker.io/library/redis___bad', 'docker.io/library/redis.-bad', 'docker.io/library/redis.')) {
        Test-Catalog ('invalid-image-' + $count) ($catalogText.Replace('docker.io/library/redis', $image)) '3.117.1' '13.*' 1 @('HexalithRedisImage')
    }
    foreach ($image in @('docker.io/library/redis.good', 'docker.io/library/redis_good', 'docker.io/library/redis__good', 'docker.io/library/redis---good')) {
        Test-Catalog ('valid-image-' + $count) ($catalogText.Replace('docker.io/library/redis', $image)) '3.117.1' '13.*' 0
    }
    Test-Catalog 'wildcard-range' ($catalogText.Replace('13.6.0-preview.1.261001-0243', '13.6.0')) '3.117.1' '13.*' 0
    Test-Catalog 'short-range-bounds' $catalogText '3.117.1' '[13,14)' 0
    Test-Catalog 'inverted-range' $catalogText '3.117.1' '[14.0.0,13.0.0]' 1 @('invalid', 'CommunityToolkit.Aspire.Hosting.Dapr')
    Test-Catalog 'nonnumeric-range-bound' $catalogText '3.117.1' '[bad,14)' 1 @('invalid', 'CommunityToolkit.Aspire.Hosting.Dapr')
    Test-Catalog 'numeric-prerelease-leading-zero' ($catalogText.Replace('<HexalithAspireAppHostSdkVersion>13.6.0', '<HexalithAspireAppHostSdkVersion>13.6.0-beta.01')) '3.117.1' '13.*' 1 @('HexalithAspireAppHostSdkVersion', 'malformed')
    $misplaced = '<package><metadata><id>Hexalith.EventStore.Aspire</id><version>3.117.1</version><description><dependency id="CommunityToolkit.Aspire.Hosting.Dapr" version="13.*" /></description></metadata></package>'
    Test-Catalog 'misplaced-dependency' $catalogText '3.117.1' '' 1 @('missing', 'CommunityToolkit.Aspire.Hosting.Dapr') $misplaced
    $groups = '<package><metadata><id>Hexalith.EventStore.Aspire</id><version>3.117.1</version><dependencies><group targetFramework="net8.0"><dependency id="CommunityToolkit.Aspire.Hosting.Dapr" version="[12,13)" /></group><group targetFramework="net10.0"><dependency id="CommunityToolkit.Aspire.Hosting.Dapr" version="[13,14)" /></group></dependencies></metadata></package>'
    Test-Catalog 'different-valid-framework-ranges' $catalogText '3.117.1' '' 0 @() $groups
    Test-Catalog 'net8-selects-incompatible-net8-range' $catalogText '3.117.1' '' 1 @('[12,13)', 'net8.0', 'incompatible') $groups 'Debug' 'net8.0'
    Test-Catalog 'net8-selects-compatible-net8-range' $catalogText '3.117.1' '' 0 @() ($groups.Replace('[12,13)', '[13,14)')) 'Debug' 'net8.0'
    $net8Snapshot = Get-Content (Join-Path $temporaryRoot 'net8-selects-compatible-net8-range/catalog.json') -Raw | ConvertFrom-Json
    if ($net8Snapshot.eventStoreHostingDaprRange -cne '[13,14)') { throw 'The supplied net8.0 dependency range was not serialized.' }
    Test-Catalog 'net8-has-no-compatible-group' $catalogText '3.117.1' '[13,14)' 1 @('applicable to net8.0') '' 'Debug' 'net8.0'
    Test-Catalog 'invalid-supplied-framework' $catalogText '3.117.1' '[13,14)' 1 @('TargetFramework', 'invalid') '' 'Debug' 'bad-framework'
    Test-Catalog 'nearest-compatible-framework' $catalogText '3.117.1' '' 0 @() ($groups.Replace('net10.0', 'net9.0'))
    Test-Catalog 'effective-framework-missing-dependency' $catalogText '3.117.1' '' 1 @('missing', 'CommunityToolkit.Aspire.Hosting.Dapr') ($groups.Replace('version="[13,14)"', 'version="[13,14)"').Replace('<group targetFramework="net10.0"><dependency id="CommunityToolkit.Aspire.Hosting.Dapr" version="[13,14)" /></group>', '<group targetFramework="net10.0" />'))
    Test-Catalog 'invalid-framework-group' $catalogText '3.117.1' '' 1 @('Invalid', 'framework') ($groups.Replace('net8.0', 'bad-framework'))

    Test-Catalog 'malformed-sdk' ($catalogText.Replace('<HexalithAspireAppHostSdkVersion>13.6.0', '<HexalithAspireAppHostSdkVersion>bad')) '3.117.1' '13.6.0-preview.1.261001-0243' 1 @('HexalithAspireAppHostSdkVersion', 'bad')
    Test-Catalog 'malformed-package' ($catalogText.Replace('Include="Dapr.Client" Version="1.18.10"', 'Include="Dapr.Client" Version="bad"')) '3.117.1' '13.6.0-preview.1.261001-0243' 1 @('Dapr.Client', 'bad')
    Test-Catalog 'missing-dependency-metadata' $catalogText '3.117.1' '' 1 @('missing', 'CommunityToolkit.Aspire.Hosting.Dapr') '<package><metadata><id>Hexalith.EventStore.Aspire</id><version>3.117.1</version></metadata></package>'
    Test-Catalog 'invalid-dependency-range' $catalogText '3.117.1' 'bad' 1 @('invalid', 'CommunityToolkit.Aspire.Hosting.Dapr', 'bad')
    Test-Catalog 'wrong-metadata-identity' $catalogText '3.117.1' '' 1 @('does not identify', '3.117.1') '<package><metadata><id>Hexalith.EventStore.Aspire</id><version>3.109.0</version></metadata></package>'

    $identityMetadata = '<package><metadata><id>Hexalith.EventStore.Aspire</id><version>3.117.1</version><dependencies><dependency id="CommunityToolkit.Aspire.Hosting.Dapr" version="[13,14)" /></dependencies></metadata></package>'
    Test-Catalog 'missing-metadata-element' $catalogText '3.117.1' '' 1 @('exactly one metadata element') '<package />'
    Test-Catalog 'duplicate-metadata-elements' $catalogText '3.117.1' '' 1 @('exactly one metadata element') ($identityMetadata.Replace('</package>', '<metadata><id>Wrong.Package</id><version>3.117.1</version></metadata></package>'))
    Test-Catalog 'missing-metadata-id' $catalogText '3.117.1' '' 1 @('exactly one ID and version') ($identityMetadata.Replace('<id>Hexalith.EventStore.Aspire</id>', ''))
    Test-Catalog 'duplicate-metadata-ids' $catalogText '3.117.1' '' 1 @('exactly one ID and version') ($identityMetadata.Replace('</id>', '</id><id>Wrong.Package</id>'))
    Test-Catalog 'missing-metadata-version' $catalogText '3.117.1' '' 1 @('exactly one ID and version') ($identityMetadata.Replace('<version>3.117.1</version>', ''))
    Test-Catalog 'duplicate-metadata-versions' $catalogText '3.117.1' '' 1 @('exactly one ID and version') ($identityMetadata.Replace('</version>', '</version><version>3.117.1</version>'))

    $wrapperOutput = @(& $pwshExecutable -NoProfile -File (Join-Path $PSScriptRoot 'validate-platform-version-catalog.ps1') -NuspecPath (Join-Path $temporaryRoot 'missing-metadata.nuspec') 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 1 -or -not $wrapperOutput.Contains('Missing dependency metadata', [StringComparison]::Ordinal)) { throw "The release validation wrapper did not propagate failure: $wrapperOutput" }
    $count++
    Write-Output 'PASS: release validation wrapper propagates metadata failure'

    $priorEnvironment = [Environment]::GetEnvironmentVariable('HexalithFrontComposerVersion')
    try {
        $environmentCatalog = $catalogText.Replace('<HexalithFrontComposerVersion>', '<HexalithFrontComposerVersion Condition="''$(HexalithFrontComposerVersion)'' == ''''">')
        [Environment]::SetEnvironmentVariable('HexalithFrontComposerVersion', '99.0.0')
        Test-Catalog 'environment-override-rejected' $environmentCatalog '3.117.1' '[13,14)' 1 @('Hexalith.FrontComposer.Shell', 'inherited from environment', '99.0.0', '4.6.0')
        [Environment]::SetEnvironmentVariable('HexalithFrontComposerVersion', '4.6.0')
        Test-Catalog 'matching-environment-selection' $environmentCatalog '3.117.1' '[13,14)' 0
        [Environment]::SetEnvironmentVariable('HexalithFrontComposerVersion', '99.0.0')
        Test-Catalog 'overwritten-environment-property-is-not-effective-drift' $catalogText '3.117.1' '[13,14)' 0
    }
    finally { [Environment]::SetEnvironmentVariable('HexalithFrontComposerVersion', $priorEnvironment) }
    $conditional = $catalogText.Replace('</Project>', '<PropertyGroup Condition="''$(Configuration)'' == ''Release'' and ''$(TargetFramework)'' == ''net10.0''"><HexalithDaprRuntimeVersion>2.3.4</HexalithDaprRuntimeVersion></PropertyGroup></Project>')
    Test-Catalog 'conditional-release-context' $conditional '3.117.1' '[13,14)' 0 @() '' 'Release' 'net10.0'
    $contextSnapshot = Get-Content (Join-Path $temporaryRoot 'conditional-release-context/catalog.json') -Raw | ConvertFrom-Json
    if ($contextSnapshot.daprRuntimeVersion -cne '2.3.4') { throw 'Configuration/TargetFramework context was not carried into standalone catalog generation.' }

    # Prove evaluated Update/property selections and preserve the project-scoped Folders exception.
    $evaluated = $catalogText.Replace('</Project>', '<ItemGroup><PackageVersion Update="Dapr.Client" Version="$(ChangedDaprSdk)" /></ItemGroup><PropertyGroup><ChangedDaprSdk>2.3.4</ChangedDaprSdk></PropertyGroup></Project>')
    Test-Catalog 'evaluated-item-update' $evaluated '3.117.1' '13.6.0-preview.1.261001-0243' 0
    $snapshot = Get-Content (Join-Path $temporaryRoot 'evaluated-item-update/catalog.json') -Raw | ConvertFrom-Json
    if ($snapshot.daprSdkVersion -cne '2.3.4') { throw 'The generated snapshot did not use evaluated CPM items.' }
    # MSBuildProjectName is reserved; evaluate via a project actually named for the exception.
    $foldersProject = Join-Path $temporaryRoot 'Hexalith.Folders.Aspire.proj'
    $escapedCatalog = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot '../Props/Directory.Packages.props'))
    [IO.File]::WriteAllText($foldersProject, "<Project><Import Project=`"$escapedCatalog`" /></Project>")
    $folders = @(& dotnet msbuild $foldersProject -nologo -getItem:PackageVersion) -join "`n" | ConvertFrom-Json
    $foldersPin = @($folders.Items.PackageVersion | Where-Object Identity -eq 'CommunityToolkit.Aspire.Hosting.Dapr')[0].Version
    if ($foldersPin -cne '13.0.0') { throw 'The Folders.Aspire exception changed.' }
    $count++

    if (-not $SkipRebuild) {
        # Build the real tooling sources in an isolated minimal project, never mutating the checkout catalog.
        $copyRoot = Join-Path $temporaryRoot 'rebuild'
        $tooling = Join-Path $copyRoot 'src/libraries/Hexalith.Builds.Tooling'
        $null = New-Item -ItemType Directory -Path $tooling -Force
        $sourceTooling = Join-Path $PSScriptRoot '../src/libraries/Hexalith.Builds.Tooling'
        foreach ($file in Get-ChildItem -LiteralPath $sourceTooling -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
            $target = Join-Path $tooling ([IO.Path]::GetRelativePath($sourceTooling, $file.FullName))
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
        foreach ($directory in @('Tools', 'Props', 'schemas')) { $null = New-Item -ItemType Directory -Path (Join-Path $copyRoot $directory) -Force }
        Copy-Item -LiteralPath $writer -Destination (Join-Path $copyRoot 'Tools/write-platform-version-catalog.ps1')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Props/Directory.Packages.props') -Destination (Join-Path $copyRoot 'Props/Directory.Packages.props')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../schemas/hexalith.module-manifest.v2.json') -Destination (Join-Path $copyRoot 'schemas/hexalith.module-manifest.v2.json')
        [IO.File]::WriteAllText((Join-Path $copyRoot 'Directory.Packages.props'), '<Project><Import Project="Props/Directory.Packages.props" /></Project>')
        [IO.File]::WriteAllText((Join-Path $copyRoot 'Directory.Build.props'), '<Project><PropertyGroup><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies></PropertyGroup></Project>')
        $project = Join-Path $tooling 'Hexalith.Builds.Tooling.csproj'
        $log = @(& dotnet build $project -c Debug -m:1 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Baseline tooling rebuild failed: $log" }
        $catalogCopy = Join-Path $copyRoot 'Props/Directory.Packages.props'
        [xml] $alternateCatalog = $authoritativeCatalogText
        $alternateCatalog.SelectSingleNode('/Project/PropertyGroup/HexalithDaprRuntimeVersion').InnerText = '2.3.4'
        $alternateCatalogPath = Join-Path $copyRoot 'alternate-catalog.props'
        [IO.File]::WriteAllText($alternateCatalogPath, $alternateCatalog.OuterXml)
        $log = @(& dotnet build $project -c Debug -m:1 "-p:PlatformVersionCatalogSource=$alternateCatalogPath" '-p:HexalithDaprRuntimeVersion=2.3.4' 2>&1) -join "`n"
        if ($LASTEXITCODE -eq 0 -or -not $log.Contains('inconsistent build selection', [StringComparison]::Ordinal) -or -not $log.Contains('HexalithDaprRuntimeVersion', [StringComparison]::Ordinal)) { throw "Alternate production authority with matching runtime override was not rejected: $log" }
        $count++
        Write-Output 'PASS: alternate production catalog with matching override cannot replace the owning authority'
        [xml] $changedCatalog = $authoritativeCatalogText
        $changedCatalog.SelectSingleNode('/Project/PropertyGroup/HexalithDaprRuntimeVersion').InnerText = '2.3.4'
        $changedCatalog.SelectSingleNode('/Project/PropertyGroup/HexalithFrontComposerVersion').InnerText = '4.6.9'
        $changedCatalog.SelectSingleNode('/Project/PropertyGroup/HexalithDaprCliVersion').InnerText = '2.3.5'
        $changed = $changedCatalog.OuterXml
        [IO.File]::WriteAllText($catalogCopy, $changed)
        $log = @(& dotnet build $project -c Debug -m:1 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Changed catalog tooling rebuild failed: $log" }
        $absoluteIntermediate = Join-Path $temporaryRoot 'absolute-intermediate'
        foreach ($attempt in @(1, 2)) {
            $log = @(& dotnet build $project -c Debug -m:1 "-p:IntermediateOutputPath=$absoluteIntermediate/" 2>&1) -join "`n"
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $absoluteIntermediate 'platform-version-catalog.json'))) { throw "Absolute intermediate path build $attempt failed: $log" }
        }
        $count++
        Write-Output 'PASS: relative and absolute intermediate paths, including incremental resources'
        foreach ($override in @('HexalithFrontComposerVersion=0.0.7', 'HexalithAspireAppHostSdkVersion=99.0.0', 'HexalithDaprRuntimeVersion=99.0.0')) {
            $log = @(& dotnet build $project -c Debug -m:1 "-p:$override" 2>&1) -join "`n"
            if ($LASTEXITCODE -eq 0 -or -not $log.Contains('inconsistent build selection', [StringComparison]::Ordinal)) { throw "Controlled build override was not rejected: $override. $log" }
            $count++
        }
        $log = @(& dotnet build $project -c Debug -m:1 '-p:HexalithFrontComposerVersion=4.6.9' 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Matching controlled build selection failed: $log" }
        $count++
        Write-Output 'PASS: controlled overrides reject divergence and permit matching selections'
        $priorEnvironment = [Environment]::GetEnvironmentVariable('HexalithFrontComposerVersion')
        try {
            [Environment]::SetEnvironmentVariable('HexalithFrontComposerVersion', '0.0.7')
            $log = @(& dotnet build $project -c Debug -m:1 2>&1) -join "`n"
            if ($LASTEXITCODE -eq 0 -or -not $log.Contains('inconsistent build selection', [StringComparison]::Ordinal)) { throw "Inherited controlled build override was not rejected: $log" }
            $count++
        }
        finally { [Environment]::SetEnvironmentVariable('HexalithFrontComposerVersion', $priorEnvironment) }
        $conditionalBuildCatalog = $changed.Replace('</Project>', '<PropertyGroup Condition="''$(Configuration)'' == ''Debug'' and ''$(TargetFramework)'' == ''net10.0''"><HexalithDaprRuntimeVersion>2.3.6</HexalithDaprRuntimeVersion></PropertyGroup></Project>')
        [IO.File]::WriteAllText($catalogCopy, $conditionalBuildCatalog)
        $log = @(& dotnet build $project -c Debug -m:1 -p:TargetFramework=net10.0 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Legitimate conditional controlled build selection failed: $log" }
        $conditionalResource = Get-Content (Join-Path $tooling 'obj/Debug/net10.0/platform-version-catalog.json') -Raw | ConvertFrom-Json
        if ($conditionalResource.daprRuntimeVersion -cne '2.3.6') { throw 'Build generation omitted the Configuration/TargetFramework context.' }
        $count++
        [IO.File]::WriteAllText($catalogCopy, $changed)
        $log = @(& dotnet build $project -c Debug -m:1 2>&1) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Restore of isolated catalog after conditional regression failed: $log" }
        $assemblyPath = Join-Path $tooling 'bin/Debug/net10.0/Hexalith.Builds.Tooling.dll'
        $null = [Reflection.Assembly]::LoadFrom((Join-Path ([IO.Path]::GetDirectoryName($assemblyPath)) 'NuGet.Versioning.dll'))
        $assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
        $pins = $assembly.GetType('Hexalith.Builds.Tooling.Manifest.SupportedPlatformPins')
        if ($pins.GetProperty('DaprRuntimeVersion').GetValue($null) -cne '2.3.4' -or $pins.GetProperty('FrontComposerVersion').GetValue($null) -cne '4.6.9') { throw 'Rebuilt pin consumers did not reflect the catalog mutation.' }
        $record = $assembly.GetType('Hexalith.Builds.Tooling.Manifest.PlatformVersionCatalog').GetProperty('Current').GetValue($null)
        if ($record.DaprCliVersion -cne '2.3.5') { throw 'The rebuilt toolchain did not reflect the catalog mutation.' }
        $snapshotType = $assembly.GetType('Hexalith.Builds.Tooling.Manifest.PlatformVersionCatalog')
        foreach ($generated in Get-ChildItem -LiteralPath $temporaryRoot -Filter 'catalog.json' -Recurse) {
            $null = $snapshotType.GetMethod('Parse').Invoke($null, @([IO.File]::ReadAllText($generated.FullName)))
        }
        $count++
        Write-Output 'PASS: all successful generated snapshots load with offline NuGet semantics'
        $count++
        Write-Output 'PASS: isolated catalog mutation and incremental rebuild'
    }
    Write-Output "Platform version catalog tests passed: $count scenarios. No Platform acceptance granted."
}
finally {
    # Loaded test assemblies remain file-locked on Windows until this PowerShell process exits.
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}
