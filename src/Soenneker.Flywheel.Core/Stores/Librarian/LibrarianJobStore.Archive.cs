using System.Text.Json;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore : IJobLogArchiveQueue
{
    internal Task ConfigureLogArchiveSchedule(CancellationToken cancellationToken) =>
        Mutate(cancellationToken, async now =>
        {
            const string id = LogArchiveJobInvoker.JobName;
            if (_logArchive is not { Enabled: true } options)
            {
                await _schedules.Remove(id);
                return true;
            }
            var request = new Soenneker.Flywheel.Communication.Requests.EnqueueRequest(id, "{}",
                new Soenneker.Flywheel.Communication.Dtos.JobPolicy
                {
                    Timeout = options.ExportTimeout * 2 + TimeSpan.FromMinutes(1)
                }, TimeSpan.Zero, Description: "Export pending log archives");
            long interval = (long)options.ExportInterval.TotalMilliseconds;
            Schedule? existing = await _schedules.Get(id);
            await _schedules.Set(id, existing is null
                ? new Schedule(Create(request) with { IsSystem = true }, interval, now)
                : existing with
                {
                    Job = existing.Job with { Policy = request.Policy, Description = request.Description, IsSystem = true },
                    Interval = interval,
                    DueAt = existing.Interval == interval ? existing.DueAt : now + interval
                });
            return true;
        });

    private readonly LibrarianTable<string, LogArchiveFileState> _archiveOpen = new("archiveOpen");
    private readonly LibrarianTable<string, LogArchiveFileState> _archiveReady = new("archiveReady");
    private readonly LibrarianTable<string, LogArchiveEntry[]> _archiveParts = new("archiveParts");
    private LogArchiveOptions? _logArchive;

    /// <summary>Archive settings fixed when constructing the store. Pending data is never expired by history cleanup.</summary>
    public LogArchiveOptions? LogArchive
    {
        get => _logArchive;
        init
        {
            value?.Validate();
            _logArchive = value;
        }
    }

    private static LogArchiveFileState NewArchiveFile(long now, LogArchiveOptions options)
    {
        string id = Guid.NewGuid().ToString();
        long interval = (long)options.RotationInterval.TotalMilliseconds;
        long rotateAt = checked((now / interval + 1) * interval);
        int bytes = JsonSerializer.SerializeToUtf8Bytes(new LogArchiveFile(id, now, []),
            LogArchiveJsonContext.Default.LogArchiveFile).Length;
        return new LogArchiveFileState(id, now, rotateAt, bytes);
    }

    private async Task<LogArchiveFileState> SaveArchivePart(LogArchiveFileState file, List<LogArchiveEntry> entries)
    {
        if (entries.Count == 0) return file;
        await _archiveParts.Set($"{file.Id}:{file.PartCount}", entries.ToArray());
        entries.Clear();
        return file with { PartCount = file.PartCount + 1 };
    }

    private async Task QueueArchive(string jobId, IReadOnlyList<JobLogEntry> entries, long now)
    {
        if (_logArchive is not { Enabled: true } options) return;
        // One rolling file per runtime namespace, shared by every job and worker.
        LogArchiveFileState file = await _archiveOpen.Get("current") ?? NewArchiveFile(now, options);
        var buffered = new List<LogArchiveEntry>(entries.Count);
        foreach (JobLogEntry entry in entries)
        {
            var archived = new LogArchiveEntry(jobId, entry);
            int bytes = JsonSerializer.SerializeToUtf8Bytes(archived, LogArchiveJsonContext.Default.LogArchiveEntry).Length;
            bool hasEntries = file.PartCount > 0 || buffered.Count > 0;
            if (hasEntries && (now >= file.RotateAt || file.Bytes + bytes + 1 > options.MaxFileSizeBytes))
            {
                file = await SaveArchivePart(file, buffered);
                await _archiveReady.Set(file.Id, file);
                file = NewArchiveFile(now, options);
                hasEntries = false;
            }
            file = file with { Bytes = file.Bytes + bytes + (hasEntries ? 1 : 0) };
            buffered.Add(archived);
        }
        file = await SaveArchivePart(file, buffered);
        await _archiveOpen.Set("current", file);
    }

    public async Task<LogArchiveClaim?> ClaimLogArchive(CancellationToken cancellationToken = default)
    {
        LogArchiveFileState? file = await Mutate<LogArchiveFileState?>(cancellationToken, async now =>
        {
            if (_logArchive is not { Enabled: true } options) return null;
            var ready = await _archiveReady.Range("value.availableAt", maximum: now, take: 1);
            LogArchiveFileState? current = await _archiveOpen.Get("current");
            LogArchiveFileState? aged = current is not null && current.RotateAt <= now ? current : null;
            if (aged is not null)
            {
                await _archiveReady.Set(aged.Id, aged);
                await _archiveOpen.Remove("current");
            }
            LogArchiveFileState? selected = ready.FirstOrDefault()?.Value ?? aged;
            if (selected is null) return null;
            if (selected.Saved)
            {
                await CleanupArchiveParts(selected);
                return null;
            }
            var claim = selected with
            {
                Token = Guid.NewGuid().ToString(),
                AvailableAt = now + (long)options.ExportTimeout.TotalMilliseconds + 30000
            };
            await _archiveReady.Set(claim.Id, claim);
            return claim;
        });
        if (file is null) return null;

        // Read bounded batches outside the claim transaction; assembling a large file must not block job leases.
        var entries = new List<LogArchiveEntry>();
        for (int part = 0; part < file.PartCount; part += 100)
        {
            int index = part;
            LogArchiveEntry[]? batch = await Mutate<LogArchiveEntry[]?>(cancellationToken, async now =>
            {
                if (await _archiveReady.Get(file.Id) is not { } claim || claim.Token != file.Token ||
                    claim.AvailableAt <= now) return null;
                // Renew during assembly too, so a large file cannot repeatedly lose its export claim.
                await _archiveReady.Set(file.Id, claim with
                {
                    AvailableAt = now + (long)_logArchive!.ExportTimeout.TotalMilliseconds + 30000
                });
                string[] keys = Enumerable.Range(index, Math.Min(100, file.PartCount - index))
                    .Select(i => $"{file.Id}:{i}").ToArray();
                var parts = await _archiveParts.GetMany<LogArchiveEntry[]>(keys);
                if (parts.Any(p => p is null)) throw new InvalidDataException("Archive file is missing a pending batch.");
                return parts.SelectMany(p => p!).ToArray();
            });
            if (batch is null) return null;
            entries.AddRange(batch);
        }
        return new LogArchiveClaim(new LogArchiveFile(file.Id, file.CreatedAt, entries), file.Token, file.AvailableAt);
    }

    private async Task<bool> CleanupArchiveParts(LogArchiveFileState file)
    {
        int remaining = Math.Max(0, file.PartCount - 100);
        string[] keys = Enumerable.Range(remaining, file.PartCount - remaining).Select(i => $"{file.Id}:{i}").ToArray();
        // Prime the read cache in one bounded request before staging deletes.
        await _archiveParts.GetMany<LogArchiveEntry[]>(keys);
        foreach (string key in keys) await _archiveParts.Remove(key);
        if (remaining == 0)
            await _archiveReady.Remove(file.Id);
        else
            await _archiveReady.Set(file.Id, file with { PartCount = remaining, Saved = true, AvailableAt = 0 });
        return remaining == 0;
    }

    public async Task CompleteLogArchive(LogArchiveClaim claim, CancellationToken cancellationToken = default)
    {
        // Mark saved together with each cleanup batch. Interrupted cleanup resumes without exporting a partial file.
        while (!await Mutate(cancellationToken, async _ =>
        {
            if (await _archiveReady.Get(claim.File.Id) is { } current && current.Token == claim.Token)
                return await CleanupArchiveParts(current);
            return true;
        })) { }
    }
}
