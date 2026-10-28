$ErrorActionPreference = "Continue"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
$log = Join-Path $PSScriptRoot "run-local.log"
Start-Transcript -Path $log -Force | Out-Null

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

try {
    Step "Checking Docker"
    docker version --format "Client {{.Client.Version}} / Server {{.Server.Version}}"
    if ($LASTEXITCODE -ne 0) { throw "Docker is not running. Start Docker Desktop." }
    docker info --format "Memory available to Docker: {{.MemTotal}} bytes, CPUs: {{.NCPU}}"

    Step "Preparing .env"
    $null = Initialize-LocalEnv

    Step "docker compose up -d (images are downloaded on first run, this can take a few minutes)"
    docker compose up -d
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed." }

    Step "Waiting for init containers (up to 6 minutes)"
    $deadline = (Get-Date).AddMinutes(6)
    while ((Get-Date) -lt $deadline) {
        $state = docker inspect -f "{{.State.Status}} {{.State.ExitCode}}" reapdeep-starrocks-init 2>$null
        if ($state -like "exited 0") { Write-Host "starrocks-init finished."; break }
        if ($state -like "exited *") { docker compose logs starrocks-init; throw "starrocks-init exited with an error: $state" }
        Start-Sleep -Seconds 10
        Write-Host "  waiting... ($state)"
    }
    if ((Get-Date) -ge $deadline) { docker compose logs --tail 50 starrocks; throw "StarRocks was not ready within 6 minutes." }

    Step "Container status"
    docker compose ps -a

    Step "Routine Load status"
    docker compose exec -T starrocks mysql "-h127.0.0.1" -P9030 -uroot -e "SHOW ROUTINE LOAD FROM analytics\G" |
        Select-String -Pattern "Name:|State:|ReasonOfStateChanged|OtherMsg"

    Step "Smoke test: Kafka -> StarRocks"
    $eventId = "smoke-" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $now = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss")
    $msg = '{"project_id":1,"event":"smoke_test","timestamp":"' + $now + '","distinct_id":"anon_smoke","event_id":"' + $eventId + '","session_id":null,"ip":"127.0.0.1","properties":{"$lib":"smoke"},"inserted_at":"' + $now + '"}'
    $msgFile = Join-Path $env:TEMP "reapdeep-smoke-$eventId.json"
    [System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding($false)))
    cmd /c "docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:29092 --topic events < `"$msgFile`""
    Remove-Item $msgFile -Force -ErrorAction SilentlyContinue
    Write-Host "Written to Kafka: $eventId"

    $ok = $false
    for ($i = 1; $i -le 12; $i++) {
        Start-Sleep -Seconds 5
        $count = docker compose exec -T starrocks mysql "-h127.0.0.1" -P9030 -uroot -N -e "SELECT count(*) FROM analytics.events WHERE event_id = '$eventId'" 2>$null
        if ("$count".Trim() -eq "1") { Write-Host "PASSED: event queryable within $($i*5)s." -ForegroundColor Green; $ok = $true; break }
    }
    if (-not $ok) {
        Write-Host "FAILED: event not visible within 60s." -ForegroundColor Red
        docker compose exec -T starrocks mysql "-h127.0.0.1" -P9030 -uroot -e "SHOW ROUTINE LOAD FROM analytics\G"
    }
}
catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
}
finally {
    Step "Done"
    Stop-Transcript | Out-Null
}
