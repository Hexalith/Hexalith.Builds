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
            -getItem:PackageVersion 2>&1 |
            ForEach-Object { [string] $_ }
    )
)
if ($LASTEXITCODE -ne 0) {
    throw "Catalog evaluation failed with exit code $LASTEXITCODE.`n$evaluationText"
}

$evaluation = $evaluationText | ConvertFrom-Json -ErrorAction Stop
$failures = [System.Collections.Generic.List[string]]::new()
$expectedPackages = [ordered] @{
    'Dapr.AI' = '1.18.4'
    'Dapr.AI.Microsoft.Extensions' = '1.18.4'
    'Fluxor' = '6.10.0'
    'Kreuzberg' = '4.10.2'
    'Microsoft.AspNetCore.Components.CustomElements' = '10.0.10'
    'Microsoft.Extensions.Diagnostics.Abstractions' = '10.0.10'
    'MinVer' = '8.0.0-rc.1'
    'NBomber.Http' = '6.2.1'
    'NFalkorDB' = '1.0.6'
    'NRedisStack' = '1.6.0'
    'NetArchTest.eNhancedEdition' = '1.4.5'
    'OpenTelemetry' = '1.17.0'
    'OpenTelemetry.Exporter.InMemory' = '1.17.0'
    'OpenTelemetry.Instrumentation.StackExchangeRedis' = '1.16.0-beta.1'
    'xunit.v3.extensibility.core' = '3.2.2'
    'ByteAether.Ulid' = '1.3.8'
    'CommunityToolkit.Aspire.Hosting.Dapr' = '13.4.1-beta.686'
    'Fluxor.Blazor.Web' = '6.10.0'
    'MediatR' = '14.2.0'
    'Microsoft.AspNetCore.Authentication.JwtBearer' = '10.0.10'
    'Microsoft.AspNetCore.Mvc.Testing' = '10.0.10'
    'Microsoft.AspNetCore.OpenApi' = '10.0.10'
    'Microsoft.AspNetCore.TestHost' = '10.0.10'
    'Microsoft.Extensions.Configuration.Binder' = '10.0.10'
    'Microsoft.Extensions.DependencyInjection' = '10.0.10'
    'Microsoft.Extensions.DependencyInjection.Abstractions' = '10.0.10'
    'Microsoft.Extensions.Hosting' = '10.0.10'
    'Microsoft.Extensions.Hosting.Abstractions' = '10.0.10'
    'Microsoft.Extensions.Http' = '10.0.10'
    'Microsoft.Extensions.Http.Resilience' = '10.8.0'
    'Microsoft.Extensions.Logging.Abstractions' = '10.0.10'
    'Microsoft.Extensions.Options' = '10.0.10'
    'Microsoft.Extensions.Options.ConfigurationExtensions' = '10.0.10'
    'Microsoft.Extensions.ServiceDiscovery' = '10.8.0'
    'Microsoft.NET.Test.Sdk' = '18.8.1'
    'ModelContextProtocol' = '1.4.1'
    'ModelContextProtocol.AspNetCore' = '1.4.1'
    'NSubstitute' = '6.0.0'
    'OpenTelemetry.Exporter.OpenTelemetryProtocol' = '1.17.0'
    'OpenTelemetry.Extensions.Hosting' = '1.17.0'
    'System.CommandLine' = '2.0.10'
    'Testcontainers' = '4.13.0'
    'YamlDotNet' = '18.1.0'
    'bunit' = '2.8.4-preview'
    'Hexalith.EventStore.Contracts' = '3.74.0'
    'OpenTelemetry.Instrumentation.AspNetCore' = '1.17.0'
    'OpenTelemetry.Instrumentation.Http' = '1.17.0'
    'OpenTelemetry.Instrumentation.Runtime' = '1.17.0'
}

$evaluatedPackages = @{}
foreach ($packageVersion in @($evaluation.Items.PackageVersion)) {
    $evaluatedPackages[[string] $packageVersion.Identity] = [string] $packageVersion.Version
}

foreach ($expectedPackage in $expectedPackages.GetEnumerator()) {
    if (-not $evaluatedPackages.ContainsKey($expectedPackage.Key)) {
        $failures.Add("Catalog is missing approved package '$($expectedPackage.Key)'.")
        continue
    }

    if ($evaluatedPackages[$expectedPackage.Key] -cne $expectedPackage.Value) {
        $failures.Add(
            "Package '$($expectedPackage.Key)' resolved to '$($evaluatedPackages[$expectedPackage.Key])'; expected '$($expectedPackage.Value)'."
        )
    }
}

$overrideEnabled = [string] $evaluation.Properties.CentralPackageVersionOverrideEnabled
if ($overrideEnabled -cne 'false') {
    $failures.Add(
        "CentralPackageVersionOverrideEnabled resolved to '$overrideEnabled'; expected exact value 'false'."
    )
}

$commonsVersion = [string] $evaluation.Properties.HexalithCommonsVersion
if ($commonsVersion -cne '2.28.2') {
    $failures.Add("HexalithCommonsVersion resolved to '$commonsVersion'; expected '2.28.2'.")
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Authoritative package catalog tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine(
    "Authoritative package catalog tests passed for $($expectedPackages.Count) approved package values."
)
