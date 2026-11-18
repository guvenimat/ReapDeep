$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$api = "http://localhost:8001/api/v1"
$tag = "ai" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$script:failed = 0

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.CookieContainer = New-Object System.Net.CookieContainer
$session = New-Object System.Net.Http.HttpClient($handler)
$session.Timeout = [TimeSpan]::FromSeconds(120)
$plain = New-Object System.Net.Http.HttpClient

function Send($client, [string]$method, [string]$path, $body = $null, [hashtable]$headers = @{ "X-Project-Id" = "1" }) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), "$api$path")
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($null -ne $body) {
        $text = if ($body -is [string]) { $body } else { ConvertTo-Json $body -Depth 10 -Compress }
        $req.Content = New-Object System.Net.Http.StringContent($text, [Text.Encoding]::UTF8, "application/json")
    }
    $r = $client.SendAsync($req).Result
    $t = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($t) { try { $t | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function S([string]$method, [string]$path, $body = $null) { Send $session $method $path $body }

$login = '{"email":"' + $envValues.ADMIN_EMAIL + '","password":"' + $envValues.ADMIN_PASSWORD + '"}'
if ((S POST "/auth/login" $login).Status -ne 200) { throw "login failed (rate limited? wait a minute)" }
$readKey = $envValues.READ_API_KEY
$project = (S GET "/project").Json
if ($project.ai_model -ne "fake model (tests)") { throw "ai-test needs AI_PROVIDER=fake (the query-api reports '$($project.ai_model)'); it never calls a real model" }
$wasEnabled = [bool]$project.ai_enabled
$trend = @{ series = @(@{ event = '$pageview'; metric = "total" }); interval = "day" }

try {
    Write-Host "`n1) Switch (tag: $tag)"
    $null = S PATCH "/project" @{ ai_enabled = $false }
    Check "the project reports the configured model and AI off" ((S GET "/project").Json.ai_enabled -eq $false)
    $off = S POST "/ai/explain" @{ kind = "trends"; query = $trend }
    Check "explaining while AI is off -> 403 with a pointer to Settings" ($off.Status -eq 403 -and $off.Json.error -match "Settings")
    Check "a read key cannot use AI -> 403" ((Send $plain POST "/ai/explain" @{ kind = "trends"; query = $trend } @{ Authorization = "Bearer $readKey" }).Status -eq 403)
    $on = S PATCH "/project" @{ ai_enabled = $true }
    Check "a manager turns AI on" ($on.Status -eq 200 -and $on.Json.ai_enabled -eq $true)

    Write-Host "`n2) Chart explanations"
    $trend.filters = @(@{ scope = "event"; property = '$browser'; op = "neq"; value = "no-$tag" })
    $e1 = S POST "/ai/explain" @{ kind = "trends"; query = $trend }
    Check "a trend explanation comes back from the model -> $($e1.Json.summary)" ($e1.Status -eq 200 -and $e1.Json.summary -like "[[]fake] insight*" -and @($e1.Json.findings).Count -ge 1)
    $e2 = S POST "/ai/explain" @{ kind = "trends"; query = $trend }
    Check "the same data is answered from the cache, keyed on what the model would see" ($e2.Status -eq 200 -and $e2.Json.cached -eq $true -and $e2.Json.generated_at -eq $e1.Json.generated_at)
    Check "funnel explanation" ((S POST "/ai/explain" @{ kind = "funnels"; query = @{ steps = @(@{ event = '$pageview' }, @{ event = '$identify' }) } }).Json.summary -like "[[]fake]*")
    Check "retention explanation" ((S POST "/ai/explain" @{ kind = "retention"; query = @{ start_event = '$pageview'; return_event = '$pageview' } }).Status -eq 200)
    Check "unknown kind -> 400" ((S POST "/ai/explain" @{ kind = "paths"; query = $trend }).Status -eq 400)
    Check "an invalid query is rejected before the model -> 400" ((S POST "/ai/explain" @{ kind = "trends"; query = @{ series = @() } }).Status -eq 400)

    Write-Host "`n3) Experiment interpretation"
    $flag = (S POST "/flags" @{ key = "ai-$tag"; enabled = $false; variants = @(@{ key = "control"; percentage = 50 }, @{ key = "test"; percentage = 50 }) }).Json
    $x = (S POST "/experiments" @{ name = "AI check $tag"; flag_id = $flag.id; control_variant = "control"; metric = @{ kind = "conversion"; event = '$identify' } }).Json
    Check "a draft experiment cannot be interpreted yet -> 409" ((S POST "/experiments/$($x.id)/explain").Status -eq 409)
    $null = S PUT "/flags/$($flag.id)" @{ enabled = $true }
    $started = S POST "/experiments/$($x.id)/start"
    $ex = S POST "/experiments/$($x.id)/explain"
    Check "a fresh experiment gets 'keep running' from the rules (too few people) and a model explanation" ($ex.Status -eq 200 -and $ex.Json.verdict.decision -eq "keep_running" -and $ex.Json.summary -like "[[]fake] experiment*")
    Check "unknown experiment -> 404" ((S POST "/experiments/999999999/explain").Status -eq 404)
    $null = S POST "/experiments/$($x.id)/stop"
    $null = S DELETE "/experiments/$($x.id)"
    $null = S DELETE "/flags/$($flag.id)"

    Write-Host "`n4) Questions in plain language"
    $q = S POST "/ai/query" @{ question = 'funnel from $pageview to $identify last week' }
    Check "a question becomes a funnel over known events" ($q.Status -eq 200 -and $q.Json.kind -eq "funnels" -and @($q.Json.query.steps).Count -eq 2 -and $q.Json.query.steps[0].event -eq '$pageview')
    $t = S POST "/ai/query" @{ question = 'daily $pageview count' }
    Check "a count question becomes a trend" ($t.Status -eq 200 -and $t.Json.kind -eq "trends" -and $t.Json.query.series[0].event -eq '$pageview')
    Check "an empty question -> 400" ((S POST "/ai/query" @{ question = " " }).Status -eq 400)

    Write-Host "`n5) Weekly strategy note"
    $d = S POST "/ai/digest"
    Check "the note has a summary, a recommendation and next steps" ($d.Status -eq 200 -and $d.Json.summary -like "[[]fake]*" -and $d.Json.recommendation -and @($d.Json.next_steps).Count -ge 1)
    Check "experiment ideas only use events the project has" (@($d.Json.experiments | Where-Object { $_.goal_event -ne "purchase" }).Count -eq 0)
}
finally {
    $null = S PATCH "/project" @{ ai_enabled = $wasEnabled }
}

if ($script:failed -eq 0) { Write-Host "`nPASSED: all AI checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
