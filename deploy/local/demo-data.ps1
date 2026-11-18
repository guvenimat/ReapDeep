param([int]$Visitors = 3000, [int]$Days = 56, [int]$Seed = 42, [switch]$Reset)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")

$envValues = Initialize-LocalEnv
$toolOut = Join-Path $env:TEMP "reapdeep-loadtest"
$inNetwork = Build-LoadTestTool $toolOut
$pgHost, $srHost, $url = if ($inNetwork) { "postgres", "starrocks", "http://ingestion:8000/capture" } else { "localhost", "localhost", "http://localhost:8000/capture" }
$pg = "Host=$pgHost;Port=5432;Database=reapdeep;Username=reapdeep;Password=$($envValues.POSTGRES_PASSWORD)"

$demoArgs = @("demo", "--pg", $pg, "--url", $url, "--visitors", $Visitors, "--days", $Days, "--seed", $Seed)
if ($Reset) { $demoArgs += @("--reset", "--sr", "Server=$srHost;Port=9030;User ID=root;Database=analytics;ConnectionReset=false") }
Invoke-LoadTestTool -OutDir $toolOut -InNetwork $inNetwork -ToolArgs $demoArgs

$api = "http://localhost:8001/api/v1"
$demoHeaders = @{ "X-Project-Id" = "4" }
function Invoke-DemoApi([string]$Method, [string]$Path, $Body = $null) {
    $params = @{ Method = $Method; Uri = "$api$Path"; WebSession = $demoSession; Headers = $demoHeaders; UseBasicParsing = $true }
    if ($null -ne $Body) { $params.Body = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json $Body -Depth 6 -Compress)); $params.ContentType = "application/json" }
    Invoke-RestMethod @params
}
$demoSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = @{ email = $envValues.ADMIN_EMAIL; password = $envValues.ADMIN_PASSWORD } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri "$api/auth/login" -Body ([Text.Encoding]::UTF8.GetBytes($login)) -ContentType "application/json" -WebSession $demoSession -UseBasicParsing | Out-Null

$demoAlerts = @(
    @{ name = "Weekly visitors growth"; insight = "Daily visitors"; condition = "increase"; threshold = 50; window = "week"; channel = "slack"; webhook_url = "https://example.com/hooks/slack" },
    @{ name = "Busy day for orders"; insight = "Purchases by device"; condition = "above"; threshold = 40; window = "day"; channel = "email"; email_to = @("shop-team@example.com", "owner@example.com") },
    @{ name = "Visitors dropped"; insight = "Daily visitors"; condition = "decrease"; threshold = 20; window = "day"; channel = "webhook"; webhook_url = "https://example.com/hooks/reapdeep" }
)
$insights = @((Invoke-DemoApi Get "/insights").insights)
$existing = @((Invoke-DemoApi Get "/alerts").alerts | ForEach-Object { $_.name })
$created = 0
foreach ($a in $demoAlerts) {
    if ($existing -contains $a.name) { continue }
    $insight = $insights | Where-Object { $_.name -eq $a.insight } | Select-Object -First 1
    if ($null -eq $insight) { Write-Warning "Skipped the '$($a.name)' alert: the '$($a.insight)' insight is missing (run with -Reset)"; continue }
    $body = @{ name = $a.name; insight_id = $insight.id; condition = $a.condition; threshold = $a.threshold; window = $a.window; channel = $a.channel; enabled = $true }
    if ($a.webhook_url) { $body.webhook_url = $a.webhook_url }
    if ($a.email_to) { $body.email_to = $a.email_to }
    Invoke-DemoApi Post "/alerts" $body | Out-Null
    $created++
}
Write-Host "Demo alerts: $created created, $($demoAlerts.Count - $created) already present or skipped."

$noteDay = [DateTime]::UtcNow.AddDays(-10).ToString("yyyy-MM-dd")
$noteFrom = [DateTime]::UtcNow.AddDays(-60).ToString("yyyy-MM-dd")
if (-not @((Invoke-DemoApi Get "/annotations?from=$noteFrom").annotations | Where-Object { $_.content -eq "Newsletter sent" })) {
    Invoke-DemoApi Post "/annotations" @{ day = $noteDay; content = "Newsletter sent" } | Out-Null
    Write-Host "Demo chart note added on $noteDay."
}
if (-not @((Invoke-DemoApi Get "/actions").actions | Where-Object { $_.name -eq "Shopping" })) {
    Invoke-DemoApi Post "/actions" @{ name = "Shopping"; description = "Viewed a product, added to cart or bought"; events = @("product_viewed", "add_to_cart", "purchase") } | Out-Null
    Write-Host "Demo action 'Shopping' added."
}

Write-Host "`nOpen http://localhost:3000, pick the 'Demo Shop' project and 'Last 30 days', then Dashboards." -ForegroundColor Green
