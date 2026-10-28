function Initialize-LocalEnv {
    $path = Join-Path $PSScriptRoot ".env"
    if (-not (Test-Path $path)) {
        $content = "POSTGRES_PASSWORD=$([guid]::NewGuid().ToString('N'))`nREAD_API_KEY=rk_$([guid]::NewGuid().ToString('N'))`n"
        [IO.File]::WriteAllText($path, $content, (New-Object Text.UTF8Encoding($false)))
        Write-Host "Created $path with random local credentials"
    }
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -cmatch '^\s*([A-Z0-9_]+)\s*=\s*(.*)$') { $values[$matches[1]] = $matches[2].Trim() }
    }
    return $values
}
