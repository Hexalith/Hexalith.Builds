# G-4 tool package qualification/publication helper functions.
#
# Dot-sourced by both the official qualification gate
# (test-g4-tool-package-contracts.ps1) and its isolated regression suite
# (test-g4-tool-package-artifact-validator.ps1) so the exact same logic that
# gates a real candidate is what the regression tests exercise -- never a
# duplicated, driftable copy.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Save-QualificationEvidence {
    # Persists a control's raw output and returns its recorded hash/size. Coverage is
    # only "satisfied" once Assert-QualificationEvidenceContent has independently
    # parsed and validated what was written here -- hash, size, and filename alone
    # never suffice.
    param(
        [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Content
    )

    $fileName = "$Name.json"
    $filePath = Join-Path $EvidenceDirectory $fileName
    [System.IO.File]::WriteAllText($filePath, $Content, [Text.UTF8Encoding]::new($false))
    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    return [ordered] @{
        file = "qualification-evidence/$fileName"
        sha256 = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        sizeBytes = $bytes.Length
    }
}

function Assert-QualificationEvidenceContent {
    # Parses and validates the CONTENT of a captured qualification-evidence artifact
    # against the real hexalith.module-run/evidence-cli `--output json` shape
    # (top-level "status"; the causal rule lives at "outcome.ruleId", not at the
    # document root, and "diagnostics[]" can be non-empty even on success). A
    # hash/size/filename triple proves an artifact was retained unmodified; it
    # proves nothing about whether the artifact actually contains a coherent,
    # rule-bearing result. This is the content-parsing leg that coverage requires.
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][ValidateSet('Negative', 'Positive', 'ExactNonPassing')][string] $Kind,
        [string] $ExpectedStatus = '',
        [string] $ExpectedOutcomeExitCode = '',
        [string] $ExpectedPhase = '',
        [string] $ExpectedCategory = '',
        [string] $ExpectedRuleId = '',
        [switch] $RequireNullRuleId,
        [AllowEmptyCollection()][string[]] $ExpectedDiagnosticRuleIds
    )

    $text = [System.IO.File]::ReadAllText($FilePath)
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "Qualification evidence '$FilePath' is empty; content cannot be validated."
    }

    try {
        $document = $text | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Qualification evidence '$FilePath' is not valid JSON. $($_.Exception.GetBaseException().Message)"
    }

    if ($document.PSObject.Properties.Name -notcontains 'status') {
        throw "Qualification evidence '$FilePath' does not declare a status."
    }
    if ($document.status -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string] $document.status)) {
        throw "Qualification evidence '$FilePath' status must be a non-blank JSON string."
    }

    if ($document.PSObject.Properties.Name -notcontains 'outcome' -or $null -eq $document.outcome) {
        throw "Qualification evidence '$FilePath' does not declare an outcome."
    }

    foreach ($outcomeField in @('exitCode', 'phase', 'category', 'ruleId')) {
        if ($document.outcome.PSObject.Properties.Name -notcontains $outcomeField) {
            throw "Qualification evidence '$FilePath' outcome does not declare '$outcomeField'."
        }
    }

    if ($document.outcome.exitCode -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string] $document.outcome.exitCode)) {
        throw "Qualification evidence '$FilePath' outcome.exitCode must be a non-blank JSON string."
    }
    if ($document.outcome.phase -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string] $document.outcome.phase)) {
        throw "Qualification evidence '$FilePath' outcome.phase must be a non-blank JSON string."
    }
    if ($document.outcome.category -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string] $document.outcome.category)) {
        throw "Qualification evidence '$FilePath' outcome.category must be a non-blank JSON string."
    }
    if ($null -ne $document.outcome.ruleId -and
        ($document.outcome.ruleId -isnot [string] -or
            [string]::IsNullOrWhiteSpace([string] $document.outcome.ruleId))) {
        throw "Qualification evidence '$FilePath' outcome.ruleId must be null or a non-blank JSON string."
    }
    if ($document.PSObject.Properties.Name -notcontains 'diagnostics' -or $null -eq $document.diagnostics) {
        throw "Qualification evidence '$FilePath' does not declare diagnostics."
    }

    $status = [string] $document.status
    $outcomeExitCode = [string] $document.outcome.exitCode
    $phase = [string] $document.outcome.phase
    $category = [string] $document.outcome.category
    $ruleId = if ($null -eq $document.outcome.ruleId) { $null } else { [string] $document.outcome.ruleId }
    $diagnosticRuleIds = @(
        foreach ($diagnostic in @($document.diagnostics)) {
            if ($null -eq $diagnostic -or
                $diagnostic.PSObject.Properties.Name -notcontains 'ruleId' -or
                $diagnostic.ruleId -isnot [string] -or
                [string]::IsNullOrWhiteSpace([string] $diagnostic.ruleId)) {
                throw "Qualification evidence '$FilePath' contains a diagnostic without a non-blank string ruleId."
            }

            [string] $diagnostic.ruleId
        }
    )

    switch ($Kind) {
        'Negative' {
            if ($status -cne 'failed') {
                throw "Qualification evidence '$FilePath' status '$status' does not represent a failed negative control."
            }
            if ($outcomeExitCode -ceq 'Success') {
                throw "Qualification evidence '$FilePath' reports a successful outcome for a failed negative control."
            }
            if ([string]::IsNullOrWhiteSpace([string] $ruleId)) {
                throw "Qualification evidence '$FilePath' does not carry a causal outcome.ruleId for a negative control."
            }
            if (-not [string]::IsNullOrWhiteSpace($ExpectedRuleId) -and $ruleId -cne $ExpectedRuleId) {
                throw "Qualification evidence '$FilePath' reports outcome.ruleId '$ruleId'; expected '$ExpectedRuleId'."
            }
        }
        'ExactNonPassing' {
            if ($status -cne 'unavailable') {
                throw "Qualification evidence '$FilePath' status '$status' represents a non-passing control as passing."
            }
            if ($outcomeExitCode -ceq 'Success' -or [string]::IsNullOrWhiteSpace([string] $ruleId)) {
                throw "Qualification evidence '$FilePath' does not carry a non-success outcome and causal rule for a non-passing control."
            }
            if (-not [string]::IsNullOrWhiteSpace($ExpectedRuleId) -and $ruleId -cne $ExpectedRuleId) {
                throw "Qualification evidence '$FilePath' reports outcome.ruleId '$ruleId'; expected '$ExpectedRuleId'."
            }
        }
        'Positive' {
            if ($status -cnotin @('passed', 'completed')) {
                throw "Qualification evidence '$FilePath' status '$status' represents a positive control as failed."
            }
            if ($outcomeExitCode -cne 'Success' -or $null -ne $ruleId) {
                throw "Qualification evidence '$FilePath' does not carry the canonical successful outcome with a null ruleId."
            }
        }
    }

    foreach ($exactValue in @(
            [pscustomobject] @{ Name = 'status'; Actual = $status; Expected = $ExpectedStatus },
            [pscustomobject] @{ Name = 'outcome.exitCode'; Actual = $outcomeExitCode; Expected = $ExpectedOutcomeExitCode },
            [pscustomobject] @{ Name = 'outcome.phase'; Actual = $phase; Expected = $ExpectedPhase },
            [pscustomobject] @{ Name = 'outcome.category'; Actual = $category; Expected = $ExpectedCategory }
        )) {
        if (-not [string]::IsNullOrWhiteSpace($exactValue.Expected) -and
            $exactValue.Actual -cne $exactValue.Expected) {
            throw "Qualification evidence '$FilePath' reports $($exactValue.Name) '$($exactValue.Actual)'; expected '$($exactValue.Expected)'."
        }
    }

    if ($RequireNullRuleId -and $null -ne $ruleId) {
        throw "Qualification evidence '$FilePath' reports outcome.ruleId '$ruleId'; expected null."
    }

    if ($PSBoundParameters.ContainsKey('ExpectedDiagnosticRuleIds') -and
        [string]::Join('|', $diagnosticRuleIds) -cne [string]::Join('|', @($ExpectedDiagnosticRuleIds))) {
        throw "Qualification evidence '$FilePath' reports diagnostic rule IDs '$($diagnosticRuleIds -join ', ')'; expected '$(@($ExpectedDiagnosticRuleIds) -join ', ')'."
    }

    return $document
}

function Assert-ModuleRunEvidenceContent {
    # Validates a retained hexalith.module-run-evidence.v1 artifact at publication
    # time. The publisher supplies the exact expected invocation, source revision,
    # tool build identity, manifest hash, and typed outcome; merely retaining JSON
    # with the right filename cannot satisfy this contract.
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string] $ExpectedFinalStatus,
        [Parameter(Mandatory = $true)][long] $ExpectedExitCode,
        [AllowNull()][object] $ExpectedRuleId,
        [Parameter(Mandatory = $true)][string] $ExpectedPhase,
        [Parameter(Mandatory = $true)][string] $ExpectedCategory,
        [Parameter(Mandatory = $true)][string] $ExpectedCommand,
        [Parameter(Mandatory = $true)][string] $ExpectedToolVersion,
        [Parameter(Mandatory = $true)][string] $ExpectedRepositoryRevision,
        [Parameter(Mandatory = $true)][string] $ExpectedRepositoryDirtyMarker,
        [Parameter(Mandatory = $true)][string] $ExpectedManifestHash,
        [string] $ExpectedEventStoreVersion = '3.90.0'
    )

    $bytes = [IO.File]::ReadAllBytes($FilePath)
    if ($bytes.Length -eq 0 -or $bytes[$bytes.Length - 1] -ne [byte][char]"`n") {
        throw "Module-run evidence '$FilePath' must be non-empty canonical JSON with a final newline."
    }

    try {
        $evidence = [IO.File]::ReadAllText($FilePath) | ConvertFrom-Json -DateKind String -ErrorAction Stop
    }
    catch {
        throw "Module-run evidence '$FilePath' is not valid JSON. $($_.Exception.GetBaseException().Message)"
    }

    $numericExitCode = $evidence.outcome.exitCode -is [int] -or
        $evidence.outcome.exitCode -is [long] -or
        $evidence.outcome.exitCode -is [decimal]
    $hasExactRuleId = if ($null -eq $ExpectedRuleId) {
        $null -eq $evidence.outcome.ruleId
    }
    else {
        $evidence.outcome.ruleId -is [string] -and $evidence.outcome.ruleId -ceq [string] $ExpectedRuleId
    }

    if ($evidence.schema -isnot [string] -or $evidence.schema -cne 'hexalith.module-run-evidence.v1' -or
        $evidence.finalStatus -isnot [string] -or $evidence.finalStatus -cne $ExpectedFinalStatus -or
        -not $numericExitCode -or [long] $evidence.outcome.exitCode -ne $ExpectedExitCode -or
        $evidence.outcome.phase -isnot [string] -or $evidence.outcome.phase -cne $ExpectedPhase -or
        $evidence.outcome.category -isnot [string] -or $evidence.outcome.category -cne $ExpectedCategory -or
        -not $hasExactRuleId -or
        $evidence.invocation.command -isnot [string] -or $evidence.invocation.command -cne $ExpectedCommand -or
        $evidence.invocation.manifestHash -isnot [string] -or $evidence.invocation.manifestHash -cne $ExpectedManifestHash -or
        $evidence.environment.toolVersion -isnot [string] -or $evidence.environment.toolVersion -cne $ExpectedToolVersion -or
        $evidence.environment.repositoryRevision -isnot [string] -or $evidence.environment.repositoryRevision -cne $ExpectedRepositoryRevision -or
        $evidence.environment.repositoryDirtyMarker -isnot [string] -or $evidence.environment.repositoryDirtyMarker -cne $ExpectedRepositoryDirtyMarker -or
        $evidence.topology.platform.eventStoreVersion -isnot [string] -or
        $evidence.topology.platform.eventStoreVersion -cne $ExpectedEventStoreVersion) {
        throw "Module-run evidence '$FilePath' does not match its exact typed outcome, invocation, manifest, tool, source-revision, dirty-marker, and platform binding."
    }

    return $evidence
}

function Assert-QualificationLogContent {
    # The log is the one required non-JSON qualification artifact. Parse it as an
    # ordered command transcript and require every packaged positive/control lane,
    # while retaining the existing redaction contract.
    param([Parameter(Mandatory = $true)][string] $FilePath)

    $content = [IO.File]::ReadAllText($FilePath)
    if ([string]::IsNullOrWhiteSpace($content)) {
        throw "Qualification log '$FilePath' is empty."
    }
    if ($content.Contains('packaged-redaction-control', [StringComparison]::Ordinal) -or
        $content.Contains('Bearer', [StringComparison]::Ordinal)) {
        throw "Qualification log '$FilePath' contains unredacted filter or credential material."
    }

    foreach ($requiredCommand in @(
            'dotnet tool run hexalith-module -- down',
            'dotnet tool run hexalith-module -- test',
            'dotnet tool run hexalith-module -- run',
            'dotnet tool run hexalith-evidence -- validate'
        )) {
        if (-not $content.Contains($requiredCommand, [StringComparison]::Ordinal)) {
            throw "Qualification log '$FilePath' does not contain required command '$requiredCommand'."
        }
    }

    return $content
}

function Get-SourceTreeState {
    # Reports whether the qualified source tree is clean and binds the exact
    # revision the candidate was qualified against. A dirty tree does not abort the
    # run -- an external/untracked candidate may still exercise every control -- but
    # it does mean releaseEligible can never become true for this run, because its
    # tracked-byte proofs could not be reproduced from a fresh clone at the same
    # revision. Only a Git environment that cannot answer the question at all (no
    # work tree, `git status`/`git rev-parse` failing outright) is a hard error.
    param([Parameter(Mandatory = $true)][string] $RepositoryRoot)

    & git -C $RepositoryRoot rev-parse --is-inside-work-tree *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Source revision binding requires a Git work tree; none was found.'
    }

    $status = @(& git -C $RepositoryRoot status --porcelain=v1 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git status failed with exit code $LASTEXITCODE."
    }

    $statusLines = @($status | Where-Object { -not [string]::IsNullOrWhiteSpace([string] $_) })

    $revisionOutput = @(& git -C $RepositoryRoot rev-parse HEAD 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git rev-parse HEAD failed with exit code $LASTEXITCODE."
    }

    return [pscustomobject] @{
        Clean = $statusLines.Count -eq 0
        Revision = ([string] $revisionOutput[0]).Trim()
        Reasons = @($statusLines)
    }
}

function Assert-TrackedFixtureBytesMatchHead {
    # Proves every fixture byte the packaged consumer exercised is identical to what
    # HEAD tracks, not merely that the path is tracked (Assert-FixturesTracked only
    # rules out untracked/ignored files, which misses a tracked-but-locally-edited
    # fixture). Publication fails closed on any drift between tracked bytes and the
    # bytes actually used to qualify the candidate.
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][string] $SourceRevision,
        [Parameter(Mandatory = $true)][string] $FixtureDirectory,
        [Parameter(Mandatory = $true)][string] $RepositoryRelativeRoot
    )

    $mismatches = [System.Collections.Generic.List[string]]::new()
    $files = @(Get-ChildItem -LiteralPath $FixtureDirectory -File -Recurse | Sort-Object -Property FullName)
    foreach ($file in $files) {
        $relativePath = ([IO.Path]::GetRelativePath($FixtureDirectory, $file.FullName)).Replace('\', '/')
        $trackedPath = "$RepositoryRelativeRoot/$relativePath"

        # `git cat-file blob` streams the exact tracked bytes with no text-mode
        # normalization, unlike capturing `git show` output through PowerShell's
        # line-oriented pipeline; redirect straight to a file so nothing is decoded
        # and re-encoded along the way.
        $catFileOutput = New-TemporaryFile
        try {
            & git -C $RepositoryRoot cat-file blob "${SourceRevision}:${trackedPath}" 2>$null 1> $catFileOutput.FullName
            if ($LASTEXITCODE -ne 0) {
                $mismatches.Add("'$trackedPath' is not readable at $SourceRevision (not tracked, renamed, or removed).")
                continue
            }

            $trackedBytes = [System.IO.File]::ReadAllBytes($catFileOutput.FullName)
        }
        finally {
            Remove-Item -LiteralPath $catFileOutput.FullName -Force -ErrorAction SilentlyContinue
        }

        $workingBytes = [System.IO.File]::ReadAllBytes($file.FullName)
        if (-not [System.Linq.Enumerable]::SequenceEqual([byte[]] $trackedBytes, [byte[]] $workingBytes)) {
            $mismatches.Add("'$trackedPath' working-tree bytes differ from the bytes tracked at $SourceRevision.")
        }
    }

    if ($mismatches.Count -gt 0) {
        throw "Tracked-fixture-vs-HEAD proof failed: $([string]::Join('; ', $mismatches))"
    }

    return $files.Count
}

function Get-NuGetPackageRole {
    # Opens a .nupkg/.snupkg archive and determines its canonical identity and role
    # entirely from its nuspec content and payload -- never from its file name or
    # extension -- so a swapped or relabeled artifact cannot pass by sharing a
    # plausible name.
    param([Parameter(Mandatory = $true)][string] $ArchivePath)

    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $nuspecEntries = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)[^/]+\.nuspec$' })
        if ($nuspecEntries.Count -eq 0) {
            throw "'$ArchivePath' contains no .nuspec entry; a NuGet package must contain exactly one."
        }

        if ($nuspecEntries.Count -gt 1) {
            $names = [string]::Join(', ', @($nuspecEntries | ForEach-Object { $_.FullName }))
            throw "'$ArchivePath' contains $($nuspecEntries.Count) .nuspec entries ($names); exactly one is required."
        }

        $entryStream = $nuspecEntries[0].Open()
        try {
            $reader = New-Object System.IO.StreamReader($entryStream)
            $nuspecText = $reader.ReadToEnd()
        }
        finally {
            $entryStream.Dispose()
        }

        try {
            [xml] $nuspecXml = $nuspecText
        }
        catch {
            throw "'$ArchivePath' nuspec entry is not valid XML. $($_.Exception.GetBaseException().Message)"
        }

        $metadata = $nuspecXml.package.metadata
        $id = [string] $metadata.id
        $version = [string] $metadata.version
        if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) {
            throw "'$ArchivePath' nuspec is missing a package id or version."
        }

        $packageTypeNames = @()
        if ($null -ne $metadata.packageTypes) {
            $packageTypeNames = @($metadata.packageTypes.packageType | ForEach-Object { [string] $_.name })
        }

        return [pscustomobject] @{
            Id = $id
            Version = $version
            IsSymbolsPackage = $packageTypeNames -contains 'SymbolsPackage'
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-CanonicalNuGetArtifact {
    # Rejects a swapped or canonical-role-invalid artifact: the primary .nupkg must
    # not be a symbols package and the .snupkg must declare the SymbolsPackage
    # nuspec type, and both must match the expected package identity exactly.
    param(
        [Parameter(Mandatory = $true)][string] $ArchivePath,
        [Parameter(Mandatory = $true)][ValidateSet('Package', 'Symbols')][string] $ExpectedRole,
        [Parameter(Mandatory = $true)][string] $ExpectedId,
        [Parameter(Mandatory = $true)][string] $ExpectedVersion
    )

    $role = Get-NuGetPackageRole -ArchivePath $ArchivePath
    if ($role.Id -cne $ExpectedId -or $role.Version -cne $ExpectedVersion) {
        throw "'$ArchivePath' nuspec identity '$($role.Id) $($role.Version)' does not match expected '$ExpectedId $ExpectedVersion'."
    }

    if ($ExpectedRole -eq 'Package' -and $role.IsSymbolsPackage) {
        throw "'$ArchivePath' declares the SymbolsPackage nuspec type; a symbols package was swapped in as the primary artifact."
    }

    if ($ExpectedRole -eq 'Symbols' -and -not $role.IsSymbolsPackage) {
        throw "'$ArchivePath' does not declare the SymbolsPackage nuspec type; the primary artifact was swapped in as symbols."
    }

    return $role
}
