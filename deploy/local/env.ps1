function Initialize-LocalEnv {
    $path = Join-Path $PSScriptRoot ".env"
    $required = [ordered]@{
        POSTGRES_PASSWORD        = { [guid]::NewGuid().ToString('N') }
        READ_API_KEY             = { "rk_" + [guid]::NewGuid().ToString('N') }
        ADMIN_EMAIL              = { "admin@reapdeep.local" }
        ADMIN_PASSWORD           = { [guid]::NewGuid().ToString('N').Substring(0, 20) }
        STARROCKS_QUERY_PASSWORD = { [guid]::NewGuid().ToString('N') }
        STARROCKS_JOBS_PASSWORD  = { [guid]::NewGuid().ToString('N') }
        REDIS_PASSWORD           = { [guid]::NewGuid().ToString('N') }
        STARROCKS_OPS_PASSWORD   = { [guid]::NewGuid().ToString('N') }
        STARROCKS_ALERTS_PASSWORD = { [guid]::NewGuid().ToString('N') }
    }
    $existing = if (Test-Path $path) { [IO.File]::ReadAllText($path) } else { "" }
    $missing = @($required.Keys | Where-Object { $existing -cnotmatch "(?m)^\s*$_\s*=" })
    if ($missing.Count -gt 0) {
        $add = ($missing | ForEach-Object { "$_=$(& $required[$_])" }) -join "`n"
        if ($existing.Length -gt 0 -and -not $existing.EndsWith("`n")) { $add = "`n" + $add }
        [IO.File]::AppendAllText($path, $add + "`n", (New-Object Text.UTF8Encoding($false)))
        Write-Host "Added to ${path}: $($missing -join ', ')"
    }
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*)$') { $values[$matches[1]] = $matches[2].Trim() }
    }
    return $values
}

$DotnetSdkImage = "mcr.microsoft.com/dotnet/sdk:10.0-noble-aot"

function Test-LocalDotnet10 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    return @(& dotnet --list-sdks 2>$null | Where-Object { $_ -match '^10\.' }).Count -gt 0
}

function Build-LoadTestTool([string]$OutDir) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    if (Test-LocalDotnet10) {
        dotnet build (Join-Path $repo "tests\ReapDeep.LoadTest") -c Release -o $OutDir | Select-String -Pattern "error" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "ReapDeep.LoadTest build failed" }
        return $false
    }
    Write-Host "No local .NET 10 SDK, building ReapDeep.LoadTest in $DotnetSdkImage and running it on the compose network"
    & { $ErrorActionPreference = "Continue"
        docker run --rm -v "${repo}:/src:ro" -v "${OutDir}:/out" -v reapdeep-nuget:/root/.nuget/packages $DotnetSdkImage bash -c `
            "mkdir /w && tar -C /src --exclude=bin --exclude=obj -cf - global.json src tests | tar -C /w -xf - && dotnet publish /w/tests/ReapDeep.LoadTest -c Release -o /out" 2>&1 |
            ForEach-Object { "$_" } | Select-String -Pattern "error" | Out-Host }
    if ($LASTEXITCODE -ne 0) { throw "ReapDeep.LoadTest build failed" }
    return $true
}

function Invoke-LoadTestTool([string]$OutDir, [bool]$InNetwork, [string[]]$ToolArgs, [string[]]$Mounts = @()) {
    if ($InNetwork) {
        $volumes = @("-v", "${OutDir}:/app:ro")
        foreach ($m in $Mounts) { $volumes += @("-v", $m) }
        & { $ErrorActionPreference = "Continue"
            docker run --rm --network reapdeep_default @volumes $DotnetSdkImage dotnet /app/ReapDeep.LoadTest.dll @ToolArgs 2>&1 | ForEach-Object { "$_" } | Out-Host }
    }
    else {
        & dotnet (Join-Path $OutDir "ReapDeep.LoadTest.dll") @ToolArgs | Out-Host
    }
    if ($LASTEXITCODE -ne 0) { throw "ReapDeep.LoadTest $($ToolArgs[0]) failed" }
}
