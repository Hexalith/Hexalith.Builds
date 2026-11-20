# Run from the consuming repository root. The Git index supplies the selected revision.
$ErrorActionPreference = 'Stop'
$path = 'references/Hexalith.Builds'

$prefix = git rev-parse --show-prefix
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($prefix) { throw 'Run from the root of the consuming repository.' }

$entry = git ls-files --stage -- $path
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (@($entry).Count -ne 1 -or $entry -cnotmatch '^160000 ([0-9a-f]+) 0\treferences/Hexalith\.Builds$') {
    throw "Expected one recorded gitlink at $path. For first-time adoption, see the repository README."
}
$revision = $Matches[1]
if (Test-Path -LiteralPath "$path/.git") {
    $changes = git -C $path status --porcelain --ignore-submodules=none
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if ($changes) { throw "Local changes in $path must be resolved before initialization." }
}

git -c submodule.recurse=false submodule update --init --checkout -- $path
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$actual = git -C $path rev-parse HEAD
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($actual -cne $revision) { throw "Checkout of $path did not match the recorded gitlink." }
