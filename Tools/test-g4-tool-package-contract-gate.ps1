#!/usr/bin/env pwsh

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$qualificationPath = Join-Path $PSScriptRoot 'test-g4-tool-package-contracts.ps1'
$buildPath = Join-Path $PSScriptRoot 'build-g4-tool-packages.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-g4-gate-$([System.Guid]::NewGuid().ToString('N'))"
# Synthetic packages include a real catalog-bearing assembly so the installed catalog check remains mandatory.
$dotnetExecutable = @(Get-Command dotnet -CommandType Application -ErrorAction Stop)[0].Source
$catalogToolingProject = Join-Path $PSScriptRoot '../src/libraries/Hexalith.Builds.Tooling/Hexalith.Builds.Tooling.csproj'
$catalogToolingAssembly = Join-Path $PSScriptRoot '../src/libraries/Hexalith.Builds.Tooling/bin/Debug/net10.0/Hexalith.Builds.Tooling.dll'
$catalogBuildOutput = @(& $dotnetExecutable build $catalogToolingProject -c Debug -m:1 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "Catalog tooling build for gate controls failed: $catalogBuildOutput" }

$global:HexalithG4GateDotNetMode = 'fail'

function global:dotnet {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [object[]] $ForwardedArguments
    )

    if ($global:HexalithG4GateDotNetMode -ceq 'build-control') {
        $arguments = @($ForwardedArguments | ForEach-Object { [string]$_ })
        $global:HexalithG4BuildCalls.Add($arguments)
        if ($arguments[0] -ceq 'pack') {
            $output = $arguments[[Array]::IndexOf($arguments, '--output') + 1]
            $id = [IO.Path]::GetFileNameWithoutExtension($arguments[1])
            foreach ($extension in @('nupkg', 'snupkg')) {
                [IO.File]::WriteAllText((Join-Path $output "$id.4.31.2.$extension"), 'synthetic package')
            }
        }
        $global:LASTEXITCODE = 0
        return
    }

    if ($global:HexalithG4GateDotNetMode -ceq 'fail') {
        $global:LASTEXITCODE = 97
        return
    }

    $arguments = @($ForwardedArguments | ForEach-Object { [string]$_ })
    $joinedArguments = $arguments -join ' '
    if ($joinedArguments -like '*tool run hexalith-module*run --help*') {
        'Starts a supported module runtime.'
    }
    elseif ($joinedArguments -like '*tool run hexalith-module*down --help*') {
        'Tears down runner-owned module resources.'
    }
    elseif ($joinedArguments -like '*tool run hexalith-module*test --help*') {
        'Runs a named module qualification profile.'
    }
    elseif ($joinedArguments -like '*tool run hexalith-module*validate --help*') {
        'Validates local Platform module declarations without starting resources.'
    }
    elseif ($joinedArguments -like '*tool run hexalith-module*validate*platform-legacy.json*') {
        '{"status":"failed","outcome":{"exitCode":"UsageOrManifest","phase":"Manifest","category":"Manifest","ruleId":"HXP001"},"diagnostics":[{"ruleId":"HXP001","source":"platform-legacy.json","field":"schema","message":"Legacy v1 cannot enroll."}]}' + "`n"
        $global:LASTEXITCODE = 1
        return
    }
    elseif ($joinedArguments -like '*tool run hexalith-module*validate*--manifest platform-valid.json --manifest platform-valid.json*') {
        '{"status":"failed","outcome":{"exitCode":"UsageOrManifest","phase":"Manifest","category":"Manifest","ruleId":"HXP003"},"diagnostics":[{"ruleId":"HXP003","source":"platform-valid.json","field":"modules[0].identity.moduleId","message":"Duplicate identity."},{"ruleId":"HXP003","source":"platform-valid.json","field":"modules[0].identity.servers[0].appId","message":"Duplicate identity."},{"ruleId":"HXP003","source":"platform-valid.json","field":"modules[0].identity.servers[0].resourceId","message":"Duplicate identity."}]}' + "`n"
        $global:LASTEXITCODE = 1
        return
    }
    elseif ($joinedArguments -like '*tool run hexalith-module*validate*') {
        '{"status":"validated","outcome":{"exitCode":"Success","phase":"None","category":"None","ruleId":null},"diagnostics":[]}' + "`n"
    }
    elseif ($joinedArguments -like '*tool run hexalith-module*--help*') {
        'Runs supported Hexalith module qualifications.'
    }
    elseif ($joinedArguments -like '*tool run hexalith-evidence*validate --help*') {
        'Validates a hexalith.readiness-evidence.v1 YAML matrix or hexalith.g4-p0-acceptance.v1 JSON record.'
    }
    elseif ($joinedArguments -like '*tool run hexalith-evidence*--help*') {
        'Validates deterministic Hexalith readiness evidence.'
    }
    $global:LASTEXITCODE = 0
}

try {
    $null = New-Item -ItemType Directory -Path $testRoot

    # Exercise the real official wrapper through its post-pack loader refusal boundary.
    $buildControl = Join-Path $testRoot 'build-control'
    $null = New-Item -ItemType Directory -Path (Join-Path $buildControl 'Tools') -Force
    Copy-Item -LiteralPath $buildPath -Destination (Join-Path $buildControl 'Tools/build-g4-tool-packages.ps1')
    [IO.File]::WriteAllText((Join-Path $buildControl 'Hexalith.Builds.slnx'), '<Solution />')
    foreach ($id in @('Hexalith.Builds.Module.Cli', 'Hexalith.Builds.Evidence.Cli')) {
        $project = Join-Path $buildControl "src/libraries/$id"
        $null = New-Item -ItemType Directory -Path $project -Force
        [IO.File]::WriteAllText((Join-Path $project "$id.csproj"), '<Project />')
    }
    [IO.File]::WriteAllText((Join-Path $buildControl 'Tools/test-g4-packaged-platform-loader.ps1'),
        'param($PackagePath, $OutputDirectory); throw "Synthetic loader refusal."')
    $global:HexalithG4BuildCalls = [Collections.Generic.List[string[]]]::new()
    $global:HexalithG4GateDotNetMode = 'build-control'
    $loaderRefusalOutput = Join-Path $buildControl 'packages'
    try {
        & (Join-Path $buildControl 'Tools/build-g4-tool-packages.ps1') -Version '4.31.2' -OutputDirectory $loaderRefusalOutput
        throw 'A loader refusal exposed successful package inventory.'
    }
    catch {
        if (-not $_.Exception.Message.Contains('Synthetic loader refusal.', [StringComparison]::Ordinal)) { throw }
    }
    finally {
        $global:HexalithG4GateDotNetMode = 'fail'
    }
    if ($global:HexalithG4BuildCalls.Count -ne 4 -or
        ($global:HexalithG4BuildCalls | ForEach-Object { $_[0] }) -join ',' -cne 'restore,build,pack,pack') {
        throw 'The official package wrapper skipped a build phase.'
    }
    foreach ($call in $global:HexalithG4BuildCalls) {
        if ($call -cnotcontains '-p:Version=4.31.2' -or $call -cnotcontains '-p:PackageVersion=4.31.2') {
            throw 'Restore, build and pack must receive identical selected version properties.'
        }
    }
    if (@(Get-ChildItem -LiteralPath $loaderRefusalOutput -File).Count -ne 4 -or
        (Test-Path -LiteralPath (Join-Path $loaderRefusalOutput 'g4-tool-package-inventory.json'))) {
        throw 'A loader refusal must retain all four packages without successful inventory.'
    }
    Write-Host 'Official package wrapper controls passed: version propagation and post-pack loader refusal.'

    $reusedDirectory = Join-Path $testRoot 'reused'
    $null = New-Item -ItemType Directory -Path $reusedDirectory
    try {
        & $qualificationPath -Version '0.0.0-gate.1' -PackageDirectory $reusedDirectory -SkipSourceValidation
        throw 'A reused qualification directory was accepted.'
    }
    catch {
        if (-not $_.Exception.Message.Contains('must not already exist', [System.StringComparison]::Ordinal)) {
            throw
        }
    }

    if (Test-Path -LiteralPath (Join-Path $reusedDirectory 'g4-tool-package-inventory.json')) {
        throw 'A reused qualification directory exposed a new complete inventory.'
    }

    $failedDirectory = Join-Path $testRoot 'failed'
    try {
        & $buildPath -Version '0.0.0-gate.2' -OutputDirectory $failedDirectory -DeferInventory
        throw 'A failing package build was accepted.'
    }
    catch {
        if (-not $_.Exception.Message.Contains('failed with exit code 97', [System.StringComparison]::Ordinal)) {
            throw
        }
    }

    if (Test-Path -LiteralPath (Join-Path $failedDirectory 'g4-tool-package-inventory.json')) {
        throw 'A failed qualification attempt exposed a new complete inventory.'
    }

    $fixtureBuildPath = Join-Path $testRoot 'fixture-build.ps1'
    foreach ($schemaName in @('hexalith.module-manifest.v2.json', 'hexalith.test-environment.v1.json', 'hexalith.test-result.v1.json')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../schemas/$schemaName") `
            -Destination (Join-Path $testRoot $schemaName)
    }
    Copy-Item -LiteralPath $catalogToolingAssembly -Destination (Join-Path $testRoot 'Hexalith.Builds.Tooling.dll')
    [IO.File]::WriteAllText($fixtureBuildPath, @'
param([string] $Version, [string] $OutputDirectory, [switch] $DeferInventory)
$null = $DeferInventory
$null = New-Item -ItemType Directory -Path $OutputDirectory
foreach ($id in @('Hexalith.Builds.Module.Cli', 'Hexalith.Builds.Evidence.Cli')) {
    $packagePath = Join-Path $OutputDirectory "$id.$Version.nupkg"
    if ($id -ceq 'Hexalith.Builds.Module.Cli') {
        $archive = [IO.Compression.ZipFile]::Open($packagePath, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($schemaName in @('hexalith.module-manifest.v2.json', 'hexalith.test-environment.v1.json', 'hexalith.test-result.v1.json')) {
                $entry = $archive.CreateEntry("tools/net10.0/any/schemas/$schemaName")
                $writer = [IO.StreamWriter]::new($entry.Open())
                try { $writer.Write([IO.File]::ReadAllText((Join-Path $PSScriptRoot $schemaName))) }
                finally { $writer.Dispose() }
            }
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $PSScriptRoot 'Hexalith.Builds.Tooling.dll'), 'tools/net10.0/any/Hexalith.Builds.Tooling.dll')
        }
        finally { $archive.Dispose() }
    }
    else {
        [IO.File]::WriteAllText($packagePath, "package:$id")
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory "$id.$Version.snupkg"), "symbols:$id")
}
'@, [Text.UTF8Encoding]::new($false))

    $postPackageDirectory = Join-Path $testRoot 'post-package-failure'
    try {
        $global:HexalithG4GateDotNetMode = 'fail'
        & $qualificationPath -Version '0.0.0-gate.3' -PackageDirectory $postPackageDirectory `
            -SkipSourceValidation -RetainPackageDirectory -PackageBuildScriptPath $fixtureBuildPath
        throw 'A post-package qualification failure was accepted.'
    }
    catch {
        if (-not $_.Exception.Message.Contains('failed with exit code 97', [StringComparison]::Ordinal)) {
            throw
        }
    }
    if (@(Get-ChildItem -LiteralPath $postPackageDirectory -File | Where-Object Extension -in @('.nupkg', '.snupkg')).Count -ne 4 -or
        (Test-Path -LiteralPath (Join-Path $postPackageDirectory 'g4-tool-package-inventory.json'))) {
        throw 'Post-package failure must retain package evidence without exposing a complete inventory.'
    }

    $duplicateFixtureRoot = Join-Path $testRoot 'duplicate-fixtures'
    $null = New-Item -ItemType Directory -Path $duplicateFixtureRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../test/fixtures/module') `
        -Destination (Join-Path $duplicateFixtureRoot 'module') -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../test/fixtures/evidence') `
        -Destination (Join-Path $duplicateFixtureRoot 'evidence') -Recurse
    $duplicateDirectory = Join-Path $duplicateFixtureRoot 'module/negative/duplicate-name'
    $null = New-Item -ItemType Directory -Path $duplicateDirectory
    Copy-Item -LiteralPath (Join-Path $duplicateFixtureRoot 'module/negative/invalid-profile.json') -Destination $duplicateDirectory
    Copy-Item -LiteralPath (Join-Path $duplicateFixtureRoot 'module/negative/invalid-profile.expected.json') -Destination $duplicateDirectory
    $duplicateOutputDirectory = Join-Path $testRoot 'duplicate-output'
    try {
        $global:HexalithG4GateDotNetMode = 'pass'
        & $qualificationPath -Version '0.0.0-gate.4' -PackageDirectory $duplicateOutputDirectory `
            -SkipSourceValidation -RequireControls -RetainPackageDirectory -FixtureRoot $duplicateFixtureRoot `
            -PackageBuildScriptPath $fixtureBuildPath
        throw 'Duplicate qualification evidence names were accepted.'
    }
    catch {
        if (-not $_.Exception.Message.Contains('output names must be unique', [StringComparison]::Ordinal)) {
            throw
        }
    }
    if (Test-Path -LiteralPath (Join-Path $duplicateOutputDirectory 'g4-tool-package-inventory.json')) {
        throw 'Duplicate qualification evidence names exposed a complete inventory.'
    }
}
finally {
    Remove-Item Function:\dotnet -Force -ErrorAction SilentlyContinue
    Remove-Variable -Name HexalithG4GateDotNetMode -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# The shared fixture-proof and publication functions have their own regression suite;
# running it here keeps it blocking wherever this gate self-test runs.
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'test-g4-tool-package-artifact-validator.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "G-4 tool package artifact validator tests failed with exit code $LASTEXITCODE."
}

[Console]::Out.WriteLine('G-4 tool package qualification gate checks passed: reused, pre-package, post-package, and duplicate-output failures expose no inventory.')
