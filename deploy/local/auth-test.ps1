$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")
Add-Type -AssemblyName System.Net.Http

$envValues = Initialize-LocalEnv
$readKey = $envValues.READ_API_KEY
$adminEmail = $envValues.ADMIN_EMAIL
$adminPassword = $envValues.ADMIN_PASSWORD
$dash = "http://localhost:3000"
$tag = "at" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$script:failed = 0

function Check($desc, $cond) {
    if ($cond) { Write-Host "  OK   $desc" -ForegroundColor Green } else { Write-Host "  FAIL $desc" -ForegroundColor Red; $script:failed++ }
}

function New-Client {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.CookieContainer = New-Object System.Net.CookieContainer
    $handler.UseCookies = $true
    New-Object System.Net.Http.HttpClient($handler)
}

function Send($client, [string]$method, [string]$url, [string]$body = $null, [hashtable]$headers = @{}) {
    $req = New-Object System.Net.Http.HttpRequestMessage((New-Object System.Net.Http.HttpMethod($method)), $url)
    foreach ($h in $headers.Keys) { $req.Headers.TryAddWithoutValidation($h, $headers[$h]) | Out-Null }
    if ($body) { $req.Content = New-Object System.Net.Http.StringContent($body, [Text.Encoding]::UTF8, "application/json") }
    $r = $client.SendAsync($req).Result
    $text = $r.Content.ReadAsStringAsync().Result
    [pscustomobject]@{
        Status  = [int]$r.StatusCode
        Json    = $(if ($text) { try { $text | ConvertFrom-Json } catch { $null } } else { $null })
        Cookie  = (($r.Headers | Where-Object { $_.Key -eq "Set-Cookie" }).Value -join "; ")
        Headers = $r.Headers
    }
}

$login = '{"email":"' + $adminEmail + '","password":"' + $adminPassword + '"}'

Write-Host "`n1) Sign-in (through nginx, $dash)"
$c = New-Client
Check "GET /auth/me without a session -> 401" ((Send $c GET "$dash/api/v1/auth/me").Status -eq 401)
Check "data endpoint without a session -> 401" ((Send $c GET "$dash/api/v1/metadata").Status -eq 401)
Check "wrong password -> 401" ((Send $c POST "$dash/api/v1/auth/login" ('{"email":"' + $adminEmail + '","password":"wrong"}')).Status -eq 401)
Check "unknown email -> 401" ((Send $c POST "$dash/api/v1/auth/login" '{"email":"nobody@example.com","password":"x"}').Status -eq 401)
Check "missing fields -> 400" ((Send $c POST "$dash/api/v1/auth/login" '{"email":""}').Status -eq 400)

$r = Send $c POST "$dash/api/v1/auth/login" ('{"email":"' + $adminEmail.ToUpperInvariant() + '","password":"' + $adminPassword + '"}')
Check "login (email case-insensitive) -> 200" ($r.Status -eq 200)
Check "session cookie is HttpOnly and SameSite=Strict" ($r.Cookie -cmatch "rd_session=" -and $r.Cookie -match "httponly" -and $r.Cookie -match "samesite=strict")
Check "response lists the user's projects with role" ($r.Json.user.email -eq $adminEmail -and ($r.Json.projects | Where-Object { $_.id -eq 1 -and $_.role -eq "owner" }))

Write-Host "`n2) Project access with a session"
Check "GET /auth/me -> 200" ((Send $c GET "$dash/api/v1/auth/me").Status -eq 200)
Check "no X-Project-Id -> 400" ((Send $c GET "$dash/api/v1/metadata").Status -eq 400)
Check "X-Project-Id of a member project -> 200" ((Send $c GET "$dash/api/v1/metadata" -headers @{ "X-Project-Id" = "1" }).Status -eq 200)
Check "X-Project-Id of another project -> 403" ((Send $c GET "$dash/api/v1/metadata" -headers @{ "X-Project-Id" = "999" }).Status -eq 403)
Check "trends query with a session -> 200" ((Send $c POST "$dash/api/v1/query/trends" '{"series":[{"event":"$pageview"}]}' @{ "X-Project-Id" = "1" }).Status -eq 200)
Check "API key still works (programmatic access)" ((Send (New-Client) GET "$dash/api/v1/metadata" -headers @{ Authorization = "Bearer $readKey" }).Status -eq 200)
Check "invalid API key is not overridden by a valid session -> 401" ((Send $c GET "$dash/api/v1/metadata" -headers @{ Authorization = "Bearer rk_wrong"; "X-Project-Id" = "1" }).Status -eq 401)

Write-Host "`n3) Sign-out"
Check "POST /auth/logout -> 204" ((Send $c POST "$dash/api/v1/auth/logout").Status -eq 204)
Check "session is revoked immediately -> 401" ((Send $c GET "$dash/api/v1/metadata" -headers @{ "X-Project-Id" = "1" }).Status -eq 401)

Write-Host "`n4) Hardening"
$r = Send (New-Client) OPTIONS "http://localhost:8001/api/v1/query/trends" -headers @{ Origin = "https://evil.example"; "Access-Control-Request-Method" = "POST" }
Check "Query API sends no CORS headers to foreign origins" (-not ($r.Headers | Where-Object { $_.Key -like "Access-Control-*" }))

$spoofed = "203.0.113.77"
$cap = Send (New-Client) POST "http://localhost:8000/capture" ('{"api_key":"pk_dev_local","batch":[{"event":"xff_' + $tag + '","distinct_id":"xff_' + $tag + '"}]}') @{ "X-Forwarded-For" = $spoofed }
Check "capture accepted" ($cap.Status -eq 202)
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep 2
    $ev = (Send (New-Client) GET "http://localhost:8001/api/v1/events?distinct_id=xff_$tag" -headers @{ Authorization = "Bearer $readKey" }).Json.events
} until (@($ev).Count -gt 0 -or (Get-Date) -gt $deadline)
Check "ingestion ignores X-Forwarded-For from an untrusted client (ip = $($ev[0].columns.ip))" (@($ev).Count -eq 1 -and $ev[0].columns.ip -and $ev[0].columns.ip -ne $spoofed)

$srPassword = $envValues.STARROCKS_QUERY_PASSWORD
$ErrorActionPreference = "Continue"
$srArgs = @("compose", "exec", "-T", "-e", "MYSQL_PWD=$srPassword", "starrocks", "mysql", "--host=127.0.0.1", "--port=9030", "--user=reapdeep_query", "-N", "-e")
$sel = & docker @srArgs "SELECT count(*) FROM analytics.events" 2>&1 | Out-String
Check "StarRocks query user can SELECT" ($sel -cmatch "(?m)^\d+\s*$")
$del = & docker @srArgs "DELETE FROM analytics.events WHERE project_id = 424242" 2>&1 | Out-String
$ErrorActionPreference = "Stop"
Check "StarRocks query user cannot DELETE" ($del -match "Access denied")

Write-Host "`n5) Login rate limit (10 attempts per minute per client IP)"
$codes = 1..12 | ForEach-Object { (Send (New-Client) POST "$dash/api/v1/auth/login" '{"email":"nobody@example.com","password":"x"}').Status }
Check "attempts beyond the limit -> 429 ($($codes -join ' '))" (($codes | Select-Object -Last 2) -join "," -eq "429,429")
Write-Host "  Note: sign-in from this machine is blocked for up to a minute after this check." -ForegroundColor DarkGray

if ($script:failed -eq 0) { Write-Host "`nPASSED: all auth checks" -ForegroundColor Green }
else { Write-Host "`nFAILED: $script:failed checks failed" -ForegroundColor Red; exit 1 }
