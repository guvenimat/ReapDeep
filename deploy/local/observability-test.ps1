$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
Add-Type -AssemblyName System.Net.Http

$t = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$script:failed = 0
$http = New-Object System.Net.Http.HttpClient
$grafana = "http://127.0.0.1:3005/api/datasources/proxy/uid"

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}
function WaitFor([scriptblock]$cond, $seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $cond) { return $true } Start-Sleep -Milliseconds 2000 }
    return [bool](& $cond)
}
function Get([string]$url) { $http.GetStringAsync($url).Result | ConvertFrom-Json }
function Prom([string]$query) { (Get "$grafana/prometheus/api/v1/query?query=$([Uri]::EscapeDataString($query))").data.result }
function Sr([string]$sql) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $out = docker.exe exec reapdeep-starrocks mysql "-h127.0.0.1" -P9030 -uroot -N -e $sql 2>&1 | Out-String
    $ErrorActionPreference = $prev
    $out.Trim()
}

Write-Host "`n1) Container health"
foreach ($c in "reapdeep-ingestion", "reapdeep-query-api", "reapdeep-recommender", "reapdeep-person-worker") {
    Check "$c reports healthy" (WaitFor { (docker.exe inspect -f "{{.State.Health.Status}}" $c) -eq "healthy" } 90)
}

Write-Host "`n2) Metrics and traces"
$body = @{ api_key = "pk_dev_local"; batch = @(@{ event = "observability_probe"; distinct_id = "obs_$t"; event_id = "obs-$t"; properties = @{} }) } | ConvertTo-Json -Depth 5 -Compress
$null = $http.PostAsync("http://localhost:8000/capture", (New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "text/plain"))).Result
foreach ($s in "reapdeep-ingestion", "reapdeep-query-api", "reapdeep-recommender", "reapdeep-person-worker") {
    Check "$s exports metrics" (WaitFor { @(Prom "target_info{service_name=`"$s`"}").Count -ge 1 } 60)
}
Check "ingestion counts accepted events" (WaitFor { @(Prom "reapdeep_ingestion_events_accepted_total").Count -ge 1 } 60)
Check "the watchdog reports every routine load job" (WaitFor { @(Prom "reapdeep_routine_load_running{load_job!=`"`"}").Count -ge 3 } 60)
$traceQuery = [Uri]::EscapeDataString('{ resource.service.name = "reapdeep-ingestion" && name = "POST /capture" }')
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
Check "capture requests appear as traces in Tempo" (WaitFor { @((Get "$grafana/tempo/api/search?q=$traceQuery&start=$($now - 600)&end=$($now + 60)&limit=5").traces).Count -ge 1 } 90)
$logQuery = [Uri]::EscapeDataString('{service_name="reapdeep-person-worker"}')
Check "logs reach Loki" (WaitFor { @((Get "$grafana/loki/loki/api/v1/query_range?query=$logQuery&limit=5").data.result).Count -ge 1 } 60)
Check "the overview dashboard is provisioned" ((Get "http://127.0.0.1:3005/api/dashboards/uid/reapdeep-overview").dashboard.panels.Count -gt 10)

Write-Host "`n3) Routine load watchdog"
$null = Sr "PAUSE ROUTINE LOAD FOR analytics.persons_from_kafka"
Start-Sleep -Seconds 25
Check "a job paused by an operator is left paused" ((Sr "SHOW ROUTINE LOAD FOR analytics.persons_from_kafka\G") -match "(?m)^PAUSED")
$null = Sr "RESUME ROUTINE LOAD FOR analytics.persons_from_kafka"
Check "and runs again once the operator resumes it" (WaitFor { (Sr "SHOW ROUTINE LOAD FOR analytics.persons_from_kafka\G") -match "(?m)^(RUNNING|NEED_SCHEDULE)" } 30)

Write-Host "`n4) Rows StarRocks cannot load are reported, not lost silently"
function FilteredRows {
    $r = @(Prom 'sum(reapdeep_routine_load_error_rows_total{load_job="events_from_kafka"})')
    if ($r.Count -eq 0) { return [double]0 }
    [double]$r[0].value[1]
}
$filteredBefore = FilteredRows
$startNs = [DateTimeOffset]::UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds() * 1000000
$ts = [DateTime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff")
$tooLong = "x" * 100
$row = '{"project_id":1,"event":"filtered_probe","timestamp":"' + $ts + '","distinct_id":"filtered_' + $t + '","event_id":"' + $tooLong + '","properties":{},"inserted_at":"' + $ts + '"}'
$encoded = [Convert]::ToBase64String((New-Object Text.UTF8Encoding($false)).GetBytes($row + "`n"))
$prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
$null = docker.exe exec reapdeep-kafka bash -c "echo $encoded | base64 -d | /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server kafka:29092 --topic events" 2>&1
$ErrorActionPreference = $prev
Check "the watchdog exports the filtered row as reapdeep_routine_load_error_rows" (WaitFor { (FilteredRows) -gt $filteredBefore } 150)
$filteredQuery = [Uri]::EscapeDataString('{service_name="reapdeep-person-worker"} |= "filtered out"')
Check "and logs a warning with the StarRocks error log" (WaitFor { @((Get "$grafana/loki/loki/api/v1/query_range?query=$filteredQuery&start=$startNs&limit=5").data.result).Count -ge 1 } 90)
Check "the row is indeed not in StarRocks" ((Sr "SELECT count(*) FROM analytics.events WHERE project_id = 1 AND distinct_id = 'filtered_$t'") -eq "0")
Check "the overview dashboard shows filtered rows" (@((Get "http://127.0.0.1:3005/api/dashboards/uid/reapdeep-overview").dashboard.panels | Where-Object { $_.title -like "Rows filtered out*" }).Count -eq 1)
Check "the events job keeps running" ((Sr "SHOW ROUTINE LOAD FOR analytics.events_from_kafka\G") -match "(?m)^(RUNNING|NEED_SCHEDULE)")

Write-Host ""
if ($script:failed -gt 0) { Write-Host "$($script:failed) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All checks passed." -ForegroundColor Green
