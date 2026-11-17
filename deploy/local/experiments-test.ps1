$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$api = "http://localhost:8001/api/v1"
$capture = "http://localhost:8000/capture"
$tag = "ex" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$script:failed = 0
$script:checks = 0

function Check($desc, $cond) {
    $script:checks++
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}
function Near($expected, $actual, [double]$tolerance = 1e-6) { $null -ne $actual -and [math]::Abs([double]$expected - [double]$actual) -le $tolerance }
function New-Client {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.CookieContainer = New-Object System.Net.CookieContainer
    New-Object System.Net.Http.HttpClient($handler)
}
$owner = New-Client
$plain = New-Object System.Net.Http.HttpClient

function Send($client, [string]$method, [string]$url, $body = $null, [hashtable]$headers = @{}) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($null -ne $body) {
        $text = if ($body -is [string]) { $body } else { ConvertTo-Json $body -Depth 10 -Compress }
        $req.Content = New-Object System.Net.Http.StringContent($text, [Text.Encoding]::UTF8, "application/json")
    }
    $r = $client.SendAsync($req).Result
    $t = $r.Content.ReadAsStringAsync().Result
    $cache = if ($r.Headers.Contains("X-Cache")) { @($r.Headers.GetValues("X-Cache"))[0] } else { $null }
    [pscustomobject]@{ Status = [int]$r.StatusCode; Text = $t; Cache = $cache; Json = $(if ($t) { try { $t | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function S([string]$method, [string]$path, $body = $null, [long]$project = 1) { Send $owner $method "$api$path" $body @{ "X-Project-Id" = "$project" } }
function Fresh([string]$path, [long]$project = 1) { Send $owner GET "$api$path" $null @{ "X-Project-Id" = "$project"; "Cache-Control" = "no-cache" } }
function Login($client, [string]$email, [string]$password) { (Send $client POST "$api/auth/login" (@{ email = $email; password = $password })).Status }
function WaitFor([scriptblock]$cond, [int]$seconds, [int]$every = 3) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $cond) { return $true }; Start-Sleep $every } until ((Get-Date) -gt $deadline)
    return [bool](& $cond)
}
function Variant($results, [string]$key) { @($results.variants | Where-Object { $_.key -eq $key })[0] }

$script:events = New-Object System.Collections.Generic.List[object]
$base = [DateTime]::UtcNow.AddMinutes(-150)
function Ts([DateTime]$t) { $t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
function Ev([string]$name, [string]$id, [DateTime]$t, [hashtable]$props = @{}) {
    $script:events.Add([ordered]@{ event = $name; distinct_id = $id; timestamp = (Ts $t); properties = $props })
}
function Exposure([string]$id, [DateTime]$t, [string]$flag, $response) { Ev '$feature_flag_called' $id $t @{ '$feature_flag' = $flag; '$feature_flag_response' = $response } }
function Flush([string]$key = "pk_dev_local") {
    for ($i = 0; $i -lt $script:events.Count; $i += 100) {
        $chunk = $script:events.GetRange($i, [math]::Min(100, $script:events.Count - $i))
        $body = ConvertTo-Json @{ api_key = $key; batch = @($chunk) } -Depth 6 -Compress
        $status = 0
        for ($try = 0; $try -lt 30 -and $status -ne 202; $try++) {
            $status = [int]$plain.PostAsync($capture, (New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "text/plain"))).Result.StatusCode
            if ($status -ne 202) { Start-Sleep 2 }
        }
        if ($status -ne 202) { throw "capture returned HTTP $status" }
    }
    $sent = $script:events.Count
    $script:events.Clear()
    return $sent
}

if ((Login $owner $envValues.ADMIN_EMAIL $envValues.ADMIN_PASSWORD) -ne 200) { throw "login failed (rate limited? wait a minute)" }
$flagA = "exp-a-$tag"; $flagB = "exp-b-$tag"
$flags = @(); $experiments = @(); $otherFlags = @(); $otherExperiments = @()
$start = (Ts ([DateTime]::UtcNow.AddHours(-3)))
$conversion = @{ kind = "conversion"; event = "exp_buy" }

try {
    $other = (S POST "/projects" @{ name = "Experiments $tag" }).Json

    Write-Host "`n1) Flags and validation (tag: $tag)"
    $fa = S POST "/flags" @{ key = $flagA; enabled = $true; variants = @(@{ key = "control"; percentage = 50 }, @{ key = "test"; percentage = 50 }) }
    $fb = S POST "/flags" @{ key = $flagB; enabled = $true; variants = @(@{ key = "control"; percentage = 50 }, @{ key = "test"; percentage = 50 }) }
    $fbool = S POST "/flags" @{ key = "bool-$tag"; enabled = $true }
    $flags = @($fa.Json, $fb.Json, $fbool.Json)
    Check "two multivariate flags and one on/off flag created" ($fa.Status -eq 201 -and $fb.Status -eq 201 -and $fbool.Status -eq 201)
    $idA = $fa.Json.id; $idB = $fb.Json.id
    Check "missing flag_id -> 400" ((S POST "/experiments" @{ name = "x"; metric = $conversion }).Status -eq 400)
    $r = S POST "/experiments" @{ name = "x"; flag_id = $fbool.Json.id; metric = $conversion }
    Check "on/off flag -> 400 'at least two variants'" ($r.Status -eq 400 -and $r.Json.error -match "two variants")
    Check "unknown metric kind -> 400" ((S POST "/experiments" @{ name = "x"; flag_id = $idA; metric = @{ kind = "clicks"; event = "x" } }).Status -eq 400)
    Check "funnel with one step -> 400" ((S POST "/experiments" @{ name = "x"; flag_id = $idA; metric = @{ kind = "funnel"; steps = @("a") } }).Status -eq 400)
    Check "revenue without a property -> 400" ((S POST "/experiments" @{ name = "x"; flag_id = $idA; metric = @{ kind = "revenue"; event = "exp_buy" } }).Status -eq 400)
    Check "exposure event as the goal -> 400" ((S POST "/experiments" @{ name = "x"; flag_id = $idA; metric = @{ kind = "conversion"; event = '$feature_flag_called' } }).Status -eq 400)
    Check "control that is not a variant -> 400" ((S POST "/experiments" @{ name = "x"; flag_id = $idA; control_variant = "nope"; metric = $conversion }).Status -eq 400)
    Check "end_date before start_date -> 400" ((S POST "/experiments" @{ name = "x"; flag_id = $idA; metric = $conversion; start_date = "2026-11-10"; end_date = "2026-11-01" }).Status -eq 400)
    Check "missing name -> 400" ((S POST "/experiments" @{ flag_id = $idA; metric = $conversion }).Status -eq 400)
    Check "flag of another project -> 400" ((S POST "/experiments" @{ name = "x"; flag_id = $idA; metric = $conversion } $other.id).Status -eq 400)
    Check "nothing was created by the invalid requests" (-not ((S GET "/experiments").Json.experiments | Where-Object { $_.flag_key -like "*$tag" }))

    Write-Host "`n2) Create"
    $r = S POST "/experiments" @{ name = "Checkout conversion $tag"; flag_id = $idA; metric = $conversion; start_date = $start }
    $x1 = $r.Json; $experiments += $x1
    Check "conversion experiment -> 201, draft, control defaults to the first variant" ($r.Status -eq 201 -and $x1.status -eq "draft" -and $x1.control_variant -eq "control" -and $x1.flag_key -eq $flagA -and @($x1.variants).Count -eq 2)
    $x2 = (S POST "/experiments" @{ name = "Checkout funnel $tag"; flag_id = $idA; metric = @{ kind = "funnel"; steps = @("exp_cart", "exp_buy"); window_seconds = 3600 }; start_date = $start }).Json; $experiments += $x2
    $x3 = (S POST "/experiments" @{ name = "Checkout within 1h $tag"; flag_id = $idA; metric = @{ kind = "conversion"; event = "exp_buy"; window_seconds = 3600 }; start_date = $start }).Json; $experiments += $x3
    $x4 = (S POST "/experiments" @{ name = "Revenue $tag"; flag_id = $idB; metric = @{ kind = "revenue"; event = "exp_buy"; property = "amount" }; start_date = $start }).Json; $experiments += $x4
    Check "funnel, windowed conversion and revenue experiments created" ($x2.metric.window_seconds -eq 3600 -and $x3.metric.window_seconds -eq 3600 -and $x4.metric.property -eq "amount")
    $u = S PUT "/experiments/$($x1.id)" @{ description = "Does the new checkout sell more?" }
    Check "update keeps the other fields" ($u.Status -eq 200 -and $u.Json.description -match "new checkout" -and $u.Json.metric.event -eq "exp_buy" -and $u.Json.start_date)
    Check "list returns the 4 experiments" (@((S GET "/experiments").Json.experiments | Where-Object { $_.flag_key -like "*$tag" }).Count -eq 4)

    Write-Host "`n3) Known-answer events through the ingestion API"
    $n = 0
    function NextT { $script:n++; $base.AddSeconds($script:n) }
    for ($i = 0; $i -lt 30; $i++) { $t = NextT; $id = "x-$tag-ca-$i"; Exposure $id $t $flagA "control"; Ev "exp_cart" $id $t.AddSeconds(60); Ev "exp_buy" $id $t.AddSeconds(300) }
    for ($i = 0; $i -lt 10; $i++) { $t = NextT; $id = "x-$tag-cb-$i"; Exposure $id $t $flagA "control"; Ev "exp_buy" $id $t.AddSeconds(300) }
    for ($i = 0; $i -lt 20; $i++) { $t = NextT; $id = "x-$tag-cc-$i"; Ev "exp_cart" $id $t.AddSeconds(-1860); Ev "exp_buy" $id $t.AddSeconds(-1800); Exposure $id $t $flagA "control" }
    for ($i = 0; $i -lt 140; $i++) { $t = NextT; $id = "x-$tag-cd-$i"; Exposure $id $t $flagA "control"; if ($i -lt 20) { Exposure $id $t.AddSeconds(900) $flagA "control" } }
    for ($i = 0; $i -lt 40; $i++) { $t = NextT; $id = "x-$tag-ta-$i"; Exposure $id $t $flagA "test"; Ev "exp_cart" $id $t.AddSeconds(60); Ev "exp_buy" $id $t.AddSeconds(300) }
    for ($i = 0; $i -lt 5; $i++) {
        $t = NextT; $anon = "x-$tag-ti-$i"; $user = "x-$tag-user-$i"
        Exposure $anon $t $flagA "test"
        Ev '$identify' $user $t.AddSeconds(30) @{ '$anon_distinct_id' = $anon }
        Ev "exp_cart" $user $t.AddSeconds(60); Ev "exp_buy" $user $t.AddSeconds(300)
    }
    for ($i = 0; $i -lt 5; $i++) { $t = NextT; $id = "x-$tag-tb-$i"; Exposure $id $t $flagA "test"; Ev "exp_cart" $id $t.AddSeconds(60); Ev "exp_buy" $id $t.AddSeconds(4320) }
    for ($i = 0; $i -lt 10; $i++) { $t = NextT; $id = "x-$tag-tc-$i"; Exposure $id $t $flagA "test"; Ev "exp_buy" $id $t.AddSeconds(120); Ev "exp_cart" $id $t.AddSeconds(180) }
    for ($i = 0; $i -lt 140; $i++) { $t = NextT; Exposure "x-$tag-td-$i" $t $flagA "test" }
    for ($i = 0; $i -lt 10; $i++) { $t = NextT; $id = "x-$tag-m-$i"; Exposure $id $t $flagA "control"; Exposure $id $t.AddSeconds(10) $flagA "test"; Ev "exp_buy" $id $t.AddSeconds(300) }
    for ($i = 0; $i -lt 15; $i++) { $t = NextT; $id = "x-$tag-f-$i"; Exposure $id $t $flagA $false; Ev "exp_buy" $id $t.AddSeconds(300) }
    for ($i = 0; $i -lt 60; $i++) { $t = NextT; $id = "x-$tag-bc1-$i"; Exposure $id $t $flagB "control"; Ev "exp_buy" $id $t.AddSeconds(300) @{ amount = 20 } }
    for ($i = 0; $i -lt 10; $i++) { $t = NextT; $id = "x-$tag-bc2-$i"; Ev "exp_buy" $id $t.AddSeconds(-600) @{ amount = 100 }; Exposure $id $t $flagB "control" }
    for ($i = 0; $i -lt 230; $i++) { $t = NextT; Exposure "x-$tag-bc3-$i" $t $flagB "control" }
    for ($i = 0; $i -lt 40; $i++) { $t = NextT; $id = "x-$tag-bt1-$i"; Exposure $id $t $flagB "test"; Ev "exp_buy" $id $t.AddSeconds(300) @{ amount = 30 } }
    for ($i = 0; $i -lt 10; $i++) { $t = NextT; $id = "x-$tag-bt2-$i"; Exposure $id $t $flagB "test"; Ev "exp_buy" $id $t.AddSeconds(300) @{ amount = 25 }; Ev "exp_buy" $id $t.AddSeconds(400) @{ amount = "25" } }
    for ($i = 0; $i -lt 150; $i++) { $t = NextT; Exposure "x-$tag-bt3-$i" $t $flagB "test" }
    $sent = Flush
    Write-Host "  sent $sent events"

    Write-Host "`n4) Conversion: control 40/200, test 60/200, 10 multi-variant persons excluded"
    $ready = WaitFor { $r = (Fresh "/experiments/$($x1.id)/results").Json; (Variant $r "control").persons -eq 200 -and (Variant $r "test").persons -eq 200 -and (Variant $r "test").conversions -eq 60 -and $r.excluded_persons -eq 10 } 120
    $r1 = (Fresh "/experiments/$($x1.id)/results").Json
    $c = Variant $r1 "control"; $t1 = Variant $r1 "test"
    Write-Host ("  control {0}/{1}, test {2}/{3}, excluded {4}, z {5}, p {6}" -f $c.conversions, $c.persons, $t1.conversions, $t1.persons, $r1.excluded_persons, $t1.comparison.statistic, $t1.comparison.p_value)
    Check "events and the anonymous -> identified merge are reflected within 120 s" $ready
    Check "control: 200 persons, 40 conversions (20 converted only before exposure, 20 repeated exposures counted once)" ($c.persons -eq 200 -and $c.conversions -eq 40 -and (Near 0.2 $c.mean) -and $c.is_control -and $null -eq $c.comparison)
    Check "test: 200 persons, 60 conversions (5 identified after exposure count once)" ($t1.persons -eq 200 -and $t1.conversions -eq 60 -and (Near 0.3 $t1.mean))
    Check "10 persons who saw both variants excluded; 15 'false' responses ignored" ($r1.excluded_persons -eq 10)
    Check "z = 2.3094010767585025 and p = 0.020921335337794035 (pooled two-proportion z-test)" ((Near 2.3094010767585025 $t1.comparison.statistic) -and (Near 0.020921335337794035 $t1.comparison.p_value 1e-9) -and $r1.test -eq "two_proportion_z")
    Check "95% CI of the difference [0.015699, 0.184301], lift +50% [-2.32%, +102.32%], significant" ((Near 0.01569876146648773 $t1.comparison.difference_low) -and (Near 0.1843012385335122 $t1.comparison.difference_high) -and (Near 0.5 $t1.comparison.relative_lift) -and (Near -0.02316722085243128 $t1.comparison.relative_low) -and (Near 1.023167220852431 $t1.comparison.relative_high) -and $t1.comparison.significant)
    Check "balanced exposure 200/200 -> no SRM warning (chi2 0, p 1)" ($r1.srm -and -not $r1.srm.warning -and (Near 0 $r1.srm.chi_square) -and (Near 1 $r1.srm.p_value))

    Write-Host "`n5) Funnel and conversion window"
    $r2 = (Fresh "/experiments/$($x2.id)/results").Json
    $c2 = Variant $r2 "control"; $t2 = Variant $r2 "test"
    Write-Host ("  funnel control {0}/{1}, test {2}/{3}, p {4}" -f $c2.conversions, $c2.persons, $t2.conversions, $t2.persons, $t2.comparison.p_value)
    Check "funnel exp_cart -> exp_buy within 1 h after exposure: control 30, test 45 (slow, reversed and pre-exposure steps not counted)" ($c2.conversions -eq 30 -and $t2.conversions -eq 45 -and $c2.persons -eq 200)
    Check "funnel z = 1.9215378456610455, p = 0.054663935891675196, not significant" ((Near 1.9215378456610455 $t2.comparison.statistic) -and (Near 0.054663935891675196 $t2.comparison.p_value 1e-9) -and -not $t2.comparison.significant)
    $r3 = (Fresh "/experiments/$($x3.id)/results").Json
    Check "conversion within 3600 s of exposure: control 40, test 55 (5 bought after 72 min)" ((Variant $r3 "control").conversions -eq 40 -and (Variant $r3 "test").conversions -eq 55)

    Write-Host "`n6) Revenue per person with deliberately skewed exposure (300 control / 200 test on a 50/50 flag)"
    $r4 = (Fresh "/experiments/$($x4.id)/results").Json
    $c4 = Variant $r4 "control"; $t4 = Variant $r4 "test"
    Write-Host ("  control mean {0} sd {1}, test mean {2} sd {3}, t {4}, p {5}, srm p {6}" -f $c4.mean, $c4.standard_deviation, $t4.mean, $t4.standard_deviation, $t4.comparison.statistic, $t4.comparison.p_value, $r4.srm.p_value)
    Check "control mean 4.0 over 300 persons (a 100 purchase before exposure not counted), test mean 8.5 over 200" ($c4.persons -eq 300 -and (Near 4 $c4.mean) -and $t4.persons -eq 200 -and (Near 8.5 $t4.mean) -and $t4.conversions -eq 50)
    Check "sample variances 64.21404682274247 and 233.91959798994975" ((Near ([math]::Sqrt(64.21404682274247)) $c4.standard_deviation) -and (Near ([math]::Sqrt(233.91959798994975)) $t4.standard_deviation))
    Check "Welch t = 3.825605684996192, df = 272.4298074252528, p = 0.00016180042242849225" ($r4.test -eq "welch_t" -and (Near 3.825605684996192 $t4.comparison.statistic) -and (Near 272.4298074252528 $t4.comparison.degrees_of_freedom 1e-6) -and (Near 0.00016180042242849225 $t4.comparison.p_value 1e-10))
    Check "95% CI of the difference [2.184237, 6.815763], lift +112.5% [40.57%, 184.43%]" ((Near 2.1842373262785215 $t4.comparison.difference_low) -and (Near 6.815762673721478 $t4.comparison.difference_high) -and (Near 1.125 $t4.comparison.relative_lift) -and (Near 0.40565381620345 $t4.comparison.relative_low) -and (Near 1.84434618379655 $t4.comparison.relative_high))
    Check "SRM warning: chi2 = 20, p = 7.744216431044088e-06 < 0.001" ($r4.srm.warning -and (Near 20 $r4.srm.chi_square) -and (Near 7.744216431044088e-06 $r4.srm.p_value 1e-12))
    $x4c = S PUT "/experiments/$($x4.id)" @{ control_variant = "test" }
    $r4c = (Fresh "/experiments/$($x4.id)/results").Json
    Check "choosing 'test' as control flips the comparison (t = -3.8256)" ($x4c.Json.control_variant -eq "test" -and (Near -3.825605684996192 (Variant $r4c "control").comparison.statistic) -and $null -eq (Variant $r4c "test").comparison)

    Write-Host "`n7) Result cache"
    $first = Send $plain GET "$api/experiments/$($x1.id)/results" $null @{ Authorization = "Bearer $readKey" }
    $second = Send $plain GET "$api/experiments/$($x1.id)/results" $null @{ Authorization = "Bearer $readKey" }
    Check "repeated results request is served from the cache (X-Cache: $($first.Cache) then $($second.Cache))" ($second.Status -eq 200 -and $second.Cache -eq "HIT" -and (Variant $second.Json "test").conversions -eq 60)

    Write-Host "`n8) Lock: variants are fixed while an experiment runs"
    Check "while all experiments are drafts the variants can change" ((S PUT "/flags/$idA" @{ variants = @(@{ key = "control"; percentage = 40 }, @{ key = "test"; percentage = 60 }) }).Status -eq 200 -and (S PUT "/flags/$idA" @{ variants = @(@{ key = "control"; percentage = 50 }, @{ key = "test"; percentage = 50 }) }).Status -eq 200)
    $s = S POST "/experiments/$($x1.id)/start"
    Check "start -> running, the explicit start date is kept" ($s.Status -eq 200 -and $s.Json.status -eq "running" -and ([DateTime]$s.Json.start_date).ToUniversalTime() -lt [DateTime]::UtcNow.AddHours(-2))
    Check "starting again -> 409" ((S POST "/experiments/$($x1.id)/start").Status -eq 409)
    $r = S PUT "/flags/$idA" @{ variants = @(@{ key = "control"; percentage = 40 }, @{ key = "test"; percentage = 60 }) }
    Check "changing percentages -> 409 naming the experiment" ($r.Status -eq 409 -and $r.Json.error -match "locked" -and $r.Json.error -match "Checkout conversion")
    Check "renaming a variant -> 409" ((S PUT "/flags/$idA" @{ variants = @(@{ key = "control"; percentage = 50 }, @{ key = "b"; percentage = 50 }) }).Status -eq 409)
    Check "removing the variants -> 409" ((S PUT "/flags/$idA" @{ variants = @() }).Status -eq 409)
    $r = S PUT "/flags/$idA" @{ enabled = $false; rollout_percentage = 80; variants = @(@{ key = "control"; percentage = 50 }, @{ key = "test"; percentage = 50 }) }
    Check "enabled, rollout and unchanged variants can still be saved" ($r.Status -eq 200 -and $r.Json.enabled -eq $false -and $r.Json.rollout_percentage -eq 80)
    Check "the flag cannot be deleted while experiments use it -> 409" ((S DELETE "/flags/$idA").Status -eq 409)
    Check "a running experiment cannot be deleted -> 409" ((S DELETE "/experiments/$($x1.id)").Status -eq 409)
    Check "the flag of a running experiment cannot change -> 409" ((S PUT "/experiments/$($x1.id)" @{ flag_id = $idB }).Status -eq 409)
    $p = S POST "/experiments/$($x1.id)/stop"
    Check "stop -> stopped with an end date" ($p.Status -eq 200 -and $p.Json.status -eq "stopped" -and $p.Json.end_date)
    Check "stopping again -> 409, restarting a stopped experiment -> 409" ((S POST "/experiments/$($x1.id)/stop").Status -eq 409 -and (S POST "/experiments/$($x1.id)/start").Status -eq 409)
    Check "after stopping, the variants can change again" ((S PUT "/flags/$idA" @{ variants = @(@{ key = "control"; percentage = 40 }, @{ key = "test"; percentage = 60 }) }).Status -eq 200)
    $null = S PUT "/flags/$idA" @{ variants = @(@{ key = "control"; percentage = 50 }, @{ key = "test"; percentage = 50 }) }
    $rs = (Fresh "/experiments/$($x1.id)/results").Json
    Check "a stopped experiment keeps its results" ((Variant $rs "test").conversions -eq 60 -and (Variant $rs "control").persons -eq 200)

    Write-Host "`n9) Access"
    Check "no credentials -> 401" ((Send $plain GET "$api/experiments").Status -eq 401)
    $rk = @{ Authorization = "Bearer $readKey" }
    Check "read API key can list and read results" ((Send $plain GET "$api/experiments" $null $rk).Status -eq 200 -and (Send $plain GET "$api/experiments/$($x2.id)/results" $null $rk).Status -eq 200)
    Check "read API key cannot create, start or delete -> 403" ((Send $plain POST "$api/experiments" @{ name = "k"; flag_id = $idA; metric = $conversion } $rk).Status -eq 403 -and (Send $plain POST "$api/experiments/$($x2.id)/start" $null $rk).Status -eq 403 -and (Send $plain DELETE "$api/experiments/$($x2.id)" $null $rk).Status -eq 403)
    $memberEmail = "exp-member-$tag@example.com"; $memberPassword = [guid]::NewGuid().ToString("N").Substring(0, 16)
    $memberId = (S POST "/members" @{ email = $memberEmail; password = $memberPassword; role = "member" }).Json.user_id
    $member = New-Client
    if ((Login $member $memberEmail $memberPassword) -eq 200) {
        $mh = @{ "X-Project-Id" = "1" }
        Check "member can list experiments and read results" ((Send $member GET "$api/experiments" $null $mh).Status -eq 200 -and (Send $member GET "$api/experiments/$($x1.id)/results" $null $mh).Status -eq 200)
        Check "member cannot create, update, start or stop -> 403" ((Send $member POST "$api/experiments" @{ name = "m"; flag_id = $idA; metric = $conversion } $mh).Status -eq 403 -and (Send $member PUT "$api/experiments/$($x2.id)" @{ name = "m" } $mh).Status -eq 403 -and (Send $member POST "$api/experiments/$($x2.id)/start" $null $mh).Status -eq 403 -and (Send $member POST "$api/experiments/$($x2.id)/stop" $null $mh).Status -eq 403)
    }
    else { Check "member signs in" $false }
    $null = S DELETE "/members/$memberId"

    Write-Host "`n10) Project isolation"
    Check "project 1's experiment ids are not reachable with the other project's header -> 404" ((S GET "/experiments/$($x1.id)" $null $other.id).Status -eq 404 -and (S GET "/experiments/$($x1.id)/results" $null $other.id).Status -eq 404 -and (S DELETE "/experiments/$($x2.id)" $null $other.id).Status -eq 404 -and (S POST "/experiments/$($x2.id)/start" $null $other.id).Status -eq 404)
    $of = (S POST "/flags" @{ key = $flagA; variants = @(@{ key = "control"; percentage = 50 }, @{ key = "test"; percentage = 50 }) } $other.id).Json; $otherFlags += $of
    $ox = (S POST "/experiments" @{ name = "Other $tag"; flag_id = $of.id; metric = $conversion; start_date = $start } $other.id).Json; $otherExperiments += $ox
    Check "the other project lists only its own experiment" (@((S GET "/experiments" $null $other.id).Json.experiments).Count -eq 1)
    for ($i = 0; $i -lt 5; $i++) { $t = NextT; $id = "x-$tag-o-$i"; Exposure $id $t $flagA "control"; Ev "exp_buy" $id $t.AddSeconds(300) }
    $null = Flush $other.public_key
    $seen = WaitFor { (Variant (Fresh "/experiments/$($ox.id)/results" $other.id).Json "control").persons -eq 5 } 90
    $ro = (Fresh "/experiments/$($ox.id)/results" $other.id).Json
    Check "the other project's experiment on the same flag key sees only its 5 persons" ($seen -and (Variant $ro "control").conversions -eq 5 -and (Variant $ro "test").persons -eq 0)
    $rp = (Fresh "/experiments/$($x1.id)/results").Json
    Check "project 1 still counts 200 control persons" ((Variant $rp "control").persons -eq 200 -and (Variant $rp "control").conversions -eq 40)

    Write-Host "`n11) Delete"
    Check "delete a draft -> 204, then 404" ((S DELETE "/experiments/$($x3.id)").Status -eq 204 -and (S GET "/experiments/$($x3.id)").Status -eq 404)
}
finally {
    foreach ($x in $experiments) { if ($x) { $null = S POST "/experiments/$($x.id)/stop"; $null = S DELETE "/experiments/$($x.id)" } }
    foreach ($x in $otherExperiments) { if ($x) { $null = S DELETE "/experiments/$($x.id)" $null $other.id } }
    foreach ($f in $flags) { if ($f) { $null = S DELETE "/flags/$($f.id)" } }
    foreach ($f in $otherFlags) { if ($f) { $null = S DELETE "/flags/$($f.id)" $null $other.id } }
}
Check "experiments and flags of this run removed" (-not ((S GET "/experiments").Json.experiments | Where-Object { $_.flag_key -like "*$tag" }) -and -not ((S GET "/flags").Json.flags | Where-Object { $_.key -like "*$tag" }))

if ($script:failed -eq 0) { Write-Host "`nPASSED: all $script:checks experiment checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed of $script:checks checks failed" -ForegroundColor Red; exit 1 }
