[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$catalogPath = Join-Path $PSScriptRoot '../Props/Directory.Packages.props'
# SDK startup notices belong to stderr and cannot become catalog JSON.
function Invoke-CatalogEvaluation {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $evaluationDirectory = Join-Path ([IO.Path]::GetTempPath()) "catalog-evaluation-$([Guid]::NewGuid().ToString('N'))"
    try {
        [void] [IO.Directory]::CreateDirectory($evaluationDirectory)
        $stdout = Join-Path $evaluationDirectory 'stdout'
        $stderr = Join-Path $evaluationDirectory 'stderr'
        & dotnet msbuild $catalogPath -nologo -nr:false @Arguments 1> $stdout 2> $stderr
        $exitCode = $LASTEXITCODE
        $evaluationText = [IO.File]::ReadAllText($stdout)
        $diagnostic = [IO.File]::ReadAllText($stderr)
        if ($exitCode -ne 0) { throw "Catalog evaluation failed with exit code $exitCode.`n$evaluationText`n$diagnostic" }
        if ($diagnostic) { [Console]::Error.Write($diagnostic) }
    }
    finally { if (Test-Path -LiteralPath $evaluationDirectory) { [IO.Directory]::Delete($evaluationDirectory, $true) } }

    return $evaluationText | ConvertFrom-Json -ErrorAction Stop
}

$evaluation = Invoke-CatalogEvaluation @(
    '-getProperty:CentralPackageVersionOverrideEnabled'
    '-getProperty:HexalithCommonsVersion'
    '-getProperty:HexalithEventStoreVersion'
    '-getProperty:HexalithMcpCliVersion'
    '-getProperty:HexalithTenantsVersion'
    '-getProperty:HexalithFoldersVersion'
    '-getProperty:HexalithFrontComposerVersion'
    '-getProperty:HexalithMemoriesVersion'
    '-getProperty:HexalithAspireHostingDaprVersion'
    '-getItem:PackageVersion'
)
$failures = [System.Collections.Generic.List[string]]::new()
$requiredPackages = @(
    'Dapr.AI'
    'Dapr.AI.Microsoft.Extensions'
    'Fluxor'
    'Kreuzberg'
    'Microsoft.AspNetCore.Components.CustomElements'
    'Microsoft.Extensions.Diagnostics.Abstractions'
    'MinVer'
    'NBomber.Http'
    'NFalkorDB'
    'NRedisStack'
    'NetArchTest.eNhancedEdition'
    'OpenTelemetry'
    'OpenTelemetry.Exporter.InMemory'
    'OpenTelemetry.Instrumentation.StackExchangeRedis'
    'xunit.v3.extensibility.core'
    'ByteAether.Ulid'
    'CommunityToolkit.Aspire.Hosting.Dapr'
    'Fluxor.Blazor.Web'
    'MediatR'
    'Microsoft.AspNetCore.Authentication.JwtBearer'
    'Microsoft.AspNetCore.Mvc.Testing'
    'Microsoft.AspNetCore.OpenApi'
    'Microsoft.AspNetCore.TestHost'
    'Microsoft.Extensions.Configuration.Binder'
    'Microsoft.Extensions.DependencyInjection'
    'Microsoft.Extensions.DependencyInjection.Abstractions'
    'Microsoft.Extensions.Hosting'
    'Microsoft.Extensions.Hosting.Abstractions'
    'Microsoft.Extensions.Http'
    'Microsoft.Extensions.Http.Resilience'
    'Microsoft.Extensions.Logging.Abstractions'
    'Microsoft.Extensions.Options'
    'Microsoft.Extensions.Options.ConfigurationExtensions'
    'Microsoft.Extensions.ServiceDiscovery'
    'Microsoft.NET.Test.Sdk'
    'Microsoft.Testing.Extensions.CodeCoverage'
    'ModelContextProtocol'
    'ModelContextProtocol.AspNetCore'
    'NSubstitute'
    'OpenTelemetry.Exporter.OpenTelemetryProtocol'
    'OpenTelemetry.Extensions.Hosting'
    'System.CommandLine'
    'Testcontainers'
    'YamlDotNet'
    'bunit'
    'Hexalith.EventStore.Contracts'
    'Hexalith.EventStore.Gateway'
    'Hexalith.FrontComposer.Contracts.UI'
    'Hexalith.EventStore.Admin.Abstractions'
    'Hexalith.EventStore.Admin.Server'
    'Hexalith.EventStore.Aspire'
    'Hexalith.EventStore.Client'
    'Hexalith.EventStore.DomainService'
    'Hexalith.EventStore.RestApi.Generators'
    'Hexalith.EventStore.Server'
    'Hexalith.EventStore.ServiceDefaults'
    'Hexalith.EventStore.SignalR'
    'Hexalith.EventStore.Testing'
    'Hexalith.EventStore.Testing.Integration'
    'Hexalith.McpCli.Abstractions'
    'Hexalith.Memories.ServiceDefaults'
    'Hexalith.Memories.Telemetry'
    'Hexalith.Tenants.Aspire'
    'Hexalith.Tenants.Client'
    'Hexalith.Tenants.Contracts'
    'Hexalith.Tenants.Server'
    'Hexalith.Tenants.Testing'
    'OpenTelemetry.Instrumentation.AspNetCore'
    'OpenTelemetry.Instrumentation.Http'
    'OpenTelemetry.Instrumentation.Runtime'
)

$evaluatedPackages = @{}
foreach ($packageVersion in @($evaluation.Items.PackageVersion)) {
    $evaluatedPackages[[string] $packageVersion.Identity] = [string] $packageVersion.Version
}

foreach ($requiredPackage in $requiredPackages) {
    if (-not $evaluatedPackages.ContainsKey($requiredPackage)) {
        $failures.Add("Catalog is missing approved package '$requiredPackage'.")
    }
}

$sharedPackageVersions = [ordered] @{
    'Hexalith.Commons' = [string] $evaluation.Properties.HexalithCommonsVersion
    'Hexalith.EventStore.Contracts' = [string] $evaluation.Properties.HexalithEventStoreVersion
    'Hexalith.EventStore.Gateway' = [string] $evaluation.Properties.HexalithEventStoreVersion
    'Hexalith.FrontComposer.Contracts.UI' = [string] $evaluation.Properties.HexalithFrontComposerVersion
    'Hexalith.Memories.ServiceDefaults' = [string] $evaluation.Properties.HexalithMemoriesVersion
    'Hexalith.Memories.Telemetry' = [string] $evaluation.Properties.HexalithMemoriesVersion
    # The neutral Dapr integration is the catalog default that every consumer receives.
    'CommunityToolkit.Aspire.Hosting.Dapr' = [string] $evaluation.Properties.HexalithAspireHostingDaprVersion
}
foreach ($sharedPackageVersion in $sharedPackageVersions.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($sharedPackageVersion.Value)) {
        $failures.Add("Shared version for package '$($sharedPackageVersion.Key)' resolved to a blank value.")
        continue
    }

    if (-not $evaluatedPackages.ContainsKey($sharedPackageVersion.Key)) {
        $failures.Add("Catalog is missing shared-version package '$($sharedPackageVersion.Key)'.")
        continue
    }

    if ($evaluatedPackages[$sharedPackageVersion.Key] -cne $sharedPackageVersion.Value) {
        $failures.Add(
            "Package '$($sharedPackageVersion.Key)' resolved to '$($evaluatedPackages[$sharedPackageVersion.Key])'; expected shared version '$($sharedPackageVersion.Value)'."
        )
    }
}

# Memories consumers take both hosted-profile rows from this catalog, and the rows follow a selected cohort.
$memoriesPackages = @('Hexalith.Memories.ServiceDefaults', 'Hexalith.Memories.Telemetry')
$syntheticMemoriesVersion = '9.8.7'
$memoriesSelections = [ordered] @{
    'Memories hosted-test profile' = @{ Arguments = @('-p:HexalithUsePublishedSdkForHostedTests=true'); Expected = [string] $evaluation.Properties.HexalithMemoriesVersion }
    'selected Memories cohort' = @{ Arguments = @("-p:HexalithMemoriesVersion=$syntheticMemoriesVersion"); Expected = $syntheticMemoriesVersion }
}
foreach ($selection in $memoriesSelections.GetEnumerator()) {
    $selected = Invoke-CatalogEvaluation (@($selection.Value.Arguments) + @('-getItem:PackageVersion'))
    foreach ($package in $memoriesPackages) {
        $rows = @($selected.Items.PackageVersion | Where-Object { [string]::Equals([string] $_.Identity, $package, [StringComparison]::OrdinalIgnoreCase) })
        if ($rows.Count -ne 1) {
            $failures.Add("The $($selection.Key) evaluation must select '$package' exactly once; found $($rows.Count).")
        }
        elseif ([string]::IsNullOrWhiteSpace($selection.Value.Expected) -or [string] $rows[0].Version -cne $selection.Value.Expected) {
            $failures.Add("The $($selection.Key) evaluation selects '$package' $($rows[0].Version); expected '$($selection.Value.Expected)'.")
        }
    }
}

$overrideEnabled = [string] $evaluation.Properties.CentralPackageVersionOverrideEnabled
if ($overrideEnabled -cne 'false') {
    $failures.Add(
        "CentralPackageVersionOverrideEnabled resolved to '$overrideEnabled'; expected exact value 'false'."
    )
}

# Each canonical family must use its one authoritative property, including new members.
$cohortFamilies = [ordered] @{
    'Hexalith.EventStore.' = 'HexalithEventStoreVersion'
    'Hexalith.McpCli.' = 'HexalithMcpCliVersion'
    'Hexalith.Tenants.' = 'HexalithTenantsVersion'
    'Hexalith.Folders' = 'HexalithFoldersVersion'
    'Hexalith.Memories.' = 'HexalithMemoriesVersion'
}
[xml] $catalog = Get-Content -LiteralPath $catalogPath -Raw
foreach ($family in $cohortFamilies.GetEnumerator()) {
    $propertyName = $family.Value
    $expectedVersion = [string] $evaluation.Properties.$propertyName
    if ([string]::IsNullOrWhiteSpace($expectedVersion)) {
        $failures.Add("Canonical family '$($family.Key)' has no catalog version.")
    }

    $members = @($catalog.SelectNodes('//PackageVersion') | Where-Object { $_.Include.StartsWith($family.Key, [StringComparison]::Ordinal) })
    if ($members.Count -eq 0) {
        $failures.Add("Canonical family '$($family.Key)' has no package bindings.")
    }

    foreach ($member in $members) {
        if ($member.Version -cne ('$' + '(' + $propertyName + ')') -or $evaluatedPackages[$member.Include] -cne $expectedVersion) {
            $failures.Add("Package '$($member.Include)' must bind directly to '$propertyName' at '$expectedVersion'.")
        }
    }
}

# Prove NuGet refuses per-package overrides before any source is contacted.
$probeDirectory = Join-Path ([IO.Path]::GetTempPath()) "canonical-cohort-$([Guid]::NewGuid().ToString('N'))"
try {
    [void] [IO.Directory]::CreateDirectory($probeDirectory)
    $escapedCatalog = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($catalogPath))
    [IO.File]::WriteAllText((Join-Path $probeDirectory 'Directory.Packages.props'),
        "<Project><Import Project=""$escapedCatalog"" /></Project>")
    foreach ($package in @('Hexalith.EventStore.Contracts', 'Hexalith.McpCli.Abstractions', 'Hexalith.Tenants.Contracts')) {
        $probePath = Join-Path $probeDirectory 'Override.csproj'
        [IO.File]::WriteAllText($probePath,
            "<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=""$package"" VersionOverride=""0.0.1"" /></ItemGroup></Project>")
        $overrideOutput = @(& dotnet restore $probePath --force --source $probeDirectory --packages (Join-Path $probeDirectory 'packages') 2>&1)
        if ($LASTEXITCODE -eq 0 -or [string]::Join("`n", $overrideOutput) -notmatch 'NU1013') {
            $failures.Add("NuGet must reject VersionOverride for '$package' with NU1013.")
        }
    }
}
finally {
    if (Test-Path -LiteralPath $probeDirectory) { [IO.Directory]::Delete($probeDirectory, $true) }
}

# A Folders.Aspire-named evaluation must match the neutral catalog row for row,
# so its retired Dapr integration exception cannot return.
$foldersOutput = @(& (Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })) `
        -NoLogo -NoProfile -File (Join-Path $PSScriptRoot 'validate-central-package-versions.ps1') `
        -CatalogPath $catalogPath -ConsumerProjectName 'Hexalith.Folders.Aspire' -EmitEvaluation 2>&1)
if ($LASTEXITCODE -ne 0) {
    $failures.Add("Folders.Aspire central evaluation failed: $([string]::Join("`n", $foldersOutput))")
}
else {
    $foldersEvaluation = ([string]::Join("`n", $foldersOutput)) | ConvertFrom-Json -ErrorAction Stop
    $foldersRows = @($foldersEvaluation.Items.PackageVersion)
    $neutralRowCount = @($evaluation.Items.PackageVersion).Count
    if ($foldersRows.Count -ne $neutralRowCount) {
        $failures.Add("Folders.Aspire evaluates $($foldersRows.Count) central selections; the neutral catalog evaluates $neutralRowCount.")
    }

    $foldersPackages = @{}
    foreach ($row in $foldersRows) {
        $identity = [string] $row.Identity
        $foldersPackages[$identity] = [string] $row.Version
        if (-not $evaluatedPackages.ContainsKey($identity)) {
            $failures.Add("Folders.Aspire adds central selection '$identity' absent from the neutral catalog.")
        }
        elseif ([string] $row.Version -cne $evaluatedPackages[$identity]) {
            $failures.Add("Folders.Aspire selects '$identity' $($row.Version); the neutral catalog selects $($evaluatedPackages[$identity]).")
        }
    }

    foreach ($identity in $evaluatedPackages.Keys) {
        if (-not $foldersPackages.ContainsKey($identity)) {
            $failures.Add("Folders.Aspire loses neutral central selection '$identity'.")
        }
    }
}

# Producer host views and frozen v1 evidence have independent authorities.
. (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1')
$producerControls = 0
$producerRoot = Join-Path ([IO.Path]::GetTempPath()) "producer-host-controls-$([Guid]::NewGuid().ToString('N'))"
try {
    [void] [IO.Directory]::CreateDirectory($producerRoot)
    $frozenPins = Get-FrozenV1PlatformPins
    if ($frozenPins.eventStoreVersion -cne '4.2.0') { $failures.Add('Frozen v1 evidence lost its independent SDK4.2 schema authority.') }
    $producerControls++
    if ((Assert-PackagedHostCatalogViews -CatalogPath $catalogPath) -cne $evaluation.Properties.HexalithEventStoreVersion) {
        $failures.Add('Packaged hosts did not resolve the current EventStore family.')
    }
    # Change one named consumer and prove qualification rejects the split family.
    [xml] $mutated = [IO.File]::ReadAllText($catalogPath)
    $group = $mutated.CreateElement('PropertyGroup')
    $group.SetAttribute('Condition', "'`$(MSBuildProjectName)' == 'Hexalith.Builds.Module.UiHost'")
    $member = $mutated.CreateElement('HexalithEventStoreVersion')
    $member.InnerText = '0.0.1'
    $null = $group.AppendChild($member)
    $null = $mutated.DocumentElement.AppendChild($group)
    $mutatedPath = Join-Path $producerRoot 'split-family.props'
    [IO.File]::WriteAllText($mutatedPath, $mutated.OuterXml, [Text.UTF8Encoding]::new($true))
    $refused = $false
    try { $null = Assert-PackagedHostCatalogViews -CatalogPath $mutatedPath }
    catch { $refused = $_.Exception.Message.Contains('differs from the default catalog') }
    if (-not $refused) { $failures.Add('A per-host package override was accepted.') }
    $producerControls += 2
    $schemaPath = Join-Path $PSScriptRoot '../schemas/hexalith.module-manifest.v1.json'
    foreach ($field in $frozenPins.Keys) {
        $schema = [IO.File]::ReadAllText($schemaPath) | ConvertFrom-Json
        $schema.'$defs'.platform.properties.$field.const = '99.0.0'
        $mutatedPath = Join-Path $producerRoot "$field.schema.json"
        [IO.File]::WriteAllText($mutatedPath, ($schema | ConvertTo-Json -Depth 100))
        $refused = $false
        try { $null = Get-FrozenV1PlatformPins -SchemaPath $mutatedPath }
        catch { $refused = $true }
        if (-not $refused) { $failures.Add("Frozen v1 verifier admitted changed '$field' independently of the current catalog.") }
        $producerControls++
    }
    Write-Output "Independent frozen-v1 and packaged-host authority controls executed: $producerControls."
}
finally { if (Test-Path -LiteralPath $producerRoot) { [IO.Directory]::Delete($producerRoot, $true) } }

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Authoritative package catalog tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine(
    "Authoritative package catalog tests passed for $($requiredPackages.Count) approved package identities, $($sharedPackageVersions.Count) shared package versions, $($memoriesSelections.Count) Memories selections, and $($cohortFamilies.Count) canonical families with overrides prohibited."
)
