using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Soenneker.Flywheel.Communication.Dtos;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private RedisChannel ChangeChannel(IDatabase db) => RedisChannel.Literal(_prefix + "changes:" + db.Database);

    private static void Publish(Mutation mutation, JobChange change)
    {
        // Publish participates in EXEC: a rejected transaction cannot emit a phantom change, and there is no
        // process-crash gap between persisting state and publishing. Readers run after the atomic EXEC completes.
        byte[] value = Serialize(change);
        mutation.Transaction.Queue(t => t.PublishAsync(mutation.Channel, value));
    }

    public async IAsyncEnumerable<JobChange> Watch(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IDatabase db = await Database(cancellationToken);
        ISubscriber subscriber = db.Multiplexer.GetSubscriber();
        RedisChannel name = ChangeChannel(db);
        var pending = Channel.CreateBounded<JobChange>(new BoundedChannelOptions(256)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        int overflow = 0;

        void Enqueue(JobChange change)
        {
            if (!pending.Writer.TryWrite(change))
                Interlocked.Exchange(ref overflow, 1);
        }

        void Restored(object? sender, ConnectionFailedEventArgs args) => Enqueue(JobChange.Resync);

        void Received(ChannelMessage message)
        {
            try
            {
                Enqueue(Decode<JobChange>(message.Message) ?? JobChange.Resync);
            }
            catch
            {
                Enqueue(JobChange.Resync);
            }
        }

        db.Multiplexer.ConnectionRestored += Restored;
        ChannelMessageQueue? queue = null;
        try
        {
            queue = await subscriber.SubscribeAsync(name);
            queue.OnMessage(Received);
            // Subscription is established before the snapshot trigger, closing the initial read/subscribe gap.
            yield return JobChange.Resync;
            await foreach (JobChange change in pending.Reader.ReadAllAsync(cancellationToken))
            {
                if (change.Kind == "Resync")
                {
                    // Re-establish and await subscription acknowledgement before requesting a recovery snapshot.
                    await queue.UnsubscribeAsync();
                    queue = await subscriber.SubscribeAsync(name);
                    queue.OnMessage(Received);
                }

                if (Interlocked.Exchange(ref overflow, 0) != 0)
                    yield return JobChange.Resync;
                yield return change;
            }
        }
        finally
        {
            db.Multiplexer.ConnectionRestored -= Restored;
            pending.Writer.TryComplete();
            if (queue is not null)
                await queue.UnsubscribeAsync();
        }
    }
}