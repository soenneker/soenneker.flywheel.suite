# Flywheel Redis performance comparison

Run against an isolated Redis server (the harness creates and removes its own namespace):

```powershell
$env:FLYWHEEL_TEST_REDIS = "localhost:16379,allowAdmin=true"
dotnet run --project test/Soenneker.Flywheel.Redis.Performance -c Release -- results.json
```

The optional output path receives JSON. `allowAdmin=true` is required for INFO command counts. Do not run other workloads against the same server while measuring.

Results from the local comparison are in `before.json` and `after.json`. The harness measures idle polling, a 1,000-job backlog, lease renewal, and a 50-line log append. Results depend on the host and Redis configuration.
