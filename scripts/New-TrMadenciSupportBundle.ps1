[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ReportDirectory,

    [string] $SoakDirectory,

    [string] $OutputPath,

    [ValidateRange(1, 168)]
    [int] $Hours = 24,

    [ValidateRange(1048576, 268435456)]
    [long] $MaximumReportBytes = 67108864
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
        'TrMadenci\crash-reports'
}
$reportPath = [System.IO.Path]::GetFullPath($ReportDirectory)
if ([string]::IsNullOrWhiteSpace($SoakDirectory)) {
    $SoakDirectory = Join-Path (Get-Location).Path 'artifacts\soak'
}
$soakPath = [System.IO.Path]::GetFullPath($SoakDirectory)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path `
        (Get-Location).Path `
        ("artifacts\support\TrMadenci-support-{0:yyyyMMdd-HHmmss}.zip" -f [DateTimeOffset]::Now)
}
$bundlePath = [System.IO.Path]::GetFullPath($OutputPath)
if ([System.IO.Path]::GetExtension($bundlePath) -ne '.zip') {
    throw 'OutputPath must use the .zip extension.'
}
if (Test-Path -LiteralPath $bundlePath) {
    throw "Refusing to overwrite an existing support bundle: $bundlePath"
}

function Protect-TrMadenciText {
    param([Parameter(Mandatory = $true)][string] $Text)

    $protected = [regex]::Replace(
        $Text,
        '(?i)([a-z][a-z0-9+.-]*://)([^/@:\s]+):([^/@\s]+)@',
        '$1[REDACTED]:[REDACTED]@')
    $protected = [regex]::Replace(
        $protected,
        '(?im)(--(?:password|secret|token|credential|private-key)(?:=|\s+))([^\s"'']+)',
        '$1[REDACTED]')
    $protected = [regex]::Replace(
        $protected,
        '(?i)("(?:password|secret|token|credential|privateKey|username|worker|wallet)"\s*:\s*")[^"]*(")',
        '$1[REDACTED]$2')
    $protected = [regex]::Replace(
        $protected,
        '(?i)\b(password|secret|token|credential|private[-_]?key)\s*=\s*([^\s,;"'']+)',
        '$1=[REDACTED]')
    $protected = [regex]::Replace(
        $protected,
        '(?im)(User worker:\s*)\S+',
        '$1[REDACTED]')
    $protected = [regex]::Replace(
        $protected,
        '(?im)((?:Developer destination|pool ready):\s*\S+\s*/\s*)\S+',
        '$1[REDACTED]')
    return $protected
}

if (-not $PSCmdlet.ShouldProcess($bundlePath, "Create sanitized TrMadenci support bundle for the last $Hours hours")) {
    return
}

$outputDirectory = Split-Path -Parent $bundlePath
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
$workingDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $tempRoot ("TrMadenciSupport-{0}" -f [Guid]::NewGuid().ToString('N'))))
$requiredPrefix = $tempRoot + [System.IO.Path]::DirectorySeparatorChar
if (-not $workingDirectory.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe temporary support-bundle path: $workingDirectory"
}

$included = [System.Collections.Generic.List[object]]::new()
$skipped = [System.Collections.Generic.List[object]]::new()
$totalSourceBytes = 0L
$cutoff = [DateTimeOffset]::Now.AddHours(-$Hours).UtcDateTime
try {
    $reportsTarget = Join-Path $workingDirectory 'reports'
    New-Item -ItemType Directory -Path $reportsTarget -Force | Out-Null

    $summaryScript = Join-Path $PSScriptRoot 'Get-TrMadenciSupervisorReport.ps1'
    $summaryPath = Join-Path $workingDirectory 'supervisor-summary.json'
    & $summaryScript -ReportDirectory $reportPath -SoakDirectory $soakPath -Hours $Hours -AsJson |
        Set-Content -LiteralPath $summaryPath -Encoding UTF8

    if (Test-Path -LiteralPath $reportPath -PathType Container) {
        $candidates = @(Get-ChildItem -LiteralPath $reportPath -File | Where-Object {
            $_.LastWriteTimeUtc -ge $cutoff -and (
                $_.Name -like 'supervisor-*.jsonl' -or
                $_.Name -like 'supervisor-*.log' -or
                $_.Name -like 'crash-*.json')
        } | Sort-Object LastWriteTimeUtc -Descending)
        foreach ($source in $candidates) {
            if ($totalSourceBytes + $source.Length -gt $MaximumReportBytes) {
                $skipped.Add([pscustomobject]@{
                    file = $source.Name
                    reason = 'bundle-size-cap'
                    length = $source.Length
                })
                continue
            }
            try {
                $content = [System.IO.File]::ReadAllText($source.FullName)
                $sanitized = Protect-TrMadenciText $content
                $target = Join-Path $reportsTarget $source.Name
                [System.IO.File]::WriteAllText($target, $sanitized, [System.Text.UTF8Encoding]::new($false))
                $totalSourceBytes += $source.Length
                $included.Add([pscustomobject]@{
                    file = "reports/$($source.Name)"
                    sourceLength = $source.Length
                    sanitizedLength = (Get-Item -LiteralPath $target).Length
                    sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
                })
            } catch {
                $skipped.Add([pscustomobject]@{
                    file = $source.Name
                    reason = 'read-or-sanitize-error'
                    error = $_.Exception.Message
                })
            }
        }
    }

    $soakTarget = Join-Path $workingDirectory 'soak'
    New-Item -ItemType Directory -Path $soakTarget -Force | Out-Null
    if (Test-Path -LiteralPath $soakPath -PathType Container) {
        $soakCandidates = @(Get-ChildItem -LiteralPath $soakPath -File | Where-Object {
            $_.LastWriteTimeUtc -ge $cutoff -and (
                $_.Name -like '*-soak-*.jsonl' -or
                $_.Name -like '*-soak-*.summary.json')
        } | Sort-Object LastWriteTimeUtc -Descending)
        foreach ($source in $soakCandidates) {
            if ($totalSourceBytes + $source.Length -gt $MaximumReportBytes) {
                $skipped.Add([pscustomobject]@{
                    file = "soak/$($source.Name)"
                    reason = 'bundle-size-cap'
                    length = $source.Length
                })
                continue
            }
            try {
                $content = [System.IO.File]::ReadAllText($source.FullName)
                $sanitized = Protect-TrMadenciText $content
                $target = Join-Path $soakTarget $source.Name
                [System.IO.File]::WriteAllText($target, $sanitized, [System.Text.UTF8Encoding]::new($false))
                $totalSourceBytes += $source.Length
                $included.Add([pscustomobject]@{
                    file = "soak/$($source.Name)"
                    sourceLength = $source.Length
                    sanitizedLength = (Get-Item -LiteralPath $target).Length
                    sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
                })
            } catch {
                $skipped.Add([pscustomobject]@{
                    file = "soak/$($source.Name)"
                    reason = 'read-or-sanitize-error'
                    error = $_.Exception.Message
                })
            }
        }
    }

    $gpu = [System.Collections.Generic.List[object]]::new()
    $nvidiaSmi = Get-Command 'nvidia-smi.exe' -ErrorAction SilentlyContinue
    if ($null -ne $nvidiaSmi) {
        try {
            $rows = & $nvidiaSmi.Source `
                '--query-gpu=index,name,driver_version,memory.total' `
                '--format=csv,noheader,nounits' 2>$null
            foreach ($row in $rows) {
                $columns = @($row -split ',' | ForEach-Object { $_.Trim() })
                if ($columns.Count -eq 4) {
                    $gpu.Add([pscustomobject]@{
                        index = $columns[0]
                        name = $columns[1]
                        driverVersion = $columns[2]
                        memoryMiB = $columns[3]
                    })
                }
            }
        } catch {
            $skipped.Add([pscustomobject]@{
                file = 'nvidia-smi'
                reason = 'system-info-error'
                error = $_.Exception.Message
            })
        }
    }
    $dotnetVersion = $null
    $dotnet = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($null -ne $dotnet) {
        try {
            $dotnetVersion = (& $dotnet.Source '--version' 2>$null | Select-Object -First 1)
        } catch {
            $dotnetVersion = $null
        }
    }
    $system = [ordered]@{
        collectedAt = [DateTimeOffset]::Now.ToString('O')
        osDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        dotnetVersion = $dotnetVersion
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        gpu = @($gpu)
    }
    $system | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $workingDirectory 'system.json') -Encoding UTF8

    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'TrMadenci'
        createdAt = [DateTimeOffset]::Now.ToString('O')
        windowHours = $Hours
        sanitized = $true
        configurationFilesIncluded = $false
        privateKeysIncluded = $false
        maximumReportBytes = $MaximumReportBytes
        includedEvidenceBytes = $totalSourceBytes
        includedFiles = @($included)
        skippedFiles = @($skipped)
    }
    $manifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $workingDirectory 'bundle-manifest.json') -Encoding UTF8

    Compress-Archive -Path (Join-Path $workingDirectory '*') `
        -DestinationPath $bundlePath -CompressionLevel Optimal
    Write-Host "Sanitized support bundle created: $bundlePath"
    Write-Host "Included evidence files: $($included.Count); skipped: $($skipped.Count)"
} finally {
    if (Test-Path -LiteralPath $workingDirectory -PathType Container) {
        $resolvedWorking = [System.IO.Path]::GetFullPath($workingDirectory)
        if (-not $resolvedWorking.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unsafe temporary path: $resolvedWorking"
        }
        Remove-Item -LiteralPath $resolvedWorking -Recurse -Force
    }
}
