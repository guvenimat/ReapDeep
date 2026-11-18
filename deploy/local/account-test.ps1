$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$api = "http://localhost:3000/api/v1"
$mailpit = "http://127.0.0.1:8026/api/v1"
$tag = "ac" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$invitee = "invitee_$tag@example.com"
$second = "second_$tag@example.com"
$unknown = "nobody_$tag@example.com"
$script:failed = 0

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}
function New-Client {
    $h = New-Object System.Net.Http.HttpClientHandler
    $h.CookieContainer = New-Object System.Net.CookieContainer
    New-Object System.Net.Http.HttpClient($h)
}
$script:limited = New-Object System.Collections.Generic.List[DateTime]
$script:noPacing = $false
function Pace {
    $now = Get-Date
    $script:limited.RemoveAll([Predicate[DateTime]] { param($t) $t -lt $now.AddSeconds(-61) }) | Out-Null
    if ($script:limited.Count -ge 10) {
        $wait = ($script:limited[0].AddSeconds(61) - $now).TotalSeconds
        Write-Host "  (waiting $([int]$wait) s for the per-IP limit of 10 per minute)"
        Start-Sleep -Seconds ([Math]::Ceiling($wait))
        $now = Get-Date
        $script:limited.RemoveAll([Predicate[DateTime]] { param($t) $t -lt $now.AddSeconds(-61) }) | Out-Null
    }
    $script:limited.Add((Get-Date))
}
function Send($client, [string]$method, [string]$path, $body = $null, [hashtable]$headers = @{ "X-Project-Id" = "1" }) {
    if ($path -in "/auth/token", "/auth/password/forgot", "/auth/password/reset" -and -not $script:noPacing) { Pace }
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), "$api$path")
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($null -ne $body) {
        $json = if ($body -is [string]) { $body } else { ConvertTo-Json $body -Compress }
        $req.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, "application/json")
    }
    $r = $client.SendAsync($req).Result
    $text = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $(if ($text) { try { $text | ConvertFrom-Json } catch { $null } } else { $null }) }
}
function Login($client, [string]$email, [string]$password) { (Send $client POST "/auth/login" @{ email = $email; password = $password }).Status }
function WaitFor([scriptblock]$cond, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { if (& $cond) { return $true }; Start-Sleep -Milliseconds 500 } until ((Get-Date) -gt $deadline)
    return [bool](& $cond)
}
function Mails([string]$to) {
    @((Invoke-RestMethod "$mailpit/search?query=$([uri]::EscapeDataString("to:$to"))&limit=50").messages)
}
function MailText($message) { (Invoke-RestMethod "$mailpit/message/$($message.ID)").Text }
function TokenFrom([string]$text, [string]$route) {
    $m = [regex]::Match($text, "http://localhost:3000/#/$route\?token=([A-Za-z0-9_-]+)")
    if ($m.Success) { $m.Groups[1].Value } else { "" }
}
function Pg($sql) { (docker compose exec -T postgres psql -U reapdeep -d reapdeep -tAc $sql | Out-String).Trim() }

$owner = New-Client
if ((Login $owner $envValues.ADMIN_EMAIL $envValues.ADMIN_PASSWORD) -ne 200) { throw "owner login failed (rate limited? wait a minute)" }

Write-Host "`n1) Invitation by email"
$r = Send $owner POST "/members" @{ email = $invitee; role = "member" }
Check "adding a new user without a password invites them -> 201 pending" ($r.Status -eq 201 -and $r.Json.pending -eq $true)
$inviteeId = $r.Json.user_id
$ok = WaitFor { $script:inviteMails = @(Mails $invitee); $script:inviteMails.Count -ge 1 }
Check "the invitation email arrives in Mailpit" ($ok -and $inviteMails[0].Subject -match "^You are invited to .+ on ReapDeep$")
$inviteText = if ($ok) { MailText $inviteMails[0] } else { "" }
$inviteToken = TokenFrom $inviteText "invite"
Check "the email has a single-use dashboard link and its expiry" ($inviteToken.Length -eq 43 -and $inviteText -match "expires in 7 days")
Check "only the token hash is stored" ((Pg "SELECT count(*) FROM auth_tokens WHERE token_hash = '$inviteToken'") -eq "0" -and (Pg "SELECT count(*) FROM auth_tokens WHERE user_id = $inviteeId AND purpose = 'invite'") -eq "1")
Check "an invited user cannot sign in before setting a password" ((Login (New-Client) $invitee "anything-$tag") -eq 401)
$info = Send (New-Client) POST "/auth/token" @{ token = $inviteToken }
Check "the link resolves to the invited email and purpose" ($info.Status -eq 200 -and $info.Json.email -eq $invitee -and $info.Json.purpose -eq "invite")
Check "a malformed token -> 400" ((Send (New-Client) POST "/auth/token" @{ token = "not-a-token" }).Status -eq 400)
Check "an unknown token -> 400" ((Send (New-Client) POST "/auth/token" @{ token = ("x" * 43) }).Status -eq 400)
$user = New-Client
Check "a too short password -> 400 and the token stays valid" ((Send $user POST "/auth/password/reset" @{ token = $inviteToken; new_password = "short" }).Status -eq 400 -and (Send (New-Client) POST "/auth/token" @{ token = $inviteToken }).Status -eq 200)
$set = Send $user POST "/auth/password/reset" @{ token = $inviteToken; new_password = "first-pass-$tag" }
Check "setting the password signs the user in" ($set.Status -eq 200 -and $set.Json.user.email -eq $invitee -and @($set.Json.projects | Where-Object { $_.id -eq 1 }).Count -eq 1)
Check "the session works" ((Send $user GET "/auth/me").Status -eq 200)
Check "the invitation link cannot be used twice" ((Send (New-Client) POST "/auth/password/reset" @{ token = $inviteToken; new_password = "other-pass-$tag" }).Status -eq 400)
Check "the user signs in with the new password" ((Login (New-Client) $invitee "first-pass-$tag") -eq 200)
$row = @((Send $owner GET "/members").Json.members | Where-Object { $_.email -eq $invitee })[0]
Check "the member is no longer pending" ($row.pending -eq $false)
Check "resending to a member with a password -> 409" ((Send $owner POST "/members/$inviteeId/invite").Status -eq 409)

$r = Send $owner POST "/members" @{ email = $second; role = "member" }
$secondId = $r.Json.user_id
Check "resending a pending invitation -> 204" ((Send $owner POST "/members/$secondId/invite").Status -eq 204)
$ok = WaitFor { (@(Mails $second)).Count -ge 2 }
Check "both invitations arrive" $ok
$tokens = @(Mails $second | ForEach-Object { TokenFrom (MailText $_) "invite" })
$used = Send (New-Client) POST "/auth/password/reset" @{ token = $tokens[0]; new_password = "second-pass-$tag" }
Check "using one invitation link revokes the other" ($used.Status -eq 200 -and (Send (New-Client) POST "/auth/token" @{ token = $tokens[1] }).Status -eq 400)
$member = New-Client
if ((Login $member $second "second-pass-$tag") -eq 200) {
    Check "a member cannot resend invitations -> 403" ((Send $member POST "/members/$inviteeId/invite").Status -eq 403)
} else { Check "second invitee signs in" $false }

Write-Host "`n2) Password reset by email"
$before = (@(Mails $invitee)).Count
$f1 = Send (New-Client) POST "/auth/password/forgot" @{ email = $invitee.ToUpperInvariant() }
$f2 = Send (New-Client) POST "/auth/password/forgot" @{ email = $unknown }
Check "forgot password answers 202 for a known and an unknown email alike" ($f1.Status -eq 202 -and $f2.Status -eq 202 -and $null -eq $f1.Json -and $null -eq $f2.Json)
Check "an invalid email -> 400" ((Send (New-Client) POST "/auth/password/forgot" @{ email = "nope" }).Status -eq 400)
$ok = WaitFor { $script:resetMails = @(Mails $invitee | Where-Object { $_.Subject -eq "Reset your ReapDeep password" }); $script:resetMails.Count -ge 1 }
Check "the reset email arrives" $ok
Start-Sleep 2
Check "nothing is sent to an unknown email" ((@(Mails $unknown)).Count -eq 0)
$resetText = if ($ok) { MailText $resetMails[0] } else { "" }
$resetToken = TokenFrom $resetText "reset"
Check "the reset link expires in an hour" ($resetToken.Length -eq 43 -and $resetText -match "expires in 60 minutes")
Check "the reset link resolves with purpose reset" ((Send (New-Client) POST "/auth/token" @{ token = $resetToken }).Json.purpose -eq "reset")
$reset = Send (New-Client) POST "/auth/password/reset" @{ token = $resetToken; new_password = "reset-pass-$tag" }
Check "resetting the password signs the user in" ($reset.Status -eq 200 -and $reset.Json.user.email -eq $invitee)
Check "existing sessions are signed out" ((Send $user GET "/auth/me").Status -eq 401)
Check "the old password no longer works" ((Login (New-Client) $invitee "first-pass-$tag") -eq 401)
Check "the new password works" ((Login (New-Client) $invitee "reset-pass-$tag") -eq 200)
Check "the reset link cannot be used twice" ((Send (New-Client) POST "/auth/password/reset" @{ token = $resetToken; new_password = "again-pass-$tag" }).Status -eq 400)

$null = Send (New-Client) POST "/auth/password/forgot" @{ email = $invitee }
$ok = WaitFor { @(Mails $invitee | Where-Object { $_.Subject -eq "Reset your ReapDeep password" }).Count -ge 2 }
$latest = @(Mails $invitee | Where-Object { $_.Subject -eq "Reset your ReapDeep password" })[0]
$expiredToken = TokenFrom (MailText $latest) "reset"
$null = Pg "UPDATE auth_tokens SET expires_at = now() - interval '1 second' WHERE token_hash = encode(sha256('$expiredToken'::bytea), 'hex')"
Check "an expired reset link -> 400" ($ok -and (Send (New-Client) POST "/auth/token" @{ token = $expiredToken }).Status -eq 400 -and (Send (New-Client) POST "/auth/password/reset" @{ token = $expiredToken; new_password = "late-pass-$tag" }).Status -eq 400)

for ($i = 0; $i -lt 3; $i++) { $null = Send (New-Client) POST "/auth/password/forgot" @{ email = $invitee } }
Start-Sleep 4
$resets = @(Mails $invitee | Where-Object { $_.Subject -eq "Reset your ReapDeep password" }).Count
Check "at most 3 reset emails per user and hour (got $resets)" ($resets -eq 3)

Write-Host "`n3) Rate limit"
$script:noPacing = $true
$statuses = for ($i = 0; $i -lt 12; $i++) { (Send (New-Client) POST "/auth/token" @{ token = ("y" * 43) }).Status }
Check "the reset and invitation endpoints share a per-IP limit of 10 per minute -> 429 ($($statuses -join ','))" ($statuses -contains 429)

Write-Host "`n4) Cleanup"
Check "invited members removed" ((Send $owner DELETE "/members/$inviteeId").Status -eq 204 -and (Send $owner DELETE "/members/$secondId").Status -eq 204)

if ($script:failed -eq 0) { Write-Host "`nPASSED: all account email checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
