# Optimizing the standard .NET implementation

## Scope and conclusion

This study optimizes the repository's `csharp-efcore-xeonfx` implementation from [#20](https://github.com/arjaythedev/multicore-server-challenge/pull/20), without replacing the .NET application platform. Kestrel, Minimal APIs, dependency injection, EF Core, AddJwtBearer/IdentityModel, BCrypt.Net-Next cost 10, System.Text.Json, and CoreCLR remain in place.

The original implementation was not generally misconfigured: EF context pooling already existed, and its DTO projections did not require `AsNoTracking()`. The important problems were query shape, collection fanout, disabled driver preparation, and avoidable database work. The optimized feed and counts remain LINQ; targeted parameterized SQL through EF Core is confined to three idempotent write operations.

The measurements below are local diagnostics, not an official capacity result. Single screening trials identify promising directions but do not establish the independent causal effect of every small change. Final matched comparisons and alternating sustained holds provide the stronger evidence.

**Correctness revision:** independent review found that the two-command favorite optimization could successfully mutate an article and then return 404 if its slug changed before the response read. Article deletion also inherited a separate slug-rename race. The current candidate, `correctness-id32`, resolves a stable article ID once for these operations. The historical `favorite2` measurements below are retained as evidence, not results for the corrected implementation.

**Updated result:** across three completed, SLA-passing 4,000-user pairs, including a reversed-order pair, the corrected candidate uses approximately 76% less PostgreSQL CPU/request and 58% less combined application/database CPU/request. Its paired p99 is 6.9-11.1 ms versus 39.7-386 ms for the baseline. Two recovery five-minute 20,000-user holds pass at approximately 5,283 requests/s, p99 230-400 ms, and zero errors. Earlier failed trials are retained and described below; this is not evidence that the local host delivers those tails reliably under other concurrent activity.

## Environment and method

The baseline source is the untouched `submissions/csharp-efcore-xeonfx` directory at repository commit `b478fbf7e2e5bda301d1ba9d6d6193129fa65c7f`. The original PR head reviewed earlier was `156440108adcf823a3f77214b72834d4874440cd`. Both baseline and optimized candidates use SDK 10.0.403 and the same .NET 10.0.14 Linux runtime image. EF Core resolves to 10.0.13; the Npgsql EF provider is 10.0.3 and BCrypt.Net-Next is 4.2.0.

| Component | Configuration |
|---|---|
| Host | Windows with an eight-vCPU Docker Linux VM, approximately 32 GB available to the VM |
| Application and PostgreSQL | Shared affinity to VM CPUs 0-5; application memory limit 4 GB, PostgreSQL 12 GB |
| Load generator | VM CPUs 6-7, 2 GB memory limit; official Go load generator |
| PostgreSQL | 18.6, official eight-core/16-GB configuration: 4-GB shared buffers, 12-GB effective cache, 9709-kB work memory, 100 connections |
| Planning and parallelism | JIT on, automatic plan selection, eight parallel workers, four workers per gather, unless explicitly varied |
| Seed | 100,000 users; 1,989,808 follows; 500,000 articles; 200 tags; 1,250,377 article tags; 4,990,637 favorites; 2,494,001 comments |
| Isolation | Fresh application process and `FILE_COPY` clone of the verified full seed for each trial |
| Durability | Normal PostgreSQL commit durability; no schema, index, function, extension, or global configuration changes |

These are VM vCPUs, not isolated physical cores. The application and database share six rather than the official target's eight CPUs, and the generator shares the underlying host. This differs materially from the official Ubuntu target and separate load-generator machine.

Each matched pair uses the same workload identifier and seed, with a new warm-up and database clone per candidate. Final controls and finalists use the ordinary `conduit` database role. Early screening used the `postgres` role; do not treat comparisons across those phases as matched controls.

Application and PostgreSQL CPU percentages each use the six target vCPUs as their denominator: 50% means about three cores. CPU-ms/request derives from cgroup CPU-time deltas, actual sampling intervals, and measured throughput. These are approximate measurement-boundary samples rather than an instruction-level attribution. No study builds, profilers, or query-plan probes run during timed comparisons.

## Changes retained

**Bounded feed selection in LINQ.** Select at most `offset + limit` newest article IDs per followed author, merge those candidates, paginate, and only then project article details. This is safe because an author's articles below that bound cannot enter the requested global prefix. Carrying follower context through the correlated projection makes Npgsql generate `LATERAL`; a superficially similar `SelectMany(...Take(...))` instead generated a much broader window-function plan. Joining selected IDs outward before the final DTO projection avoids an EF translation limitation. An overflow-safe fallback preserves the contract for offsets near `int.MaxValue`.

**Smaller reads and counts.** Filter and count against article IDs and junction tables rather than loading unnecessary user/navigation data. Counts retain the contract's 1,000-row cap. Ordered tag arrays use provider-supported `EF.Functions.ArrayAgg` in LINQ, avoiding the repeated article columns caused by collection-projection fanout. Comments use one query that still distinguishes a nonexistent article from an existing article with no comments.

**Standard query and serialization features.** Static `EF.CompileAsyncQuery` delegates cover hot reads, with shared expression-based DTO projections. Source-generated System.Text.Json metadata covers request and response types, retaining reflection fallback. The timestamp converter uses standard UTF-8 `DateTime.TryFormat` into a stack buffer rather than allocating an intermediate string; output remains UTC with exactly three fractional digits.

**Preparation and pooling.** Npgsql automatic preparation is enabled with capacity 64 and minimum usage count 2; the final physical connection pool is 32. PostgreSQL retains automatic custom/generic plan selection. EF context pooling and normal server GC remain enabled.

**Scoped connection reuse.** List and feed handlers hold one ordinary EF connection across their page and count commands, closing it in `finally` before the pooled context returns. This avoids an additional checkout/reset between those commands without introducing a read transaction, custom connection pool, or scheduler. Sustained results favored its stability over ordinary per-command connection lifetimes.

**Idempotent writes with stable resource identity.** Parameterized `ExecuteSqlInterpolatedAsync` statements implement `INSERT ... ON CONFLICT DO NOTHING` for follows and favorites, and bulk insertion of missing tags using `unnest`. Favorite and unfavorite resolve the article ID by slug, mutate by that ID, and use the same ID for the compiled response projection. This deliberately restores a third command: eliminating it was not worth the rename race. Article deletion resolves ID and author together, checks ownership, and deletes by ID and owner rather than looking up the mutable slug again. Missing tag names are sorted consistently to avoid opposite insertion lock orders in concurrent requests. Ordinary EF tracking, transactions, `SaveChangesAsync`, and `ExecuteDeleteAsync` remain responsible for other writes. Writes commit before responses.

The official generator's article writes select existing seed tags (`bench/cmd/loadgen/vu.go`, `newArticle`). Consequently, batch lookup of existing tags matters to that path, but missing-tag bulk insertion is a correctness/concurrency improvement, not an independently measured steady-state throughput win.

## Screening experiments

The complete evidence now contains 51 timed trials across 27 candidate configurations: the original 38 trials/26 configurations plus 13 correctness-revision and recovery trials. An interrupted recovery attempt produced no completed measurement and is not counted. Each row below is one original short trial, generally a directional screen rather than a replicated estimate. Variants are incremental within a phase; names correspond to the persisted evidence. Errors were zero in these original screening trials.

| Variant | Users | Requests/s | p95 ms | p99 ms | PG CPU % | App CPU % |
|---|---:|---:|---:|---:|---:|---:|
| Original baseline | 4,000 | 1,054.8 | 61.34 | 177.54 | 43.51 | 19.17 |
| Preparation only | 4,000 | 1,057.2 | 24.40 | 66.94 | 36.82 | 19.13 |
| Bounded LINQ feed/counts | 4,000 | 1,055.8 | 5.70 | 13.85 | 11.28 | 21.31 |
| Scalar tag aggregation | 4,000 | 1,055.6 | 6.56 | 16.21 | 11.71 | 22.40 |
| Combined comments/existence | 4,000 | 1,055.7 | 6.72 | 15.74 | 11.94 | 21.82 |
| Ordered capped count | 4,000 | 1,055.4 | 9.84 | 25.14 | 13.07 | 22.03 |
| Uncompiled bounded LINQ | 12,000 | 3,163.2 | 86.72 | 264.96 | 29.37 | 41.06 |
| Ordered count, uncompiled | 12,000 | 3,145.7 | 183.04 | 886.27 | 29.79 | 40.79 |
| Compiled queries | 12,000 | 3,133.0 | 313.09 | 1,097.73 | 31.61 | 33.41 |
| Compiled + UTF-8 timestamps | 12,000 | 3,168.4 | 23.90 | 177.79 | 30.41 | 31.47 |
| Compiled + JSON metadata | 12,000 | 3,172.7 | 9.26 | 31.04 | 28.61 | 31.00 |
| Pool 32 | 16,000 | 4,241.1 | 15.98 | 70.85 | 36.20 | 34.09 |
| Pool 90 | 16,000 | 4,242.2 | 14.98 | 62.18 | 37.29 | 34.39 |
| Pool 8 | 16,000 | 4,189.0 | 85.06 | 1,121.28 | 33.30 | 34.32 |
| Alternative join, pool 90 | 16,000 | 4,240.8 | 22.29 | 50.94 | 36.86 | 35.26 |
| Pool 16 | 16,000 | 4,238.0 | 45.50 | 99.26 | 36.02 | 33.65 |
| Pool 64 | 16,000 | 4,240.7 | 15.44 | 88.13 | 36.48 | 34.23 |
| Alternative join, pool 16 | 16,000 | 4,240.3 | 26.19 | 72.77 | 35.55 | 34.93 |
| Generic plans + JIT off + no parallel workers, pool 16 | 16,000 | 3,904.4 | 2,508.80 | 4,554.75 | 46.09 | 33.30 |
| Explicit held connection, pool 32 | 20,000 | 5,063.9 | 1,212.42 | 1,921.02 | 43.00 | 37.51 |
| Ordinary EF connection lifetime, pool 32 | 20,000 | 5,261.3 | 222.08 | 545.79 | 44.24 | 36.76 |
| Idempotent upserts, held connection, pool 32 | 20,000 | 5,286.8 | 31.42 | 107.01 | 42.60 | 35.57 |
| Workstation GC | 20,000 | 3,558.9 | 3,207.17 | 6,250.50 | 28.61 | 34.67 |
| Server GC, maximum two heaps | 20,000 | 5,269.5 | 129.60 | 358.66 | 42.78 | 35.55 |
| PostgreSQL JIT off only | 20,000 | 5,283.0 | 41.79 | 200.83 | 42.64 | 36.42 |

The query-shape improvement is large enough to stand out despite local variability. Compiled queries also consistently move application CPU in the right direction across this phase. In contrast, the table does not justify attributing the large p99 swings between individual 12,000-user runs to timestamp formatting or JSON metadata alone. They are small, idiomatic changes retained as a combination, not independently proven multiplicative gains.

The ordered and unordered global capped counts produced the same index-only plan: 1,000 rows, six buffers, and zero heap fetches in the inspected seed. There is no evidence that ordering alone made the count faster. Likewise, the alternative join shape did not establish a meaningful improvement. Pool 8 and 16 produced worse tails under pressure; 32, 64, and 90 were close at 16,000 users.

Workstation GC regressed decisively. Neither limiting server GC heaps nor disabling PostgreSQL JIT alone established a reliable advantage, so their defaults remain unchanged. The forced-generic-plan combination was rejected both by workload results and by the actual query plans below.

## Historical sustained finalist selection

These are the original five-minute measurements at 20,000 users, after a three-minute 500-user warm-up and a one-minute ramp. The first two candidates ran in alternating order across workload seeds A and B. The subsequently superseded two-command-favorite candidate then repeated both seeds. All six runs had zero errors and a clean load-generator flag; the benchmark workload did not expose its rename race.

| Candidate | Seed/order | Requests/s | p95 ms | p99 ms | PG CPU % | App CPU % | SLA |
|---|---|---:|---:|---:|---:|---:|---|
| Held connection, three-command favorite | A, first | 5,276.4 | 88.32 | 474.62 | 45.93 | 36.94 | Pass |
| Ordinary EF connection lifetime | A, second | 4,959.9 | 1,233.92 | 2,803.71 | 48.08 | 38.31 | Fail |
| Ordinary EF connection lifetime | B, first | 5,290.3 | 33.31 | 109.57 | 46.94 | 36.12 | Pass |
| Held connection, three-command favorite | B, second | 5,277.7 | 45.28 | 494.59 | 45.54 | 36.52 | Pass |
| Superseded `favorite2`: held connection, two-command favorite | A | 5,292.2 | 31.57 | 145.54 | 44.90 | 35.83 | Pass |
| Superseded `favorite2`: held connection, two-command favorite | B | 5,277.8 | 89.79 | 273.66 | 45.66 | 36.05 | Pass |

The two-command candidate was originally selected because it passed both sustained trials without an observed CPU-efficiency regression. Independent review and deterministic regression tests subsequently disproved its rename correctness, so it is no longer retained. Its slightly lower CPU and better p99 values never established an independently repeatable percentage benefit for that one command change. Ordinary connection lifetimes were substantially less stable in this sample, despite producing the single lowest p99 in one trial. Neither a single best tail, a single short screen, nor a benchmark pass alone is a sound selection criterion.

Those historical unprofiled `favorite2` holds consumed approximately 2.7 PostgreSQL cores and 2.2 application cores at 5,285 requests/s. Database work remained slightly larger, but the application was no longer waiting mostly idle while PostgreSQL dominated the machine. Application processing, database processing, allocation/GC, and durable-write latency all matter near this load.

## Historical matched baseline comparison

These original controls compare the baseline with the superseded `favorite2` binary, using the ordinary `conduit` role on both sides, the same pinned runtime/SDK, a fresh full seed per trial, three-minute warm-ups, one-minute ramps, and two-minute measurements at 4,000 users. Candidate order is reversed for seed B. All four runs have zero errors, pass the SLA, and have a clean generator flag.

| Candidate | Seed/order | Requests/s | p95 ms | p99 ms | PG CPU % | App CPU % | PG CPU-ms/request | App CPU-ms/request |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| Original | A, first | 1,056.1 | 18.72 | 44.45 | 39.10 | 18.43 | 2.221 | 1.047 |
| Superseded `favorite2` | A, second | 1,057.4 | 3.55 | 6.46 | 9.37 | 15.10 | 0.532 | 0.857 |
| Superseded `favorite2` | B, first | 1,055.9 | 3.01 | 4.74 | 8.69 | 14.31 | 0.494 | 0.813 |
| Original | B, second | 1,054.8 | 18.05 | 41.57 | 38.17 | 18.02 | 2.171 | 1.025 |

Averaging these equal-load trials, PostgreSQL CPU/request falls from 2.196 to 0.513 ms, application CPU/request from 1.036 to 0.835 ms, and their sum from 3.232 to 1.348 ms: approximately 77%, 19%, and 58% reductions respectively. Mean feed latency falls from 20.2-21.2 ms to 1.7-2.0 ms.

Requests/s stays almost constant because this is the same think-time-driven offered load. The result demonstrates better efficiency and latency, not a measured 2.4-times capacity increase. The longer warm-up also explains why these baseline tails are much lower than the initial short screening trial; cross-phase tail differences must not be assigned to an optimization.

## Corrected candidate: fresh measurements and recovery

The immutable `correctness-id32` candidate uses assembly SHA-256 `EC93DD980E36320DB9A72503382947E50B38133124ED1E539BB65EAB804E1ACE`. Its snapshot matches the current application source and project/SDK files. The same pinned runtime, ordinary `conduit` role, seed cloning, CPU affinity, durability, 180-second warm-up, and 60-second ramp are retained. The corrected binary and preserved `favorite2` binary were not rebuilt between the failed and recovered holds.

### Completed equal-load controls

These are all three complete pairs that passed the SLA with zero errors and no generator flag. Pair A ran before the performance deterioration; the two recovery B pairs ran after the pause. The last pair reverses order. Timestamps are UTC run prefixes in `benchmarks/results`; retaining all of them makes the selection explicit rather than choosing a single best baseline or candidate tail.

| Pair/order | Run prefix | Candidate | Requests/s | p95 ms | p99 ms | PG CPU % | App CPU % | PG CPU-ms/request | App CPU-ms/request |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| A, first | `20261031T055505` | Original | 1,055.6 | 32.58 | 89.28 | 43.09 | 19.82 | 2.449 | 1.126 |
| A, second | `20261031T060143` | Corrected | 1,057.4 | 4.50 | 11.05 | 10.51 | 16.34 | 0.597 | 0.927 |
| Recovery B, first | `20261102T050422` | Original | 1,054.5 | 17.34 | 39.71 | 37.53 | 17.77 | 2.136 | 1.011 |
| Recovery B, second | `20261102T051056` | Corrected | 1,055.7 | 3.95 | 8.70 | 10.11 | 15.09 | 0.574 | 0.857 |
| Reversed B, first | `20261102T054635` | Corrected | 1,055.8 | 3.61 | 6.91 | 9.53 | 14.57 | 0.541 | 0.828 |
| Reversed B, second | `20261102T055307` | Original | 1,051.4 | 41.60 | 386.05 | 43.71 | 19.57 | 2.494 | 1.117 |

Across these pairs, mean PostgreSQL CPU/request is 2.360 ms original versus 0.571 ms corrected; application CPU/request is 1.085 versus 0.871 ms; combined is 3.444 versus 1.441 ms. These are approximately 76%, 20%, and 58% reductions. They are descriptive averages of three pairs (seed A once, seed B twice), not independent confidence intervals. Feed means are 19.09-45.53 ms original versus 1.97-2.31 ms corrected. Offered throughput remains essentially equal, so this is not a measured capacity multiplier.

The initial B attempt's corrected run (`20261031T060818`) also passed at 1,055.7 requests/s, p95 5.95 ms, p99 15.30 ms, PG CPU/request 0.672 ms, and app CPU/request 0.979 ms. Its subsequent baseline did not sustain the same offered rate and failed the SLA; therefore that attempt is not included in the equal-load paired average. Both raw results remain in the evidence.

### Sustained recovery comparison

These five-minute measurements use the original hold workload IDs. All three pass the SLA with zero errors and no generator limitation. The old binary runs only as a diagnostic control, not as a valid submission candidate.

| Candidate | Seed/order | Run prefix | Requests/s | p95 ms | p99 ms | PG CPU % | App CPU % |
|---|---|---|---:|---:|---:|---:|---:|---:|
| Superseded `favorite2` | A, first | `20261102T051727` | 5,296.9 | 21.87 | 79.74 | 44.40 | 35.65 |
| Corrected `correctness-id32` | A, second | `20261102T052700` | 5,288.0 | 48.32 | 230.40 | 45.72 | 36.16 |
| Corrected `correctness-id32` | B | `20261102T053635` | 5,277.3 | 65.06 | 400.13 | 45.67 | 36.39 |

Correctness has a potential cost, not a free performance claim: in the single same-seed old/new recovery comparison, PG CPU/request is 0.503 versus 0.519 ms, app CPU/request 0.404 versus 0.410 ms, and the corrected tail is higher. Neither the order nor the number of repetitions isolates the extra command's causal cost. The old candidate's original p99 range was 146-274 ms, while its recovery control reached 80 ms, demonstrating substantial host/run variability even without a source change. The corrected implementation nevertheless passes both recovery holds without abandoning standard framework features or durability.

### Failed attempts are not discarded

| Candidate/load | Run prefix | Requests/s | p95 ms | p99 ms | Errors % | Generator flag |
|---|---|---:|---:|---:|---:|---|
| Original, 4,000 users | `20261031T061450` | 858.2 | 4,175.87 | 6,397.95 | 0.067 | No |
| Corrected, 20,000 users, A | `20261031T062124` | 2,945.4 | 8,577.02 | 10,051.58 | 2.701 | Yes: sampler tick 2,791 ms late |
| Corrected, 20,000 users, B | `20261031T063104` | 3,040.6 | 8,183.81 | 10,002.43 | 1.618 | No |

The unmodified baseline deteriorated before the corrected high-load failures. A concurrent video call was reported while the local investigation was running, and testing was paused. A recovery attempt was interrupted before any timed result completed; its helper and PostgreSQL were stopped. Later runs resumed with the same binaries and fixture. This chronology makes host interference plausible, but no synchronized host trace establishes that the call caused the failures. The failed snapshots reported no PostgreSQL deadlocks or container memory-limit/OOM events; that does not rule out CPU scheduling, I/O, host activity, or other application/database effects. One failed hold had a generator flag and the other did not, so the generator flag alone does not explain the deterioration.

The recovery results demonstrate successful later runs, not proof that the failure mechanism is solved. Absolute p99 remains noisy, including the passing reversed baseline's 386-ms p99. All 13 new completed trials, including these failures and the extra successful B control, are retained. A dedicated Linux target and separate generator are still required before an official capacity claim.

## Remaining bottlenecks and plan evidence

### PostgreSQL is much cheaper, but still does real work

The bounded feed reduces unnecessary work for typical users; it does not eliminate per-request feed selection, capped counts, favorites counts, tag aggregation, or durable writes. Actual compiled SQL was captured and probed using the ordinary `conduit` role and session-local prepared statements. The following are representative `EXPLAIN (ANALYZE, BUFFERS)` executions, not repeated latency distributions or workload CPU shares.

| Query | Custom plan ms | Generic plan ms | Generic, JIT off/parallel off ms |
|---|---:|---:|---:|
| Reconstructed original feed, viewer 42 | 14.527 | 14.714 | 15.120 |
| Bounded feed, viewer 42 | 0.719 | 0.707 | 0.702 |
| Feed count, viewer 42 | 0.303 | 0.364 | 0.304 |
| Reconstructed original feed, highest-follow-count viewer 33573 | 0.562 | 466.876 | 160.586 |
| Bounded feed, viewer 33573 | 6.312 | 6.376 | 6.636 |
| Feed count, viewer 33573 | 0.305 | 0.354 | 0.334 |
| Tag-filtered page | 0.541 | 42.674 | 36.689 |
| Global page | 0.437 | 460.014 | 0.568 |
| Global capped count | 0.240 | 0.242 | 0.226 |
| Popular article detail | 0.371 | 0.377 | 0.364 |
| Popular article comments | 0.085 | 0.081 | 0.092 |

The original-feed probe reconstructs the original LINQ, including the followee/user join and tag fanout; it is not an execution of the untouched baseline binary. Its tag projection uses `ToArray` rather than `ToList`, which was separately verified not to remove that fanout.

**Skewed following lists remain a limitation.** Bounded selection is about 20 times faster for viewer 42 in this probe, but slower than the original's good custom plan for the most heavily connected viewer. Work grows with the number of followed authors and with deeper offsets. No claim is made that this query is universally optimal. Adaptive query selection would need a separate, representative investigation; adding request-time statistics or benchmark-specific heuristics was not justified by these results.

**Generic planning can be disastrous even with prepared statements.** Unknown page bounds inflate estimated work in some generic plans. A separate capture attributed about 303 ms of a 404-ms global-page execution to PostgreSQL JIT compilation. Disabling JIT removes that particular cost but does not repair the tag query's much broader scanning. Enabling preparation does not imply forcing generic plans; automatic custom/generic selection is important.

### Application work, GC, and unavoidable crypto

The first compiled-query profile at 12,000 users measured roughly 2.13 application cores, 128.5 MB/s allocated, about 3.9% of elapsed time in GC pauses, and a working set averaging 426 MB with a 527-MB maximum. EF reported about 4,204 queries/s and 149 saves/s; Npgsql reported about 99.8% prepared executions. Those values describe that instrumented run, not the final sustained-ranking runs.

The superseded `favorite2` candidate's separate 35-second profile at 20,000 users, after a three-minute warm-up, measured approximately 2.49 application cores (2.01 user, 0.48 system), 194.5 MB/s allocated, 3.2% elapsed GC pause time, and an 840-MB mean working set with a 1.00-GB maximum. The highest one-second GC-pause sample was 127 ms; that is an interval total, not necessarily one pause. EF reported approximately 6,176 queries/s and 95 saves/s, with about 99.97% prepared executions. This profile was not repeated for `correctness-id32`; its allocation and query rates are historical diagnostic evidence, not measurements of the corrected binary. The collectors themselves perturb execution, so these numbers must not replace an unprofiled hold's CPU measurements.

The application still performs EF query execution/materialization, standard JWT signature and expiry validation, JSON serialization, and bcrypt. Login and registration must retain bcrypt cost 10; neither password-result caching nor offloading it to a custom crypto implementation is an acceptable optimization here. GC is measurable, but the workstation-GC experiment shows that changing collectors is not a free throughput gain.

Connection/reset traffic and PostgreSQL WAL synchronization are additional costs. Sampled database sessions included active reads, connection reset commands, and some `WalSync`/`WALWrite` waits; many samples were idle client reads. That establishes the presence of those mechanisms, not their aggregate CPU or latency contribution.

In that historical profile's 27 database snapshots, there were 864 session observations: 739 were idle client reads; 13 active observations were `WalSync` and 12 were `WALWrite`. Eight active query observations showed connection-reset SQL. Active read observations included page projections and the capped feed count. These are sampled session observations, not counts of requests or percentages of CPU time. They support retaining connection reuse and treating durable-write stalls as a remaining tail-latency concern, not disabling resets or durability.

**Profiling limitation:** the collected managed traces are sampled thread-time/wall-clock stacks, heavily influenced by blocked waits. They must not be read as on-CPU percentages. The runtime advertised Linux on-CPU collection, but the actual collection failed with `Operation not permitted (os error 1)` in this Docker environment. No privileged workaround or host reconfiguration was attempted. Some runtime/Npgsql up-down instruments are exported as rates or deltas; negative deltas are not negative numbers of threads, queued requests, or executing commands.

The counter collector also warned that its ten-histogram tracking limit was reached. Runtime counters, prepared ratios, and the reported database samples were collected, but route-histogram coverage is incomplete. No complete per-route CPU attribution is claimed. A permitted on-CPU profile on the real Linux target is the next useful diagnostic, rather than speculative replacement of EF, authentication, or serialization.

## Validity and limits

At 24,000 users, cleaned held-connection and ordinary-lifetime candidates achieved 6,187 and 5,893 requests/s respectively, but both triggered the official load-generator CPU gate. Their p95/p99 values were 382/547 ms and 1,002/1,945 ms. These are diagnostic results, not valid capacity claims.

The gate reads whole-VM `/proc/stat` in Docker, so target CPU contaminates its generator CPU reading: whole-VM averages were 75.65% and 75.49%, while the generator processes used about 29% of their two allotted CPUs and did not fall behind. This does not demonstrate generator saturation. The gate was not disabled or changed; lower-load sustained comparisons avoid that flag. A separate load-generator machine is required for a credible official capacity search.

Native AOT was not presumed to improve this warmed-up server workload. This study retains ordinary CoreCLR JIT/PGO and mainstream EF support. It also excludes custom routing/authentication, handwritten wire protocols, native/custom bcrypt, cross-request batching, result caching, weakened durability, schema changes, and global PostgreSQL tuning. The completed matrix covers the reasonable mainstream levers supported by the observed bottlenecks; it is not proof of a universal optimum on every host and data distribution.

Source, reproducibility instructions, compact raw measurements, variant patches, query plans, and profile summaries are under [benchmarks](benchmarks/README.md). Published binaries and large diagnostic traces are intentionally not part of the submission.

## Correctness

The corrected candidate passes all 197 official conformance steps and the existing 40 focused API assertions. Focused cases cover sorted/empty tags, UTC timestamp precision, comments 404 versus empty results, bounded feed pagination and integer-overflow safety, filter precedence, anonymous/viewer isolation, concurrent duplicate favorites, missing-article favorite/unfavorite responses, repeated unfavorite operations, and concurrent creation of shared tags in opposite input order. The untouched baseline also passed all 197 steps in the original final harness.

Three new deterministic regressions force concurrent article renames with ordinary PostgreSQL transactions and locks, outside the timed workload. Favorite and unfavorite are blocked on an uncommitted favorite-row update while the author renames the article through the API. Delete resolves the old committed slug while an uncommitted article rename blocks its mutation; committing the rename then releases the delete. The checks verify the returned article/state or actual disappearance by stable ID. Each case failed against the preserved `favorite2` binary for the reviewed reason and passed against `correctness-id32`. No production test hooks, command interceptors, schema changes, or probabilistic race loops were added.

The database URI adapter also no longer silently discards query parameters such as `sslmode=verify-full` or certificate settings. It rejects query parameters, fragments, and non-PostgreSQL schemes when services are registered. Four focused checks verify this failure behavior. This is fail-fast rejection, not implementation of URI TLS options or proof of secure remote deployment; the local fixture continues using isolated loopback connections.

**Identifier limits remain a documented benchmark/production boundary.** At the review owner's request, non-empty username/title validation and the exact mandated slug formula are preserved rather than adding a non-contract length cap. Very long route-backed identifiers can still exceed Kestrel's default 8,192-byte HTTP/1.1 request-line limit; sufficiently large index keys can also hit PostgreSQL limits. Passing ordinary benchmark inputs is not proof that these fields are suitable for unrestricted production input. Production adoption needs an explicit, consistently enforced identifier-length policy and supported database TLS configuration.

The revision does not claim a general production hardening audit. Other reviewed policies remain unchanged: the benchmark's JWT issuer/audience and 30-day token requirements, post-commit response reads that can fail after a successful write, nontransactional page/count reads, synchronous bcrypt admission under hostile load, and feed cost for unusually large following lists or deep offsets. Those need application-specific requirements, not benchmark-specific shortcuts.
