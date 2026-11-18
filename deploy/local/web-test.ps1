$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$api = "http://localhost:8001/api/v1"
$capture = "http://localhost:8000/capture"
$tag = "web" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$script:failed = 0
$script:checks = 0

function Check($desc, $cond) {
    $script:checks++
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}
function New-Client {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.CookieContainer = New-Object System.Net.CookieContainer
    New-Object System.Net.Http.HttpClient($handler)
}
$owner = New-Client
$plain = New-Object System.Net.Http.HttpClient

function Send($client, [string]$method, [string]$url, $body = $null, [hashtable]$headers = @{}) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
        foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
        if ($null -ne $body) {
            $text = if ($body -is [string]) { $body } else { ConvertTo-Json $body -Depth 10 -Compress }
            $req.Content = New-Object System.Net.Http.StringContent($text, [Text.Encoding]::UTF8, "application/json")
        }
        $r = $client.SendAsync($req).Result
        if ([int]$r.StatusCode -ne 429) { break }
        $wait = if ($r.Headers.RetryAfter -and $r.Headers.RetryAfter.Delta) { [int]$r.Headers.RetryAfter.Delta.Value.TotalSeconds + 1 } else { 20 }
        Write-Host "  (429, waiting $wait s)" -ForegroundColor Yellow
        Start-Sleep $wait
    }
    $t = $r.Content.ReadAsStringAsync().Result
    $cache = if ($r.Headers.Contains("X-Cache")) { @($r.Headers.GetValues("X-Cache"))[0] } else { $null }
    [pscustomobject]@{ Status = [int]$r.StatusCode; Text = $t; Cache = $cache; Json = $(if ($t) { try { $t | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function Web($body, [long]$project, [switch]$Fresh) {
    $h = @{ "X-Project-Id" = "$project" }
    if ($Fresh) { $h["Cache-Control"] = "no-cache" }
    Send $owner POST "$api/query/web" $body $h
}
function Rows($r, [string]$dimension) { @(@($r.breakdowns | Where-Object { $_.dimension -eq $dimension })[0].rows) }
function Row($rows, $value) { @($rows | Where-Object { $_.value -eq $value })[0] }
function Same($rows, [string[]]$expected) {
    $actual = @($rows | ForEach-Object { if ($null -eq $_.sessions) { "$($_.pathname):$($_.pageviews):$($_.visitors)" } else { "$($_.value):$($_.sessions):$($_.visitors):$($_.bounce_rate):$($_.avg_session_seconds)" } })
    $ok = ($actual -join "|") -eq ($expected -join "|")
    if (-not $ok) { Write-Host "       got      $($actual -join ' | ')" -ForegroundColor Yellow; Write-Host "       expected $($expected -join ' | ')" -ForegroundColor Yellow }
    $ok
}

$offset = [TimeSpan]::FromHours(3)
$localToday = ([DateTime]::UtcNow + $offset).Date
$day1 = $localToday.AddDays(-3); $day2 = $localToday.AddDays(-2)
function At([DateTime]$localDay, [int]$hour, [int]$minute, [int]$second = 0) {
    [DateTime]::SpecifyKind($localDay.AddHours($hour).AddMinutes($minute).AddSeconds($second) - $offset, [DateTimeKind]::Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
}
function Ymd([DateTime]$d) { $d.ToString("yyyy-MM-dd") }

$script:events = New-Object System.Collections.Generic.List[object]
function Ev([string]$name, [string]$id, [string]$ts, [hashtable]$props) {
    $script:events.Add([ordered]@{ event = $name; distinct_id = "$id-$tag"; timestamp = $ts; properties = $props })
}
$desktopChrome = @{ '$device_type' = "Desktop"; '$browser' = "Chrome"; '$os' = "Windows" }
$mobileSafari = @{ '$device_type' = "Mobile"; '$browser' = "Safari"; '$os' = "iOS" }
$mobileChrome = @{ '$device_type' = "Mobile"; '$browser' = "Chrome"; '$os' = "Android" }
$desktopFirefox = @{ '$device_type' = "Desktop"; '$browser' = "Firefox"; '$os' = "Linux" }
function Props([hashtable]$device, [string]$session, [string]$path, [hashtable]$extra = @{}) {
    $p = @{ '$pathname' = $path; '$current_url' = "https://shop.example$path" }
    if ($session) { $p['$session_id'] = "$session-$tag" }
    foreach ($k in $device.Keys) { $p[$k] = $device[$k] }
    foreach ($k in $extra.Keys) { $p[$k] = $extra[$k] }
    $p
}
$google = @{ '$referring_domain' = "www.google.com"; utm_source = "google"; utm_medium = "cpc"; utm_campaign = "spring" }
$newsletter = @{ '$referring_domain' = "news.example.com"; utm_source = "newsletter"; utm_medium = "email"; utm_campaign = "launch" }
$internal = @{ '$referring_domain' = "shop.example" }

function Flush([string]$key) {
    for ($i = 0; $i -lt $script:events.Count; $i += 50) {
        $chunk = $script:events.GetRange($i, [math]::Min(50, $script:events.Count - $i))
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

if ((Send $owner POST "$api/auth/login" @{ email = $envValues.ADMIN_EMAIL; password = $envValues.ADMIN_PASSWORD }).Status -ne 200) { throw "login failed (rate limited? wait a minute)" }
$project = (Send $owner POST "$api/projects" @{ name = "Web analytics $tag"; timezone = "Europe/Istanbul" } @{ "X-Project-Id" = "1" }).Json
if (-not $project.id) { throw "could not create the test project" }
Write-Host "Test project $($project.id) (Europe/Istanbul), current period $(Ymd $day1) .. $(Ymd $day2), tag $tag"

Write-Host "`n1) Known-answer data through ingestion"
Ev "flag_check" "a" (At $day1 0 29 50) (Props $desktopChrome "s1" "/landing-api" @{ '$referring_domain' = "other.example" })
Ev '$pageview' "a" (At $day1 0 30) (Props $desktopChrome "s1" "/" $google)
Ev '$pageview' "a" (At $day1 0 31) (Props $desktopChrome "s1" "/pricing" $internal)
Ev "signup" "a" (At $day1 0 35) (Props $desktopChrome "s1" "/pricing" $internal)
Ev '$pageview' "a" (At $day2 10 0) (Props $desktopChrome "s2" "/pricing" @{ '$referring_domain' = '$direct' })
Ev '$pageview' "b" (At $day1 12 0) (Props $mobileSafari "s3" "/blog/post" $newsletter)
Ev '$pageview' "b" (At $day1 12 2) (Props $mobileSafari "s3" "/" $internal)
Ev '$pageview' "b" (At $day1 12 4) (Props $mobileSafari "s3" "/pricing" $internal)
Ev '$pageview' "c" (At $day2 15 0) (Props $mobileChrome "s4" "/" $google)
Ev "add_to_cart" "c" (At $day2 15 1 40) (Props $mobileChrome "s4" "/" $google)
Ev "api_call" "d" (At $day1 13 0) (Props $desktopChrome "s6" "/api")
Ev '$pageview' "e" (At $day1 14 0) (Props $desktopChrome "" "/")
Ev '$pageview' "f" (At $localToday.AddDays(-1) 1 30) (Props $desktopChrome "s7" "/")
Ev '$pageview' "a" (At $localToday.AddDays(-4) 10 0) (Props $desktopChrome "p1" "/")
Ev '$pageview' "g" (At $localToday.AddDays(-5) 10 0) (Props $desktopChrome "p2" "/")
Ev '$pageview' "g" (At $localToday.AddDays(-5) 10 10) (Props $desktopChrome "p2" "/pricing" $internal)
$sent = Flush $project.public_key
Ev '$identify' "u_c" (At $day2 16 0) @{ '$anon_distinct_id' = "c-$tag" }
Ev '$pageview' "u_c" (At $day2 16 0 5) (Props $desktopFirefox "s5" "/checkout")
Ev '$pageview' "u_c" (At $day2 16 0 35) (Props $desktopFirefox "s5" "/" $internal)
$sent += Flush $project.public_key
Ev '$pageview' "iso" ([DateTime]::UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")) (Props $desktopChrome "iso" "/iso-$tag")
$sent += Flush "pk_dev_local"
Check "$sent events accepted by ingestion (19 to the test project, 1 to project 1)" ($sent -eq 20)

$range = @{ date_from = (Ymd $day1); date_to = (Ymd $day2); interval = "day" }
$deadline = (Get-Date).AddSeconds(120)
do {
    Start-Sleep 3
    $r = (Web $range $project.id -Fresh).Json
    $ready = $r -and $r.current.pageviews -eq 10 -and $r.current.visitors -eq 4 -and $r.previous.pageviews -eq 3
} until ($ready -or (Get-Date) -gt $deadline)
Check "data visible, anonymous and identified visitor C merged into one person" $ready

Write-Host "`n2) Overview with the previous period"
$c = $r.current; $p = $r.previous
Check "visitors 4 (A, B, C merged, E without a session), D without a pageview not counted -> $($c.visitors)" ($c.visitors -eq 4)
Check "pageviews 10 (the out-of-range pageview at local 01:30 today-1 excluded) -> $($c.pageviews)" ($c.pageviews -eq 10)
Check "sessions 5 (the session without a pageview and the pageview without a session id left out) -> $($c.sessions)" ($c.sessions -eq 5)
Check "bounces 2, bounce rate 0.4 -> $($c.bounces), $($c.bounce_rate)" ($c.bounces -eq 2 -and $c.bounce_rate -eq 0.4)
Check "average session duration (310 + 0 + 240 + 100 + 30) / 5 = 136 s -> $($c.avg_session_seconds)" ($c.avg_session_seconds -eq 136)
Check "previous period: 2 visitors, 3 pageviews, 2 sessions, bounce 0.5, 300 s -> $($p.visitors)/$($p.pageviews)/$($p.sessions)/$($p.bounce_rate)/$($p.avg_session_seconds)" ($p.visitors -eq 2 -and $p.pageviews -eq 3 -and $p.sessions -eq 2 -and $p.bounce_rate -eq 0.5 -and $p.avg_session_seconds -eq 300)
Check "range in the project time zone: $($r.date_from) .. $($r.date_to), previous from $($r.previous_from)" ($r.timezone -eq "Europe/Istanbul" -and $r.date_from -eq "$(Ymd $day1)T00:00:00" -and $r.date_to -eq "$(Ymd $localToday.AddDays(-1))T00:00:00" -and $r.previous_from -eq "$(Ymd $localToday.AddDays(-5))T00:00:00")

Write-Host "`n3) Time series"
$s = @($r.series)
Check "2 daily buckets in local time" ($r.interval -eq "day" -and $s.Count -eq 2 -and $s[0].bucket -eq "$(Ymd $day1)T00:00:00" -and $s[1].bucket -eq "$(Ymd $day2)T00:00:00")
Check "day 1: 3 visitors, 6 pageviews (the 00:30 local pageview is 21:30 UTC the day before) -> $($s[0].visitors)/$($s[0].pageviews)" ($s[0].visitors -eq 3 -and $s[0].pageviews -eq 6)
Check "day 2: 2 visitors, 4 pageviews -> $($s[1].visitors)/$($s[1].pageviews)" ($s[1].visitors -eq 2 -and $s[1].pageviews -eq 4)
$hourly = (Web @{ date_from = (Ymd $day1); date_to = (Ymd $day2) } $project.id).Json
Check "without an interval a 2-day range is hourly: 48 buckets, 10 pageviews -> $(@($hourly.series).Count)" ($hourly.interval -eq "hour" -and @($hourly.series).Count -eq 48 -and (@($hourly.series) | Measure-Object pageviews -Sum).Sum -eq 10)

Write-Host "`n4) Pages"
Check "top pages by pageviews: / 5 (4 visitors), /pricing 3 (2), /blog/post 1, /checkout 1" (Same $r.pages @("/:5:4", "/pricing:3:2", "/blog/post:1:1", "/checkout:1:1"))
Check "entry pages come from each session's first pageview, not the earlier custom event" (Same (Rows $r "entry_page") @("/:2:2:0.5:205", "/blog/post:1:1:0:240", "/checkout:1:1:0:30", "/pricing:1:1:1:0"))

Write-Host "`n5) Traffic sources"
Check "referring domain of the first pageview; missing and `$direct are both direct" (Same (Rows $r "referring_domain") @('$direct:2:2:0.5:15', "www.google.com:2:2:0.5:205", "news.example.com:1:1:0:240"))
Check "UTM source" (Same (Rows $r "utm_source") @(":2:2:0.5:15", "google:2:2:0.5:205", "newsletter:1:1:0:240"))
Check "UTM medium" (Same (Rows $r "utm_medium") @(":2:2:0.5:15", "cpc:2:2:0.5:205", "email:1:1:0:240"))
Check "UTM campaign" (Same (Rows $r "utm_campaign") @(":2:2:0.5:15", "spring:2:2:0.5:205", "launch:1:1:0:240"))

Write-Host "`n6) Devices"
Check "device type" (Same (Rows $r "device_type") @("Desktop:3:2:0.3333:113.3", "Mobile:2:2:0.5:170"))
Check "browser" (Same (Rows $r "browser") @("Chrome:3:2:0.6667:136.7", "Firefox:1:1:0:30", "Safari:1:1:0:240"))
Check "OS" (Same (Rows $r "os") @("Windows:2:1:0.5:155", "Android:1:1:1:100", "Linux:1:1:0:30", "iOS:1:1:0:240"))

Write-Host "`n7) Filters and limit"
$mobile = (Web (@{ filters = @(@{ property = '$device_type'; op = "eq"; value = "Mobile" }) } + $range) $project.id).Json
Check "filter device_type = Mobile: 2 visitors, 4 pageviews, 2 sessions, bounce 0.5, 170 s -> $($mobile.current.visitors)/$($mobile.current.pageviews)/$($mobile.current.sessions)/$($mobile.current.bounce_rate)/$($mobile.current.avg_session_seconds)" ($mobile.current.visitors -eq 2 -and $mobile.current.pageviews -eq 4 -and $mobile.current.sessions -eq 2 -and $mobile.current.bounce_rate -eq 0.5 -and $mobile.current.avg_session_seconds -eq 170)
$limited = (Web (@{ limit = 1 } + $range) $project.id).Json
Check "limit 1 keeps the top row of each table" (@($limited.pages).Count -eq 1 -and $limited.pages[0].pathname -eq "/" -and @((Rows $limited "browser")).Count -eq 1 -and (Rows $limited "browser")[0].value -eq "Chrome")

Write-Host "`n8) Cache, validation, access and isolation"
$a = Web $range $project.id
Check "a repeated request is served from the result cache (X-Cache: $($a.Cache))" ($a.Status -eq 200 -and $a.Cache -eq "HIT" -and $a.Json.current.pageviews -eq 10)
Check "invalid date -> 400 naming date_from" ((Web @{ date_from = "yesterday" } $project.id).Json.error -match "date_from")
Check "invalid interval -> 400" ((Web (@{ interval = "minute" } + @{ date_from = (Ymd $day1) }) $project.id).Status -eq 400)
Check "no session or key -> 401" ((Send $plain POST "$api/query/web" $range).Status -eq 401)
Check "a project the user cannot access -> 403" ((Send $owner POST "$api/query/web" $range @{ "X-Project-Id" = "999999999" }).Status -eq 403)
$isoFilter = @{ filters = @(@{ property = '$pathname'; op = "eq"; value = "/iso-$tag" }) }
$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep 2
    $iso1 = Send $plain POST "$api/query/web" $isoFilter @{ Authorization = "Bearer $readKey"; "X-Project-Id" = "$($project.id)"; "Cache-Control" = "no-cache" }
} until ($iso1.Json.current.pageviews -eq 1 -or (Get-Date) -gt $deadline)
Check "the read key reads project 1 (the X-Project-Id header does not switch it): its pageview is there" ($iso1.Status -eq 200 -and $iso1.Json.current.pageviews -eq 1 -and $iso1.Json.pages[0].pathname -eq "/iso-$tag")
Check "the test project does not see project 1's pageview" ((Web $isoFilter $project.id -Fresh).Json.current.pageviews -eq 0)

Check "the test project is deleted" ((Send $owner DELETE "$api/project" @{ confirm = $project.name } @{ "X-Project-Id" = "$($project.id)" }).Status -eq 202)
if ($script:failed -eq 0) { Write-Host "`nPASSED: all $script:checks web analytics checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed of $script:checks checks failed" -ForegroundColor Red; exit 1 }
