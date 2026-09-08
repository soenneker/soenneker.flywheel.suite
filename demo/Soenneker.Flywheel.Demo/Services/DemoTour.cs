using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Demo.Dtos;
using Soenneker.Flywheel.Demo.Requests;
using Soenneker.Flywheel.Demo.Responses;
using Soenneker.Flywheel.Generated;

namespace Soenneker.Flywheel.Demo.Services;

public sealed class DemoTour(IJobClient client, IJobStore store, ILogger<DemoTour> logger)
{
    public async Task<DemoRun> Run(string? batchId = null, CancellationToken cancellationToken = default)
    {
        batchId ??= Guid.NewGuid().ToString("N");
        if (batchId.Length > 100 || string.IsNullOrWhiteSpace(batchId)) throw new ArgumentException("Demo batch id must be 1–100 characters.");
        var jobs = new List<DemoJob>();
        var retry = new JobPolicy { MaxAttempts = 4, InitialBackoff = TimeSpan.FromSeconds(2), MaxBackoff = TimeSpan.FromSeconds(5) };

        string welcome = await Add("Welcome + deduplication", FlywheelJobs.NotificationJobs_Welcome, new WelcomeEmail("Taylor", "taylor@example.test"), "welcome");
        string duplicate = await client.Enqueue(FlywheelJobs.NotificationJobs_Welcome, new WelcomeEmail("Taylor", "taylor@example.test"),
            idempotencyKey: $"demo:{batchId}:welcome", cancellationToken: cancellationToken);
        await Add("Report with progress logs (ValueTask)", FlywheelJobs.ReportJobs_Generate, new ReportRequest("Monthly sales", 5), "report");
        await Add("Delayed report (15 seconds)", FlywheelJobs.ReportJobs_Delayed, new ReportRequest("Deferred export", 3), "delayed", delay: TimeSpan.FromSeconds(15));
        foreach (int minutes in new[] { 5, 15, 30, 60, 120 })
        {
            await Add($"Scheduled report ({minutes} minutes)", FlywheelJobs.ReportJobs_Delayed,
                new ReportRequest($"Scheduled export in {minutes} minutes", 3), $"scheduled-report-{minutes}", delay: TimeSpan.FromMinutes(minutes));
        }
        await Add("Order event payload", FlywheelJobs.OrderJobs_OnPlaced, new OrderPlaced($"order-{batchId}", 3, 129.95m), "order");
        await Add("Transient delivery: succeeds on attempt 3", FlywheelJobs.DeliveryJobs_Send, new DeliveryRequest($"delivery-{batchId}"), "retry", retry);
        await Add("Permanent failure → dead letter", FlywheelJobs.FailureJobs_Reject, new FailureRequest("Unsupported demo document format"), "dead-letter", retry with { MaxAttempts = 3 });
        await Add("Timeout → retry → dead letter", FlywheelJobs.LongRunningJobs_TimeOut, new WorkRequest(30), "timeout",
            retry with { MaxAttempts = 2, Timeout = TimeSpan.FromSeconds(3) });
        await Add("Long-running: cancel from the dashboard", FlywheelJobs.LongRunningJobs_Cancellable, new WorkRequest(120), "cancel",
            new JobPolicy { MaxAttempts = 1, Timeout = TimeSpan.FromMinutes(3) });
        string cancelled = await Add("Cancelled before execution", FlywheelJobs.NotificationJobs_Welcome, new WelcomeEmail("Cancelled example", "nobody@example.test"), "pre-cancelled",
            delay: TimeSpan.FromMinutes(2));
        await store.Cancel(cancelled, cancellationToken);
        bool recurring = await client.Recurring("demo-pulse-v1", FlywheelJobs.MaintenanceJobs_Pulse, new PulseRequest("Recurring maintenance"),
            TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

        foreach (DemoJob job in jobs) logger.LogInformation("{Scenario}: {JobId}", job.Scenario, job.Id);
        logger.LogInformation("Demo batch {BatchId}: duplicate enqueue returned the same id: {Deduplicated}", batchId, welcome == duplicate);
        return new DemoRun(batchId, jobs, welcome == duplicate, recurring);

        async Task<string> Add<T>(string scenario, JobDefinition<T> definition, T payload, string key, JobPolicy? policy = null, TimeSpan? delay = null)
        {
            string id = await client.Enqueue(definition, payload, policy, delay, $"demo:{batchId}:{key}", cancellationToken);
            jobs.Add(new DemoJob(scenario, id));
            return id;
        }
    }
}
