param([int]$Visitors = 3000, [int]$Days = 56, [int]$Seed = 42)
$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot
. (Join-Path $PSScriptRoot "env.ps1")

$envValues = Initialize-LocalEnv
$pg = "Host=localhost;Port=5432;Database=reapdeep;Username=reapdeep;Password=$($envValues.POSTGRES_PASSWORD)"
$project = Join-Path $PSScriptRoot "..\..\tests\ReapDeep.LoadTest"

dotnet run -c Release --project $project -- demo --pg $pg --visitors $Visitors --days $Days --seed $Seed
if ($LASTEXITCODE -ne 0) { throw "demo seed failed" }

Write-Host "`nOpen http://localhost:3000, pick the 'Demo Shop' project and 'Last 30 days', then Dashboards." -ForegroundColor Green
