param(
    [ValidateRange(5, 3600)]
    [int]$DurationSeconds = 120,
    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 5
)

$ErrorActionPreference = 'Stop'
$existingProcess = Get-Process -Name 'Companion.Shell' -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
if ($null -ne $existingProcess) {
    throw "Stop the existing Companion.Shell process (PID $($existingProcess.Id)) before running the isolated performance gate."
}
$process = $null

$startedProcess = $false
$isolatedDataDir = $null
$previousVoidDataDir = $env:VOID_DATA_DIR
function Get-PrivateWorkingSetBytes([int]$processId) {
    try {
        $cim = Get-CimInstance -ClassName Win32_PerfRawData_PerfProc_Process -Filter "IDProcess = $processId" -ErrorAction Stop
        if ($cim -and $cim.WorkingSetPrivate) {
            return [int64]$cim.WorkingSetPrivate
        }
    }
    catch {
        # Fall back to process working set
    }

    $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($proc) {
        return [int64]$proc.WorkingSet64
    }
    return 0
}

try {
    if ($null -eq $process) {
        $scriptRoot = Split-Path -Parent $PSScriptRoot
        $dotnetRoot = Join-Path $scriptRoot ".dotnet"
        if (Test-Path $dotnetRoot) {
            $env:DOTNET_ROOT = $dotnetRoot
        }
        $workDir = Join-Path $scriptRoot "publish\Shell"
        $exePath = Join-Path $workDir "Companion.Shell.exe"
        if (-not (Test-Path -LiteralPath $exePath)) {
            $workDir = Join-Path $scriptRoot "src\Companion.Shell\bin\Release\net8.0-windows10.0.19041.0"
            $exePath = Join-Path $workDir "Companion.Shell.exe"
        }
        if (-not (Test-Path -LiteralPath $exePath)) {
            throw "Release app executable was not found at '$exePath'. Build and publish the solution first."
        }

        $isolatedDataDir = Join-Path ([System.IO.Path]::GetTempPath()) ("VoidPerf-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $isolatedDataDir | Out-Null
        $env:VOID_DATA_DIR = $isolatedDataDir
        $process = Start-Process -FilePath $exePath -WorkingDirectory $workDir -PassThru
        $startedProcess = $true
        Start-Sleep -Seconds 20
    }

    $sampleCount = [Math]::Ceiling($DurationSeconds / $IntervalSeconds)
    $rows = [System.Collections.Generic.List[object]]::new()
    $process.Refresh()
    $previousProcessorSeconds = $process.TotalProcessorTime.TotalSeconds
    $previousSampleTime = [DateTimeOffset]::UtcNow

    for ($sampleIndex = 0; $sampleIndex -lt $sampleCount; $sampleIndex++) {
        Start-Sleep -Seconds $IntervalSeconds
        $process.Refresh()
        if ($process.HasExited) {
            throw "Companion.Shell exited during performance sampling."
        }

        $sampleTime = [DateTimeOffset]::UtcNow
        $elapsedSeconds = ($sampleTime - $previousSampleTime).TotalSeconds
        $processorSeconds = $process.TotalProcessorTime.TotalSeconds
        $cpuPercent = if ($elapsedSeconds -gt 0) {
            (($processorSeconds - $previousProcessorSeconds) / $elapsedSeconds / [Environment]::ProcessorCount) * 100
        }
        else {
            0
        }

        $rows.Add([pscustomobject]@{
            Time                = $sampleTime.LocalDateTime
            PrivateWorkingSetMB = [Math]::Round((Get-PrivateWorkingSetBytes $process.Id) / 1MB, 1)
            ProcessorPercent    = [Math]::Round($cpuPercent, 1)
        })

        $previousProcessorSeconds = $processorSeconds
        $previousSampleTime = $sampleTime
    }

$rows | Format-Table -AutoSize
$averageCpu = [Math]::Round(($rows | Measure-Object -Property ProcessorPercent -Average).Average, 1)
$peakMemory = ($rows | Measure-Object -Property PrivateWorkingSetMB -Maximum).Maximum
Write-Output ("Average CPU: {0:0.0}% | Peak private working set: {1:0.0} MB" -f $averageCpu, $peakMemory)

    if ($averageCpu -gt 0.0 -or $peakMemory -ge 85) {
        throw 'Performance gate failed: idle CPU must round to 0.0% and private working set must stay below 85 MB.'
    }
}
finally {
    if ($startedProcess -and $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(10000) | Out-Null
    }
    if ($isolatedDataDir) {
        $env:VOID_DATA_DIR = $previousVoidDataDir
    }
    if ($isolatedDataDir -and (Test-Path -LiteralPath $isolatedDataDir)) {
        $resolvedTempPath = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $resolvedDataPath = [System.IO.Path]::GetFullPath($isolatedDataDir)
        $tempPrefix = $resolvedTempPath.TrimEnd('\') + '\'
        if (-not $resolvedDataPath.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove isolated data directory outside the system temp directory: '$resolvedDataPath'."
        }
        for ($attempt = 0; $attempt -lt 20 -and (Test-Path -LiteralPath $resolvedDataPath); $attempt++) {
            try {
                Remove-Item -LiteralPath $resolvedDataPath -Recurse -Force
            }
            catch {
                if ($attempt -eq 19) {
                    Write-Warning "Could not remove isolated data directory '$resolvedDataPath': $($_.Exception.Message)"
                }
                else {
                    Start-Sleep -Milliseconds 500
                }
            }
        }
    }
}
