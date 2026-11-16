param([int]$Visitors = 3000, [int]$Days = 56, [int]$Seed = 42, [switch]$Reset)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")

$envValues = Initialize-LocalEnv
$toolOut = Join-Path $env:TEMP "reapdeep-loadtest"
$inNetwork = Build-LoadTestTool $toolOut
$pgHost, $srHost, $url = if ($inNetwork) { "postgres", "starrocks", "http://ingestion:8000/capture" } else { "localhost", "localhost", "http://localhost:8000/capture" }
$pg = "Host=$pgHost;Port=5432;Database=reapdeep;Username=reapdeep;Password=$($envValues.POSTGRES_PASSWORD)"

$demoArgs = @("demo", "--pg", $pg, "--url", $url, "--visitors", $Visitors, "--days", $Days, "--seed", $Seed)
if ($Reset) { $demoArgs += @("--reset", "--sr", "Server=$srHost;Port=9030;User ID=root;Database=analytics;ConnectionReset=false") }
Invoke-LoadTestTool -OutDir $toolOut -InNetwork $inNetwork -ToolArgs $demoArgs

Write-Host "`nOpen http://localhost:3000, pick the 'Demo Shop' project and 'Last 30 days', then Dashboards." -ForegroundColor Green
