$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$readKey = (Initialize-LocalEnv).READ_API_KEY
$tag = "dt" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$anonA = "anonA_$tag"; $user = "user_$tag"; $anonB = "anonB_$tag"
$chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
$ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"
$dash = "http://localhost:3000"
$script:failed = 0
$http = New-Object System.Net.Http.HttpClient

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}
function Fill([string]$s) { $s.Replace('__TAG__', $tag).Replace('__A__', $anonA).Replace('__U__', $user).Replace('__B__', $anonB) }
function Capture([string]$batchJson) {
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "http://localhost:8000/capture")
    $req.Headers.TryAddWithoutValidation("User-Agent", $ua) | Out-Null
    $req.Content = New-Object System.Net.Http.StringContent((Fill ('{"api_key":"pk_dev_local","batch":' + $batchJson + '}')), [Text.Encoding]::UTF8, "text/plain")
    if ([int]$http.SendAsync($req).Result.StatusCode -ne 202) { throw "capture failed" }
}
function Api([string]$path, [string]$body) {
    $req = New-Object System.Net.Http.HttpRequestMessage($(if ($body) { [System.Net.Http.HttpMethod]::Post } else { [System.Net.Http.HttpMethod]::Get }), "$dash$(Fill $path)")
    $req.Headers.TryAddWithoutValidation("Authorization", "Bearer $readKey") | Out-Null
    if ($body) { $req.Content = New-Object System.Net.Http.StringContent((Fill $body), [Text.Encoding]::UTF8, "application/json") }
    $http.SendAsync($req).Result.Content.ReadAsStringAsync().Result | ConvertFrom-Json
}

$cdpPort = 9333
$cdpProfile = Join-Path $env:TEMP "rd-dash-$tag"
Start-Process $chrome -ArgumentList @("--headless=new", "--disable-gpu", "--no-first-run", "--remote-debugging-port=$cdpPort",
    "--user-data-dir=$cdpProfile", "--window-size=1400,900", "about:blank")
$deadline = (Get-Date).AddSeconds(15)
do { Start-Sleep -Milliseconds 300; try { $browserWs = (Invoke-RestMethod "http://127.0.0.1:$cdpPort/json/version").webSocketDebuggerUrl } catch {} } until ($browserWs -or (Get-Date) -gt $deadline)
if (-not $browserWs) { throw "Chrome DevTools did not start" }

$script:cdpId = 0
function CdpCall($ws, [string]$method, $params) {
    $id = ++$script:cdpId
    $msg = @{ id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 5 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
    $ws.SendAsync((New-Object 'ArraySegment[byte]' -ArgumentList @(, $bytes)), 'Text', $true, [Threading.CancellationToken]::None).Wait()
    $buf = New-Object byte[] 1048576
    while ($true) {
        $ms = New-Object IO.MemoryStream
        do {
            $r = $ws.ReceiveAsync((New-Object 'ArraySegment[byte]' -ArgumentList @(, $buf)), [Threading.CancellationToken]::None).Result
            $ms.Write($buf, 0, $r.Count)
        } until ($r.EndOfMessage)
        $resp = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
        if ($resp.id -eq $id) { return $resp }
    }
}

function Dom([string]$hash, [string]$readyJs) {
    $target = Invoke-RestMethod -Method Put "http://127.0.0.1:$cdpPort/json/new?$([uri]::EscapeDataString("$dash/$hash"))"
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.ConnectAsync([uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    try {
        $cond = "document.readyState === 'complete' && !!($readyJs) && !document.querySelector('.stale')"
        $deadline = (Get-Date).AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 250
            $ok = (CdpCall $ws "Runtime.evaluate" @{ expression = "Boolean($cond)"; returnByValue = $true }).result.result.value
        } until ($ok -or (Get-Date) -gt $deadline)
        if (-not $ok) { Write-Host "  (timeout: $hash)" -ForegroundColor Yellow }
        return (CdpCall $ws "Runtime.evaluate" @{ expression = "document.documentElement.outerHTML"; returnByValue = $true }).result.result.value
    }
    finally {
        $ws.Dispose()
        Invoke-RestMethod "http://127.0.0.1:$cdpPort/json/close/$($target.id)" | Out-Null
    }
}
$hasTable = { param($id) "document.querySelector('[data-testid=`"$id`"] tbody tr')" }
function Q([string]$json) { [uri]::EscapeDataString((Fill $json)) }
function Section([string]$html, [string]$id) {
    $i = $html.IndexOf("data-testid=""$id""")
    if ($i -lt 0) { return "" }
    $j = $html.IndexOf("data-testid=", $i + 20)
    if ($j -lt 0) { $j = $html.Length }
    $html.Substring($i, $j - $i)
}

$null = Dom "" "localStorage.setItem('reapdeep.apiKey', '$readKey') || true"

Write-Host "`n1) Test data (tag: $tag)"
Capture '[{"event":"page___TAG__","distinct_id":"__A__"},{"event":"page___TAG__","distinct_id":"__A__"},{"event":"step1___TAG__","distinct_id":"__A__","properties":{"$current_url":"https://example.com/products/1"}}]'
Start-Sleep -Milliseconds 300
Capture '[{"event":"$identify","distinct_id":"__U__","properties":{"$anon_distinct_id":"__A__","$set":{"email":"__U__@example.com","plan":"pro"}}}]'
Start-Sleep -Milliseconds 300
Capture '[{"event":"page___TAG__","distinct_id":"__U__"},{"event":"step2___TAG__","distinct_id":"__U__"},{"event":"step3___TAG__","distinct_id":"__U__","properties":{"amount":499}}]'
Capture '[{"event":"step1___TAG__","distinct_id":"__B__"}]'

Write-Host "  Waiting for StarRocks ingestion and identity sync..."
$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep 2
    $t = Api "/api/v1/query/trends" '{"series":[{"event":"page___TAG__","metric":"unique_users"},{"event":"step3___TAG__"}]}'
    $p = Api "/api/v1/persons?search=__U__&identified=true"
    $ready = $t.results[0].total -eq 1 -and $t.results[1].total -eq 1 -and @($p.persons).Count -eq 1
} until ($ready -or (Get-Date) -gt $deadline)
Check "data ready" $ready
$personId = $p.persons[0].id

Write-Host "`n2) Connection status and nginx proxy"
Check "/healthz through nginx" ((Invoke-WebRequest "$dash/healthz" -UseBasicParsing).Content -match '"postgres":"ok".*"starrocks":"ok"')
$html = Dom "#/events" "document.querySelector('.status.ok')"
Check "top bar shows 'Connected'" ((Section $html "connection-status") -match "Connected")

Write-Host "`n3) Trends tab"
$q = Q '{"series":[{"event":"page___TAG__","metric":"total"},{"event":"page___TAG__","metric":"unique_users"}],"filters":[],"deduplicate":false,"chart":"line"}'
$html = Dom "#/trends?range=24h&q=$q" (& $hasTable "trends-table")
$chart = Section $html "trends-chart"; $table = Section $html "trends-table"
Check "SVG line chart rendered (2 series)" (([regex]::Matches($chart, 'class="line"')).Count -eq 2)
Check "legend with 2 series" (([regex]::Matches($chart, '<li>')).Count -eq 2)
Check "table: total events = 3 (2 anonymous + 1 after identify)" ($table -match 'Total events</td><td class="num strong">3</td>')
Check "table: unique users = 1 (anonymous + identified merged)" ($table -match 'Unique users</td><td class="num strong">1</td>')
$q = Q '{"series":[{"event":"page___TAG__","metric":"total"}],"filters":[],"deduplicate":false,"chart":"bar"}'
$chart = Section (Dom "#/trends?range=24h&q=$q" "document.querySelector('[data-testid=trends-chart] path[fill]')") "trends-chart"
Check "bar chart mode (rounded column path)" ($chart -match '<path fill="var\(--series-1\)" d="M[\d.]+,[\d.]+V[\d.]+Q')

Write-Host "`n4) Funnels tab"
$q = Q '{"steps":["step1___TAG__","step2___TAG__","step3___TAG__"],"window_seconds":604800,"filters":[]}'
$html = Dom "#/funnels?range=7d&q=$q" (& $hasTable "funnel-table")
$chart = Section $html "funnel-chart"; $table = Section $html "funnel-table"
Check "SVG funnel bars (all 3 steps filled: 2, 1, 1)" (([regex]::Matches($chart, '<path fill="var\(--series-1\)"')).Count -eq 3)
$counts = [regex]::Matches($table, '<td class="num strong">(\d+)</td>') | ForEach-Object { $_.Groups[1].Value }
Check "table: persons per step 2/1/1 -> $($counts -join '/')" (($counts -join '/') -eq "2/1/1")
Check "table: step 3 conversion 50%" ($table -match '>50%<')

Write-Host "`n5) Events tab"
$q = Q '{"event":"","distinct_id":"","person_id":"","limit":50}'
$table = Section (Dom "#/events?range=7d&q=$q" (& $hasTable "events-table")) "events-table"
Check "table contains this run's events (anonymous and identified)" ($table -match "anonA_$tag" -and $table -match "user_$tag" -and $table -match "step3_$tag")
Check "person column links to the Persons tab" ($table -match ('href="#/persons/' + $personId + '"'))
Check "page and browser columns filled" ($table -match '/products/1' -and $table -match 'Chrome / Windows')

Write-Host "`n6) Persons tab"
$html = Dom "#/persons" (& $hasTable "persons-table")
Check "person list (identified) contains this user" ((Section $html "persons-table") -match "$user@example.com")
$html = Dom "#/persons/$personId" "document.querySelector('[data-testid=person-distinct-ids] li') && document.querySelector('[data-testid=person-detail] [data-testid=events-table] tbody tr')"
$ids = Section $html "person-distinct-ids"
Check "person detail: anonymous + identified distinct_ids" ($ids -match $anonA -and $ids -match $user)
Check "person detail: properties (plan: pro)" ($ids -match '<pre class="json">[^<]*"plan": "pro"')
$evTable = Section $html "events-table"
$rows = ([regex]::Matches($evTable, '<tr class="clickable')).Count
Check "person event history: anonymous + after identify = 7 events -> $rows" ($rows -eq 7)

$bws = New-Object System.Net.WebSockets.ClientWebSocket; $bws.ConnectAsync([uri]$browserWs, [Threading.CancellationToken]::None).Wait(); CdpCall $bws "Browser.close" @{} | Out-Null
Start-Sleep 1; Remove-Item $cdpProfile -Recurse -Force -ErrorAction SilentlyContinue
if ($script:failed -eq 0) { Write-Host "`nPASSED: the dashboard renders real data on every tab" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
