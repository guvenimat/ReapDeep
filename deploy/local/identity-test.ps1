$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
Add-Type -AssemblyName System.Net.Http

$t = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$a1 = "anon1_$t"; $a2 = "anon2_$t"; $a3 = "anon3_$t"; $a4 = "anon4_$t"
$u1 = "user1_$t"; $u2 = "user2_$t"; $u3 = "user3_$t"
$script:failed = 0
$http = New-Object System.Net.Http.HttpClient

function Send([hashtable[]]$events) {
    $body = @{ api_key = "pk_dev_local"; batch = $events } | ConvertTo-Json -Depth 6 -Compress
    $content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "text/plain")
    $r = $http.PostAsync("http://localhost:8000/capture", $content).Result
    if ([int]$r.StatusCode -ne 202) { throw "capture: HTTP $([int]$r.StatusCode)" }
}
function Ev($name, $distinctId, $props = @{}) { @{ event = $name; distinct_id = $distinctId; properties = $props } }
function Identify($user, $anon, $set) {
    $p = @{}
    if ($anon) { $p['$anon_distinct_id'] = $anon }
    if ($set) { $p['$set'] = $set }
    Ev '$identify' $user $p
}

function Pg($sql) { (docker compose exec -T postgres psql -U reapdeep -d reapdeep -tAc $sql | Out-String).Trim() }
function Sr($sql) { (docker compose exec -T starrocks mysql "-h127.0.0.1" -P9030 -uroot -N -e $sql | Out-String).Trim() }

function PersonOf($d) {
    Pg "SELECT p.id || '|' || p.is_identified || '|' || p.is_deleted || '|' || p.properties::text FROM person_distinct_ids d JOIN persons p ON p.id = d.person_id WHERE d.project_id = 1 AND d.distinct_id = '$d'"
}
function PersonId($d) { (PersonOf $d).Split('|')[0] }

function WaitFor($desc, [scriptblock]$cond, $seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $cond) { return } Start-Sleep -Milliseconds 300 }
    Write-Host "  TIMEOUT: $desc" -ForegroundColor Red; $script:failed++
}
function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}

Write-Host "`n1) Anonymous visitor with pageviews only: no person record"
Send @((Ev '$pageview' $a1), (Ev '$pageview' $a1))
Start-Sleep 2
Check "no person for $a1" ((PersonOf $a1) -eq "")

Write-Host "`n2) Anonymous visitor with `$set: unidentified person"
Send @((Ev '$set' $a1 @{ '$set' = @{ first_source = "google"; plan = "free" } }))
WaitFor "person for $a1" { (PersonOf $a1) -ne "" }
$p1 = PersonId $a1
Check "unidentified, plan=free" ((PersonOf $a1) -like "*|false|false|*""plan"": ""free""*")

Write-Host "`n3) Case C: identify($u1, anon=$a1): user claims the anonymous person"
Send @((Identify $u1 $a1 @{ email = "u1@example.com"; plan = "pro" }))
WaitFor "person for $u1" { (PersonOf $u1) -ne "" }
Check "$u1 and $a1 share person $p1" ((PersonId $u1) -eq $p1)
Check "identified, plan=pro (new value wins), first_source kept" ((PersonOf $u1) -like "*|true|false|*""plan"": ""pro""*" -and (PersonOf $u1) -like "*first_source*")
Send @((Ev "button_click" $u1 @{ button = "purchase" }))

Write-Host "`n4) Case D (merge): $a3 is an unidentified person, identify($u1, anon=$a3)"
Send @((Ev '$pageview' $a3), (Ev '$set' $a3 @{ '$set' = @{ theme = "dark"; plan = "trial" } }))
WaitFor "person for $a3" { (PersonOf $a3) -ne "" }
$p3 = PersonId $a3
Send @((Identify $u1 $a3 $null))
WaitFor "$a3 -> $p1" { (PersonId $a3) -eq $p1 }
Check "$a3 now belongs to $p1" ((PersonId $a3) -eq $p1)
Check "old person $p3 marked deleted" ((Pg "SELECT is_deleted FROM persons WHERE id = '$p3'") -eq "t")
Check "properties: theme added, plan=pro kept (identified person wins)" ((PersonOf $u1) -like "*""plan"": ""pro""*" -and (PersonOf $u1) -like "*""theme"": ""dark""*")

Write-Host "`n5) Case A: identify($u2, anon=$a2), both new: one identified person"
Send @((Ev '$pageview' $a2), (Identify $u2 $a2 @{ email = "u2@example.com" }))
WaitFor "person for $u2" { (PersonOf $u2) -ne "" }
$p2 = PersonId $u2
Check "$a2 and $u2 share an identified person" ((PersonId $a2) -eq $p2 -and (PersonOf $u2) -like "*|true|*")

Write-Host "`n6) Conflict: identify($u2, anon=$a1): $a1 belongs to another identified person, merge rejected"
$v1 = Pg "SELECT version FROM persons WHERE id = '$p1'"
Send @((Identify $u2 $a1 @{ last_login = "today" }))
WaitFor "properties of $u2 updated" { (PersonOf $u2) -like "*last_login*" }
Check "$a1 still belongs to $p1" ((PersonId $a1) -eq $p1)
Check "$p1 unchanged (version $v1)" ((Pg "SELECT version FROM persons WHERE id = '$p1'") -eq $v1)

Write-Host "`n7) Case C (conflict): identify($u3, anon=$a1): new person for $u3, $a1 is not moved"
Send @((Identify $u3 $a1 $null))
WaitFor "person for $u3" { (PersonOf $u3) -ne "" }
Check "$u3 is a separate person, $a1 still on $p1" ((PersonId $u3) -ne $p1 -and (PersonId $a1) -eq $p1)

Write-Host "`n8) Case B: identify($u1, anon=$a4): new anonymous id joins the existing user"
Send @((Ev '$pageview' $a4), (Identify $u1 $a4 $null))
WaitFor "$a4 -> $p1" { (PersonId $a4) -eq $p1 }
Check "$a4 belongs to $p1" ((PersonId $a4) -eq $p1)

Write-Host "`n9) Reprocessing: the same identify again changes nothing"
$v1 = Pg "SELECT version FROM persons WHERE id = '$p1'"
Send @((Identify $u1 $a4 $null))
Start-Sleep 2
Check "version unchanged ($v1)" ((Pg "SELECT version FROM persons WHERE id = '$p1'") -eq $v1)

Write-Host "`n10) StarRocks: past anonymous and new events under one person (LEFT JOIN + COALESCE)"
$ids = "'$a1','$a3','$a4','$u1'"
$sql = "SELECT COALESCE(p.person_id, e.distinct_id), count(*) FROM analytics.events e LEFT JOIN analytics.person_distinct_ids p " +
       "ON e.project_id = p.project_id AND e.distinct_id = p.distinct_id WHERE e.project_id = 1 AND e.distinct_id IN ($ids) GROUP BY 1"
WaitFor "one person with 11 events in StarRocks" { $r = Sr $sql; $r -match "^$p1\s+11$" } 40
$r = Sr $sql
Write-Host "  $r"
Check "a1/a3/a4/u1 events under one person ($p1)" ($r -match "^$p1\s+\d+$")
$cnt = [int]($r -split '\s+')[1]
Check "event count 11 ($cnt)" ($cnt -eq 11)

$sp = Sr "SELECT is_identified, is_deleted, get_json_string(properties, '$.plan'), get_json_string(properties, '$.theme') FROM analytics.persons WHERE project_id = 1 AND person_id = '$p1'"
Check "StarRocks persons: identified, plan=pro, theme=dark ($sp)" ($sp -match "^1\s+0\s+pro\s+dark$")
$sd = Sr "SELECT is_deleted FROM analytics.persons WHERE project_id = 1 AND person_id = '$p3'"
Check "StarRocks persons: merged person marked deleted ($sd)" ($sd -eq "1")

$plan = Sr "EXPLAIN SELECT count(*) FROM analytics.events e LEFT JOIN analytics.person_distinct_ids p ON e.project_id = p.project_id AND e.distinct_id = p.distinct_id WHERE e.project_id = 1"
Check "LEFT JOIN runs colocated" ($plan -match "COLOCATE")
Check "outbox drained" ((Pg "SELECT count(*) FROM person_outbox") -eq "0")

if ($script:failed -eq 0) { Write-Host "`nPASSED: all identity scenarios" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
