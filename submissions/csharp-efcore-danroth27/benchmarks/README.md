# Reproducing the local optimization study

Use PowerShell 7.5 or later, Docker with at least eight Linux VM CPUs, and the already installed .NET SDK selected by the submission's `global.json`. The focused JSON assertions use PowerShell's `ConvertFrom-Json -DateKind String`.

**Use a dedicated benchmark PostgreSQL container. `Run-Study.ps1` drops and recreates `conduit_review_run` with `FORCE` on every trial. Never point it at a shared or production database.** It preserves the `conduit_seed` template and the container/volume. The container is left running so multiple invocations can use the same seed; stop it explicitly when finished.

## Fixture

Prepare PostgreSQL 18 with the repository's unchanged schema, full official seed, ordinary `conduit` role, and the official eight-core/16-GB settings. Verify the seed table counts against the report. Name the fully loaded template database `conduit_seed`; no clients should remain connected to it.

The evaluated fixture used this PostgreSQL image:

```text
postgres@sha256:979bccfde35445db618c65107d27d08c6c21339d8dc3ad4c830c6633947be0e4
```

The application runtime image is pinned in the runner:

```text
mcr.microsoft.com/dotnet/aspnet@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
```

Give PostgreSQL CPUs 0-5, a 12-GB memory limit, adequate shared memory for 4-GB shared buffers, and the settings listed in the report. Publish its namespace's application port 3110 as host loopback port 53110. The application and tools join that container's network namespace. The runner expects local `conduit` connections to the trial database to work without a password, as in the isolated study fixture; restrict any trust authentication to this disposable local environment. Its setup connection uses `docker exec ... psql -U postgres`.

Do not run two studies in that namespace concurrently: they share the trial database and application port. No schema/index/global PostgreSQL configuration changes are made by the runner. Session-only changes for rejected experiments are candidate environment settings, not requirements.

## Build the official tools

From the repository root:

```powershell
$Repo = (Get-Location).Path
$Bench = Join-Path $Repo 'submissions\csharp-efcore-danroth27\benchmarks'
$Work = Join-Path $Bench 'work'
New-Item -ItemType Directory -Force (Join-Path $Work 'bin') | Out-Null
docker build -t conduit-efcore-tools -f "$Bench\Dockerfile.tools" .
docker run --rm --mount "type=bind,source=$Repo,target=/repo" conduit-efcore-tools bash -lc 'cd /repo/bench && go build -o /repo/submissions/csharp-efcore-danroth27/benchmarks/work/bin/loadgen ./cmd/loadgen && go build -o /repo/submissions/csharp-efcore-danroth27/benchmarks/work/bin/conformance ./cmd/conformance'
```

`work` is ignored and excluded from application compilation/publication. Linux paths inside Docker commands refer to container paths; PowerShell host paths use backslashes.

## Publish and compare immutable candidates

The publisher runs from the optimized submission directory so that its pinned SDK also applies when publishing the original baseline. It records source snapshots, assembly hashes, build properties, and environment overrides; it refuses to overwrite an existing candidate name.

```powershell
& "$Bench\Publish-Candidate.ps1" -Name original -Source "$Repo\submissions\csharp-efcore-xeonfx"
& "$Bench\Publish-Candidate.ps1" -Name optimized

$Pg = 'your-dedicated-postgres-container'
& "$Bench\Run-Study.ps1" -PostgresContainer $Pg -Conformance original,optimized
& "$Bench\Run-Study.ps1" -PostgresContainer $Pg -Order original,optimized -Users 4000 -WarmupSeconds 180 -RampSeconds 60 -MeasureSeconds 120 -WorkloadId control-a
& "$Bench\Run-Study.ps1" -PostgresContainer $Pg -Order optimized,original -Users 4000 -WarmupSeconds 180 -RampSeconds 60 -MeasureSeconds 120 -WorkloadId control-b
& "$Bench\Summarize.ps1"
```

For finalists, use matched 20,000-user trials with a 180-second 500-user warm-up, 60-second ramp, and 300-second measurement, reversing order with a second workload ID. Retain the official error/latency and load-generator gates. Run profiling separately, never during a ranking measurement. A `-Root` override can place all generated work elsewhere; use the same root for publishing, running, and summarizing. `-Tools` and `-HttpUrl` override the tools image and host readiness URL.

To vary only configuration, reuse a binary:

```powershell
& "$Bench\Publish-Candidate.ps1" -Name optimized-pool64 -Reuse optimized -Environment @{ DB_POOL_SIZE = '64' }
```

Environment overrides are a complete replacement, not inherited from the reused candidate. Build-property changes require a fresh publish rather than `-Reuse`. Source patches preserve the experimental code differences; apply them to the original source in a separate working copy before publishing. The final implementation may include packaging-only changes absent from the measured snapshot.

`Verify-Edges.ps1` runs focused API assertions against an already running candidate. Use only a disposable trial database: it creates users and articles, exercises concurrent writes, and cleans up its articles but cannot delete users through this API.

## Evidence

The report explains the experiment names, matched comparisons, profiling caveats, and limitations. Retained measurements include merged load-generator JSON and compact run metadata, rather than request transcripts containing authentication tokens. Variant patches record source changes from the original implementation; configuration-only variants share the recorded assembly hash. The final patch was replayed against the original snapshot.

| File/directory | Contents |
|---|---|
| `environment.json` | Image digests, PostgreSQL settings, CPU affinity, memory limits, and toolchain |
| `candidates.json`, `variants` | Candidate settings, hashes, descriptions, and source patches |
| `results-summary.json`, `results` | All 38 trials, merged endpoint distributions, and CPU/timing metadata |
| `conformance.json`, `focused-tests.txt` | Official conformance summaries and final focused-assertion result |
| `plans` | Actual SQL and JSON query plans for custom/generic planning probes |
| `profiles` | Counter/activity samples, summaries, thread-time stack reports, collector warnings, and the denied on-CPU probe |

Profiling used `dotnet-trace` and `dotnet-counters` 10.0.750501, run inside the application container against PID 1 on the existing .NET 10 runtime with `DOTNET_ROLL_FORWARD=Major`. The final collection used 20,000 users after the same three-minute warm-up, a 25-second ramp, and a separate diagnostic load window. Its collection settings were:

```text
dotnet-trace collect -p 1 --profile dotnet-sampled-thread-time --duration 00:00:35 --buffersize 64 --format Speedscope
dotnet-counters collect -p 1 --duration 00:00:35 --refresh-interval 1 --format json --counters System.Runtime,Microsoft.AspNetCore.Hosting,Microsoft.EntityFrameworkCore,Npgsql
```

Output paths were in the bind-mounted study directory; PostgreSQL activity was sampled approximately once a second during collection. Large `.nettrace` and Speedscope files are omitted. Thread-time reports are not on-CPU attribution, and the recorded histogram-limit warning means route-histogram coverage is incomplete.

CPU percentages for the application and database are normalized to their six shared target CPUs. The official generator's whole-box CPU gate sees the entire Docker VM, not only its two assigned CPUs. Flagged runs remain flagged in the evidence and must not be represented as valid capacity results.

Stop the dedicated PostgreSQL container when done, preserving its volume:

```powershell
docker stop $Pg
```
