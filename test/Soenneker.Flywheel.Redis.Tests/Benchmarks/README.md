# Flywheel Redis performance comparison

Run against an isolated Redis server (the harness creates and removes its own namespace):

```powershell
$env:FLYWHEEL_TEST_REDIS = "localhost:16379,allowAdmin=true"
$env:FLYWHEEL_BENCHMARK_OUTPUT = "$PWD/results.json"
dotnet test --project test/Soenneker.Flywheel.Redis.Tests -c Release -- --treenode-filter "/*/*/BenchmarkRunner/RedisOperations"
```

The optional `FLYWHEEL_BENCHMARK_OUTPUT` path receives JSON. This explicit test only runs when selected and is excluded from normal test runs. `allowAdmin=true` is required for INFO command counts. Do not run other workloads against the same server while measuring.

Results from the local comparison are in `before.json` and `after.json`. The harness measures idle polling, a 1,000-job backlog, lease renewal, and a 50-line log append. Results depend on the host and Redis configuration.

The newer `idle-operations-before.json` and `idle-operations-after.json` also cover empty maintenance and
live sampling. The sampler microbenchmark repeatedly calls within the same second, measuring duplicate
sample suppression after the first write. Use the [standalone host harness](../../Soenneker.Flywheel.Performance/README.md)
for the adaptive idle recorder, whole-process idle allocations, and CPU measurements without TUnit overhead.

The current operation implementation lives in the standalone harness and is linked into this explicit
test. It now uses 50 warmup iterations and also measures a method with 100 running jobs. For the latest
before/after backlog results, including process and Redis CPU, use the [standalone results](../../Soenneker.Flywheel.Performance/README.md#backlog-and-concurrency-operations).
The older JSON files here retain their original five-iteration warmup methodology.
