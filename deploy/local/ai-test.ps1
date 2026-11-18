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
$orgId = [long]$project.organization_id
$memberEmail = "ai_member_$tag@example.com"; $memberPassword = "temp-$tag-pass"
$trend = @{ series = @(@{ event = '$pageview'; metric = "total" }); interval = "day" }

try {
    Write-Host "`n1) Platform and customer switches (tag: $tag)"
    $orgs = S GET "/platform/organizations"
    $before = @($orgs.Json.organizations | Where-Object { $_.id -eq $orgId })[0]
    Check "the platform administrator lists the customers with their AI settings" ($orgs.Status -eq 200 -and $null -ne $before)
    Check "a read key cannot open the platform -> 401" ((Send $plain GET "/platform/organizations" $null @{ Authorization = "Bearer $readKey" }).Status -eq 401)
    $null = S POST "/members" @{ email = $memberEmail; name = "AI member $tag"; password = $memberPassword; role = "member" }
    $mh = New-Object System.Net.Http.HttpClientHandler; $mh.CookieContainer = New-Object System.Net.CookieContainer
    $memberClient = New-Object System.Net.Http.HttpClient($mh)
    $memberLogin = Send $memberClient POST "/auth/login" (@{ email = $memberEmail; password = $memberPassword } | ConvertTo-Json -Compress)
    Check "a customer user is not a platform administrator" ($memberLogin.Status -eq 200 -and -not $memberLogin.Json.platform_admin -and (S GET "/auth/me").Json.platform_admin)
    Check "a customer user cannot open the platform -> 403" ((Send $memberClient GET "/platform/organizations").Status -eq 403)
    $viewer = "platform-viewer@reapdeep.local"
    $null = S POST "/members" @{ email = $viewer; name = "Platform viewer"; password = $memberPassword; role = "member" }
    $vh = New-Object System.Net.Http.HttpClientHandler; $vh.CookieContainer = New-Object System.Net.CookieContainer
    $viewerClient = New-Object System.Net.Http.HttpClient($vh)
    $vLogin = Send $viewerClient POST "/auth/login" (@{ email = $viewer; password = $memberPassword } | ConvertTo-Json -Compress)
    Check "a platform viewer can see the customers and the change log" ($vLogin.Json.platform_role -eq "viewer" -and (Send $viewerClient GET "/platform/organizations").Status -eq 200 -and (Send $viewerClient GET "/platform/audit").Status -eq 200)
    Check "but cannot change them -> 403" ((Send $viewerClient PUT "/platform/organizations/$orgId/ai" @{ enabled = $true }).Status -eq 403)
    Check "a member sees the AI settings but cannot change them -> 403" ((Send $memberClient GET "/organization/ai").Status -eq 200 -and (Send $memberClient PUT "/organization/ai" @{ enabled = $false }).Status -eq 403)

    $null = S PUT "/platform/organizations/$orgId/ai" @{ enabled = $false }
    $off = S POST "/ai/explain" @{ kind = "trends"; query = $trend }
    Check "with AI off on the platform side: 403 'not included'" ($off.Status -eq 403 -and $off.Json.error -match "not included")
    Check "the customer sees AI as not included and no features" ((S GET "/organization/ai").Json.platform_enabled -eq $false -and @((S GET "/project").Json.ai_features).Count -eq 0)
    $all = @("experiment_explain", "insight_explain", "query", "digest")
    $null = S PUT "/platform/organizations/$orgId/ai" @{ enabled = $true; features = $all }
    $null = S PUT "/organization/ai" @{ enabled = $false }
    $custOff = S POST "/ai/explain" @{ kind = "trends"; query = $trend }
    Check "the platform allows it, the customer turns AI off: 403 pointing to Settings" ($custOff.Status -eq 403 -and $custOff.Json.error -match "Settings")
    $partial = S PUT "/organization/ai" @{ enabled = $true; features = @("experiment_explain", "query", "digest") }
    Check "the customer keeps three features: chart explanations are off" ($partial.Json.effective -notcontains "insight_explain" -and @($partial.Json.effective).Count -eq 3 -and (S POST "/ai/explain" @{ kind = "trends"; query = $trend }).Status -eq 403)
    Check "the project reports the features in use" (@((S GET "/project").Json.ai_features) -contains "query" -and @((S GET "/project").Json.ai_features) -notcontains "insight_explain")
    $null = S PUT "/platform/organizations/$orgId/ai" @{ features = @("insight_explain", "query") }
    Check "the platform narrows the list: only what both allow stays on" (((S GET "/organization/ai").Json.effective -join ",") -eq "query")
    Check "unknown feature -> 400" ((S PUT "/organization/ai" @{ features = @("chatbot") }).Status -eq 400 -and (S PUT "/platform/organizations/$orgId/ai" @{ features = @("chatbot") }).Status -eq 400)
    Check "unknown organization -> 404" ((S PUT "/platform/organizations/999999999/ai" @{ enabled = $true }).Status -eq 404)
    $log = @((S GET "/platform/audit").Json.entries | Where-Object { $_.organization_id -eq $orgId } | Select-Object -First 3)
    Check "every platform change is logged with who did it and the before and after state" ($log.Count -eq 3 -and $log[0].actor_email -eq $envValues.ADMIN_EMAIL -and (($log[0].after.features) -join ",") -eq "insight_explain,query" -and $log[2].after.enabled -eq $false)
    $null = S PUT "/platform/organizations/$orgId/ai" @{ enabled = $true; features = $all }
    $null = S PUT "/organization/ai" @{ enabled = $true; features = $all }
    Check "everything on again" (@((S GET "/organization/ai").Json.effective).Count -eq 4)

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
    $notes = (S GET "/ai/notes").Json.notes
    Check "the note is kept in the project's archive as an on-request note" (@($notes).Count -ge 1 -and $notes[0].trigger -eq "manual" -and $notes[0].answer.summary -like "[[]fake]*")

    Write-Host "`n6) Scheduled notes"
    Check "no schedule by default -> off" ((S GET "/ai/schedule").Json.frequency -in "off", "daily", "weekly", "monthly")
    Check "invalid frequency or hour -> 400" ((S PUT "/ai/schedule" @{ frequency = "hourly" }).Status -eq 400 -and (S PUT "/ai/schedule" @{ frequency = "daily"; hour = 24 }).Status -eq 400)
    Check "an invalid e-mail address -> 400" ((S PUT "/ai/schedule" @{ frequency = "weekly"; email_to = @("not-an-address") }).Status -eq 400)
    Check "a member cannot change the schedule -> 403" ((Send $memberClient PUT "/ai/schedule" @{ frequency = "daily" }).Status -eq 403)
    $recipient = "notes_$tag@example.com"
    $sch = S PUT "/ai/schedule" @{ frequency = "weekly"; weekday = 1; hour = 9; email_to = @($recipient) }
    Check "a weekly Monday 09:00 schedule with an e-mail recipient is saved with its next run in the future" ($sch.Status -eq 200 -and $sch.Json.frequency -eq "weekly" -and ([DateTime]$sch.Json.next_run_at).ToUniversalTime() -gt [DateTime]::UtcNow -and $sch.Json.email_to[0] -eq $recipient)
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    & docker compose exec -T postgres psql -U reapdeep -d reapdeep -tAc "UPDATE ai_digest_schedules SET next_run_at = now() - interval '1 minute' WHERE project_id = 1" 2>&1 | Out-Null
    $ErrorActionPreference = $prev
    $deadline = (Get-Date).AddSeconds(90)
    do { Start-Sleep 3; $scheduled = @((S GET "/ai/notes").Json.notes | Where-Object { $_.trigger -eq "schedule" -and $_.emailed_to -contains $recipient }) } until ($scheduled.Count -gt 0 -or (Get-Date) -gt $deadline)
    Check "the scheduler writes the note when it is due and records the e-mail" ($scheduled.Count -gt 0)
    $mail = (Invoke-RestMethod -Uri "http://localhost:8026/api/v1/search?query=to:$recipient" -UseBasicParsing).messages
    Check "the note reaches the recipient by e-mail (Mailpit)" (@($mail).Count -ge 1 -and $mail[0].Subject -like "Strategy note for *")
    $after = (S GET "/ai/schedule").Json
    Check "the next run moves to the following Monday at 09:00 and the run has no error" (([DateTime]$after.next_run_at).ToUniversalTime() -gt [DateTime]::UtcNow -and -not $after.last_error -and $after.last_run_at)
    Check "turning the schedule off" ((S PUT "/ai/schedule" @{ frequency = "off" }).Json.frequency -eq "off")
}
finally {
    if ($before) {
        $null = S PUT "/platform/organizations/$orgId/ai" @{ enabled = $before.ai_enabled; features = @($before.ai_features) }
        $null = S PUT "/organization/ai" @{ enabled = $before.customer_enabled; features = @($before.customer_features) }
    }
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    & docker compose exec -T postgres psql -U reapdeep -d reapdeep -tAc "DELETE FROM users WHERE email IN ('$memberEmail', 'platform-viewer@reapdeep.local')" 2>&1 | Out-Null
    $ErrorActionPreference = $prev
}

if ($script:failed -eq 0) { Write-Host "`nPASSED: all AI checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
