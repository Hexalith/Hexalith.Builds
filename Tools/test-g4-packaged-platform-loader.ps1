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

$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.WorkingDirectory = $hostDirectory
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('exec', $hostAssembly, '--validate-platform')) { $start.ArgumentList.Add($argument) }
$start.Environment['HEXALITH_G4_PLAN'] = $planPath
$start.Environment['HEXALITH_G4_RUN_ID'] = $runId
$start.Environment['HEXALITH_G4_SIGNING_KEY'] = 'synthetic-loader-fixture-only'
$start.Environment['DOTNET_HOST_TRACE'] = '1'
$start.Environment['DOTNET_HOST_TRACEFILE'] = Join-Path $OutputDirectory 'host-trace.log'
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$receipt = [ordered]@{
    package = $PackagePath; packageSha256 = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
    argv = @('dotnet', 'exec', $hostAssembly, '--validate-platform'); exitCode = $null
    result = 'failed'; timedOut = $false; diagnosticsComplete = $false; operationBoundSeconds = $ChildTimeoutSeconds
    rootTooling = @{ path = $rootTooling; identity = [Reflection.AssemblyName]::GetAssemblyName($rootTooling).FullName; sha256 = (Get-FileHash -LiteralPath $rootTooling -Algorithm SHA256).Hash }
    hostTooling = @{ path = $hostTooling; identity = [Reflection.AssemblyName]::GetAssemblyName($hostTooling).FullName; sha256 = (Get-FileHash -LiteralPath $hostTooling -Algorithm SHA256).Hash }
}
$stdoutFile = [IO.File]::Open((Join-Path $OutputDirectory 'child.stdout.log'), 'CreateNew', 'Write', 'Read')
$stderrFile = [IO.File]::Open((Join-Path $OutputDirectory 'child.stderr.log'), 'CreateNew', 'Write', 'Read')
$bound = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($ChildTimeoutSeconds))
$clock = [Diagnostics.Stopwatch]::StartNew()
$milliseconds = $ChildTimeoutSeconds * 1000
try {
    if (-not $process.Start()) { throw 'Could not start the packaged validation child.' }
    $stdout = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile, $bound.Token)
    $stderr = $process.StandardError.BaseStream.CopyToAsync($stderrFile, $bound.Token)
    if (-not $process.WaitForExit([int]($milliseconds * 0.9))) {
        $receipt.timedOut = $true
        $process.Kill($true)
        $null = $process.WaitForExit([Math]::Max(0, $milliseconds - [int]$clock.ElapsedMilliseconds))
    }
    $drain = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout, $stderr))
    try {
        $receipt.diagnosticsComplete = $drain.Wait([Math]::Max(0, $milliseconds - [int]$clock.ElapsedMilliseconds))
    }
    catch [AggregateException] {
        if (-not $bound.IsCancellationRequested) { throw }
        $receipt.timedOut = $true
    }
    if (-not $receipt.diagnosticsComplete) { $receipt.timedOut = $true }
    if ($process.HasExited) { $receipt.exitCode = $process.ExitCode }
    $stdoutFile.Flush()
    $stderrFile.Flush()
    if ($receipt.timedOut) { throw "Packaged validation child timed out within its $ChildTimeoutSeconds-second execution/collection ceiling; partial logs retained." }
    if ($process.ExitCode -ne 0) { throw "Packaged validation child exited $($process.ExitCode): $(Get-Content -LiteralPath (Join-Path $OutputDirectory 'child.stderr.log') -Raw)" }
    $loaded = Get-Content -LiteralPath (Join-Path $workspace 'loaded-contract.json') -Raw | ConvertFrom-Json
    if ($loaded.identity -cne $receipt.rootTooling.identity -or $loaded.path -cne $hostTooling -or
        $receipt.rootTooling.sha256 -cne $receipt.hostTooling.sha256) {
        throw 'Packaged child did not load the same Tooling contract as the CLI/model.'
    }
    if (Test-Path -LiteralPath $plan.readinessPath) { throw 'Validation unexpectedly wrote runtime readiness.' }
    if (Test-Path -LiteralPath (Join-Path $workspace 'startup-attempted')) { throw 'Validation unexpectedly attempted runtime startup.' }
    $receipt.result = 'passed'
    Write-Host "Packaged Platform loader passed offline; contract $($loaded.identity); no resources started. Receipt: $OutputDirectory"
}
finally {
    $bound.Cancel()
    $stdoutFile.Dispose()
    $stderrFile.Dispose()
    $process.Dispose()
    $bound.Dispose()
    $receipt['durationMilliseconds'] = $clock.ElapsedMilliseconds
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'loader-result.json') -Encoding utf8
}
