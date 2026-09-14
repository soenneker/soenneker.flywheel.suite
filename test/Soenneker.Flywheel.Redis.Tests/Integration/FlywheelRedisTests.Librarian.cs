using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Hashing.Sha256;
using Soenneker.Librarian.Redis;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Utils.Json;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task MissedCommittedNotificationsRequestResync() => WithStore(async (store, db, ns) =>
    {
        await using var observer = new RedisJobStore(_ => Task.FromResult(db), ns);
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var feed = observer.Watch(timeout.Token).GetAsyncEnumerator();
        Check(await feed.MoveNextAsync() && feed.Current == JobChange.Resync, "Initial resync missing");
        await store.Enqueue(Request());
        await store.Enqueue(Request());
        Check(await feed.MoveNextAsync() && feed.Current == JobChange.Resync, "Missed revisions were presented as a complete feed");
        Check((await observer.List()).Count == 2, "Resync did not expose committed documents");
    });

    private static RedisLibrarianDatabase OpenLibrarian(IDatabase db, string ns) => new(ns, _ => ValueTask.FromResult(db), keyPrefix: "flywheel");
    private static string DocumentId<T>(T key) => new Sha256HashingUtil().Hash(JsonUtil.Serialize(key)!);
    private static string LibrarianPrefix(string ns) => "flywheel:{" + ns + "}:containers:";

    private static Task SeedJob(IDatabase db, string ns, JobRecord job) => SeedRecord(db, ns, "jobs", job.Id, job);

    private static async Task SeedRecord<TKey, TValue>(IDatabase db, string ns, string table, TKey key, TValue value)
    {
        await using var database = OpenLibrarian(db, ns);
        string? order = value is JobRecord job ? (long.MaxValue - job.CreatedAt).ToString("D19", CultureInfo.InvariantCulture) + ":" + job.Id : null;
        string? scheduledOrder = value is JobRecord pending && pending.State == JobState.Scheduled ?
            pending.DueAt.ToString("D19", CultureInfo.InvariantCulture) + ":" + pending.Id : null;
        var writes = new List<LibrarianWrite>
        {
            new("flywheel." + table, DocumentId(key), JsonUtil.Serialize(new { key, value, order, scheduledOrder, marker = true })!),
            new("flywheel.control", "flywheel." + table, Guid.NewGuid().ToString("N")),
            new("flywheel.control", "format", "4")
        };
        if (value is JobRecord record)
        {
            writes.Add(new LibrarianWrite("flywheel.control", "revision", Guid.NewGuid().ToString("N")));
            writes.Add(new LibrarianWrite("flywheel.control", "flywheel.dispatch", Guid.NewGuid().ToString("N")));
            writes.Add(new LibrarianWrite("flywheel.control", "flywheel.running", Guid.NewGuid().ToString("N")));
            writes.Add(new LibrarianWrite("flywheel.dispatch", DocumentId(key), record.State == JobState.Scheduled ?
                JsonUtil.Serialize(new { key, value = new { record.Id, record.Name, record.ApplicationVersion, Priority = record.Policy.Priority.Value, record.DueAt }, marker = true }) : null));
            writes.Add(new LibrarianWrite("flywheel.running", DocumentId(key), record.State == JobState.Running ?
                JsonUtil.Serialize(new { key, value = new { record.Name, record.LeaseUntil }, marker = true }) : null));
        }
        await database.Execute(new LibrarianBatch(writes));
    }

    private static async Task<string?> ControlValue(IDatabase db, string ns, string id)
    {
        await using var database = OpenLibrarian(db, ns);
        return await (await database.GetContainer("flywheel.control")).GetItem(id);
    }
}
