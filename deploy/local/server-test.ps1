$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$api = "http://localhost:8001/api/v1"
$ingestion = "http://localhost:8000"
$reco = "http://localhost:8002"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$tag = "sv" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$script:failed = 0

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.CookieContainer = New-Object System.Net.CookieContainer
$session = New-Object System.Net.Http.HttpClient($handler)
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
    [pscustomobject]@{ Status = [int]$r.StatusCode; CacheControl = "$($r.Headers.CacheControl)"; Text = $t; Json = $(if ($t) { try { $t | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function S([string]$method, [string]$path, $body = $null) { Send $session $method "$api$path" $body @{ "X-Project-Id" = "1" } }
function Bearer([string]$key) { if ($key) { @{ Authorization = "Bearer $key" } } else { @{} } }
function Batch([string]$key, $events) { Send $plain POST "$ingestion/batch" (ConvertTo-Json @{ batch = @($events) } -Depth 10 -Compress) (Bearer $key) }
function Definitions([string]$key) { Send $plain GET "$reco/flags/definitions" $null (Bearer $key) }
function Day([DateTime]$d) { $d.ToString("yyyy-MM-dd") }
function Total([string]$event, [string]$property, [string]$value, [DateTime]$from, [DateTime]$to) {
    $body = @{ series = @(@{ event = $event }); filters = @(@{ property = $property; op = "eq"; value = $value }); date_from = (Day $from); date_to = (Day $to) }
    $r = S POST "/query/trends" $body
    if ($r.Status -ne 200) { return -1 }
    return [int]$r.Json.results[0].total
}
function WaitFor([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $cond) { return $true }; Start-Sleep 2 } until ((Get-Date) -gt $deadline)
    return [bool](& $cond)
}

$login = '{"email":"' + $envValues.ADMIN_EMAIL + '","password":"' + $envValues.ADMIN_PASSWORD + '"}'
if ((Send $session POST "$api/auth/login" $login).Status -ne 200) { throw "login failed (rate limited? wait a minute)" }

Write-Host "`n1) Server key (tag: $tag)"
$created = (S POST "/api-keys" @{ name = "Server $tag"; scope = "server" }).Json
$sk = $created.key
Check "a manager creates a server key with the sk_ prefix" ($sk -like "sk_*" -and $created.api_key.scope -eq "server")
$readKey = (S POST "/api-keys" @{ name = "Read $tag"; scope = "read" }).Json
Check "a server key cannot query analytics -> 403" ((Send $plain GET "$api/events?limit=1" $null (Bearer $sk)).Status -eq 403)
$ingestReady = WaitFor { (Batch $sk @(@{ event = "server_ping"; distinct_id = "ping-$tag" })).Status -eq 202 } 45
Check "ingestion picks up the new key" $ingestReady

Write-Host "`n2) Batch import"
$now = [DateTime]::UtcNow
$past = $now.AddDays(-60)
function Ev([string]$id, [string]$distinct, [DateTime]$at) {
    @{ event = "server_order"; distinct_id = $distinct; event_id = "$id-$tag"; timestamp = $at.ToString("o"); properties = @{ tag = $tag; amount = 25; '$ip' = "203.0.113.77" } }
}
Check "no key -> 401" ((Batch "" @(Ev "a" "c1" $past)).Status -eq 401)
Check "a read key is not a server key -> 401" ((Batch $readKey.key @(Ev "a" "c1" $past)).Status -eq 401)
Check "a public key is not a server key -> 401" ((Batch "pk_dev_local" @(Ev "a" "c1" $past)).Status -eq 401)
$events = @(
    (Ev "o1" "c1-$tag" $past), (Ev "o2" "c2-$tag" $past.AddHours(1)), (Ev "o3" "c1-$tag" $past.AddHours(2)),
    (Ev "old" "c3-$tag" $now.AddDays(-400)), (Ev "future" "c3-$tag" $now.AddHours(2)),
    @{ event = "server_order"; event_id = "nodistinct-$tag"; properties = @{ tag = $tag } }
)
$r = Batch $sk $events
Check "60-day-old events are accepted; older than a year, 2 h ahead or without distinct_id are rejected -> $($r.Text)" ($r.Status -eq 202 -and $r.Json.accepted -eq 3 -and $r.Json.rejected -eq 3)
$again = Batch $sk @($events[0..2])
Check "sending the same event_ids again counts them as duplicates -> $($again.Text)" ($again.Status -eq 202 -and $again.Json.duplicates -eq 3)
$big = Batch $sk @(1..5001 | ForEach-Object { @{ event = "x"; distinct_id = "d" } })
Check "more than 5000 events -> 400" ($big.Status -eq 400)
$fromBrowser = Send $plain POST "$ingestion/batch" (ConvertTo-Json @{ batch = @(@{ event = "x"; distinct_id = "d" }) } -Compress) @{ Authorization = "Bearer $sk"; Origin = "https://shop.example" }
Check "a server key sent from a browser (Origin header) -> 403" ($fromBrowser.Status -eq 403)
Check "the events land on their own date, 60 days ago" (WaitFor { (Total "server_order" "tag" $tag $past.AddDays(-1) $past.AddDays(1)) -eq 3 } 90)

Write-Host "`n3) Flag definitions"
$flagKey = "srv-$tag"
$flag = (S POST "/flags" @{ key = $flagKey; enabled = $true; conditions = @(@{ property = "plan"; op = "in"; value = @("pro", "team") }) }).Json
Check "no key -> 401" ((Definitions "").Status -eq 401)
Check "a public key cannot read the definitions -> 401" ((Definitions "pk_dev_local").Status -eq 401)
$defs = $null
$served = WaitFor { $script:defs = Definitions $sk; @($script:defs.Json.flags | Where-Object { $_.key -eq $flagKey }).Count -eq 1 } 30
$mine = @($defs.Json.flags | Where-Object { $_.key -eq $flagKey })[0]
Check "a server key reads the enabled flags with salt, rollout and conditions" ($served -and $mine.salt -and $mine.rollout_percentage -eq 100 -and $mine.conditions[0].op -eq "in")
Check "the definitions are never cached by proxies" ($defs.CacheControl -match "no-store")

Write-Host "`n4) .NET server SDK (sample app inside the compose network)"
$sdkCmd = "mkdir /w && cd /src && tar --exclude=bin --exclude=obj -cf - sdk/dotnet | tar -xf - -C /w && cd /w && dotnet run --project sdk/dotnet/samples/ReapDeep.Server.Sample -c Release -- $tag 2>&1 | tail -3"
$out = docker run --rm --network reapdeep_default -e "REAPDEEP_SERVER_KEY=$sk" -e "REAPDEEP_INGESTION_URL=http://ingestion:8000" -e "REAPDEEP_RECOMMENDER_URL=http://recommender:8002" `
    -v "${repo}:/src:ro" -v reapdeep-nuget:/root/.nuget/packages mcr.microsoft.com/dotnet/sdk:10.0-noble-aot sh -c $sdkCmd
$line = @($out | Where-Object { $_ -like '{*' })[-1]
$result = if ($line) { $line | ConvertFrom-Json } else { $null }
Check "the sample imports 120 orders, loads the flags and sees the flag on for a pro customer -> $line" ($result -and $result.imported -eq 120 -and $result.rejected -eq 0 -and $result.flags_loaded -and $result.flag_enabled)
Check "imported orders are stored 60 days back" (WaitFor { (Total "order_completed" "tag" $tag $past.AddDays(-1) $past.AddDays(1)) -eq 120 } 90)
Check "the captured event carries the flag value" (WaitFor { (Total "checkout_started" "new_checkout" "true" $now.AddDays(-1) $now.AddDays(1)) -ge 1 -and (Total "checkout_started" "tag" $tag $now.AddDays(-1) $now.AddDays(1)) -eq 1 } 60)
Check "the SDK sent one exposure event for the flag" (WaitFor { (Total '$feature_flag_called' '$feature_flag' $flagKey $now.AddDays(-1) $now.AddDays(1)) -eq 1 } 60)

Write-Host "`n5) Revocation"
$null = S DELETE "/api-keys/$($created.api_key.id)"
Check "a revoked server key stops working within the 30 s refresh" (WaitFor { (Batch $sk @(@{ event = "server_ping"; distinct_id = "ping-$tag" })).Status -eq 401 -and (Definitions $sk).Status -eq 401 } 45)

$null = S DELETE "/flags/$($flag.id)"
$null = S DELETE "/api-keys/$($readKey.api_key.id)"

if ($script:failed -eq 0) { Write-Host "`nPASSED: all server ingestion checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
