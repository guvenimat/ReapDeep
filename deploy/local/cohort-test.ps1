$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$api = "http://localhost:8001/api/v1"
$tag = "co" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$anonA = "anonA_$tag"; $userA = "userA_$tag"; $buyerB = "buyerB_$tag"; $casualC = "casualC_$tag"
$view = "view_$tag"; $buy = "buy_$tag"; $plan = "plan_$tag"
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
function S([string]$method, [string]$path, $body = $null) { Send $session $method $path (Json $body) @{ "X-Project-Id" = "1" } }
function K([string]$method, [string]$path, $body = $null) { Send $plain $method $path (Json $body) @{ Authorization = "Bearer $readKey" } }
function Json($value) { if ($null -eq $value -or $value -is [string]) { $value } else { ConvertTo-Json $value -Depth 12 -Compress } }

function Capture([string]$batchJson) {
    $content = New-Object System.Net.Http.StringContent(('{"api_key":"pk_dev_local","batch":' + $batchJson + '}'), [Text.Encoding]::UTF8, "text/plain")
    $r = $plain.PostAsync("http://localhost:8000/capture", $content).Result
    if ([int]$r.StatusCode -ne 202) { throw "capture HTTP $([int]$r.StatusCode)" }
}
function StarRocks([string]$sql) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $out = & docker compose exec -T starrocks mysql "--host=127.0.0.1" "--port=9030" "--user=root" -N -e $sql 2>&1 | Out-String
    $ErrorActionPreference = $prev
    $out.Trim()
}
function WaitFor([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $cond) { return $true }; Start-Sleep 2 } until ((Get-Date) -gt $deadline)
    return [bool](& $cond)
}
function WaitReady([long]$id) {
    $ok = WaitFor { $c = (S GET "/cohorts/$id").Json; $c.status -eq "ready" -and $null -ne $c.calculated_at } 90
    if ($ok) { (S GET "/cohorts/$id").Json } else { $null }
}
function Performed([string]$ev, [int]$count, [string]$op = "gte") { @{ type = "performed_event"; event = $ev; count_op = $op; count = $count; within_days = 1 } }
function NotPerformed([string]$ev) { @{ type = "not_performed_event"; event = $ev; within_days = 1 } }
function Definition($groups) { @{ groups = @($groups | ForEach-Object { @{ conditions = @($_) } }) } }
function Trend($filters, $breakdown = $null, [string]$metric = "total") {
    $body = @{ series = @(@{ event = $view; metric = $metric }); date_from = (Get-Date).ToUniversalTime().AddDays(-1).ToString("o"); filters = @($filters) }
    if ($breakdown) { $body.breakdown = $breakdown }
    (S POST "/query/trends" $body)
}

$login = '{"email":"' + $envValues.ADMIN_EMAIL + '","password":"' + $envValues.ADMIN_PASSWORD + '"}'
if ((Send $session POST "/auth/login" $login).Status -ne 200) { throw "login failed (rate limited? wait a minute)" }

Write-Host "`n1) Test data (tag: $tag)"
Capture "[{`"event`":`"$view`",`"distinct_id`":`"$anonA`"},{`"event`":`"$view`",`"distinct_id`":`"$anonA`"}]"
Start-Sleep -Milliseconds 300
Capture "[{`"event`":`"`$identify`",`"distinct_id`":`"$userA`",`"properties`":{`"`$anon_distinct_id`":`"$anonA`",`"`$set`":{`"email`":`"$userA@example.com`",`"plan`":`"$plan`"}}}]"
Start-Sleep -Milliseconds 300
Capture "[{`"event`":`"$view`",`"distinct_id`":`"$userA`"},{`"event`":`"$view`",`"distinct_id`":`"$buyerB`"},{`"event`":`"$view`",`"distinct_id`":`"$buyerB`"},{`"event`":`"$view`",`"distinct_id`":`"$buyerB`"},{`"event`":`"$buy`",`"distinct_id`":`"$buyerB`"},{`"event`":`"$view`",`"distinct_id`":`"$casualC`"}]"
$ready = WaitFor {
    $script:personA = @((K GET "/persons?search=$userA@example.com").Json.persons)[0]
    $t = (Trend @() $null "unique_users").Json
    $script:personA -and $t.results[0].total -eq 3 -and
        (StarRocks "SELECT count(*) FROM analytics.persons WHERE person_id = '$($script:personA.id)' AND get_json_string(properties, '$.plan') = '$plan'") -eq "1"
} 90
Check "data ready (A: 2 anonymous + 1 identified view, B: 3 views + buy, C: 1 view; persons synced)" $ready
$personId = $personA.id

Write-Host "`n2) Preview and validation"
$windowShoppers = Definition @(, @((Performed $view 3), (NotPerformed $buy)))
$p = S POST "/cohorts/preview" @{ definition = $windowShoppers }
Check "preview: viewed 3+ and did not buy -> 1 (A, counted across anonymous and identified ids) -> $($p.Json.persons)" ($p.Status -eq 200 -and $p.Json.persons -eq 1)
Check "preview: viewed 3+ -> 2 (A and B)" ((S POST "/cohorts/preview" @{ definition = (Definition @(, @(Performed $view 3))) }).Json.persons -eq 2)
Check "preview: OR group (bought) or (property plan) -> 2" ((S POST "/cohorts/preview" @{ definition = (Definition @(@(Performed $buy 1), @(@{ type = "person_property"; property = "plan"; op = "eq"; value = $plan }))) }).Json.persons -eq 2)
Check "only a negative condition in a group -> 400" ((S POST "/cohorts/preview" @{ definition = (Definition @(, @(NotPerformed $buy))) }).Status -eq 400)
$tooLong = Definition @(, @(@{ type = "performed_event"; event = $view; count = 1; within_days = 91 }))
Check "within_days above 90 -> 400" ((S POST "/cohorts/preview" @{ definition = $tooLong }).Status -eq 400)
Check "read API key cannot create cohorts -> 403" ((K POST "/cohorts" @{ name = "x"; definition = $windowShoppers }).Status -eq 403)

Write-Host "`n3) Dynamic, property and static cohorts"
$dyn = (S POST "/cohorts" @{ name = "Window shoppers $tag"; kind = "dynamic"; definition = $windowShoppers }).Json
$early = S POST "/query/trends" @{ series = @(@{ event = $view }); filters = @(@{ scope = "cohort"; property = "$($dyn.id)"; op = "in" }) }
Check "querying a cohort before its first calculation -> 409" ($early.Status -eq 409)
$prop = (S POST "/cohorts" @{ name = "Plan $tag"; definition = (Definition @(, @(@{ type = "person_property"; property = "plan"; op = "eq"; value = $plan }))) }).Json
$stat = S POST "/cohorts" @{ name = "List $tag"; kind = "static"; members = @($personId, $casualC) }
Check "static cohort from a person id and a distinct_id -> 201, person id expanded to 2 distinct_ids" ($stat.Status -eq 201 -and $stat.Json.static_members -eq 3)
$stat = $stat.Json
$dyn = WaitReady $dyn.id; $prop = WaitReady $prop.id; $stat = WaitReady $stat.id
Check "dynamic cohort calculated: 1 person" ($dyn.member_count -eq 1)
Check "property cohort calculated: 1 person" ($prop.member_count -eq 1)
Check "static cohort calculated: 2 persons" ($stat.member_count -eq 2)
$members = (S GET "/cohorts/$($dyn.id)/persons").Json.persons
Check "members list: person A with both distinct_ids" (@($members).Count -eq 1 -and $members[0].id -eq $personId -and @($members[0].distinct_ids).Count -eq 2)
Check "StarRocks: membership stored per distinct_id (2 rows for A)" ((StarRocks "SELECT count(*) FROM analytics.cohort_members WHERE cohort_id = $($dyn.id) AND version = (SELECT max(version) FROM analytics.cohort_members WHERE cohort_id = $($dyn.id))") -eq "2")
Check "read API key can list cohorts" (@((K GET "/cohorts").Json.cohorts | Where-Object { $_.id -eq $dyn.id }).Count -eq 1)

Write-Host "`n4) Cohorts in analyses"
$in = (Trend @(@{ scope = "cohort"; property = "$($dyn.id)"; op = "in" })).Json.results[0].total
$out = (Trend @(@{ scope = "cohort"; property = "$($dyn.id)"; op = "not_in" })).Json.results[0].total
$all = (Trend @()).Json.results[0].total
Check "trend filter: in cohort = A's 3 views, not in = 4, together = all ($in + $out = $all)" ($in -eq 3 -and $out -eq 4 -and $all -eq 7)
Check "unique users in cohort = member_count" ((Trend @(@{ scope = "cohort"; property = "$($dyn.id)"; op = "in" }) $null "unique_users").Json.results[0].total -eq 1)
$bd = (Trend @() @{ scope = "cohort"; property = ""; cohort_ids = @($dyn.id, $stat.id) } "unique_users").Json.results
$byName = @{}; foreach ($r in $bd) { $byName[$r.breakdown_value] = $r.total }
Check "trend breakdown by cohort: one series per cohort (window shoppers 1, list 2)" (@($bd).Count -eq 2 -and $byName["Window shoppers $tag"] -eq 1 -and $byName["List $tag"] -eq 2)
$fb = (S POST "/query/funnels" @{ steps = @(@{ event = $view }, @{ event = $buy }); date_from = (Get-Date).ToUniversalTime().AddDays(-1).ToString("o"); breakdown = @{ scope = "cohort"; property = ""; cohort_ids = @($dyn.id, $prop.id) } }).Json.results
$counts = ($fb | ForEach-Object { "$($_.breakdown_value)=" + (($_.steps | ForEach-Object { $_.count }) -join "/") }) -join ", "
Check "funnel breakdown by cohort -> $counts" (@($fb).Count -eq 2 -and @($fb | Where-Object { $_.breakdown_value -eq "Window shoppers $tag" })[0].steps[0].count -eq 1)
$ret = S POST "/query/retention" @{ start_event = $view; period = "day"; periods = 1; date_from = (Get-Date).ToUniversalTime().AddDays(-1).ToString("yyyy-MM-dd"); filters = @(@{ scope = "cohort"; property = "$($dyn.id)"; op = "in" }) }
Check "retention accepts a cohort filter" ($ret.Status -eq 200 -and ($ret.Json.cohorts | Measure-Object -Property size -Sum).Sum -eq 1)
Check "unknown cohort -> 404" ((Trend @(@{ scope = "cohort"; property = "999999"; op = "in" })).Status -eq 404)
Check "invalid cohort operator -> 400" ((Trend @(@{ scope = "cohort"; property = "$($dyn.id)"; op = "eq" })).Status -eq 400)
$explain = (S POST "/query/trends" @{ series = @(@{ event = $view }); explain = $true; filters = @(@{ scope = "cohort"; property = "$($dyn.id)"; op = "in" }) }).Json
Check "EXPLAIN: the cohort join is colocated" ($explain.plans[0] -match "SEMI JOIN \(COLOCATE\)")

Write-Host "`n5) Editing and recalculation"
$before = $dyn.calculated_at
$edit = S PUT "/cohorts/$($dyn.id)" @{ definition = (Definition @(, @((Performed $view 1), (NotPerformed $buy)))) }
Check "changing the rules resets calculated_at" ($edit.Status -eq 200 -and $null -eq $edit.Json.calculated_at)
$dyn = WaitReady $dyn.id
Check "recalculated: viewed 1+ and did not buy -> 2 (A and C)" ($dyn.member_count -eq 2 -and $dyn.calculated_at -ne $before)
Check "the analysis follows the new version: unique users in cohort -> 2" ((Trend @(@{ scope = "cohort"; property = "$($dyn.id)"; op = "in" }) $null "unique_users").Json.results[0].total -eq 2)
Check "a static cohort has no rules -> 400" ((S PUT "/cohorts/$($stat.id)" @{ definition = $windowShoppers }).Status -eq 400)
$replace = S PUT "/cohorts/$($stat.id)/members" @{ members = @($buyerB) }
$stat = WaitReady $stat.id
Check "replacing static members recalculates -> 1 person" ($replace.Status -eq 200 -and $stat.member_count -eq 1)

Write-Host "`n6) Usage and deletion"
$insight = (S POST "/insights" @{ name = "Uses cohort $tag"; kind = "trends"; query = @{ series = @(@{ event = $view; metric = "total" }); filters = @(@{ scope = "cohort"; property = "$($prop.id)"; op = "in" }) } }).Json
$usage = (S GET "/cohorts/$($prop.id)/usage").Json.insights
Check "usage lists the saved insight that filters by the cohort" (@($usage).Count -eq 1 -and $usage[0].id -eq $insight.id)
Check "a cohort not used anywhere has no usage" (@((S GET "/cohorts/$($stat.id)/usage").Json.insights).Count -eq 0)
foreach ($c in @($dyn, $prop, $stat)) { $null = S DELETE "/cohorts/$($c.id)" }
$null = S DELETE "/insights/$($insight.id)"
Check "deleted cohort -> 404" ((S GET "/cohorts/$($dyn.id)").Status -eq 404)
$purged = WaitFor { (StarRocks "SELECT count(*) FROM analytics.cohort_members WHERE cohort_id IN ($($dyn.id), $($prop.id), $($stat.id))") -eq "0" } 60
Check "worker removes the deleted cohorts' rows from StarRocks" $purged

if ($script:failed -eq 0) { Write-Host "`nPASSED: all cohort checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
