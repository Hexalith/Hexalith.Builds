#!/usr/bin/env pwsh
#Requires -Version 7.6

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [string] $OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) "hexalith-platform-loader-$([Guid]::NewGuid().ToString('N'))"),

    [Parameter(DontShow = $true)]
    [ValidateRange(1, 60)]
    [int] $ChildTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Loader regression directory '$OutputDirectory' must not already exist."
}
$null = New-Item -ItemType Directory -Path $OutputDirectory
$extracted = Join-Path $OutputDirectory 'package'
[IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $extracted)
$payload = Join-Path $extracted 'tools/net10.0/any'
$hostDirectory = Join-Path $payload 'g4-host'
$hostAssembly = Join-Path $hostDirectory 'Hexalith.Builds.Module.AppHost.dll'
$rootTooling = Join-Path $payload 'Hexalith.Builds.Tooling.dll'
$hostTooling = Join-Path $hostDirectory 'Hexalith.Builds.Tooling.dll'
foreach ($file in @($hostAssembly, $rootTooling, $hostTooling)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package omitted '$file'." }
}

# Isolate configuration discovery even when output is beneath the checkout. No feed is used.
$fixture = Join-Path $OutputDirectory 'fixture'
$null = New-Item -ItemType Directory -Path $fixture
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../test/fixtures/module/platform-loader') -File |
    Where-Object Extension -in @('.cs', '.csproj') | Copy-Item -Destination $fixture
$nugetConfig = Join-Path $fixture 'NuGet.Config'
[IO.File]::WriteAllText($nugetConfig, '<configuration><packageSources><clear /></packageSources></configuration>')
$buildOutput = @(& dotnet build (Join-Path $fixture 'Hexalith.Platform.Composition.csproj') -c Debug `
    --configfile $nugetConfig "-p:ToolPayload=$payload" -p:ImportDirectoryBuildProps=false `
    -p:ImportDirectoryBuildTargets=false -p:ImportDirectoryPackagesProps=false -m:1 2>&1) -join "`n"
$buildExit = $LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'fixture-build.log'), $buildOutput)
if ($buildExit -ne 0) { throw "Loader fixture build failed: $buildOutput" }

$workspace = Join-Path $OutputDirectory 'workspace'
$null = New-Item -ItemType Directory -Path $workspace
$declaration = Join-Path $workspace 'declaration.json'
[IO.File]::WriteAllText($declaration, '{}')
$runId = [Guid]::NewGuid().ToString('N')
$planPath = Join-Path $workspace 'plan.json'
$plan = @{
    schema = 'hexalith.g4-run-plan.v1'; runId = $runId
    tenantNamespace = 'loader-fixture'; foreignTenantNamespace = 'loader-foreign'; resourceNamespace = 'loader-fixture'
    workspace = $workspace; daprHome = $workspace; readinessPath = (Join-Path $workspace 'readiness.json')
    daprConfigPath = ''; stateStoreComponentPath = ''; pubSubComponentPath = ''; isolatedResourcesPath = $workspace
    redisImage = ''; ports = @{}; modules = @(); uiMarkers = @()
    platformDeclarationPaths = @($declaration)
    platformDeclarationHash = (Get-FileHash -LiteralPath $declaration -Algorithm SHA256).Hash
    platformModelAssemblyPath = (Join-Path $fixture 'bin/Debug/net10.0/Hexalith.Platform.Composition.dll')
}
$plan | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $planPath -Encoding utf8

function Invoke-PackagedHostChild {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]] $Arguments,
        [Parameter(Mandatory = $true)] [Collections.IDictionary] $Environment
    )

    $childStart = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $childStart.WorkingDirectory = $hostDirectory
    $childStart.UseShellExecute = $false
    $childStart.RedirectStandardOutput = $true
    $childStart.RedirectStandardError = $true
    # An open, unwritten stdin keeps the AppHost stop listener from treating end-of-input as a stop request.
    $childStart.RedirectStandardInput = $true
    foreach ($argument in @('exec', $hostAssembly) + $Arguments) { $childStart.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $childStart.Environment[[string]$key] = [string]$Environment[$key] }
    $childStart.Environment['HEXALITH_G4_PLAN'] = $planPath
    $childStart.Environment['HEXALITH_G4_RUN_ID'] = $runId
    $childStart.Environment['HEXALITH_G4_SIGNING_KEY'] = 'synthetic-loader-fixture-only'
    $childStart.Environment['DOTNET_HOST_TRACE'] = '1'
    $childStart.Environment['DOTNET_HOST_TRACEFILE'] = Join-Path $OutputDirectory "$Name.host-trace.log"
    $child = [ordered]@{
        argv = @('dotnet', 'exec', $hostAssembly) + $Arguments; exitCode = $null
        timedOut = $false; diagnosticsComplete = $false; operationBoundSeconds = $ChildTimeoutSeconds
        stdout = Join-Path $OutputDirectory "$Name.stdout.log"; stderr = Join-Path $OutputDirectory "$Name.stderr.log"
    }
    # Record the child before any start, timeout or exit failure can throw, so the receipt keeps its facts.
    $receipt[$Name] = $child
    $childProcess = [Diagnostics.Process]::new()
    $childProcess.StartInfo = $childStart
    $stdoutFile = [IO.File]::Open($child.stdout, 'CreateNew', 'Write', 'Read')
    $stderrFile = [IO.File]::Open($child.stderr, 'CreateNew', 'Write', 'Read')
    $bound = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($ChildTimeoutSeconds))
    $childClock = [Diagnostics.Stopwatch]::StartNew()
    $milliseconds = $ChildTimeoutSeconds * 1000
    try {
        if (-not $childProcess.Start()) { throw "Could not start the packaged $Name child." }
        $stdout = $childProcess.StandardOutput.BaseStream.CopyToAsync($stdoutFile, $bound.Token)
        $stderr = $childProcess.StandardError.BaseStream.CopyToAsync($stderrFile, $bound.Token)
        if (-not $childProcess.WaitForExit([int]($milliseconds * 0.9))) {
            $child.timedOut = $true
            $childProcess.Kill($true)
            $null = $childProcess.WaitForExit([Math]::Max(0, $milliseconds - [int]$childClock.ElapsedMilliseconds))
        }
        $drain = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout, $stderr))
        try {
            $child.diagnosticsComplete = $drain.Wait([Math]::Max(0, $milliseconds - [int]$childClock.ElapsedMilliseconds))
        }
        catch [AggregateException] {
            if (-not $bound.IsCancellationRequested) { throw }
            $child.timedOut = $true
        }
        if (-not $child.diagnosticsComplete) { $child.timedOut = $true }
        if ($childProcess.HasExited) { $child.exitCode = $childProcess.ExitCode }
        $stdoutFile.Flush()
        $stderrFile.Flush()
    }
    finally {
        $bound.Cancel()
        $stdoutFile.Dispose()
        $stderrFile.Dispose()
        $childProcess.Dispose()
        $bound.Dispose()
        $child['durationMilliseconds'] = $childClock.ElapsedMilliseconds
    }

    if ($child.timedOut) { throw "Packaged $Name child timed out within its $ChildTimeoutSeconds-second execution/collection ceiling; partial logs retained." }
    if ($child.exitCode -ne 0) { throw "Packaged $Name child exited $($child.exitCode): $(Get-Content -LiteralPath $child.stderr -Raw)" }
    return $child
}

$receipt = [ordered]@{
    package = $PackagePath; packageSha256 = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
    result = 'failed'; validation = $null; runtime = $null
    rootTooling = @{ path = $rootTooling; identity = [Reflection.AssemblyName]::GetAssemblyName($rootTooling).FullName; sha256 = (Get-FileHash -LiteralPath $rootTooling -Algorithm SHA256).Hash }
    hostTooling = @{ path = $hostTooling; identity = [Reflection.AssemblyName]::GetAssemblyName($hostTooling).FullName; sha256 = (Get-FileHash -LiteralPath $hostTooling -Algorithm SHA256).Hash }
}
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    $receipt.validation = Invoke-PackagedHostChild -Name 'validation' -Arguments @('--validate-platform') -Environment @{}
    $loaded = Get-Content -LiteralPath (Join-Path $workspace 'loaded-contract.json') -Raw | ConvertFrom-Json
    if ($loaded.identity -cne $receipt.rootTooling.identity -or $loaded.path -cne $hostTooling -or
        $receipt.rootTooling.sha256 -cne $receipt.hostTooling.sha256) {
        throw 'Packaged child did not load the same Tooling contract as the CLI/model.'
    }
    if (Test-Path -LiteralPath $plan.readinessPath) { throw 'Validation unexpectedly wrote runtime readiness.' }
    if (Test-Path -LiteralPath (Join-Path $workspace 'startup-attempted')) { throw 'Validation unexpectedly attempted runtime startup.' }

    # Runtime start: the packaged AppHost carries the build machine's AppHost, DCP and dashboard paths. Resolve this
    # machine's paths exactly as the runner does, with the packaged Tooling, and require the host to use them. The
    # resolution runs in a child pwsh: callers run this gate in their own process and later load the SDK's
    # NuGet.Versioning, which must not collide with the payload's copy.
    $resolver = Join-Path $OutputDirectory 'resolve-aspire-paths.ps1'
    [IO.File]::WriteAllText($resolver, @'
param([Parameter(Mandatory = $true)] [string] $Payload, [Parameter(Mandatory = $true)] [string] $Workspace)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$null = [Reflection.Assembly]::LoadFrom((Join-Path $Payload 'NuGet.Versioning.dll'))
$tooling = [Reflection.Assembly]::LoadFrom((Join-Path $Payload 'Hexalith.Builds.Tooling.dll'))
$pathsType = $tooling.GetType('Hexalith.Builds.Tooling.Runtime.CompositionAspireHostPaths', $true)
$pinsType = $tooling.GetType('Hexalith.Builds.Tooling.Runtime.CompositionToolchainPins', $true)
$resolved = $null
$unavailable = $pathsType::TryResolve($pathsType::DefaultPackagesRoot(), $pinsType::AspireAppHostSdkVersion, $pathsType::CurrentRuntimeIdentifier(), [ref]$resolved)
if ($null -ne $unavailable) {
    [Console]::Error.WriteLine("$($unavailable.RuleId) $($unavailable.Message)")
    exit 3
}
$created = $resolved.CreateEnvironment($Workspace)
$environment = [ordered]@{}
foreach ($key in $created.Keys) { $environment[$key] = $created[$key] }
$environment | ConvertTo-Json -Compress
'@)
    $resolverErrors = Join-Path $OutputDirectory 'resolve-aspire-paths.stderr.log'
    $pwshExecutable = @(Get-Command pwsh -CommandType Application -ErrorAction Stop)[0].Source
    $resolverOutput = @(& $pwshExecutable -NoProfile -File $resolver -Payload $payload -Workspace $workspace 2> $resolverErrors)
    if ($LASTEXITCODE -ne 0) {
        throw "Packaged runtime start cannot resolve local Aspire orchestration (exit $LASTEXITCODE): $(Get-Content -LiteralPath $resolverErrors -Raw)"
    }
    $aspireEnvironment = ($resolverOutput -join "`n") | ConvertFrom-Json -AsHashtable
    $receipt.runtime = Invoke-PackagedHostChild -Name 'runtime' -Arguments @() -Environment $aspireEnvironment
    $runtimePathsFile = Join-Path $workspace 'runtime-paths.json'
    if (-not (Test-Path -LiteralPath $runtimePathsFile -PathType Leaf)) {
        throw "Packaged runtime start did not reach Aspire store creation: $(Get-Content -LiteralPath $receipt.runtime.stderr -Raw)"
    }
    $observed = Get-Content -LiteralPath $runtimePathsFile -Raw | ConvertFrom-Json
    $receipt.runtime['observed'] = $observed
    $expectedStore = Join-Path $aspireEnvironment['HEXALITH_G4_ASPIRE_STORE_PATH'] '.aspire'
    if ($observed.storeBasePath -cne $expectedStore -or -not $observed.storeExists) {
        throw "Packaged runtime used Aspire store '$($observed.storeBasePath)' instead of the run-private '$expectedStore'."
    }
    if ($observed.appHostDirectory -cne $aspireEnvironment['HEXALITH_G4_APPHOST_DIRECTORY']) {
        throw "Packaged runtime used AppHost directory '$($observed.appHostDirectory)' instead of '$($aspireEnvironment['HEXALITH_G4_APPHOST_DIRECTORY'])'."
    }
    if ($observed.dcpCliPath -cne $aspireEnvironment['DcpPublisher__CliPath'] -or $observed.dashboardPath -cne $aspireEnvironment['DcpPublisher__DashboardPath']) {
        throw "Packaged runtime did not receive this machine's DCP '$($aspireEnvironment['DcpPublisher__CliPath'])' and dashboard '$($aspireEnvironment['DcpPublisher__DashboardPath'])'."
    }
    if (Test-Path -LiteralPath $plan.readinessPath) { throw 'The runtime start unexpectedly wrote readiness; resources may have started.' }
    $receipt.result = 'passed'
    Write-Host "Packaged Platform loader passed offline; contract $($loaded.identity); runtime start reached the run-private Aspire store '$expectedStore'; no resources started. Receipt: $OutputDirectory"
}
finally {
    $receipt['durationMilliseconds'] = $clock.ElapsedMilliseconds
    $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'loader-result.json') -Encoding utf8
}
