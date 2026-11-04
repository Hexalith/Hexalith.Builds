[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$catalogPath = Join-Path $PSScriptRoot '../Props/Directory.Packages.props'
$evaluationText = [string]::Join(
    "`n",
    @(
        & dotnet msbuild $catalogPath -nologo `
            -getProperty:CentralPackageVersionOverrideEnabled `
            -getProperty:HexalithCommonsVersion `
            -getProperty:HexalithEventStoreVersion `
            -getProperty:HexalithMcpCliVersion `
            -getProperty:HexalithTenantsVersion `
            -getProperty:HexalithFrontComposerVersion `
            -getItem:PackageVersion 2>&1 |
            ForEach-Object { [string] $_ }
    )
)
if ($LASTEXITCODE -ne 0) {
    throw "Catalog evaluation failed with exit code $LASTEXITCODE.`n$evaluationText"
}

$evaluation = $evaluationText | ConvertFrom-Json -ErrorAction Stop
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

$overrideEnabled = [string] $evaluation.Properties.CentralPackageVersionOverrideEnabled
if ($overrideEnabled -cne 'false') {
    $failures.Add(
        "CentralPackageVersionOverrideEnabled resolved to '$overrideEnabled'; expected exact value 'false'."
    )
}

# Each canonical family must use its one authoritative property, including new members.
$cohortFamilies = [ordered] @{
    'Hexalith.EventStore.' = @{ Property = 'HexalithEventStoreVersion'; Version = '4.1.0' }
    'Hexalith.McpCli.' = @{ Property = 'HexalithMcpCliVersion'; Version = '2.0.0' }
    'Hexalith.Tenants.' = @{ Property = 'HexalithTenantsVersion'; Version = '7.0.0' }
}
[xml] $catalog = Get-Content -LiteralPath $catalogPath -Raw
foreach ($family in $cohortFamilies.GetEnumerator()) {
    $propertyName = $family.Value.Property
    $expectedVersion = $family.Value.Version
    if ([string] $evaluation.Properties.$propertyName -cne $expectedVersion) {
        $failures.Add("Canonical family '$($family.Key)' must default to '$expectedVersion'.")
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
            "<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=""$package"" VersionOverride=""0.0.1"" /></ItemGroup></Project>")
        $overrideOutput = @(& dotnet restore $probePath --force --source $probeDirectory --packages (Join-Path $probeDirectory 'packages') 2>&1)
        if ($LASTEXITCODE -eq 0 -or [string]::Join("`n", $overrideOutput) -notmatch 'NU1013') {
            $failures.Add("NuGet must reject VersionOverride for '$package' with NU1013.")
        }
    }
}
finally {
    if (Test-Path -LiteralPath $probeDirectory) { [IO.Directory]::Delete($probeDirectory, $true) }
}

# The approved stable selection belongs exclusively to Builds and retains one package identity.
$stableOutput = @(& (Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })) `
        -NoLogo -NoProfile -File (Join-Path $PSScriptRoot 'validate-central-package-versions.ps1') `
        -CatalogPath $catalogPath -ConsumerProjectName 'Hexalith.Folders.Aspire' -EmitEvaluation 2>&1)
if ($LASTEXITCODE -ne 0) {
    $failures.Add("Folders.Aspire central evaluation failed: $([string]::Join("`n", $stableOutput))")
}
else {
    $stableEvaluation = ([string]::Join("`n", $stableOutput)) | ConvertFrom-Json -ErrorAction Stop
    $stableRows = @($stableEvaluation.Items.PackageVersion | Where-Object { $_.Identity -eq 'CommunityToolkit.Aspire.Hosting.Dapr' })
    if ($stableRows.Count -ne 1 -or $stableRows[0].Version -cne '13.0.0') {
        $failures.Add('Folders.Aspire must centrally select exactly one Dapr integration identity at stable 13.0.0.')
    }

    foreach ($row in @($stableEvaluation.Items.PackageVersion)) {
        if ($row.Identity -ne 'CommunityToolkit.Aspire.Hosting.Dapr' -and $row.Version -cne $evaluatedPackages[$row.Identity]) {
            $failures.Add("Folders.Aspire changes unrelated central selection '$($row.Identity)'.")
        }
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Authoritative package catalog tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine(
    "Authoritative package catalog tests passed for $($requiredPackages.Count) approved package identities, $($sharedPackageVersions.Count) shared package versions, and $($cohortFamilies.Count) canonical families with overrides prohibited."
)
