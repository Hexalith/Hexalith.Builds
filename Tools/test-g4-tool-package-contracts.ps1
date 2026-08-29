#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    [string] $PackageDirectory,

    [switch] $SkipSourceValidation,

    [switch] $RequireControls,

    [switch] $RetainPackageDirectory,

    [string] $FixtureRoot = 'test/fixtures',

    [Parameter(DontShow = $true)]
    [string] $PackageBuildScriptPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1')

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version '$Version' is not a supported NuGet semantic version."
}

if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-builds-g4-packages-$([System.Guid]::NewGuid().ToString('N'))"
}
elseif (-not [System.IO.Path]::IsPathRooted($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot $PackageDirectory
}

$packageDirectoryPath = [System.IO.Path]::GetFullPath($PackageDirectory)
if (-not [System.IO.Path]::IsPathRooted($FixtureRoot)) {
    $FixtureRoot = Join-Path $repositoryRoot $FixtureRoot
}

$fixtureRootPath = [System.IO.Path]::GetFullPath($FixtureRoot)
if (Test-Path -LiteralPath $packageDirectoryPath) {
    throw "Package qualification directory '$packageDirectoryPath' must not already exist; every qualification attempt requires a unique directory."
}

$ownsPackageDirectory = $true
$hadNuGetPackages = Test-Path -LiteralPath Env:NUGET_PACKAGES
$previousNuGetPackages = $env:NUGET_PACKAGES
$hadDotNetCliHome = Test-Path -LiteralPath Env:DOTNET_CLI_HOME
$previousDotNetCliHome = $env:DOTNET_CLI_HOME
$consumerEnvironmentConfigured = $false
$qualificationLog = [System.Collections.Generic.List[string]]::new()

function Format-SafeArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $redactNext = $false
    $safeArguments = foreach ($argument in $Arguments) {
        if ($redactNext) {
            '<redacted>'
            $redactNext = $false
            continue
        }

        $argument
        if ($argument -in @('--filter', '--api-key')) {
            $redactNext = $true
        }
    }

    return $safeArguments -join ' '
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [string] $WorkingDirectory = $repositoryRoot
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $qualificationLog.Add("$ dotnet $($Arguments -join ' ')")
        $output = @(& dotnet @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
        foreach ($line in $output) {
            $qualificationLog.Add([string]$line)
            Write-Host ([string]$line)
        }

        if ($exitCode -ne 0) {
            throw "dotnet $($Arguments -join ' ') failed with exit code $exitCode."
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-ToolCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Command,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $qualificationLog.Add("$ dotnet tool run $Command -- $(Format-SafeArguments -Arguments $Arguments)")
        $output = @(& dotnet tool run $Command -- @Arguments 2>&1)
        foreach ($line in $output) {
            $qualificationLog.Add([string]$line)
        }

        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = $output -join [Environment]::NewLine
        }
    }
    finally {
        Pop-Location
    }
}

function Get-RequiredFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string[]] $Extensions,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "$Description directory '$Directory' is required for packaged-tool qualification."
    }

    $fixtures = @(
        Get-ChildItem -LiteralPath $Directory -File -Recurse |
            Where-Object {
                $_.Extension -in $Extensions -and
                -not $_.Name.EndsWith('.expected.json', [System.StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object -Property FullName
    )
    if ($fixtures.Count -ne 1) {
        throw "$Description directory '$Directory' must contain exactly one fixture; found $($fixtures.Count)."
    }

    return $fixtures[0]
}

function Get-FixtureExpectationPath {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $Fixture
    )

    $baseNameExpectation = Join-Path $Fixture.DirectoryName "$($Fixture.BaseName).expected.json"
    if (Test-Path -LiteralPath $baseNameExpectation -PathType Leaf) {
        return $baseNameExpectation
    }

    $fullNameExpectation = "$($Fixture.FullName).expected.json"
    if (Test-Path -LiteralPath $fullNameExpectation -PathType Leaf) {
        return $fullNameExpectation
    }

    return $baseNameExpectation
}

function Get-NegativeFixtures {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string[]] $Extensions,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "$Description directory '$Directory' is required for packaged-tool qualification."
    }

    $fixtures = @(
        Get-ChildItem -LiteralPath $Directory -File -Recurse |
            Where-Object {
                $_.Extension -in $Extensions -and
                -not $_.Name.EndsWith('.expected.json', [System.StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object -Property FullName
    )
    if ($fixtures.Count -eq 0) {
        throw "$Description directory '$Directory' must contain one or more blocking negative fixtures."
    }

    foreach ($fixture in $fixtures) {
        $expectedPath = Get-FixtureExpectationPath -Fixture $fixture
        if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
            throw "Negative fixture '$($fixture.FullName)' requires '$expectedPath' with exitCode and ruleId."
        }
    }

    return $fixtures
}

function Get-FixtureProvenanceMode {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory
    )

    $repositoryFixtureRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'test/fixtures'))
    if (-not [StringComparer]::Ordinal.Equals([IO.Path]::GetFullPath($Directory), $repositoryFixtureRoot)) {
        return 'external'
    }

    # Fixtures are contract inputs: distinguish untracked and modified repository
    # bytes so local qualification can retain truthful nonrelease evidence.
    & git -C $repositoryRoot rev-parse --is-inside-work-tree *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Repository fixture provenance cannot be established outside a Git worktree.'
    }

    $untracked = @(
        & git -C $repositoryRoot status --porcelain --ignored=matching --untracked-files=all -- $Directory |
            Where-Object { $_ -match '^(\?\?|!!) ' }
    )
    if ($untracked.Count -gt 0) {
        return 'repository-untracked'
    }

    & git -C $repositoryRoot diff --quiet HEAD -- test/fixtures
    if ($LASTEXITCODE -eq 1) {
        return 'repository-modified'
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Repository fixture bytes could not be compared with HEAD (git diff exit code $LASTEXITCODE)."
    }

    return 'repository-tracked'
}

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Get-FixtureManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string] $Mode
    )

    $files = @(
        Get-ChildItem -LiteralPath $Directory -File -Recurse |
            Sort-Object -Property FullName |
            ForEach-Object {
                [ordered] @{
                    file = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/')
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    sizeBytes = $_.Length
                }
            }
    )
    if ($files.Count -eq 0) {
        throw "Fixture root '$Directory' contains no files."
    }

    $material = [string]::Join("`n", @($files | ForEach-Object { "$($_.file)|$($_.sha256)|$($_.sizeBytes)" }))
    return [ordered] @{
        mode = $Mode
        root = $(if ($Mode.StartsWith('repository-', [StringComparison]::Ordinal)) { 'test/fixtures' } else { '<external>' })
        sha256 = Get-Sha256Text -Value $material
        files = $files
    }
}

function Assert-PositiveResult {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if ($Result.ExitCode -ne 0) {
        throw "$Description was expected to pass but exited $($Result.ExitCode)."
    }
}

function Assert-ToolHelp {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Description,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedText
    )

    Assert-PositiveResult -Result $Result -Description $Description
    if ($Result.Output.Contains('Run a local tool. Note that this command cannot be used to run a global tool.', [StringComparison]::Ordinal)) {
        throw "$Description returned dotnet tool wrapper help instead of tool-specific help."
    }
    if (-not $Result.Output.Contains($ExpectedText, [StringComparison]::Ordinal)) {
        throw "$Description did not contain the tool-specific contract '$ExpectedText'."
    }
}

function ConvertFrom-ToolResult {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    try {
        $decoded = $Result.Output | ConvertFrom-Json
    }
    catch {
        throw "$Description did not emit one decodable JSON command result: $($_.Exception.Message)"
    }

    if ($null -eq $decoded -or $null -eq $decoded.outcome -or $null -eq $decoded.diagnostics) {
        throw "$Description did not emit the required status, outcome, and diagnostics contract."
    }

    return $decoded
}

function Assert-ExactRuleIds {
    param(
        [AllowEmptyCollection()]
        [string[]] $Actual,

        [AllowEmptyCollection()]
        [string[]] $Expected,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if ([string]::Join('|', $Actual) -cne [string]::Join('|', $Expected)) {
        throw "$Description emitted diagnostic rule IDs '$($Actual -join ', ')', expected '$($Expected -join ', ')'."
    }
}

function Assert-JsonToolResult {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Description,

        [Parameter(Mandatory = $true)]
        [string] $Status,

        [Parameter(Mandatory = $true)]
        [int] $ExitCode,

        [Parameter(Mandatory = $true)]
        [string] $OutcomeExitCode,

        [Parameter(Mandatory = $true)]
        [string] $Phase,

        [Parameter(Mandatory = $true)]
        [string] $Category,

        [AllowNull()]
        [object] $OutcomeRuleId,

        [AllowEmptyCollection()]
        [string[]] $RuleIds
    )

    if ($Result.ExitCode -ne $ExitCode) {
        throw "$Description exited $($Result.ExitCode), expected $ExitCode."
    }

    $decoded = ConvertFrom-ToolResult -Result $Result -Description $Description
    $hasExactRuleId = if ($null -eq $OutcomeRuleId) {
        $null -eq $decoded.outcome.ruleId
    }
    else {
        $decoded.outcome.ruleId -is [string] -and $decoded.outcome.ruleId -ceq $OutcomeRuleId
    }
    if ($decoded.status -isnot [string] -or $decoded.status -cne $Status -or
        $decoded.outcome.exitCode -isnot [string] -or $decoded.outcome.exitCode -cne $OutcomeExitCode -or
        $decoded.outcome.phase -isnot [string] -or $decoded.outcome.phase -cne $Phase -or
        $decoded.outcome.category -isnot [string] -or $decoded.outcome.category -cne $Category -or
        -not $hasExactRuleId) {
        throw "$Description emitted a result contract that does not match status '$Status', process exit $ExitCode, outcome exit '$OutcomeExitCode', phase '$Phase', category '$Category', and outcome rule '$OutcomeRuleId'."
    }

    Assert-ExactRuleIds -Actual @($decoded.diagnostics | ForEach-Object { [string]$_.ruleId }) -Expected $RuleIds -Description $Description
    return $decoded
}

function Assert-ModuleEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $FinalStatus,

        [Parameter(Mandatory = $true)]
        [int] $ExitCode,

        [AllowNull()]
        [object] $RuleId,

        [Parameter(Mandatory = $true)]
        [string] $Phase,

        [Parameter(Mandatory = $true)]
        [string] $Category,

        [Parameter(Mandatory = $true)]
        [string] $Command,

        [Parameter(Mandatory = $true)]
        [string] $ToolVersion,

        [Parameter(Mandatory = $true)]
        [string] $RepositoryRevision,

        [Parameter(Mandatory = $true)]
        [string] $RepositoryDirtyMarker,

        [Parameter(Mandatory = $true)]
        [string] $ManifestHash,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description artifact '$Path' does not exist."
    }

    try {
        $evidence = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "$Description artifact '$Path' is not decodable JSON: $($_.Exception.Message)"
    }

    $hasNumericExitCode = $evidence.outcome.exitCode -is [int] -or
        $evidence.outcome.exitCode -is [long] -or
        $evidence.outcome.exitCode -is [decimal]
    $hasExactRuleId = if ($null -eq $RuleId) {
        $null -eq $evidence.outcome.ruleId
    }
    else {
        $evidence.outcome.ruleId -is [string] -and $evidence.outcome.ruleId -ceq $RuleId
    }
    if ($evidence.schema -isnot [string] -or $evidence.schema -cne 'hexalith.module-run-evidence.v1' -or
        $evidence.topology.platform.eventStoreVersion -isnot [string] -or $evidence.topology.platform.eventStoreVersion -cne '3.90.0' -or
        $evidence.finalStatus -isnot [string] -or $evidence.finalStatus -cne $FinalStatus -or
        -not $hasNumericExitCode -or [long]$evidence.outcome.exitCode -ne $ExitCode -or
        $evidence.outcome.phase -isnot [string] -or $evidence.outcome.phase -cne $Phase -or
        $evidence.outcome.category -isnot [string] -or $evidence.outcome.category -cne $Category -or
        -not $hasExactRuleId -or
        $evidence.invocation.command -isnot [string] -or $evidence.invocation.command -cne $Command -or
        $evidence.environment.toolVersion -isnot [string] -or $evidence.environment.toolVersion -cne $ToolVersion -or
        $evidence.environment.repositoryRevision -isnot [string] -or $evidence.environment.repositoryRevision -cne $RepositoryRevision -or
        $evidence.environment.repositoryDirtyMarker -isnot [string] -or $evidence.environment.repositoryDirtyMarker -cne $RepositoryDirtyMarker -or
        $evidence.invocation.manifestHash -isnot [string] -or $evidence.invocation.manifestHash -cne $ManifestHash) {
        throw "$Description artifact does not match the exact schema, EventStore pin, status/outcome types, tool version, source revision, dirty marker, manifest hash, and invocation binding."
    }

    return $evidence
}

function Assert-NegativeResult {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,

        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $Fixture
    )

    $expectedPath = Get-FixtureExpectationPath -Fixture $Fixture
    $expected = Get-Content -LiteralPath $expectedPath -Raw | ConvertFrom-Json
    if ($null -eq $expected.exitCode -or [string]::IsNullOrWhiteSpace($expected.status) -or
        [string]::IsNullOrWhiteSpace($expected.outcomeExitCode) -or [string]::IsNullOrWhiteSpace($expected.phase) -or [string]::IsNullOrWhiteSpace($expected.category) -or
        [string]::IsNullOrWhiteSpace($expected.outcomeRuleId) -or $null -eq $expected.ruleIds) {
        throw "Negative fixture expectation '$expectedPath' must contain status, nonzero process exitCode, outcomeExitCode, phase, category, outcomeRuleId, and ordered ruleIds."
    }

    if ([int]$expected.exitCode -eq 0) {
        throw "Negative fixture expectation '$expectedPath' cannot declare a successful exit code."
    }

    $null = Assert-JsonToolResult -Result $Result -Description "Negative fixture '$($Fixture.FullName)'" `
        -Status ([string]$expected.status) -ExitCode ([int]$expected.exitCode) -Phase ([string]$expected.phase) `
        -OutcomeExitCode ([string]$expected.outcomeExitCode) -Category ([string]$expected.category) `
        -OutcomeRuleId ([string]$expected.outcomeRuleId) `
        -RuleIds @($expected.ruleIds | ForEach-Object { [string]$_ })
}

function Assert-ExactNonPassingResult {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,

        [Parameter(Mandatory = $true)]
        [int] $ExitCode,

        [Parameter(Mandatory = $true)]
        [string] $RuleId,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    $null = Assert-JsonToolResult -Result $Result -Description $Description -Status 'unavailable' -ExitCode $ExitCode `
        -OutcomeExitCode 'PrerequisiteUnavailable' `
        -Phase 'Prerequisite' -Category 'PrerequisiteUnavailable' -OutcomeRuleId $RuleId -RuleIds @($RuleId)
}

function Assert-UniqueQualificationEvidenceNames {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo[]] $ModuleNegatives,

        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo[]] $EvidenceNegatives
    )

    if (@(Get-ChildItem -LiteralPath $Directory -Force).Count -ne 0) {
        throw "Qualification evidence directory '$Directory' must be empty before controls start."
    }

    $names = @(
        'source-release-passed.json'
        'packaged-down-output.json'
        'packaged-down-evidence.json'
        'packaged-test-output.json'
        'packaged-test-evidence.json'
        'packaged-unavailable-output.json'
        'packaged-unavailable-evidence.json'
        'packaged-readiness-output.json'
        'qualification.log'
        $ModuleNegatives | ForEach-Object { "module-negative-$($_.BaseName)-output.json" }
        $EvidenceNegatives | ForEach-Object { "evidence-negative-$($_.BaseName)-output.json" }
    )
    $duplicates = @($names | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    if ($duplicates.Count -gt 0) {
        throw "Qualification evidence output names must be unique; duplicate names: $($duplicates -join ', ')."
    }
}

function Complete-QualificationInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string] $PackageVersion,

        [Parameter(Mandatory = $true)]
        [string] $SourceValidationMode,

        [Parameter(Mandatory = $true)]
        [string] $ControlsMode,

        [Parameter(Mandatory = $true)]
        [object] $FixtureManifest,

        [Parameter(Mandatory = $true)]
        [string] $PackageBuildMode,

        [Parameter(Mandatory = $true)]
        [object] $SourceTreeState,

        [AllowEmptyCollection()]
        [string[]] $FixtureByteProofFailures = @()
    )

    $inventoryPath = Join-Path $Directory 'g4-tool-package-inventory.json'
    if (Test-Path -LiteralPath $inventoryPath) {
        throw "Qualification inventory '$inventoryPath' existed before all controls completed."
    }

    $packageIds = @('Hexalith.Builds.Evidence.Cli', 'Hexalith.Builds.Module.Cli')
    $inventoryPackages = foreach ($packageId in $packageIds) {
        $nupkg = Join-Path $Directory "$packageId.$PackageVersion.nupkg"
        $snupkg = Join-Path $Directory "$packageId.$PackageVersion.snupkg"
        if (-not (Test-Path -LiteralPath $nupkg -PathType Leaf) -or -not (Test-Path -LiteralPath $snupkg -PathType Leaf)) {
            throw "Qualified package pair for '$packageId' is incomplete."
        }

        # Reject swapped or canonical-role-invalid nupkg/snupkg records: open each
        # retained artifact and prove its identity and role from its nuspec content
        # (exactly one nuspec, correct id/version, SymbolsPackage type required on
        # the .snupkg and forbidden on the .nupkg) independently of its file name.
        $null = Assert-CanonicalNuGetArtifact -ArchivePath $nupkg -ExpectedRole 'Package' -ExpectedId $packageId -ExpectedVersion $PackageVersion
        $null = Assert-CanonicalNuGetArtifact -ArchivePath $snupkg -ExpectedRole 'Symbols' -ExpectedId $packageId -ExpectedVersion $PackageVersion

        [ordered]@{
            id = $packageId
            version = $PackageVersion
            nupkg = [ordered]@{
                file = [System.IO.Path]::GetFileName($nupkg)
                sha256 = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash
                sizeBytes = (Get-Item -LiteralPath $nupkg).Length
            }
            snupkg = [ordered]@{
                file = [System.IO.Path]::GetFileName($snupkg)
                sha256 = (Get-FileHash -LiteralPath $snupkg -Algorithm SHA256).Hash
                sizeBytes = (Get-Item -LiteralPath $snupkg).Length
            }
        }
    }

    $qualificationEvidenceRoot = Join-Path $Directory 'qualification-evidence'
    $qualificationEvidence = if (Test-Path -LiteralPath $qualificationEvidenceRoot -PathType Container) {
        @(
            Get-ChildItem -LiteralPath $qualificationEvidenceRoot -File -Recurse |
                Sort-Object -Property FullName |
                ForEach-Object {
                    [ordered]@{
                        file = [System.IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/')
                        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                        sizeBytes = $_.Length
                    }
                }
        )
    }
    else {
        @()
    }

    # releaseEligible binds together every gate above: it can be true only for a
    # candidate that ran the official source-validating build (never
    # -SkipSourceValidation), exercised every required control, proved its
    # fixtures repository-tracked, built through the official package script, and
    # was qualified against a clean, immutable source tree. Any external/untracked,
    # partial, or dirty-tree candidate remains non-eligible but still receives an
    # honest inventory recording exactly why.
    $ineligibilityReasons = [System.Collections.Generic.List[string]]::new()
    if ($SourceValidationMode -cne 'executed') {
        $ineligibilityReasons.Add('source validation was skipped (-SkipSourceValidation).')
    }
    if ($ControlsMode -cne 'executed') {
        $ineligibilityReasons.Add('qualification controls were not required (-RequireControls not set).')
    }
    if ($FixtureManifest.mode -cne 'repository-tracked') {
        $ineligibilityReasons.Add("fixture provenance was '$($FixtureManifest.mode)', not repository-tracked.")
    }
    if ($PackageBuildMode -cne 'official') {
        $ineligibilityReasons.Add("package build mode was '$PackageBuildMode', not official.")
    }
    if (-not $SourceTreeState.Clean) {
        $ineligibilityReasons.Add("source tree was not clean: $([string]::Join('; ', @($SourceTreeState.Reasons)))")
    }
    foreach ($fixtureByteProofFailure in @($FixtureByteProofFailures)) {
        $ineligibilityReasons.Add($fixtureByteProofFailure)
    }

    $inventory = [ordered]@{
        schema = 'hexalith.g4-tool-package-inventory.v1'
        version = $PackageVersion
        configuration = 'Release'
        packages = @($inventoryPackages)
        qualificationEvidence = @($qualificationEvidence)
        qualification = [ordered] @{
            packageBuild = [ordered] @{
                mode = $PackageBuildMode
                result = 'passed'
            }
            sourceValidation = [ordered] @{
                mode = $SourceValidationMode
                result = $(if ($SourceValidationMode -ceq 'executed') { 'passed' } else { 'not-run' })
            }
            controls = [ordered] @{
                mode = $ControlsMode
                result = $(if ($ControlsMode -ceq 'executed') { 'passed' } else { 'not-run' })
            }
            fixtures = $FixtureManifest
            sourceTree = [ordered] @{
                clean = $SourceTreeState.Clean
                revision = $SourceTreeState.Revision
            }
            releaseEligible = $ineligibilityReasons.Count -eq 0
            ineligibilityReasons = @($ineligibilityReasons)
        }
    }
    $inventory | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $inventoryPath -Encoding utf8

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "g4_tool_package_directory=$Directory"
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "g4_tool_package_inventory=$inventoryPath"
    }

    return $inventoryPath
}

$consumerRoot = $null
$sourceValidationMode = $(if ($SkipSourceValidation) { 'skipped' } else { 'executed' })
$controlsMode = $(if ($RequireControls) { 'executed' } else { 'skipped' })
$packageBuildMode = $(if ([string]::IsNullOrWhiteSpace($PackageBuildScriptPath)) { 'official' } else { 'fixture' })
$fixtureManifest = [ordered] @{
    mode = 'not-used'
    root = $null
    sha256 = $null
    files = @()
}
$fixtureByteProofFailures = [System.Collections.Generic.List[string]]::new()

try {
    if (-not $SkipSourceValidation) {
        $solutionPath = Join-Path $repositoryRoot 'Hexalith.Builds.slnx'
        Invoke-DotNet -Arguments @('restore', $solutionPath)
        Invoke-DotNet -Arguments @('build', $solutionPath, '--configuration', 'Release', '--no-restore', '-p:GeneratePackageOnBuild=false')

        $testProjects = @(
            Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'test') -File -Filter '*.csproj' -Recurse |
                Sort-Object -Property FullName
        )
        if ($testProjects.Count -eq 0) {
            throw 'No G-4 test projects were found under test/.'
        }

        foreach ($testProject in $testProjects) {
            Invoke-DotNet -Arguments @('test', $testProject.FullName, '--configuration', 'Release', '--no-restore')
        }
    }

    # Bind the qualified source revision and its clean/dirty state now, before any
    # package or control output could be attributed to it. A dirty tree does not
    # abort qualification; it permanently forecloses releaseEligible below.
    $sourceTreeState = Get-SourceTreeState -RepositoryRoot $repositoryRoot

    $buildScript = if ([string]::IsNullOrWhiteSpace($PackageBuildScriptPath)) {
        Join-Path $PSScriptRoot 'build-g4-tool-packages.ps1'
    }
    else {
        (Resolve-Path -LiteralPath $PackageBuildScriptPath -ErrorAction Stop).ProviderPath
    }
    $qualificationLog.Add("$ $buildScript -Version $Version -OutputDirectory $packageDirectoryPath -DeferInventory")
    $buildOutput = @(& $buildScript -Version $Version -OutputDirectory $packageDirectoryPath -DeferInventory 2>&1)
    foreach ($line in $buildOutput) {
        $qualificationLog.Add([string]$line)
        Write-Host ([string]$line)
    }

    $qualificationEvidenceRoot = Join-Path $packageDirectoryPath 'qualification-evidence'
    $null = New-Item -ItemType Directory -Path $qualificationEvidenceRoot

    $consumerRoot = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-builds-g4-consumer-$([System.Guid]::NewGuid().ToString('N'))"
    $toolManifestDirectory = Join-Path $consumerRoot '.config'
    $toolManifestPath = Join-Path $toolManifestDirectory 'dotnet-tools.json'
    $nuGetConfigPath = Join-Path $consumerRoot 'NuGet.Config'
    $null = New-Item -ItemType Directory -Path $toolManifestDirectory -Force

    $toolManifest = [ordered]@{
        version = 1
        isRoot = $true
        tools = [ordered]@{
            'hexalith.builds.module.cli' = [ordered]@{
                version = $Version
                commands = @('hexalith-module')
            }
            'hexalith.builds.evidence.cli' = [ordered]@{
                version = $Version
                commands = @('hexalith-evidence')
            }
        }
    }
    $toolManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $toolManifestPath -Encoding utf8

    $env:NUGET_PACKAGES = Join-Path $consumerRoot '.nuget/packages'
    $env:DOTNET_CLI_HOME = Join-Path $consumerRoot '.dotnet'
    $consumerEnvironmentConfigured = $true

    $escapedPackageDirectory = [System.Security.SecurityElement]::Escape($packageDirectoryPath)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="g4-local" value="$escapedPackageDirectory" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nuGetConfigPath -Encoding utf8

    Invoke-DotNet -Arguments @('tool', 'restore', '--configfile', $nuGetConfigPath, '--no-http-cache') -WorkingDirectory $consumerRoot

    Assert-ToolHelp -Description 'hexalith-module help' -ExpectedText 'Runs supported Hexalith module qualifications.' `
        -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('--help') -WorkingDirectory $consumerRoot)
    Assert-ToolHelp -Description 'hexalith-module run help' -ExpectedText 'Starts a supported module runtime.' `
        -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('run', '--help') -WorkingDirectory $consumerRoot)
    Assert-ToolHelp -Description 'hexalith-module down help' -ExpectedText 'Tears down runner-owned module resources.' `
        -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('down', '--help') -WorkingDirectory $consumerRoot)
    Assert-ToolHelp -Description 'hexalith-module test help' -ExpectedText 'Runs a named module qualification profile.' `
        -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('test', '--help') -WorkingDirectory $consumerRoot)
    Assert-ToolHelp -Description 'hexalith-evidence help' -ExpectedText 'Validates deterministic Hexalith readiness evidence.' `
        -Result (Invoke-ToolCommand -Command 'hexalith-evidence' -Arguments @('--help') -WorkingDirectory $consumerRoot)
    Assert-ToolHelp -Description 'hexalith-evidence validate help' -ExpectedText 'Validates a hexalith.readiness-evidence.v1 YAML matrix.' `
        -Result (Invoke-ToolCommand -Command 'hexalith-evidence' -Arguments @('validate', '--help') -WorkingDirectory $consumerRoot)

    if ($RequireControls) {
        $fixtureProvenanceMode = Get-FixtureProvenanceMode -Directory $fixtureRootPath
        $fixtureManifest = Get-FixtureManifest -Directory $fixtureRootPath -Mode $fixtureProvenanceMode
        $fixtureRootRelativePath = ([IO.Path]::GetRelativePath($repositoryRoot, $fixtureRootPath)).Replace('\', '/')
        $fixtureProof = [ordered] @{}
        $qualificationEvidenceEntries = [System.Collections.Generic.List[object]]::new()
        $consumerFixturesRoot = Join-Path $consumerRoot 'test/fixtures'
        $null = New-Item -ItemType Directory -Path $consumerFixturesRoot -Force
        foreach ($fixtureDirectoryName in @('module', 'evidence')) {
            $sourceFixtureDirectory = Join-Path $fixtureRootPath $fixtureDirectoryName
            Copy-Item -LiteralPath $sourceFixtureDirectory -Destination $consumerFixturesRoot -Recurse -Force
        }

        # Prove every byte the packaged consumer is about to exercise is identical to
        # what the bound source revision tracks. Assert-FixturesTracked above only
        # rules out untracked/ignored files; a tracked-but-locally-edited fixture
        # would pass that check yet still be irreproducible from a fresh checkout.
        #
        # A CLEAN tree that disagrees with its own HEAD is an impossible state --
        # tampering between the binding and the copy -- and fails closed. A DIRTY
        # tree is expected to disagree: the run still completes with honest,
        # complete evidence, but the unproven directories are recorded and
        # permanently foreclose releaseEligible below. No candidate can ever become
        # release-eligible without a passing byte proof.
        if ($fixtureProvenanceMode -ceq 'external') {
            foreach ($fixtureDirectoryName in @('module', 'evidence')) {
                $fixtureProof[$fixtureDirectoryName] = 'unproven'
            }
            $fixtureByteProofFailures.Add(
                'tracked-fixture-vs-HEAD byte proof was not attempted for an external fixture root.')
        }
        else {
            foreach ($fixtureDirectoryName in @('module', 'evidence')) {
                $copiedFixtureDirectory = Join-Path $consumerFixturesRoot $fixtureDirectoryName
                try {
                    $matchedFileCount = Assert-TrackedFixtureBytesMatchHead -RepositoryRoot $repositoryRoot `
                        -SourceRevision $sourceTreeState.Revision `
                        -FixtureDirectory $copiedFixtureDirectory `
                        -RepositoryRelativeRoot "$fixtureRootRelativePath/$fixtureDirectoryName"
                    $fixtureProof[$fixtureDirectoryName] = $matchedFileCount
                }
                catch {
                    if ($sourceTreeState.Clean) {
                        throw
                    }

                    $fixtureProof[$fixtureDirectoryName] = 'unproven'
                    $fixtureByteProofFailures.Add(
                        "tracked-fixture-vs-HEAD byte proof did not hold for '$fixtureRootRelativePath/$fixtureDirectoryName': $($_.Exception.Message)")
                }
            }
        }
        $fixtureManifest['trackedByteMatchCounts'] = $fixtureProof

        & git -C $consumerRoot init --quiet
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to initialize the isolated package consumer repository (exit code $LASTEXITCODE)."
        }

        $modulePositive = Get-RequiredFixture -Directory (Join-Path $consumerFixturesRoot 'module/positive') -Extensions @('.json') -Description 'Positive module manifest'
        $evidencePositive = Get-RequiredFixture -Directory (Join-Path $consumerFixturesRoot 'evidence/positive') -Extensions @('.yaml', '.yml') -Description 'Positive readiness evidence'
        $moduleNegatives = Get-NegativeFixtures -Directory (Join-Path $consumerFixturesRoot 'module/negative') -Extensions @('.json') -Description 'Module manifest negative control'
        $evidenceNegatives = Get-NegativeFixtures -Directory (Join-Path $consumerFixturesRoot 'evidence/negative') -Extensions @('.yaml', '.yml') -Description 'Readiness evidence negative control'
        Assert-UniqueQualificationEvidenceNames -Directory $qualificationEvidenceRoot `
            -ModuleNegatives $moduleNegatives -EvidenceNegatives $evidenceNegatives
        $moduleManifestPath = 'test/fixtures/module/positive/hexalith.module-manifest.v1.json'
        $readinessEvidencePath = 'test/fixtures/evidence/positive/readiness.yaml'
        $sourceRevisionOutput = @(& git -C $repositoryRoot rev-parse HEAD 2>&1)
        $sourceRevision = if ($LASTEXITCODE -eq 0 -and $sourceRevisionOutput.Count -eq 1) {
            ([string]$sourceRevisionOutput[0]).Trim()
        }
        else {
            ''
        }
        if ($sourceRevision -cnotmatch '^[0-9a-f]{40}$') {
            throw 'Packaged tool source revision could not be bound to one full Git commit.'
        }
        $packagedToolVersion = "$Version+$sourceRevision"
        $packagedManifestHash = (Get-FileHash -LiteralPath $modulePositive.FullName -Algorithm SHA256).Hash
        $packagedFilterHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('Bearer packaged-redaction-control')))
        $sourceEvidencePath = Join-Path $fixtureRootPath 'evidence/positive/evidence/release-passed.json'
        $null = Assert-ModuleEvidence -Path $sourceEvidencePath -FinalStatus 'completed' -ExitCode 0 -RuleId $null `
            -Phase 'None' -Category 'None' -Command 'hexalith-module test --profile full' `
            -ToolVersion '0.0.0-contract' -RepositoryRevision '0123456789abcdef0123456789abcdef01234567' `
            -RepositoryDirtyMarker 'clean' -ManifestHash ('A' * 64) `
            -Description 'Source readiness evidence'

        Copy-Item -LiteralPath $sourceEvidencePath -Destination (Join-Path $qualificationEvidenceRoot 'source-release-passed.json')

        $downResult = Invoke-ToolCommand -Command 'hexalith-module' -Arguments @(
            'down',
            '--manifest',
            $moduleManifestPath,
            '--filter',
            'Bearer packaged-redaction-control',
            '--evidence',
            'evidence/module-run.json',
            '--output',
            'json'
        ) -WorkingDirectory $consumerRoot
        $null = Assert-JsonToolResult -Result $downResult -Description 'Positive module manifest and canonical evidence' `
            -Status 'completed' -ExitCode 0 -OutcomeExitCode 'Success' -Phase 'None' -Category 'None' -OutcomeRuleId $null -RuleIds @('HXI001')
        $qualificationEvidenceEntries.Add((Save-QualificationEvidence -EvidenceDirectory $qualificationEvidenceRoot -Name 'packaged-down-output' -Content $downResult.Output))

        $moduleEvidencePath = Join-Path $consumerRoot 'evidence/module-run.json'
        if (-not (Test-Path -LiteralPath $moduleEvidencePath -PathType Leaf)) {
            throw 'Packaged module command did not retain its requested evidence artifact.'
        }

        $moduleEvidenceBytes = [System.IO.File]::ReadAllBytes($moduleEvidencePath)
        if ($moduleEvidenceBytes.Length -eq 0 -or $moduleEvidenceBytes[$moduleEvidenceBytes.Length - 1] -ne [byte][char]"`n") {
            throw 'Packaged module evidence is not canonical UTF-8 JSON with a final newline.'
        }

        $moduleEvidenceText = [System.IO.File]::ReadAllText($moduleEvidencePath)
        if ($moduleEvidenceText.Contains('packaged-redaction-control', [System.StringComparison]::Ordinal) -or
            $moduleEvidenceText.Contains('Bearer', [System.StringComparison]::Ordinal)) {
            throw 'Packaged module evidence retained raw filter or credential material.'
        }

        $moduleEvidence = $moduleEvidenceText | ConvertFrom-Json
        if ($moduleEvidence.schema -cne 'hexalith.module-run-evidence.v1' -or
            $moduleEvidence.invocation.manifestPath -cne $moduleManifestPath) {
            throw 'Packaged module evidence did not retain the expected canonical schema and consumer-relative manifest identity.'
        }
        $null = Assert-ModuleEvidence -Path $moduleEvidencePath -FinalStatus 'completed' -ExitCode 0 -RuleId $null `
            -Phase 'None' -Category 'None' `
            -Command "hexalith-module down --manifest $moduleManifestPath --filter-sha256 $packagedFilterHash" `
            -ToolVersion $packagedToolVersion -RepositoryRevision 'unavailable' -RepositoryDirtyMarker 'dirty' `
            -ManifestHash $packagedManifestHash `
            -Description 'Packaged passing module evidence'
        Copy-Item -LiteralPath $moduleEvidencePath -Destination (Join-Path $qualificationEvidenceRoot 'packaged-down-evidence.json')

        $testResult = Invoke-ToolCommand -Command 'hexalith-module' -Arguments @(
            'test',
            '--manifest',
            $moduleManifestPath,
            '--profile',
            'full',
            '--evidence',
            'evidence/test-unavailable.json',
            '--output',
            'json'
        ) -WorkingDirectory $consumerRoot
        Assert-ExactNonPassingResult -Result $testResult -ExitCode 2 -RuleId 'HXR002' `
            -Description 'Positive packaged module test command path'
        $qualificationEvidenceEntries.Add((Save-QualificationEvidence -EvidenceDirectory $qualificationEvidenceRoot -Name 'packaged-test-output' -Content $testResult.Output))

        $testEvidencePath = Join-Path $consumerRoot 'evidence/test-unavailable.json'
        $null = Assert-ModuleEvidence -Path $testEvidencePath -FinalStatus 'unavailable' -ExitCode 2 -RuleId 'HXR002' `
            -Phase 'Prerequisite' -Category 'PrerequisiteUnavailable' `
            -Command "hexalith-module test --manifest $moduleManifestPath --profile full" `
            -ToolVersion $packagedToolVersion -RepositoryRevision 'unavailable' -RepositoryDirtyMarker 'dirty' `
            -ManifestHash $packagedManifestHash `
            -Description 'Positive packaged module test evidence'
        Copy-Item -LiteralPath $testEvidencePath -Destination (Join-Path $qualificationEvidenceRoot 'packaged-test-evidence.json')

        $unavailableResult = Invoke-ToolCommand -Command 'hexalith-module' -Arguments @(
            'run',
            '--manifest',
            $moduleManifestPath,
            '--evidence',
            'evidence/unavailable.json',
            '--output',
            'json'
        ) -WorkingDirectory $consumerRoot
        Assert-ExactNonPassingResult -Result $unavailableResult -ExitCode 2 -RuleId 'HXR002' -Description 'Unavailable platform prerequisite control'
        $qualificationEvidenceEntries.Add((Save-QualificationEvidence -EvidenceDirectory $qualificationEvidenceRoot -Name 'packaged-unavailable-output' -Content $unavailableResult.Output))

        $unavailableEvidencePath = Join-Path $consumerRoot 'evidence/unavailable.json'
        $null = Assert-ModuleEvidence -Path $unavailableEvidencePath -FinalStatus 'unavailable' -ExitCode 2 -RuleId 'HXR002' `
            -Phase 'Prerequisite' -Category 'PrerequisiteUnavailable' `
            -Command "hexalith-module run --manifest $moduleManifestPath" `
            -ToolVersion $packagedToolVersion -RepositoryRevision 'unavailable' -RepositoryDirtyMarker 'dirty' `
            -ManifestHash $packagedManifestHash `
            -Description 'Packaged unavailable module evidence'
        Copy-Item -LiteralPath $unavailableEvidencePath -Destination (Join-Path $qualificationEvidenceRoot 'packaged-unavailable-evidence.json')

        $positiveEvidenceResult = Invoke-ToolCommand -Command 'hexalith-evidence' -Arguments @('validate', $readinessEvidencePath, '--output', 'json') -WorkingDirectory $consumerRoot
        $null = Assert-JsonToolResult -Result $positiveEvidenceResult -Description 'Positive readiness evidence' `
            -Status 'passed' -ExitCode 0 -OutcomeExitCode 'Success' -Phase 'None' -Category 'None' -OutcomeRuleId $null -RuleIds @()
        $qualificationEvidenceEntries.Add((Save-QualificationEvidence -EvidenceDirectory $qualificationEvidenceRoot -Name 'packaged-readiness-output' -Content $positiveEvidenceResult.Output))

        foreach ($fixture in $moduleNegatives) {
            $negativeResult = Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('down', '--manifest', $fixture.FullName, '--output', 'json') -WorkingDirectory $consumerRoot
            Assert-NegativeResult -Fixture $fixture -Result $negativeResult
            $qualificationEvidenceEntries.Add((Save-QualificationEvidence -EvidenceDirectory $qualificationEvidenceRoot -Name "module-negative-$($fixture.BaseName)-output" -Content $negativeResult.Output))
        }

        foreach ($fixture in $evidenceNegatives) {
            $negativeResult = Invoke-ToolCommand -Command 'hexalith-evidence' -Arguments @('validate', $fixture.FullName, '--output', 'json') -WorkingDirectory $consumerRoot
            Assert-NegativeResult -Fixture $fixture -Result $negativeResult
            $qualificationEvidenceEntries.Add((Save-QualificationEvidence -EvidenceDirectory $qualificationEvidenceRoot -Name "evidence-negative-$($fixture.BaseName)-output" -Content $negativeResult.Output))
        }

        # Coverage is only satisfied once every retained control-output artifact's
        # CONTENT has been independently parsed and validated against the real
        # hexalith-module/hexalith-evidence `--output json` result contract -- not
        # merely hashed, sized, and named. The paired `*-evidence.json` artifacts
        # above are already exhaustively content-validated by Assert-ModuleEvidence.
        $expectedRuleIdsByName = @{
            'packaged-unavailable-output' = 'HXR002'
        }
        foreach ($fixture in $moduleNegatives) {
            $expectedRuleIdsByName["module-negative-$($fixture.BaseName)-output"] =
            [string] ((Get-Content -LiteralPath (Get-FixtureExpectationPath -Fixture $fixture) -Raw | ConvertFrom-Json).outcomeRuleId)
        }
        foreach ($fixture in $evidenceNegatives) {
            $expectedRuleIdsByName["evidence-negative-$($fixture.BaseName)-output"] =
            [string] ((Get-Content -LiteralPath (Get-FixtureExpectationPath -Fixture $fixture) -Raw | ConvertFrom-Json).outcomeRuleId)
        }

        foreach ($entry in $qualificationEvidenceEntries) {
            $entryPath = Join-Path $packageDirectoryPath $entry.file
            $entryName = [IO.Path]::GetFileNameWithoutExtension($entry.file)
            if ($entryName -like 'module-negative-*' -or $entryName -like 'evidence-negative-*') {
                $null = Assert-QualificationEvidenceContent -FilePath $entryPath -Kind 'Negative' -ExpectedRuleId $expectedRuleIdsByName[$entryName]
            }
            elseif ($entryName -eq 'packaged-unavailable-output') {
                $null = Assert-QualificationEvidenceContent -FilePath $entryPath -Kind 'ExactNonPassing' -ExpectedRuleId $expectedRuleIdsByName[$entryName]
            }
            elseif ($entryName -eq 'packaged-test-output') {
                $null = Assert-QualificationEvidenceContent -FilePath $entryPath -Kind 'ExactNonPassing'
            }
            elseif ($entryName -in @('packaged-down-output', 'packaged-readiness-output')) {
                $null = Assert-QualificationEvidenceContent -FilePath $entryPath -Kind 'Positive'
            }
        }
    }

    $qualificationLogText = [string]::Join("`n", $qualificationLog)
    if ($qualificationLogText.Contains('packaged-redaction-control', [StringComparison]::Ordinal) -or
        $qualificationLogText.Contains('Bearer', [StringComparison]::Ordinal)) {
        throw 'Qualification output retained raw filter or credential material.'
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $qualificationEvidenceRoot 'qualification.log'),
        $qualificationLog,
        [System.Text.UTF8Encoding]::new($false))
    $inventoryPath = Complete-QualificationInventory -Directory $packageDirectoryPath -PackageVersion $Version `
        -SourceValidationMode $sourceValidationMode -ControlsMode $controlsMode -FixtureManifest $fixtureManifest `
        -PackageBuildMode $packageBuildMode -SourceTreeState $sourceTreeState `
        -FixtureByteProofFailures @($fixtureByteProofFailures)

    Write-Host "Packed G-4 tool contract qualification passed for version '$Version'; inventory recorded at '$inventoryPath'."
}
finally {
    if ($consumerEnvironmentConfigured) {
        if ($hadNuGetPackages) {
            $env:NUGET_PACKAGES = $previousNuGetPackages
        }
        else {
            Remove-Item -LiteralPath Env:NUGET_PACKAGES
        }

        if ($hadDotNetCliHome) {
            $env:DOTNET_CLI_HOME = $previousDotNetCliHome
        }
        else {
            Remove-Item -LiteralPath Env:DOTNET_CLI_HOME
        }
    }

    if ($null -ne $consumerRoot -and (Test-Path -LiteralPath $consumerRoot)) {
        Remove-Item -LiteralPath $consumerRoot -Recurse -Force
    }

    if ($ownsPackageDirectory -and -not $RetainPackageDirectory -and (Test-Path -LiteralPath $packageDirectoryPath)) {
        Remove-Item -LiteralPath $packageDirectoryPath -Recurse -Force
    }
}
