# Standalone performance measurements

This console harness starts the worker pool, maintenance, and live recorder against an isolated Redis server.
It warms up for five seconds, then measures the whole process for 61 seconds without a test framework,
web server, dashboard connection, job handlers, or recurring schedules. The default is 12 workers, matching
the checked-in production worker count in Leadping's engine. It removes only its own random namespace and
stops all services before exiting. The caller owns the Redis server.

| Measurement, 12 workers / 61 seconds | Baseline | Updated | Reduction |
| --- | ---: | ---: | ---: |
| Process allocation | 24,437 B/s | 2,132 B/s | 91.3% |
| Redis command processing | 24.97/s | 1.95/s | 92.2% |
| Process CPU time over the measured minute | 437.5 ms | 171.9 ms | 60.7% |

Raw results: [baseline](idle-before.json), [updated](idle-after.json), and [connection-only control](idle-control.json).
The control allocated 523 B/s. CPU values are observations from these local runs, not a production CPU guarantee.
The idle changes passed 149 regression tests (16 Core + 53 Redis + 80 Dashboard). The dispatch changes
include five Redis integration cases for concurrent dispatch, required metadata, priority across read
windows, Unicode metadata, version restrictions, and cleanup.
After removing compatibility paths, all 58 Redis and 16 Core cases passed.

```powershell
$env:FLYWHEEL_TEST_REDIS = "localhost:16379,allowAdmin=true"
$env:FLYWHEEL_BENCHMARK_WORKERS = "12"
$env:FLYWHEEL_BENCHMARK_SECONDS = "61"
$env:FLYWHEEL_BENCHMARK_OUTPUT = "$PWD/idle-results.json"
dotnet run --project test/Soenneker.Flywheel.Performance -c Release
```

Set `FLYWHEEL_BENCHMARK_CONTROL=1` to measure just the idle process and Redis connection. Clear it before
measuring Flywheel. Allocation totals include Redis client background threads. Redis command totals come
from `INFO stats` and include commands executed inside Lua, not just client requests; the final INFO call
is excluded. CPU time is process CPU time, subject to OS timer granularity. Run measurements sequentially
with no other clients using that Redis server.

For a before/after comparison, use this identical harness with both source trees. `FlywheelSourceRoot` is an
MSBuild property selecting the source tree; it defaults to this repository. Build the two versions into
different artifact directories so dependencies cannot be accidentally mixed:

```powershell
dotnet build test/Soenneker.Flywheel.Performance -c Release `
  -p:FlywheelSourceRoot=C:/path/to/baseline --artifacts-path artifacts/perf-before
dotnet artifacts/perf-before/bin/Soenneker.Flywheel.Performance/release/Soenneker.Flywheel.Performance.dll
```

The committed JSON files compare baseline commit `10ed02c` with the working changes on .NET 10 / Redis 6.0.16,
on Windows with Redis in WSL2. These are local idle measurements, not end-to-end Leadping measurements or
a claim about maximum job throughput. Payload-heavy dispatch, dashboard clients, Redis network latency,
and distributed failover need separate workloads.

## Backlog and concurrency operations

The dispatch comparison uses the same standalone operation harness against baseline commit `10ed02c`
and the current changes, on .NET 10.0.12 / Redis 8.10.0, Windows with Redis in WSL2. Each scenario gets
50 warmup operations; backlog and concurrency get 100 measured operations. The 1,000-job backlog has
4 KB JSON payloads and measures a claim followed by a retry with zero delay. The concurrency scenario
keeps 100 running jobs, with a method concurrency limit of 101, and measures claim, completion, and
enqueue of the replacement pending job. This is not a sustained throughput test with 100 active handlers.

| Per operation | Baseline | Updated | Reduction |
| --- | ---: | ---: | ---: |
| Backlog allocation | 4,787,778 B | 293,016 B | 93.9% |
| Backlog elapsed time | 18.31 ms | 6.29 ms | 65.6% |
| Backlog process CPU | 19.22 ms | 5.63 ms | 70.7% |
| Backlog Redis CPU | 3.23 ms | 1.24 ms | 61.8% |
| Concurrency allocation | 629,579 B | 176,722 B | 71.9% |
| Concurrency elapsed time | 19.74 ms | 17.46 ms | 11.6% |

Raw results: [baseline](dispatch-before.json) and [initial dispatch optimization](dispatch-after.json).
The initial dispatch results were captured before removing compatibility code. These supersede the
short exploratory backlog measurements from the idle work. Elapsed and CPU timings vary with scheduling,
tiered compilation, and this local network; they are observations, not production guarantees. CPU totals
sum all process threads and may exceed elapsed time. Redis CPU is the change in `used_cpu_user` plus
`used_cpu_sys`, including the two surrounding command-count INFO calls. No other workload used the server.

```powershell
$env:FLYWHEEL_TEST_REDIS = "localhost:16390,allowAdmin=true"
$env:FLYWHEEL_BENCHMARK_MODE = "operations"
$env:FLYWHEEL_BENCHMARK_OUTPUT = "$PWD/operations.json"
dotnet run --project test/Soenneker.Flywheel.Performance -c Release
```

Use the `FlywheelSourceRoot` and separate build artifact directories shown above to compare revisions.
Remove `FLYWHEEL_BENCHMARK_MODE` to return to the idle host scenario. The explicit TUnit benchmark links
this same operation harness instead of maintaining a separate implementation.

The current operation harness calls `SampleLiveActivity` directly through its internal test entry point.
The historical operation baseline used the sampler entry point present in that revision; reproducing
that baseline requires its corresponding harness. There is no sampler API compatibility shim.

Dispatch reads a required binary index with four outstanding reads of at most 128 entries, then loads
only the selected payload. It has no old-writer detection, compatibility checkpoint, payload-based
metadata fallback, or automatic rebuilding. See [storage details](../../docs/redis.md#dispatch-index).

The index adds Redis memory proportional to queued/running jobs. In the initial optimization measurements,
which still included a compatibility checkpoint, backlog command processing rose from 59 to 65 commands per operation (including Lua
subcommands), while Redis CPU and transferred payload volume fell. Renewal allocation rose from
39,374 B to 40,398 B and commands from 22 to 23. Concurrency process CPU rose from 8.13 to 11.25 ms in
this pair even though allocation, elapsed time, and Redis CPU fell; the results do not establish a CPU
win for every workload. The checkpoint write and its supporting reads have since been removed. Renewal
and logging still read full job records, and selection still scans all
eligible metadata. These measurements do not establish globally optimal performance or a world ranking.

The changes remove broadcast idle probes, per-second empty sampling, redundant empty-maintenance work,
and heartbeat-induced dispatch conflicts/dashboard refreshes. Active jobs still receive one-second live
sampling. An unchanged lifecycle revision proves empty history; gaps without that proof stay unknown.

Functional validation uses Redis 8.10.0, matching CI. During local validation, Redis 6.0.16 with RESP3
returned an unexpected push response for a self-published transaction; the same test passed with
`protocol=resp2` and with Redis 8.10.0's default protocol. Use the CI Redis version for the integration suite.
The idle benchmark does not enqueue jobs and is kept on the same Redis 6 server for both measurements.

The [current-format-only run](dispatch-current-only.json), after compatibility removal, measured
291,759 B and 5.44 ms per backlog operation, with 63 Redis commands. Renewal used 39,590 B and
22 commands. These are separate local observations with the same workload and warmup settings;
the historical comparison above remains labeled with its original implementation.
