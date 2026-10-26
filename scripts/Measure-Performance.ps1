param(
    [ValidateRange(10, 300)][int]$Seconds = 30,
    [ValidateRange(0, 60)][int]$WarmupSeconds = 5,
    [string]$OutputPath,
    [string]$AppPath = (Join-Path $env:LOCALAPPDATA 'Programs\QuietIsland\QuietIsland.exe')
)
$ErrorActionPreference = 'Stop'
$process = Get-Process QuietIsland -ErrorAction SilentlyContinue | Where-Object Path -eq $AppPath | Select-Object -First 1
if (-not $process) { throw 'Start QuietIsland before measuring.' }
$processId = $process.Id
$context = $null
try {
    Start-Process -FilePath $AppPath -ArgumentList '--status' -WindowStyle Hidden -Wait
    $status = Get-Content (Join-Path $env:LOCALAPPDATA 'QuietIsland\status.json') -Raw | ConvertFrom-Json
    if ($status.ProcessId -eq $processId) {
        $context = [pscustomobject]@{Playing=$status.Media.Playing; OnlineLyrics=$status.Lyrics.OnlineLyrics; WidthDip=$status.Width; DpiScale=$status.DpiScale; Visible=$status.Opacity -gt 0}
    }
} catch { }
if ($WarmupSeconds) { Start-Sleep -Seconds $WarmupSeconds; $process.Refresh() }
$cpuStart = $process.TotalProcessorTime.TotalSeconds
$clock = [Diagnostics.Stopwatch]::StartNew()
$samples = [Collections.Generic.List[object]]::new()
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
    $process.Refresh()
    if ($process.HasExited) { throw 'QuietIsland exited during measurement.' }
    $gpuAvailable = $true
    try { $engines = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine) }
    catch { $gpuAvailable = $false; $engines = @() }
    $ownEngines = @($engines | Where-Object { $_.Name.StartsWith("pid_${processId}_") })
    # Windows reports the busiest engine rather than averaging independent engines.
    $ownGpu = ($ownEngines | Measure-Object UtilizationPercentage -Maximum).Maximum
    $systemGpu = ($engines | Group-Object { $_.Name -replace '^pid_\d+_', '' } | ForEach-Object {
        ($_.Group | Measure-Object UtilizationPercentage -Sum).Sum
    } | Measure-Object -Maximum).Maximum
    $samples.Add([pscustomobject]@{
        WorkingSetMB = $process.WorkingSet64 / 1MB
        PrivateCommitMB = $process.PrivateMemorySize64 / 1MB
        GpuAvailable = $gpuAvailable -and $engines.Count -gt 0
        ProcessGpuPercent = if ($null -eq $ownGpu) { 0 } else { [Math]::Min(100, $ownGpu) }
        SystemBusiestGpuEnginePercent = [Math]::Min(100, [double]$systemGpu)
    })
    Start-Sleep -Milliseconds 2000
}
$process.Refresh()
$gpuSamples = @($samples | Where-Object GpuAvailable)
$result = [pscustomobject]@{
    MeasuredAt = [DateTimeOffset]::Now.ToString('o')
    Seconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
    ProcessId = $processId
    LogicalProcessors = [Environment]::ProcessorCount
    WarmupSeconds = $WarmupSeconds
    CpuTotalPercent = [Math]::Round(($process.TotalProcessorTime.TotalSeconds - $cpuStart) / $clock.Elapsed.TotalSeconds / [Environment]::ProcessorCount * 100, 3)
    WorkingSetMB = [Math]::Round(($samples | Measure-Object WorkingSetMB -Average).Average, 1)
    PrivateCommitMB = [Math]::Round(($samples | Measure-Object PrivateCommitMB -Average).Average, 1)
    GpuCountersAvailable = $gpuSamples.Count -gt 0
    ProcessGpuPeakPercent = if ($gpuSamples.Count) { ($gpuSamples | Measure-Object ProcessGpuPercent -Maximum).Maximum } else { $null }
    SystemBusiestGpuEnginePeakPercent = if ($gpuSamples.Count) { ($gpuSamples | Measure-Object SystemBusiestGpuEnginePercent -Maximum).Maximum } else { $null }
    Samples = $samples.Count
    Context = $context
}
$json = $result | ConvertTo-Json
if ($OutputPath) { [IO.File]::WriteAllText($OutputPath, $json) }
$json
