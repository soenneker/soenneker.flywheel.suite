using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Services;
using Soenneker.Flywheel.Redis;
using StackExchange.Redis;

if (Environment.GetEnvironmentVariable("FLYWHEEL_BENCHMARK_MODE") == "operations")
{
    await Soenneker.Flywheel.Redis.Performance.RedisOperationsBenchmark.Run();
    return;
}

// Standalone process: no test runner, web server, dashboard connection, or per-second measurement timer.
// Both revisions must use this same harness and an otherwise idle, isolated Redis server.
string connectionString = Environment.GetEnvironmentVariable("FLYWHEEL_TEST_REDIS") ?? "localhost:16379,allowAdmin=true";
int workerCount = int.TryParse(Environment.GetEnvironmentVariable("FLYWHEEL_BENCHMARK_WORKERS"), out int count) ? count : 12;
int seconds = int.TryParse(Environment.GetEnvironmentVariable("FLYWHEEL_BENCHMARK_SECONDS"), out int duration) ? duration : 61;
bool control = Environment.GetEnvironmentVariable("FLYWHEEL_BENCHMARK_CONTROL") == "1";
if (workerCount is < 1 or > 256 || seconds < 15) throw new ArgumentOutOfRangeException("Use 1–256 workers and at least 15 seconds.");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
IDatabase db = connection.GetDatabase();
IServer server = connection.GetServer((await db.IdentifyEndpointAsync("performance"))!);
string ns = "performance-host-" + Guid.NewGuid().ToString("N");
string prefix = "flywheel:{" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ns))) + "}:v1:";
var store = new RedisJobStore(_ => Task.FromResult(db), ns);
var options = new FlywheelOptions { Workers = workerCount };
await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
var executor = new JobExecutor(store, services.GetRequiredService<IServiceScopeFactory>(), [], options, NullLogger<JobExecutor>.Instance);
using var worker = new WorkerService(executor, store, options, NullLogger<WorkerService>.Instance);
using var maintenance = new MaintenanceService(new MaintenanceRunner(store, store, worker, options), options, NullLogger<MaintenanceService>.Instance);
using var recorder = new RedisLiveActivityRecorder(store, NullLogger<RedisLiveActivityRecorder>.Instance);
IHostedService[] hosted = control ? [] : [worker, maintenance, recorder];
try
{
    foreach (IHostedService service in hosted) await service.StartAsync(stop.Token);
    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long commands = await Commands();
    using Process process = Process.GetCurrentProcess();
    TimeSpan cpu = process.TotalProcessorTime;
    long bytes = GC.GetTotalAllocatedBytes(true);
    long started = Stopwatch.GetTimestamp();
    await Task.Delay(TimeSpan.FromSeconds(seconds), stop.Token);
    double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
    bytes = GC.GetTotalAllocatedBytes(true) - bytes;
    double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
    long commandCount = await Commands() - commands - 1;
    string json = JsonSerializer.Serialize(new
    {
        Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        RedisVersion = server.Version.ToString(), Workers = workerCount, Control = control, Seconds = elapsed,
        AllocatedBytes = bytes, BytesPerSecond = bytes / elapsed, CpuMilliseconds = cpuMs,
        RedisCommands = commandCount, RedisCommandsPerSecond = commandCount / elapsed
    }, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    string? output = Environment.GetEnvironmentVariable("FLYWHEEL_BENCHMARK_OUTPUT");
    if (!string.IsNullOrWhiteSpace(output)) await File.WriteAllTextAsync(output, json, stop.Token);
}
finally
{
    for (int i = hosted.Length - 1; i >= 0; i--) await hosted[i].StopAsync(CancellationToken.None);
    await foreach (RedisKey key in server.KeysAsync(pattern: prefix + "*")) await db.KeyDeleteAsync(key);
}

async Task<long> Commands() => long.Parse((await server.InfoAsync("stats")).SelectMany(group => group)
    .Single(pair => pair.Key == "total_commands_processed").Value);
