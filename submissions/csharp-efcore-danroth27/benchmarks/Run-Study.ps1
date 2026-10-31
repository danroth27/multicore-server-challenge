param(
    [string[]] $Order = @(),
    [int] $Users = 4000,
    [int] $MeasureSeconds = 45,
    [int] $RampSeconds = 20,
    [int] $WarmupSeconds = 40,
    [string] $WorkloadId = 'optimization-seed-a',
    [string[]] $Conformance = @(),
    [Parameter(Mandatory)][string] $PostgresContainer,
    [string] $Root = (Join-Path $PSScriptRoot 'work'),
    [string] $Tools = 'conduit-efcore-tools',
    [string] $HttpUrl = 'http://127.0.0.1:53110'
)
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$Pg = $PostgresContainer
$App = "conduit-efcore-study-app-$PID"
$Load = "conduit-efcore-study-load-$PID"
$Runtime = 'mcr.microsoft.com/dotnet/aspnet@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee'
$Secret = 'multicore-server-challenge'
$Results = Join-Path $Root 'results'
New-Item -ItemType Directory -Force $Results | Out-Null

function Invoke-Docker {
    $output = & docker @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "docker $args failed ($LASTEXITCODE): $output" }
    return $output
}
function Stop-App {
    if ($App -in (Invoke-Docker ps -a --format '{{.Names}}')) {
        Invoke-Docker logs $App | Set-Content (Join-Path $Results "$script:Label-server.log")
        Invoke-Docker stop --timeout 10 $App | Out-Null
        Invoke-Docker rm $App | Out-Null
    }
}
function Start-App([string] $Name) {
    Stop-App
    $script:Candidate = Get-Content (Join-Path $Root "candidates\$Name\candidate.json") -Raw | ConvertFrom-Json
    Invoke-Docker exec $Pg psql -U postgres -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS conduit_review_run WITH (FORCE)' | Out-Null
    Invoke-Docker exec $Pg psql -U postgres -v ON_ERROR_STOP=1 -c 'CREATE DATABASE conduit_review_run TEMPLATE conduit_seed STRATEGY FILE_COPY' | Out-Null
    $arguments = @('run', '-d', '--name', $App, '--network', "container:$Pg", '--cpuset-cpus', '0-5',
        '--memory', '4g', '--mount', "type=bind,source=$($script:Candidate.app),target=/app,readonly",
        '--mount', "type=bind,source=$Root,target=/study",
        '-e', 'DATABASE_URL=postgres://conduit@127.0.0.1:5432/conduit_review_run',
        '-e', "JWT_SECRET=$Secret", '-e', 'HOST=0.0.0.0', '-e', 'PORT=3110',
        '-e', 'ASPNETCORE_ENVIRONMENT=Production')
    foreach ($entry in $script:Candidate.environment.PSObject.Properties) {
        $arguments += @('-e', "$($entry.Name)=$($entry.Value)")
    }
    $arguments += @('-w', '/app', $Runtime, 'dotnet', 'conduit.dll')
    Invoke-Docker @arguments | Out-Null
    $ready = $false
    for ($i = 0; $i -lt 120; $i++) {
        try {
            $response = Invoke-WebRequest "$HttpUrl/api/tags" -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        }
        catch [System.Net.Http.HttpRequestException], [System.Threading.Tasks.TaskCanceledException] {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) { throw "App not ready: $(Invoke-Docker logs $App)" }
}
function Usage([string] $Container) {
    $text = (Invoke-Docker exec $Container cat /sys/fs/cgroup/cpu.stat) -join "`n"
    if ($text -notmatch '(?m)^usage_usec\s+(\d+)$') { throw "No CPU usage for $Container" }
    return @{ usec = [double]$Matches[1]; time = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
}
function Wait-Until([long] $Time) {
    $remaining = $Time - [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int]$remaining) }
}
function Snapshot([string] $Stage) {
    Invoke-Docker exec $Pg psql -U postgres -d conduit_review_run -At -c @'
SELECT json_build_object(
 'database',(SELECT row_to_json(s) FROM pg_stat_database s WHERE datname=current_database()),
 'wal',(SELECT row_to_json(w) FROM pg_stat_wal w),
 'activity',(SELECT json_agg(a) FROM
   (SELECT state,wait_event_type,wait_event,count(*) FROM pg_stat_activity
    WHERE datname=current_database() AND pid<>pg_backend_pid()
    GROUP BY state,wait_event_type,wait_event) a));
'@ | Set-Content (Join-Path $Results "$script:Label-$Stage-pg.json")
    foreach ($container in @($Pg, $App)) {
        Invoke-Docker exec $container sh -c 'cat /sys/fs/cgroup/memory.current; cat /sys/fs/cgroup/memory.events; cat /sys/fs/cgroup/memory.stat' |
            Set-Content (Join-Path $Results "$script:Label-$Stage-$container-memory.txt")
    }
}
try {
    Invoke-Docker start $Pg | Out-Null
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        & docker exec $Pg pg_isready -U postgres *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep 1
    }
    if (-not $ready) { throw 'PostgreSQL not ready' }
    foreach ($name in $Conformance) {
        $script:Label = "$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))-$name-conformance"
        Start-App $name
        Invoke-Docker run --rm --network "container:$Pg" --cpuset-cpus 6-7 --mount "type=bind,source=$Root,target=/work" `
            $Tools /work/bin/conformance -base http://127.0.0.1:3110 -secret $Secret -q `
            -transcript "/work/results/$script:Label.jsonl" |
            Tee-Object -FilePath (Join-Path $Results "$script:Label.txt")
        Stop-App
    }
    foreach ($name in $Order) {
        $script:Label = "$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))-$name-u$Users"
        Write-Output "=== $script:Label ==="
        Start-App $name
        Invoke-Docker run --rm --network "container:$Pg" --cpuset-cpus 6-7 --mount "type=bind,source=$Root,target=/work" `
            $Tools /work/bin/loadgen run -target http://127.0.0.1:3110 -users 500 -ramp 10s `
            -measure "${WarmupSeconds}s" -tail 3s -secret $Secret -run-id "$WorkloadId-warmup" `
            -out "/work/results/$script:Label-warmup.json" -q | Out-Null
        $start = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + 4000
        Invoke-Docker run -d --name $Load --network "container:$Pg" --cpuset-cpus 6-7 --memory 2g `
            --mount "type=bind,source=$Root,target=/work" $Tools /work/bin/loadgen run `
            -target http://127.0.0.1:3110 -users $Users -ramp "${RampSeconds}s" -measure "${MeasureSeconds}s" -tail 5s `
            -start-at $start -secret $Secret -run-id $WorkloadId -out "/work/results/$script:Label.json" -q | Out-Null
        if ((Invoke-Docker inspect --format '{{.State.Running}}' $Load) -ne 'true') { throw 'Load generator not running' }
        Wait-Until ($start + $RampSeconds * 1000)
        Snapshot 'before'
        $pgBefore = Usage $Pg; $appBefore = Usage $App; $loadBefore = Usage $Load
        Wait-Until ($start + ($RampSeconds + $MeasureSeconds) * 1000)
        $pgAfter = Usage $Pg; $appAfter = Usage $App; $loadAfter = Usage $Load
        Snapshot 'after'
        $exit = Invoke-Docker wait $Load
        Invoke-Docker logs $Load | Set-Content (Join-Path $Results "$script:Label-load.log")
        Invoke-Docker rm $Load | Out-Null
        if ($exit -ne '0') { throw "Load generator exited $exit" }
        $cpu = [ordered]@{}
        foreach ($entry in @(@('postgres',$pgBefore,$pgAfter,6),@('app',$appBefore,$appAfter,6),@('loadgen',$loadBefore,$loadAfter,2))) {
            $elapsed = $entry[2].time - $entry[1].time
            $seconds = ($entry[2].usec - $entry[1].usec) / 1000000
            $cpu[$entry[0]] = @{ cpu_percent = $seconds / ($elapsed / 1000 * $entry[3]) * 100
                core_seconds = $seconds; elapsed_ms = $elapsed }
        }
        [ordered]@{ variant=$name; users=$Users; workload=$WorkloadId; candidate=$script:Candidate; cpu=$cpu
            warmup_seconds=$WarmupSeconds; ramp_seconds=$RampSeconds; measure_seconds=$MeasureSeconds } |
            ConvertTo-Json -Depth 10 | Set-Content (Join-Path $Results "$script:Label-meta.json")
        $merged = & docker run --rm --mount "type=bind,source=$Root,target=/work" $Tools /work/bin/loadgen merge `
            -o "/work/results/$script:Label-merged.json" "/work/results/$script:Label.json" 2>&1
        $mergeExit = $LASTEXITCODE
        $merged | Tee-Object -FilePath (Join-Path $Results "$script:Label-summary.txt")
        if ($mergeExit -notin @(0,3)) { throw "Merge failed ($mergeExit)" }
        $cpu | ConvertTo-Json -Depth 4
        Stop-App
    }
}
finally {
    if ($Load -in (& docker ps -a --format '{{.Names}}')) { & docker rm -f $Load | Out-Null }
    Stop-App
}
