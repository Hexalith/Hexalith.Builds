[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validatorPath = Join-Path $PSScriptRoot 'validate-consumer-package-authority.ps1'
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "consumer-package-authority-$([Guid]::NewGuid().ToString('N'))"
$catalogPath = Join-Path $temporaryRoot 'shared/Directory.Packages.props'
$failures = [System.Collections.Generic.List[string]]::new()
$scenarioCount = 0

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Content
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-ConsumerFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [AllowEmptyString()]
        [string] $WrapperContent = '',

        [AllowEmptyString()]
        [string] $ProjectContent = '',

        [AllowEmptyString()]
        [string] $BuildPropsContent = ''
    )

    $root = Join-Path $temporaryRoot $Name
    if ([string]::IsNullOrWhiteSpace($WrapperContent)) {
        $WrapperContent = @"
<Project>
  <Import Project="$catalogPath" />
</Project>
"@
    }

    if ([string]::IsNullOrWhiteSpace($ProjectContent)) {
        $ProjectContent = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageVersion>1.0.0</PackageVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Fixture.Package" PrivateAssets="all" IncludeAssets="runtime; build" />
    <PackageReference Include="Implicit.Package" IsImplicitlyDefined="true" />
  </ItemGroup>
</Project>
'@
    }

    Write-Utf8File -Path (Join-Path $root 'Directory.Packages.props') -Content $WrapperContent
    Write-Utf8File -Path (Join-Path $root 'Fixture.csproj') -Content $ProjectContent
    if (-not [string]::IsNullOrWhiteSpace($BuildPropsContent)) {
        Write-Utf8File -Path (Join-Path $root 'Directory.Build.props') -Content $BuildPropsContent
    }

    return $root
}

function Test-Scenario {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedExitCode,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedOutput
    )

    $script:scenarioCount++
    $output = @(
        & $pwshExecutable -NoLogo -NoProfile -File $validatorPath `
            -RepositoryRoot $RepositoryRoot -CatalogPath $catalogPath 2>&1
    )
    $exitCode = $LASTEXITCODE
    $outputText = [string]::Join("`n", @($output | ForEach-Object { [string] $_ }))
    if ($exitCode -ne $ExpectedExitCode) {
        $script:failures.Add(
            "$Name expected exit code $ExpectedExitCode but received $exitCode. Output: $outputText"
        )
        return
    }

    if ($outputText -notlike "*$ExpectedOutput*") {
        $script:failures.Add("$Name output did not contain '$ExpectedOutput'. Output: $outputText")
    }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    Write-Utf8File -Path $catalogPath -Content @'
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
    <HexalithVersionsLoaded>true</HexalithVersionsLoaded>
    <HexalithCommonsVersion Condition="'$(HexalithCommonsVersion)' == ''">2.28.2</HexalithCommonsVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Fixture.Package" Version="1.2.3" />
    <PackageVersion Include="Hexalith.Commons" Version="$(HexalithCommonsVersion)" />
  </ItemGroup>
</Project>
'@

    $validRoot = New-ConsumerFixture -Name 'valid'
    Test-Scenario -Name 'Valid version-free consumer' -RepositoryRoot $validRoot -ExpectedExitCode 0 `
        -ExpectedOutput 'consumer package authority validation passed'

    $includeRoot = New-ConsumerFixture -Name 'package-version-include' -BuildPropsContent @'
<Project><ItemGroup><PackageVersion Include="Local.Package" Version="1.0.0" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'PackageVersion Include' -RepositoryRoot $includeRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'consumer PackageVersion Include'

    $updateRoot = New-ConsumerFixture -Name 'package-version-update' -BuildPropsContent @'
<Project><ItemGroup><PackageVersion Update="Fixture.Package" Version="2.0.0" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Imported PackageVersion Update' -RepositoryRoot $updateRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'consumer PackageVersion Update'

    $attributeRoot = New-ConsumerFixture -Name 'package-reference-version' -ProjectContent @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Fixture.Package" Version="1.2.3" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'PackageReference Version attribute' -RepositoryRoot $attributeRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'PackageReference Version'

    $nestedRoot = New-ConsumerFixture -Name 'package-reference-nested-version' -ProjectContent @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Fixture.Package"><Version>1.2.3</Version></PackageReference></ItemGroup></Project>
'@
    Test-Scenario -Name 'PackageReference nested Version' -RepositoryRoot $nestedRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'PackageReference nested Version'

    $overrideRoot = New-ConsumerFixture -Name 'version-override' -ProjectContent @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Fixture.Package" VersionOverride="2.0.0" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'PackageReference VersionOverride' -RepositoryRoot $overrideRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'PackageReference VersionOverride'

    $nestedOverrideRoot = New-ConsumerFixture -Name 'nested-version-override' -ProjectContent @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Fixture.Package"><VersionOverride>2.0.0</VersionOverride></PackageReference></ItemGroup></Project>
'@
    Test-Scenario -Name 'PackageReference nested VersionOverride' -RepositoryRoot $nestedOverrideRoot `
        -ExpectedExitCode 1 -ExpectedOutput 'PackageReference nested VersionOverride'

    $globalRoot = New-ConsumerFixture -Name 'global-package-reference' -BuildPropsContent @'
<Project><ItemGroup><GlobalPackageReference Include="Fixture.Package"><Version>1.2.3</Version></GlobalPackageReference></ItemGroup></Project>
'@
    Test-Scenario -Name 'GlobalPackageReference version' -RepositoryRoot $globalRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'GlobalPackageReference nested Version'

    $globalAttributeRoot = New-ConsumerFixture -Name 'global-package-reference-attribute' -BuildPropsContent @'
<Project><ItemGroup><GlobalPackageReference Include="Fixture.Package" Version="1.2.3" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'GlobalPackageReference Version attribute' -RepositoryRoot $globalAttributeRoot `
        -ExpectedExitCode 1 -ExpectedOutput 'GlobalPackageReference Version'

    $optOutRoot = New-ConsumerFixture -Name 'cpm-opt-out' -ProjectContent @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>
'@
    Test-Scenario -Name 'CPM opt-out' -RepositoryRoot $optOutRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'ManagePackageVersionsCentrally=false'

    $overrideOptInRoot = New-ConsumerFixture -Name 'override-opt-in' -ProjectContent @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><CentralPackageVersionOverrideEnabled>true</CentralPackageVersionOverrideEnabled></PropertyGroup></Project>
'@
    Test-Scenario -Name 'Override opt-in' -RepositoryRoot $overrideOptInRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'enables CentralPackageVersionOverrideEnabled'

    $propertyRoot = New-ConsumerFixture -Name 'shared-version-property' -WrapperContent @"
<Project>
  <PropertyGroup><HexalithCommonsVersion>2.28.0</HexalithCommonsVersion></PropertyGroup>
  <Import Project="$catalogPath" />
</Project>
"@
    Test-Scenario -Name 'Shared dependency-version property' -RepositoryRoot $propertyRoot -ExpectedExitCode 1 `
        -ExpectedOutput "overrides authoritative version property 'HexalithCommonsVersion'"

    $missingImportRoot = New-ConsumerFixture -Name 'missing-import' -WrapperContent @'
<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>
'@
    Test-Scenario -Name 'Missing shared import' -RepositoryRoot $missingImportRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'does not import the authoritative catalog'

    $wrongImportRoot = New-ConsumerFixture -Name 'wrong-import' -WrapperContent @'
<Project>
  <PropertyGroup>
    <LocalPackageProps>$(MSBuildThisFileDirectory)Local.Packages.props</LocalPackageProps>
  </PropertyGroup>
  <Import Project="$(LocalPackageProps)" />
</Project>
'@
    Test-Scenario -Name 'Resolvable non-catalog import' -RepositoryRoot $wrongImportRoot -ExpectedExitCode 1 `
        -ExpectedOutput 'imports resolve to'

    $fallbackChainRoot = New-ConsumerFixture -Name 'fallback-chain' -WrapperContent @'
<Project>
  <PropertyGroup>
    <Hexalith1BuildPackageProps>$(MSBuildThisFileDirectory)missing/Directory.Packages.props</Hexalith1BuildPackageProps>
    <Hexalith2BuildPackageProps>$(MSBuildThisFileDirectory)../shared/Directory.Packages.props</Hexalith2BuildPackageProps>
  </PropertyGroup>
  <Import Project="$(Hexalith1BuildPackageProps)" Condition="Exists('$(Hexalith1BuildPackageProps)') And '$(HexalithVersionsLoaded)' != 'true'" />
  <Import Project="$(Hexalith2BuildPackageProps)" Condition="Exists('$(Hexalith2BuildPackageProps)') And '$(HexalithVersionsLoaded)' != 'true'" />
</Project>
'@
    Test-Scenario -Name 'Fallback-chain wrapper import' -RepositoryRoot $fallbackChainRoot -ExpectedExitCode 0 `
        -ExpectedOutput 'consumer package authority validation passed'

    $missingRowRoot = New-ConsumerFixture -Name 'missing-catalog-row' -ProjectContent @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Missing.Package" /></ItemGroup></Project>
'@
    Test-Scenario -Name 'Missing catalog row' -RepositoryRoot $missingRowRoot -ExpectedExitCode 1 `
        -ExpectedOutput "PackageReference 'Missing.Package' has no authoritative catalog row"
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Consumer package authority validator tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine("Consumer package authority validator tests passed: $scenarioCount scenarios.")
