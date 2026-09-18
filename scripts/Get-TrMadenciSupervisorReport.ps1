[CmdletBinding()]
param(
    [string] $ReportDirectory,

    [string] $SoakDirectory,

    [ValidateRange(1, 720)]
    [int] $Hours = 24,

    [ValidateRange(1, 100)]
    [int] $LatestFailures = 5,

    [switch] $AsJson
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
$generatedAt = [DateTimeOffset]::Now
$windowStart = $generatedAt.AddHours(-$Hours)
$events = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[object]]::new()
$parseErrors = [System.Collections.Generic.List[object]]::new()
$hashrateSamples = [System.Collections.Generic.List[object]]::new()
$gpuSamples = [System.Collections.Generic.List[object]]::new()
$soakFinalSnapshots = [System.Collections.Generic.List[object]]::new()
$eventFiles = @()
$failureFiles = @()
$logFiles = @()
$soakFiles = @()
$acceptedShareEvents = 0
$rejectedShareEvents = 0
$invalidShareEvents = 0
$latestCoin = $null
$latestTicker = $null
$latestNetwork = $null
$latestAlgorithm = $null
$latestPool = $null

function ConvertTo-ReportTimestamp {
    param([AllowNull()][object] $Value)

    if ($null -eq $Value) {
        return $null
    }
    $timestamp = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse([string] $Value, [ref] $timestamp)) {
        return $timestamp
    }
    return $null
}

if (Test-Path -LiteralPath $reportPath -PathType Container) {
    $eventFiles = @(Get-ChildItem -LiteralPath $reportPath -File -Filter 'supervisor-*.jsonl' |
        Where-Object { $_.LastWriteTimeUtc -ge $windowStart.UtcDateTime } |
        Sort-Object LastWriteTimeUtc)
    foreach ($file in $eventFiles) {
        $lineNumber = 0
        foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
            $lineNumber++
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            try {
                $entry = $line | ConvertFrom-Json
                $timestamp = ConvertTo-ReportTimestamp $entry.timestamp
                if ($null -ne $timestamp -and $timestamp -ge $windowStart) {
                    $events.Add($entry)
                }
            } catch {
                $parseErrors.Add([pscustomobject]@{
                    file = $file.Name
                    line = $lineNumber
                    error = $_.Exception.Message
                })
            }
        }
    }

    $failureFiles = @(Get-ChildItem -LiteralPath $reportPath -File -Filter 'crash-*.json' |
        Where-Object { $_.LastWriteTimeUtc -ge $windowStart.UtcDateTime } |
        Sort-Object LastWriteTimeUtc -Descending)
    foreach ($file in $failureFiles) {
        try {
            $entry = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $timestamp = ConvertTo-ReportTimestamp $entry.exitedAt
            if ($null -ne $timestamp -and $timestamp -ge $windowStart) {
                $failures.Add([pscustomobject]@{
                    file = $file.Name
                    path = $file.FullName
                    exitedAt = $timestamp
                    childProcessId = $entry.childProcessId
                    runtime = $entry.runtime
                    exitCode = $entry.exitCode
                    exitCodeHex = $entry.exitCodeHex
                    reason = $entry.reason
                    restartPlanned = $entry.restartPlanned
                    restartAttempt = $entry.restartAttempt
                })
            }
        } catch {
            $parseErrors.Add([pscustomobject]@{
                file = $file.Name
                line = $null
                error = $_.Exception.Message
            })
        }
    }

    $logFiles = @(Get-ChildItem -LiteralPath $reportPath -File -Filter 'supervisor-*.log' |
        Where-Object { $_.LastWriteTimeUtc -ge $windowStart.UtcDateTime } |
        Sort-Object LastWriteTimeUtc)
    foreach ($file in $logFiles) {
        $lineNumber = 0
        try {
            foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
                $lineNumber++
                if ($line -notmatch '^(?<timestamp>\S+)\s+\[(?:stdout|stderr)\]\s+(?<message>.*)$') {
                    continue
                }
                $timestampText = $Matches.timestamp
                $message = $Matches.message
                $timestamp = ConvertTo-ReportTimestamp $timestampText
                if ($null -eq $timestamp -or $timestamp -lt $windowStart) {
                    continue
                }

                if ($message -match 'Coin/network:\s+(?<coin>.+?)\s+\((?<ticker>[^)]+)\)\s+/\s+(?<network>.+)$') {
                    $latestCoin = $Matches.coin
                    $latestTicker = $Matches.ticker
                    $latestNetwork = $Matches.network
                }
                if ($message -match 'Algorithm:\s+(?<algorithm>[A-Za-z0-9_-]+)') {
                    $latestAlgorithm = $Matches.algorithm
                }
                if ($message -match '^Pool:\s+(?<pool>\S+)') {
                    $latestPool = $Matches.pool
                }
                if ($message -match 'Share #\d+ ACCEPTED') {
                    $acceptedShareEvents++
                }
                if ($message -match 'share rejected:') {
                    $rejectedShareEvents++
                }
                if ($message -match 'failed local CPU verification') {
                    $invalidShareEvents++
                }
                if ($message -match 'Hashrate\s+(?<rate>\d+(?:\.\d+)?)\s+(?<unit>MH/s|H/s)\s+\|\s+shares\s+(?<accepted>\d+)/(?<rejected>\d+)') {
                    $rate = 0.0
                    if ([double]::TryParse(
                        $Matches.rate,
                        [Globalization.NumberStyles]::Float,
                        [Globalization.CultureInfo]::InvariantCulture,
                        [ref] $rate)) {
                        $megaHashesPerSecond = if ($Matches.unit -eq 'H/s') {
                            $rate / 1000000
                        } else {
                            $rate
                        }
                        $hashrateSamples.Add([pscustomobject]@{
                            timestamp = $timestamp
                            megaHashesPerSecond = $megaHashesPerSecond
                            sessionAccepted = [long] $Matches.accepted
                            sessionRejected = [long] $Matches.rejected
                        })
                    }
                }
                if ($message -match 'GPU(?<device>\d+)\s+(?<rate>\d+(?:\.\d+)?)\s+MH/s') {
                    $gpuRate = 0.0
                    [void] [double]::TryParse(
                        $Matches.rate,
                        [Globalization.NumberStyles]::Float,
                        [Globalization.CultureInfo]::InvariantCulture,
                        [ref] $gpuRate)
                    $deviceIndex = [int] $Matches.device
                    $power = $null
                    $temperature = $null
                    if ($message -match '\|\s+power\s+(?<power>\d+(?:\.\d+)?)W') {
                        $parsedPower = 0.0
                        if ([double]::TryParse(
                            $Matches.power,
                            [Globalization.NumberStyles]::Float,
                            [Globalization.CultureInfo]::InvariantCulture,
                            [ref] $parsedPower)) {
                            $power = $parsedPower
                        }
                    }
                    if ($message -match '\|\s+temp\s+(?<temperature>\d+)C') {
                        $temperature = [int] $Matches.temperature
                    }
                    $gpuSamples.Add([pscustomobject]@{
                        timestamp = $timestamp
                        deviceIndex = $deviceIndex
                        megaHashesPerSecond = $gpuRate
                        powerWatts = $power
                        temperatureC = $temperature
                    })
                }
            }
        } catch {
            $parseErrors.Add([pscustomobject]@{
                file = $file.Name
                line = $lineNumber
                error = $_.Exception.Message
            })
        }
    }
}

if (Test-Path -LiteralPath $soakPath -PathType Container) {
    $soakFiles = @(Get-ChildItem -LiteralPath $soakPath -File -Filter '*-soak-*.jsonl' |
        Where-Object { $_.LastWriteTimeUtc -ge $windowStart.UtcDateTime } |
        Sort-Object LastWriteTimeUtc)
    foreach ($file in $soakFiles) {
        $lineNumber = 0
        $lastSnapshot = $null
        try {
            foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
                $lineNumber++
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }
                $entry = $line | ConvertFrom-Json
                $timestamp = ConvertTo-ReportTimestamp $entry.timestamp
                if ($null -eq $timestamp -or $timestamp -lt $windowStart -or
                    $entry.type -ne 'status' -or $null -eq $entry.snapshot) {
                    continue
                }
                $snapshot = $entry.snapshot
                $lastSnapshot = $snapshot
                $latestCoin = $snapshot.CoinName
                $latestTicker = $snapshot.CoinTicker
                $latestNetwork = $snapshot.Network
                $latestAlgorithm = $snapshot.Algorithm
                $latestPool = $snapshot.Pool
                $hashrateSamples.Add([pscustomobject]@{
                    timestamp = $timestamp
                    megaHashesPerSecond = [double] $snapshot.CurrentHashesPerSecond / 1000000
                    sessionAccepted = [long] $snapshot.AcceptedShares
                    sessionRejected = [long] $snapshot.RejectedShares
                })
                foreach ($gpu in @($snapshot.Gpus)) {
                    $gpuSamples.Add([pscustomobject]@{
                        timestamp = $timestamp
                        deviceIndex = [int] $gpu.DeviceIndex
                        megaHashesPerSecond = [double] $gpu.HashesPerSecond / 1000000
                        powerWatts = if ($null -ne $gpu.Telemetry) {
                            $gpu.Telemetry.PowerWatts
                        } else { $null }
                        temperatureC = if ($null -ne $gpu.Telemetry) {
                            $gpu.Telemetry.TemperatureC
                        } else { $null }
                    })
                }
            }
            if ($null -ne $lastSnapshot) {
                $soakFinalSnapshots.Add([pscustomobject]@{
                    file = $file.Name
                    snapshot = $lastSnapshot
                })
            }
        } catch {
            $parseErrors.Add([pscustomobject]@{
                file = $file.Name
                line = $lineNumber
                error = $_.Exception.Message
            })
        }
    }
}

$eventCounts = [ordered]@{}
foreach ($group in @($events | Group-Object type | Sort-Object Name)) {
    $eventCounts[$group.Name] = $group.Count
}
$reasonCounts = @($failures | Group-Object reason | Sort-Object Name | ForEach-Object {
    [pscustomobject]@{
        reason = $_.Name
        count = $_.Count
    }
})
$latest = @($failures | Sort-Object exitedAt -Descending | Select-Object -First $LatestFailures |
    ForEach-Object {
        [pscustomobject]@{
            file = $_.file
            path = $_.path
            exitedAt = $_.exitedAt.ToString('O')
            childProcessId = $_.childProcessId
            runtime = $_.runtime
            exitCode = $_.exitCode
            exitCodeHex = $_.exitCodeHex
            reason = $_.reason
            restartPlanned = $_.restartPlanned
            restartAttempt = $_.restartAttempt
        }
    })
$lastEvent = $events | Sort-Object { ConvertTo-ReportTimestamp $_.timestamp } -Descending |
    Select-Object -First 1

$hashrateValues = @($hashrateSamples | ForEach-Object megaHashesPerSecond)
$latestHashrate = $hashrateSamples | Sort-Object timestamp -Descending | Select-Object -First 1
$powerTimeline = @($gpuSamples | Group-Object {
    $_.timestamp.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss')
} | ForEach-Object {
    $items = @($_.Group)
    $powerValues = @($items | Where-Object { $null -ne $_.powerWatts } | ForEach-Object powerWatts)
    $temperatureValues = @($items | Where-Object { $null -ne $_.temperatureC } |
        ForEach-Object temperatureC)
    [pscustomobject]@{
        timestamp = ($items | Sort-Object timestamp | Select-Object -First 1).timestamp
        totalPowerWatts = if ($powerValues.Count -gt 0) {
            ($powerValues | Measure-Object -Sum).Sum
        } else {
            $null
        }
        maximumTemperatureC = if ($temperatureValues.Count -gt 0) {
            ($temperatureValues | Measure-Object -Maximum).Maximum
        } else {
            $null
        }
    }
} | Sort-Object timestamp)
$powerValues = @($powerTimeline | Where-Object { $null -ne $_.totalPowerWatts } |
    ForEach-Object totalPowerWatts)
$temperatureValues = @($gpuSamples | Where-Object { $null -ne $_.temperatureC } |
    ForEach-Object temperatureC)
$estimatedEnergyKwh = 0.0
for ($index = 1; $index -lt $powerTimeline.Count; $index++) {
    $previous = $powerTimeline[$index - 1]
    $current = $powerTimeline[$index]
    if ($null -eq $previous.totalPowerWatts) {
        continue
    }
    $seconds = ($current.timestamp - $previous.timestamp).TotalSeconds
    if ($seconds -gt 0) {
        $estimatedEnergyKwh += $previous.totalPowerWatts * [Math]::Min($seconds, 30) / 3600000
    }
}
$latestPower = $powerTimeline | Where-Object { $null -ne $_.totalPowerWatts } |
    Sort-Object timestamp -Descending | Select-Object -First 1
$structuredAccepted = @($soakFinalSnapshots | ForEach-Object { [long] $_.snapshot.AcceptedShares } |
    Measure-Object -Sum).Sum
$structuredRejected = @($soakFinalSnapshots | ForEach-Object { [long] $_.snapshot.RejectedShares } |
    Measure-Object -Sum).Sum
$structuredInvalid = @($soakFinalSnapshots | ForEach-Object { [long] $_.snapshot.InvalidShares } |
    Measure-Object -Sum).Sum
$structuredEnergyKwh = @($soakFinalSnapshots |
    ForEach-Object { [double] $_.snapshot.SessionEnergyKwh } | Measure-Object -Sum).Sum
$hasStructuredSoak = $soakFinalSnapshots.Count -gt 0
$miningSummary = [ordered]@{
    coin = $latestCoin
    ticker = $latestTicker
    network = $latestNetwork
    algorithm = $latestAlgorithm
    pool = $latestPool
    acceptedShareEvents = $acceptedShareEvents
    rejectedShareEvents = $rejectedShareEvents
    invalidShareEvents = $invalidShareEvents
    acceptedShares = if ($hasStructuredSoak) { $structuredAccepted } else { $acceptedShareEvents }
    rejectedShares = if ($hasStructuredSoak) { $structuredRejected } else { $rejectedShareEvents }
    invalidShares = if ($hasStructuredSoak) { $structuredInvalid } else { $invalidShareEvents }
    shareSource = if ($hasStructuredSoak) { 'soak-final-snapshots' } else { 'supervisor-log-events' }
    hashrateSampleCount = $hashrateValues.Count
    latestMegaHashesPerSecond = if ($null -ne $latestHashrate) {
        $latestHashrate.megaHashesPerSecond
    } else { $null }
    averageMegaHashesPerSecond = if ($hashrateValues.Count -gt 0) {
        ($hashrateValues | Measure-Object -Average).Average
    } else { $null }
    maximumMegaHashesPerSecond = if ($hashrateValues.Count -gt 0) {
        ($hashrateValues | Measure-Object -Maximum).Maximum
    } else { $null }
    telemetrySampleCount = $powerTimeline.Count
    latestPowerWatts = if ($null -ne $latestPower) { $latestPower.totalPowerWatts } else { $null }
    averagePowerWatts = if ($powerValues.Count -gt 0) {
        ($powerValues | Measure-Object -Average).Average
    } else { $null }
    estimatedEnergyKwh = $estimatedEnergyKwh
    energyKwh = if ($hasStructuredSoak) { $structuredEnergyKwh } else { $estimatedEnergyKwh }
    energySource = if ($hasStructuredSoak) { 'soak-session-counters' } else { 'bounded-log-integration' }
    maximumTemperatureC = if ($temperatureValues.Count -gt 0) {
        ($temperatureValues | Measure-Object -Maximum).Maximum
    } else { $null }
}

$summary = [ordered]@{
    schemaVersion = 1
    product = 'TrMadenci'
    generatedAt = $generatedAt.ToString('O')
    windowStart = $windowStart.ToString('O')
    windowHours = $Hours
    reportDirectory = $reportPath
    reportDirectoryExists = Test-Path -LiteralPath $reportPath -PathType Container
    eventFileCount = $eventFiles.Count
    failureFileCount = $failureFiles.Count
    combinedLogFileCount = $logFiles.Count
    soakEvidenceFileCount = $soakFiles.Count
    eventsInWindow = $events.Count
    failuresInWindow = $failures.Count
    eventCounts = $eventCounts
    failureReasons = $reasonCounts
    lastEvent = $lastEvent
    latestFailures = $latest
    mining = $miningSummary
    parseErrors = @($parseErrors)
}

if ($AsJson) {
    $summary | ConvertTo-Json -Depth 10
    return
}

Write-Host "TrMadenci Supervisor report ($Hours hour window)"
Write-Host "Directory : $reportPath"
Write-Host "Events    : $($events.Count) in $($eventFiles.Count) file(s)"
Write-Host "Failures  : $($failures.Count)"
Write-Host "Parse errs: $($parseErrors.Count)"
if ($hashrateValues.Count -gt 0 -or $powerValues.Count -gt 0 -or
    $acceptedShareEvents + $rejectedShareEvents + $invalidShareEvents -gt 0) {
    Write-Host ''
    Write-Host 'Mining activity in window:'
    Write-Host ("  Coin/network : {0} ({1}) / {2} / {3}" -f `
        $latestCoin, $latestTicker, $latestNetwork, $latestAlgorithm)
    Write-Host ("  Shares       : {0} accepted / {1} rejected / {2} local-invalid" -f `
        $miningSummary.acceptedShares, $miningSummary.rejectedShares, $miningSummary.invalidShares)
    Write-Host ("  Hashrate     : latest {0:N2}, average {1:N2}, maximum {2:N2} MH/s" -f `
        $miningSummary.latestMegaHashesPerSecond,
        $miningSummary.averageMegaHashesPerSecond,
        $miningSummary.maximumMegaHashesPerSecond)
    $energyLabel = if ($miningSummary.energySource -eq 'soak-session-counters') {
        'measured'
    } else {
        'estimated'
    }
    Write-Host ("  Power/energy : latest {0:N1} W, average {1:N1} W, {2} {3:N3} kWh" -f `
        $miningSummary.latestPowerWatts,
        $miningSummary.averagePowerWatts,
        $energyLabel,
        $miningSummary.energyKwh)
    Write-Host ("  Peak temp    : {0} C" -f $miningSummary.maximumTemperatureC)
}
if ($eventCounts.Count -gt 0) {
    Write-Host ''
    Write-Host 'Event counts:'
    $eventCounts.GetEnumerator() | ForEach-Object {
        Write-Host ("  {0,-24} {1,5}" -f $_.Key, $_.Value)
    }
}
if ($reasonCounts.Count -gt 0) {
    Write-Host ''
    Write-Host 'Failure reasons:'
    $reasonCounts | ForEach-Object {
        Write-Host ("  {0,-24} {1,5}" -f $_.reason, $_.count)
    }
}
if ($latest.Count -gt 0) {
    Write-Host ''
    Write-Host 'Latest failures:'
    $latest | Format-Table exitedAt, reason, exitCodeHex, restartPlanned, restartAttempt, file -AutoSize
}
