$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$api = "http://localhost:8001/api/v1"
$reco = "http://localhost:8002"
$tag = "ff" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$identities = 6000
$tolerance = 2.5
$script:failed = 0

function Check($desc, $cond) {
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
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($null -ne $body) {
        $text = if ($body -is [string]) { $body } else { ConvertTo-Json $body -Depth 10 -Compress }
        $req.Content = New-Object System.Net.Http.StringContent($text, [Text.Encoding]::UTF8, "application/json")
    }
    $r = $client.SendAsync($req).Result
    $t = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Text = $t; Json = $(if ($t) { try { $t | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function S([string]$method, [string]$path, $body = $null, [long]$project = 1) { Send $owner $method "$api$path" $body @{ "X-Project-Id" = "$project" } }
function Login($client, [string]$email, [string]$password) {
    (Send $client POST "$api/auth/login" (@{ email = $email; password = $password })).Status
}
function Flags([string]$identity, [string]$key = "pk_dev_local", $properties = $null) {
    $url = "$reco/flags?api_key=$key&identity=$([uri]::EscapeDataString($identity))"
    if ($null -ne $properties) { $url += "&properties=" + [uri]::EscapeDataString((ConvertTo-Json $properties -Compress)) }
    $r = $plain.GetAsync($url).Result
    $t = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($t) { $t | ConvertFrom-Json } else { $null }) }
}
function Value($response, [string]$flag) {
    $p = $response.Json.flags.PSObject.Properties[$flag]
    if ($p) { $p.Value } else { "<absent>" }
}
function WaitFor([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $cond) { return $true }; Start-Sleep 1 } until ((Get-Date) -gt $deadline)
    return [bool](& $cond)
}

if ((Login $owner $envValues.ADMIN_EMAIL $envValues.ADMIN_PASSWORD) -ne 200) { throw "login failed (rate limited? wait a minute)" }
$exp = "exp-$tag"; $roll = "roll-$tag"; $off = "off-$tag"; $pro = "pro-$tag"
$created = @()

try {
    Write-Host "`n1) Validation (tag: $tag)"
    Check "key with a space -> 400" ((S POST "/flags" @{ key = "bad key" }).Status -eq 400)
    Check "key starting with '-' -> 400" ((S POST "/flags" @{ key = "-x$tag" }).Status -eq 400)
    $r = S POST "/flags" @{ key = "sum-$tag"; variants = @(@{ key = "control"; percentage = 50 }, @{ key = "test"; percentage = 40 }) }
    Check "variant percentages adding up to 90 -> 400 with a clear message" ($r.Status -eq 400 -and $r.Json.error -match "add up to 100")
    Check "a single variant -> 400" ((S POST "/flags" @{ key = "one-$tag"; variants = @(@{ key = "only"; percentage = 100 }) }).Status -eq 400)
    Check "duplicate variant keys -> 400" ((S POST "/flags" @{ key = "dup-$tag"; variants = @(@{ key = "a"; percentage = 50 }, @{ key = "a"; percentage = 50 }) }).Status -eq 400)
    Check "rollout 101 -> 400" ((S POST "/flags" @{ key = "r-$tag"; rollout_percentage = 101 }).Status -eq 400)
    Check "unknown operator -> 400" ((S POST "/flags" @{ key = "op-$tag"; conditions = @(@{ property = "plan"; op = "like"; value = "x" }) }).Status -eq 400)
    Check "'in' without a list -> 400" ((S POST "/flags" @{ key = "in-$tag"; conditions = @(@{ property = "plan"; op = "in"; value = "pro" }) }).Status -eq 400)
    Check "'gt' with text -> 400" ((S POST "/flags" @{ key = "gt-$tag"; conditions = @(@{ property = "seats"; op = "gt"; value = "many" }) }).Status -eq 400)
    Check "nothing was created by the invalid requests" (-not ((S GET "/flags").Json.flags | Where-Object { $_.key -like "*$tag" }))

    Write-Host "`n2) Create"
    $r = S POST "/flags" @{ key = $exp; name = "Checkout experiment $tag"; enabled = $true; variants = @(@{ key = "control"; percentage = 20 }, @{ key = "a"; percentage = 30 }, @{ key = "b"; percentage = 50 }) }
    Check "multivariate flag -> 201 with variants, rollout 100 and the creator" ($r.Status -eq 201 -and @($r.Json.variants).Count -eq 3 -and $r.Json.rollout_percentage -eq 100 -and $r.Json.created_by -eq $envValues.ADMIN_EMAIL)
    $expFlag = $r.Json; $created += $expFlag
    Check "the salt is never returned" (-not ($r.Json.PSObject.Properties.Name -contains "salt"))
    $rollFlag = (S POST "/flags" @{ key = $roll; enabled = $true; rollout_percentage = 30 }).Json; $created += $rollFlag
    $offFlag = (S POST "/flags" @{ key = $off }).Json; $created += $offFlag
    Check "a new flag is disabled unless enabled is sent" ($offFlag.enabled -eq $false -and $offFlag.name -eq $off)
    $proFlag = (S POST "/flags" @{ key = $pro; enabled = $true; conditions = @(@{ property = "plan"; op = "in"; value = @("pro", "team") }, @{ property = "seats"; op = "gte"; value = 5 }) }).Json; $created += $proFlag
    Check "flag with person property conditions -> created" ($proFlag.id -and @($proFlag.conditions).Count -eq 2 -and $proFlag.conditions[1].value -eq 5)
    Check "same key again -> 409" ((S POST "/flags" @{ key = $exp }).Status -eq 409)
    Check "list returns the new flags" (@((S GET "/flags").Json.flags | Where-Object { $_.key -like "*-$tag" }).Count -eq 4)
    Check "get by id" ((S GET "/flags/$($expFlag.id)").Json.key -eq $exp)
    Check "changing the key -> 400" ((S PUT "/flags/$($expFlag.id)" @{ key = "renamed-$tag" }).Status -eq 400)
    Check "unknown id -> 404" ((S PUT "/flags/999999999" @{ enabled = $true }).Status -eq 404)

    Write-Host "`n3) Access"
    Check "no credentials -> 401" ((Send $plain GET "$api/flags").Status -eq 401)
    Check "read API key can list flags" ((Send $plain GET "$api/flags" $null @{ Authorization = "Bearer $readKey" }).Status -eq 200)
    Check "read API key cannot create -> 403" ((Send $plain POST "$api/flags" @{ key = "k-$tag" } @{ Authorization = "Bearer $readKey" }).Status -eq 403)
    Check "read API key cannot delete -> 403" ((Send $plain DELETE "$api/flags/$($offFlag.id)" $null @{ Authorization = "Bearer $readKey" }).Status -eq 403)
    $memberEmail = "flags-member-$tag@example.com"; $memberPassword = [guid]::NewGuid().ToString("N").Substring(0, 16)
    $memberId = (S POST "/members" @{ email = $memberEmail; password = $memberPassword; role = "member" }).Json.user_id
    $member = New-Client
    if ((Login $member $memberEmail $memberPassword) -eq 200) {
        $mh = @{ "X-Project-Id" = "1" }
        Check "member can list flags" ((Send $member GET "$api/flags" $null $mh).Status -eq 200)
        Check "member cannot create, update or delete -> 403" ((Send $member POST "$api/flags" @{ key = "m-$tag" } $mh).Status -eq 403 -and (Send $member PUT "$api/flags/$($offFlag.id)" @{ enabled = $true } $mh).Status -eq 403 -and (Send $member DELETE "$api/flags/$($offFlag.id)" $null $mh).Status -eq 403)
    }
    else { Check "member signs in" $false }
    $null = S DELETE "/members/$memberId"

    Write-Host "`n4) Recommender /flags"
    Check "invalid api_key -> 401" ((Flags "x" "pk_nope").Status -eq 401)
    Check "missing identity -> 400" ((Flags "").Status -eq 400)
    Check "properties that are not a JSON object -> 400" (($plain.GetAsync("$reco/flags?api_key=pk_dev_local&identity=x&properties=%5B1%5D").Result.StatusCode) -eq 400)
    $ready = WaitFor { (Value (Flags "probe") $pro) -ne "<absent>" } 20
    Check "new flags reach the Recommender's cache within 20 s" $ready

    Write-Host "`n5) Distribution over $identities identities (tolerance +-$tolerance points)"
    $counts = @{ control = 0; a = 0; b = 0 }; $rolled = 0; $first = @{}
    $sw = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt $identities; $i++) {
        $id = "anon-$tag-$i"
        $r = Flags $id
        $v = Value $r $exp
        if ($counts.ContainsKey("$v")) { $counts["$v"]++ }
        if ((Value $r $roll) -eq $true) { $rolled++ }
        if ($i -lt 300) { $first[$id] = "$v|$(Value $r $roll)" }
    }
    $sw.Stop()
    $pct = @{}; foreach ($k in $counts.Keys) { $pct[$k] = [math]::Round(100.0 * $counts[$k] / $identities, 2) }
    $rollPct = [math]::Round(100.0 * $rolled / $identities, 2)
    Write-Host ("  control {0}% / a {1}% / b {2}% (expected 20/30/50); rollout {3}% (expected 30); {4} requests in {5:N1} s" -f $pct.control, $pct.a, $pct.b, $rollPct, $identities, $sw.Elapsed.TotalSeconds)
    Check "every identity got one of the 3 variants" (($counts.control + $counts.a + $counts.b) -eq $identities)
    Check "variant shares match 20/30/50 -> $($pct.control)/$($pct.a)/$($pct.b)" ([math]::Abs($pct.control - 20) -le $tolerance -and [math]::Abs($pct.a - 30) -le $tolerance -and [math]::Abs($pct.b - 50) -le $tolerance)
    Check "boolean rollout share matches 30% -> $rollPct%" ([math]::Abs($rollPct - 30) -le $tolerance)

    Write-Host "`n6) Same identity, same variant"
    $changed = @($first.Keys | Where-Object { $r = Flags $_ "pk_dev_local" @{ plan = "pro"; seats = 9 }; "$(Value $r $exp)|$(Value $r $roll)" -ne $first[$_] }).Count
    Check "300 identities asked again (with different properties) keep their variant and rollout -> $changed changed" ($changed -eq 0)

    Write-Host "`n7) Disabled flags"
    Check "a disabled flag is left out of the response" ((Value (Flags "anon-$tag-1") $off) -eq "<absent>")
    $null = S PUT "/flags/$($offFlag.id)" @{ enabled = $true }
    Check "enabling it -> true for everyone (rollout 100)" (WaitFor { (Value (Flags "anon-$tag-1") $off) -eq $true } 20)
    $updated = S PUT "/flags/$($expFlag.id)" @{ enabled = $false }
    Check "disabling the experiment keeps its variants and records who changed it" ($updated.Status -eq 200 -and @($updated.Json.variants).Count -eq 3 -and $updated.Json.updated_by -eq $envValues.ADMIN_EMAIL)
    Check "disabled experiment returns no variant (getFlag -> undefined)" (WaitFor { (Value (Flags "anon-$tag-1") $exp) -eq "<absent>" } 20)
    $null = S PUT "/flags/$($expFlag.id)" @{ enabled = $true }
    Check "re-enabled experiment gives the same identity the same variant" (WaitFor { "$(Value (Flags "anon-$tag-0") $exp)|$(Value (Flags "anon-$tag-0") $roll)" -eq $first["anon-$tag-0"] } 20)

    Write-Host "`n8) Person property targeting (plan in [pro, team] AND seats >= 5)"
    Check "no properties -> false" ((Value (Flags "p-$tag") $pro) -eq $false)
    Check "plan=pro, seats=5 -> true" ((Value (Flags "p-$tag" "pk_dev_local" @{ plan = "pro"; seats = 5 }) $pro) -eq $true)
    Check "plan=Team (case-insensitive), seats='12' as text -> true" ((Value (Flags "p-$tag" "pk_dev_local" @{ plan = "Team"; seats = "12" }) $pro) -eq $true)
    Check "plan=free, seats=50 -> false" ((Value (Flags "p-$tag" "pk_dev_local" @{ plan = "free"; seats = 50 }) $pro) -eq $false)
    Check "plan=pro, seats=3 -> false" ((Value (Flags "p-$tag" "pk_dev_local" @{ plan = "pro"; seats = 3 }) $pro) -eq $false)

    Write-Host "`n8b) Cohort targeting"
    $beta = (S POST "/cohorts" @{ name = "Beta $tag"; kind = "static"; members = @("beta-$tag-1", "beta-$tag-2") }).Json
    Check "unknown cohort id -> 400" ((S POST "/flags" @{ key = "nc-$tag"; conditions = @(@{ property = '$cohort'; op = "in_cohort"; value = 999999999 }) }).Status -eq 400)
    Check "in_cohort without a cohort id -> 400" ((S POST "/flags" @{ key = "nc2-$tag"; conditions = @(@{ property = '$cohort'; op = "in_cohort" }) }).Status -eq 400)
    $inKey = "beta-$tag"; $outKey = "nobeta-$tag"
    $inFlag = (S POST "/flags" @{ key = $inKey; enabled = $true; conditions = @(@{ property = '$cohort'; op = "in_cohort"; value = $beta.id }) }).Json; $created += $inFlag
    $outFlag = (S POST "/flags" @{ key = $outKey; enabled = $true; conditions = @(@{ property = '$cohort'; op = "not_in_cohort"; value = $beta.id }) }).Json; $created += $outFlag
    Check "flags with cohort conditions are created" ($inFlag.id -and $outFlag.id -and $inFlag.conditions[0].op -eq "in_cohort")
    $synced = WaitFor { (Value (Flags "beta-$tag-1") $inKey) -eq $true } 120
    Check "a cohort member gets the flag once the worker syncs the cohort" $synced
    Check "the other member too, and not_in_cohort is off for them" ((Value (Flags "beta-$tag-2") $inKey) -eq $true -and (Value (Flags "beta-$tag-1") $outKey) -eq $false)
    Check "someone outside the cohort: in_cohort off, not_in_cohort on" ((Value (Flags "stranger-$tag") $inKey) -eq $false -and (Value (Flags "stranger-$tag") $outKey) -eq $true)
    $blocked = S DELETE "/cohorts/$($beta.id)"
    Check "a cohort used by an enabled flag cannot be deleted -> 409" ($blocked.Status -eq 409 -and $blocked.Json.error -match $inKey)
    Check "the refused delete leaves the flag targeting intact" ((Value (Flags "beta-$tag-1") $inKey) -eq $true)
    $gone = (S POST "/cohorts" @{ name = "Gone $tag"; kind = "static"; members = @("gone-$tag-1") }).Json
    $null = S DELETE "/cohorts/$($gone.id)"
    Check "a deleted cohort cannot be targeted -> 400" ((S POST "/flags" @{ key = "gone-$tag"; conditions = @(@{ property = '$cohort'; op = "in_cohort"; value = $gone.id }) }).Status -eq 400)

    Write-Host "`n9) Project isolation"
    $other = (S POST "/projects" @{ name = "Flags $tag" }).Json
    Check "second project created" ($other.id -gt 1 -and $other.public_key -like "pk_*")
    $null = S POST "/flags" @{ key = $exp; enabled = $true; rollout_percentage = 0 } $other.id
    Check "the same key can exist in another project" (@((S GET "/flags" $null $other.id).Json.flags | Where-Object { $_.key -eq $exp }).Count -eq 1)
    Check "the other project does not see project 1's flags in its list" (-not ((S GET "/flags" $null $other.id).Json.flags | Where-Object { $_.key -in @($roll, $off, $pro) }))
    Check "project 1's flag ids are not reachable with the other project's header -> 404" ((S GET "/flags/$($expFlag.id)" $null $other.id).Status -eq 404 -and (S DELETE "/flags/$($rollFlag.id)" $null $other.id).Status -eq 404)
    $seen = WaitFor { (Flags "anon-$tag-0" $other.public_key).Json.flags.PSObject.Properties.Name -contains $exp } 45
    $o = Flags "anon-$tag-0" $other.public_key
    Check "the other project's public key (known to the Recommender within 30 s) gets only its own flag (rollout 0 -> false)" ($seen -and (Value $o $exp) -eq $false -and (Value $o $roll) -eq "<absent>" -and (Value $o $pro) -eq "<absent>")
    Check "project 1 still serves its variant for the same key" ((Value (Flags "anon-$tag-0") $exp) -in @("control", "a", "b"))

    Write-Host "`n10) Exposure event reaches StarRocks with flag key and variant"
    $variant = Value (Flags "anon-$tag-0") $exp
    $batch = '{"api_key":"pk_dev_local","batch":[{"event":"$feature_flag_called","distinct_id":"anon-' + $tag + '-0","properties":{"$feature_flag":"' + $exp + '","$feature_flag_response":"' + $variant + '"}}]}'
    $resp = $plain.PostAsync("http://localhost:8000/capture", (New-Object System.Net.Http.StringContent($batch, [Text.Encoding]::UTF8, "text/plain"))).Result
    Check "capture -> 202" ([int]$resp.StatusCode -eq 202)
    $query = @{ series = @(@{ event = '$feature_flag_called' }); filters = @(@{ scope = "event"; property = '$feature_flag'; op = "eq"; value = $exp }, @{ scope = "event"; property = '$feature_flag_response'; op = "eq"; value = "$variant" }) }
    $stored = WaitFor { (Send $plain POST "$api/query/trends" $query @{ Authorization = "Bearer $readKey"; "Cache-Control" = "no-cache" }).Json.results[0].total -eq 1 } 60
    Check "trends filtered on `$feature_flag and `$feature_flag_response count the exposure" $stored

    Write-Host "`n11) Delete"
    Check "delete -> 204" ((S DELETE "/flags/$($rollFlag.id)").Status -eq 204)
    Check "deleted flag -> 404" ((S GET "/flags/$($rollFlag.id)").Status -eq 404)
    Check "deleted flag leaves the /flags response" (WaitFor { (Value (Flags "anon-$tag-0") $roll) -eq "<absent>" } 20)
    $created = @($created | Where-Object { $_.id -ne $rollFlag.id })
}
finally {
    foreach ($f in $created) { $null = S DELETE "/flags/$($f.id)" }
    if ($other) { $null = S DELETE "/project" @{ confirm = $other.name } $other.id }
}
Check "flags of this run removed" (-not ((S GET "/flags").Json.flags | Where-Object { $_.key -like "*-$tag" }))

if ($script:failed -eq 0) { Write-Host "`nPASSED: all feature flag checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
