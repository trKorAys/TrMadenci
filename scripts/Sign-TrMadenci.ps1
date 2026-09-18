[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PackagePath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f ]{40,59}$')]
    [string] $CertificateThumbprint,

    [ValidatePattern('^https?://')]
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$normalizedThumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
if ($normalizedThumbprint -notmatch '^[0-9A-F]{40}$') {
    throw 'CertificateThumbprint must contain exactly 40 hexadecimal characters.'
}
$packageDirectory = (Resolve-Path -LiteralPath $PackagePath).Path
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
    throw "Required signing targets were not found: $($missingTargets -join ', ')"
}

$targets = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object {
    $_.BaseName.StartsWith('TrMadenci.', [System.StringComparison]::OrdinalIgnoreCase) -and
    $_.Extension -in @('.exe', '.dll')
} | Sort-Object FullName)

foreach ($targetFile in $targets) {
    $target = $targetFile.FullName
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Required signing target was not found: $target"
    }
}

$certificate = @(
    Get-ChildItem -Path Cert:\CurrentUser\My -CodeSigningCert
    Get-ChildItem -Path Cert:\LocalMachine\My -CodeSigningCert
) | Where-Object {
    $_.Thumbprint.Replace(' ', '').ToUpperInvariant() -eq $normalizedThumbprint
} | Select-Object -First 1

if ($null -eq $certificate) {
    throw "No code-signing certificate with thumbprint $normalizedThumbprint was found in CurrentUser/My or LocalMachine/My."
}
if (-not $certificate.HasPrivateKey) {
    throw 'The selected code-signing certificate does not have an accessible private key.'
}
$now = Get-Date
if ($certificate.NotBefore -gt $now -or $certificate.NotAfter -le $now) {
    throw "The selected certificate is not currently valid ($($certificate.NotBefore) - $($certificate.NotAfter))."
}

$signToolCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ($null -eq $signToolCommand) {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signToolCommand = Get-ChildItem -LiteralPath $kitsRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
}
if ($null -eq $signToolCommand) {
    throw 'signtool.exe was not found. Install the Windows SDK Signing Tools feature.'
}
$signToolPath = if ($signToolCommand.PSObject.Properties.Name -contains 'Source') {
    $signToolCommand.Source
} else {
    $signToolCommand.FullName
}
$storeArguments = if ($certificate.PSParentPath -like '*LocalMachine*') { @('/sm') } else { @() }

foreach ($targetFile in $targets) {
    $target = $targetFile.FullName
    if (-not $PSCmdlet.ShouldProcess($target, "Authenticode sign with $normalizedThumbprint")) {
        continue
    }
    $arguments = @(
        'sign'
        '/sha1', $normalizedThumbprint
        '/fd', 'SHA256'
        '/tr', $TimestampUrl
        '/td', 'SHA256'
    ) + $storeArguments + @($target)
    & $signToolPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe failed for $target with exit code $LASTEXITCODE."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $target
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant() -ne $normalizedThumbprint) {
        throw "Signature verification failed for ${target}: $($signature.StatusMessage)"
    }
    Write-Host "Signed and verified: $target"
}
