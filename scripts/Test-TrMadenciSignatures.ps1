[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PackagePath,

    [ValidatePattern('^[0-9A-Fa-f ]{40,59}$')]
    [string] $ExpectedThumbprint
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageDirectory = (Resolve-Path -LiteralPath $PackagePath).Path
$normalizedExpected = if ([string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
    $null
} else {
    $ExpectedThumbprint.Replace(' ', '').ToUpperInvariant()
}
if ($null -ne $normalizedExpected -and $normalizedExpected -notmatch '^[0-9A-F]{40}$') {
    throw 'ExpectedThumbprint must contain exactly 40 hexadecimal characters.'
}
$requiredTargets = @(
    'TrMadenci.Service.exe',
    'TrMadenci.Service.dll',
    'TrMadenci.Core.dll',
    'TrMadenci.Native.dll',
    'TrMadenci.NativeBridge.dll',
    'TrMadenci.Protocols.dll'
)

$missingTargets = @($requiredTargets | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $packageDirectory $_) -PathType Leaf)
})
if ($missingTargets.Count -gt 0) {
    throw "Required signed targets were not found: $($missingTargets -join ', ')"
}

$publisherTargets = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object {
    $_.BaseName.StartsWith('TrMadenci.', [System.StringComparison]::OrdinalIgnoreCase) -and
    $_.Extension -in @('.exe', '.dll')
} | Sort-Object FullName)
$packageBinaries = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object {
    $_.Extension -in @('.exe', '.dll')
} | Sort-Object FullName)

$results = foreach ($targetFile in $publisherTargets) {
    $target = $targetFile.FullName
    $signature = Get-AuthenticodeSignature -LiteralPath $target
    if ($signature.Status -ne 'Valid') {
        throw "Invalid Authenticode signature for ${target}: $($signature.StatusMessage)"
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "The signature for $target does not contain a trusted timestamp."
    }
    $actualThumbprint = $signature.SignerCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant()
    if ($null -ne $normalizedExpected -and $actualThumbprint -ne $normalizedExpected) {
        throw "Unexpected signer for $target. Expected $normalizedExpected, found $actualThumbprint."
    }
    $hash = Get-FileHash -LiteralPath $target -Algorithm SHA256
    [pscustomobject]@{
        File = $target
        Signer = $signature.SignerCertificate.Subject
        Thumbprint = $actualThumbprint
        Timestamp = $signature.TimeStamperCertificate.Subject
        Sha256 = $hash.Hash
    }
}

$publisherThumbprints = @($results | Select-Object -ExpandProperty Thumbprint -Unique)
if ($publisherThumbprints.Count -ne 1) {
    throw "TrMadenci binaries must use one publisher identity; found $($publisherThumbprints.Count)."
}

$invalidPackageBinaries = foreach ($targetFile in $packageBinaries) {
    $signature = Get-AuthenticodeSignature -LiteralPath $targetFile.FullName
    if ($signature.Status -ne 'Valid') {
        [pscustomobject]@{
            File = $targetFile.FullName
            Status = $signature.Status
            Message = $signature.StatusMessage
        }
    }
}
if (@($invalidPackageBinaries).Count -gt 0) {
    $details = ($invalidPackageBinaries | ForEach-Object { "$($_.File) [$($_.Status)]" }) -join '; '
    throw "Package contains unsigned or invalid executable binaries: $details"
}

$results | Format-Table -AutoSize
Write-Host "Verified $($publisherTargets.Count) TrMadenci binaries from publisher $($publisherThumbprints[0])."
Write-Host "Verified Authenticode trust for all $($packageBinaries.Count) EXE/DLL files in the package."
