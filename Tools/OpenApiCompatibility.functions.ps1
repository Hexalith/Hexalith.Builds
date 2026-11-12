# Proven OpenAPI dependency pairs. New framework major lines require explicit validation.
function Test-OpenApiPackageCompatibility {
    param([string] $OpenApiVersion, [string] $AspNetCoreOpenApiVersion)

    if ($AspNetCoreOpenApiVersion -match '^10\.') {
        return $OpenApiVersion -match '^2\.'
    }
    if ($AspNetCoreOpenApiVersion -match '^11\.' -and $OpenApiVersion -match '^3\.(\d+)\.') {
        return [int]$Matches[1] -ge 11
    }
    return $false
}
