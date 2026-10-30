$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$api = "http://localhost:8001/api/v1"
$tag = "al" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$ev = "alert_$tag"
$port = 8899
$hookLog = Join-Path $env:TEMP "reapdeep-hooks-$tag.jsonl"
$hookUrl = "http://host.docker.internal:$port/hook"
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
function K([string]$method, [string]$path) { Send $plain $method $path $null @{ Authorization = "Bearer $readKey" } }
function WaitFor([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $cond) { return $true }; Start-Sleep 2 } until ((Get-Date) -gt $deadline)
    return [bool](& $cond)
}
function Hooks { if (Test-Path $hookLog) { @(Get-Content $hookLog | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
function Hmac([string]$secret, [string]$body) {
    $h = New-Object System.Security.Cryptography.HMACSHA256 (, [Text.Encoding]::UTF8.GetBytes($secret))
    -join ($h.ComputeHash([Text.Encoding]::UTF8.GetBytes($body)) | ForEach-Object { $_.ToString("x2") })
}

$receiver = Start-Job -ArgumentList $port, $hookLog {
    param($port, $log)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $port)
    $listener.Start()
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-Date) -lt $deadline) {
        $task = $listener.AcceptTcpClientAsync()
        if (-not $task.Wait(1000)) { continue }
        $client = $task.Result
        try {
            $stream = $client.GetStream()
            $stream.ReadTimeout = 5000
            $buf = New-Object byte[] 65536
            $total = 0
            $headerEnd = -1
            do {
                $n = $stream.Read($buf, $total, $buf.Length - $total)
                $total += $n
                $text = [Text.Encoding]::UTF8.GetString($buf, 0, $total)
                $headerEnd = $text.IndexOf("`r`n`r`n")
            } while ($headerEnd -lt 0 -and $n -gt 0)
            $headers = $text.Substring(0, $headerEnd)
            $length = [int]([regex]::Match($headers, "(?im)^content-length:\s*(\d+)").Groups[1].Value)
            $bodyStart = [Text.Encoding]::UTF8.GetByteCount($text.Substring(0, $headerEnd + 4))
            while ($total - $bodyStart -lt $length) { $total += $stream.Read($buf, $total, $buf.Length - $total) }
            $body = [Text.Encoding]::UTF8.GetString($buf, $bodyStart, $length)
            $signature = [regex]::Match($headers, "(?im)^x-reapdeep-signature:\s*(\S+)").Groups[1].Value
            Add-Content -Path $log -Value (@{ signature = $signature; body = $body } | ConvertTo-Json -Compress)
            $resp = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: 2`r`nConnection: close`r`n`r`nok")
            $stream.Write($resp, 0, $resp.Length)
        }
        finally { $client.Close() }
    }
    $listener.Stop()
}

try {
    $login = '{"email":"' + $envValues.ADMIN_EMAIL + '","password":"' + $envValues.ADMIN_PASSWORD + '"}'
    if ((Send $session POST "/auth/login" $login).Status -ne 200) { throw "login failed (rate limited? wait a minute)" }

    Write-Host "`n1) Test data (tag: $tag)"
    $batch = '{"api_key":"pk_dev_local","batch":[' + ((1..3 | ForEach-Object { '{"event":"' + $ev + '","distinct_id":"u' + $_ + '_' + $tag + '","properties":{"amount":' + ($_ * 10) + '}}' }) -join ",") + ']}'
    $r = $plain.PostAsync("http://localhost:8000/capture", (New-Object System.Net.Http.StringContent($batch, [Text.Encoding]::UTF8, "text/plain"))).Result
    if ([int]$r.StatusCode -ne 202) { throw "capture failed" }
    $ready = WaitFor { (S POST "/query/trends" @{ series = @(@{ event = $ev }) }).Json.results[0].total -eq 3 } 60
    Check "3 events ingested" $ready
    $insight = (S POST "/insights" @{ name = "Alert source $tag"; kind = "trends"; query = @{ series = @(@{ event = $ev; metric = "total" }); filters = @(); deduplicate = $false; chart = "line" } }).Json
    $sumInsight = (S POST "/insights" @{ name = "Alert sum $tag"; kind = "trends"; query = @{ series = @(@{ event = $ev; metric = "sum"; property = "amount" }, @{ event = $ev; metric = "unique_users" }); formula = "A / B" } }).Json
    $broken = (S POST "/insights" @{ name = "Broken $tag"; kind = "trends"; query = @{ series = @(@{ event = $ev; metric = "avg" }) } }).Json
    $funnel = (S POST "/insights" @{ name = "Funnel $tag"; kind = "funnels"; query = @{ steps = @("a", "b") } }).Json

    Write-Host "`n2) Validation and access"
    $base = @{ name = "Volume $tag"; insight_id = $insight.id; condition = "above"; threshold = 2; window = "day"; webhook_url = $hookUrl }
    function With($patch) { $b = $base.Clone(); foreach ($k in $patch.Keys) { $b[$k] = $patch[$k] }; $b }
    Check "ftp webhook -> 400" ((S POST "/alerts" (With @{ webhook_url = "ftp://example.com/x" })).Status -eq 400)
    Check "webhook with credentials -> 400" ((S POST "/alerts" (With @{ webhook_url = "http://user:pw@example.com/x" })).Status -eq 400)
    Check "funnel insight -> 400 (alerts watch trends)" ((S POST "/alerts" (With @{ insight_id = $funnel.id })).Status -eq 400)
    Check "change condition with a non-positive percentage -> 400" ((S POST "/alerts" (With @{ condition = "increase"; threshold = 0 })).Status -eq 400)
    Check "unknown window -> 400" ((S POST "/alerts" (With @{ window = "month" })).Status -eq 400)
    Check "read API key cannot list alerts -> 403" ((K GET "/alerts").Status -eq 403)

    Write-Host "`n3) Firing, webhook and signature"
    $created = S POST "/alerts" $base
    $alert = $created.Json
    Check "create -> 201 with a secret shown to the manager" ($created.Status -eq 201 -and $alert.secret -like "whsec_*" -and $alert.state -eq "pending")
    $fired = WaitFor { (S GET "/alerts").Json.alerts | Where-Object { $_.id -eq $alert.id -and $_.state -eq "firing" } } 60
    Check "the background job evaluates the new alert: 3 > 2 -> firing" $fired
    $ok = WaitFor { @(Hooks).Count -ge 1 } 20
    $hook = @(Hooks)[0]
    $payload = $hook.body | ConvertFrom-Json
    Check "webhook received with state firing and value 3" ($ok -and $payload.state -eq "firing" -and $payload.value -eq 3 -and $payload.alert_id -eq $alert.id -and $payload.insight.name -eq $insight.name)
    Check "X-ReapDeep-Signature is the HMAC-SHA256 of the body with the alert secret" ($hook.signature -eq ("sha256=" + (Hmac $alert.secret $hook.body)))
    $check = (S POST "/alerts/$($alert.id)/check").Json
    Check "checking again while still firing sends nothing new" ($check.state -eq "firing" -and $null -eq $check.event -and @(Hooks).Count -eq 1)

    Write-Host "`n4) Resolving"
    $updated = S PUT "/alerts/$($alert.id)" @{ threshold = 10 }
    Check "raising the threshold keeps the state and schedules a check" ($updated.Status -eq 200 -and $updated.Json.state -eq "firing" -and $updated.Json.threshold -eq 10)
    $resolved = WaitFor { (S GET "/alerts").Json.alerts | Where-Object { $_.id -eq $alert.id -and $_.state -eq "ok" } } 60
    $ok = WaitFor { @(Hooks).Count -ge 2 } 20
    Check "3 is not above 10 -> ok, and a resolved webhook is sent" ($resolved -and $ok -and (@(Hooks)[1].body | ConvertFrom-Json).state -eq "resolved")
    $events = (S GET "/alerts/$($alert.id)/events").Json.events
    Check "history: resolved then fired, both delivered with HTTP 200" (@($events).Count -eq 2 -and $events[0].kind -eq "resolved" -and $events[1].kind -eq "fired" -and $events[0].response_status -eq 200 -and $events[1].response_status -eq 200)

    Write-Host "`n5) Change against the previous window and formulas"
    $inc = (S POST "/alerts" (With @{ name = "Growth $tag"; condition = "increase"; threshold = 50; enabled = $false })).Json
    $r = (S POST "/alerts/$($inc.id)/check").Json
    Check "increase: 3 against 0 in the previous day -> firing (checked on demand while disabled)" ($r.state -eq "firing" -and $r.previous_value -eq 0 -and $r.event.kind -eq "fired")
    $per = (S POST "/alerts" (With @{ name = "Per user $tag"; insight_id = $sumInsight.id; condition = "below"; threshold = 25; enabled = $false })).Json
    $r = (S POST "/alerts/$($per.id)/check").Json
    Check "formula insight: sum / unique users = 60 / 3 = 20 < 25 -> firing -> $($r.value)" ($r.state -eq "firing" -and $r.value -eq 20)

    Write-Host "`n6) Errors and cleanup"
    $bad = (S POST "/alerts" (With @{ name = "Broken $tag"; insight_id = $broken.id; enabled = $false })).Json
    $r = (S POST "/alerts/$($bad.id)/check").Json
    Check "an insight that cannot run puts the alert in error with a message" ($r.state -eq "error" -and $r.message -match "property" -and $r.event.kind -eq "error")
    $r2 = (S POST "/alerts/$($bad.id)/check").Json
    Check "a repeated error is not logged twice" ($r2.state -eq "error" -and $null -eq $r2.event)
    $null = S DELETE "/insights/$($insight.id)"
    Check "deleting the insight deletes its alerts" (-not ((S GET "/alerts").Json.alerts | Where-Object { $_.id -in @($alert.id, $inc.id) }))
    foreach ($a in @($per, $bad)) { $null = S DELETE "/alerts/$($a.id)" }
    foreach ($i in @($sumInsight, $broken, $funnel)) { $null = S DELETE "/insights/$($i.id)" }
    Check "alerts of this run removed" (-not ((S GET "/alerts").Json.alerts | Where-Object { $_.name -like "*$tag" }))
}
finally {
    Stop-Job $receiver -ErrorAction SilentlyContinue
    Remove-Job $receiver -Force -ErrorAction SilentlyContinue
    Remove-Item $hookLog -ErrorAction SilentlyContinue
}

if ($script:failed -eq 0) { Write-Host "`nPASSED: all alert checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
