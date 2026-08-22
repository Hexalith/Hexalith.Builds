[CmdletBinding()]
param()

# Regression suite for the loop-6 publication-hardening functions in
# G4PackageQualification.functions.ps1: nupkg/snupkg canonical-role validation,
# qualification-evidence content parsing, source-tree clean/immutable binding, and
# tracked-fixture-vs-HEAD byte proof. It dot-sources the SAME functions the official
# gate (test-g4-tool-package-contracts.ps1) uses, so these tests exercise the exact
# rejection paths a real qualification run relies on, not a driftable copy.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1')

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "g4-artifact-validator-$([Guid]::NewGuid().ToString('N'))"
$failures = [System.Collections.Generic.List[string]]::new()
$scenarioCount = 0

function Test-Throws {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][scriptblock] $ScriptBlock,
        [Parameter(Mandatory = $true)][string] $ExpectedMessageFragment
    )

    $script:scenarioCount++
    try {
        & $ScriptBlock
        $script:failures.Add("$Name expected an exception containing '$ExpectedMessageFragment' but none was thrown.")
    }
    catch {
        $message = $_.Exception.Message
        if (-not $message.Contains($ExpectedMessageFragment, [System.StringComparison]::Ordinal)) {
            $script:failures.Add("$Name threw an exception that did not contain '$ExpectedMessageFragment'. Actual: $message")
        }
    }
}

function Test-Succeeds {
    param(
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][scriptblock] $ScriptBlock
    )

    $script:scenarioCount++
    try {
        & $ScriptBlock
    }
    catch {
        $script:failures.Add("$Name was expected to succeed but threw: $($_.Exception.Message)")
    }
}

function New-Nuspec {
    param(
        [Parameter(Mandatory = $true)][string] $Id,
        [Parameter(Mandatory = $true)][string] $Version,
        [switch] $AsSymbolsPackage
    )

    $packageTypeXml = if ($AsSymbolsPackage) {
        '<packageTypes><packageType name="SymbolsPackage" /></packageTypes>'
    }
    else {
        '<packageTypes><packageType name="DotnetTool" /></packageTypes>'
    }

    return @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd">
  <metadata>
    <id>$Id</id>
    <version>$Version</version>
    $packageTypeXml
  </metadata>
</package>
"@
}

function New-ZipArchive {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][hashtable] $Entries
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($Path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $Entries.Keys) {
            $entry = $archive.CreateEntry($entryName)
            $writer = New-Object System.IO.StreamWriter($entry.Open())
            try {
                $writer.Write([string] $Entries[$entryName])
            }
            finally {
                $writer.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
try {
    # --- Assert-CanonicalNuGetArtifact / Get-NuGetPackageRole ---

    $validNupkgPath = Join-Path $temporaryRoot 'valid.nupkg'
    New-ZipArchive -Path $validNupkgPath -Entries @{
        'Fixture.Tool.nuspec' = New-Nuspec -Id 'Fixture.Tool' -Version '1.2.3'
        'tools/net10.0/any/Fixture.Tool.dll' = 'binary-stand-in'
    }
    Test-Succeeds -Name 'Valid canonical nupkg is accepted' -ScriptBlock {
        $role = Assert-CanonicalNuGetArtifact -ArchivePath $validNupkgPath -ExpectedRole 'Package' -ExpectedId 'Fixture.Tool' -ExpectedVersion '1.2.3'
        if ($role.IsSymbolsPackage) { throw 'expected a non-symbols role' }
    }

    $validSnupkgPath = Join-Path $temporaryRoot 'valid.snupkg'
    New-ZipArchive -Path $validSnupkgPath -Entries @{
        'Fixture.Tool.nuspec' = New-Nuspec -Id 'Fixture.Tool' -Version '1.2.3' -AsSymbolsPackage
        'tools/net10.0/any/Fixture.Tool.pdb' = 'symbols-stand-in'
    }
    Test-Succeeds -Name 'Valid canonical snupkg is accepted' -ScriptBlock {
        $role = Assert-CanonicalNuGetArtifact -ArchivePath $validSnupkgPath -ExpectedRole 'Symbols' -ExpectedId 'Fixture.Tool' -ExpectedVersion '1.2.3'
        if (-not $role.IsSymbolsPackage) { throw 'expected a symbols role' }
    }

    $zeroNuspecPath = Join-Path $temporaryRoot 'zero-nuspec.nupkg'
    New-ZipArchive -Path $zeroNuspecPath -Entries @{
        'tools/net10.0/any/Fixture.Tool.dll' = 'binary-stand-in'
    }
    Test-Throws -Name 'Zero-nuspec archive is rejected' -ExpectedMessageFragment 'contains no .nuspec entry' -ScriptBlock {
        Assert-CanonicalNuGetArtifact -ArchivePath $zeroNuspecPath -ExpectedRole 'Package' -ExpectedId 'Fixture.Tool' -ExpectedVersion '1.2.3'
    }

    $multipleNuspecPath = Join-Path $temporaryRoot 'multiple-nuspec.nupkg'
    New-ZipArchive -Path $multipleNuspecPath -Entries @{
        'Fixture.Tool.nuspec' = New-Nuspec -Id 'Fixture.Tool' -Version '1.2.3'
        'nested/Other.Tool.nuspec' = New-Nuspec -Id 'Other.Tool' -Version '9.9.9'
    }
    Test-Throws -Name 'Multiple-nuspec archive is rejected' -ExpectedMessageFragment 'exactly one is required' -ScriptBlock {
        Assert-CanonicalNuGetArtifact -ArchivePath $multipleNuspecPath -ExpectedRole 'Package' -ExpectedId 'Fixture.Tool' -ExpectedVersion '1.2.3'
    }

    Test-Throws -Name 'Symbols package swapped in as primary artifact is rejected' `
        -ExpectedMessageFragment 'swapped in as the primary artifact' -ScriptBlock {
        Assert-CanonicalNuGetArtifact -ArchivePath $validSnupkgPath -ExpectedRole 'Package' -ExpectedId 'Fixture.Tool' -ExpectedVersion '1.2.3'
    }

    Test-Throws -Name 'Primary artifact swapped in as symbols package is rejected' `
        -ExpectedMessageFragment 'swapped in as symbols' -ScriptBlock {
        Assert-CanonicalNuGetArtifact -ArchivePath $validNupkgPath -ExpectedRole 'Symbols' -ExpectedId 'Fixture.Tool' -ExpectedVersion '1.2.3'
    }

    Test-Throws -Name 'Identity mismatch is rejected' -ExpectedMessageFragment 'does not match expected' -ScriptBlock {
        Assert-CanonicalNuGetArtifact -ArchivePath $validNupkgPath -ExpectedRole 'Package' -ExpectedId 'Fixture.Tool' -ExpectedVersion '9.9.9'
    }

    # --- Assert-QualificationEvidenceContent ---

    $negativeEvidencePath = Join-Path $temporaryRoot 'negative.json'
    Set-Content -LiteralPath $negativeEvidencePath -Encoding utf8 -Value (
        '{"status":"failed","outcome":{"exitCode":"UsageOrManifest","phase":"Manifest","category":"Manifest","ruleId":"HXM016"},"diagnostics":[]}'
    )
    Test-Succeeds -Name 'Well-formed negative evidence is accepted' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $negativeEvidencePath -Kind 'Negative' -ExpectedRuleId 'HXM016'
    }
    Test-Throws -Name 'Negative evidence with unexpected ruleId is rejected' -ExpectedMessageFragment "expected 'HXM009'" -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $negativeEvidencePath -Kind 'Negative' -ExpectedRuleId 'HXM009'
    }

    $falselyPassedPath = Join-Path $temporaryRoot 'falsely-passed.json'
    Set-Content -LiteralPath $falselyPassedPath -Encoding utf8 -Value (
        '{"status":"passed","outcome":{"exitCode":"Success","phase":"None","category":"None","ruleId":null},"diagnostics":[]}'
    )
    Test-Throws -Name 'Passed status is rejected for a negative control' -ExpectedMessageFragment 'does not represent a failed negative control' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $falselyPassedPath -Kind 'Negative'
    }
    Test-Throws -Name 'Passed status is rejected for an ExactNonPassing control' -ExpectedMessageFragment 'represents a non-passing control as passing' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $falselyPassedPath -Kind 'ExactNonPassing'
    }

    $completedPath = Join-Path $temporaryRoot 'completed.json'
    Set-Content -LiteralPath $completedPath -Encoding utf8 -Value (
        '{"status":"completed","outcome":{"exitCode":"Success","phase":"None","category":"None","ruleId":null},"diagnostics":[]}'
    )
    Test-Throws -Name 'Completed status is rejected for an ExactNonPassing control' -ExpectedMessageFragment 'represents a non-passing control as passing' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $completedPath -Kind 'ExactNonPassing'
    }
    Test-Succeeds -Name 'Completed status is accepted for a Positive control' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $completedPath -Kind 'Positive'
    }

    $failedPath = Join-Path $temporaryRoot 'failed-for-positive.json'
    Set-Content -LiteralPath $failedPath -Encoding utf8 -Value (
        '{"status":"failed","outcome":{"exitCode":"UsageOrManifest","phase":"Manifest","category":"Manifest","ruleId":"HXM016"},"diagnostics":[]}'
    )
    Test-Throws -Name 'Failed status is rejected for a Positive control' -ExpectedMessageFragment "represents a positive control as failed" -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $failedPath -Kind 'Positive'
    }

    $notJsonPath = Join-Path $temporaryRoot 'not-json.json'
    Set-Content -LiteralPath $notJsonPath -Encoding utf8 -Value 'this is not JSON {{{'
    Test-Throws -Name 'Non-JSON evidence is rejected' -ExpectedMessageFragment 'is not valid JSON' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $notJsonPath -Kind 'Positive'
    }

    $emptyPath = Join-Path $temporaryRoot 'empty.json'
    Set-Content -LiteralPath $emptyPath -Encoding utf8 -Value ''
    Test-Throws -Name 'Empty evidence is rejected' -ExpectedMessageFragment 'is empty' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $emptyPath -Kind 'Positive'
    }

    $noStatusPath = Join-Path $temporaryRoot 'no-status.json'
    Set-Content -LiteralPath $noStatusPath -Encoding utf8 -Value '{"outcome":{"exitCode":"Success"}}'
    Test-Throws -Name 'Evidence missing status is rejected' -ExpectedMessageFragment 'does not declare a status' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $noStatusPath -Kind 'Positive'
    }

    $noRuleIdPath = Join-Path $temporaryRoot 'no-rule-id.json'
    Set-Content -LiteralPath $noRuleIdPath -Encoding utf8 -Value (
        '{"status":"failed","outcome":{"exitCode":"UsageOrManifest","phase":"Manifest","category":"Manifest","ruleId":null},"diagnostics":[]}'
    )
    Test-Throws -Name 'Failed evidence without a causal ruleId is rejected' -ExpectedMessageFragment 'does not carry a causal outcome.ruleId' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $noRuleIdPath -Kind 'Negative'
    }

    $wrongTypedStatusPath = Join-Path $temporaryRoot 'wrong-typed-status.json'
    Set-Content -LiteralPath $wrongTypedStatusPath -Encoding utf8 -Value (
        '{"status":1,"outcome":{"exitCode":"Success","phase":"None","category":"None","ruleId":null},"diagnostics":[]}'
    )
    Test-Throws -Name 'Non-string evidence status is rejected' -ExpectedMessageFragment 'status must be a non-blank JSON string' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $wrongTypedStatusPath -Kind 'Positive'
    }

    $wrongDiagnosticPath = Join-Path $temporaryRoot 'wrong-diagnostic.json'
    Set-Content -LiteralPath $wrongDiagnosticPath -Encoding utf8 -Value (
        '{"status":"failed","outcome":{"exitCode":"UsageOrManifest","phase":"Manifest","category":"Manifest","ruleId":"HXM016"},"diagnostics":[{"message":"missing typed rule"}]}'
    )
    Test-Throws -Name 'Diagnostic without typed rule ID is rejected' -ExpectedMessageFragment 'without a non-blank string ruleId' -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $wrongDiagnosticPath -Kind 'Negative'
    }

    Test-Throws -Name 'Exact typed outcome mismatch is rejected' -ExpectedMessageFragment "outcome.category 'Manifest'; expected 'Unexpected'" -ScriptBlock {
        $null = Assert-QualificationEvidenceContent -FilePath $negativeEvidencePath -Kind 'Negative' `
            -ExpectedRuleId 'HXM016' -ExpectedStatus 'failed' -ExpectedOutcomeExitCode 'UsageOrManifest' `
            -ExpectedPhase 'Manifest' -ExpectedCategory 'Unexpected' -ExpectedDiagnosticRuleIds @()
    }

    # --- Get-SourceTreeState ---

    $cleanRepoRoot = Join-Path $temporaryRoot 'clean-repo'
    New-Item -ItemType Directory -Path $cleanRepoRoot -Force | Out-Null
    & git -C $cleanRepoRoot init --quiet
    & git -C $cleanRepoRoot config user.email 'fixture@local.invalid'
    & git -C $cleanRepoRoot config user.name 'Fixture'
    Set-Content -LiteralPath (Join-Path $cleanRepoRoot 'tracked.txt') -Encoding utf8 -Value 'tracked content'
    & git -C $cleanRepoRoot add -A
    & git -C $cleanRepoRoot commit --quiet -m 'fixture commit'
    Test-Succeeds -Name 'Clean tree reports Clean=true' -ScriptBlock {
        $state = Get-SourceTreeState -RepositoryRoot $cleanRepoRoot
        if (-not $state.Clean) { throw 'expected a clean tree' }
        if ([string]::IsNullOrWhiteSpace($state.Revision)) { throw 'expected a bound revision' }
    }

    Set-Content -LiteralPath (Join-Path $cleanRepoRoot 'tracked.txt') -Encoding utf8 -Value 'modified content'
    Test-Succeeds -Name 'Dirty tree reports Clean=false without throwing' -ScriptBlock {
        $state = Get-SourceTreeState -RepositoryRoot $cleanRepoRoot
        if ($state.Clean) { throw 'expected a dirty tree' }
        if (@($state.Reasons).Count -eq 0) { throw 'expected recorded reasons' }
    }
    & git -C $cleanRepoRoot checkout --quiet -- tracked.txt

    $nonRepoRoot = Join-Path $temporaryRoot 'non-repo'
    New-Item -ItemType Directory -Path $nonRepoRoot -Force | Out-Null
    Test-Throws -Name 'Non-Git directory is rejected' -ExpectedMessageFragment 'requires a Git work tree' -ScriptBlock {
        Get-SourceTreeState -RepositoryRoot $nonRepoRoot
    }

    # --- Assert-TrackedFixtureBytesMatchHead ---

    $fixtureSubdirectory = Join-Path $cleanRepoRoot 'test/fixtures/module'
    New-Item -ItemType Directory -Path $fixtureSubdirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixtureSubdirectory 'sample.json') -Encoding utf8 -Value '{"sample":true}'
    & git -C $cleanRepoRoot add -A
    & git -C $cleanRepoRoot commit --quiet -m 'add fixture'
    $headRevision = (& git -C $cleanRepoRoot rev-parse HEAD).Trim()

    $copiedFixtureDirectory = Join-Path $temporaryRoot 'copied-fixtures'
    New-Item -ItemType Directory -Path $copiedFixtureDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $fixtureSubdirectory 'sample.json') -Destination $copiedFixtureDirectory

    Test-Succeeds -Name 'Byte-identical copied fixture matches HEAD' -ScriptBlock {
        $count = Assert-TrackedFixtureBytesMatchHead -RepositoryRoot $cleanRepoRoot -SourceRevision $headRevision `
            -FixtureDirectory $copiedFixtureDirectory -RepositoryRelativeRoot 'test/fixtures/module'
        if ($count -ne 1) { throw "expected 1 matched file, got $count" }
    }

    Set-Content -LiteralPath (Join-Path $copiedFixtureDirectory 'sample.json') -Encoding utf8 -Value '{"sample":false}'
    Test-Throws -Name 'Edited copied fixture is rejected' -ExpectedMessageFragment 'bytes differ from the bytes tracked at' -ScriptBlock {
        Assert-TrackedFixtureBytesMatchHead -RepositoryRoot $cleanRepoRoot -SourceRevision $headRevision `
            -FixtureDirectory $copiedFixtureDirectory -RepositoryRelativeRoot 'test/fixtures/module'
    }

    $untrackedFixtureDirectory = Join-Path $temporaryRoot 'untracked-fixtures'
    New-Item -ItemType Directory -Path $untrackedFixtureDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $untrackedFixtureDirectory 'never-tracked.json') -Encoding utf8 -Value '{}'
    Test-Throws -Name 'Fixture absent at the bound revision is rejected' -ExpectedMessageFragment 'is not readable at' -ScriptBlock {
        Assert-TrackedFixtureBytesMatchHead -RepositoryRoot $cleanRepoRoot -SourceRevision $headRevision `
            -FixtureDirectory $untrackedFixtureDirectory -RepositoryRelativeRoot 'test/fixtures/module'
    }
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("G-4 tool package artifact validator tests failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine("G-4 tool package artifact validator tests passed: $scenarioCount scenarios.")
