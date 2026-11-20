$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

$repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot "..") "..")).Path
$dashboard = Join-Path $repo "dashboard"
$npm = if ($env:OS -eq "Windows_NT") { "npm.cmd" } else { "npm" }

Set-Location -Path $dashboard

function Invoke-Npm([string[]]$npmArgs) {
    $label = "npm " + ($npmArgs -join " ")
    Write-Host "`n$label" -ForegroundColor Cyan
    & $npm @npmArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "`nFAILED: dashboard checks ($label)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path (Join-Path $dashboard "node_modules"))) {
    Invoke-Npm @("ci")
}
Invoke-Npm @("run", "lint")
Invoke-Npm @("test")
Invoke-Npm @("run", "build")

Write-Host "`nPASSED: dashboard checks" -ForegroundColor Green
