param([string] $Root = (Join-Path $PSScriptRoot 'work'))
$ErrorActionPreference = 'Stop'
$rows = foreach ($file in Get-ChildItem (Join-Path $Root 'results') -Filter '*-merged.json') {
    $result = Get-Content $file.FullName -Raw | ConvertFrom-Json
    $meta = Get-Content ($file.FullName -replace '-merged.json$', '-meta.json') -Raw | ConvertFrom-Json
    $feed = $result.endpoints | Where-Object name -eq 'GET /api/articles/feed'
    [pscustomobject]@{
        run = $file.BaseName -replace '-merged$', ''
        variant=$meta.variant; users=$meta.users; workload=$meta.workload
        rps=[math]::Round($result.overall.rps,1)
        p95=[math]::Round($result.overall.p95_ms,2); p99=[math]::Round($result.overall.p99_ms,2)
        errors=[math]::Round($result.overall.error_rate*100,3)
        pg_cpu=[math]::Round($meta.cpu.postgres.cpu_percent,2)
        app_cpu=[math]::Round($meta.cpu.app.cpu_percent,2)
        load_cpu=[math]::Round($meta.cpu.loadgen.cpu_percent,2)
        pg_ms=[math]::Round($meta.cpu.postgres.core_seconds*1000000/$meta.cpu.postgres.elapsed_ms/$result.overall.rps,3)
        app_ms=[math]::Round($meta.cpu.app.core_seconds*1000000/$meta.cpu.app.elapsed_ms/$result.overall.rps,3)
        feed_mean=[math]::Round($feed.mean_ms,2)
        limited=$result.verdict.lg_limited
    }
}
$rows | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Root 'results-summary.json')
$rows | Format-Table -AutoSize
