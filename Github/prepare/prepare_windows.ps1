[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Validate', 'Build')]
    [string]$Phase
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments, [switch]$Capture)
    if ($Capture) {
        $result = & $Executable @Arguments
    }
    else {
        & $Executable @Arguments | Out-Host
    }
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    if ($Capture) {
        return $result
    }
}

function Resolve-OwnedPath {
    param([string]$Value, [string]$Kind)
    if ([string]::IsNullOrEmpty($Value) -or $Value.StartsWith('/') -or
        $Value.Contains('\') -or $Value.Contains('${{') -or $Value -match '^[A-Za-z]:' -or
        $Value -match '[\x00-\x1f\x7f-\x9f]' -or
        @($Value.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
        throw "Unsafe $Kind path: $Value"
    }
    $candidate = $workspace
    foreach ($part in $Value.Split('/')) {
        $candidate = Join-Path $candidate $part
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -Force -LiteralPath $candidate
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "$Kind path contains a symbolic link or junction: $Value"
            }
        }
    }
    return [IO.Path]::GetFullPath($candidate)
}

try {
    if ($env:CI_PREPARE_SOLUTION -eq 'none') {
        exit 0
    }
    $workspace = (Get-Item -Force -LiteralPath $env:GITHUB_WORKSPACE).FullName
    $solution = Resolve-OwnedPath $env:CI_PREPARE_SOLUTION 'solution'
    $globalJson = Resolve-OwnedPath $env:CI_PREPARE_GLOBAL_JSON 'global-json'
    if (-not (Test-Path -LiteralPath $solution -PathType Leaf) -or
        [IO.Path]::GetExtension($solution) -cne '.slnx') {
        throw 'Solution must be an existing .slnx file'
    }
    if (-not (Test-Path -LiteralPath $globalJson -PathType Leaf) -or
        [IO.Path]::GetFileName($globalJson) -cne 'global.json') {
        throw 'SDK path must be an existing global.json file'
    }
    # The module SDK directory, never the possibly nested solution directory,
    # controls SDK selection and the working directory for both product commands.
    $moduleRoot = Split-Path -Parent $globalJson
    $relativeSolution = [IO.Path]::GetRelativePath($moduleRoot, $solution)
    if ($relativeSolution -eq '..' -or $relativeSolution.StartsWith("..$([IO.Path]::DirectorySeparatorChar)") -or
        [IO.Path]::IsPathRooted($relativeSolution)) {
        throw 'Solution escapes the module SDK directory'
    }
    $submodules = @()
    if (-not [string]::IsNullOrEmpty($env:CI_PREPARE_SUBMODULES)) {
        $submodules = @($env:CI_PREPARE_SUBMODULES.Split("`n"))
    }
    if (@($submodules | Select-Object -Unique -CaseInsensitive).Count -ne $submodules.Count) {
        throw 'Duplicate direct submodule paths'
    }
    Push-Location -LiteralPath $workspace
    try {
        if ($submodules.Count -gt 0) {
            # Use NUL records so quoted Git names and whitespace remain literal.
            $records = (Invoke-Checked git @('config', '-z', '--file', '.gitmodules', '--get-regexp', '^submodule\..*\.path$') -Capture) -join "`n"
            $declared = @($records.Split([char]0) | Where-Object { $_ -ne '' } | ForEach-Object { $_.Substring($_.IndexOf("`n") + 1) })
            if (@($declared | Select-Object -Unique).Count -ne $declared.Count) {
                throw 'Duplicate paths in root .gitmodules'
            }
            foreach ($path in $submodules) {
                $null = Resolve-OwnedPath $path 'submodule'
                if ($path -cnotin $declared) {
                    throw "Submodule is not declared in root .gitmodules: $path"
                }
                $entry = (Invoke-Checked git @('--literal-pathspecs', 'ls-files', '-z', '--stage', '--', $path) -Capture) -join "`n"
                $entries = @($entry.Split([char]0) | Where-Object { $_ -ne '' })
                if ($entries.Count -ne 1 -or -not $entries[0].StartsWith('160000 ') -or
                    $entries[0].Substring($entries[0].IndexOf("`t") + 1) -cne $path) {
                    throw "Declared submodule is not a direct root gitlink: $path"
                }
            }
            if ($Phase -eq 'Build') {
                Invoke-Checked git (@('--literal-pathspecs', '-c', 'protocol.file.allow=never', '-c', 'submodule.recurse=false',
                    'submodule', 'update', '--init', '--checkout', '--') + $submodules)
            }
        }
    }
    finally {
        Pop-Location
    }
    if ($Phase -eq 'Validate') {
        exit 0
    }
    $nugetConfig = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../NuGet.ci.Config'))
    $flags = @('-noAutoResponse', '-p:Configuration=Release', '-warnaserror',
        '-p:UseHexalithProjectReferences=false', '-p:NuGetAudit=false', "-p:RestoreConfigFile=$nugetConfig",
        '-p:EnableNETAnalyzers=true', '-p:RunAnalyzers=true', '-p:RunAnalyzersDuringBuild=true')
    $env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
    $env:DOTNET_NOLOGO = '1'
    Push-Location -LiteralPath $moduleRoot
    try {
        Invoke-Checked dotnet (@('restore', $relativeSolution) + $flags)
        Invoke-Checked dotnet (@('build', $relativeSolution, '--no-restore') + $flags)
    }
    finally {
        Pop-Location
    }
    exit 0
}
catch {
    Write-Error $_
    exit 1
}
