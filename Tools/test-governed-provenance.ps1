[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testDirectory = Join-Path $PSScriptRoot '../Github/governed-provenance/tests'
& python3 -m unittest discover -s $testDirectory -p 'test_*.py' -v
if ($LASTEXITCODE -ne 0) {
    throw "Governed workflow provenance tests failed with exit code $LASTEXITCODE."
}

[Console]::Out.WriteLine('Governed workflow provenance tests passed.')
