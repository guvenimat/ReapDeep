$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$readKey = (Initialize-LocalEnv).READ_API_KEY
$tag = "qt" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$anonA = "anonA_$tag"; $user = "user_$tag"; $anonB = "anonB_$tag"
$chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"
$firefox = "Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0"
$api = "http://localhost:8001"
$script:failed = 0
$http = New-Object System.Net.Http.HttpClient

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}

$t10 = (Get-Date).ToUniversalTime().AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ssZ")
function Fill([string]$s) { $s.Replace('__T10__', $t10).Replace('__TAG__', $tag).Replace('__A__', $anonA).Replace('__U__', $user).Replace('__B__', $anonB) }

function Capture([string]$ua, [string]$batchJson) {
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "http://localhost:8000/capture")
    $req.Headers.TryAddWithoutValidation("User-Agent", $ua) | Out-Null
    $req.Content = New-Object System.Net.Http.StringContent((Fill ('{"api_key":"pk_dev_local","batch":' + $batchJson + '}')), [Text.Encoding]::UTF8, "text/plain")
    $r = $http.SendAsync($req).Result
    if ([int]$r.StatusCode -ne 202) { throw "capture HTTP $([int]$r.StatusCode)" }
}

function Call([string]$method, [string]$path, [string]$body = $null, [string]$key = $readKey) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), "$api$path")
    if ($key) { $req.Headers.TryAddWithoutValidation("Authorization", "Bearer $key") | Out-Null }
    if ($body) { $req.Content = New-Object System.Net.Http.StringContent((Fill $body), [Text.Encoding]::UTF8, "application/json") }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = $http.SendAsync($req).Result
    $text = $r.Content.ReadAsStringAsync().Result
    $sw.Stop()
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($text) { $text | ConvertFrom-Json } else { $null }); Ms = $sw.Elapsed.TotalMilliseconds; Raw = $text }
}

function Trend([string]$body) { (Call POST "/api/v1/query/trends" $body).Json.results }
$qtFilter = '{"property":"qt","op":"eq","value":"__TAG__"}'

Write-Host "`n1) Authentication"
Check "invalid key -> 401" ((Call GET "/api/v1/events" -key "rk_wrong").Status -eq 401)
Check "missing key -> 401" ((Call GET "/api/v1/events" -key "").Status -eq 401)
Check "valid key -> 200" ((Call GET "/api/v1/events?limit=1").Status -eq 200)
Check "invalid property name -> 400" ((Call POST "/api/v1/query/trends" '{"series":[{"event":"x"}],"filters":[{"property":"a'' OR 1=1 --","op":"eq","value":"1"}]}').Status -eq 400)

Write-Host "`n2) Sending test data (tag: $tag)"
Capture $chrome @'
[{"event":"$pageview","distinct_id":"__A__","event_id":"__TAG__-pv1","properties":{"qt":"__TAG__","$current_url":"https://example.com/products/1"}},
 {"event":"$pageview","distinct_id":"__A__","event_id":"__TAG__-pv1","properties":{"qt":"__TAG__","$current_url":"https://example.com/products/1"}},
 {"event":"$pageview","distinct_id":"__A__","event_id":"__TAG__-pv2","properties":{"qt":"__TAG__","$current_url":"https://example.com/products/2"}},
 {"event":"view_product","distinct_id":"__A__","timestamp":"__T10__","properties":{"qt":"__TAG__","product":"Pro plan"}}]
'@
Start-Sleep -Milliseconds 500
Capture $chrome '[{"event":"$identify","distinct_id":"__U__","properties":{"qt":"__TAG__","$anon_distinct_id":"__A__","$set":{"email":"__U__@example.com","plan":"pro"}}}]'
Start-Sleep -Milliseconds 500
Capture $chrome @'
[{"event":"$pageview","distinct_id":"__U__","properties":{"qt":"__TAG__","$current_url":"https://example.com/cart"}},
 {"event":"add_to_cart","distinct_id":"__U__","properties":{"qt":"__TAG__"}},
 {"event":"purchase","distinct_id":"__U__","properties":{"qt":"__TAG__","amount":499}},
 {"event":"order_qt","distinct_id":"__U__","properties":{"qt":"__TAG__","amount":499}}]
'@
Capture $firefox @'
[{"event":"$pageview","distinct_id":"__B__","properties":{"qt":"__TAG__","$current_url":"https://example.com/"}},
 {"event":"view_product","distinct_id":"__B__","properties":{"qt":"__TAG__","product":"Pro plan"}},
 {"event":"order_qt","distinct_id":"__B__","properties":{"qt":"__TAG__","amount":101}},
 {"event":"order_qt","distinct_id":"__B__","properties":{"qt":"__TAG__","amount":"n/a"}}]
'@

Write-Host "  Waiting for StarRocks ingestion and identity sync..."
$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep 2
    $ev = (Call GET "/api/v1/events?distinct_id=$anonA&limit=10").Json.events
    $all = Trend ('{"series":[{"event":"$pageview"}],"filters":[' + $qtFilter + ']}')
    $pp = Trend ('{"series":[{"event":"$pageview","metric":"unique_users"}],"filters":[' + $qtFilter + ',{"scope":"person","property":"plan","op":"eq","value":"pro"}]}')
    $ready = $ev.Count -ge 4 -and @($ev | Where-Object { $_.resolved_person_id -ne $anonA }).Count -eq $ev.Count -and $all[0].total -eq 5 -and $pp[0].total -eq 1
} until ($ready -or (Get-Date) -gt $deadline)
Check "data ready (events loaded, identity mapping and person synced)" $ready
$personId = $ev[0].resolved_person_id

Write-Host "`n3) Trends"
$r = Trend ('{"series":[{"event":"$pageview"}],"filters":[' + $qtFilter + ']}')
Check "total pageviews = 5 (including duplicate) -> $($r[0].total)" ($r[0].total -eq 5)
$r = Trend ('{"series":[{"event":"$pageview"}],"filters":[' + $qtFilter + '],"deduplicate":true}')
Check "deduplicate: total pageviews = 4 -> $($r[0].total)" ($r[0].total -eq 4)
$r = Trend ('{"series":[{"event":"$pageview","metric":"unique_users"}],"filters":[' + $qtFilter + ']}')
Check "unique_users = 2 (anonA+user is one person, anonB) -> $($r[0].total)" ($r[0].total -eq 2)

$r = Trend ('{"series":[{"event":"$pageview"},{"event":"$pageview","metric":"unique_users"}],"filters":[' + $qtFilter + ',{"property":"$browser","op":"eq","value":"Chrome"}],"deduplicate":true}')
Check "Chrome: 2 anonymous + 1 post-identify pageview = 3 events -> $($r[0].total)" ($r[0].total -eq 3)
Check "Chrome: those 3 events are 1 unique person (anonymous + identified merged) -> $($r[1].total)" ($r[1].total -eq 1)

$r = Trend ('{"series":[{"event":"$pageview"}],"filters":[' + $qtFilter + ',{"property":"browser","op":"eq","value":"Firefox"}]}')
Check "column filter (browser = Firefox) = 1 -> $($r[0].total)" ($r[0].total -eq 1)
$r = Trend ('{"series":[{"event":"$pageview"}],"filters":[' + $qtFilter + ',{"property":"$current_url","op":"contains","value":"/products/"}]}')
Check "contains filter (url contains /products/) = 3 -> $($r[0].total)" ($r[0].total -eq 3)
$r = Trend ('{"series":[{"event":"$pageview","metric":"unique_users"}],"filters":[' + $qtFilter + ',{"scope":"person","property":"plan","op":"eq","value":"pro"}]}')
Check "person property filter (plan = pro) = 1 person -> $($r[0].total)" ($r[0].total -eq 1)

$r = Trend ('{"series":[{"event":"$pageview"}],"filters":[' + $qtFilter + '],"breakdown":{"property":"$browser"}}')
$bd = ($r | ForEach-Object { "$($_.breakdown_value)=$($_.total)" }) -join ", "
Check "breakdown `$browser: Chrome=4, Firefox=1 -> $bd" ($bd -eq "Chrome=4, Firefox=1")
$today = ($r[0].data | Where-Object { $_.count -gt 0 })
Check "time bucket in project time zone (today, Europe/Istanbul) -> $($today.bucket)" ($today.bucket -eq (Get-Date).ToUniversalTime().AddHours(3).ToString("yyyy-MM-dd'T'00:00:00"))

$d2 = (Get-Date).ToUniversalTime().AddHours(3).AddDays(-2).ToString("yyyy-MM-dd"); $d0 = (Get-Date).ToUniversalTime().AddHours(3).ToString("yyyy-MM-dd")
$r = Trend ('{"series":[{"event":"$pageview"}],"filters":[' + $qtFilter + '],"date_from":"' + $d2 + '","date_to":"' + $d0 + '"}')
Check "date_from/date_to ($d2 -> $d0, inclusive): 3 buckets, total 5 -> $($r[0].data.Count) buckets, $($r[0].total)" ($r[0].data.Count -eq 3 -and $r[0].total -eq 5)
$r = Trend ('{"series":[{"event":"$pageview"}],"filters":[' + $qtFilter + '],"date_from":"' + $d2 + '","date_to":"' + $d2 + '"}')
Check "date range without data -> total 0 -> $($r[0].total)" ($r[0].total -eq 0)

Write-Host "`n3b) Property metrics, formulas and comparison"
$orders = '{"series":[{"event":"order_qt","metric":"sum","property":"amount"},{"event":"order_qt","metric":"avg","property":"amount"},{"event":"order_qt","metric":"min","property":"amount"},{"event":"order_qt","metric":"max","property":"amount"},{"event":"order_qt","metric":"unique_users"}],"filters":[' + $qtFilter + '],"formula":"A / E"}'
$r = Trend $orders
$byMetric = @{}; foreach ($x in $r) { $byMetric[$x.metric] = $x.total }
Check "sum/avg/min/max of amount = 600/300/101/499 (non-numeric value ignored) -> $($byMetric.sum)/$($byMetric.avg)/$($byMetric.min)/$($byMetric.max)" ($byMetric.sum -eq 600 -and $byMetric.avg -eq 300 -and $byMetric.min -eq 101 -and $byMetric.max -eq 499)
Check "formula A / E (sum / unique users) = 300 -> $($byMetric.formula)" ($byMetric.formula -eq 300)
$formulaRow = @($r | Where-Object { $_.metric -eq "formula" })[0]
Check "formula result is labeled with its text and the series letters are returned" ($formulaRow.event -eq "A / E" -and (@($r | Where-Object { $_.series -eq "E" }).Count -eq 1))
$r = Trend ('{"series":[{"event":"order_qt","metric":"sum","property":"amount"}],"filters":[' + $qtFilter + '],"compare":true}')
$prev = @($r | Where-Object { $_.compare -eq "previous" })[0]
Check "compare: previous period returned with the same bucket count and total 0" ($null -ne $prev -and $prev.total -eq 0 -and $prev.data.Count -eq @($r | Where-Object { -not $_.compare })[0].data.Count)
Check "formula with an unknown series -> 400" ((Call POST "/api/v1/query/trends" '{"series":[{"event":"x"}],"formula":"A / B"}').Status -eq 400)
Check "property metric without a property -> 400" ((Call POST "/api/v1/query/trends" '{"series":[{"event":"x","metric":"avg"}]}').Status -eq 400)

Write-Host "`n4) Funnels"
$f = (Call POST "/api/v1/query/funnels" ('{"steps":[{"event":"view_product"},{"event":"add_to_cart"},{"event":"purchase"}],"filters":[' + $qtFilter + ']}')).Json.results[0].steps
$counts = ($f | ForEach-Object { $_.count }) -join "/"
Check "steps 2/1/1 (anonymous step 1 -> identify -> identified steps 2 and 3 as one person) -> $counts" ($counts -eq "2/1/1")
Check "conversion: step 3 / step 1 = 0.5 -> $($f[2].conversion_from_first)" ($f[2].conversion_from_first -eq 0.5)
$f = (Call POST "/api/v1/query/funnels" ('{"steps":[{"event":"view_product"},{"event":"purchase"}],"filters":[' + $qtFilter + '],"breakdown":{"property":"$browser"}}')).Json.results
$fb = ($f | ForEach-Object { "$($_.breakdown_value)=$(($_.steps | ForEach-Object { $_.count }) -join '/')" } | Sort-Object) -join ", "
Check "funnel breakdown `$browser: Chrome=1/1, Firefox=1/0 -> $fb" ($fb -eq "Chrome=1/1, Firefox=1/0")
$f = (Call POST "/api/v1/query/funnels" ('{"steps":[{"event":"view_product"},{"event":"purchase"}],"filters":[' + $qtFilter + '],"window_seconds":60}')).Json.results[0].steps
Check "60s window: purchase (10 min later) is outside the window -> 2/0 -> $(($f | ForEach-Object { $_.count }) -join '/')" ((($f | ForEach-Object { $_.count }) -join '/') -eq "2/0")

$fr = (Call POST "/api/v1/query/funnels" ('{"steps":[{"event":"view_product"},{"event":"add_to_cart"},{"event":"purchase"}],"filters":[' + $qtFilter + '],"sampling":1}')).Json
Check "sampling=1 equals the exact funnel (2/1/1) and is reported -> $(($fr.results[0].steps | ForEach-Object { $_.count }) -join '/'), sampling $($fr.sampling)" (((($fr.results[0].steps | ForEach-Object { $_.count }) -join '/') -eq "2/1/1") -and $fr.sampling -eq 1)
Check "invalid sampling -> 400" ((Call POST "/api/v1/query/funnels" '{"steps":[{"event":"a"},{"event":"b"}],"sampling":1.5}').Status -eq 400)

Write-Host "`n5) Events / Persons / Metadata"
$ev = (Call GET "/api/v1/events?person_id=$personId&limit=50").Json.events
Check "events by person_id: anonA + user events together -> $($ev.Count)" ($ev.Count -eq 9 -and ($ev.distinct_id | Sort-Object -Unique).Count -eq 2)
$p = (Call GET "/api/v1/persons/$personId").Json
Check "person detail: distinct_ids [$($p.distinct_ids -join ', ')], email=$($p.properties.email)" (($p.distinct_ids -contains $anonA) -and ($p.distinct_ids -contains $user) -and $p.properties.email -eq "$user@example.com")
Check "person search (email)" (@((Call GET "/api/v1/persons?search=$user").Json.persons | Where-Object { $_.id -eq $personId }).Count -eq 1)
Check "unknown person -> 404" ((Call GET "/api/v1/persons/00000000-0000-0000-0000-000000000000").Status -eq 404)
Capture $chrome '[{"event":"defs___TAG__","distinct_id":"__A__","properties":{"n___TAG__":4.5,"b___TAG__":true,"d___TAG__":"2026-10-29T10:00:00Z","s___TAG__":"x","$current_url":"https://example.com/"}},{"event":"$set","distinct_id":"__U__","properties":{"$set":{"pp___TAG__":12}}}]'
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep 2
    $m = (Call GET "/api/v1/metadata").Json
    $types = @{}; foreach ($d in @($m.event_properties) + @($m.person_properties)) { $types[$d.name] = $d.type }
} until (($m.events.name -contains "defs_$tag" -and $types.ContainsKey("pp_$tag")) -or (Get-Date) -gt $deadline)
Check "metadata: event names, columns, event and person properties" (($m.events.name -contains "view_product") -and ($m.event_columns -contains '$browser') -and ($m.person_properties.name -contains "email"))
Check "definitions recorded within seconds (new event name)" ($m.events.name -contains "defs_$tag")
Check "property types inferred: number, boolean, datetime, string" ($types["n_$tag"] -eq "number" -and $types["b_$tag"] -eq "boolean" -and $types["d_$tag"] -eq "datetime" -and $types["s_$tag"] -eq "string")
Check "person property from `$set recorded with its type" ((@($m.person_properties) | Where-Object { $_.name -eq "pp_$tag" }).type -eq "number")
Check "properties stored as columns are listed only in event_columns" (-not (@($m.event_properties).name -contains '$current_url') -and ($m.event_columns -contains '$current_url'))

Write-Host "`n5b) Retention and paths"
function Ago([int]$days, [int]$minutes = 0) { (Get-Date).ToUniversalTime().Date.AddHours(9).AddDays(-$days).AddMinutes(-$minutes).ToString("yyyy-MM-ddTHH:mm:ssZ") }
Capture $chrome ('[{"event":"rs___TAG__","distinct_id":"ra___TAG__","timestamp":"' + (Ago 5) + '"},{"event":"rs___TAG__","distinct_id":"rb___TAG__","timestamp":"' + (Ago 5) + '"},' +
    '{"event":"ru___TAG__","distinct_id":"ra___TAG__","timestamp":"' + (Ago 4) + '"},{"event":"rs___TAG__","distinct_id":"rc___TAG__","timestamp":"' + (Ago 4) + '"},' +
    '{"event":"ru___TAG__","distinct_id":"rc___TAG__","timestamp":"' + (Ago 2) + '"},{"event":"ru___TAG__","distinct_id":"ra___TAG__","timestamp":"' + (Ago 2) + '"}]')
Capture $chrome ('[{"event":"$pageview","distinct_id":"pa___TAG__","timestamp":"' + (Ago 1 30) + '","properties":{"$pathname":"/p___TAG__","qt":"__TAG__"}},' +
    '{"event":"$pageview","distinct_id":"pa___TAG__","timestamp":"' + (Ago 1 29) + '","properties":{"$pathname":"/pricing","qt":"__TAG__"}},' +
    '{"event":"buy___TAG__","distinct_id":"pa___TAG__","timestamp":"' + (Ago 1 28) + '","properties":{"qt":"__TAG__"}},' +
    '{"event":"$pageview","distinct_id":"pb___TAG__","timestamp":"' + (Ago 1 30) + '","properties":{"$pathname":"/p___TAG__","qt":"__TAG__"}},' +
    '{"event":"$pageview","distinct_id":"pb___TAG__","timestamp":"' + (Ago 1 29) + '","properties":{"$pathname":"/pricing","qt":"__TAG__"}},' +
    '{"event":"buy___TAG__","distinct_id":"pb___TAG__","timestamp":"' + (Ago 1 28) + '","properties":{"qt":"__TAG__"}},' +
    '{"event":"$pageview","distinct_id":"pc___TAG__","timestamp":"' + (Ago 1 30) + '","properties":{"$pathname":"/p___TAG__","qt":"__TAG__"}},' +
    '{"event":"$pageview","distinct_id":"pc___TAG__","timestamp":"' + (Ago 1 29) + '","properties":{"$pathname":"/p___TAG__","qt":"__TAG__"}},' +
    '{"event":"$pageview","distinct_id":"pc___TAG__","timestamp":"' + (Ago 1 28) + '","properties":{"$pathname":"/blog","qt":"__TAG__"}}]')
$from = (Get-Date).ToUniversalTime().AddDays(-6).ToString("yyyy-MM-dd")
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep 2
    $ret = (Call POST "/api/v1/query/retention" ('{"start_event":"rs___TAG__","return_event":"ru___TAG__","period":"day","periods":4,"date_from":"' + $from + '"}')).Json
    $big = @($ret.cohorts | Where-Object { $_.size -eq 2 })
} until ($big.Count -eq 1 -or (Get-Date) -gt $deadline)
$small = @($ret.cohorts | Where-Object { $_.size -eq 1 })
Check "retention: cohort of 2 -> day 1: 1, day 3: 1 ($($big[0].counts -join ','))" ($big.Count -eq 1 -and $big[0].counts[1] -eq 1 -and $big[0].counts[3] -eq 1 -and $big[0].counts[2] -eq 0)
Check "retention: cohort of 1 -> day 2: 1 ($($small[0].counts -join ','))" ($small.Count -eq 1 -and $small[0].counts[2] -eq 1)
Check "retention: future periods are null" ($null -eq $ret.cohorts[-1].counts[1])
Check "retention: average of day 1 = 1/3 -> $($ret.average[1])" ([math]::Abs($ret.average[1] - 0.3333) -lt 0.001)
Check "retention: invalid period -> 400" ((Call POST "/api/v1/query/retention" '{"start_event":"x","period":"month"}').Status -eq 400)
$pth = (Call POST "/api/v1/query/paths" ('{"start_event":"$pageview","depth":3,"date_from":"' + $from + '","filters":[' + $qtFilter + ']}')).Json
$top = @($pth.paths | Where-Object { ($_.nodes -join '|') -eq ('$pageview /p_' + $tag + '|$pageview /pricing|buy_' + $tag) })
Check "paths: /p -> /pricing -> buy followed by 2 persons ($($top[0].persons))" ($top.Count -eq 1 -and $top[0].persons -eq 2)
Check "paths: repeated page collapses (/p -> /blog)" (@($pth.paths | Where-Object { ($_.nodes -join '|') -eq ('$pageview /p_' + $tag + '|$pageview /blog') }).Count -eq 1)
Check "paths: step 2 has /pricing for 2 persons" ((($pth.steps | Where-Object { $_.step -eq 2 }).nodes | Where-Object { $_.name -eq '$pageview /pricing' }).persons -eq 2)
Check "paths: depth out of range -> 400" ((Call POST "/api/v1/query/paths" '{"start_event":"x","depth":9}').Status -eq 400)

Write-Host "`n6) EXPLAIN: is the person_distinct_ids join colocated?"
$e1 = (Call POST "/api/v1/query/trends" ('{"series":[{"event":"$pageview","metric":"unique_users"}],"filters":[' + $qtFilter + '],"explain":true}')).Json
Check "trends unique_users: OUTER JOIN (COLOCATE)" ($e1.plans[0] -match "(LEFT|RIGHT) OUTER JOIN \(COLOCATE\)")
$e2 = (Call POST "/api/v1/query/funnels" ('{"steps":[{"event":"view_product"},{"event":"purchase"}],"filters":[' + $qtFilter + '],"explain":true}')).Json
Check "funnel: OUTER JOIN (COLOCATE)" ($e2.plans[0] -match "(LEFT|RIGHT) OUTER JOIN \(COLOCATE\)")
Check "no INNER JOIN" (-not ($e1.sql[0] + $e2.sql[0] -match "INNER JOIN"))
Check "project_id filter present" ($e1.sql[0] -match "e\.project_id = @p0")

Write-Host "`n7) Latency (30 runs per query after warm-up)"
function Bench([string]$name, [string]$method, [string]$path, [string]$body) {
    1..3 | ForEach-Object { Call $method $path $body | Out-Null }
    $client = @(); $server = @()
    1..30 | ForEach-Object { $c = Call $method $path $body; $client += $c.Ms; if ($c.Json.took_ms) { $server += $c.Json.took_ms } }
    $client = $client | Sort-Object; $server = $server | Sort-Object
    $p = { param($a, $q) $a[[Math]::Min($a.Count - 1, [int][Math]::Ceiling($a.Count * $q) - 1)] }
    $line = "  {0,-36} client p50 {1,6:N1} ms  p95 {2,6:N1} ms" -f $name, (& $p $client 0.5), (& $p $client 0.95)
    if ($server.Count) { $line += "   (server p95 {0:N1} ms)" -f (& $p $server 0.95) }
    Write-Host $line
}
Bench "trends total (7 days, daily)" POST "/api/v1/query/trends" '{"series":[{"event":"$pageview"}],"interval":"day"}'
Bench "trends unique_users + breakdown" POST "/api/v1/query/trends" '{"series":[{"event":"$pageview","metric":"unique_users"}],"breakdown":{"property":"$browser"}}'
Bench "trends person filter (persons join)" POST "/api/v1/query/trends" '{"series":[{"event":"$pageview","metric":"unique_users"}],"filters":[{"scope":"person","property":"plan","op":"eq","value":"pro"}]}'
Bench "funnel 3 steps (30 days default)" POST "/api/v1/query/funnels" '{"steps":[{"event":"view_product"},{"event":"add_to_cart"},{"event":"purchase"}]}'
Bench "events (latest 50)" GET "/api/v1/events?limit=50" $null
Write-Host "  Note: local data is tiny; the real targets (trend p95 < 1s, funnel p95 < 3s at 100M events/month) are measured in stage 8."

if ($script:failed -eq 0) { Write-Host "`nPASSED: all Query API checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
