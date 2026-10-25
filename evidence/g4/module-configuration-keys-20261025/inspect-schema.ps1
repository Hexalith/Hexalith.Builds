param([string] $AssemblyPath, [string] $OutputPath)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
$stream = $assembly.GetManifestResourceStream('Hexalith.Builds.Tooling.Manifest.hexalith.module-manifest.v2.json')
if ($null -eq $stream) { throw 'Embedded module manifest schema is missing.' }
$outputStream = [IO.File]::Open($OutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try { $stream.CopyTo($outputStream) }
finally { $outputStream.Dispose(); $stream.Dispose() }
[ordered]@{ assembly = $AssemblyPath; version = $assembly.GetCustomAttributesData().Where({ $_.AttributeType.Name -eq 'AssemblyInformationalVersionAttribute' }).ConstructorArguments.Value; resource = 'Hexalith.Builds.Tooling.Manifest.hexalith.module-manifest.v2.json'; output = $OutputPath; sha256 = (Get-FileHash $OutputPath -Algorithm SHA256).Hash; configurationKey = (Get-Content $OutputPath -Raw | ConvertFrom-Json).'$defs'.configurationKey } | ConvertTo-Json -Depth 5
