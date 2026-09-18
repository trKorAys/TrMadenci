[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $PackagePath,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $ConfigurationPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f ]{40,59}$')]
    [string] $ExpectedThumbprint,

    [Parameter(Mandatory = $true)]
    [switch] $AcknowledgeMining
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageDirectory = (Resolve-Path -LiteralPath $PackagePath).Path
$configurationFullPath = (Resolve-Path -LiteralPath $ConfigurationPath).Path
$executable = Join-Path $packageDirectory 'TrMadenci.Service.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Packaged miner executable was not found: $executable"
}
if (-not $AcknowledgeMining.IsPresent) {
    throw 'OpenCL qualification mines and submits shares; pass -AcknowledgeMining explicitly.'
}

& (Join-Path $PSScriptRoot 'Test-TrMadenciSignatures.ps1') `
    -PackagePath $packageDirectory `
    -ExpectedThumbprint $ExpectedThumbprint
& (Join-Path $PSScriptRoot 'Test-TrMadenciReleaseManifest.ps1') `
    -PackagePath $packageDirectory

$arguments = @(
    $configurationFullPath
    '--mine'
    '--etchash-opencl-nonce-self-test'
    '--etchash-opencl-qualification'
    '--plain-console'
)
if ($PSCmdlet.ShouldProcess($executable, 'Run signed ETCHash OpenCL qualification until the first accepted share')) {
    & $executable @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "OpenCL release qualification failed with exit code $LASTEXITCODE."
    }
    Write-Host 'Signed ETCHash OpenCL qualification completed successfully.'
}
