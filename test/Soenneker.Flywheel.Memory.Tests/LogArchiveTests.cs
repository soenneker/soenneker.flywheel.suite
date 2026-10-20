using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Registrars;
using System.Threading;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class LogArchiveTests
{
    private static ServiceProvider Create(DebounceClock clock, bool enabled = true, Action<LogArchiveOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        var archiveOptions = new LogArchiveOptions
        {
            Enabled = enabled, MaxFileSizeBytes = 32768, RotationInterval = TimeSpan.FromSeconds(1),
            ExportTimeout = TimeSpan.FromSeconds(1)
        };
        configure?.Invoke(archiveOptions);
        services.AddSingleton(archiveOptions);
        services.AddFlywheel().AddMemory(options => options.HistoryRetention = TimeSpan.FromMinutes(5));
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task SharedFilesRotateAtMidnightUtcAndKeepEachJobIdentity(CancellationToken cancellationToken)
    {
        var defaults = new LogArchiveOptions();
        if (defaults.MaxFileSizeBytes != 200 * 1024 * 1024 || defaults.RotationInterval != TimeSpan.FromDays(1) || defaults.Enabled)
            throw new Exception("Archive defaults changed.");
        var clock = new DebounceClock();
        clock.Advance(TimeSpan.FromHours(12) - TimeSpan.FromSeconds(1));
        await using ServiceProvider provider = Create(clock, configure: options =>
        {
            options.RotationInterval = defaults.RotationInterval;
            options.MaxFileSizeBytes = defaults.MaxFileSizeBytes;
        });
        var store = provider.GetRequiredService<MemoryJobStore>();
        string firstId = await store.Enqueue(new EnqueueRequest("first", "{}", new JobPolicy(), TimeSpan.Zero, null), cancellationToken);
        JobLease first = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        string secondId = await store.Enqueue(new EnqueueRequest("second", "{}", new JobPolicy(), TimeSpan.Zero, null), cancellationToken);
        JobLease second = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        await store.AppendLogs(first, [new JobLogMessage("Information", "test", "first")], cancellationToken);
        await store.AppendLogs(second, [new JobLogMessage("Information", "test", "second")], cancellationToken);
        if (await store.ClaimLogArchive(cancellationToken) is not null) throw new Exception("Daily file closed early.");
        long beforeMidnight = clock.GetUtcNow().ToUnixTimeMilliseconds();
        clock.Advance(TimeSpan.FromSeconds(1));
        // A new append must rotate before putting the new day's entry into the file.
        await store.AppendLogs(first, [new JobLogMessage("Information", "test", "next day")], cancellationToken);
        LogArchiveClaim yesterday = (await store.ClaimLogArchive(cancellationToken))!;
        if (yesterday.File.Entries.Count != 2 ||
            !yesterday.File.Entries.Select(e => e.JobId).ToHashSet().SetEquals([firstId, secondId]) ||
            yesterday.File.Entries.Any(e => e.Log.Timestamp != beforeMidnight))
            throw new Exception("Shared daily file mixed dates or lost job identity.");
        await store.CompleteLogArchive(yesterday, cancellationToken);
        if (await store.ClaimLogArchive(cancellationToken) is not null) throw new Exception("New daily file closed early.");
        clock.Advance(TimeSpan.FromDays(1));
        LogArchiveClaim today = (await store.ClaimLogArchive(cancellationToken))!;
        if (today.File.Id == yesterday.File.Id || today.File.Entries.Single().Log.Message != "next day")
            throw new Exception("Idle daily rotation failed.");
        await store.CompleteLogArchive(today, cancellationToken);
        clock.Advance(TimeSpan.FromDays(1));
        if (await store.ClaimLogArchive(cancellationToken) is not null) throw new Exception("Empty archive was generated.");
    }

    [Test]
    public async Task RotationPreservesLogsBeyondDashboardLimitsAndExpiration(CancellationToken cancellationToken)
    {
        var clock = new DebounceClock();
        await using ServiceProvider provider = Create(clock);
        var store = provider.GetRequiredService<MemoryJobStore>();
        string id = await store.Enqueue(new EnqueueRequest("archive", "{}", new JobPolicy(), TimeSpan.Zero, null), cancellationToken);
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        for (int batch = 0; batch < 21; batch++)
        {
            JobLogMessage[] messages = Enumerable.Range(0, 50)
                .Select(i => new JobLogMessage("Information", "test", $"{batch * 50 + i}:雪\"\\\n" + new string('x', 100))).ToArray();
            if (!await store.AppendLogs(lease, messages, cancellationToken)) throw new Exception("Lease was rejected.");
        }
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        if (await store.AppendLogs(lease, [new JobLogMessage("Error", "test", "stale")], cancellationToken))
            throw new Exception("Stale logs were accepted.");
        clock.Advance(TimeSpan.FromMinutes(6));
        await store.Maintain(100, cancellationToken);
        if ((await store.GetLogs(id, cancellationToken: cancellationToken)).Count != 0)
            throw new Exception("Dashboard logs did not expire.");
        var entries = new List<JobLogEntry>();
        while (await store.ClaimLogArchive(cancellationToken) is { } claim)
        {
            int bytes = JsonSerializer.SerializeToUtf8Bytes(claim.File, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
            if (bytes > 32768) throw new Exception("File exceeded its serialized byte limit.");
            if (claim.File.Entries.Any(e => e.JobId != id)) throw new Exception("Archive lost job identity.");
            entries.AddRange(claim.File.Entries.Select(e => e.Log));
            await store.CompleteLogArchive(claim, cancellationToken);
        }
        if (entries.Count != 1050 || entries.Select(e => e.Id).Distinct().Count() != 1050 ||
            entries.Any(e => e.Message == "stale"))
            throw new Exception("Archive lost or duplicated accepted entries.");
    }

    [Test]
    public async Task ClaimsRetryAndStaleAcknowledgmentsCannotDeleteNewClaims(CancellationToken cancellationToken)
    {
        var clock = new DebounceClock();
        await using ServiceProvider provider = Create(clock);
        var store = provider.GetRequiredService<MemoryJobStore>();
        await store.Enqueue(new EnqueueRequest("archive", "{}", new JobPolicy(), TimeSpan.Zero, null), cancellationToken);
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        await store.AppendLogs(lease, [new JobLogMessage("Information", "test", "saved")], cancellationToken);
        if (await store.ClaimLogArchive(cancellationToken) is not null) throw new Exception("Open file exported too early.");
        clock.Advance(TimeSpan.FromSeconds(2));
        LogArchiveClaim first = (await store.ClaimLogArchive(cancellationToken))!;
        if (first is null || await store.ClaimLogArchive(cancellationToken) is not null)
            throw new Exception("Claim was not exclusive.");
        clock.Advance(TimeSpan.FromSeconds(32));
        LogArchiveClaim second = (await store.ClaimLogArchive(cancellationToken))!;
        if (second.File.Id != first.File.Id || second.Token == first.Token) throw new Exception("Retry changed file identity.");
        await store.CompleteLogArchive(first, cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(32));
        LogArchiveClaim third = (await store.ClaimLogArchive(cancellationToken))!;
        if (third is null) throw new Exception("Stale acknowledgment deleted pending data.");
        await store.CompleteLogArchive(third, cancellationToken);
        if (await store.ClaimLogArchive(cancellationToken) is not null) throw new Exception("Acknowledged file remained queued.");
    }

    [Test]
    public async Task LargeNumberOfPendingBatchesCanBeAssembledAndAcknowledged(CancellationToken cancellationToken)
    {
        var clock = new DebounceClock();
        await using ServiceProvider provider = Create(clock, configure: options => options.MaxFileSizeBytes = 200 * 1024 * 1024);
        var store = provider.GetRequiredService<MemoryJobStore>();
        await store.Enqueue(new EnqueueRequest("many-batches", "{}", new JobPolicy(), TimeSpan.Zero, null), cancellationToken);
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        for (int i = 0; i < 205; i++)
            await store.AppendLogs(lease, [new JobLogMessage("Information", "test", i.ToString())], cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        LogArchiveClaim claim = (await store.ClaimLogArchive(cancellationToken))!;
        if (claim.File.Entries.Count != 205 || claim.File.Entries.Last().Log.Message != "204")
            throw new Exception("Archive assembly lost pending batches.");
        await store.CompleteLogArchive(claim, cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(2));
        if (await store.ClaimLogArchive(cancellationToken) is not null)
            throw new Exception("Archive cleanup left an exportable file.");
    }

    [Test]
    public async Task DisabledArchivalDoesNotQueueLogs(CancellationToken cancellationToken)
    {
        var clock = new DebounceClock();
        await using ServiceProvider provider = Create(clock, enabled: false);
        var store = provider.GetRequiredService<MemoryJobStore>();
        await store.Enqueue(new EnqueueRequest("archive", "{}", new JobPolicy(), TimeSpan.Zero, null), cancellationToken);
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        await store.AppendLogs(lease, [new JobLogMessage("Information", "test", "saved")], cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(10));
        if (await store.ClaimLogArchive(cancellationToken) is not null) throw new Exception("Disabled archive queued logs.");
    }
}
