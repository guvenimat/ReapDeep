$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$api = "http://localhost:3000/api/v1"
$tag = "in" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$script:failed = 0

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.CookieContainer = New-Object System.Net.CookieContainer
$session = New-Object System.Net.Http.HttpClient($handler)
$plain = New-Object System.Net.Http.HttpClient

function Send($client, [string]$method, [string]$path, [string]$body = $null, [hashtable]$headers = @{ "X-Project-Id" = "1" }) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), "$api$path")
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($body) { $req.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "application/json") }
    $r = $client.SendAsync($req).Result
    $text = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($text) { try { $text | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function S([string]$method, [string]$path, [string]$body = $null) { Send $session $method $path $body }
function K([string]$method, [string]$path, [string]$body = $null) { Send $plain $method $path $body @{ Authorization = "Bearer $readKey"; "Cache-Control" = "no-cache" } }

$login = '{"email":"' + $envValues.ADMIN_EMAIL + '","password":"' + $envValues.ADMIN_PASSWORD + '"}'
if ((S POST "/auth/login" $login).Status -ne 200) { throw "login failed (rate limited? wait a minute)" }

Write-Host "`n1) Test data (tag: $tag)"
$batch = "[{`"event`":`"view_$tag`",`"distinct_id`":`"a_$tag`"},{`"event`":`"view_$tag`",`"distinct_id`":`"b_$tag`"},{`"event`":`"view_$tag`",`"distinct_id`":`"c_$tag`"},{`"event`":`"buy_$tag`",`"distinct_id`":`"a_$tag`"}]"
$content = New-Object System.Net.Http.StringContent(('{"api_key":"pk_dev_local","batch":' + $batch + '}'), [Text.Encoding]::UTF8, "text/plain")
if ([int]$plain.PostAsync("http://localhost:8000/capture", $content).Result.StatusCode -ne 202) { throw "capture failed" }
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep 2; $t = (K POST "/query/trends" "{`"series`":[{`"event`":`"view_$tag`"}]}").Json } until ($t.results[0].total -eq 3 -or (Get-Date) -gt $deadline)
Check "events ingested" ($t.results[0].total -eq 3)

Write-Host "`n2) Saved insights"
$trendQuery = "{`"series`":[{`"event`":`"view_$tag`",`"metric`":`"total`"}],`"filters`":[],`"deduplicate`":false,`"chart`":`"bar`"}"
$funnelQuery = "{`"steps`":[`"view_$tag`",`"buy_$tag`"],`"window_seconds`":86400,`"filters`":[],`"sampling`":1}"
$tr = S POST "/insights" "{`"name`":`"Views $tag`",`"kind`":`"trends`",`"query`":$trendQuery}"
$fu = S POST "/insights" "{`"name`":`"Conversion $tag`",`"kind`":`"funnels`",`"query`":$funnelQuery}"
Check "create trend and funnel insights -> 201" ($tr.Status -eq 201 -and $fu.Status -eq 201 -and $tr.Json.created_by -eq $envValues.ADMIN_EMAIL)
Check "query is stored as JSON" ($tr.Json.query.chart -eq "bar" -and $fu.Json.query.steps[1] -eq "buy_$tag")
Check "read API key can list insights" (@((K GET "/insights").Json.insights | Where-Object { $_.id -eq $tr.Json.id }).Count -eq 1)
Check "read API key cannot save -> 403" ((K POST "/insights" "{`"name`":`"x`",`"kind`":`"trends`",`"query`":{}}").Status -eq 403)
Check "invalid kind -> 400" ((S POST "/insights" '{"name":"x","kind":"sankey","query":{}}').Status -eq 400)
Check "query must be an object -> 400" ((S POST "/insights" '{"name":"x","kind":"trends","query":[1]}').Status -eq 400)
$up = S PUT "/insights/$($tr.Json.id)" "{`"name`":`"Daily views $tag`"}"
Check "rename keeps the query" ($up.Status -eq 200 -and $up.Json.name -eq "Daily views $tag" -and $up.Json.query.chart -eq "bar")
Check "unknown insight -> 404" ((S PUT "/insights/999999" '{"name":"x"}').Status -eq 404)

Write-Host "`n2b) Chart notes"
$noteDay = [DateTime]::UtcNow.AddDays(-3).ToString("yyyy-MM-dd")
$note = S POST "/annotations" ('{"day":"' + $noteDay + '","content":"Campaign launched ' + $tag + '"}')
Check "a member adds a note to a day" ($note.Status -eq 201 -and $note.Json.day -eq $noteDay -and $note.Json.created_by)
$from = [DateTime]::UtcNow.AddDays(-7).ToString("yyyy-MM-dd"); $to = [DateTime]::UtcNow.ToString("yyyy-MM-dd")
$listed = @((K GET "/annotations?from=$from&to=$to").Json.annotations | Where-Object { $_.content -eq "Campaign launched $tag" })
Check "a read key lists the notes of a range" ($listed.Count -eq 1)
$outside = @((S GET "/annotations?from=$($([DateTime]::UtcNow.AddDays(-30)).ToString('yyyy-MM-dd'))&to=$($([DateTime]::UtcNow.AddDays(-10)).ToString('yyyy-MM-dd'))").Json.annotations | Where-Object { $_.content -eq "Campaign launched $tag" })
Check "a range that does not include the day leaves it out" ($outside.Count -eq 0)
Check "a read key cannot add notes -> 403" ((K POST "/annotations" ('{"day":"' + $noteDay + '","content":"x"}')).Status -eq 403)
Check "invalid day -> 400" ((S POST "/annotations" '{"day":"18.11.2026","content":"x"}').Status -eq 400)
Check "empty or longer than 300 characters -> 400" ((S POST "/annotations" ('{"day":"' + $noteDay + '","content":" "}')).Status -eq 400 -and (S POST "/annotations" ('{"day":"' + $noteDay + '","content":"' + ('a' * 301) + '"}')).Status -eq 400)
Check "a range over 800 days -> 400" ((S GET "/annotations?from=2020-01-01&to=2026-01-01").Status -eq 400)
Check "delete -> 204, then 404" ((S DELETE "/annotations/$($note.Json.id)").Status -eq 204 -and (S DELETE "/annotations/$($note.Json.id)").Status -eq 404)

Write-Host "`n2c) Actions"
$act = S POST "/actions" "{`"name`":`"Engaged $tag`",`"description`":`"viewed or bought`",`"events`":[`"view_$tag`",`"buy_$tag`",`"view_$tag`"]}"
Check "a member creates an action; repeated events are kept once" ($act.Status -eq 201 -and @($act.Json.events).Count -eq 2)
Check "the same name again -> 409" ((S POST "/actions" "{`"name`":`"Engaged $tag`",`"events`":[`"x`"]}").Status -eq 409)
Check "an action without events -> 400" ((S POST "/actions" "{`"name`":`"Empty $tag`",`"events`":[]}").Status -eq 400)
Check "a read key lists actions but cannot create them" (@((K GET "/actions").Json.actions | Where-Object { $_.name -eq "Engaged $tag" }).Count -eq 1 -and (K POST "/actions" "{`"name`":`"k`",`"events`":[`"x`"]}").Status -eq 403)
$at = (K POST "/query/trends" "{`"series`":[{`"action`":`"Engaged $tag`"},{`"action`":`"Engaged $tag`",`"metric`":`"unique_users`"}]}").Json.results
Check "a trend series from the action counts both events (4) and 3 persons -> $($at[0].total) / $($at[1].total)" ($at[0].total -eq 4 -and $at[1].total -eq 3 -and $at[0].event -eq "Engaged $tag")
$af = (K POST "/query/funnels" "{`"steps`":[{`"action`":`"Engaged $tag`"},{`"event`":`"buy_$tag`"}]}").Json.results[0].steps
Check "a funnel step from the action: 3 persons enter -> $($af[0].count)" ($af[0].count -eq 3 -and $af[0].event -eq "Engaged $tag")
Check "an unknown action -> 400" ((K POST "/query/trends" "{`"series`":[{`"action`":`"Nope $tag`"}]}").Status -eq 400)
$null = S PUT "/actions/$($act.Json.id)" "{`"name`":`"Engaged $tag`",`"events`":[`"buy_$tag`"]}"
$at2 = (K POST "/query/trends" "{`"series`":[{`"action`":`"Engaged $tag`"}]}").Json.results
Check "changing the action changes the result of the same query (1)" ($at2[0].total -eq 1)
Check "delete -> 204, then the query reports the unknown action" ((S DELETE "/actions/$($act.Json.id)").Status -eq 204 -and (K POST "/query/trends" "{`"series`":[{`"action`":`"Engaged $tag`"}]}").Status -eq 400)

Write-Host "`n2d) Funnel correlation"
function At([int]$minutesAgo) { [DateTime]::UtcNow.AddMinutes(-$minutesAgo).ToString("o") }
function Ev([string]$who, [string]$name, [int]$ago) { "{`"event`":`"$name`",`"distinct_id`":`"$who`",`"timestamp`":`"$(At $ago)`"}" }
function Evp([string]$who, [string]$name, [int]$ago, [string]$segment) {
    "{`"event`":`"$name`",`"distinct_id`":`"$who`",`"timestamp`":`"$(At $ago)`",`"properties`":{`"segment`":`"$segment`"}}"
}
$corr = @()
foreach ($k in 1..3) { $corr += (Evp "cv$k-$tag" "cview_$tag" 10 "pro"); $corr += (Ev "cv$k-$tag" "coupon_$tag" 9); $corr += (Ev "cv$k-$tag" "cbuy_$tag" 8) }
foreach ($k in 1..3) { $corr += (Evp "dv$k-$tag" "cview_$tag" 10 "free"); $corr += (Ev "dv$k-$tag" "faq_$tag" 9) }
$content = New-Object System.Net.Http.StringContent(('{"api_key":"pk_dev_local","batch":[' + ($corr -join ",") + ']}'), [Text.Encoding]::UTF8, "text/plain")
if ([int]$plain.PostAsync("http://localhost:8000/capture", $content).Result.StatusCode -ne 202) { throw "capture failed" }
$corrBody = "{`"steps`":[{`"event`":`"cview_$tag`"},{`"event`":`"cbuy_$tag`"}],`"window_seconds`":86400}"
$deadline = (Get-Date).AddSeconds(40)
do { Start-Sleep 2; $cr = (K POST "/query/funnels/correlation" $corrBody).Json; $fs = (K POST "/query/funnels" $corrBody).Json.results[0].steps } until (($cr.converted + $cr.dropped -eq 6 -and $fs[0].count -eq 6) -or (Get-Date) -gt $deadline)
Check "3 converted (the coupon users) and 3 dropped (the faq readers), as in the funnel itself -> $($cr.converted) / $($cr.dropped)" ($cr.converted -eq 3 -and $cr.dropped -eq 3 -and $fs[1].count -eq 3)
$coupon = @($cr.positive | Where-Object { $_.event -eq "coupon_$tag" })[0]
$faq = @($cr.negative | Where-Object { $_.event -eq "faq_$tag" })[0]
Check "the coupon is more common among converters: all 3 users converted, none dropped" ($coupon -and $coupon.converted_with -eq 3 -and $coupon.dropped_with -eq 0 -and $coupon.odds_ratio -gt 1)
Check "the faq is more common among those who dropped: none converted, all 3 dropped" ($faq -and $faq.converted_with -eq 0 -and $faq.dropped_with -eq 3 -and $faq.odds_ratio -lt 1)
Check "the funnel's own steps are never candidates" (-not (@($cr.positive) + @($cr.negative) | Where-Object { $_.event -in "cview_$tag", "cbuy_$tag" }))
$propBody = "{`"steps`":[{`"event`":`"cview_$tag`"},{`"event`":`"cbuy_$tag`"}],`"window_seconds`":86400,`"event`":`"cview_$tag`",`"property`":`"segment`"}"
$pc = (K POST "/query/funnels/correlation/property" $propBody).Json
$pro = @($pc.values | Where-Object { $_.value -eq "pro" })[0]
$free = @($pc.values | Where-Object { $_.value -eq "free" })[0]
Check "property correlation: pro -> 3 converted, 0 dropped" ($pc.converted -eq 3 -and $pc.dropped -eq 3 -and $pro -and $pro.converted_with -eq 3 -and $pro.dropped_with -eq 0)
Check "property correlation: free -> 0 converted, 3 dropped" ($free -and $free.converted_with -eq 0 -and $free.dropped_with -eq 3)
Check "property correlation reports a p-value" ($null -ne $pro.p_value -and $null -ne $free.p_value)

Write-Host "`n3) Dashboards"
$d = S POST "/dashboards" "{`"name`":`"Board $tag`",`"insight_ids`":[$($tr.Json.id),$($fu.Json.id)]}"
Check "create a dashboard with two insights -> 201" ($d.Status -eq 201 -and @($d.Json.items).Count -eq 2 -and $d.Json.items[0].id -eq $tr.Json.id)
$re = S PUT "/dashboards/$($d.Json.id)" "{`"insight_ids`":[$($fu.Json.id),$($tr.Json.id)]}"
Check "reorder items" ($re.Json.items[0].id -eq $fu.Json.id -and $re.Json.items[1].id -eq $tr.Json.id)
Check "unknown insight id -> 400" ((S PUT "/dashboards/$($d.Json.id)" '{"insight_ids":[999999]}').Status -eq 400)
Check "dashboard list shows the item count" ((((S GET "/dashboards").Json.dashboards) | Where-Object { $_.id -eq $d.Json.id }).items -eq 2)

Write-Host "`n4) Dashboard in the browser"
$chrome = if ($env:OS -eq 'Windows_NT') { "$env:ProgramFiles\Google\Chrome\Application\chrome.exe" } else { "/usr/bin/google-chrome" }
$temp = if ($env:TEMP) { $env:TEMP } else { [IO.Path]::GetTempPath() }
$port = 9445; $profileDir = Join-Path $temp "rd-insights-$tag"
Start-Process $chrome -ArgumentList @("--headless=new", "--disable-gpu", "--no-first-run", "--remote-debugging-port=$port", "--user-data-dir=$profileDir", "--window-size=1400,900", "about:blank")
$deadline = (Get-Date).AddSeconds(15)
do { Start-Sleep -Milliseconds 300; try { $null = Invoke-RestMethod "http://127.0.0.1:$port/json/version"; $up = $true } catch { $up = $false } } until ($up -or (Get-Date) -gt $deadline)
$target = Invoke-RestMethod -Method Put "http://127.0.0.1:$port/json/new?$([uri]::EscapeDataString('http://localhost:3000/'))"
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ws.ConnectAsync([uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
$script:cdpId = 0
function Eval([string]$js) {
    $id = ++$script:cdpId
    $msg = @{ id = $id; method = "Runtime.evaluate"; params = @{ expression = $js; awaitPromise = $true; returnByValue = $true } } | ConvertTo-Json -Depth 5 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
    $ws.SendAsync((New-Object 'ArraySegment[byte]' -ArgumentList @(, $bytes)), 'Text', $true, [Threading.CancellationToken]::None).Wait()
    $buf = New-Object byte[] 4194304
    while ($true) {
        $ms = New-Object IO.MemoryStream
        do { $r = $ws.ReceiveAsync((New-Object 'ArraySegment[byte]' -ArgumentList @(, $buf)), [Threading.CancellationToken]::None).Result; $ms.Write($buf, 0, $r.Count) } until ($r.EndOfMessage)
        $resp = [Text.Encoding]::UTF8.GetString($ms.ToArray()) | ConvertFrom-Json
        if ($resp.id -eq $id) { return $resp.result.result.value }
    }
}
function WaitJs([string]$js, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { Start-Sleep -Milliseconds 300; $v = Eval $js } until ($v -or (Get-Date) -gt $deadline)
    return $v
}
$null = WaitJs "document.readyState === 'complete'"
$null = Eval "fetch('/api/v1/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({email:'$($envValues.ADMIN_EMAIL)',password:'$($envValues.ADMIN_PASSWORD)'})}).then(r=>r.status)"
$null = Eval "location.hash = '#/dashboards/$($d.Json.id)?range=24h'; location.reload(); true"
Start-Sleep 1
$ready = WaitJs "(function(){const t=[...document.querySelectorAll('[data-testid=dashboard-tile]')];return t.length===2&&t.every(x=>x.querySelector('svg'))&&!document.querySelector('.stale')})()"
$diag = Eval "(function(){const t=[...document.querySelectorAll('[data-testid=dashboard-tile]')];return t.length+' tiles, svg '+t.map(x=>x.querySelectorAll('svg').length).join('/')+', stale '+document.querySelectorAll('.stale').length})()"
Check "dashboard page renders both tiles with charts ($diag)" $ready
Check "dashboard title" ((Eval "document.querySelector('[data-testid=dashboard-name]')?.textContent") -eq "Board $tag")
Check "funnel tile first, with 2 funnel bars" ((Eval "document.querySelectorAll('[data-testid=dashboard-tile]')[0].querySelectorAll('path[fill]').length") -ge 2)
Check "trend tile shows the total (3)" ((Eval "document.querySelectorAll('[data-testid=dashboard-tile]')[1].querySelector('[data-testid=tile-total]')?.textContent") -eq "3")
$href = Eval "document.querySelectorAll('[data-testid=dashboard-tile]')[1].querySelector('a').getAttribute('href')"
Check "tile title opens the insight in the Trends tab" ($href -cmatch "^#/trends\?" -and $href -match "insight=$($tr.Json.id)" -and $href -match "range=24h")
$null = Eval "location.hash = '$href'; true"
$saved = WaitJs "document.querySelector('[data-testid=insight-name]')?.textContent"
Check "Trends tab shows the saved insight name and chart" ($saved -eq "Daily views $tag" -and (WaitJs "!!document.querySelector('[data-testid=trends-chart] path[fill]')"))

$null = Eval "location.hash = '#/trends?range=24h&q=' + encodeURIComponent(JSON.stringify({series:[{event:'buy_$tag',metric:'unique_users'}],filters:[],deduplicate:false,chart:'line'})); true"
$null = WaitJs "!!document.querySelector('[aria-label=`"Insight name`"]')"
$typed = Eval "(function(){const i=document.querySelector('[aria-label=`"Insight name`"]');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(i,'Buyers $tag');i.dispatchEvent(new Event('input',{bubbles:true}));return true})()"
Start-Sleep -Milliseconds 400
$null = Eval "document.querySelector('[data-testid=save-insight-button]').click(); true"
$savedName = WaitJs "document.querySelector('[data-testid=insight-name]')?.textContent"
Check "saving from the Trends tab stores the insight and switches to it" ($savedName -eq "Buyers $tag" -and (Eval "location.hash") -match "insight=\d+")
$buyers = (S GET "/insights").Json.insights | Where-Object { $_.name -eq "Buyers $tag" }
Check "saved query matches the editor state" ($buyers.query.series[0].metric -eq "unique_users" -and $buyers.query.series[0].event -eq "buy_$tag")

$ws.Dispose()
Invoke-RestMethod "http://127.0.0.1:$port/json/close/$($target.id)" | Out-Null
try { $bt = (Invoke-RestMethod "http://127.0.0.1:$port/json/version").webSocketDebuggerUrl; $bws = New-Object System.Net.WebSockets.ClientWebSocket; $bws.ConnectAsync([uri]$bt, [Threading.CancellationToken]::None).Wait(); $b = [Text.Encoding]::UTF8.GetBytes('{"id":1,"method":"Browser.close"}'); $bws.SendAsync((New-Object 'ArraySegment[byte]' -ArgumentList @(, $b)), 'Text', $true, [Threading.CancellationToken]::None).Wait() } catch {}
Start-Sleep 1; Remove-Item $profileDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n5) Deleting"
Check "deleting an insight removes it from the dashboard" ((S DELETE "/insights/$($tr.Json.id)").Status -eq 204 -and @((S GET "/dashboards/$($d.Json.id)").Json.items).Count -eq 1)
Check "delete dashboard -> 204, then 404" ((S DELETE "/dashboards/$($d.Json.id)").Status -eq 204 -and (S GET "/dashboards/$($d.Json.id)").Status -eq 404)
Check "insights survive dashboard deletion" (@((S GET "/insights").Json.insights | Where-Object { $_.id -eq $fu.Json.id }).Count -eq 1)
$null = S DELETE "/insights/$($fu.Json.id)"
if ($buyers) { $null = S DELETE "/insights/$($buyers.id)" }

if ($script:failed -eq 0) { Write-Host "`nPASSED: all insight and dashboard checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
