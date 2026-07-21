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

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Authoritative package catalog tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine(
    "Authoritative package catalog tests passed for $($requiredPackages.Count) approved package identities and $($sharedPackageVersions.Count) shared package versions."
)
