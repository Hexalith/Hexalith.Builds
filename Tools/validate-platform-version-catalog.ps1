[CmdletBinding()]
param(
    [string] $CatalogPath = (Join-Path $PSScriptRoot '../Props/Directory.Packages.props'),
    [string] $NuspecPath = '',
    [string] $Configuration = 'Debug',
    [string] $TargetFramework = 'net10.0',
    [ValidateRange(1, 300)]
    [int] $TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$outputPath = Join-Path ([IO.Path]::GetTempPath()) "hexalith-platform-catalog-$([Guid]::NewGuid().ToString('N')).json"
$global:LASTEXITCODE = 0
$exitCode = 0
try {
    & (Join-Path $PSScriptRoot 'write-platform-version-catalog.ps1') -CatalogPath $CatalogPath -OutputPath $outputPath -NuspecPath $NuspecPath -Configuration $Configuration -TargetFramework $TargetFramework -TimeoutSeconds $TimeoutSeconds
    $exitCode = $LASTEXITCODE
}
finally {
    if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Force }
}

exit $exitCode
