[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$validatorPath = Join-Path $PSScriptRoot 'validate-package-version-exceptions.ps1'
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "package-version-exceptions-$([Guid]::NewGuid().ToString('N'))"
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

function Invoke-Validator {
    param(
        [Parameter(Mandatory = $true)]
        [string] $InventoryPath,

        [Parameter(Mandatory = $true)]
        [string] $CatalogPath,

        [AllowEmptyString()]
        [string] $WorkspaceRoot = ''
    )

    $arguments = @(
        '-NoLogo'
        '-NoProfile'
        '-File'
        $validatorPath
        '-InventoryPath'
        $InventoryPath
        '-CatalogPath'
        $CatalogPath
    )
    if (-not [string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
        $arguments += @('-WorkspaceRoot', $WorkspaceRoot)
    }

    $output = @(& $pwshExecutable @arguments 2>&1)
    return [pscustomobject] @{
        ExitCode = $LASTEXITCODE
        Output = [string]::Join("`n", @($output | ForEach-Object { [string] $_ }))
    }
}

function Test-Scenario {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $InventoryPath,

        [Parameter(Mandatory = $true)]
        [string] $CatalogPath,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedExitCode,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedOutput,

        [AllowEmptyString()]
        [string] $WorkspaceRoot = ''
    )

    $script:scenarioCount++
    $result = Invoke-Validator `
        -InventoryPath $InventoryPath -CatalogPath $CatalogPath -WorkspaceRoot $WorkspaceRoot
    if ($result.ExitCode -ne $ExpectedExitCode) {
        $script:failures.Add(
            "$Name expected exit code $ExpectedExitCode but received $($result.ExitCode). Output: $($result.Output)"
        )
        return
    }

    if ($result.Output -notlike "*$ExpectedOutput*") {
        $script:failures.Add("$Name output did not contain '$ExpectedOutput'. Output: $($result.Output)")
    }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    $catalogPath = Join-Path $temporaryRoot 'catalog.props'
    Write-Utf8File -Path $catalogPath -Content @'
<Project><ItemGroup><PackageVersion Include="Aspire.Hosting" Version="13.4.6" /></ItemGroup></Project>
'@

    $workspaceRoot = Join-Path $temporaryRoot 'workspace'
    $moduleRoot = Join-Path $workspaceRoot 'references/Fixture.Module'
    Write-Utf8File -Path (Join-Path $workspaceRoot '.gitmodules') -Content @'
[submodule "references/Fixture.Module"]
  path = references/Fixture.Module
  url = https://example.invalid/Fixture.Module.git
'@
    Write-Utf8File -Path (Join-Path $moduleRoot '.git') -Content 'gitdir: ../../.git/modules/references/Fixture.Module'
    Write-Utf8File -Path (Join-Path $moduleRoot 'src/Fixture.AppHost/Fixture.AppHost.csproj') -Content @'
<Project Sdk="Aspire.AppHost.Sdk/13.4.6"></Project>
'@
    Write-Utf8File -Path (Join-Path $moduleRoot '.config/dotnet-tools.json') -Content @'
{"version":1,"isRoot":true,"tools":{"fixture.tool":{"version":"1.2.3","commands":["fixture"]}}}
'@

    $validInventoryPath = Join-Path $temporaryRoot 'valid.json'
    Write-Utf8File -Path $validInventoryPath -Content @'
{
  "schemaVersion": 1,
  "architectureDecision": "ADR-package-version-exceptions",
  "exceptions": [
    {
      "kind": "apphost-sdk",
      "owner": "Fixture.Module",
      "path": "src/Fixture.AppHost/Fixture.AppHost.csproj",
      "id": "Aspire.AppHost.Sdk",
      "version": "13.4.6",
      "rationale": "Project SDK resolver versions cannot use NuGet CPM.",
      "alignmentRule": "exact-catalog-package",
      "catalogPackage": "Aspire.Hosting"
    },
    {
      "kind": "dotnet-tool",
      "owner": "Fixture.Module",
      "path": ".config/dotnet-tools.json",
      "id": "fixture.tool",
      "version": "1.2.3",
      "rationale": "Tool manifests cannot use NuGet CPM.",
      "alignmentRule": "exact-manifest"
    }
  ]
}
'@

    Test-Scenario -Name 'Valid inventory schema' -InventoryPath $validInventoryPath `
        -CatalogPath $catalogPath -ExpectedExitCode 0 -ExpectedOutput 'validated 2 allowlisted exceptions'
    Test-Scenario -Name 'Valid workspace evidence' -InventoryPath $validInventoryPath `
        -CatalogPath $catalogPath -WorkspaceRoot $workspaceRoot -ExpectedExitCode 0 `
        -ExpectedOutput 'workspace evidence matches the allowlist'

    $explicitWorkspace = Join-Path $temporaryRoot 'explicit-import-workspace'
    Copy-Item -LiteralPath $workspaceRoot -Destination $explicitWorkspace -Recurse
    $explicitProject = Join-Path $explicitWorkspace 'references/Fixture.Module/src/Fixture.AppHost/Fixture.AppHost.csproj'
    Write-Utf8File -Path $explicitProject -Content '<Project><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="13.4.6" /><Import Project="Sdk.targets" Sdk="Aspire.AppHost.Sdk" Version="13.4.6" /></Project>'
    Test-Scenario -Name 'Explicit SDK imports are recognized' -InventoryPath $validInventoryPath -CatalogPath $catalogPath -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 0 -ExpectedOutput 'workspace evidence matches the allowlist'
    Write-Utf8File -Path $explicitProject -Content '<Project><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="13.4.2" /></Project>'
    Test-Scenario -Name 'Explicit SDK import drift fails' -InventoryPath $validInventoryPath -CatalogPath $catalogPath -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 1 -ExpectedOutput 'Aspire.AppHost.Sdk/13.4.2 is not aligned'

    Write-Utf8File -Path $explicitProject -Content "<Project><Import Project='Sdk.props' Sdk='Aspire.AppHost.Sdk' Version='13.4.6' /><Import Project='Sdk.targets' Sdk='Aspire.AppHost.Sdk' Version='13.4.6' /></Project>"
    Test-Scenario -Name 'Active single-quoted SDK imports' -InventoryPath $validInventoryPath -CatalogPath $catalogPath -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 0 -ExpectedOutput 'workspace evidence matches the allowlist'
    Write-Utf8File -Path $explicitProject -Content '<Project Sdk="Aspire.AppHost.Sdk/13.4.6"><!-- <Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="99.0.0" /> --></Project>'
    Test-Scenario -Name 'Commented SDK import ignored' -InventoryPath $validInventoryPath -CatalogPath $catalogPath -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 0 -ExpectedOutput 'workspace evidence matches the allowlist'
    Write-Utf8File -Path $explicitProject -Content '<Project><!-- <Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="13.4.6" /> --></Project>'
    Test-Scenario -Name 'Comment alone supplies no active SDK evidence' -InventoryPath $validInventoryPath -CatalogPath $catalogPath -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 1 -ExpectedOutput 'was not found in the workspace'

    $propertyCatalog = Join-Path $temporaryRoot 'property-sdk-catalog.props'
    Write-Utf8File -Path $propertyCatalog -Content ([IO.File]::ReadAllText($catalogPath).Replace('<Project>', '<Project><PropertyGroup><HexalithAspireAppHostSdkVersion>13.4.6</HexalithAspireAppHostSdkVersion></PropertyGroup>'))
    Write-Utf8File -Path $explicitProject -Content '<Project><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="$(HexalithAspireAppHostSdkVersion)" /><Import Project="Sdk.targets" Sdk="Aspire.AppHost.Sdk" Version="$(HexalithAspireAppHostSdkVersion)" /></Project>'
    Test-Scenario -Name 'Explicit SDK import property resolves from catalog' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 0 -ExpectedOutput 'workspace evidence matches the allowlist'

    foreach ($body in @(
        '<Project Sdk="Aspire.AppHost.Sdk/$(HexalithAspireAppHostSdkVersion)" />',
        '<Project><Sdk Name="Aspire.AppHost.Sdk" Version="$(HexalithAspireAppHostSdkVersion)" /></Project>',
        '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="$(HexalithAspireAppHostSdkVersion)" /></Project>',
        '<Project><Import Project="Sdk.props" Sdk="Unrelated.Sdk" Version="$(UnrelatedVersion)" /><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="13.4.6" /><Import Condition="false" Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="99.0.0" /></Project>'
    )) {
        Write-Utf8File -Path $explicitProject -Content $body
        Test-Scenario -Name 'Effective SDK declaration forms and unrelated imports' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 0 -ExpectedOutput 'workspace evidence matches the allowlist'
    }
    Write-Utf8File -Path (Join-Path (Split-Path $explicitProject -Parent) 'selections.props') -Content '<Project><PropertyGroup><SdkBase>13.4.6</SdkBase><SdkAlias>$(SdkBase)</SdkAlias></PropertyGroup></Project>'
    Write-Utf8File -Path $explicitProject -Content '<Project><Import Project="selections.props" /><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="$(SdkAlias)" /></Project>'
    Test-Scenario -Name 'Imported nested consumer properties resolve' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 0 -ExpectedOutput 'workspace evidence matches the allowlist'
    Write-Utf8File -Path $explicitProject -Content '<Project><PropertyGroup><HexalithAspireAppHostSdkVersion>13.4.2</HexalithAspireAppHostSdkVersion></PropertyGroup><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="$(HexalithAspireAppHostSdkVersion)" /></Project>'
    Test-Scenario -Name 'Consuming-project SDK override is observed' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 1 -ExpectedOutput 'Aspire.AppHost.Sdk/13.4.2 is not aligned'
    Write-Utf8File -Path $explicitProject -Content '<Project><Import Condition="false" Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="13.4.6" /></Project>'
    Test-Scenario -Name 'Inactive SDK import supplies no evidence' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 1 -ExpectedOutput 'was not found in the workspace'

    Write-Utf8File -Path $explicitProject -Content '<Project><PropertyGroup><HexalithAspireAppHostSdkVersion>99.0.0</HexalithAspireAppHostSdkVersion></PropertyGroup><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="$(HexalithAspireAppHostSdkVersion)" /><PropertyGroup><HexalithAspireAppHostSdkVersion>13.4.6</HexalithAspireAppHostSdkVersion></PropertyGroup></Project>'
    Test-Scenario -Name 'Later assignment cannot conceal SDK import drift' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 1 -ExpectedOutput 'Aspire.AppHost.Sdk/99.0.0 is not aligned'

    Write-Utf8File -Path $explicitProject -Content '<Project><ImportGroup Condition="true"><Import Condition="false" Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="99.0.0" /><Import Project="Sdk.targets" Sdk="Aspire.AppHost.Sdk" Version="$(HexalithAspireAppHostSdkVersion)" /></ImportGroup></Project>'
    Test-Scenario -Name 'Active SDK ImportGroup respects child conditions' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 0 -ExpectedOutput 'workspace evidence matches the allowlist'
    Write-Utf8File -Path $explicitProject -Content '<Project><ImportGroup Condition="false"><Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="99.0.0" /></ImportGroup></Project>'
    Test-Scenario -Name 'Inactive SDK ImportGroup supplies no active evidence' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -WorkspaceRoot $explicitWorkspace -ExpectedExitCode 1 -ExpectedOutput 'was not found in the workspace'

    $priorSdkEnvironment = [Environment]::GetEnvironmentVariable('HexalithAspireAppHostSdkVersion')
    try {
        [Environment]::SetEnvironmentVariable('HexalithAspireAppHostSdkVersion', '99.0.0')
        Test-Scenario -Name 'Injected SDK environment cannot become catalog authority' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -ExpectedExitCode 1 -ExpectedOutput "authoritative catalog selects '13.4.6'"
        [Environment]::SetEnvironmentVariable('HexalithAspireAppHostSdkVersion', '13.4.6')
        Test-Scenario -Name 'Matching inherited SDK selection agrees with catalog' -InventoryPath $validInventoryPath -CatalogPath $propertyCatalog -ExpectedExitCode 0 -ExpectedOutput 'validated 2 allowlisted exceptions'
    }
    finally { [Environment]::SetEnvironmentVariable('HexalithAspireAppHostSdkVersion', $priorSdkEnvironment) }

    $misalignedWorkspace = Join-Path $temporaryRoot 'misaligned-workspace'
    $misalignedModule = Join-Path $misalignedWorkspace 'references/Fixture.Module'
    Write-Utf8File -Path (Join-Path $misalignedWorkspace '.gitmodules') -Content @'
[submodule "references/Fixture.Module"]
  path = references/Fixture.Module
  url = https://example.invalid/Fixture.Module.git
'@
    Write-Utf8File -Path (Join-Path $misalignedModule '.git') -Content 'gitdir: ../../.git/modules/references/Fixture.Module'
    Write-Utf8File -Path (Join-Path $misalignedModule 'src/Fixture.AppHost/Fixture.AppHost.csproj') -Content @'
<Project Sdk="Aspire.AppHost.Sdk/13.4.2"></Project>
'@
    Write-Utf8File -Path (Join-Path $misalignedModule '.config/dotnet-tools.json') -Content @'
{"version":1,"isRoot":true,"tools":{"fixture.tool":{"version":"1.2.3","commands":["fixture"]}}}
'@
    Test-Scenario -Name 'Misaligned SDK family' -InventoryPath $validInventoryPath `
        -CatalogPath $catalogPath -WorkspaceRoot $misalignedWorkspace -ExpectedExitCode 1 `
        -ExpectedOutput "Aspire.AppHost.Sdk/13.4.2 is not aligned with Aspire.Hosting/13.4.6"

    Write-Utf8File -Path (Join-Path $moduleRoot '.config/dotnet-tools.json') -Content @'
{"version":1,"isRoot":true,"tools":{"fixture.tool":{"version":"1.2.3","commands":["fixture"]},"unlisted.tool":{"version":"9.9.9","commands":["unlisted"]}}}
'@
    Test-Scenario -Name 'Unlisted tool exception' -InventoryPath $validInventoryPath `
        -CatalogPath $catalogPath -WorkspaceRoot $workspaceRoot -ExpectedExitCode 1 `
        -ExpectedOutput "unlisted version exception 'dotnet-tool|Fixture.Module|.config/dotnet-tools.json|unlisted.tool'"

    $elementFormWorkspace = Join-Path $temporaryRoot 'element-form-workspace'
    $elementFormModule = Join-Path $elementFormWorkspace 'references/Fixture.Module'
    Write-Utf8File -Path (Join-Path $elementFormWorkspace '.gitmodules') -Content @'
[submodule "references/Fixture.Module"]
  path = references/Fixture.Module
  url = https://example.invalid/Fixture.Module.git
'@
    Write-Utf8File -Path (Join-Path $elementFormModule '.git') -Content 'gitdir: ../../.git/modules/references/Fixture.Module'
    Write-Utf8File -Path (Join-Path $elementFormModule 'src/Fixture.AppHost/Fixture.AppHost.csproj') -Content @'
<Project Sdk="Aspire.AppHost.Sdk/13.4.6"></Project>
'@
    Write-Utf8File -Path (Join-Path $elementFormModule 'src/Element.AppHost/Element.AppHost.csproj') -Content @'
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Version="13.4.6" Name="Aspire.AppHost.Sdk" />
</Project>
'@
    Write-Utf8File -Path (Join-Path $elementFormModule '.config/dotnet-tools.json') -Content @'
{"version":1,"isRoot":true,"tools":{"fixture.tool":{"version":"1.2.3","commands":["fixture"]}}}
'@
    Test-Scenario -Name 'Unlisted element-form SDK pin' -InventoryPath $validInventoryPath `
        -CatalogPath $catalogPath -WorkspaceRoot $elementFormWorkspace -ExpectedExitCode 1 `
        -ExpectedOutput "unlisted version exception 'apphost-sdk|Fixture.Module|src/Element.AppHost/Element.AppHost.csproj|Aspire.AppHost.Sdk'"

    $uninitializedWorkspace = Join-Path $temporaryRoot 'uninitialized-workspace'
    Write-Utf8File -Path (Join-Path $uninitializedWorkspace '.gitmodules') -Content @'
[submodule "references/Fixture.Module"]
  path = references/Fixture.Module
  url = https://example.invalid/Fixture.Module.git
'@
    New-Item -ItemType Directory -Path (Join-Path $uninitializedWorkspace 'references/Fixture.Module') -Force | Out-Null
    Test-Scenario -Name 'Uninitialized submodule' -InventoryPath $validInventoryPath `
        -CatalogPath $catalogPath -WorkspaceRoot $uninitializedWorkspace -ExpectedExitCode 1 `
        -ExpectedOutput "Submodule 'references/Fixture.Module' is not initialized; cannot verify exceptions for owner 'Fixture.Module'"

    $duplicateInventoryPath = Join-Path $temporaryRoot 'duplicate.json'
    $duplicateInventory = Get-Content -LiteralPath $validInventoryPath -Raw | ConvertFrom-Json
    $duplicateInventory.exceptions = @($duplicateInventory.exceptions) + @($duplicateInventory.exceptions[0])
    Write-Utf8File -Path $duplicateInventoryPath -Content ($duplicateInventory | ConvertTo-Json -Depth 10)
    Test-Scenario -Name 'Duplicate allowlist entry' -InventoryPath $duplicateInventoryPath `
        -CatalogPath $catalogPath -ExpectedExitCode 1 -ExpectedOutput 'duplicate exception key'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Package version exception validator tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine("Package version exception validator tests passed: $scenarioCount scenarios.")
