using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Redis.Util.Atomics;
using StackExchange.Redis;
using System.Buffers.Binary;
using System.Text;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private void SaveDispatchMetadata(RedisAtomicTransaction tx, JobRecord job, JobRecord? previous)
    {
        // Renewals, progress and cancellation requests do not alter dispatch metadata.
        if (previous is not null && job.Name == previous.Name && job.State == previous.State &&
            job.Policy.Priority == previous.Policy.Priority && job.DueAt == previous.DueAt &&
            job.ApplicationVersion == previous.ApplicationVersion)
            return;

        if (job.State == JobState.Scheduled || job.State == JobState.Running)
        {
            byte[] value = EncodeDispatchMetadata(new DispatchMetadata(job.Name, job.State.Value, job.Policy.Priority.Value,
                job.DueAt, job.ApplicationVersion));
            tx.Queue(t => t.HashSetAsync(Dispatch, job.Id, value));
        }
        else
            tx.Queue(t => t.HashDeleteAsync(Dispatch, job.Id));
    }

    // Dispatch format v1: format byte, state/priority (int32), due (int64), name byte length
    // (int32), version-present byte, then UTF-8 name and optional application version. Job records
    // retain their existing public JSON format. A fixed header avoids JSON parsing for every queued job.
    private static byte[] EncodeDispatchMetadata(DispatchMetadata metadata)
    {
        int nameLength = Encoding.UTF8.GetByteCount(metadata.Name);
        int versionLength = metadata.ApplicationVersion is null ? 0 : Encoding.UTF8.GetByteCount(metadata.ApplicationVersion);
        var value = new byte[22 + nameLength + versionLength];
        value[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(1), metadata.State);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(5), metadata.Priority);
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(9), metadata.DueAt);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(17), nameLength);
        value[21] = metadata.ApplicationVersion is null ? (byte)0 : (byte)1;
        Encoding.UTF8.GetBytes(metadata.Name, value.AsSpan(22));
        if (metadata.ApplicationVersion is not null)
            Encoding.UTF8.GetBytes(metadata.ApplicationVersion, value.AsSpan(22 + nameLength));
        return value;
    }

    private static DispatchMetadata ReadIndexedDispatchMetadata(RedisValue value)
    {
        ReadOnlySpan<byte> bytes = ((ReadOnlyMemory<byte>)value).Span;
        if (bytes.Length < 22 || bytes[0] != 1 || bytes[21] > 1)
            throw new InvalidOperationException("Invalid dispatch metadata format.");
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[17..]);
        if ((uint)nameLength > (uint)(bytes.Length - 22))
            throw new InvalidOperationException("Invalid dispatch metadata name length.");
        return new DispatchMetadata(Encoding.UTF8.GetString(bytes.Slice(22, nameLength)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[9..]),
            bytes[21] == 0 ? null : Encoding.UTF8.GetString(bytes[(22 + nameLength)..]));
    }
}
