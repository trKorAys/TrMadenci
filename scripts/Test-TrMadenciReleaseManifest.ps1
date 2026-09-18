[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PackagePath,

    [string] $ManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageDirectory = (Resolve-Path -LiteralPath $PackagePath).Path
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $packageDirectory 'TrMadenci.ReleaseManifest.json'
} elseif (-not [System.IO.Path]::IsPathRooted($ManifestPath)) {
    $ManifestPath = Join-Path (Get-Location).Path $ManifestPath
}
$manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) {
    throw "Release manifest was not found: $manifestFullPath"
}

$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'TrMadenci') {
    throw 'The release manifest schema or product identifier is invalid.'
}

$expected = @{}
foreach ($entry in @($manifest.files)) {
    $relativePath = [string]$entry.path
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        [System.IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.Split(@('/', '\')).Contains('..')) {
        throw "Unsafe path in release manifest: $relativePath"
    }
    $normalizedPath = $relativePath.Replace('/', '\')
    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $packageDirectory $normalizedPath))
    $packagePrefix = $packageDirectory.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($packagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Manifest path escapes the package directory: $relativePath"
    }
    if ($expected.ContainsKey($relativePath)) {
        throw "Duplicate path in release manifest: $relativePath"
    }
    $expected[$relativePath] = $entry
}

$actual = @{}
foreach ($file in @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object {
    -not $_.FullName.Equals($manifestFullPath, [System.StringComparison]::OrdinalIgnoreCase)
})) {
    $relativePath = $file.FullName.Substring($packageDirectory.Length).TrimStart('\', '/').Replace('\', '/')
    $actual[$relativePath] = $file
}

$missing = @($expected.Keys | Where-Object { -not $actual.ContainsKey($_) } | Sort-Object)
$unexpected = @($actual.Keys | Where-Object { -not $expected.ContainsKey($_) } | Sort-Object)
if ($missing.Count -gt 0) {
    throw "Package files are missing: $($missing -join ', ')"
}
if ($unexpected.Count -gt 0) {
    throw "Package contains files absent from the manifest: $($unexpected -join ', ')"
}

foreach ($relativePath in @($expected.Keys | Sort-Object)) {
    $entry = $expected[$relativePath]
    $file = $actual[$relativePath]
    if ([long]$entry.length -ne $file.Length) {
        throw "File length mismatch for $relativePath. Expected $($entry.length), found $($file.Length)."
    }
    $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if (-not $actualHash.Equals([string]$entry.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 mismatch for $relativePath."
    }
}

Write-Host "Release manifest verified: $($actual.Count) files, no missing, changed, or unexpected files."
