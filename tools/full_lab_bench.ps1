param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("baseline", "final")]
    [string]$Phase,
    [int]$Runs = 2,
    [string]$OutRoot = "out/full-lab-bench"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $root
$resolvedOutRoot = [System.IO.Path]::GetFullPath($OutRoot, $root)
New-Item -ItemType Directory -Force -Path $resolvedOutRoot | Out-Null

$summary = [System.Collections.Generic.List[object]]::new()
for ($run = 1; $run -le $Runs; $run++) {
    $name = "full-lab-$Phase-$run"
    $out = Join-Path $resolvedOutRoot $name
    $stdoutPath = Join-Path $resolvedOutRoot "$name.stdout.log"
    $stderrPath = Join-Path $resolvedOutRoot "$name.stderr.log"
    Write-Host "[$Phase $run/$Runs] starting full lab generation: $out"
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $argumentLine = "run bpcg all --planet earth --profile lab --seed 6226 --out `"$out`""
    $process = Start-Process -FilePath (Get-Command uv).Source -ArgumentList $argumentLine -WorkingDirectory $root `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru

    $lastCpuByPid = @{}
    $peakUtil = 0.0
    $peakThreads = 0
    $lastSampleAt = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $lastSampleCpu = 0.0
    $lastProgress = 0
    while (-not $process.HasExited) {
        Start-Sleep -Seconds 1
        $rows = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
        $byPid = @{}
        foreach ($row in $rows) { $byPid[[int]$row.ProcessId] = $row }
        $ids = [System.Collections.Generic.HashSet[int]]::new()
        [void]$ids.Add($process.Id)
        $changed = $true
        while ($changed) {
            $changed = $false
            foreach ($row in $rows) {
                if ($ids.Contains([int]$row.ParentProcessId) -and $ids.Add([int]$row.ProcessId)) { $changed = $true }
            }
        }
        $cpu = 0.0
        $threads = 0
        foreach ($id in $ids) {
            try {
                $p = Get-Process -Id $id -ErrorAction Stop
                $value = $p.CPU
                if ($null -ne $value) {
                    $lastCpuByPid[$id] = [Math]::Max([double]$lastCpuByPid[$id], [double]$value)
                    $cpu += [double]$value
                }
                $threads += $p.Threads.Count
            } catch { }
        }
        $now = [System.Diagnostics.Stopwatch]::GetTimestamp()
        $interval = ($now - $lastSampleAt) / [System.Diagnostics.Stopwatch]::Frequency
        if ($interval -gt 0) {
            $util = [Math]::Max(0.0, (($cpu - $lastSampleCpu) / $interval) * 100.0 / [Environment]::ProcessorCount)
            $peakUtil = [Math]::Max($peakUtil, $util)
        }
        $peakThreads = [Math]::Max($peakThreads, $threads)
        $lastSampleAt = $now
        $lastSampleCpu = $cpu
        if ($clock.Elapsed.TotalSeconds -ge $lastProgress + 30) {
            $lastProgress = [int]$clock.Elapsed.TotalSeconds
            Write-Host ("[{0} {1}/{2}] {3:N0}s elapsed; sampled process-tree CPU {4:N1}% of {5} logical CPUs; threads {6}" -f $Phase, $run, $Runs, $clock.Elapsed.TotalSeconds, $util, [Environment]::ProcessorCount, $threads)
        }
    }
    $process.WaitForExit()
    $process.WaitForExit()
    $clock.Stop()
    $totalCpu = 0.0
    foreach ($value in $lastCpuByPid.Values) { $totalCpu += [double]$value }
    $record = [ordered]@{
        phase = $Phase
        run = $run
        exit_code = $process.ExitCode
        wall_seconds = $clock.Elapsed.TotalSeconds
        process_tree_cpu_seconds = $totalCpu
        average_cpu_percent_of_logical_capacity = $totalCpu / $clock.Elapsed.TotalSeconds * 100.0 / [Environment]::ProcessorCount
        peak_sampled_cpu_percent_of_logical_capacity = $peakUtil
        logical_processors = [Environment]::ProcessorCount
        peak_process_tree_threads = $peakThreads
        output = $out
        stdout = $stdoutPath
        stderr = $stderrPath
    }
    $summary.Add($record)
    $record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resolvedOutRoot "$name.metrics.json")
    Write-Host ("[{0} {1}/{2}] complete: exit={3}, wall={4:N1}s, process CPU={5:N1}s, avg={6:N1}%, peak sampled={7:N1}%" -f $Phase, $run, $Runs, $process.ExitCode, $record.wall_seconds, $totalCpu, $record.average_cpu_percent_of_logical_capacity, $peakUtil)
    if ($process.ExitCode -ne 0) {
        throw "Full lab benchmark failed; inspect $stderrPath"
    }
}

$ordered = @($summary | Sort-Object wall_seconds)
$walls = @($ordered | ForEach-Object { [double]$_.wall_seconds })
$median = if ($walls.Count % 2) { $walls[[int]($walls.Count / 2)] } else { ($walls[$walls.Count / 2 - 1] + $walls[$walls.Count / 2]) / 2.0 }
[ordered]@{
    phase = $Phase
    runs = $Runs
    median_wall_seconds = $median
    min_wall_seconds = ($walls | Measure-Object -Minimum).Minimum
    max_wall_seconds = ($walls | Measure-Object -Maximum).Maximum
    run_metrics = @($summary)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $resolvedOutRoot "$Phase-summary.json")
