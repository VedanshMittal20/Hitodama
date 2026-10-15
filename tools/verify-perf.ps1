param(
    [ValidateRange(5, 3600)]
    [int]$DurationSeconds = 120,
    [ValidateRange(1, 60)]
    [int]$IntervalSeconds = 5
)

$ErrorActionPreference = 'Stop'
$process = Get-Process -Name 'Companion.Shell' -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending |
    Select-Object -First 1

$startedProcess = $false
if ($null -eq $process) {
    $scriptRoot = Split-Path -Parent $PSScriptRoot
    $dotnetRoot = Join-Path $scriptRoot ".dotnet"
    if (Test-Path $dotnetRoot) {
        $env:DOTNET_ROOT = $dotnetRoot
    }
    $exePath = Join-Path $scriptRoot "publish\Shell\Companion.Shell.exe"
    $workDir = Join-Path $scriptRoot "publish\Shell"
    $process = Start-Process -FilePath $exePath -WorkingDirectory $workDir -PassThru
    $startedProcess = $true
    Start-Sleep -Seconds 20
}

try {
    $processInstance = Get-CimInstance -ClassName Win32_PerfRawData_PerfProc_Process |
        Where-Object { [int]$_.IDProcess -eq $process.Id } |
        Select-Object -First 1

    if ($null -eq $processInstance) {
        throw "Could not locate the performance counter instance for PID $($process.Id)."
    }

$counterPaths = @(
    "\Process($($processInstance.Name))\Working Set - Private",
    "\Process($($processInstance.Name))\% Processor Time"
)
$sampleCount = [Math]::Ceiling($DurationSeconds / $IntervalSeconds)
$samples = Get-Counter -Counter $counterPaths -SampleInterval $IntervalSeconds -MaxSamples $sampleCount
$rows = foreach ($sample in $samples) {
    $privateBytes = ($sample.CounterSamples |
        Where-Object { $_.Path -like '*Working Set - Private' } |
        Select-Object -First 1).CookedValue
    $processorTime = ($sample.CounterSamples |
        Where-Object { $_.Path -like '*% Processor Time' } |
        Select-Object -First 1).CookedValue

    [pscustomobject]@{
        Time                 = $sample.Timestamp
        PrivateWorkingSetMB  = [Math]::Round($privateBytes / 1MB, 1)
        ProcessorPercent     = [Math]::Round($processorTime / [Environment]::ProcessorCount, 1)
    }
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
    }
}
