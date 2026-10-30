$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$api = "http://localhost:8001/api/v1"
$tag = "rl" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$buy = "buy_$tag"; $act = "act_$tag"
$script:failed = 0

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.CookieContainer = New-Object System.Net.CookieContainer
$session = New-Object System.Net.Http.HttpClient($handler)
$plain = New-Object System.Net.Http.HttpClient

function Send($client, [string]$method, [string]$path, [string]$body = $null, [hashtable]$headers = @{}) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), "$api$path")
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($body) { $req.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "application/json") }
    $r = $client.SendAsync($req).Result
    $text = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($text) { try { $text | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function Json($value) { if ($null -eq $value -or $value -is [string]) { $value } else { ConvertTo-Json $value -Depth 12 -Compress } }
function S([string]$method, [string]$path, $body = $null) { Send $session $method $path (Json $body) @{ "X-Project-Id" = "1" } }

$now = (Get-Date).ToUniversalTime()
function Ago([double]$days, [double]$hours = 1) { $now.AddDays(-$days).AddHours(-$hours).ToString("o") }
$script:batch = New-Object System.Collections.ArrayList
function Ev([string]$event, [string]$distinctId, [string]$timestamp, [hashtable]$props = @{}) {
    $null = $script:batch.Add(@{ event = $event; distinct_id = $distinctId; timestamp = $timestamp; properties = $props })
}
function Flush {
    $body = Json @{ api_key = "pk_dev_local"; sent_at = (Get-Date).ToUniversalTime().ToString("o"); batch = @($script:batch) }
    $content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "text/plain")
    $r = $plain.PostAsync("http://localhost:8000/capture", $content).Result
    if ([int]$r.StatusCode -ne 202) { throw "capture HTTP $([int]$r.StatusCode)" }
    $script:batch.Clear()
}
function Buy([string]$distinctId, [double]$days, [string]$order, $amount = 10) {
    $props = @{ amount = $amount; currency = "EUR"; item_id = "sku-1" }
    if ($order) { $props.order_id = $order }
    Ev $buy $distinctId (Ago $days) $props
}
function Orders([string]$distinctId, [double]$lastDays, [int]$count) {
    for ($k = 0; $k -lt $count; $k++) { Buy $distinctId ($lastDays + $k) "o-$distinctId-$k" }
}
function WaitFor([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $cond) { return $true }; Start-Sleep 2 } until ((Get-Date) -gt $deadline)
    return [bool](& $cond)
}
function Rfm($extra = @{}) {
    $body = @{ purchase_event = $buy }
    foreach ($k in $extra.Keys) { $body[$k] = $extra[$k] }
    S POST "/query/rfm" $body
}
function Segment($rfm, [string]$key) { @($rfm.segments | Where-Object { $_.key -eq $key })[0] }

$login = '{"email":"' + $envValues.ADMIN_EMAIL + '","password":"' + $envValues.ADMIN_PASSWORD + '"}'
if ((Send $session POST "/auth/login" $login).Status -ne 200) { throw "login failed (rate limited? wait a minute)" }

Write-Host "`n1) Test data (tag: $tag)"
$c = 1..10 | ForEach-Object { "c$($_)_$tag" }
Orders $c[0] 300 1
Orders $c[1] 250 5
Orders $c[2] 200 1
$anon4 = "anon4_$tag"
Buy $anon4 151 "o-c4-a"
Buy $anon4 152 "o-c4-b"
Orders $c[4] 100 1
Orders $c[5] 60 2
Orders $c[6] 30 1
Buy $c[6] 30 "o-$($c[6])-0" "abc"
Orders $c[7] 20 3
Buy $c[7] 23 $null
Orders $c[8] 5 1
Buy $c[8] 5 "o-$($c[8])-0" -5
Orders $c[9] 1 6
Buy $c[9] 1 "o-$($c[9])-0"

$p = 1..5 | ForEach-Object { "p$($_)_$tag" }
foreach ($d in 0..4) { Ev $act $p[0] (Ago $d 0.02) }
Ev $act $p[1] (Ago 10 0.02); Ev $act $p[1] (Ago 2 0.02)
Ev $act $p[2] (Ago 4 0.02)
Ev $act $p[3] (Ago 100 0.02); Ev $act $p[3] (Ago 0 0.02)
$anon5 = "anon5_$tag"
Ev $act $anon5 (Ago 3 0.02)
Flush
Start-Sleep -Milliseconds 300
Ev "`$identify" $c[3] (Ago 151 0.5) @{ "`$anon_distinct_id" = $anon4 }
Ev "`$identify" $p[4] (Ago 2 0.5) @{ "`$anon_distinct_id" = $anon5 }
Flush
Start-Sleep -Milliseconds 300
Buy $c[3] 150 "o-c4-c"
Ev $act $p[4] (Ago 2 0.02)
Flush

$ready = WaitFor { $script:r = (Rfm).Json; $script:r.customers -eq 10 -and $script:r.revenue -eq 260 -and (Segment $script:r "at_risk").persons -eq 1 } 120
Check "data ready: 10 customers (anonymous + identified purchases of c4 merged into one person) -> $($r.customers)" $ready

Write-Host "`n2) RFM scores and segments"
$expected = [ordered]@{
    lost = 1; cant_lose = 1; hibernating = 1; at_risk = 1; about_to_sleep = 1; need_attention = 1
    promising = 1; loyal = 1; new_customers = 1; champions = 1; potential_loyalist = 0
}
$actual = ($expected.Keys | ForEach-Object { "$_=$((Segment $r $_).persons)" }) -join " "
Check "every customer lands in its expected segment ($actual)" (@($expected.Keys | Where-Object { (Segment $r $_).persons -ne $expected[$_] }).Count -eq 0)
$cell = @($r.grid | Where-Object { $_.r -eq 2 -and $_.f -eq 4 })[0]
Check "c4 (3 orders over two ids, 150 days ago) is R2 F4 -> at_risk: $($cell.persons) in $($cell.segment)" ($cell.persons -eq 1 -and $cell.segment -eq "at_risk")
Check "five single-order customers share F1 (ties share a score)" ((@($r.grid | Where-Object { $_.f -eq 1 }) | Measure-Object persons -Sum).Sum -eq 5)
$champ = Segment $r "champions"
Check "champions: one order with two items counts once -> 6 orders, 70 EUR -> $($champ.avg_frequency) orders, $($champ.avg_monetary)" ($champ.avg_frequency -eq 6 -and $champ.avg_monetary -eq 70)
Check "champions recency is 1 day -> $($champ.avg_recency_days)" ($champ.avg_recency_days -eq 1)
Check "loyal: purchase without order_id is its own order -> 4 orders -> $((Segment $r 'loyal').avg_frequency)" ((Segment $r "loyal").avg_frequency -eq 4)
Check "negative and non-numeric amounts are ignored: new customers 10, promising 10" ((Segment $r "new_customers").avg_monetary -eq 10 -and (Segment $r "promising").avg_monetary -eq 10)
Check "total revenue 260 EUR, single currency -> $($r.revenue) $($r.currencies -join ',')" ($r.revenue -eq 260 -and @($r.currencies).Count -eq 1 -and $r.currencies[0] -eq "EUR")
Check "R5 threshold: last purchase 1-5 days ago -> $($r.thresholds.recency[4].min)-$($r.thresholds.recency[4].max)" ($r.thresholds.recency[4].min -eq 1 -and $r.thresholds.recency[4].max -eq 5)

$r90 = (Rfm @{ window_days = 90 }).Json
Check "90-day window keeps the 5 customers who bought in the last 90 days -> $($r90.customers)" ($r90.customers -eq 5)
Check "window_days 30 -> 400" ((Rfm @{ window_days = 30 }).Status -eq 400)
Check "invalid amount property -> 400" ((Rfm @{ amount_property = "bad'name" }).Status -eq 400)
$filtered = (Rfm @{ filters = @(@{ scope = "event"; property = "order_id"; op = "contains"; value = $c[9] }) }).Json
Check "event filter limits the report to matching purchases -> $($filtered.customers)" ($filtered.customers -eq 1)
$ex = (Rfm @{ explain = $true }).Json
Check "explain: LEFT JOIN on person_distinct_ids, no INNER JOIN, no NTILE" ($ex.sql[0] -match "LEFT JOIN analytics.person_distinct_ids" -and $ex.sql[0] -notmatch "INNER JOIN" -and $ex.sql[0] -notmatch "NTILE")

Write-Host "`n3) RFM segment as a cohort"
$def = @{ groups = @(@{ conditions = @(@{ type = "rfm_segment"; segments = @("champions", "loyal"); within_days = 365; event = $buy }) }) }
$prev = S POST "/cohorts/preview" @{ definition = $def }
Check "preview champions + loyal -> 2 -> $($prev.Json.persons)" ($prev.Json.persons -eq 2)
$withNeg = @{ groups = @(@{ conditions = @(@{ type = "rfm_segment"; segments = @("champions", "loyal"); event = $buy }, @{ type = "not_performed_event"; event = $buy; within_days = 10 }) }) }
Check "rfm segment and did not buy in 10 days -> 1 (loyal) -> $((S POST '/cohorts/preview' @{ definition = $withNeg }).Json.persons)" ((S POST "/cohorts/preview" @{ definition = $withNeg }).Json.persons -eq 1)
Check "unknown segment -> 400" ((S POST "/cohorts/preview" @{ definition = @{ groups = @(@{ conditions = @(@{ type = "rfm_segment"; segments = @("vip") }) }) } }).Status -eq 400)
$cohort = (S POST "/cohorts" @{ name = "RFM test $tag"; kind = "dynamic"; definition = @{ groups = @(@{ conditions = @(@{ type = "rfm_segment"; segments = @("at_risk"); event = $buy }) }) } }).Json
$ok = WaitFor { $script:cs = (S GET "/cohorts/$($cohort.id)").Json; $script:cs.status -eq "ready" -and $null -ne $script:cs.calculated_at } 90
Check "worker materializes the at_risk cohort -> $($cs.member_count) member" ($ok -and $cs.member_count -eq 1)
$members = (S GET "/cohorts/$($cohort.id)/persons").Json.persons
Check "the member is c4's person with both distinct_ids" (@($members).Count -eq 1 -and @($members[0].distinct_ids) -contains $anon4 -and @($members[0].distinct_ids) -contains $c[3])
$inCohort = (Rfm @{ filters = @(@{ scope = "cohort"; property = "$($cohort.id)"; op = "in" }) }).Json
Check "cohort filter on the RFM report -> 1 customer, scored only against itself -> $($inCohort.customers)" ($inCohort.customers -eq 1)

Write-Host "`n4) Lifecycle"
$tz = [TimeZoneInfo]::FindSystemTimeZoneById("Turkey Standard Time")
$today = [TimeZoneInfo]::ConvertTimeFromUtc($now, $tz).Date
function Lifecycle($extra = @{}) {
    $body = @{ event = $act; period = "day"; date_from = $today.AddDays(-4).ToString("yyyy-MM-dd"); date_to = $today.ToString("yyyy-MM-dd") }
    foreach ($k in $extra.Keys) { $body[$k] = $extra[$k] }
    S POST "/query/lifecycle" $body
}
function Row($p) { "$($p.new)/$($p.returning)/$($p.resurrecting)/$($p.dormant)" }
$ok = WaitFor { $script:lc = (Lifecycle).Json; (Row @($script:lc.periods)[1]) -eq "1/1/0/1" -and (Row @($script:lc.periods)[2]) -eq "0/2/1/0" } 90
$rows = @($lc.periods | ForEach-Object { Row $_ })
Write-Host "  periods (new/returning/resurrecting/dormant): $($rows -join '  ')"
Check "five daily periods" ($rows.Count -eq 5)
Check "day -4: p1 and p3 are new" ($rows[0] -eq "2/0/0/0")
Check "day -3: p5 new (anonymous), p1 returning, p3 dormant" ($rows[1] -eq "1/1/0/1")
Check "day -2: p1 and p5 returning (identified id merged), p2 resurrecting" ($ok -and $rows[2] -eq "0/2/1/0")
Check "day -1: p1 returning, p2 and p5 dormant" ($rows[3] -eq "0/1/0/2")
Check "today: p1 returning, p4 new (last seen 100 days ago, beyond the 90-day look-back)" ($rows[4] -eq "1/1/0/0")
$long = @((Lifecycle @{ lookback_days = 180 }).Json.periods)
Check "with a 180-day look-back p4 is resurrecting, not new -> $(Row $long[4])" ((Row $long[4]) -eq "0/1/1/0")
$weekly = (Lifecycle @{ period = "week"; date_from = $today.AddDays(-20).ToString("yyyy-MM-dd") }).Json
Check "weekly periods start on Monday" (@($weekly.periods | Where-Object { ([DateTime]$_.start).DayOfWeek -ne "Monday" }).Count -eq 0)
$monthly = (Lifecycle @{ period = "month"; date_from = $today.AddDays(-40).ToString("yyyy-MM-dd") }).Json
Check "monthly periods start on the 1st" (@($monthly.periods | Where-Object { ([DateTime]$_.start).Day -ne 1 }).Count -eq 0)
Check "lookback_days 3 -> 400" ((Lifecycle @{ lookback_days = 3 }).Status -eq 400)
Check "period hour -> 400" ((Lifecycle @{ period = "hour" }).Status -eq 400)

Write-Host "`n5) Saved insights"
$i1 = S POST "/insights" @{ name = "RFM $tag"; kind = "rfm"; query = @{ window_days = 365; purchase_event = $buy } }
$i2 = S POST "/insights" @{ name = "Lifecycle $tag"; kind = "lifecycle"; query = @{ event = $act; period = "week" } }
Check "rfm and lifecycle insights can be saved" ($i1.Status -eq 201 -and $i2.Status -eq 201)

Write-Host "`n6) Cleanup"
$null = S DELETE "/cohorts/$($cohort.id)"
foreach ($i in @($i1, $i2)) { if ($i.Json.id) { $null = S DELETE "/insights/$($i.Json.id)" } }

if ($script:failed -eq 0) { Write-Host "`nPASSED: all RFM and lifecycle checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
