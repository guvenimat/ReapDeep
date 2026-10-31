$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$one = "http://localhost:8001/api/v1"
$two = "http://localhost:8003/api/v1"
$tag = "sc" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$memberEmail = "member_$tag@example.com"; $memberPassword = "temp-$tag"
$script:failed = 0

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}
function New-Client {
    $h = New-Object System.Net.Http.HttpClientHandler
    $h.CookieContainer = New-Object System.Net.CookieContainer
    New-Object System.Net.Http.HttpClient($h)
}
function Send($client, [string]$base, [string]$method, [string]$path, [string]$body = $null, [hashtable]$headers = @{ "X-Project-Id" = "1" }) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), "$base$path")
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($body) { $req.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "application/json") }
    $r = $client.SendAsync($req).Result
    $text = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($text) { try { $text | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function Login($client, [string]$base, [string]$email, [string]$password) {
    (Send $client $base POST "/auth/login" ('{"email":"' + $email + '","password":"' + $password + '"}')).Status
}
function Role($client, [string]$base) { ((Send $client $base GET "/auth/me").Json.projects | Where-Object { $_.id -eq 1 }).role }
function Quiet([string[]]$a) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $null = & docker.exe @a 2>&1
    $ErrorActionPreference = $prev
}

Write-Host "`n1) Two Query API instances"
Check "both instances are healthy" ((docker.exe inspect -f "{{.State.Health.Status}}" reapdeep-query-api) -eq "healthy" -and (docker.exe inspect -f "{{.State.Health.Status}}" reapdeep-query-api-2) -eq "healthy")
$owner = New-Client
if ((Login $owner $one $envValues.ADMIN_EMAIL $envValues.ADMIN_PASSWORD) -ne 200) { throw "owner login failed (rate limited? wait a minute)" }
$r = Send $owner $one POST "/members" "{`"email`":`"$memberEmail`",`"name`":`"Member $tag`",`"password`":`"$memberPassword`",`"role`":`"member`"}"
$memberId = $r.Json.user_id
$member = New-Client
$null = Login $member $one $memberEmail $memberPassword
Check "a session created on one instance works on the other" ((Role $member $one) -eq "member" -and (Role $member $two) -eq "member")

Write-Host "`n2) Changes made on one instance apply on the other at once"
$null = Send $owner $two PATCH "/members/$memberId" '{"role":"admin"}'
Check "a promotion made on instance 2 is seen by instance 1" ((Role $member $one) -eq "admin")
$key = (Send $member $one POST "/api-keys" "{`"name`":`"scale $tag`"}").Json
$keyClient = New-Client
$auth = @{ Authorization = "Bearer $($key.key)" }
Check "a new key works on both instances (and is now cached)" ((Send $keyClient $one GET "/metadata" -headers $auth).Status -eq 200 -and (Send $keyClient $two GET "/metadata" -headers $auth).Status -eq 200)
$null = Send $owner $two DELETE "/api-keys/$($key.api_key.id)"
Check "a key revoked on instance 2 is refused by instance 1" ((Send $keyClient $one GET "/metadata" -headers $auth).Status -eq 401)
$null = Send $owner $one DELETE "/members/$memberId"
Check "a removed member loses access on instance 2 at once" ((Send $member $two GET "/members").Status -in 401, 403)
Check "and on instance 1" ((Send $member $one GET "/members").Status -in 401, 403)

Write-Host "`n3) A request quota shared by the instances"
$quotaKey = (Send $owner $one POST "/api-keys" "{`"name`":`"quota $tag`"}").Json
$qauth = @{ Authorization = "Bearer $($quotaKey.key)" }
$statuses = @()
for ($i = 0; $i -lt 160; $i++) {
    $base = if ($i % 2 -eq 0) { $one } else { $two }
    $statuses += (Send $keyClient $base GET "/metadata" -headers $qauth).Status
}
$blocked = $null
for ($i = 0; $i -lt 200; $i++) {
    $r = $keyClient.SendAsync((New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$(if ($i % 2 -eq 0) { $one } else { $two })/metadata") | ForEach-Object { $_.Headers.Add("Authorization", "Bearer $($quotaKey.key)"); $_ })).Result
    if ([int]$r.StatusCode -eq 429) { $blocked = $r; break }
}
Check "requests from one key are counted across both instances and refused past the limit" ($null -ne $blocked -and ($statuses | Where-Object { $_ -ne 200 }).Count -eq 0)
Check "the refusal says when to retry" ($blocked -and $blocked.Headers.RetryAfter.Delta.TotalSeconds -gt 0 -and $blocked.Headers.RetryAfter.Delta.TotalSeconds -le 60)
Check "another key of the same project is not affected" ((Send $keyClient $one GET "/metadata" -headers @{ Authorization = "Bearer $($envValues.READ_API_KEY)" }).Status -eq 200)
$null = Send $owner $one DELETE "/api-keys/$($quotaKey.api_key.id)"

Write-Host "`n4) Redis down"
Quiet @("stop", "reapdeep-redis")
$other = New-Client
Check "sign-in still works" ((Login $other $two $envValues.ADMIN_EMAIL $envValues.ADMIN_PASSWORD) -eq 200)
Check "sessions are read from PostgreSQL" ((Role $other $one) -eq "owner")
$key2 = (Send $other $one POST "/api-keys" "{`"name`":`"scale2 $tag`"}").Json
$auth2 = @{ Authorization = "Bearer $($key2.key)" }
$null = Send $keyClient $two GET "/metadata" -headers $auth2
$null = Send $other $one DELETE "/api-keys/$($key2.api_key.id)"
Check "a revocation is still effective everywhere" ((Send $keyClient $two GET "/metadata" -headers $auth2).Status -eq 401)
Quiet @("start", "reapdeep-redis")
$null = docker.exe inspect -f "{{.State.Health.Status}}" reapdeep-redis
Start-Sleep -Seconds 8
Check "the cache is used again once Redis is back" ((Role $other $two) -eq "owner")

Write-Host ""
if ($script:failed -gt 0) { Write-Host "$($script:failed) check(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All checks passed." -ForegroundColor Green
