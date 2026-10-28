$ErrorActionPreference = "Stop"
$sdk = Split-Path $PSScriptRoot -Parent
$compose = Join-Path $sdk "..\..\deploy\local\docker-compose.yml"
$chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
$tag = "e2e" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$profileDir = Join-Path $env:TEMP "reapdeep-e2e-chrome-$tag"
$url = "http://localhost:5500/products?utm_source=e2e&e2e=$tag"

$server = Start-Process node -ArgumentList "demo/serve.mjs" -WorkingDirectory $sdk -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 1
    Write-Host "Opening browser: $url"
    Start-Process $chrome -Wait -ArgumentList @("--headless=new", "--disable-gpu", "--no-first-run", "--user-data-dir=$profileDir",
        "--virtual-time-budget=5000", "--screenshot=$profileDir\page.png", $url)

    $user = "e2e_user_$tag"
    $sql = "SELECT event, distinct_id, pathname, utm_source, browser, os, device_type, properties FROM analytics.events " +
           "WHERE project_id = 1 AND (distinct_id = '$user' OR distinct_id = (SELECT get_json_string(properties, '$.`$anon_distinct_id') FROM analytics.events " +
           "WHERE project_id = 1 AND distinct_id = '$user' AND event = '`$identify' LIMIT 1)) ORDER BY event_id"
    for ($i = 1; $i -le 12; $i++) {
        Start-Sleep -Seconds 5
        $rows = docker compose -f $compose exec -T starrocks mysql "-h127.0.0.1" -P9030 -uroot -N -e $sql
        if (@($rows).Count -ge 5) { break }
    }
    $rows
    $count = @($rows | Where-Object { $_ }).Count
    if ($count -eq 5) { Write-Host "PASSED: 5 events in StarRocks ($($i*5)s)" -ForegroundColor Green }
    else { Write-Host "FAILED: expected 5 events, got $count" -ForegroundColor Red; exit 1 }
}
finally {
    Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue
}
