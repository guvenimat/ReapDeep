param(
    [switch]$SkipIngest,
    [switch]$SkipSeed,
    [switch]$SkipBench,
    [switch]$KeepData,
    [switch]$CleanupOnly,
    [long]$Events = 100000000,
    [int]$Runs = 20
)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")

$envValues = Initialize-LocalEnv
$pg = "Host=localhost;Port=5432;Database=reapdeep;Username=reapdeep;Password=$($envValues.POSTGRES_PASSWORD)"
$sr = "Server=localhost;Port=9030;User ID=root;Database=analytics;ConnectionReset=false"
$toolOut = Join-Path $env:TEMP "reapdeep-loadtest"
$resultsDir = Join-Path $PSScriptRoot "load-test-results"
New-Item -ItemType Directory -Force $resultsDir | Out-Null
$report = Join-Path $resultsDir ("report-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".md")

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Tool {
    & dotnet (Join-Path $toolOut "ReapDeep.LoadTest.dll") @args --report $report
    if ($LASTEXITCODE -ne 0) { throw "ReapDeep.LoadTest $($args[0]) failed" }
}
function ToolInNetwork {
    $toolArgs = $args
    $containerReport = "/results/" + (Split-Path $report -Leaf)
    & { $ErrorActionPreference = "Continue"
        docker run --rm --network reapdeep_default -v "${toolOut}:/app:ro" -v "${resultsDir}:/results" mcr.microsoft.com/dotnet/runtime:10.0 `
            dotnet /app/ReapDeep.LoadTest.dll @toolArgs --report $containerReport 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { throw "ReapDeep.LoadTest $($toolArgs[0]) failed" }
}
function Append([string]$text) { Add-Content -Path $report -Value $text -Encoding utf8 }

function Start-StatsSampler([string]$file) {
    Start-Job -ArgumentList $file -ScriptBlock {
        param($file)
        while ($true) {
            docker stats --no-stream --format "{{.Name}},{{.CPUPerc}},{{.MemUsage}}" | Add-Content -Path $file
            Start-Sleep -Milliseconds 500
        }
    }
}

function Stop-StatsSampler($job, [string]$file, [string]$title) {
    Stop-Job $job; Remove-Job $job
    if (-not (Test-Path $file)) { return }
    $toMiB = { param($s) $n = [double]($s -replace '[^0-9.]', ''); if ($s -match 'GiB') { $n * 1024 } elseif ($s -match 'KiB') { $n / 1024 } else { $n } }
    $rows = Get-Content $file | Where-Object { $_ -match '^reapdeep-' } | ForEach-Object {
        $name, $cpu, $mem = $_.Split(',')
        [pscustomobject]@{ Name = $name; Cpu = [double]($cpu -replace '[^0-9.]', ''); Mem = & $toMiB ($mem.Split('/')[0].Trim()) }
    }
    Append "`n#### Resource peaks during $title`n"
    Append "| Container | Peak CPU (100% = 1 core) | Peak memory |"
    Append "|---|---|---|"
    foreach ($g in ($rows | Group-Object Name | Sort-Object Name)) {
        if ($g.Name -notmatch 'ingestion|kafka$|starrocks$|person-worker|postgres') { continue }
        $cpu = ($g.Group | Measure-Object Cpu -Maximum).Maximum
        $mem = ($g.Group | Measure-Object Mem -Maximum).Maximum
        Append ("| {0} | {1:0}% | {2:0} MiB |" -f $g.Name, $cpu, $mem)
    }
    Remove-Item $file -ErrorAction SilentlyContinue
}

Step "Building the load test tool"
dotnet build (Join-Path $PSScriptRoot "..\..\tests\ReapDeep.LoadTest") -c Release -o $toolOut | Select-String -Pattern "error"
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if ($CleanupOnly) {
    Step "Removing load test data (projects 2 and 3)"
    Tool cleanup --sr $sr --pg $pg
    return
}

Step "Starting the stack"
& { $ErrorActionPreference = "Continue"; docker compose --profile app up -d --build 2>&1 | Out-Null }
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }
$cpus = docker info --format "{{.NCPU}}"
$mem = [math]::Round([double](docker info --format "{{.MemTotal}}") / 1GB, 1)
Append "# ReapDeep load test - $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Append "`nSingle machine: load generator, ingestion, Kafka, StarRocks (allin1) and PostgreSQL share $cpus CPUs and $mem GB of Docker memory. Numbers show where the bottlenecks are, not production capacity."

$benchKey = "rk_bench_" + [guid]::NewGuid().ToString("N")
Tool setup --pg $pg --bench-key $benchKey

if (-not $SkipIngest) {
    Append "`nIngestion load runs from a container on the compose network (http://ingestion:8000). Running it from the Windows host adds the Docker Desktop port forwarder to every request, which alone produced 0.4-1.5 s p99 spikes."
    $runTag = "lt" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $scenarios = @(
        @{ Name = "10K/s small batches (10 events/request)"; Rate = 10000; Seconds = 30; Batch = 10; Marker = $false },
        @{ Name = "10K/s sustained (50 events/request)"; Rate = 10000; Seconds = 60; Batch = 50; Marker = $true },
        @{ Name = "50K/s peak (100 events/request)"; Rate = 50000; Seconds = 20; Batch = 100; Marker = $true }
    )
    $i = 0
    foreach ($s in $scenarios) {
        $i++
        Step "Ingestion: $($s.Name)"
        $statsFile = Join-Path $env:TEMP "reapdeep-stats-$runTag-$i.csv"
        $job = Start-StatsSampler $statsFile
        $toolArgs = @("ingest", "--name", $s.Name, "--rate", $s.Rate, "--seconds", $s.Seconds, "--batch", $s.Batch, "--run-id", "$runTag-$i",
            "--url", "http://ingestion:8000/capture", "--sr", "Server=starrocks;Port=9030;User ID=root;Database=analytics;ConnectionReset=false")
        if ($s.Marker) { $toolArgs += "--marker" }
        try { ToolInNetwork @toolArgs } finally { Stop-StatsSampler $job $statsFile $s.Name }
    }
}

if (-not $SkipSeed) {
    Step "Seeding $Events events into StarRocks (project 2)"
    Tool seed --sr $sr --pg $pg --events $Events
    Start-Sleep -Seconds 30
}

if (-not $SkipBench) {
    Step "Query benchmark"
    Tool bench --api "http://localhost:8001" --key $benchKey --runs $Runs
}

if (-not $KeepData) {
    Step "Removing load test data (projects 2 and 3)"
    Tool cleanup --sr $sr --pg $pg
}

Step "Report: $report"
