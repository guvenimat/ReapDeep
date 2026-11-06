$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
Add-Type -AssemblyName System.Net.Http

$t = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$script:failed = 0
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(30)

function Capture([hashtable[]]$events) {
    $body = @{ api_key = "pk_dev_local"; batch = $events } | ConvertTo-Json -Depth 6 -Compress
    $content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "text/plain")
    $r = $http.PostAsync("http://localhost:8000/capture", $content).Result
    $text = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($text -like "{*") { $text | ConvertFrom-Json } else { $null }) }
}
function Ev($name, $distinctId, $eventId) { @{ event = $name; distinct_id = $distinctId; event_id = $eventId; properties = @{ test = "durability" } } }
function Sr($sql) { (docker compose exec -T starrocks mysql "-h127.0.0.1" -P9030 -uroot -N -e $sql | Out-String).Trim() }
function Rows($distinctId) { Sr "SELECT count(*) FROM analytics.events WHERE project_id = 1 AND distinct_id = '$distinctId'" }
function WaitFor([scriptblock]$cond, $seconds = 90) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $cond) { return $true } Start-Sleep -Milliseconds 1000 }
    return [bool](& $cond)
}
function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}
function Quiet([string[]]$a) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $null = & docker.exe @a 2>&1
    $ErrorActionPreference = $prev
}

Write-Host "`n1) A retried batch is stored once"
$u = "dedup_$t"
$first = Capture @((Ev "page_opened" $u "evt-a-$t"), (Ev "page_opened" $u "evt-b-$t"))
$again = Capture @((Ev "page_opened" $u "evt-a-$t"), (Ev "page_opened" $u "evt-b-$t"), (Ev "page_opened" $u "evt-c-$t"))
Check "the first batch is accepted" ($first.Status -eq 202 -and $first.Json.accepted -eq 2 -and $first.Json.duplicates -eq 0)
Check "the retry reports two duplicates and is still accepted" ($again.Status -eq 202 -and $again.Json.accepted -eq 3 -and $again.Json.duplicates -eq 2)
$inBatch = Capture @((Ev "page_opened" $u "evt-d-$t"), (Ev "page_opened" $u "evt-d-$t"))
Check "a duplicate inside one batch is dropped" ($inBatch.Json.duplicates -eq 1)
$noId = Capture @(@{ event = "page_opened"; distinct_id = $u }, @{ event = "page_opened"; distinct_id = $u })
Check "events without an id are never deduplicated" ($noId.Json.accepted -eq 2 -and $noId.Json.duplicates -eq 0)
Check "StarRocks holds 6 rows for this visitor (a, b, c, d and two without an id)" (WaitFor { (Rows $u) -eq "6" })
Start-Sleep -Seconds 3
Check "and no more" ((Rows $u) -eq "6")
$other = Capture @(@{ event = "page_opened"; distinct_id = $u; event_id = "evt-a-$t"; properties = @{} })
Check "a later batch with a known id and other properties is still a duplicate" ($other.Json.duplicates -eq 1)

Write-Host "`n2) Redis down: ingestion keeps accepting"
Quiet @("stop", "reapdeep-redis")
$down = Capture @((Ev "page_opened" "redis_$t" "evt-r-$t"), (Ev "page_opened" "redis_$t" "evt-r-$t"))
Check "events are accepted without deduplication" ($down.Status -eq 202 -and $down.Json.accepted -eq 2 -and $down.Json.duplicates -eq 0)
Quiet @("start", "reapdeep-redis")
$null = WaitFor { (docker.exe inspect -f "{{.State.Health.Status}}" reapdeep-redis) -eq "healthy" } 30
$back = WaitFor { (Capture @((Ev "page_opened" "redis_$t" "evt-s-$t"), (Ev "page_opened" "redis_$t" "evt-s-$t"))).Json.duplicates -eq 1 } 30
Check "deduplication resumes when Redis is back" $back

Write-Host "`n3) Kafka down: claimed ids are released so the SDK retry is not lost"
Quiet @("stop", "reapdeep-kafka")
$kafkaDown = Capture @((Ev "page_opened" "kafka_$t" "evt-k-$t"))
Check "ingestion answers 503 while Kafka is down" ($kafkaDown.Status -eq 503)
Quiet @("start", "reapdeep-kafka")
$null = WaitFor { (docker.exe inspect -f "{{.State.Health.Status}}" reapdeep-kafka) -eq "healthy" } 90
$retry = $null
$ok = WaitFor { $script:retry = Capture @((Ev "page_opened" "kafka_$t" "evt-k-$t")); $script:retry.Status -eq 202 } 90
Check "the retry is accepted, not reported as a duplicate" ($ok -and $script:retry.Json.duplicates -eq 0 -and $script:retry.Json.accepted -eq 1)
Check "and it reaches StarRocks once" (WaitFor { (Rows "kafka_$t") -eq "1" } 120)

Write-Host "`n4) Dead letter topic"
$bad = '{"event":"$identify","distinct_id":42,"marker":"dlq_' + $t + '"}'
$encoded = [Convert]::ToBase64String((New-Object Text.UTF8Encoding($false)).GetBytes($bad + "`n"))
Quiet @("exec", "reapdeep-kafka", "bash", "-c", "echo $encoded | base64 -d | /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server kafka:29092 --topic events")
$found = WaitFor {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $out = docker.exe exec reapdeep-kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server kafka:29092 --topic events_dead_letter --from-beginning --timeout-ms 5000 --property print.headers=true 2>&1 | Out-String
    $ErrorActionPreference = $prev
    $script:dlq = $out
    $out -like "*dlq_$t*"
} 60
Check "an identity event that cannot be parsed lands in events_dead_letter" $found
Check "with the stage and the source offset in its headers" ($script:dlq -like "*reapdeep-stage:unparseable*" -and $script:dlq -like "*reapdeep-source:events/*")
Check "the message reached Kafka without a byte order mark, so StarRocks can still read it" (@($script:dlq -split "`n" | Where-Object { $_ -like "*dlq_$t*" -and $_ -notmatch [char]0xFEFF }).Count -ge 1)
Check "the person worker keeps consuming" (WaitFor { (docker.exe inspect -f "{{.State.Running}}" reapdeep-person-worker) -eq "true" } 5)

Write-Host "`n5) Query API: event list and error classification"
$readKey = ((Get-Content .env | Where-Object { $_ -like "READ_API_KEY=*" }) -split "=", 2)[1].Trim()
$req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "http://localhost:8001/api/v1/events?distinct_id=$u&limit=10")
$req.Headers.Add("Authorization", "Bearer $readKey")
$list = ($http.SendAsync($req).Result.Content.ReadAsStringAsync().Result | ConvertFrom-Json).events
Check "the event list shows each event id once" (@($list).Count -eq 6 -and @($list | Select-Object -ExpandProperty event_id -Unique).Count -eq 6)

function Pg($sql) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $out = (docker.exe exec reapdeep-postgres psql -U reapdeep -d reapdeep -tAc $sql 2>&1 | Out-String).Trim()
    $ErrorActionPreference = $prev
    $out
}
function RawCapture([string]$body) {
    $content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "text/plain")
    $http.PostAsync("http://localhost:8000/capture", $content).Result
}
function Health {
    try { [int]$http.GetAsync("http://localhost:8000/healthz").Result.StatusCode } catch { 0 }
}

Write-Host "`n6) Ingestion quota per project"
$q = "quota_$t"
$quotaBody = '{"api_key":"pk_dev_local","batch":[' + ((1..1000 | ForEach-Object { '{"event":"quota_probe","distinct_id":"' + $q + '"}' }) -join ',') + ']}'
$null = Pg "UPDATE projects SET ingest_events_per_second = 1000 WHERE id = 1"
try {
    Write-Host "  waiting 32 s for ingestion to reload the project settings..."
    Start-Sleep -Seconds 32
    $acceptedBatches = 0; $throttled = $null
    for ($i = 0; $i -lt 6 -and -not $throttled; $i++) {
        $r = RawCapture $quotaBody
        if ([int]$r.StatusCode -eq 202) { $acceptedBatches++ } elseif ([int]$r.StatusCode -eq 429) { $throttled = $r }
    }
    Check "a project over its events per second gets 429" ($null -ne $throttled)
    Check "with Retry-After: 1" ($throttled -and $throttled.Headers.RetryAfter.Delta.TotalSeconds -eq 1)
    Start-Sleep -Seconds 1
    $after = RawCapture $quotaBody
    Check "the next second accepts again" ([int]$after.StatusCode -eq 202)
    if ([int]$after.StatusCode -eq 202) { $acceptedBatches++ }
    $expected = $acceptedBatches * 1000
    Check "refused batches are not stored: StarRocks holds exactly $expected probe events" (WaitFor { (Rows $q) -eq "$expected" } 60)
} finally {
    $null = Pg "UPDATE projects SET ingest_events_per_second = NULL WHERE id = 1"
}

Write-Host "`n7) Health check reports whether the project keys are loaded"
Check "healthy while PostgreSQL is up" ((Health) -eq 200)
Quiet @("stop", "reapdeep-postgres")
try {
    Quiet @("restart", "reapdeep-ingestion")
    Check "after a restart without PostgreSQL, /healthz answers 503" (WaitFor { (Health) -eq 503 } 30)
    $down = Capture @((Ev "page_opened" "health_$t" "evt-h-$t"))
    Check "and capture answers 503, so clients retry" ($down.Status -eq 503)
} finally {
    Quiet @("start", "reapdeep-postgres")
}
Check "once PostgreSQL is back, /healthz turns 200" (WaitFor { (Health) -eq 200 } 90)
$up = Capture @((Ev "page_opened" "health_$t" "evt-h-$t"))
Check "and the retried event is accepted" ($up.Status -eq 202 -and $up.Json.duplicates -eq 0)

Write-Host ""
if ($script:failed -gt 0) { Write-Host "$($script:failed) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All checks passed." -ForegroundColor Green
