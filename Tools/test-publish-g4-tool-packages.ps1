#!/usr/bin/env pwsh

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-g4-publisher-$([System.Guid]::NewGuid().ToString('N'))"
$sourceRepositoryRoot = Join-Path $testRoot 'source'
$publisherPath = Join-Path $sourceRepositoryRoot 'Tools/publish-g4-tool-packages.ps1'
$packageIds = @('Hexalith.Builds.Evidence.Cli', 'Hexalith.Builds.Module.Cli')
$originalGitHubToken = $env:GITHUB_TOKEN
$originalNuGetApiKey = $env:NUGET_API_KEY
$global:HexalithG4PublisherTestInvocations = [System.Collections.Generic.List[string]]::new()

function global:dotnet {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [object[]] $ForwardedArguments
    )

    $global:HexalithG4PublisherTestInvocations.Add(($ForwardedArguments | ConvertTo-Json -Compress))
    $global:LASTEXITCODE = 0
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Actual,

        [Parameter(Mandatory = $true)]
        [object] $Expected,

        [Parameter(Mandatory = $true)]
        [string] $Because
    )

    if ($Actual -cne $Expected) {
        throw "$Because Expected '$Expected', actual '$Actual'."
    }
}

function Write-TestPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $PackageId,

        [Parameter(Mandatory = $true)]
        [string] $PackageVersion,

        [switch] $AsSymbolsPackage,

        [ValidateRange(0, 2)]
        [int] $NuspecCount = 1
    )

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            if ($NuspecCount -eq 0) {
                $null = $archive.CreateEntry('tools/net10.0/any/fixture.dll')
            }
            for ($nuspecIndex = 0; $nuspecIndex -lt $NuspecCount; $nuspecIndex++) {
                $entryName = if ($nuspecIndex -eq 0) { "$PackageId.nuspec" } else { "nested/Additional.$nuspecIndex.nuspec" }
                $entry = $archive.CreateEntry($entryName)
                $entryStream = $entry.Open()
                try {
                    $writer = [IO.StreamWriter]::new($entryStream, [Text.UTF8Encoding]::new($false), 1024, $true)
                    try {
                        $escapedId = [Security.SecurityElement]::Escape($PackageId)
                        $escapedVersion = [Security.SecurityElement]::Escape($PackageVersion)
                        $packageType = if ($AsSymbolsPackage) { 'SymbolsPackage' } else { 'DotnetTool' }
                        $writer.Write("<?xml version=`"1.0`" encoding=`"utf-8`"?><package><metadata><id>$escapedId</id><version>$escapedVersion</version><authors>Hexalith</authors><description>Publisher contract fixture.</description><packageTypes><packageType name=`"$packageType`" /></packageTypes></metadata></package>")
                    }
                    finally {
                        $writer.Dispose()
                    }
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-FixtureExpectationPath {
    param([Parameter(Mandatory = $true)][IO.FileInfo] $Fixture)

    $baseNameExpectation = Join-Path $Fixture.DirectoryName "$($Fixture.BaseName).expected.json"
    if (Test-Path -LiteralPath $baseNameExpectation -PathType Leaf) {
        return $baseNameExpectation
    }

    return "$($Fixture.FullName).expected.json"
}

function New-ToolResultContent {
    param(
        [Parameter(Mandatory = $true)][string] $Status,
        [Parameter(Mandatory = $true)][string] $OutcomeExitCode,
        [Parameter(Mandatory = $true)][string] $Phase,
        [Parameter(Mandatory = $true)][string] $Category,
        [AllowNull()][object] $RuleId,
        [AllowEmptyCollection()][string[]] $RuleIds
    )

    return [ordered] @{
        status = $Status
        outcome = [ordered] @{
            exitCode = $OutcomeExitCode
            phase = $Phase
            category = $Category
            ruleId = $RuleId
        }
        diagnostics = @($RuleIds | ForEach-Object { [ordered] @{ ruleId = $_ } })
    } | ConvertTo-Json -Depth 5
}

function New-ModuleEvidenceContent {
    param(
        [Parameter(Mandatory = $true)][string] $FinalStatus,
        [Parameter(Mandatory = $true)][long] $ExitCode,
        [AllowNull()][object] $RuleId,
        [Parameter(Mandatory = $true)][string] $Phase,
        [Parameter(Mandatory = $true)][string] $Category,
        [Parameter(Mandatory = $true)][string] $Command,
        [Parameter(Mandatory = $true)][string] $ToolVersion,
        [Parameter(Mandatory = $true)][string] $ManifestHash
    )

    $document = [ordered] @{
        schema = 'hexalith.module-run-evidence.v1'
        finalStatus = $FinalStatus
        outcome = [ordered] @{
            exitCode = $ExitCode
            phase = $Phase
            category = $Category
            ruleId = $RuleId
        }
        invocation = [ordered] @{
            command = $Command
            manifestHash = $ManifestHash
        }
        environment = [ordered] @{
            toolVersion = $ToolVersion
            repositoryRevision = 'unavailable'
            repositoryDirtyMarker = 'dirty'
        }
        topology = [ordered] @{
            platform = [ordered] @{
                eventStoreVersion = '3.90.0'
            }
        }
    }

    return ($document | ConvertTo-Json -Depth 7) + "`n"
}

function Get-RequiredEvidenceNames {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $FixtureFiles
    )

    $names = [Collections.Generic.List[string]]::new()
    foreach ($name in @(
            'source-release-passed.json',
            'packaged-down-output.json',
            'packaged-down-evidence.json',
            'packaged-test-output.json',
            'packaged-test-evidence.json',
            'packaged-unavailable-output.json',
            'packaged-unavailable-evidence.json',
            'packaged-readiness-output.json',
            'qualification.log'
        )) {
        $names.Add("qualification-evidence/$name")
    }
    foreach ($fixture in $FixtureFiles) {
        if ($fixture.file.StartsWith('module/negative/', [StringComparison]::Ordinal) -and
            $fixture.file.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase) -and
            -not $fixture.file.EndsWith('.expected.json', [StringComparison]::OrdinalIgnoreCase)) {
            $names.Add("qualification-evidence/module-negative-$([IO.Path]::GetFileNameWithoutExtension($fixture.file))-output.json")
        }
        elseif ($fixture.file.StartsWith('evidence/negative/', [StringComparison]::Ordinal) -and
            ($fixture.file.EndsWith('.yaml', [StringComparison]::OrdinalIgnoreCase) -or
                $fixture.file.EndsWith('.yml', [StringComparison]::OrdinalIgnoreCase))) {
            $names.Add("qualification-evidence/evidence-negative-$([IO.Path]::GetFileNameWithoutExtension($fixture.file))-output.json")
        }
    }

    return @($names | Sort-Object)
}

function New-PackageInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PublishedVersion
    )

    $packageDirectory = Join-Path $testRoot $PublishedVersion
    $null = New-Item -ItemType Directory -Path $packageDirectory -Force
    $packages = [System.Collections.Generic.List[object]]::new()
    foreach ($packageId in $packageIds) {
        $nupkgName = "$packageId.$PublishedVersion.nupkg"
        $snupkgName = "$packageId.$PublishedVersion.snupkg"
        $nupkgPath = Join-Path $packageDirectory $nupkgName
        $snupkgPath = Join-Path $packageDirectory $snupkgName
        Write-TestPackage -Path $nupkgPath -PackageId $packageId -PackageVersion $PublishedVersion
        Write-TestPackage -Path $snupkgPath -PackageId $packageId -PackageVersion $PublishedVersion -AsSymbolsPackage

        $packages.Add([ordered]@{
            id = $packageId
            version = $PublishedVersion
            nupkg = [ordered]@{
                file = $nupkgName
                sha256 = (Get-FileHash -LiteralPath $nupkgPath -Algorithm SHA256).Hash
                sizeBytes = (Get-Item -LiteralPath $nupkgPath).Length
            }
            snupkg = [ordered]@{
                file = $snupkgName
                sha256 = (Get-FileHash -LiteralPath $snupkgPath -Algorithm SHA256).Hash
                sizeBytes = (Get-Item -LiteralPath $snupkgPath).Length
            }
        })
    }

    $fixtureRoot = Join-Path $sourceRepositoryRoot 'test/fixtures'
    $fixtureFiles = @(
        Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse |
            Sort-Object -Property FullName |
            ForEach-Object {
                [ordered] @{
                    file = [IO.Path]::GetRelativePath($fixtureRoot, $_.FullName).Replace('\', '/')
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    sizeBytes = $_.Length
                }
            }
    )
    $fixtureMaterial = [string]::Join("`n", @($fixtureFiles | ForEach-Object {
                "$($_.file)|$($_.sha256)|$($_.sizeBytes)"
            }))
    $fixtureManifestSha256 = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fixtureMaterial))
    ).ToLowerInvariant()

    $qualifiedRevision = (& git -C $sourceRepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $qualifiedRevision -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Publisher test source revision could not be resolved.'
    }
    $manifestHash = (Get-FileHash -LiteralPath (Join-Path $fixtureRoot 'module/positive/hexalith.module-manifest.v1.json') -Algorithm SHA256).Hash
    $packagedToolVersion = "$PublishedVersion+$qualifiedRevision"
    $expectationsByEvidenceName = @{}
    foreach ($fixture in @(Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse)) {
        $relativeFixture = [IO.Path]::GetRelativePath($fixtureRoot, $fixture.FullName).Replace('\', '/')
        $evidenceName = if ($relativeFixture.StartsWith('module/negative/', [StringComparison]::Ordinal) -and
            $relativeFixture.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase) -and
            -not $relativeFixture.EndsWith('.expected.json', [StringComparison]::OrdinalIgnoreCase)) {
            "qualification-evidence/module-negative-$($fixture.BaseName)-output.json"
        }
        elseif ($relativeFixture.StartsWith('evidence/negative/', [StringComparison]::Ordinal) -and
            ($relativeFixture.EndsWith('.yaml', [StringComparison]::OrdinalIgnoreCase) -or
                $relativeFixture.EndsWith('.yml', [StringComparison]::OrdinalIgnoreCase))) {
            "qualification-evidence/evidence-negative-$($fixture.BaseName)-output.json"
        }
        else {
            $null
        }

        if ($null -ne $evidenceName) {
            $expectationsByEvidenceName[$evidenceName] = Get-Content -LiteralPath (Get-FixtureExpectationPath -Fixture $fixture) -Raw | ConvertFrom-Json
        }
    }

    $qualificationEvidenceRows = [Collections.Generic.List[object]]::new()
    foreach ($relativeEvidencePath in @(Get-RequiredEvidenceNames -FixtureFiles $fixtureFiles)) {
        $qualificationEvidencePath = Join-Path $packageDirectory $relativeEvidencePath
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $qualificationEvidencePath) -Force
        $content = switch ($relativeEvidencePath) {
            'qualification-evidence/source-release-passed.json' {
                [IO.File]::ReadAllText((Join-Path $fixtureRoot 'evidence/positive/evidence/release-passed.json'))
            }
            'qualification-evidence/packaged-down-output.json' {
                New-ToolResultContent -Status 'completed' -OutcomeExitCode 'Success' -Phase 'None' -Category 'None' `
                    -RuleId $null -RuleIds @('HXI001')
            }
            'qualification-evidence/packaged-test-output.json' {
                New-ToolResultContent -Status 'unavailable' -OutcomeExitCode 'PrerequisiteUnavailable' `
                    -Phase 'Prerequisite' -Category 'PrerequisiteUnavailable' -RuleId 'HXR002' -RuleIds @('HXR002')
            }
            'qualification-evidence/packaged-unavailable-output.json' {
                New-ToolResultContent -Status 'unavailable' -OutcomeExitCode 'PrerequisiteUnavailable' `
                    -Phase 'Prerequisite' -Category 'PrerequisiteUnavailable' -RuleId 'HXR002' -RuleIds @('HXR002')
            }
            'qualification-evidence/packaged-readiness-output.json' {
                New-ToolResultContent -Status 'passed' -OutcomeExitCode 'Success' -Phase 'None' -Category 'None' `
                    -RuleId $null -RuleIds @()
            }
            'qualification-evidence/packaged-down-evidence.json' {
                $filterHash = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('Bearer packaged-redaction-control')))
                New-ModuleEvidenceContent -FinalStatus 'completed' -ExitCode 0 -RuleId $null -Phase 'None' -Category 'None' `
                    -Command "hexalith-module down --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --filter-sha256 $filterHash" `
                    -ToolVersion $packagedToolVersion -ManifestHash $manifestHash
            }
            'qualification-evidence/packaged-test-evidence.json' {
                New-ModuleEvidenceContent -FinalStatus 'unavailable' -ExitCode 2 -RuleId 'HXR002' `
                    -Phase 'Prerequisite' -Category 'PrerequisiteUnavailable' `
                    -Command 'hexalith-module test --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --profile full' `
                    -ToolVersion $packagedToolVersion -ManifestHash $manifestHash
            }
            'qualification-evidence/packaged-unavailable-evidence.json' {
                New-ModuleEvidenceContent -FinalStatus 'unavailable' -ExitCode 2 -RuleId 'HXR002' `
                    -Phase 'Prerequisite' -Category 'PrerequisiteUnavailable' `
                    -Command 'hexalith-module run --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json' `
                    -ToolVersion $packagedToolVersion -ManifestHash $manifestHash
            }
            'qualification-evidence/qualification.log' {
                @(
                    '$ dotnet tool run hexalith-module -- down --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --filter <redacted>',
                    '$ dotnet tool run hexalith-module -- test --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --profile full',
                    '$ dotnet tool run hexalith-module -- run --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json',
                    '$ dotnet tool run hexalith-evidence -- validate test/fixtures/evidence/positive/readiness.yaml'
                ) -join "`n"
            }
            default {
                $expected = $expectationsByEvidenceName[$relativeEvidencePath]
                if ($null -eq $expected) {
                    throw "No publisher test evidence content was defined for '$relativeEvidencePath'."
                }
                New-ToolResultContent -Status ([string]$expected.status) -OutcomeExitCode ([string]$expected.outcomeExitCode) `
                    -Phase ([string]$expected.phase) -Category ([string]$expected.category) `
                    -RuleId ([string]$expected.outcomeRuleId) `
                    -RuleIds @($expected.ruleIds | ForEach-Object { [string]$_ })
            }
        }
        [IO.File]::WriteAllText(
            $qualificationEvidencePath,
            [string]$content,
            [Text.UTF8Encoding]::new($false))
        $qualificationEvidenceRows.Add([ordered] @{
                file = $relativeEvidencePath
                sha256 = (Get-FileHash -LiteralPath $qualificationEvidencePath -Algorithm SHA256).Hash
                sizeBytes = (Get-Item -LiteralPath $qualificationEvidencePath).Length
            })
    }

    $inventory = [ordered]@{
        schema = 'hexalith.g4-tool-package-inventory.v1'
        version = $PublishedVersion
        configuration = 'Release'
        packages = $packages
        qualificationEvidence = @($qualificationEvidenceRows)
        qualification = [ordered] @{
            packageBuild = [ordered] @{ mode = 'official'; result = 'passed' }
            sourceValidation = [ordered] @{ mode = 'executed'; result = 'passed' }
            controls = [ordered] @{ mode = 'executed'; result = 'passed' }
            fixtures = [ordered] @{
                mode = 'repository-tracked'
                root = 'test/fixtures'
                sha256 = $fixtureManifestSha256
                files = $fixtureFiles
            }
            sourceTree = [ordered] @{
                clean = $true
                revision = $qualifiedRevision
            }
            releaseEligible = $true
            ineligibilityReasons = @()
        }
    }
    $inventoryPath = Join-Path $packageDirectory 'g4-tool-package-inventory.json'
    [System.IO.File]::WriteAllText($inventoryPath, ($inventory | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))

    return $packageDirectory
}

function Invoke-RejectionCase {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PublishedVersion,

        [Parameter(Mandatory = $true)]
        [scriptblock] $Mutate,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedMessage
    )

    $packageDirectory = New-PackageInventory -PublishedVersion $PublishedVersion
    $inventoryPath = Join-Path $packageDirectory 'g4-tool-package-inventory.json'
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    & $Mutate $inventory $packageDirectory
    [IO.File]::WriteAllText($inventoryPath, ($inventory | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    $global:HexalithG4PublisherTestInvocations.Clear()
    try {
        & $publisherPath -Version $PublishedVersion -PackageDirectory $packageDirectory
        throw "Publisher accepted rejected scenario '$PublishedVersion'."
    }
    catch {
        if (-not $_.Exception.Message.Contains($ExpectedMessage, [StringComparison]::Ordinal)) {
            throw
        }
    }

    Assert-Equal -Actual $global:HexalithG4PublisherTestInvocations.Count -Expected 0 `
        -Because 'Rejected qualification inventory must publish no package.'
}

function Invoke-PublisherCase {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PublishedVersion,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSource
    )

    $packageDirectory = New-PackageInventory -PublishedVersion $PublishedVersion
    $expectedPrimaryNames = @($packageIds | ForEach-Object { "$($_).$PublishedVersion.nupkg" } | Sort-Object)
    $global:HexalithG4PublisherTestInvocations.Clear()
    & $publisherPath -Version $PublishedVersion -PackageDirectory $packageDirectory

    Assert-Equal -Actual $global:HexalithG4PublisherTestInvocations.Count -Expected 2 -Because 'The publisher must invoke dotnet once per primary package.'

    $publishedNames = [System.Collections.Generic.List[string]]::new()
    foreach ($serializedInvocation in $global:HexalithG4PublisherTestInvocations) {
        $arguments = @($serializedInvocation | ConvertFrom-Json)
        Assert-Equal -Actual $arguments[0] -Expected 'nuget' -Because 'The publisher must use the NuGet command group.'
        Assert-Equal -Actual $arguments[1] -Expected 'push' -Because 'The publisher must use the NuGet push command.'

        $artifactPath = [string]$arguments[2]
        if ($artifactPath.EndsWith('.snupkg', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The symbol package '$artifactPath' must not be submitted directly; dotnet pushes the adjacent symbol package with its primary package."
        }
        if ($arguments -contains '--no-symbols') {
            throw 'The publisher must leave automatic adjacent symbol-package publication enabled.'
        }

        $publishedNames.Add([System.IO.Path]::GetFileName($artifactPath))
        $sourceIndex = [Array]::IndexOf($arguments, '--source')
        if ($sourceIndex -lt 0 -or $sourceIndex + 1 -ge $arguments.Count) {
            throw 'The publisher invocation is missing its package source.'
        }

        Assert-Equal -Actual $arguments[$sourceIndex + 1] -Expected $ExpectedSource -Because 'The publisher must route the version to the expected feed.'
    }

    Assert-Equal -Actual (($publishedNames | Sort-Object) -join '|') -Expected ($expectedPrimaryNames -join '|') -Because 'The publisher must submit exactly the two approved primary packages.'
}

function Set-EvidenceContentAndRefreshInventory {
    param(
        [Parameter(Mandatory = $true)][object] $Inventory,
        [Parameter(Mandatory = $true)][string] $PackageDirectory,
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Content
    )

    $rows = @($Inventory.qualificationEvidence | Where-Object file -CEQ $RelativePath)
    if ($rows.Count -ne 1) {
        throw "Expected one qualification-evidence inventory row for '$RelativePath'."
    }

    $path = Join-Path $PackageDirectory $RelativePath
    [IO.File]::WriteAllText($path, $Content, [Text.UTF8Encoding]::new($false))
    $rows[0].sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    $rows[0].sizeBytes = (Get-Item -LiteralPath $path).Length
}

try {
    $null = New-Item -ItemType Directory -Path (Join-Path $sourceRepositoryRoot 'Tools') -Force
    $null = New-Item -ItemType Directory -Path (Join-Path $sourceRepositoryRoot 'test') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'publish-g4-tool-packages.ps1') `
        -Destination $publisherPath -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1') `
        -Destination (Join-Path $sourceRepositoryRoot 'Tools/G4PackageQualification.functions.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../test/fixtures') `
        -Destination (Join-Path $sourceRepositoryRoot 'test') -Recurse -Force

    & git -C $sourceRepositoryRoot init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the isolated publisher-test repository.' }
    & git -C $sourceRepositoryRoot config user.email 'publisher-test@hexalith.invalid'
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the isolated publisher-test Git email.' }
    & git -C $sourceRepositoryRoot config user.name 'Hexalith Publisher Test'
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the isolated publisher-test Git name.' }
    & git -C $sourceRepositoryRoot add --all
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage the isolated publisher-test source fixture.' }
    & git -C $sourceRepositoryRoot commit --quiet -m 'test: create publisher source fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit the isolated publisher-test source fixture.' }

    $env:NUGET_API_KEY = 'stable-test-token'
    $env:GITHUB_TOKEN = $null
    Invoke-PublisherCase -PublishedVersion '9.8.7' -ExpectedSource 'https://api.nuget.org/v3/index.json'

    $env:NUGET_API_KEY = $null
    $env:GITHUB_TOKEN = 'prerelease-test-token'
    Invoke-PublisherCase -PublishedVersion '9.8.7-preview.1' -ExpectedSource 'https://nuget.pkg.github.com/Hexalith/index.json'

    $env:NUGET_API_KEY = 'stable-test-token'
    Invoke-RejectionCase -PublishedVersion '9.8.8' -ExpectedMessage 'incomplete or bypassed' -Mutate {
        param($Inventory)
        $Inventory.qualification.sourceValidation.mode = 'skipped'
        $Inventory.qualification.sourceValidation.result = 'not-run'
        $Inventory.qualification.releaseEligible = $false
    }
    Invoke-RejectionCase -PublishedVersion '9.8.13' -ExpectedMessage 'incomplete or bypassed' -Mutate {
        param($Inventory)
        $Inventory.qualification.packageBuild.mode = 'fixture'
        $Inventory.qualification.releaseEligible = $false
    }
    Invoke-RejectionCase -PublishedVersion '9.8.9' -ExpectedMessage 'incomplete or bypassed' -Mutate {
        param($Inventory)
        $Inventory.qualification.controls.mode = 'skipped'
        $Inventory.qualification.controls.result = 'not-run'
        $Inventory.qualification.releaseEligible = $false
    }
    Invoke-RejectionCase -PublishedVersion '9.8.10' -ExpectedMessage 'external fixture provenance' -Mutate {
        param($Inventory)
        $Inventory.qualification.fixtures.mode = 'external'
        $Inventory.qualification.fixtures.root = '<external>'
    }
    Invoke-RejectionCase -PublishedVersion '9.8.11' -ExpectedMessage 'incomplete or bypassed' -Mutate {
        param($Inventory)
        $Inventory.qualification.releaseEligible = $false
    }
    Invoke-RejectionCase -PublishedVersion '9.8.14' -ExpectedMessage 'incomplete or bypassed' -Mutate {
        param($Inventory)
        $Inventory.qualification.releaseEligible = 'false'
    }
    Invoke-RejectionCase -PublishedVersion '9.8.12' -ExpectedMessage 'qualification evidence is missing or incomplete' -Mutate {
        param($Inventory)
        $Inventory.qualificationEvidence = @()
    }
    Invoke-RejectionCase -PublishedVersion '9.8.15' -ExpectedMessage 'does not match its retained bytes' -Mutate {
        param($Inventory, $PackageDirectory)
        $path = Join-Path $PackageDirectory 'qualification-evidence/qualification.log'
        $bytes = [IO.File]::ReadAllBytes($path)
        $bytes[0] = $bytes[0] -bxor 1
        [IO.File]::WriteAllBytes($path, $bytes)
    }
    Invoke-RejectionCase -PublishedVersion '9.8.16' -ExpectedMessage 'does not cover every required' -Mutate {
        param($Inventory, $PackageDirectory)
        $removed = @($Inventory.qualificationEvidence | Where-Object file -like 'qualification-evidence/module-negative-*-output.json')[0]
        $Inventory.qualificationEvidence = @($Inventory.qualificationEvidence | Where-Object file -cne $removed.file)
        Remove-Item -LiteralPath (Join-Path $PackageDirectory $removed.file)
    }
    Invoke-RejectionCase -PublishedVersion '9.8.17' -ExpectedMessage 'does not bind version' -Mutate {
        param($Inventory)
        $Inventory.packages[0].version = '9.8.999'
    }
    Invoke-RejectionCase -PublishedVersion '9.8.18' -ExpectedMessage 'does not match expected' -Mutate {
        param($Inventory, $PackageDirectory)
        $package = $Inventory.packages[0]
        $path = Join-Path $PackageDirectory $package.nupkg.file
        Write-TestPackage -Path $path -PackageId 'Wrong.Package' -PackageVersion '9.8.18'
        $package.nupkg.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $package.nupkg.sizeBytes = (Get-Item -LiteralPath $path).Length
    }
    Invoke-RejectionCase -PublishedVersion '9.8.19' -ExpectedMessage 'does not match expected' -Mutate {
        param($Inventory, $PackageDirectory)
        $package = $Inventory.packages[1]
        $path = Join-Path $PackageDirectory $package.snupkg.file
        Write-TestPackage -Path $path -PackageId $package.id -PackageVersion '9.8.999' -AsSymbolsPackage
        $package.snupkg.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $package.snupkg.sizeBytes = (Get-Item -LiteralPath $path).Length
    }
    Invoke-RejectionCase -PublishedVersion '9.8.20' -ExpectedMessage "status 'passed' does not represent a failed negative control" -Mutate {
        param($Inventory, $PackageDirectory)
        $relativePath = @($Inventory.qualificationEvidence.file | Where-Object { $_ -like 'qualification-evidence/module-negative-*-output.json' })[0]
        $content = New-ToolResultContent -Status 'passed' -OutcomeExitCode 'Success' -Phase 'None' -Category 'None' `
            -RuleId $null -RuleIds @()
        Set-EvidenceContentAndRefreshInventory -Inventory $Inventory -PackageDirectory $PackageDirectory `
            -RelativePath $relativePath -Content $content
    }
    Invoke-RejectionCase -PublishedVersion '9.8.21' -ExpectedMessage 'does not match its exact typed outcome' -Mutate {
        param($Inventory, $PackageDirectory)
        $relativePath = 'qualification-evidence/packaged-test-evidence.json'
        $path = Join-Path $PackageDirectory $relativePath
        $evidence = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $evidence.environment.toolVersion = '9.8.21+0000000000000000000000000000000000000000'
        $content = ($evidence | ConvertTo-Json -Depth 12) + "`n"
        Set-EvidenceContentAndRefreshInventory -Inventory $Inventory -PackageDirectory $PackageDirectory `
            -RelativePath $relativePath -Content $content
    }
    Invoke-RejectionCase -PublishedVersion '9.8.22' -ExpectedMessage "does not contain required command 'dotnet tool run hexalith-evidence -- validate'" -Mutate {
        param($Inventory, $PackageDirectory)
        $relativePath = 'qualification-evidence/qualification.log'
        $content = @(
            '$ dotnet tool run hexalith-module -- down',
            '$ dotnet tool run hexalith-module -- test',
            '$ dotnet tool run hexalith-module -- run'
        ) -join "`n"
        Set-EvidenceContentAndRefreshInventory -Inventory $Inventory -PackageDirectory $PackageDirectory `
            -RelativePath $relativePath -Content $content
    }
    Invoke-RejectionCase -PublishedVersion '9.8.23' -ExpectedMessage 'does not match qualified revision' -Mutate {
        param($Inventory)
        $Inventory.qualification.sourceTree.revision = '0000000000000000000000000000000000000000'
    }
    Invoke-RejectionCase -PublishedVersion '9.8.24' -ExpectedMessage 'no clean, immutable qualified source-tree binding' -Mutate {
        param($Inventory)
        $Inventory.qualification.sourceTree.clean = $false
    }
    Invoke-RejectionCase -PublishedVersion '9.8.25' -ExpectedMessage 'symbols package was swapped in as the primary artifact' -Mutate {
        param($Inventory, $PackageDirectory)
        $package = $Inventory.packages[0]
        $path = Join-Path $PackageDirectory $package.nupkg.file
        Write-TestPackage -Path $path -PackageId $package.id -PackageVersion '9.8.25' -AsSymbolsPackage
        $package.nupkg.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $package.nupkg.sizeBytes = (Get-Item -LiteralPath $path).Length
    }
    Invoke-RejectionCase -PublishedVersion '9.8.26' -ExpectedMessage 'primary artifact was swapped in as symbols' -Mutate {
        param($Inventory, $PackageDirectory)
        $package = $Inventory.packages[0]
        $path = Join-Path $PackageDirectory $package.snupkg.file
        Write-TestPackage -Path $path -PackageId $package.id -PackageVersion '9.8.26'
        $package.snupkg.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $package.snupkg.sizeBytes = (Get-Item -LiteralPath $path).Length
    }
    Invoke-RejectionCase -PublishedVersion '9.8.27' -ExpectedMessage 'contains no .nuspec entry' -Mutate {
        param($Inventory, $PackageDirectory)
        $package = $Inventory.packages[0]
        $path = Join-Path $PackageDirectory $package.nupkg.file
        Write-TestPackage -Path $path -PackageId $package.id -PackageVersion '9.8.27' -NuspecCount 0
        $package.nupkg.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $package.nupkg.sizeBytes = (Get-Item -LiteralPath $path).Length
    }
    Invoke-RejectionCase -PublishedVersion '9.8.28' -ExpectedMessage 'contains 2 .nuspec entries' -Mutate {
        param($Inventory, $PackageDirectory)
        $package = $Inventory.packages[0]
        $path = Join-Path $PackageDirectory $package.nupkg.file
        Write-TestPackage -Path $path -PackageId $package.id -PackageVersion '9.8.28' -NuspecCount 2
        $package.nupkg.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $package.nupkg.sizeBytes = (Get-Item -LiteralPath $path).Length
    }

    # Source-tree cleanliness is evaluated immediately before publication. Keep
    # this scenario last because its deliberate tracked-file edit makes all later
    # inventories ineligible until the isolated repository is removed.
    Invoke-RejectionCase -PublishedVersion '9.8.29' -ExpectedMessage 'Publication source tree is not clean' -Mutate {
        param($Inventory, $PackageDirectory)
        Add-Content -LiteralPath (Join-Path $sourceRepositoryRoot 'Tools/G4PackageQualification.functions.ps1') `
            -Value '# Deliberate publisher-test dirtiness.'
    }
}
finally {
    $env:GITHUB_TOKEN = $originalGitHubToken
    $env:NUGET_API_KEY = $originalNuGetApiKey
    Remove-Item Function:\dotnet -Force -ErrorAction SilentlyContinue
    Remove-Variable -Name HexalithG4PublisherTestInvocations -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

[Console]::Out.WriteLine('G-4 tool package publisher checks passed.')
