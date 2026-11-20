param(
    [string]$Filter
)

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

$repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot "..") "..")).Path
$image = "mcr.microsoft.com/dotnet/sdk:10.0-noble-aot"
$filterArg = ""
if ($Filter) {
    $escaped = $Filter.Replace("'", "'\''")
    $filterArg = " --filter '$escaped'"
}
$bashCommand = "mkdir -p /w && tar -C /src --exclude=bin --exclude=obj --exclude=node_modules --exclude=dist -cf - ReapDeep.sln global.json src tests sdk dashboard docs README.md NOTICE LICENSE | tar -C /w -xf - && dotnet test /w/tests/ReapDeep.UnitTests -c Release --nologo$filterArg"

Write-Host "`nRunning unit tests in $image"
& docker run --rm -v "${repo}:/src:ro" -v "reapdeep-nuget:/root/.nuget/packages" $image bash -c $bashCommand
$code = $LASTEXITCODE

if ($code -eq 0) { Write-Host "`nPASSED: unit tests" -ForegroundColor Green }
else { Write-Host "`nFAILED: unit tests" -ForegroundColor Red; exit $code }
