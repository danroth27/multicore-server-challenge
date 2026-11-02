# C# with Minimal APIs and optimized EF Core queries

Front: direct

An optimization study based on [`csharp-efcore-xeonfx`](../csharp-efcore-xeonfx), contributed in [#20](https://github.com/arjaythedev/multicore-server-challenge/pull/20). It retains Kestrel, Minimal APIs, EF Core, the standard JWT bearer handler, BCrypt.Net-Next at cost 10, and System.Text.Json on CoreCLR.

The improvements are mostly ordinary data-access work: bounded feed selection expressed in LINQ, smaller DTO projections, scalar tag aggregation, compiled queries, prepared statements, and fewer database round trips. Three parameterized EF Core SQL operations implement concurrency-safe insertion of follows, favorites, and missing tags. There is no custom HTTP server, authentication implementation, cryptography, database protocol, cross-request batching, or result cache.

See the [optimization report](OPTIMIZATION-REPORT.md) for experiments, local results, rejected alternatives, and remaining bottlenecks. These are local Docker measurements, not an official leaderboard score.

Independent review found rename races in the two-command favorite optimization and inherited article deletion. The current implementation resolves stable article IDs for favorite, unfavorite, and authorized deletion; it deliberately restores the extra favorite lookup. The report identifies the superseded measurements separately from the corrected candidate.

Across three completed, SLA-passing 4,000-user pairs, including reverse order, the corrected implementation uses approximately 76% less PostgreSQL CPU/request and 58% less combined app/database CPU/request. Two recovery five-minute 20,000-user holds pass at about 5,283 requests/s, p99 230-400 ms, and zero errors. Earlier failed trials are also retained: shared-host variability remains unresolved, so these are not guaranteed sustained-capacity numbers.

## Run

Requires the .NET 10 SDK and the benchmark's PostgreSQL schema and seed. `global.json` selects SDK 10.0.403. Run the following from this submission directory. On the benchmark's Linux target, `install.sh`, `build.sh`, and `start.sh` implement the standard submission lifecycle.

```powershell
dotnet publish .\Conduit.csproj -c Release -o .\bin
$env:DATABASE_URL = 'postgres://conduit:conduit@localhost:5432/conduit'
$env:JWT_SECRET = 'replace-with-a-secret'
$env:HOST = '0.0.0.0'
$env:PORT = '3110'
dotnet .\bin\conduit.dll
```

The database URL and JWT secret above are examples, not production credentials.

`DATABASE_URL` accepts `postgres` or `postgresql` URIs without query parameters or fragments. Unsupported parameters, including TLS options, fail at startup rather than being silently ignored. This benchmark adapter is not a general-purpose production connection configuration. Username/title length behavior remains as prescribed by the benchmark; production use needs explicit route-backed identifier limits.

| Setting | Default | Purpose |
|---|---:|---|
| `DB_POOL_SIZE` | 32 | Maximum physical PostgreSQL connections |
| `DB_MAX_AUTO_PREPARE` | 64 | Npgsql automatic prepared-statement capacity per connection |
| `DB_AUTO_PREPARE_MIN_USAGES` | 2 | Executions before automatic preparation |
| `DB_OPTIONS` | unset | Optional PostgreSQL session settings, used only for experiments |

Keep PostgreSQL's automatic custom/generic plan selection and normal server GC. Forcing generic plans and switching to workstation GC regressed this workload. Never use session settings that weaken commit durability or violate the benchmark rules.

The pool default is a measured choice for this local fixture, not a universal sizing recommendation. EF context pooling was already present in the original implementation. Read projections contain DTOs rather than tracked entities; adding `AsNoTracking()` to them is not the missing optimization.

## Reproduce the study

The [benchmark instructions](benchmarks/README.md) describe the dedicated database fixture, immutable candidate publishing, conformance checks, and matched trials. Scripts and compact evidence are included; generated binaries, database data, and working directories are not.
