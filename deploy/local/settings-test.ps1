$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$api = "http://localhost:3000/api/v1"
$tag = "st" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
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
function Send($client, [string]$method, [string]$path, [string]$body = $null, [hashtable]$headers = @{ "X-Project-Id" = "1" }) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), "$api$path")
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($body) { $req.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "application/json") }
    $r = $client.SendAsync($req).Result
    $text = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($text) { try { $text | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function Login($client, [string]$email, [string]$password) {
    (Send $client POST "/auth/login" ('{"email":"' + $email + '","password":"' + $password + '"}')).Status
}

$owner = New-Client
if ((Login $owner $envValues.ADMIN_EMAIL $envValues.ADMIN_PASSWORD) -ne 200) { throw "owner login failed (rate limited? wait a minute)" }
$keyClient = New-Object System.Net.Http.HttpClient

Write-Host "`n1) Access rules"
Check "read API key cannot list members -> 403" ((Send $keyClient GET "/members" -headers @{ Authorization = "Bearer $readKey" }).Status -eq 403)
Check "read API key cannot list API keys -> 403" ((Send $keyClient GET "/api-keys" -headers @{ Authorization = "Bearer $readKey" }).Status -eq 403)
$members = (Send $owner GET "/members").Json.members
$ownerId = ($members | Where-Object { $_.email -eq $envValues.ADMIN_EMAIL }).user_id
Check "owner lists members (self = owner)" ($ownerId -and ($members | Where-Object { $_.user_id -eq $ownerId }).role -eq "owner")

Write-Host "`n2) Adding a member"
Check "new user without a password -> 400" ((Send $owner POST "/members" "{`"email`":`"$memberEmail`",`"role`":`"member`"}").Status -eq 400)
$r = Send $owner POST "/members" "{`"email`":`"$memberEmail`",`"name`":`"Member $tag`",`"password`":`"$memberPassword`",`"role`":`"member`"}"
Check "owner adds a member with a temporary password -> 201" ($r.Status -eq 201 -and $r.Json.role -eq "member")
$memberId = $r.Json.user_id
Check "adding the same user again -> 409" ((Send $owner POST "/members" "{`"email`":`"$memberEmail`",`"password`":`"$memberPassword`",`"role`":`"member`"}").Status -eq 409)

$member = New-Client
Check "member signs in with the temporary password" ((Login $member $memberEmail $memberPassword) -eq 200)
$me = (Send $member GET "/auth/me").Json
Check "member sees the organization's project with role 'member'" (($me.projects | Where-Object { $_.id -eq 1 }).role -eq "member")
Check "member can query data" ((Send $member POST "/query/trends" '{"series":[{"event":"$pageview"}]}').Status -eq 200)
Check "member can list members" ((Send $member GET "/members").Status -eq 200)
Check "member cannot add members -> 403" ((Send $member POST "/members" '{"email":"x@example.com","password":"12345678","role":"member"}').Status -eq 403)
Check "member cannot list API keys -> 403" ((Send $member GET "/api-keys").Status -eq 403)
Check "member cannot request deletion -> 403" ((Send $member POST "/deletion-requests" '{"target_type":"distinct_id","target_id":"x"}').Status -eq 403)

Write-Host "`n3) Roles"
$r = Send $owner PATCH "/members/$memberId" '{"role":"admin"}'
Check "owner promotes the member to admin" ($r.Status -eq 200 -and $r.Json.role -eq "admin")
Check "promotion applies to the member's session immediately" (((Send $member GET "/auth/me").Json.projects | Where-Object { $_.id -eq 1 }).role -eq "admin")
Check "admin can list API keys" ((Send $member GET "/api-keys").Status -eq 200)
Check "admin cannot make someone owner -> 403" ((Send $member PATCH "/members/$memberId" '{"role":"owner"}').Status -eq 403)
Check "admin cannot change an owner -> 403" ((Send $member PATCH "/members/$ownerId" '{"role":"member"}').Status -eq 403)
Check "the last owner cannot be demoted -> 409" ((Send $owner PATCH "/members/$ownerId" '{"role":"admin"}').Status -eq 409)
Check "the last owner cannot leave -> 409" ((Send $owner DELETE "/members/$ownerId").Status -eq 409)
Check "invalid role -> 400" ((Send $owner PATCH "/members/$memberId" '{"role":"root"}').Status -eq 400)

Write-Host "`n4) Password change"
$member2 = New-Client
Check "member has a second session" ((Login $member2 $memberEmail $memberPassword) -eq 200)
Check "wrong current password -> 400" ((Send $member POST "/auth/password" '{"current_password":"wrong","new_password":"new-password-1"}').Status -eq 400)
Check "too short new password -> 400" ((Send $member POST "/auth/password" "{`"current_password`":`"$memberPassword`",`"new_password`":`"short`"}").Status -eq 400)
Check "password changed -> 204" ((Send $member POST "/auth/password" "{`"current_password`":`"$memberPassword`",`"new_password`":`"new-password-$tag`"}").Status -eq 204)
Check "current session stays signed in" ((Send $member GET "/auth/me").Status -eq 200)
Check "other sessions are signed out" ((Send $member2 GET "/auth/me").Status -eq 401)
Check "old password no longer works" ((Login (New-Client) $memberEmail $memberPassword) -eq 401)
Check "new password works" ((Login (New-Client) $memberEmail "new-password-$tag") -eq 200)

Write-Host "`n5) API keys"
$r = Send $member POST "/api-keys" "{`"name`":`"key $tag`"}"
$newKey = $r.Json.key
Check "admin creates a key; the full key is returned once" ($r.Status -eq 201 -and $newKey -cmatch '^rk_[0-9a-f]{32}$' -and $r.Json.api_key.prefix -eq $newKey.Substring(0, 7))
$listed = (Send $owner GET "/api-keys").Json.api_keys | Where-Object { $_.id -eq $r.Json.api_key.id }
Check "list shows the prefix and creator, never the key" ($listed.prefix -eq $newKey.Substring(0, 7) -and $listed.created_by -eq $memberEmail -and -not ($listed.PSObject.Properties.Name -contains "key"))
Check "the new key can query data" ((Send $keyClient GET "/metadata" -headers @{ Authorization = "Bearer $newKey" }).Status -eq 200)
Check "revoke -> 204" ((Send $owner DELETE "/api-keys/$($r.Json.api_key.id)").Status -eq 204)
Check "revoked key is rejected immediately -> 401" ((Send $keyClient GET "/metadata" -headers @{ Authorization = "Bearer $newKey" }).Status -eq 401)
Check "unknown key id -> 404" ((Send $owner DELETE "/api-keys/999999").Status -eq 404)

Write-Host "`n6) Removing a member"
Check "owner removes the member -> 204" ((Send $owner DELETE "/members/$memberId").Status -eq 204)
Check "removed member loses access to the project immediately -> 403" ((Send $member GET "/metadata").Status -eq 403)
Check "removed member has no projects" (@((Send $member GET "/auth/me").Json.projects).Count -eq 0)

Write-Host "`n7) Projects"
$r = Send $owner GET "/project"
Check "current project shows its public key" ($r.Status -eq 200 -and $r.Json.id -eq 1 -and $r.Json.public_key -eq "pk_dev_local" -and $r.Json.timezone -eq "Europe/Istanbul")
Check "read API key can read the project" ((Send $keyClient GET "/project" -headers @{ Authorization = "Bearer $readKey" }).Json.public_key -eq "pk_dev_local")
Check "read API key cannot create projects -> 403" ((Send $keyClient POST "/projects" '{"name":"x"}' -headers @{ Authorization = "Bearer $readKey" }).Status -eq 403)
Check "blank project name -> 400" ((Send $owner POST "/projects" '{"name":" "}').Status -eq 400)
Check "unknown timezone -> 400" ((Send $owner POST "/projects" ('{"name":"Project ' + $tag + '","timezone":"Mars/Olympus"}')).Status -eq 400)
$r = Send $owner POST "/projects" ('{"name":"Project ' + $tag + '"}')
$newProject = $r.Json
Check "owner creates a project -> 201 with a pk_ key" ($r.Status -eq 201 -and $newProject.public_key -cmatch '^pk_[0-9a-f]{32}$' -and $newProject.organization_id -eq 1)
Check "same name again -> 409" ((Send $owner POST "/projects" ('{"name":"project ' + $tag + '"}')).Status -eq 409)
Check "new project appears in /auth/me" (@((Send $owner GET "/auth/me").Json.projects | Where-Object { $_.id -eq $newProject.id }).Count -eq 1)
Check "new project is reachable with X-Project-Id" ((Send $owner GET "/project" -headers @{ "X-Project-Id" = "$($newProject.id)" }).Json.public_key -eq $newProject.public_key)
$capture = '{"api_key":"' + $newProject.public_key + '","batch":[{"event":"settings_test","distinct_id":"settings-' + $tag + '"}]}'
$ingest = New-Object System.Net.Http.HttpClient
$accepted = $false
$deadline = (Get-Date).AddSeconds(45)
while (-not $accepted -and (Get-Date) -lt $deadline) {
    $resp = $ingest.PostAsync("http://localhost:8000/capture", (New-Object System.Net.Http.StringContent($capture, [Text.Encoding]::UTF8, "application/json"))).Result
    if ([int]$resp.StatusCode -eq 202) { $accepted = $true } else { Start-Sleep -Seconds 3 }
}
Check "ingestion accepts the new public key within 45 s" $accepted

Write-Host "`n8) Visitor IP privacy"
function Sr($sql) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $out = (& docker compose exec -T starrocks mysql "-h127.0.0.1" -P9030 -uroot -N -e $sql 2>&1 | Out-String).Trim()
    $ErrorActionPreference = $prev
    $out
}
function IpsOf([string]$distinctId) { Sr "SELECT DISTINCT ip FROM analytics.events WHERE project_id = $($newProject.id) AND distinct_id = '$distinctId'" }
$np = @{ "X-Project-Id" = "$($newProject.id)" }
$detail = (Send $owner GET "/project" -headers $np).Json
Check "a new project anonymizes IP addresses by default and uses the server's ingestion limit" ($detail.anonymize_ip -eq $true -and $null -eq $detail.ingest_events_per_second)
$deadline = (Get-Date).AddSeconds(60)
while (-not (IpsOf "settings-$tag") -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
$anonIp = IpsOf "settings-$tag"
Check "the stored IP ends in .0 ($anonIp)" ($anonIp -match '^\d+\.\d+\.\d+\.0$')
Check "read API key cannot change project settings -> 403" ((Send $keyClient PATCH "/project" '{"anonymize_ip":false}' -headers @{ Authorization = "Bearer $readKey" }).Status -eq 403)
Check "anonymize_ip is required -> 400" ((Send $owner PATCH "/project" '{}' -headers $np).Status -eq 400)
$r = Send $owner PATCH "/project" '{"anonymize_ip":false}' -headers $np
Check "owner turns anonymization off -> 200" ($r.Status -eq 200 -and $r.Json.anonymize_ip -eq $false)
Check "the setting is stored" ((Send $owner GET "/project" -headers $np).Json.anonymize_ip -eq $false)
$fullBody = '{"api_key":"' + $newProject.public_key + '","batch":[{"event":"settings_test","distinct_id":"settings-full-' + $tag + '"}]}'
$fullIp = ""
$deadline = (Get-Date).AddSeconds(75)
while ($fullIp -notmatch '^\d+\.\d+\.\d+\.[1-9]\d*$' -and (Get-Date) -lt $deadline) {
    $null = $ingest.PostAsync("http://localhost:8000/capture", (New-Object System.Net.Http.StringContent($fullBody, [Text.Encoding]::UTF8, "text/plain"))).Result
    Start-Sleep -Seconds 5
    $fullIp = [string](@((IpsOf "settings-full-$tag") -split "`r?`n" | Where-Object { $_ -match '\.[1-9]\d*$' }) -join '')
}
Check "ingestion stores the full IP once the setting is picked up ($fullIp)" ($fullIp -match '^\d+\.\d+\.\d+\.[1-9]\d*$')

$prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
& docker compose exec -T postgres psql -U reapdeep -d reapdeep -tAc "DELETE FROM api_keys WHERE name = 'key $tag'; DELETE FROM users WHERE email = '$memberEmail'; DELETE FROM projects WHERE name = 'Project $tag';" 2>&1 | Out-Null
$ErrorActionPreference = $prev

if ($script:failed -eq 0) { Write-Host "`nPASSED: all settings checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
