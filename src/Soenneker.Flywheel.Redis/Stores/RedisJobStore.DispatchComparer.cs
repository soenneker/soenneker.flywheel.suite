namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private sealed class DispatchComparer : IComparer<DispatchCandidate>
    {
        public static readonly DispatchComparer Instance = new();

        public int Compare(DispatchCandidate x, DispatchCandidate y)
        {
            int result = y.Priority.CompareTo(x.Priority);
            if (result == 0) result = x.DueAt.CompareTo(y.DueAt);
            if (result == 0) result = ((ReadOnlyMemory<byte>)x.Id).Span.SequenceCompareTo(((ReadOnlyMemory<byte>)y.Id).Span);
            return result;
        }
    }
}
