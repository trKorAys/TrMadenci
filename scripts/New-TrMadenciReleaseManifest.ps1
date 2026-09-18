[CmdletBinding(SupportsShouldProcess = $true)]
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

$files = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object {
    -not $_.FullName.Equals($manifestFullPath, [System.StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object {
    $relativePath = $_.FullName.Substring($packageDirectory.Length).TrimStart('\', '/')
    [pscustomobject]@{
        path = $relativePath.Replace('\', '/')
        length = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
} | Sort-Object path)

if ($files.Count -eq 0) {
    throw "The package does not contain any files: $packageDirectory"
}

$manifest = [ordered]@{
    schemaVersion = 1
    product = 'TrMadenci'
    files = $files
}
$json = $manifest | ConvertTo-Json -Depth 4

if ($PSCmdlet.ShouldProcess($manifestFullPath, "Write release manifest for $($files.Count) files")) {
    $parentDirectory = Split-Path -Parent $manifestFullPath
    if (-not (Test-Path -LiteralPath $parentDirectory -PathType Container)) {
        throw "Manifest parent directory does not exist: $parentDirectory"
    }
    Set-Content -LiteralPath $manifestFullPath -Value $json -Encoding UTF8
    Write-Host "Wrote release manifest for $($files.Count) files: $manifestFullPath"
}
