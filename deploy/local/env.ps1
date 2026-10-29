function Initialize-LocalEnv {
    $path = Join-Path $PSScriptRoot ".env"
    $required = [ordered]@{
        POSTGRES_PASSWORD        = { [guid]::NewGuid().ToString('N') }
        READ_API_KEY             = { "rk_" + [guid]::NewGuid().ToString('N') }
        ADMIN_EMAIL              = { "admin@reapdeep.local" }
        ADMIN_PASSWORD           = { [guid]::NewGuid().ToString('N').Substring(0, 20) }
        STARROCKS_QUERY_PASSWORD = { [guid]::NewGuid().ToString('N') }
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
