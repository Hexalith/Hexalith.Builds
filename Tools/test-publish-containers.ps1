[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testDirectory = Join-Path $PSScriptRoot '../Github/publish-containers/tests'
& python3 -m unittest discover -s $testDirectory -p 'test_*.py' -v
if ($LASTEXITCODE -ne 0) {
    throw "Container publisher fixture tests failed with exit code $LASTEXITCODE."
}

[Console]::Out.WriteLine('Container publisher fixture tests passed.')
