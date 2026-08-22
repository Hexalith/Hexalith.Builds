#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    [string] $PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1')

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version '$Version' is not a supported NuGet semantic version."
}

if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot "artifacts/g4-tool-packages/$Version"
}
elseif (-not [System.IO.Path]::IsPathRooted($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot $PackageDirectory
}

$packageDirectoryPath = [System.IO.Path]::GetFullPath($PackageDirectory)
$inventoryPath = Join-Path $packageDirectoryPath 'g4-tool-package-inventory.json'

function Get-RedactedDotNetArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $redactNextArgument = $false
    $redactedArguments = foreach ($argument in $Arguments) {
        if ($redactNextArgument) {
            $redactNextArgument = $false
            '<redacted>'
            continue
        }

        if ($argument -eq '--api-key') {
            $redactNextArgument = $true
        }

        $argument
    }

    return @($redactedArguments)
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        $redactedArguments = Get-RedactedDotNetArguments -Arguments $Arguments
        throw "dotnet $($redactedArguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-RequiredQualificationEvidenceNames {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $FixtureRows
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

    foreach ($fixtureRow in $FixtureRows) {
        $fixturePath = [string]$fixtureRow.file
        if ($fixturePath.StartsWith('module/negative/', [StringComparison]::Ordinal) -and
            $fixturePath.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase) -and
            -not $fixturePath.EndsWith('.expected.json', [StringComparison]::OrdinalIgnoreCase)) {
            $names.Add("qualification-evidence/module-negative-$([IO.Path]::GetFileNameWithoutExtension($fixturePath))-output.json")
        }
        elseif ($fixturePath.StartsWith('evidence/negative/', [StringComparison]::Ordinal) -and
            ($fixturePath.EndsWith('.yaml', [StringComparison]::OrdinalIgnoreCase) -or
                $fixturePath.EndsWith('.yml', [StringComparison]::OrdinalIgnoreCase))) {
            $names.Add("qualification-evidence/evidence-negative-$([IO.Path]::GetFileNameWithoutExtension($fixturePath))-output.json")
        }
    }

    $duplicates = @($names | Group-Object | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) {
        throw "Package inventory fixture coverage produces duplicate qualification evidence names: $($duplicates.Name -join ', ')."
    }

    return @($names | Sort-Object)
}

function Get-FixtureExpectationPath {
    param([Parameter(Mandatory = $true)][IO.FileInfo] $Fixture)

    $baseNameExpectation = Join-Path $Fixture.DirectoryName "$($Fixture.BaseName).expected.json"
    if (Test-Path -LiteralPath $baseNameExpectation -PathType Leaf) {
        return $baseNameExpectation
    }

    return "$($Fixture.FullName).expected.json"
}

if (-not (Test-Path -LiteralPath $inventoryPath -PathType Leaf)) {
    throw "Package inventory '$inventoryPath' is required before publication. Run build-g4-tool-packages.ps1 first."
}

$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
if ($inventory.schema -cne 'hexalith.g4-tool-package-inventory.v1' -or $inventory.version -cne $Version -or $inventory.configuration -cne 'Release') {
    throw "Package inventory '$inventoryPath' does not describe the requested Release version '$Version'."
}

$qualification = $inventory.qualification
if ($null -eq $qualification -or
    [string]$qualification.packageBuild.mode -cne 'official' -or
    [string]$qualification.packageBuild.result -cne 'passed' -or
    [string]$qualification.sourceValidation.mode -cne 'executed' -or
    [string]$qualification.sourceValidation.result -cne 'passed' -or
    [string]$qualification.controls.mode -cne 'executed' -or
    [string]$qualification.controls.result -cne 'passed' -or
    $qualification.releaseEligible -isnot [bool] -or
    $qualification.releaseEligible -ne $true) {
    throw 'Package inventory is incomplete or bypassed; publication requires passed source validation, passed controls, and explicit release eligibility.'
}

$qualifiedSourceTree = $qualification.sourceTree
if ($null -eq $qualifiedSourceTree -or
    $qualifiedSourceTree.clean -isnot [bool] -or
    $qualifiedSourceTree.clean -ne $true -or
    [string]$qualifiedSourceTree.revision -cnotmatch '^[0-9a-f]{40}$') {
    throw 'Package inventory has no clean, immutable qualified source-tree binding.'
}
$currentSourceTree = Get-SourceTreeState -RepositoryRoot $repositoryRoot
if (-not $currentSourceTree.Clean) {
    throw "Publication source tree is not clean: $([string]::Join('; ', @($currentSourceTree.Reasons)))"
}
if ($currentSourceTree.Revision -cne [string]$qualifiedSourceTree.revision) {
    throw "Publication source revision '$($currentSourceTree.Revision)' does not match qualified revision '$($qualifiedSourceTree.revision)'."
}
$qualifiedSourceRevision = [string]$qualifiedSourceTree.revision

$fixtureBinding = $qualification.fixtures
if ($null -eq $fixtureBinding -or
    [string]$fixtureBinding.mode -cne 'repository-tracked' -or
    [string]$fixtureBinding.root -cne 'test/fixtures') {
    throw 'Package inventory uses incomplete or external fixture provenance; publication requires repository-tracked test/fixtures.'
}
$fixtureRoot = Join-Path $repositoryRoot 'test/fixtures'
$fixtureRows = @($fixtureBinding.files | Sort-Object -Property file)
$actualFixtureFiles = @(Get-ChildItem -LiteralPath $fixtureRoot -File -Recurse | Sort-Object -Property FullName)
if ($fixtureRows.Count -eq 0 -or $fixtureRows.Count -ne $actualFixtureFiles.Count) {
    throw 'Package inventory fixture manifest is incomplete or does not match repository test/fixtures.'
}
$fixtureMaterial = [System.Collections.Generic.List[string]]::new()
for ($fixtureIndex = 0; $fixtureIndex -lt $actualFixtureFiles.Count; $fixtureIndex++) {
    $actualFixture = $actualFixtureFiles[$fixtureIndex]
    $fixtureRow = $fixtureRows[$fixtureIndex]
    $relativeFixture = [IO.Path]::GetRelativePath($fixtureRoot, $actualFixture.FullName).Replace('\', '/')
    $actualFixtureHash = (Get-FileHash -LiteralPath $actualFixture.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$fixtureRow.file -cne $relativeFixture -or
        [string]$fixtureRow.sha256 -cne $actualFixtureHash -or
        [long]$fixtureRow.sizeBytes -ne $actualFixture.Length) {
        throw "Package inventory fixture '$relativeFixture' does not match its tracked declaration bytes."
    }
    $trackedOutput = @(& git -C $repositoryRoot ls-files --error-unmatch -- "test/fixtures/$relativeFixture" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Package inventory fixture 'test/fixtures/$relativeFixture' is not tracked by Git."
    }
    $fixtureMaterial.Add("$relativeFixture|$actualFixtureHash|$($actualFixture.Length)")
}
$actualFixtureManifestHash = Get-Sha256Text -Value ([string]::Join("`n", $fixtureMaterial))
if ([string]$fixtureBinding.sha256 -cne $actualFixtureManifestHash) {
    throw 'Package inventory fixture manifest SHA-256 does not match its ordered tracked files.'
}
$matchedFixtureCount = Assert-TrackedFixtureBytesMatchHead -RepositoryRoot $repositoryRoot `
    -SourceRevision $qualifiedSourceRevision -FixtureDirectory $fixtureRoot -RepositoryRelativeRoot 'test/fixtures'
if ($matchedFixtureCount -ne $actualFixtureFiles.Count) {
    throw "Tracked-fixture-vs-HEAD proof covered $matchedFixtureCount files; expected $($actualFixtureFiles.Count)."
}

$qualificationEvidenceRoot = Join-Path $packageDirectoryPath 'qualification-evidence'
$qualificationEvidence = @($inventory.qualificationEvidence | Sort-Object -Property file)
$actualQualificationEvidence = @(
    if (Test-Path -LiteralPath $qualificationEvidenceRoot -PathType Container) {
        Get-ChildItem -LiteralPath $qualificationEvidenceRoot -File -Recurse | Sort-Object -Property FullName
    }
)
if ($qualificationEvidence.Count -eq 0 -or $qualificationEvidence.Count -ne $actualQualificationEvidence.Count) {
    throw 'Package inventory qualification evidence is missing or incomplete.'
}
$requiredQualificationEvidenceNames = Get-RequiredQualificationEvidenceNames -FixtureRows $fixtureRows
$declaredQualificationEvidenceNames = @($qualificationEvidence | ForEach-Object { [string]$_.file } | Sort-Object)
$actualQualificationEvidenceNames = @($actualQualificationEvidence | ForEach-Object {
        [IO.Path]::GetRelativePath($packageDirectoryPath, $_.FullName).Replace('\', '/')
    } | Sort-Object)
if (@(Compare-Object -ReferenceObject $requiredQualificationEvidenceNames -DifferenceObject $declaredQualificationEvidenceNames).Count -gt 0 -or
    @(Compare-Object -ReferenceObject $requiredQualificationEvidenceNames -DifferenceObject $actualQualificationEvidenceNames).Count -gt 0) {
    throw 'Package inventory qualification evidence does not cover every required positive command and negative fixture.'
}
for ($evidenceIndex = 0; $evidenceIndex -lt $actualQualificationEvidence.Count; $evidenceIndex++) {
    $actualEvidence = $actualQualificationEvidence[$evidenceIndex]
    $evidenceRow = $qualificationEvidence[$evidenceIndex]
    $relativeEvidence = [IO.Path]::GetRelativePath($packageDirectoryPath, $actualEvidence.FullName).Replace('\', '/')
    if ([string]$evidenceRow.file -cne $relativeEvidence -or
        [string]$evidenceRow.sha256 -cne (Get-FileHash -LiteralPath $actualEvidence.FullName -Algorithm SHA256).Hash -or
        [long]$evidenceRow.sizeBytes -ne $actualEvidence.Length) {
        throw "Package inventory qualification evidence '$relativeEvidence' does not match its retained bytes."
    }
}

# Parse and validate every required evidence artifact before any package push. The
# inventory hashes bind bytes, but only these semantic checks prove those bytes are
# the required positive/control results rather than renamed or jointly tampered
# content.
$evidencePathsByName = @{}
foreach ($evidenceFile in $actualQualificationEvidence) {
    $relativeEvidence = [IO.Path]::GetRelativePath($packageDirectoryPath, $evidenceFile.FullName).Replace('\', '/')
    $evidencePathsByName[$relativeEvidence] = $evidenceFile.FullName
}

$moduleManifestPath = Join-Path $fixtureRoot 'module/positive/hexalith.module-manifest.v1.json'
$moduleManifestHash = (Get-FileHash -LiteralPath $moduleManifestPath -Algorithm SHA256).Hash
$packagedToolVersion = "$Version+$qualifiedSourceRevision"
$packagedFilterHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('Bearer packaged-redaction-control')))

$sourceEvidencePath = $evidencePathsByName['qualification-evidence/source-release-passed.json']
$sourceFixtureEvidencePath = Join-Path $fixtureRoot 'evidence/positive/evidence/release-passed.json'
if (-not [System.Linq.Enumerable]::SequenceEqual(
        [byte[]][IO.File]::ReadAllBytes($sourceEvidencePath),
        [byte[]][IO.File]::ReadAllBytes($sourceFixtureEvidencePath))) {
    throw 'Qualification source-release evidence does not exactly match the repository-tracked positive evidence fixture.'
}
$null = Assert-ModuleRunEvidenceContent -FilePath $sourceEvidencePath `
    -ExpectedFinalStatus 'completed' -ExpectedExitCode 0 -ExpectedRuleId $null -ExpectedPhase 'None' -ExpectedCategory 'None' `
    -ExpectedCommand 'hexalith-module test --profile full' -ExpectedToolVersion '0.0.0-contract' `
    -ExpectedRepositoryRevision '0123456789abcdef0123456789abcdef01234567' -ExpectedRepositoryDirtyMarker 'clean' `
    -ExpectedManifestHash ('A' * 64)

$null = Assert-ModuleRunEvidenceContent `
    -FilePath $evidencePathsByName['qualification-evidence/packaged-down-evidence.json'] `
    -ExpectedFinalStatus 'completed' -ExpectedExitCode 0 -ExpectedRuleId $null -ExpectedPhase 'None' -ExpectedCategory 'None' `
    -ExpectedCommand "hexalith-module down --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --filter-sha256 $packagedFilterHash" `
    -ExpectedToolVersion $packagedToolVersion -ExpectedRepositoryRevision 'unavailable' -ExpectedRepositoryDirtyMarker 'dirty' `
    -ExpectedManifestHash $moduleManifestHash
$null = Assert-ModuleRunEvidenceContent `
    -FilePath $evidencePathsByName['qualification-evidence/packaged-test-evidence.json'] `
    -ExpectedFinalStatus 'unavailable' -ExpectedExitCode 2 -ExpectedRuleId 'HXR002' -ExpectedPhase 'Prerequisite' -ExpectedCategory 'PrerequisiteUnavailable' `
    -ExpectedCommand 'hexalith-module test --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json --profile full' `
    -ExpectedToolVersion $packagedToolVersion -ExpectedRepositoryRevision 'unavailable' -ExpectedRepositoryDirtyMarker 'dirty' `
    -ExpectedManifestHash $moduleManifestHash
$null = Assert-ModuleRunEvidenceContent `
    -FilePath $evidencePathsByName['qualification-evidence/packaged-unavailable-evidence.json'] `
    -ExpectedFinalStatus 'unavailable' -ExpectedExitCode 2 -ExpectedRuleId 'HXR002' -ExpectedPhase 'Prerequisite' -ExpectedCategory 'PrerequisiteUnavailable' `
    -ExpectedCommand 'hexalith-module run --manifest test/fixtures/module/positive/hexalith.module-manifest.v1.json' `
    -ExpectedToolVersion $packagedToolVersion -ExpectedRepositoryRevision 'unavailable' -ExpectedRepositoryDirtyMarker 'dirty' `
    -ExpectedManifestHash $moduleManifestHash

$null = Assert-QualificationEvidenceContent `
    -FilePath $evidencePathsByName['qualification-evidence/packaged-down-output.json'] -Kind Positive `
    -ExpectedStatus 'completed' -ExpectedOutcomeExitCode 'Success' -ExpectedPhase 'None' -ExpectedCategory 'None' `
    -RequireNullRuleId -ExpectedDiagnosticRuleIds @('HXI001')
foreach ($name in @('packaged-test-output', 'packaged-unavailable-output')) {
    $null = Assert-QualificationEvidenceContent `
        -FilePath $evidencePathsByName["qualification-evidence/$name.json"] -Kind ExactNonPassing `
        -ExpectedStatus 'unavailable' -ExpectedOutcomeExitCode 'PrerequisiteUnavailable' `
        -ExpectedPhase 'Prerequisite' -ExpectedCategory 'PrerequisiteUnavailable' -ExpectedRuleId 'HXR002' `
        -ExpectedDiagnosticRuleIds @('HXR002')
}
$null = Assert-QualificationEvidenceContent `
    -FilePath $evidencePathsByName['qualification-evidence/packaged-readiness-output.json'] -Kind Positive `
    -ExpectedStatus 'passed' -ExpectedOutcomeExitCode 'Success' -ExpectedPhase 'None' -ExpectedCategory 'None' `
    -RequireNullRuleId -ExpectedDiagnosticRuleIds @()

foreach ($fixtureFile in $actualFixtureFiles) {
    $relativeFixture = [IO.Path]::GetRelativePath($fixtureRoot, $fixtureFile.FullName).Replace('\', '/')
    $evidenceName = if ($relativeFixture.StartsWith('module/negative/', [StringComparison]::Ordinal) -and
        $relativeFixture.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase) -and
        -not $relativeFixture.EndsWith('.expected.json', [StringComparison]::OrdinalIgnoreCase)) {
        "qualification-evidence/module-negative-$($fixtureFile.BaseName)-output.json"
    }
    elseif ($relativeFixture.StartsWith('evidence/negative/', [StringComparison]::Ordinal) -and
        ($relativeFixture.EndsWith('.yaml', [StringComparison]::OrdinalIgnoreCase) -or
            $relativeFixture.EndsWith('.yml', [StringComparison]::OrdinalIgnoreCase))) {
        "qualification-evidence/evidence-negative-$($fixtureFile.BaseName)-output.json"
    }
    else {
        $null
    }

    if ($null -eq $evidenceName) {
        continue
    }

    $expectationPath = Get-FixtureExpectationPath -Fixture $fixtureFile
    if (-not (Test-Path -LiteralPath $expectationPath -PathType Leaf)) {
        throw "Negative fixture '$relativeFixture' has no outcome expectation for publication validation."
    }
    $expectation = Get-Content -LiteralPath $expectationPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $null = Assert-QualificationEvidenceContent -FilePath $evidencePathsByName[$evidenceName] -Kind Negative `
        -ExpectedStatus ([string]$expectation.status) -ExpectedOutcomeExitCode ([string]$expectation.outcomeExitCode) `
        -ExpectedPhase ([string]$expectation.phase) -ExpectedCategory ([string]$expectation.category) `
        -ExpectedRuleId ([string]$expectation.outcomeRuleId) `
        -ExpectedDiagnosticRuleIds @($expectation.ruleIds | ForEach-Object { [string]$_ })
}

$null = Assert-QualificationLogContent `
    -FilePath $evidencePathsByName['qualification-evidence/qualification.log']

$approvedIds = @('Hexalith.Builds.Evidence.Cli', 'Hexalith.Builds.Module.Cli')
$inventoryPackages = @($inventory.packages | Sort-Object -Property id)
$inventoryIds = @($inventoryPackages | ForEach-Object { [string]$_.id } | Sort-Object)
if ($inventoryPackages.Count -ne 2 -or (($inventoryIds -join '|') -cne ($approvedIds -join '|'))) {
    throw 'Package inventory must contain exactly the two approved G-4 tool package IDs.'
}

$expectedArtifactNames = [System.Collections.Generic.List[string]]::new()
$primaryArtifactNames = [System.Collections.Generic.List[string]]::new()
foreach ($package in $inventoryPackages) {
    if ([string]$package.version -cne $Version) {
        throw "Package inventory row '$($package.id)' does not bind version '$Version'."
    }
    $primaryArtifactNames.Add([string]$package.nupkg.file)
    foreach ($artifactName in @($package.nupkg.file, $package.snupkg.file)) {
        $expectedArtifactNames.Add([string]$artifactName)
    }

    foreach ($artifact in @($package.nupkg, $package.snupkg)) {
        $artifactPath = Join-Path $packageDirectoryPath $artifact.file
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            throw "Inventory artifact '$artifactPath' is missing."
        }

        $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
        $actualSize = (Get-Item -LiteralPath $artifactPath).Length
        if ($actualHash -cne $artifact.sha256 -or [long]$artifact.sizeBytes -ne $actualSize) {
            throw "Hash or size mismatch for '$artifactPath'; publication is blocked."
        }

    }

    $null = Assert-CanonicalNuGetArtifact -ArchivePath (Join-Path $packageDirectoryPath $package.nupkg.file) `
        -ExpectedRole Package -ExpectedId ([string]$package.id) -ExpectedVersion $Version
    $null = Assert-CanonicalNuGetArtifact -ArchivePath (Join-Path $packageDirectoryPath $package.snupkg.file) `
        -ExpectedRole Symbols -ExpectedId ([string]$package.id) -ExpectedVersion $Version
}

$actualArtifactNames = @(
    Get-ChildItem -LiteralPath $packageDirectoryPath -File |
        Where-Object { $_.Extension -in @('.nupkg', '.snupkg') } |
        ForEach-Object Name |
        Sort-Object
)
$artifactDifference = @(Compare-Object -ReferenceObject @($expectedArtifactNames | Sort-Object) -DifferenceObject $actualArtifactNames)
if ($artifactDifference.Count -gt 0 -or $actualArtifactNames.Count -ne 4) {
    throw 'The package directory contains unexpected, missing, or stale NuGet artifacts.'
}

if ($Version.Contains('-', [System.StringComparison]::Ordinal)) {
    $source = 'https://nuget.pkg.github.com/Hexalith/index.json'
    $apiKey = $env:GITHUB_TOKEN
    $channel = 'GitHub Packages prerelease'
}
else {
    $source = 'https://api.nuget.org/v3/index.json'
    $apiKey = $env:NUGET_API_KEY
    $channel = 'NuGet.org stable release'
}

if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw "A package API key is required to publish the $channel."
}

Write-Host "Publishing the verified G-4 tool package inventory to $channel."
# dotnet discovers and publishes the adjacent .snupkg automatically. Submitting
# the symbol package again produces a duplicate-symbol 409 from NuGet.org.
foreach ($artifactName in @($primaryArtifactNames | Sort-Object)) {
    $artifactPath = Join-Path $packageDirectoryPath $artifactName
    Invoke-DotNet -Arguments @('nuget', 'push', $artifactPath, '--api-key', $apiKey, '--source', $source)
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value '## G-4 tool package inventory'
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ''
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ("Published version: ``{0}`` ({1})" -f $Version, $channel)
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ''
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value '| Package | Artifact | SHA-256 |'
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value '| --- | --- | --- |'
    foreach ($package in $inventoryPackages) {
        foreach ($artifact in @($package.nupkg, $package.snupkg)) {
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ("| {0} | {1} | ``{2}`` |" -f $package.id, $artifact.file, $artifact.sha256)
        }
    }
}
