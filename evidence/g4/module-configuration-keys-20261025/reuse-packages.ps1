param([string] $Version, [string] $OutputDirectory, [switch] $DeferInventory)
$ErrorActionPreference = 'Stop'
if ($Version -cne '0.0.0-app-api-token.20261025') { throw 'Unexpected local candidate version.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory
Get-ChildItem -LiteralPath '/tmp/module-cli-compat-88cl840t/packages' -File | Where-Object { $_.Extension -in @('.nupkg', '.snupkg') } | Copy-Item -Destination $OutputDirectory
