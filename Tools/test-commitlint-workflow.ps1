[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testDirectory = Join-Path $PSScriptRoot '../Github/commitlint/tests'
& python3 -m unittest discover -s $testDirectory -p 'test_*.py' -v
if ($LASTEXITCODE -ne 0) {
    throw "Commitlint workflow tests failed with exit code $LASTEXITCODE."
}

[Console]::Out.WriteLine('Commitlint workflow tests passed.')
