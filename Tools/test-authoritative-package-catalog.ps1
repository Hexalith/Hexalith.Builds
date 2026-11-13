[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$catalogPath = Join-Path $PSScriptRoot '../Props/Directory.Packages.props'
# SDK startup notices belong to stderr and cannot become catalog JSON.
$evaluationDirectory = Join-Path ([IO.Path]::GetTempPath()) "catalog-evaluation-$([Guid]::NewGuid().ToString('N'))"
try {
    [void] [IO.Directory]::CreateDirectory($evaluationDirectory)
    $stdout = Join-Path $evaluationDirectory 'stdout'
    $stderr = Join-Path $evaluationDirectory 'stderr'
    & dotnet msbuild $catalogPath -nologo -nr:false `
        -getProperty:CentralPackageVersionOverrideEnabled `
        -getProperty:HexalithCommonsVersion `
        -getProperty:HexalithEventStoreVersion `
        -getProperty:HexalithMcpCliVersion `
        -getProperty:HexalithTenantsVersion `
        -getProperty:HexalithFrontComposerVersion `
        -getItem:PackageVersion 1> $stdout 2> $stderr
    $exitCode = $LASTEXITCODE
    $evaluationText = [IO.File]::ReadAllText($stdout)
    $diagnostic = [IO.File]::ReadAllText($stderr)
    if ($exitCode -ne 0) { throw "Catalog evaluation failed with exit code $exitCode.`n$evaluationText`n$diagnostic" }
    if ($diagnostic) { [Console]::Error.Write($diagnostic) }
}
finally { if (Test-Path -LiteralPath $evaluationDirectory) { [IO.Directory]::Delete($evaluationDirectory, $true) } }

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
    'Hexalith.EventStore.' = @{ Property = 'HexalithEventStoreVersion'; Version = '5.0.0' }
    'Hexalith.McpCli.' = @{ Property = 'HexalithMcpCliVersion'; Version = '2.0.1' }
    'Hexalith.Tenants.' = @{ Property = 'HexalithTenantsVersion'; Version = '7.0.7' }
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

# Clean project-name probes keep the retained SDK4.2 views finite and independently evaluated.
$partiesSdk42Consumers = @(
    'Hexalith.Parties'
    'Hexalith.Parties.AdminPortal'
    'Hexalith.Parties.AdminPortal.Tests'
    'Hexalith.Parties.AppHost'
    'Hexalith.Parties.Aspire'
    'Hexalith.Parties.Aspire.Tests'
    'Hexalith.Parties.Authentication'
    'Hexalith.Parties.Authentication.Tests'
    'Hexalith.Parties.Catalog.Tests'
    'Hexalith.Parties.Ci.Tests'
    'Hexalith.Parties.Client'
    'Hexalith.Parties.Client.Tests'
    'Hexalith.Parties.ConsumerPortal'
    'Hexalith.Parties.ConsumerPortal.Tests'
    'Hexalith.Parties.Contracts'
    'Hexalith.Parties.Contracts.Tests'
    'Hexalith.Parties.EventStoreAdminUI.TestHost'
    'Hexalith.Parties.EventStoreExtensions'
    'Hexalith.Parties.EventStoreGateway.TestHost'
    'Hexalith.Parties.EventStoreSample.Contracts.TestArtifact'
    'Hexalith.Parties.EventStoreSample.TestHost'
    'Hexalith.Parties.IntegrationTests'
    'Hexalith.Parties.Mcp'
    'Hexalith.Parties.Mcp.Tests'
    'Hexalith.Parties.Picker'
    'Hexalith.Parties.Picker.Tests'
    'Hexalith.Parties.Platform.AttachedLifecycleTests'
    'Hexalith.Parties.Platform.IntegrationTests'
    'Hexalith.Parties.Platform.LifecycleTests'
    'Hexalith.Parties.Projections'
    'Hexalith.Parties.Projections.Tests'
    'Hexalith.Parties.Sample'
    'Hexalith.Parties.Sample.Tests'
    'Hexalith.Parties.Security'
    'Hexalith.Parties.Security.Tests'
    'Hexalith.Parties.Server.Tests'
    'Hexalith.Parties.Testing'
    'Hexalith.Parties.Tests'
    'Hexalith.Parties.UI'
    'Hexalith.Parties.UI.Tests'
)
$platformSdk42Consumers = @(
    'Hexalith.Platform.CatalogBuilder'
    'Hexalith.Platform.Composition'
    'Hexalith.Platform.Composition.Tests'
    'Hexalith.Platform.Custody'
    'Hexalith.Platform.Custody.Component'
    'Hexalith.Platform.Custody.Component.Tests'
    'Hexalith.Platform.Custody.Tests'
    'Hexalith.Platform.EventStoreHost'
    'Hexalith.Platform.Identity'
    'Hexalith.Platform.Identity.Tests'
    'Hexalith.Platform.OwnerServices.Tests'
    'Hexalith.Platform.Protection'
    'Hexalith.Platform.Protection.Qualification.Enrollment'
    'Hexalith.Platform.Protection.Qualification.Executor'
    'Hexalith.Platform.Protection.Qualification.Observer'
    'Hexalith.Platform.Protection.Tests'
    'Hexalith.Platform.Secrets.ArchiveProcessFixture'
    'Hexalith.Platform.Secrets.Authority'
    'Hexalith.Platform.Secrets.Authority.Tests'
    'Hexalith.Platform.Secrets.Custody'
    'Hexalith.Platform.Secrets.Custody.Tests'
    'Hexalith.Platform.Secrets.Inventory'
    'Hexalith.Platform.Secrets.Inventory.Tests'
    'Hexalith.Platform.Secrets.Qualification.Enrollment'
    'Hexalith.Platform.Secrets.Qualification.Executor'
    'Hexalith.Platform.Secrets.Qualification.Observer'
    'Hexalith.Platform.Secrets.Runtime'
    'Hexalith.Platform.Secrets.Runtime.Tests'
    'Hexalith.Platform.TenantRegistry.Host'
    'Hexalith.Platform.TestExtension'
    'Hexalith.Platform.TestExtension.Qualification.Tests'
    'Hexalith.Platform.Works.Runtime'
    'Hexalith.Platform.Works.Tests'
)
$buildsHostSdk42Consumers = @('Hexalith.Builds.Module.AppHost', 'Hexalith.Builds.Module.EventStoreHost', 'Hexalith.Builds.Module.UiHost')
$viewProbeRoot = Join-Path ([IO.Path]::GetTempPath()) "sdk42-catalog-views-$([Guid]::NewGuid().ToString('N'))"
$viewCount = 0
function Test-OwnedCatalogView {
    param([string] $ProjectName, [string] $ExpectedSdk, [string] $ExpectedAspNetOpenApi,
          [string] $ExpectedOpenApi, [string] $ExpectedPartiesView, [string] $ExpectedPlatformView,
          [string] $SelectedSdk = '', [string] $ExpectedBuildsHostView = 'false',
          [string] $ExpectedOpenIdConnect = '11.0.0', [string] $ExpectedFrontComposerView = 'false')
    $probe = Join-Path $viewProbeRoot "$ProjectName.proj"
    $escapedCatalog = [Security.SecurityElement]::Escape([IO.Path]::GetFullPath($catalogPath))
    [IO.File]::WriteAllText($probe, "<Project><Import Project=`"$escapedCatalog`" /></Project>")
    $stdout = Join-Path $viewProbeRoot 'evaluation.stdout'
    $stderr = Join-Path $viewProbeRoot 'evaluation.stderr'
    $properties = @(if ($SelectedSdk) { "-p:HexalithEventStoreVersion=$SelectedSdk" })
    & dotnet msbuild $probe -nologo -noAutoResponse -nr:false @properties `
        -getProperty:HexalithEventStoreVersion,HexalithPartiesSdk42CatalogView,HexalithPlatformSdk42CatalogView,HexalithBuildsPackagedHostSdk42CatalogView,HexalithFrontComposerNet10CatalogView `
        -getItem:PackageVersion 1> $stdout 2> $stderr
    if ($LASTEXITCODE -ne 0) { throw "Clean catalog view '$ProjectName' failed: $([IO.File]::ReadAllText($stderr))" }
    $observed = [IO.File]::ReadAllText($stdout) | ConvertFrom-Json
    $pins = @{}
    foreach ($row in @($observed.Items.PackageVersion)) { $pins[$row.Identity] = $row.Version }
    if ($observed.Properties.HexalithEventStoreVersion -cne $ExpectedSdk -or
        $observed.Properties.HexalithPartiesSdk42CatalogView -cne $ExpectedPartiesView -or
        $observed.Properties.HexalithPlatformSdk42CatalogView -cne $ExpectedPlatformView -or
        $observed.Properties.HexalithBuildsPackagedHostSdk42CatalogView -cne $ExpectedBuildsHostView -or
        $observed.Properties.HexalithFrontComposerNet10CatalogView -cne $ExpectedFrontComposerView -or
        $pins['Microsoft.AspNetCore.Authentication.OpenIdConnect'] -cne $ExpectedOpenIdConnect -or
        $pins['Microsoft.AspNetCore.OpenApi'] -cne $ExpectedAspNetOpenApi -or
        $pins['Microsoft.OpenApi'] -cne $ExpectedOpenApi) {
        $failures.Add("Clean catalog view '$ProjectName' changed its exact SDK/OpenAPI tuple or owner identity.")
    }
    foreach ($member in @($observed.Items.PackageVersion | Where-Object { $_.Identity.StartsWith('Hexalith.EventStore.', [StringComparison]::Ordinal) })) {
        if ($member.Version -cne $ExpectedSdk) { $failures.Add("Clean catalog view '$ProjectName' split its canonical EventStore family.") }
    }
    foreach ($row in @($observed.Items.PackageVersion)) {
        if (-not $row.Identity.StartsWith('Hexalith.EventStore.', [StringComparison]::Ordinal) -and
            $row.Identity -notin @('Microsoft.AspNetCore.OpenApi', 'Microsoft.OpenApi', 'Microsoft.AspNetCore.Authentication.OpenIdConnect') -and
            $row.Version -cne $evaluatedPackages[$row.Identity]) {
            $failures.Add("Clean catalog view '$ProjectName' changes unrelated package '$($row.Identity)'.")
        }
    }
    $script:viewCount++
}
try {
    [void] [IO.Directory]::CreateDirectory($viewProbeRoot)
    foreach ($name in $partiesSdk42Consumers) { Test-OwnedCatalogView $name '4.2.0' '10.0.15' '2.13.0' 'true' 'false' }
    foreach ($name in $platformSdk42Consumers) { Test-OwnedCatalogView $name '4.2.0' '10.0.15' '2.13.0' 'false' 'true' }
    foreach ($name in $buildsHostSdk42Consumers) { Test-OwnedCatalogView $name '4.2.0' '10.0.15' '2.13.0' 'false' 'false' -ExpectedBuildsHostView 'true' }
    foreach ($name in @('Unrelated.Consumer', 'Hexalith.Parties.Future', 'Hexalith.Parties.Tests.Future',
                        'hexalith.Parties.Tests', 'Hexalith.Platform.Future', 'Hexalith.Platform.EventStoreHost.Future',
                        'Hexalith.Builds.Module.AppHost.Future', 'Hexalith.Builds.Module.EventStoreHost.Future', 'Hexalith.Builds.Module.UiHost.Future',
                        'hexalith.Builds.Module.AppHost', 'hexalith.Builds.Module.EventStoreHost', 'hexalith.Builds.Module.UiHost')) {
        Test-OwnedCatalogView $name '5.0.0' '11.0.0' '3.11.0' 'false' 'false'
    }
    foreach ($name in (@('Hexalith.Parties.Tests', 'Hexalith.Platform.EventStoreHost') + $buildsHostSdk42Consumers)) {
        $partiesView = if ($name -eq 'Hexalith.Parties.Tests') { 'true' } else { 'false' }
        $platformView = if ($name -eq 'Hexalith.Platform.EventStoreHost') { 'true' } else { 'false' }
        $buildsHostView = if ($name -cin $buildsHostSdk42Consumers) { 'true' } else { 'false' }
        foreach ($sdk in @('4.0.4', '4.1.0', '4.3.0', '5.0.0')) {
            Test-OwnedCatalogView $name $sdk '11.0.0' '3.11.0' $partiesView $platformView $sdk -ExpectedBuildsHostView $buildsHostView
        }
    }
    # Include the supported source samples: their central transitive pins consume Shell too.
    foreach ($name in @('Counter.Domain', 'Counter.Specimens', 'Counter.Specimens.Domain', 'Counter.Web', 'IdeParityCounter',
                        'Hexalith.FrontComposer.AppHost', 'Hexalith.FrontComposer.Shell', 'Hexalith.FrontComposer.Shell.Tests',
                        'Hexalith.FrontComposer.Shell.Tests.Bench', 'Hexalith.FrontComposer.SourceTools.Tests',
                        'Hexalith.FrontComposer.Testing', 'Hexalith.FrontComposer.Testing.Tests',
                        'Hexalith.FrontComposer.UI', 'Hexalith.FrontComposer.UI.Tests')) {
        Test-OwnedCatalogView $name '5.0.0' '11.0.0' '3.11.0' 'false' 'false' `
            -ExpectedOpenIdConnect '10.0.15' -ExpectedFrontComposerView 'true'
    }
    foreach ($name in @('Hexalith.Memories.Server', 'Hexalith.Memories.Web', 'Hexalith.FrontComposer.Future',
                        'Hexalith.FrontComposer.Shell.Future', 'Counter.Web.Future', 'Counter.Specimens.Domain.Future',
                        'counter.Web', 'ideParityCounter', 'hexalith.FrontComposer.Shell')) {
        Test-OwnedCatalogView $name '5.0.0' '11.0.0' '3.11.0' 'false' 'false'
    }
    Write-Output "Owned catalog views passed $viewCount exact positive/negative probes."
}
finally { if (Test-Path -LiteralPath $viewProbeRoot) { [IO.Directory]::Delete($viewProbeRoot, $true) } }

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

# Producer host views and frozen v1 evidence have independent authorities.
. (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1')
$producerControls = 0
$producerRoot = Join-Path ([IO.Path]::GetTempPath()) "producer-host-controls-$([Guid]::NewGuid().ToString('N'))"
try {
    [void] [IO.Directory]::CreateDirectory($producerRoot)
    $frozenPins = Get-FrozenV1PlatformPins
    if ($frozenPins.eventStoreVersion -cne '4.2.0') { $failures.Add('Frozen v1 evidence lost its independent SDK4.2 schema authority.') }
    $producerControls++
    if ((Assert-PackagedHostCatalogViews -CatalogPath $catalogPath) -cne '4.2.0') { $failures.Add('Actual producer host view was not independently qualified.') }
    $producerControls++
    [xml] $savedCatalog = [IO.File]::ReadAllText($catalogPath)
    $savedCatalogPath = Join-Path $producerRoot 'unchanged-saved.props'
    [IO.File]::WriteAllText($savedCatalogPath, $savedCatalog.OuterXml, [Text.UTF8Encoding]::new($true))
    if ((Assert-PackagedHostCatalogViews -CatalogPath $savedCatalogPath) -cne '4.2.0') { $failures.Add('Saving an unchanged catalog changed producer evaluation.') }
    $producerControls++
    $previousSdk = $env:HexalithEventStoreVersion
    try {
        $env:HexalithEventStoreVersion = '99.0.0'
        if ((Assert-PackagedHostCatalogViews -CatalogPath $catalogPath) -cne '4.2.0') { $failures.Add('Producer role evaluation inherited the caller SDK.') }
        $producerControls++
    }
    finally { $env:HexalithEventStoreVersion = $previousSdk }
    foreach ($fault in @('unknown-role', 'broad-selector', 'duplicate-role', 'missing-role', 'global-selector', 'sdk5', 'aspnet11', 'openapi3', 'crossed-role')) {
        [xml] $mutated = [IO.File]::ReadAllText($catalogPath)
        $selectors = @($mutated.SelectNodes('/Project/PropertyGroup/HexalithBuildsPackagedHostSdk42CatalogView'))
        $condition = $selectors[1].GetAttribute('Condition')
        switch ($fault) {
            'unknown-role' { $selectors[1].SetAttribute('Condition', $condition.Replace('Hexalith.Builds.Module.UiHost', 'Hexalith.Builds.Module.UnknownHost')) }
            'broad-selector' { $selectors[1].SetAttribute('Condition', "`$(MSBuildProjectName.StartsWith('Hexalith.Builds.Module.'))") }
            'duplicate-role' { $selectors[1].SetAttribute('Condition', $condition + " or `$(MSBuildProjectName.Equals('Hexalith.Builds.Module.UiHost'))") }
            'missing-role' { $selectors[1].SetAttribute('Condition', ($condition -split '\s+or\s+', 2)[1]) }
            'global-selector' { $selectors[0].InnerText = 'true' }
            'sdk5' { $mutated.SelectNodes('/Project/PropertyGroup/HexalithEventStoreVersion')[0].InnerText = '5.0.0' }
            'aspnet11' { $mutated.SelectNodes('/Project/PropertyGroup/HexalithAspNetCoreOpenApiVersion')[1].InnerText = '11.0.0' }
            'openapi3' { $mutated.SelectNodes('/Project/PropertyGroup/HexalithOpenApiVersion')[1].InnerText = '3.11.0' }
            'crossed-role' {
                $group = $mutated.CreateElement('PropertyGroup')
                $group.SetAttribute('Condition', "`$(MSBuildProjectName.Equals('Hexalith.Builds.Module.UiHost'))")
                $version = $mutated.CreateElement('HexalithEventStoreVersion')
                $version.InnerText = '5.0.0'
                $null = $group.AppendChild($version)
                $null = $mutated.DocumentElement.AppendChild($group)
            }
        }
        $mutatedPath = Join-Path $producerRoot "$fault.props"
        [IO.File]::WriteAllText($mutatedPath, $mutated.OuterXml, [Text.UTF8Encoding]::new($true))
        $refused = $false
        try { $null = Assert-PackagedHostCatalogViews -CatalogPath $mutatedPath }
        catch {
            $expectedRefusal = if ($fault -eq 'global-selector') { 'exact finite default-false' }
                elseif ($fault -in @('unknown-role', 'broad-selector', 'duplicate-role', 'missing-role')) { 'exactly the three admitted producer roles' }
                else { 'changed its admitted SDK4.2/OpenAPI tuple' }
            $refused = $_.Exception.Message.Contains($expectedRefusal, [StringComparison]::Ordinal)
            if (-not $refused) { $failures.Add("Producer '$fault' refused for an unrelated reason: $($_.Exception.Message)") }
        }
        if (-not $refused) { $failures.Add("Producer role verifier admitted '$fault' or did not prove its intended refusal.") }
        $producerControls++
    }
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
    "Authoritative package catalog tests passed for $($requiredPackages.Count) approved package identities, $($sharedPackageVersions.Count) shared package versions, and $($cohortFamilies.Count) canonical families with overrides prohibited."
)
