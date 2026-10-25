param([Parameter(Mandatory)][string] $AssemblyPath, [Parameter(Mandatory)][string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
$null = [IO.Directory]::CreateDirectory($OutputDirectory)
$resources = @()
foreach ($name in @($assembly.GetManifestResourceNames() | Sort-Object)) {
    if (-not $name.EndsWith('.json', [StringComparison]::Ordinal)) { continue }
    $path = Join-Path $OutputDirectory $name
    $stream = $assembly.GetManifestResourceStream($name)
    if ($null -eq $stream) { throw "Missing embedded resource $name" }
    $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $stream.CopyTo($output) } finally { $output.Dispose(); $stream.Dispose() }
    $resources += [ordered]@{ name = $name; file = $name; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); sizeBytes = (Get-Item -LiteralPath $path).Length }
}
$informationalVersion = @($assembly.GetCustomAttributesData() | Where-Object { $_.AttributeType.Name -ceq 'AssemblyInformationalVersionAttribute' })[0].ConstructorArguments[0].Value
[ordered]@{ assembly = $AssemblyPath; informationalVersion = $informationalVersion; resources = $resources } | ConvertTo-Json -Depth 6
