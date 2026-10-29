$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$api = "http://localhost:8001"
$tag = "del" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$anonA = "anonA_$tag"; $user = "user_$tag"; $control = "control_$tag"; $anonOnly = "anon_only_$tag"
$multiA = "multiA_$tag"; $multiU = "multiU_$tag"; $returnAnon = "anonR_$tag"
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
function Key([string]$method, [string]$path, [string]$body = $null) { Send $plain $method $path $body @{ Authorization = "Bearer $readKey" } }
function Sess([string]$method, [string]$path, [string]$body = $null) { Send $session $method $path $body @{ "X-Project-Id" = "1" } }

function Capture([string]$batchJson) {
    $content = New-Object System.Net.Http.StringContent(('{"api_key":"pk_dev_local","batch":' + $batchJson + '}'), [Text.Encoding]::UTF8, "text/plain")
    $r = $plain.PostAsync("http://localhost:8000/capture", $content).Result
    if ([int]$r.StatusCode -ne 202) { throw "capture HTTP $([int]$r.StatusCode)" }
}
function EventCount([string]$distinctId) {
    $from = (Get-Date).ToUniversalTime().AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ")
    @((Key GET "/api/v1/events?distinct_id=$distinctId&date_from=$from&limit=200").Json.events).Count
}
function StarRocks([string]$sql) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $out = & docker compose exec -T starrocks mysql "--host=127.0.0.1" "--port=9030" "--user=root" -N -e $sql 2>&1 | Out-String
    $ErrorActionPreference = $prev
    $out.Trim()
}
function Postgres([string]$sql) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $out = & docker compose exec -T postgres psql -U reapdeep -d reapdeep -tAc $sql 2>&1 | Out-String
    $ErrorActionPreference = $prev
    $out.Trim()
}
function WaitFor([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $cond) { return $true }; Start-Sleep 3 } until ((Get-Date) -gt $deadline)
    return [bool](& $cond)
}

$login = '{"email":"' + $envValues.ADMIN_EMAIL + '","password":"' + $envValues.ADMIN_PASSWORD + '"}'
if ((Send $session POST "/api/v1/auth/login" $login).Status -ne 200) { throw "login failed (rate limited? wait a minute)" }

Write-Host "`n1) Test data (tag: $tag)"
Capture "[{`"event`":`"page_$tag`",`"distinct_id`":`"$anonA`"},{`"event`":`"page_$tag`",`"distinct_id`":`"$anonA`"}]"
Start-Sleep -Milliseconds 300
Capture "[{`"event`":`"`$identify`",`"distinct_id`":`"$user`",`"properties`":{`"`$anon_distinct_id`":`"$anonA`",`"`$set`":{`"email`":`"$user@example.com`"}}}]"
Start-Sleep -Milliseconds 300
Capture "[{`"event`":`"page_$tag`",`"distinct_id`":`"$user`"},{`"event`":`"page_$tag`",`"distinct_id`":`"$control`"},{`"event`":`"page_$tag`",`"distinct_id`":`"$anonOnly`"}]"
Capture "[{`"event`":`"page_$tag`",`"distinct_id`":`"$multiA`"}]"
Start-Sleep -Milliseconds 300
Capture "[{`"event`":`"`$identify`",`"distinct_id`":`"$multiU`",`"properties`":{`"`$anon_distinct_id`":`"$multiA`",`"`$set`":{`"email`":`"$multiU@example.com`"}}},{`"event`":`"page_$tag`",`"distinct_id`":`"$multiU`"}]"

$ready = WaitFor {
    $script:person = @((Key GET "/api/v1/persons?search=$user@example.com").Json.persons)[0]
    $script:multi = @((Key GET "/api/v1/persons?search=$multiU@example.com").Json.persons)[0]
    $script:person -and $script:multi -and (EventCount $anonA) -eq 2 -and (EventCount $multiU) -eq 2 -and (EventCount $anonOnly) -eq 1 -and
        (StarRocks "SELECT count(*) FROM analytics.person_distinct_ids WHERE distinct_id IN ('$anonA','$user') AND is_deleted = 0") -eq "2"
} 60
Check "data ready (person with 2 distinct_ids, 4 events; StarRocks mapping synced)" $ready
$personId = $person.id

Write-Host "`n2) Creating deletion requests"
Check "read API key cannot request deletion -> 403" ((Key POST "/api/v1/deletion-requests" "{`"target_type`":`"person`",`"target_id`":`"$personId`"}").Status -eq 403)
Check "invalid target_type -> 400" ((Sess POST "/api/v1/deletion-requests" '{"target_type":"everything","target_id":"x"}').Status -eq 400)
$r1 = Sess POST "/api/v1/deletion-requests" "{`"target_type`":`"person`",`"target_id`":`"$personId`"}"
Check "owner session: person deletion request -> 202 (pending)" ($r1.Status -eq 202 -and $null -eq $r1.Json.processed_at -and $r1.Json.requested_by -eq $envValues.ADMIN_EMAIL)
$r2 = Sess POST "/api/v1/deletion-requests" "{`"target_type`":`"person`",`"target_id`":`"$personId`"}"
Check "repeated request returns the same pending request (id $($r1.Json.id))" ($r2.Json.id -eq $r1.Json.id)
$r3 = Sess POST "/api/v1/deletion-requests" "{`"target_type`":`"distinct_id`",`"target_id`":`"$anonOnly`"}"
$r4 = Sess POST "/api/v1/deletion-requests" "{`"target_type`":`"distinct_id`",`"target_id`":`"$multiA`"}"
Check "distinct_id deletion requests -> 202" ($r3.Status -eq 202 -and $r4.Status -eq 202)
$ids = @($r1.Json.id, $r3.Json.id, $r4.Json.id)

Write-Host "`n3) Batch job (interval 30 s, minimum age 20 s locally)"
$done = WaitFor {
    $list = (Key GET "/api/v1/deletion-requests").Json.deletion_requests
    @($list | Where-Object { $ids -contains $_.id -and $_.processed_at }).Count -eq 3
} 120
Check "all three requests processed" $done

Write-Host "`n4) Person deletion"
Check "events of both distinct_ids deleted from StarRocks" ((EventCount $anonA) -eq 0 -and (EventCount $user) -eq 0)
Check "control user's events untouched" ((EventCount $control) -eq 1)
Check "person no longer returned by the API -> 404" ((Key GET "/api/v1/persons/$personId").Status -eq 404)
Check "PostgreSQL: person and distinct_id rows removed" ((Postgres "SELECT (SELECT count(*) FROM persons WHERE id = '$personId') + (SELECT count(*) FROM person_distinct_ids WHERE distinct_id IN ('$anonA','$user'))") -eq "0")
$synced = WaitFor { (StarRocks "SELECT count(*) FROM analytics.person_distinct_ids WHERE distinct_id IN ('$anonA','$user') AND is_deleted = 0") -eq "0" } 30
Check "StarRocks: distinct_id mappings deleted" $synced
$erased = WaitFor { (StarRocks "SELECT count(*) FROM analytics.persons WHERE person_id = '$personId' AND (is_deleted = 0 OR json_length(properties) > 0)") -eq "0" } 30
Check "StarRocks: no person properties left" $erased
$t = Key POST "/api/v1/query/trends" "{`"series`":[{`"event`":`"page_$tag`",`"metric`":`"unique_users`"}]}"
Check "trends: only the remaining users are counted (control, anon_only deleted, multi person) -> $($t.Json.results[0].total)" ($t.Json.results[0].total -eq 2)

Write-Host "`n5) distinct_id deletion"
Check "anonymous distinct_id: events deleted" ((EventCount $anonOnly) -eq 0)
Check "one distinct_id of a person: only its events deleted" ((EventCount $multiA) -eq 0 -and (EventCount $multiU) -eq 2)
$m = (Key GET "/api/v1/persons/$($multi.id)").Json
Check "person kept with the remaining distinct_id" ($m.id -eq $multi.id -and (@($m.distinct_ids) -join ",") -eq $multiU)

Write-Host "`n6) A deleted user comes back"
Capture "[{`"event`":`"`$identify`",`"distinct_id`":`"$user`",`"properties`":{`"`$anon_distinct_id`":`"$returnAnon`",`"`$set`":{`"plan`":`"free`"}}},{`"event`":`"page_$tag`",`"distinct_id`":`"$user`"}]"
$back = WaitFor { (StarRocks "SELECT count(*) FROM analytics.person_distinct_ids WHERE distinct_id IN ('$user','$returnAnon') AND is_deleted = 0") -eq "2" } 60
Check "new mapping reaches StarRocks despite the deletion tombstone (time-based version)" $back
$p2 = @((Key GET "/api/v1/persons?search=$user").Json.persons)[0]
Check "a new person is created, without the old properties" ($p2 -and $p2.id -ne $personId -and -not $p2.properties.email -and $p2.properties.plan -eq "free")

Write-Host "`n7) Tombstone purge (60 s locally)"
$purged = WaitFor { (StarRocks "SELECT count(*) FROM analytics.person_distinct_ids WHERE distinct_id IN ('$anonA','$multiA')") -eq "0" -and (StarRocks "SELECT count(*) FROM analytics.persons WHERE person_id = '$personId'") -eq "0" } 150
Check "StarRocks tombstones are physically removed" $purged

if ($script:failed -eq 0) { Write-Host "`nPASSED: all deletion checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
